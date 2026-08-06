using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 覆盖审计：回答未分配、重复、Context-only、失败与预算截断。
/// 首期以报告形式输出，默认不接入发布门禁。
/// </summary>
public interface ICoverageAuditor
{
    CoverageAuditReport Audit(
        string snapshotIdentity,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage>? leaves = null);
}

public sealed class CoverageAuditor : ICoverageAuditor
{
    private static readonly JsonSerializerOptions HashOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public CoverageAuditReport Audit(
        string snapshotIdentity,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage>? leaves = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(manifests);

        leaves ??= [];
        var items = new List<CoverageAuditItem>();
        var fileAssignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // 页面级：缺少 Document 入口 / Context-only
        foreach (var manifest in manifests)
        {
            if (manifest.EntryFiles.Count == 0
                || !manifest.RequiredEvidenceKinds.Contains(EvidenceKind.Document))
            {
                items.Add(new CoverageAuditItem
                {
                    SubjectKind = "Page",
                    SubjectId = manifest.PageId,
                    Status = CoverageItemStatus.ContextOnlyEvidence,
                    AssignedDomainId = manifest.DomainId,
                    AssignedPageId = manifest.PageId,
                    Detail = "Page lacks Document entry files or Document evidence requirement."
                });
                continue;
            }

            var missingEntries = manifest.EntryFiles
                .Where(entry => inventory.Files.All(file =>
                    !string.Equals(file.RelativePath, entry, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (missingEntries.Length > 0)
            {
                items.Add(new CoverageAuditItem
                {
                    SubjectKind = "Page",
                    SubjectId = manifest.PageId,
                    Status = CoverageItemStatus.Failed,
                    AssignedDomainId = manifest.DomainId,
                    AssignedPageId = manifest.PageId,
                    Detail = "Entry files missing from inventory: " + string.Join(", ", missingEntries)
                });
            }

            foreach (var root in manifest.IncludedRoots)
            {
                foreach (var file in inventory.Files.Where(item => IsUnderOrEqual(item.RelativePath, root)))
                {
                    if (!fileAssignments.TryGetValue(file.RelativePath, out var pages))
                    {
                        pages = [];
                        fileAssignments[file.RelativePath] = pages;
                    }

                    if (!pages.Contains(manifest.PageId, StringComparer.OrdinalIgnoreCase))
                    {
                        pages.Add(manifest.PageId);
                    }
                }
            }
        }

        // 文件级覆盖
        foreach (var file in inventory.Files)
        {
            if (!fileAssignments.TryGetValue(file.RelativePath, out var pages) || pages.Count == 0)
            {
                items.Add(new CoverageAuditItem
                {
                    SubjectKind = "File",
                    SubjectId = file.RelativePath,
                    RelativePath = file.RelativePath,
                    Status = CoverageItemStatus.Unassigned,
                    Detail = "File not assigned to any planned page."
                });
                continue;
            }

            if (pages.Count > 1)
            {
                items.Add(new CoverageAuditItem
                {
                    SubjectKind = "File",
                    SubjectId = file.RelativePath,
                    RelativePath = file.RelativePath,
                    Status = CoverageItemStatus.DuplicateAssignment,
                    AssignedPageId = pages[0],
                    Detail = "Assigned to multiple pages: " + string.Join(", ", pages)
                });
            }
            else
            {
                items.Add(new CoverageAuditItem
                {
                    SubjectKind = "File",
                    SubjectId = file.RelativePath,
                    RelativePath = file.RelativePath,
                    Status = CoverageItemStatus.Covered,
                    AssignedPageId = pages[0],
                    AssignedDomainId = manifests
                        .FirstOrDefault(manifest =>
                            string.Equals(manifest.PageId, pages[0], StringComparison.OrdinalIgnoreCase))
                        ?.DomainId
                });
            }
        }

        // 模块级
        foreach (var module in inventory.Modules)
        {
            var assigned = manifests.Any(manifest =>
                string.Equals(manifest.ModuleId, module.ModuleId, StringComparison.OrdinalIgnoreCase)
                || manifest.IncludedRoots.Any(root => IsUnderOrEqual(module.RootPath, root)
                                                      || IsUnderOrEqual(root, module.RootPath)));

            items.Add(new CoverageAuditItem
            {
                SubjectKind = "Module",
                SubjectId = module.ModuleId,
                RelativePath = module.RootPath,
                Status = assigned ? CoverageItemStatus.Covered : CoverageItemStatus.Unassigned,
                Detail = assigned ? null : "Module not covered by any domain/page."
            });
        }

        // 领域预算截断
        foreach (var domain in domains.Where(item => item.BudgetTruncated))
        {
            items.Add(new CoverageAuditItem
            {
                SubjectKind = "Domain",
                SubjectId = domain.DomainId,
                Status = CoverageItemStatus.BudgetTruncated,
                AssignedDomainId = domain.DomainId,
                Detail = $"Domain truncated at depth {domain.Depth} with {domain.FileCount} files."
            });
        }

        // 叶子失败
        foreach (var leaf in leaves.Where(item => item.Status == LeafPageStatus.Failed))
        {
            items.Add(new CoverageAuditItem
            {
                SubjectKind = "Page",
                SubjectId = leaf.PageId,
                Status = CoverageItemStatus.Failed,
                AssignedDomainId = leaf.DomainId,
                AssignedPageId = leaf.PageId,
                Detail = leaf.ErrorMessage ?? "Leaf generation failed."
            });
        }

        items = items
            .OrderBy(item => item.SubjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SubjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Status.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var covered = items.Count(item => item.Status == CoverageItemStatus.Covered);
        var unassigned = items.Count(item => item.Status == CoverageItemStatus.Unassigned);
        var failed = items.Count(item => item.Status == CoverageItemStatus.Failed);
        var budget = items.Count(item => item.Status == CoverageItemStatus.BudgetTruncated);
        var duplicates = items.Count(item => item.Status == CoverageItemStatus.DuplicateAssignment);
        var contextOnly = items.Count(item => item.Status == CoverageItemStatus.ContextOnlyEvidence);
        var excluded = items.Count(item => item.Status == CoverageItemStatus.IntentionallyExcluded);

        var blocking = new List<string>();
        if (failed > 0)
        {
            blocking.Add($"Failed subjects: {failed}");
        }

        if (contextOnly > 0)
        {
            blocking.Add($"Context-only pages: {contextOnly}");
        }

        // 模块完全未覆盖视为阻断；文件级 Unassigned 首期仅警告
        var unassignedModules = items.Count(item =>
            item.SubjectKind == "Module" && item.Status == CoverageItemStatus.Unassigned);
        if (unassignedModules > 0)
        {
            blocking.Add($"Unassigned modules: {unassignedModules}");
        }

        var report = new CoverageAuditReport
        {
            SnapshotIdentity = snapshotIdentity,
            AuditedAtUtc = DateTimeOffset.UtcNow,
            Items = items,
            CoveredCount = covered,
            UnassignedCount = unassigned,
            FailedCount = failed,
            BudgetTruncatedCount = budget,
            DuplicateAssignmentCount = duplicates,
            ContextOnlyEvidenceCount = contextOnly,
            IntentionallyExcludedCount = excluded,
            HasBlockingErrors = blocking.Count > 0,
            BlockingReasons = blocking
        };

        return new CoverageAuditReport
        {
            SnapshotIdentity = report.SnapshotIdentity,
            AuditedAtUtc = report.AuditedAtUtc,
            Items = report.Items,
            CoveredCount = report.CoveredCount,
            UnassignedCount = report.UnassignedCount,
            FailedCount = report.FailedCount,
            BudgetTruncatedCount = report.BudgetTruncatedCount,
            DuplicateAssignmentCount = report.DuplicateAssignmentCount,
            ContextOnlyEvidenceCount = report.ContextOnlyEvidenceCount,
            IntentionallyExcludedCount = report.IntentionallyExcludedCount,
            HasBlockingErrors = report.HasBlockingErrors,
            BlockingReasons = report.BlockingReasons,
            ContentHash = ComputeHash(report)
        };
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        var normalizedPath = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        var normalizedRoot = (root ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return true;
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHash(CoverageAuditReport report)
    {
        var payload = new
        {
            report.SnapshotIdentity,
            report.CoveredCount,
            report.UnassignedCount,
            report.FailedCount,
            report.BudgetTruncatedCount,
            report.DuplicateAssignmentCount,
            report.ContextOnlyEvidenceCount,
            items = report.Items.Select(item => new
            {
                item.SubjectKind,
                item.SubjectId,
                Status = item.Status.ToString(),
                item.AssignedDomainId,
                item.AssignedPageId
            })
        };
        var json = JsonSerializer.Serialize(payload, HashOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
