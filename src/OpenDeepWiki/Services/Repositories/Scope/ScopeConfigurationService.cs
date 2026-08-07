using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories.Scope;

public interface IScopeConfigurationService
{
    Task<ScopeConfigurationResponse> GetAsync(string repositoryId, CancellationToken cancellationToken = default);

    Task<ScopePreviewResponse> PreviewAsync(
        string repositoryId,
        ScopeConfigurationDocument document,
        CancellationToken cancellationToken = default);

    Task<ScopeConfigurationResponse> UpdateAsync(
        string repositoryId,
        ScopeConfigurationDocument document,
        string? actorUserId,
        string? changeSummary = null,
        CancellationToken cancellationToken = default);

    Task<ResolvedScopeConfiguration?> GetResolvedCurrentAsync(
        string repositoryId,
        CancellationToken cancellationToken = default);

    Task<IRepositoryFileSelectionPolicy> GetPolicyAsync(
        string repositoryId,
        CancellationToken cancellationToken = default);
}

public sealed class ScopeConfigurationService(
    IContext context,
    IScopeConfigurationValidator validator,
    IScopeConfigurationNormalizer normalizer,
    IRepositoryFileSelectionPolicyFactory policyFactory,
    IRepositoryGenerationLockService generationLockService) : IScopeConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<ScopeConfigurationResponse> GetAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var repository = await RequireRepositoryAsync(repositoryId, cancellationToken);
        var current = await context.RepositoryScopeConfigurations
            .AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && item.IsCurrent && !item.IsDeleted)
            .OrderByDescending(item => item.ConfigurationVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return new ScopeConfigurationResponse
            {
                RepositoryId = repositoryId,
                HasConfiguration = false,
                MigrationHint = true,
                MigrationMessage =
                    "该仓库尚未配置 Scope。当前保持 Git/Archive/LocalDirectory/Perforce 兼容行为；建议配置 DocumentScope 以启用三期知识边界。",
                SourceType = repository.SourceType.ToString()
            };
        }

        var document = JsonSerializer.Deserialize<ScopeConfigurationDocument>(current.ConfigurationJson, JsonOptions)
                       ?? new ScopeConfigurationDocument();

        return new ScopeConfigurationResponse
        {
            RepositoryId = repositoryId,
            HasConfiguration = true,
            MigrationHint = false,
            ConfigurationVersion = current.ConfigurationVersion,
            ContentHash = current.ContentHash,
            ReindexRequired = current.ReindexRequired,
            Configuration = document,
            SourceType = repository.SourceType.ToString(),
            UpdatedAt = current.UpdatedAt ?? current.CreatedAt
        };
    }

    public async Task<ScopePreviewResponse> PreviewAsync(
        string repositoryId,
        ScopeConfigurationDocument document,
        CancellationToken cancellationToken = default)
    {
        await RequireRepositoryAsync(repositoryId, cancellationToken);
        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            return new ScopePreviewResponse
            {
                IsValid = false,
                Errors = validation.Errors,
                Warnings = validation.Warnings
            };
        }

        var resolved = normalizer.Normalize(document);
        var impact = await BuildImpactPreviewAsync(repositoryId, resolved, cancellationToken);

        return new ScopePreviewResponse
        {
            IsValid = true,
            Errors = validation.Errors,
            Warnings = validation.Warnings,
            ContentHash = resolved.ContentHash,
            NormalizedConfiguration = JsonSerializer.Deserialize<ScopeConfigurationDocument>(
                resolved.NormalizedJson, JsonOptions),
            Impact = impact
        };
    }

    public async Task<ScopeConfigurationResponse> UpdateAsync(
        string repositoryId,
        ScopeConfigurationDocument document,
        string? actorUserId,
        string? changeSummary = null,
        CancellationToken cancellationToken = default)
    {
        await RequireRepositoryAsync(repositoryId, cancellationToken);

        var validation = validator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ScopeConfigurationException(
                "SCOPE_VALIDATION_FAILED",
                string.Join("; ", validation.Errors));
        }

        // 配置更新与生成任务共用仓库级协调锁
        var lockOwnerId = $"scope-config:{Guid.NewGuid():N}";
        var acquired = await generationLockService.TryAcquireAsync(
            context,
            repositoryId,
            RepositoryGenerationLockOwnerType.Repository,
            lockOwnerId,
            RepositoryGenerationLockScope.Repository,
            cancellationToken);

        if (!acquired)
        {
            throw new ScopeConfigurationException(
                "REPOSITORY_LOCKED",
                "仓库当前有生成或配置任务在执行，请稍后重试。");
        }

        try
        {
            var resolved = normalizer.Normalize(document);
            var current = await context.RepositoryScopeConfigurations
                .Where(item => item.RepositoryId == repositoryId && item.IsCurrent && !item.IsDeleted)
                .OrderByDescending(item => item.ConfigurationVersion)
                .FirstOrDefaultAsync(cancellationToken);

            if (current is not null
                && string.Equals(current.ContentHash, resolved.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return await GetAsync(repositoryId, cancellationToken);
            }

            var impact = await BuildImpactPreviewAsync(repositoryId, resolved, cancellationToken);
            var nextVersion = (current?.ConfigurationVersion ?? 0) + 1;

            if (current is not null)
            {
                current.IsCurrent = false;
                current.UpdateTimestamp();
            }

            var entity = new RepositoryScopeConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                RepositoryId = repositoryId,
                ConfigurationVersion = nextVersion,
                ConfigurationJson = resolved.NormalizedJson,
                ContentHash = resolved.ContentHash,
                IsCurrent = true,
                ReindexRequired = impact.ReindexRequired,
                IsLegacyFallback = false,
                CreatedByUserId = actorUserId,
                ChangeSummary = changeSummary,
                CreatedAt = DateTime.UtcNow
            };

            context.RepositoryScopeConfigurations.Add(entity);

            context.RepositoryScopeAuditLogs.Add(new RepositoryScopeAuditLog
            {
                Id = Guid.NewGuid().ToString(),
                RepositoryId = repositoryId,
                PreviousConfigurationVersion = current?.ConfigurationVersion,
                ConfigurationVersion = nextVersion,
                PreviousContentHash = current?.ContentHash,
                ContentHash = resolved.ContentHash,
                ActorUserId = actorUserId,
                Action = "Update",
                ImpactPreviewJson = JsonSerializer.Serialize(impact, JsonOptions),
                Notes = changeSummary,
                CreatedAt = DateTime.UtcNow
            });

            await context.SaveChangesAsync(cancellationToken);
            return await GetAsync(repositoryId, cancellationToken);
        }
        finally
        {
            await generationLockService.ReleaseAsync(
                context,
                repositoryId,
                RepositoryGenerationLockOwnerType.Repository,
                lockOwnerId,
                cancellationToken);
        }
    }

    public async Task<ResolvedScopeConfiguration?> GetResolvedCurrentAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var current = await context.RepositoryScopeConfigurations
            .AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && item.IsCurrent && !item.IsDeleted)
            .OrderByDescending(item => item.ConfigurationVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<ScopeConfigurationDocument>(current.ConfigurationJson, JsonOptions)
                       ?? throw new InvalidOperationException("Stored scope configuration is invalid JSON.");
        var resolved = normalizer.Normalize(document);
        return resolved;
    }

    public async Task<IRepositoryFileSelectionPolicy> GetPolicyAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await GetResolvedCurrentAsync(repositoryId, cancellationToken);
        return resolved is null
            ? policyFactory.CreateLegacyFallback()
            : policyFactory.Create(resolved);
    }

    private async Task<ScopeImpactPreview> BuildImpactPreviewAsync(
        string repositoryId,
        ResolvedScopeConfiguration next,
        CancellationToken cancellationToken)
    {
        var currentEntity = await context.RepositoryScopeConfigurations
            .AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && item.IsCurrent && !item.IsDeleted)
            .OrderByDescending(item => item.ConfigurationVersion)
            .FirstOrDefaultAsync(cancellationToken);

        ResolvedScopeConfiguration? previous = null;
        if (currentEntity is not null)
        {
            var previousDocument = JsonSerializer.Deserialize<ScopeConfigurationDocument>(
                currentEntity.ConfigurationJson, JsonOptions);
            if (previousDocument is not null)
            {
                previous = normalizer.Normalize(previousDocument);
            }
        }

        var previousIds = previous?.DocumentScopes.Select(scope => scope.Id).ToHashSet(StringComparer.Ordinal)
                          ?? new HashSet<string>(StringComparer.Ordinal);
        var nextIds = next.DocumentScopes.Select(scope => scope.Id).ToHashSet(StringComparer.Ordinal);

        var added = nextIds.Except(previousIds).OrderBy(id => id).ToArray();
        var removed = previousIds.Except(nextIds).OrderBy(id => id).ToArray();
        var changed = next.DocumentScopes
            .Where(scope => previousIds.Contains(scope.Id))
            .Where(scope =>
            {
                var old = previous!.DocumentScopes.First(item => item.Id == scope.Id);
                return !string.Equals(old.Root, scope.Root, StringComparison.OrdinalIgnoreCase)
                       || !old.IncludedPathGlobs.SequenceEqual(scope.IncludedPathGlobs)
                       || !old.ExcludedPathGlobs.SequenceEqual(scope.ExcludedPathGlobs)
                       || !old.IncludedSuffixes.SequenceEqual(scope.IncludedSuffixes, StringComparer.OrdinalIgnoreCase)
                       || old.AcceptAllTextFiles != scope.AcceptAllTextFiles;
            })
            .Select(scope => scope.Id)
            .OrderBy(id => id)
            .ToArray();

        var languages = await context.RepositoryBranches
            .AsNoTracking()
            .Where(branch => branch.RepositoryId == repositoryId && !branch.IsDeleted)
            .Join(
                context.BranchLanguages.AsNoTracking().Where(language => !language.IsDeleted),
                branch => branch.Id,
                language => language.RepositoryBranchId,
                (_, language) => language.LanguageCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pageCount = await context.RepositoryBranches
            .AsNoTracking()
            .Where(branch => branch.RepositoryId == repositoryId && !branch.IsDeleted)
            .Join(
                context.BranchLanguages.AsNoTracking().Where(language => !language.IsDeleted),
                branch => branch.Id,
                language => language.RepositoryBranchId,
                (_, language) => language.Id)
            .Join(
                context.DocCatalogs.AsNoTracking().Where(catalog => !catalog.IsDeleted),
                languageId => languageId,
                catalog => catalog.BranchLanguageId,
                (_, catalog) => catalog.Id)
            .CountAsync(cancellationToken);

        var reindexRequired = previous is null
                              || added.Length > 0
                              || removed.Length > 0
                              || changed.Length > 0
                              || previous.WorkspaceContentPolicy != next.WorkspaceContentPolicy;

        var estimatedInvalidated = removed.Length > 0 || changed.Length > 0
            ? pageCount
            : 0;

        return new ScopeImpactPreview
        {
            PreviousConfigurationVersion = currentEntity?.ConfigurationVersion,
            NextConfigurationVersion = (currentEntity?.ConfigurationVersion ?? 0) + 1,
            ReindexRequired = reindexRequired,
            AddedDocumentScopeIds = added,
            RemovedDocumentScopeIds = removed,
            ChangedDocumentScopeIds = changed,
            AffectedLanguageCodes = languages,
            EstimatedInvalidatedPages = estimatedInvalidated,
            RebuildScopeSummary = reindexRequired
                ? "Scope 语义变化，需按新 DocumentScope 重建 inventory/文档主体。"
                : "配置无实质范围变化，可不触发全量重建。",
            MigrationHint = previous is null
        };
    }

    private async Task<Repository> RequireRepositoryAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);

        return repository
               ?? throw new ScopeConfigurationException("REPOSITORY_NOT_FOUND", "仓库不存在");
    }
}

public sealed class ScopeConfigurationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class ScopeConfigurationResponse
{
    public required string RepositoryId { get; init; }

    public bool HasConfiguration { get; init; }

    public bool MigrationHint { get; init; }

    public string? MigrationMessage { get; init; }

    public int? ConfigurationVersion { get; init; }

    public string? ContentHash { get; init; }

    public bool ReindexRequired { get; init; }

    public ScopeConfigurationDocument? Configuration { get; init; }

    public string? SourceType { get; init; }

    public DateTime? UpdatedAt { get; init; }
}

public sealed class ScopePreviewResponse
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? ContentHash { get; init; }

    public ScopeConfigurationDocument? NormalizedConfiguration { get; init; }

    public ScopeImpactPreview? Impact { get; init; }
}
