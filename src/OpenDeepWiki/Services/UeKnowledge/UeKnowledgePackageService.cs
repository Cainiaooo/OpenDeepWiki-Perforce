using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.UeKnowledge;

public interface IUeKnowledgePackageService
{
    Task<UeKnowledgePackageSummaryDto> IngestAsync(
        string repositoryId,
        IngestUeKnowledgePackageRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UeKnowledgePackageSummaryDto>> ListAsync(
        string repositoryId,
        string? branchId = null,
        CancellationToken cancellationToken = default);

    Task<UeKnowledgePackageSummaryDto?> GetCurrentAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    Task<UeKnowledgeFactIndex?> GetCurrentFactIndexAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    Task<UeKnowledgeFactIndex?> GetFactIndexAsync(
        string repositoryId,
        string packageId,
        CancellationToken cancellationToken = default);

    Task<UeKnowledgeSemanticDiffResult?> DiffCurrentWithAsync(
        string repositoryId,
        string branchId,
        string otherPackageId,
        CancellationToken cancellationToken = default);

    Task<UeMcpContractCompatibilityResult?> CheckMcpCompatibilityAsync(
        string repositoryId,
        string branchId,
        string requestBuildChangelist,
        IReadOnlyCollection<string>? requiredToolNames = null,
        CancellationToken cancellationToken = default);

    Task MarkStaleIfIncompatibleAsync(
        string repositoryId,
        string branchId,
        string currentBuildChangelist,
        CancellationToken cancellationToken = default);
}

public sealed class UeKnowledgePackageService(
    IContext context,
    IUeKnowledgePackageLoader loader,
    IUeKnowledgePackageValidator validator,
    IUeKnowledgeSemanticDiff semanticDiff,
    IUeKnowledgeMcpContractChecker mcpContractChecker) : IUeKnowledgePackageService
{
    public async Task<UeKnowledgePackageSummaryDto> IngestAsync(
        string repositoryId,
        IngestUeKnowledgePackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(request);

        var repository = await context.Repositories
                             .AsNoTracking()
                             .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken)
                         ?? throw new UeKnowledgeException("仓库不存在", "NOT_FOUND");

        var branch = await context.RepositoryBranches
                         .AsNoTracking()
                         .FirstOrDefaultAsync(
                             b => b.Id == request.BranchId
                                  && b.RepositoryId == repositoryId
                                  && !b.IsDeleted,
                             cancellationToken)
                     ?? throw new UeKnowledgeException("分支不存在", "NOT_FOUND");

        var loadResult = LoadPackage(request);
        if (loadResult.HasErrors)
        {
            throw new UeKnowledgeException(
                "包加载失败: " + string.Join("; ", loadResult.Errors),
                "INVALID_PACKAGE");
        }

        var expectedProjectId = request.ExpectedProjectId;
        if (string.IsNullOrWhiteSpace(expectedProjectId))
        {
            // 默认用仓库名作为 project identity 期望（可被请求覆盖）
            expectedProjectId = repository.RepoName;
        }

        var validation = validator.Validate(
            loadResult,
            expectedProjectId,
            request.ExpectedBuildChangelist,
            request.StrictBuildChangelistMatch);

        if (!validation.IsValid || validation.Package is null || validation.FactIndex is null)
        {
            throw new UeKnowledgeException(
                "包验证失败: " + string.Join("; ", validation.Errors),
                "VALIDATION_FAILED",
                validation.Errors,
                validation.Warnings);
        }

        // 幂等：同一 packageDigest 已存在则直接返回
        var existing = await context.UeKnowledgePackages
            .FirstOrDefaultAsync(
                p => p.RepositoryId == repositoryId
                     && p.BranchId == branch.Id
                     && p.PackageDigest == validation.Package.PackageDigest
                     && !p.IsDeleted,
                cancellationToken);

        if (existing is not null)
        {
            if (request.SetAsCurrent && !existing.IsCurrent)
            {
                await SetCurrentAsync(repositoryId, branch.Id, existing.Id, cancellationToken);
                existing.IsCurrent = true;
                existing.IsStale = false;
                existing.UpdateTimestamp();
                await context.SaveChangesAsync(cancellationToken);
            }

            return ToSummary(existing, validation.Warnings);
        }

        var completeness = validation.Package.Manifest.Completeness switch
        {
            UeKnowledgeCompletenessDto.Partial => UeKnowledgeCompleteness.Partial,
            UeKnowledgeCompletenessDto.Truncated => UeKnowledgeCompleteness.Truncated,
            _ => validation.FactIndex.IsPartial
                ? UeKnowledgeCompleteness.Partial
                : UeKnowledgeCompleteness.Complete
        };

        var entity = new UeKnowledgePackage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchId = branch.Id,
            IsCurrent = false,
            IsStale = false,
            Status = completeness == UeKnowledgeCompleteness.Complete
                ? UeKnowledgePackageStatus.Validated
                : UeKnowledgePackageStatus.Partial,
            Completeness = completeness,
            SchemaVersion = validation.Package.Manifest.SchemaVersion,
            ExporterVersion = validation.Package.Manifest.ExporterVersion,
            ProjectIdentity = validation.Package.Manifest.Project.ProjectId,
            BranchName = validation.Package.Manifest.Project.Branch ?? branch.BranchName,
            BuildChangelist = validation.Package.Manifest.BuildChangelist,
            EngineVersion = validation.Package.Manifest.EngineVersion,
            TargetPlatform = validation.Package.Manifest.TargetPlatform,
            ExportSource = validation.Package.Manifest.ExportSource,
            PackageDigest = validation.Package.PackageDigest,
            SemanticDigest = validation.Package.SemanticDigest,
            ManifestJson = JsonSerializer.Serialize(validation.Package.Manifest, UeKnowledgeDigest.PrettyJsonOptions),
            FactIndexJson = JsonSerializer.Serialize(validation.FactIndex, UeKnowledgeDigest.JsonOptions),
            PackageRootPath = string.IsNullOrWhiteSpace(validation.Package.PackageRootPath)
                ? request.PackageRootPath
                : validation.Package.PackageRootPath,
            ValidationWarningsJson = validation.Warnings.Count == 0
                ? null
                : JsonSerializer.Serialize(validation.Warnings, UeKnowledgeDigest.JsonOptions),
            ExportedAtUtc = validation.Package.Manifest.ExportedAtUtc?.UtcDateTime,
            IngestedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        context.UeKnowledgePackages.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        if (request.SetAsCurrent)
        {
            await SetCurrentAsync(repositoryId, branch.Id, entity.Id, cancellationToken);
            entity.IsCurrent = true;
            entity.IsStale = false;
            await context.SaveChangesAsync(cancellationToken);
        }

        return ToSummary(entity, validation.Warnings);
    }

