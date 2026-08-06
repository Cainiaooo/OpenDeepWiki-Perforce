using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories.Scope;

public interface IWikiGenerationService
{
    Task<WikiGeneration> BeginStagingAsync(
        IContext context,
        BeginWikiGenerationRequest request,
        CancellationToken cancellationToken = default);

    Task PublishAsync(
        IContext context,
        string generationId,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        IContext context,
        string generationId,
        string errorMessage,
        CancellationToken cancellationToken = default);

    Task AbandonAsync(
        IContext context,
        string generationId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<BranchLanguagePublication?> GetPublicationAsync(
        string branchLanguageId,
        CancellationToken cancellationToken = default);

    Task<bool> AreDerivativesCurrentAsync(
        string branchLanguageId,
        CancellationToken cancellationToken = default);
}

public sealed class BeginWikiGenerationRequest
{
    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public required string BranchLanguageId { get; init; }

    public required string LanguageCode { get; init; }

    public int? ScopeConfigurationVersion { get; init; }

    public string? ScopeContentHash { get; init; }

    public string? TargetRevision { get; init; }

    public string? TrackedManifestHash { get; init; }

    public string? ManifestJson { get; init; }

    public string? GenerationEngineVersion { get; init; }

    public string? OwnerTaskId { get; init; }

    public string? OwnerTaskType { get; init; }
}

/// <summary>
/// 首期简化：staging 标记 + 校验通过后事务性翻转。
/// 失败不推进 LastCommitId / 发布指针，读者仍访问上一完整快照。
/// </summary>
public sealed class WikiGenerationService(IContext rootContext) : IWikiGenerationService
{
    public async Task<WikiGeneration> BeginStagingAsync(
        IContext context,
        BeginWikiGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        // 执行中若配置版本变化，调用方应在 Publish 前复验；此处记录开始时版本。
        var snapshotIdentity = SnapshotIdentityBuilder.Build(
            request.RepositoryId,
            request.BranchId,
            request.ScopeConfigurationVersion,
            request.TargetRevision,
            request.TrackedManifestHash,
            request.GenerationEngineVersion);

        var publicationIdentity = SnapshotIdentityBuilder.BuildPublicationIdentity(
            snapshotIdentity,
            request.LanguageCode);

        var generation = new WikiGeneration
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = request.RepositoryId,
            BranchId = request.BranchId,
            BranchLanguageId = request.BranchLanguageId,
            Status = WikiGenerationStatus.Staging,
            ScopeConfigurationVersion = request.ScopeConfigurationVersion,
            ScopeContentHash = request.ScopeContentHash,
            TargetRevision = request.TargetRevision,
            TrackedManifestHash = request.TrackedManifestHash,
            GenerationEngineVersion = request.GenerationEngineVersion
                                      ?? SnapshotIdentityBuilder.GenerationEngineVersion,
            SnapshotIdentity = snapshotIdentity,
            PublicationIdentity = publicationIdentity,
            LanguageCode = request.LanguageCode,
            ManifestJson = request.ManifestJson,
            OwnerTaskId = request.OwnerTaskId,
            OwnerTaskType = request.OwnerTaskType,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        context.WikiGenerations.Add(generation);
        await context.SaveChangesAsync(cancellationToken);
        return generation;
    }

