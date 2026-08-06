using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 确定性 Domain / Topic 规划器。
/// 领域 ID 来自稳定 scope/module identity，不依赖模型自由命名。
/// </summary>
public interface IDomainTopicPlanner
{
    IReadOnlyList<PlannedDomain> PlanDomains(
        SourceInventory inventory,
        GenerationPolicy policy);

    IReadOnlyList<ScopeManifest> PlanTopics(
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        GenerationPolicy policy);
}

public sealed class DomainTopicPlanner : IDomainTopicPlanner
{
    private static readonly JsonSerializerOptions ManifestHashOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public IReadOnlyList<PlannedDomain> PlanDomains(
        SourceInventory inventory,
        GenerationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(policy);

        var domains = new List<PlannedDomain>();

        // 优先按 UE 模块拆分；无模块时回退到 Document scope 根
        if (inventory.Modules.Count > 0)
        {
            foreach (var module in inventory.Modules
                         .OrderBy(item => item.ScopeId, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase))
            {
                var seed = CreateDomainFromModule(module, depth: 0);
                domains.AddRange(SplitDomainIfNeeded(seed, inventory, policy, depth: 0));
            }
        }
        else
        {
            domains.AddRange(PlanDomainsFromScopeRoots(inventory, policy));
        }

        // 项目/Target/游离 Document 文件补领域，避免深层文件只在 Inventory 出现却无规划归属
        domains.AddRange(PlanResidualDomains(inventory, domains, policy));

        return domains
            .OrderBy(domain => domain.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(domain => domain.DomainId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PlannedDomain> PlanDomainsFromScopeRoots(
        SourceInventory inventory,
        GenerationPolicy policy)
    {
        var byScope = inventory.Files
            .GroupBy(file => file.ScopeId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byScope)
        {
            var roots = group
                .Select(file => GetTopLevelRoot(file.RelativePath, group.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (roots.Length == 0)
            {
                roots = [string.Empty];
            }

            foreach (var root in roots)
            {
                var domainId = StableDomainId(group.Key, root, parent: null);
                var seed = new PlannedDomain
                {
                    DomainId = domainId,
                    ScopeId = group.Key,
                    DisplayName = string.IsNullOrEmpty(root) ? group.Key : Path.GetFileName(root.TrimEnd('/')),
                    IncludedRoots = [root],
                    Depth = 0,
                    FileCount = CountFilesUnderRoots(inventory, [root]),
                    ModuleIds = []
                };

                foreach (var planned in SplitDomainIfNeeded(seed, inventory, policy, depth: 0))
                {
                    yield return planned;
                }
            }
        }
    }

    private static IEnumerable<PlannedDomain> PlanResidualDomains(
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> existingDomains,
        GenerationPolicy policy)
    {
        var coveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in existingDomains)
        {
            foreach (var file in inventory.Files.Where(item =>
                         domain.IncludedRoots.Any(root => IsUnderOrEqual(item.RelativePath, root))))
            {
                coveredPaths.Add(file.RelativePath);
            }
        }

        var residualFiles = inventory.Files
            .Where(file => !coveredPaths.Contains(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (residualFiles.Count == 0)
        {
            yield break;
        }

        // 每个 Project / Target 单独成域；其余游离文件按目录聚合
        foreach (var project in inventory.Projects
                     .OrderBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase))
        {
            var projectFiles = residualFiles
                .Where(file => string.Equals(file.ProjectId, project.ProjectId, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(file.RelativePath, project.UprojectPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (projectFiles.Count == 0)
            {
                continue;
            }

            var root = project.UprojectPath ?? project.RootPath;
            var seed = new PlannedDomain
            {
                DomainId = StableDomainId(project.ScopeId, root, parent: "project"),
                ScopeId = project.ScopeId,
                DisplayName = project.Name + " Project",
                IncludedRoots = [ScopePathUtilityDir(root)],
                Depth = 0,
                FileCount = projectFiles.Count,
                ModuleIds = []
            };

            foreach (var planned in SplitDomainIfNeeded(seed, inventory, policy, depth: 0))
            {
                yield return planned;
            }

            foreach (var file in projectFiles)
            {
                coveredPaths.Add(file.RelativePath);
            }
        }

        foreach (var target in inventory.Targets
                     .OrderBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            if (coveredPaths.Contains(target.TargetCsPath))
            {
                continue;
            }

            var seed = new PlannedDomain
            {
                DomainId = StableDomainId(target.ScopeId, target.TargetCsPath, parent: "target"),
                ScopeId = target.ScopeId,
                DisplayName = target.Name + " Target",
                IncludedRoots = [target.TargetCsPath],
                Depth = 0,
                FileCount = 1,
                ModuleIds = []
            };
            yield return seed;
            coveredPaths.Add(target.TargetCsPath);
        }

        // 游离文件以精确路径作为 root，避免父目录把已归属模块文件再次纳入导致重复分配
        var stillResidual = residualFiles
            .Where(file => !coveredPaths.Contains(file.RelativePath))
            .GroupBy(file => GetParentDirectory(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in stillResidual)
        {
            var paths = group
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var seed = new PlannedDomain
            {
                DomainId = StableDomainId(group.First().ScopeId, group.Key + "|residual", parent: "residual"),
                ScopeId = group.First().ScopeId,
                DisplayName = string.IsNullOrEmpty(group.Key)
                    ? "Residual Files"
                    : Path.GetFileName(group.Key.TrimEnd('/')) + " Residual",
                IncludedRoots = paths,
                Depth = 0,
                FileCount = paths.Length,
                ModuleIds = []
            };
            yield return seed;
        }
    }

    private static string ScopePathUtilityDir(string path)
    {
        // uproject 文件本身作为 included root，便于精确覆盖该入口
        return path.Replace('\\', '/');
    }

    private static string GetParentDirectory(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? string.Empty : normalized[..index];
    }

    public IReadOnlyList<ScopeManifest> PlanTopics(
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        GenerationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(policy);

        var manifests = new List<ScopeManifest>();
        var usedPageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var domain in domains
                     .OrderBy(item => item.ScopeId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.DomainId, StringComparer.OrdinalIgnoreCase))
        {
            var entryFiles = SelectEntryFiles(inventory, domain, policy.MaxEntryFilesPerPage);
            if (entryFiles.Count == 0)
            {
                // 无 Document 入口的候选拒绝进入 Catalog（覆盖审计会标 Unassigned/Failed）
                continue;
            }

            var pageId = StablePageId(domain.DomainId, "overview");
            if (!usedPageIds.Add(pageId))
            {
                pageId = StablePageId(domain.DomainId, domain.IncludedRoots.FirstOrDefault() ?? "overview");
                usedPageIds.Add(pageId);
            }

            var related = domains
                .Where(other => !string.Equals(other.DomainId, domain.DomainId, StringComparison.OrdinalIgnoreCase)
                                && other.ModuleIds.Any(moduleId => domain.ModuleIds.Contains(moduleId, StringComparer.OrdinalIgnoreCase)
                                    || ShareDependency(inventory, domain, other)))
                .Select(other => StablePageId(other.DomainId, "overview"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();

            var excluded = domains
                .Where(other => !string.Equals(other.DomainId, domain.DomainId, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToArray();

            var topicSummary =
                $"Overview of domain '{domain.DisplayName}' under scope '{domain.ScopeId}', " +
                $"covering {domain.FileCount} document files.";

            var manifest = new ScopeManifest
            {
                PageId = pageId,
                ScopeId = domain.ScopeId,
                DomainId = domain.DomainId,
                TopicSummary = topicSummary,
                IncludedRoots = domain.IncludedRoots,
                ExcludedTopics = excluded,
                EntryFiles = entryFiles,
                RelatedPages = related,
                RequiredEvidenceKinds = [EvidenceKind.Document],
                ModuleId = domain.ModuleIds.FirstOrDefault()
            };

            manifest = new ScopeManifest
            {
                PageId = manifest.PageId,
                ScopeId = manifest.ScopeId,
                DomainId = manifest.DomainId,
                TopicSummary = manifest.TopicSummary,
                IncludedRoots = manifest.IncludedRoots,
                ExcludedTopics = manifest.ExcludedTopics,
                EntryFiles = manifest.EntryFiles,
                RelatedPages = manifest.RelatedPages,
                RequiredEvidenceKinds = manifest.RequiredEvidenceKinds,
                ModuleId = manifest.ModuleId,
                ContentHash = ComputeManifestHash(manifest)
            };

            manifests.Add(manifest);
        }

        return manifests
            .OrderBy(item => item.ScopeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DomainId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PlannedDomain> SplitDomainIfNeeded(
        PlannedDomain domain,
        SourceInventory inventory,
        GenerationPolicy policy,
        int depth)
    {
        var fileCount = CountFilesUnderRoots(inventory, domain.IncludedRoots);
        domain = CloneDomain(domain, fileCount: fileCount);

        if (domain.FileCount <= policy.DomainFileBudget || depth >= policy.MaxPlanningDepth)
        {
            var truncated = domain.FileCount > policy.DomainFileBudget;
            yield return CloneDomain(
                domain,
                budgetTruncated: truncated,
                status: truncated ? DomainPlanStatus.BudgetTruncated : DomainPlanStatus.Planned);
            yield break;
        }

        var children = ProposeChildRoots(inventory, domain);
        if (children.Count <= 1)
        {
            yield return CloneDomain(
                domain,
                budgetTruncated: true,
                status: DomainPlanStatus.BudgetTruncated);
            yield break;
        }

        foreach (var childRoot in children)
        {
            var childId = StableDomainId(domain.ScopeId, childRoot, domain.DomainId);
            var child = new PlannedDomain
            {
                DomainId = childId,
                ScopeId = domain.ScopeId,
                DisplayName = Path.GetFileName(childRoot.TrimEnd('/')) is { Length: > 0 } name
                    ? name
                    : childRoot,
                IncludedRoots = [childRoot],
                ParentDomainId = domain.DomainId,
                Depth = depth + 1,
                ModuleIds = domain.ModuleIds
                    .Where(moduleId =>
                    {
                        var module = inventory.Modules.FirstOrDefault(item =>
                            string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
                        return module is not null
                               && IsUnderOrEqual(module.RootPath, childRoot);
                    })
                    .ToArray()
            };

            foreach (var planned in SplitDomainIfNeeded(child, inventory, policy, depth + 1))
            {
                yield return planned;
            }
        }
    }

    private static PlannedDomain CloneDomain(
        PlannedDomain domain,
        int? fileCount = null,
        bool? budgetTruncated = null,
        DomainPlanStatus? status = null)
        => new()
        {
            DomainId = domain.DomainId,
            ScopeId = domain.ScopeId,
            DisplayName = domain.DisplayName,
            IncludedRoots = domain.IncludedRoots,
            ParentDomainId = domain.ParentDomainId,
            Depth = domain.Depth,
            FileCount = fileCount ?? domain.FileCount,
            BudgetTruncated = budgetTruncated ?? domain.BudgetTruncated,
            ModuleIds = domain.ModuleIds,
            Topics = domain.Topics,
            Status = status ?? domain.Status
        };

    private static PlannedDomain CreateDomainFromModule(InventoryModuleEntry module, int depth)
    {
        return new PlannedDomain
        {
            DomainId = StableDomainId(module.ScopeId, module.RootPath, parent: null),
            ScopeId = module.ScopeId,
            DisplayName = module.Name,
            IncludedRoots = [module.RootPath],
            Depth = depth,
            FileCount = module.FileCount,
            ModuleIds = [module.ModuleId]
        };
    }

    private static List<string> ProposeChildRoots(SourceInventory inventory, PlannedDomain domain)
    {
        var childRoots = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in domain.IncludedRoots)
        {
            var prefix = string.IsNullOrEmpty(root) ? string.Empty : root.TrimEnd('/') + "/";
            foreach (var file in inventory.Files)
            {
                if (!IsUnderOrEqual(file.RelativePath, root))
                {
                    continue;
                }

                var relative = string.IsNullOrEmpty(prefix)
                    ? file.RelativePath
                    : file.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        ? file.RelativePath[prefix.Length..]
                        : string.Empty;

                if (string.IsNullOrEmpty(relative))
                {
                    continue;
                }

                var segment = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                // 跳过 Source/Public/Private 这一层，优先按下一层能力域拆分
                if (segment is "Source" or "Public" or "Private" or "Classes" or "Internal")
                {
                    var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        childRoots.Add(string.IsNullOrEmpty(root)
                            ? $"{parts[0]}/{parts[1]}"
                            : $"{root.TrimEnd('/')}/{parts[0]}/{parts[1]}");
                        continue;
                    }
                }

                childRoots.Add(string.IsNullOrEmpty(root) ? segment : $"{root.TrimEnd('/')}/{segment}");
            }
        }

        return childRoots
            .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> SelectEntryFiles(
        SourceInventory inventory,
        PlannedDomain domain,
        int maxEntryFiles)
    {
        var underDomain = inventory.Files
            .Where(file => domain.IncludedRoots.Any(root => IsUnderOrEqual(file.RelativePath, root)))
            .ToList();

        var preferred = underDomain
            .Where(file => file.IsEntryPoint
                           || file.FileKind is "BuildCs" or "UPlugin" or "UProject" or "Header")
            .OrderBy(file => file.IsEntryPoint ? 0 : 1)
            .ThenBy(file => file.FileKind switch
            {
                "BuildCs" => 0,
                "UPlugin" => 1,
                "UProject" => 2,
                "Header" => 3,
                _ => 9
            })
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxEntryFiles))
            .ToList();

        if (preferred.Count > 0)
        {
            return preferred;
        }

        return underDomain
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.RelativePath)
            .Take(Math.Max(1, maxEntryFiles))
            .ToArray();
    }

    private static bool ShareDependency(
        SourceInventory inventory,
        PlannedDomain left,
        PlannedDomain right)
    {
        var leftNames = left.ModuleIds
            .Select(id => inventory.Modules.FirstOrDefault(module =>
                string.Equals(module.ModuleId, id, StringComparison.OrdinalIgnoreCase))?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rightNames = right.ModuleIds
            .Select(id => inventory.Modules.FirstOrDefault(module =>
                string.Equals(module.ModuleId, id, StringComparison.OrdinalIgnoreCase))?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in inventory.Dependencies)
        {
            var fromName = inventory.Modules
                .FirstOrDefault(module => string.Equals(module.ModuleId, edge.FromModuleId, StringComparison.OrdinalIgnoreCase))
                ?.Name;
            if (fromName is null)
            {
                continue;
            }

            if (leftNames.Contains(fromName) && rightNames.Contains(edge.ToModuleName))
            {
                return true;
            }

            if (rightNames.Contains(fromName) && leftNames.Contains(edge.ToModuleName))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountFilesUnderRoots(SourceInventory inventory, IReadOnlyList<string> roots)
        => inventory.Files.Count(file => roots.Any(root => IsUnderOrEqual(file.RelativePath, root)));

    private static string GetTopLevelRoot(string relativePath, string scopeId)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        // 保留前两段作为粗粒度根（SampleProject/Source）
        return parts.Length == 1 ? parts[0] : $"{parts[0]}/{parts[1]}";
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        var normalizedRoot = (root ?? string.Empty).Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return true;
        }

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StableDomainId(string scopeId, string root, string? parent)
    {
        var raw = $"domain|{scopeId}|{root}|{parent ?? "-"}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..12];
        var slug = SourceInventoryBuilder.Slugify(
            string.IsNullOrWhiteSpace(root) ? scopeId : Path.GetFileName(root.TrimEnd('/')));
        return $"domain-{slug}-{hash}";
    }

    private static string StablePageId(string domainId, string topicKey)
    {
        var raw = $"page|{domainId}|{topicKey}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..12];
        var slug = SourceInventoryBuilder.Slugify(topicKey);
        return $"page-{slug}-{hash}";
    }

    private static string ComputeManifestHash(ScopeManifest manifest)
    {
        var payload = new
        {
            manifest.PageId,
            manifest.ScopeId,
            manifest.DomainId,
            manifest.TopicSummary,
            manifest.IncludedRoots,
            manifest.ExcludedTopics,
            manifest.EntryFiles,
            manifest.RelatedPages,
            evidence = manifest.RequiredEvidenceKinds.Select(kind => kind.ToString()).ToArray(),
            manifest.ModuleId
        };
        var json = JsonSerializer.Serialize(payload, ManifestHashOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