    public async Task<IReadOnlyList<UeKnowledgePackageSummaryDto>> ListAsync(
        string repositoryId,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.UeKnowledgePackages
            .AsNoTracking()
            .Where(p => p.RepositoryId == repositoryId && !p.IsDeleted);

        if (!string.IsNullOrWhiteSpace(branchId))
        {
            query = query.Where(p => p.BranchId == branchId);
        }

        var items = await query
            .OrderByDescending(p => p.IsCurrent)
            .ThenByDescending(p => p.IngestedAtUtc)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return items.Select(p => ToSummary(p)).ToList();
    }

    public async Task<UeKnowledgePackageSummaryDto?> GetCurrentAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.UeKnowledgePackages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.RepositoryId == repositoryId
                     && p.BranchId == branchId
                     && p.IsCurrent
                     && !p.IsStale
                     && !p.IsDeleted,
                cancellationToken);

        return entity is null ? null : ToSummary(entity);
    }

    public async Task<UeKnowledgeFactIndex?> GetCurrentFactIndexAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.UeKnowledgePackages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.RepositoryId == repositoryId
                     && p.BranchId == branchId
                     && p.IsCurrent
                     && !p.IsStale
                     && !p.IsDeleted,
                cancellationToken);

        return DeserializeFactIndex(entity?.FactIndexJson);
    }

    public async Task<UeKnowledgeFactIndex?> GetFactIndexAsync(
        string repositoryId,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var entity = await context.UeKnowledgePackages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.Id == packageId
                     && p.RepositoryId == repositoryId
                     && !p.IsDeleted,
                cancellationToken);

        return DeserializeFactIndex(entity?.FactIndexJson);
    }

    public async Task<UeKnowledgeSemanticDiffResult?> DiffCurrentWithAsync(
        string repositoryId,
        string branchId,
        string otherPackageId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentFactIndexAsync(repositoryId, branchId, cancellationToken);
        var other = await GetFactIndexAsync(repositoryId, otherPackageId, cancellationToken);
        if (current is null || other is null)
        {
            return null;
        }

        return semanticDiff.Diff(current, other);
    }

    public async Task<UeMcpContractCompatibilityResult?> CheckMcpCompatibilityAsync(
        string repositoryId,
        string branchId,
        string requestBuildChangelist,
        IReadOnlyCollection<string>? requiredToolNames = null,
        CancellationToken cancellationToken = default)
    {
        var index = await GetCurrentFactIndexAsync(repositoryId, branchId, cancellationToken);
        if (index is null)
        {
            return null;
        }

        return mcpContractChecker.Check(index, requestBuildChangelist, requiredToolNames);
    }

    public async Task MarkStaleIfIncompatibleAsync(
        string repositoryId,
        string branchId,
        string currentBuildChangelist,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentBuildChangelist))
        {
            return;
        }

        var current = await context.UeKnowledgePackages
            .FirstOrDefaultAsync(
                p => p.RepositoryId == repositoryId
                     && p.BranchId == branchId
                     && p.IsCurrent
                     && !p.IsDeleted,
                cancellationToken);

        if (current is null)
        {
            return;
        }

        // Build CL 不同：标记 stale 并取消 current，避免 /current 继续返回不兼容事实。
        // 历史包仍可通过 GetFactIndexAsync(repositoryId, packageId) 查询。
        if (!string.Equals(current.BuildChangelist, currentBuildChangelist.Trim(), StringComparison.Ordinal))
        {
            current.IsStale = true;
            current.IsCurrent = false;
            current.UpdateTimestamp();
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private UeKnowledgePackageLoadResult LoadPackage(IngestUeKnowledgePackageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PackageRootPath))
        {
            return loader.LoadFromDirectory(request.PackageRootPath);
        }

        if (string.IsNullOrWhiteSpace(request.ManifestJson))
        {
            return new UeKnowledgePackageLoadResult
            {
                Manifest = new UeKnowledgeManifest(),
                PackageRootPath = string.Empty,
                ShardContentsByRelativePath = new Dictionary<string, string>(),
                Errors = ["必须提供 packageRootPath 或 manifestJson"]
            };
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UeKnowledgeManifest>(
                               request.ManifestJson,
                               UeKnowledgeDigest.JsonOptions)
                           ?? throw new JsonException("manifest 为空");

            // 仅 manifest 模式：要求调用方把分片内容作为 inline 不在此路径支持；
            // 必须配合 packageRootPath。这里给出明确错误。
            if (manifest.Shards.Count > 0)
            {
                return new UeKnowledgePackageLoadResult
                {
                    Manifest = manifest,
                    PackageRootPath = string.Empty,
                    ShardContentsByRelativePath = new Dictionary<string, string>(),
                    Errors =
                    [
                        "仅提交 manifestJson 时无法校验分片 digest；请提供 packageRootPath 指向完整包目录"
                    ]
                };
            }

            return loader.LoadFromContents(manifest, new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            return new UeKnowledgePackageLoadResult
            {
                Manifest = new UeKnowledgeManifest(),
                PackageRootPath = string.Empty,
                ShardContentsByRelativePath = new Dictionary<string, string>(),
                Errors = [$"manifestJson 解析失败: {ex.Message}"]
            };
        }
    }

    private async Task SetCurrentAsync(
        string repositoryId,
        string branchId,
        string packageId,
        CancellationToken cancellationToken)
    {
        var packages = await context.UeKnowledgePackages
            .Where(p => p.RepositoryId == repositoryId
                        && p.BranchId == branchId
                        && !p.IsDeleted
                        && (p.IsCurrent || p.Id == packageId))
            .ToListAsync(cancellationToken);

        foreach (var package in packages)
        {
            if (package.Id == packageId)
            {
                package.IsCurrent = true;
                package.IsStale = false;
                if (package.Status == UeKnowledgePackageStatus.Superseded)
                {
                    package.Status = package.Completeness == UeKnowledgeCompleteness.Complete
                        ? UeKnowledgePackageStatus.Validated
                        : UeKnowledgePackageStatus.Partial;
                }
            }
            else if (package.IsCurrent)
            {
                package.IsCurrent = false;
                package.Status = UeKnowledgePackageStatus.Superseded;
            }

            package.UpdateTimestamp();
        }
    }

    private static UeKnowledgeFactIndex? DeserializeFactIndex(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<UeKnowledgeFactIndex>(json, UeKnowledgeDigest.JsonOptions);
    }

    private static UeKnowledgePackageSummaryDto ToSummary(
        UeKnowledgePackage entity,
        IReadOnlyList<string>? extraWarnings = null)
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(entity.ValidationWarningsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(
                    entity.ValidationWarningsJson,
                    UeKnowledgeDigest.JsonOptions);
                if (parsed is { Count: > 0 })
                {
                    warnings.AddRange(parsed);
                }
            }
            catch
            {
                // ignore corrupt warning payload
            }
        }

        if (extraWarnings is { Count: > 0 })
        {
            warnings.AddRange(extraWarnings.Where(w => !warnings.Contains(w)));
        }

        if (entity.IsStale)
        {
            warnings.Add("package marked stale for current build");
        }

        return new UeKnowledgePackageSummaryDto
        {
            Id = entity.Id,
            RepositoryId = entity.RepositoryId,
            BranchId = entity.BranchId,
            IsCurrent = entity.IsCurrent,
            IsStale = entity.IsStale,
            Status = entity.Status.ToString(),
            Completeness = entity.Completeness.ToString(),
            SchemaVersion = entity.SchemaVersion,
            ExporterVersion = entity.ExporterVersion,
            ProjectIdentity = entity.ProjectIdentity,
            BuildChangelist = entity.BuildChangelist,
            EngineVersion = entity.EngineVersion,
            PackageDigest = entity.PackageDigest,
            SemanticDigest = entity.SemanticDigest,
            ExportedAtUtc = entity.ExportedAtUtc,
            IngestedAtUtc = entity.IngestedAtUtc,
            Warnings = warnings
        };
    }
}

public sealed class UeKnowledgeException : Exception
{
    public string ErrorCode { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    public UeKnowledgeException(
        string message,
        string errorCode,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Errors = errors ?? [];
        Warnings = warnings ?? [];
    }
}