    public async Task PublishAsync(
        IContext context,
        string generationId,
        CancellationToken cancellationToken = default)
    {
        var generation = await context.WikiGenerations
            .FirstOrDefaultAsync(item => item.Id == generationId && !item.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Wiki generation '{generationId}' not found.");

        if (generation.Status != WikiGenerationStatus.Staging)
        {
            throw new InvalidOperationException(
                $"Only staging generations can be published. Current status: {generation.Status}.");
        }

        // 配置版本在执行中变化时不发布
        if (generation.ScopeConfigurationVersion is int expectedVersion)
        {
            var currentConfig = await context.RepositoryScopeConfigurations
                .AsNoTracking()
                .Where(item => item.RepositoryId == generation.RepositoryId
                               && item.IsCurrent
                               && !item.IsDeleted)
                .Select(item => new { item.ConfigurationVersion, item.ContentHash })
                .FirstOrDefaultAsync(cancellationToken);

            if (currentConfig is not null
                && (currentConfig.ConfigurationVersion != expectedVersion
                    || (!string.IsNullOrEmpty(generation.ScopeContentHash)
                        && !string.Equals(
                            currentConfig.ContentHash,
                            generation.ScopeContentHash,
                            StringComparison.OrdinalIgnoreCase))))
            {
                generation.Status = WikiGenerationStatus.Abandoned;
                generation.ErrorMessage = "Scope configuration changed during generation; refusing publish.";
                generation.FailedAt = DateTime.UtcNow;
                generation.UpdateTimestamp();
                await SoftDeleteGenerationContentAsync(context, generation.Id, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException(generation.ErrorMessage);
            }
        }

        var previousPublished = await context.WikiGenerations
            .Where(item => item.BranchLanguageId == generation.BranchLanguageId
                           && item.Status == WikiGenerationStatus.Published
                           && item.Id != generation.Id
                           && !item.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var previous in previousPublished)
        {
            previous.Status = WikiGenerationStatus.Superseded;
            previous.UpdateTimestamp();
        }

        generation.Status = WikiGenerationStatus.Published;
        generation.PublishedAt = DateTime.UtcNow;
        generation.UpdateTimestamp();

        var publication = await context.BranchLanguagePublications
            .FirstOrDefaultAsync(
                item => item.BranchLanguageId == generation.BranchLanguageId && !item.IsDeleted,
                cancellationToken);

        if (publication is null)
        {
            publication = new BranchLanguagePublication
            {
                Id = Guid.NewGuid().ToString(),
                BranchLanguageId = generation.BranchLanguageId,
                CurrentGenerationId = generation.Id,
                // 衍生产物尚未对齐时保持 null/旧值，读取端识别落后
                DerivativeSourceGenerationId = null,
                PublishedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            };
            context.BranchLanguagePublications.Add(publication);
        }
        else
        {
            publication.CurrentGenerationId = generation.Id;
            publication.PublishedAt = DateTime.UtcNow;
            publication.UpdateTimestamp();
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(
        IContext context,
        string generationId,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        var generation = await context.WikiGenerations
            .FirstOrDefaultAsync(item => item.Id == generationId && !item.IsDeleted, cancellationToken);

        if (generation is null)
        {
            return;
        }

        if (generation.Status == WikiGenerationStatus.Published)
        {
            throw new InvalidOperationException("Cannot fail an already published generation.");
        }

        generation.Status = WikiGenerationStatus.Failed;
        generation.ErrorMessage = errorMessage;
        generation.FailedAt = DateTime.UtcNow;
        generation.UpdateTimestamp();
        await SoftDeleteGenerationContentAsync(context, generation.Id, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AbandonAsync(
        IContext context,
        string generationId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var generation = await context.WikiGenerations
            .FirstOrDefaultAsync(item => item.Id == generationId && !item.IsDeleted, cancellationToken);

        if (generation is null || generation.Status == WikiGenerationStatus.Published)
        {
            return;
        }

        generation.Status = WikiGenerationStatus.Abandoned;
        generation.ErrorMessage = reason;
        generation.FailedAt = DateTime.UtcNow;
        generation.UpdateTimestamp();
        await SoftDeleteGenerationContentAsync(context, generation.Id, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Staging 失败时清除该 generation 的正文，读者仍只见已发布 generation。
    /// </summary>
    private static async Task SoftDeleteGenerationContentAsync(
        IContext context,
        string generationId,
        CancellationToken cancellationToken)
    {
        var catalogs = await context.DocCatalogs
            .Where(item => item.GenerationId == generationId && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var catalog in catalogs)
        {
            catalog.MarkAsDeleted();
        }

        var files = await context.DocFiles
            .Where(item => item.GenerationId == generationId && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var file in files)
        {
            file.MarkAsDeleted();
        }
    }

    public Task<BranchLanguagePublication?> GetPublicationAsync(
        string branchLanguageId,
        CancellationToken cancellationToken = default)
    {
        return rootContext.BranchLanguagePublications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.BranchLanguageId == branchLanguageId && !item.IsDeleted,
                cancellationToken);
    }

    public async Task<bool> AreDerivativesCurrentAsync(
        string branchLanguageId,
        CancellationToken cancellationToken = default)
    {
        var publication = await GetPublicationAsync(branchLanguageId, cancellationToken);
        if (publication is null || publication.CurrentGenerationId is null)
        {
            return true;
        }

        return string.Equals(
            publication.CurrentGenerationId,
            publication.DerivativeSourceGenerationId,
            StringComparison.Ordinal);
    }
}
