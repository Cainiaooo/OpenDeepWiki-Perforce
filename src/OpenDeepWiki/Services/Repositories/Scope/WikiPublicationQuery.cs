using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// 读取端可见 generation 解析：有发布指针时只读当前 generation 的正文。
/// </summary>
public static class WikiPublicationQuery
{
    public const string LegacyGenerationId = "";

    public static async Task<string?> GetPublishedGenerationIdAsync(
        IContext context,
        string branchLanguageId,
        CancellationToken cancellationToken = default)
    {
        var publication = await context.BranchLanguagePublications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.BranchLanguageId == branchLanguageId && !item.IsDeleted,
                cancellationToken);

        return publication?.CurrentGenerationId;
    }

    public static IQueryable<DocCatalog> FilterVisibleCatalogs(
        IQueryable<DocCatalog> query,
        string branchLanguageId,
        string? publishedGenerationId)
    {
        query = query.Where(catalog =>
            catalog.BranchLanguageId == branchLanguageId && !catalog.IsDeleted);

        if (string.IsNullOrEmpty(publishedGenerationId))
        {
            // 无发布指针：兼容旧数据（GenerationId 为空）
            return query.Where(catalog =>
                catalog.GenerationId == null || catalog.GenerationId == LegacyGenerationId);
        }

        return query.Where(catalog => catalog.GenerationId == publishedGenerationId);
    }

    public static IQueryable<DocFile> FilterVisibleFiles(
        IQueryable<DocFile> query,
        string branchLanguageId,
        string? publishedGenerationId)
    {
        query = query.Where(file => file.BranchLanguageId == branchLanguageId && !file.IsDeleted);

        if (string.IsNullOrEmpty(publishedGenerationId))
        {
            return query.Where(file =>
                file.GenerationId == null || file.GenerationId == LegacyGenerationId);
        }

        return query.Where(file => file.GenerationId == publishedGenerationId);
    }

    public static bool MatchesGeneration(string? entityGenerationId, string? scopeGenerationId)
    {
        var entity = entityGenerationId ?? LegacyGenerationId;
        var scope = scopeGenerationId ?? LegacyGenerationId;
        return string.Equals(entity, scope, StringComparison.Ordinal);
    }
}
