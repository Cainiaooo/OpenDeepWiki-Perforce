using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Services.Context;

public interface IWikiSnapshotResolver
{
    /// <summary>
    /// 按仓库/分支/语言与请求版本解析可读 Wiki Snapshot。
    /// </summary>
    Task<SnapshotResolveResult> ResolveAsync(
        string repositoryId,
        string? branchName,
        string? languageCode,
        string? requestedRevision,
        SnapshotResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 基于 UE Build Identity 解析 Wiki Snapshot（含跨分支拒绝与写操作门禁）。
    /// </summary>
    Task<SnapshotResolveResult> ResolveForBuildIdentityAsync(
        string repositoryId,
        BuildIdentity identity,
        string? languageCode,
        EditorOperationRiskLevel riskLevel,
        SnapshotResolveOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 从已发布 WikiGeneration 中选择与请求 CL 兼容的快照。
/// 规则：优先 Exact → 配置范围内 CompatibleFallback → AllowStale 时 Stale → 否则 Rejected。
/// </summary>
public sealed class WikiSnapshotResolver(IContext context) : IWikiSnapshotResolver
{
    public async Task<SnapshotResolveResult> ResolveAsync(
        string repositoryId,
        string? branchName,
        string? languageCode,
        string? requestedRevision,
        SnapshotResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotResolveOptions();
        var warnings = new List<ContextWarning>();

        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);

        if (repository is null)
        {
            return Reject(ContextReasonCodes.RejectedRepositoryNotFound, "仓库不存在");
        }

        var branchQuery = context.RepositoryBranches
            .AsNoTracking()
            .Where(b => b.RepositoryId == repositoryId && !b.IsDeleted);

        RepositoryBranch? branch;
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            branch = await branchQuery
                .FirstOrDefaultAsync(
                    b => b.BranchName == branchName,
                    cancellationToken);
        }
        else
        {
            // 无默认分支标记：取最早创建分支作为默认
            branch = await branchQuery
                .OrderBy(b => b.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (branch is null)
        {
            return Reject(ContextReasonCodes.RejectedBranchNotFound, "分支不存在");
        }

        var languageQuery = context.BranchLanguages
            .AsNoTracking()
            .Where(bl => bl.RepositoryBranchId == branch.Id && !bl.IsDeleted);

        BranchLanguage? language;
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            language = await languageQuery
                .FirstOrDefaultAsync(bl => bl.LanguageCode == languageCode, cancellationToken);

            if (language is null)
            {
                // 回退到默认语言
                language = await languageQuery
                    .OrderByDescending(bl => bl.IsDefault)
                    .ThenBy(bl => bl.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (language is not null)
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.UnknownSnapshot,
                        Message = $"请求语言 '{languageCode}' 不存在，已回退到 '{language.LanguageCode}'",
                        Severity = "warning"
                    });
                }
            }
        }
        else
        {
            language = await languageQuery
                .OrderByDescending(bl => bl.IsDefault)
                .ThenBy(bl => bl.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (language is null)
        {
            return Reject(ContextReasonCodes.RejectedLanguageNotFound, "语言文档不存在");
        }

        var publication = await context.BranchLanguagePublications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.BranchLanguageId == language.Id && !p.IsDeleted,
                cancellationToken);

        WikiGeneration? generation = null;
        if (publication?.CurrentGenerationId is { Length: > 0 } currentId)
        {
            generation = await context.WikiGenerations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    g => g.Id == currentId && !g.IsDeleted,
                    cancellationToken);
        }

        // 无发布指针：尝试遗留（无 generation 隔离）或任意已发布 generation
        if (generation is null)
        {
            generation = await context.WikiGenerations
                .AsNoTracking()
                .Where(g => g.BranchLanguageId == language.Id
                            && g.Status == WikiGenerationStatus.Published
                            && !g.IsDeleted)
                .OrderByDescending(g => g.PublishedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (generation is null)
        {
            // 兼容：有正文但无 WikiGeneration 记录
            var hasLegacyDocs = await context.DocCatalogs
                .AsNoTracking()
                .AnyAsync(
                    c => c.BranchLanguageId == language.Id
                         && !c.IsDeleted
                         && (c.GenerationId == null
                             || c.GenerationId == WikiPublicationQuery.LegacyGenerationId),
                    cancellationToken);

            if (hasLegacyDocs)
            {
                warnings.Add(new ContextWarning
                {
                    ReasonCode = ContextReasonCodes.UnknownSnapshot,
                    Message = "使用遗留正文（无 WikiGeneration 快照身份）",
                    Severity = "warning"
                });

                return new SnapshotResolveResult
                {
                    Compatibility = string.IsNullOrWhiteSpace(requestedRevision)
                        ? ContextCompatibility.Unknown
                        : ContextCompatibility.Unknown,
                    GenerationId = WikiPublicationQuery.LegacyGenerationId,
                    TargetRevision = null,
                    SnapshotIdentity = null,
                    BranchLanguageId = language.Id,
                    LanguageCode = language.LanguageCode,
                    Warnings = warnings
                };
            }

            return Reject(
                ContextReasonCodes.RejectedNoPublication,
                "当前分支语言尚无已发布 Wiki 快照",
                warnings);
        }

        var resolvedRevision = generation.TargetRevision;
        var requested = NormalizeRevision(requestedRevision);

        if (string.IsNullOrEmpty(requested))
        {
            if (!options.AllowCurrentWhenRevisionOmitted)
            {
                return Reject(
                    ContextReasonCodes.RejectedNoPublication,
                    "未指定请求版本且不允许使用当前发布快照",
                    warnings);
            }

            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.ExactMatch,
                Message = $"未指定请求版本，使用当前发布快照 revision={resolvedRevision ?? "unknown"}",
                Severity = "info"
            });

            return Success(
                ContextCompatibility.Exact,
                generation,
                language,
                warnings);
        }

        // Exact
        if (string.Equals(requested, NormalizeRevision(resolvedRevision), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.ExactMatch,
                Message = $"Wiki 快照精确匹配请求版本 {requested}",
                Severity = "info"
            });
            return Success(ContextCompatibility.Exact, generation, language, warnings);
        }

        // 尝试在已发布历史中找更精确的 generation
        var historical = await context.WikiGenerations
            .AsNoTracking()
            .Where(g => g.BranchLanguageId == language.Id
                        && !g.IsDeleted
                        && (g.Status == WikiGenerationStatus.Published
                            || g.Status == WikiGenerationStatus.Superseded)
                        && g.TargetRevision != null)
            .OrderByDescending(g => g.PublishedAt)
            .ToListAsync(cancellationToken);

        var exactHistorical = historical.FirstOrDefault(g =>
            string.Equals(
                NormalizeRevision(g.TargetRevision),
                requested,
                StringComparison.OrdinalIgnoreCase));

        if (exactHistorical is not null)
        {
            warnings.Add(new ContextWarning
            {
                ReasonCode = ContextReasonCodes.ExactMatch,
                Message = $"使用历史发布快照精确匹配请求版本 {requested}",
                Severity = "info"
            });
            return Success(ContextCompatibility.Exact, exactHistorical, language, warnings);
        }

        // 数字 CL 兼容回退
        if (TryParseRevision(requested, out var requestedCl)
            && TryParseRevision(resolvedRevision, out var currentCl))
        {
            // 请求版本远新于当前 Wiki → Wiki 落后
            if (requestedCl > currentCl)
            {
                var distance = requestedCl - currentCl;
                if (distance <= options.MaxCompatibleFallbackDistance)
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.CompatibleFallback,
                        Message =
                            $"请求 CL {requestedCl} 新于 Wiki CL {currentCl}（差 {distance}），使用当前兼容回退快照",
                        Severity = "warning"
                    });
                    return Success(ContextCompatibility.CompatibleFallback, generation, language, warnings);
                }

                if (options.AllowStale)
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.StaleSnapshot,
                        Message =
                            $"请求 CL {requestedCl} 远新于 Wiki CL {currentCl}（差 {distance}），快照可能过期",
                        Severity = "warning"
                    });
                    return Success(ContextCompatibility.Stale, generation, language, warnings);
                }

                return Reject(
                    ContextReasonCodes.RejectedFutureRevision,
                    $"请求 CL {requestedCl} 远新于已发布 Wiki CL {currentCl}，拒绝返回可执行上下文",
                    warnings);
            }

            // 请求版本旧于当前 Wiki：寻找不超过 requested 的最近历史快照
            var candidates = historical
                .Where(g => TryParseRevision(g.TargetRevision, out var cl) && cl <= requestedCl)
                .Select(g => (Generation: g, Cl: ParseRevisionOrZero(g.TargetRevision)))
                .OrderByDescending(x => x.Cl)
                .ToList();

            if (candidates.Count > 0)
            {
                var best = candidates[0];
                var distance = requestedCl - best.Cl;
                if (distance == 0)
                {
                    return Success(ContextCompatibility.Exact, best.Generation, language, warnings);
                }

                if (distance <= options.MaxCompatibleFallbackDistance)
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.CompatibleFallback,
                        Message =
                            $"请求 CL {requestedCl}，使用最近兼容历史快照 CL {best.Cl}（差 {distance}）",
                        Severity = "warning"
                    });
                    return Success(ContextCompatibility.CompatibleFallback, best.Generation, language, warnings);
                }
            }

            // 只有比请求更新的快照（含未来知识风险）
            if (currentCl > requestedCl)
            {
                if (options.AllowStale)
                {
                    warnings.Add(new ContextWarning
                    {
                        ReasonCode = ContextReasonCodes.StaleSnapshot,
                        Message =
                            $"仅有新于请求 CL {requestedCl} 的 Wiki（当前 {currentCl}）。可能混入未来 CL 内容，请谨慎使用",
                        Severity = "warning"
                    });
                    return Success(ContextCompatibility.Stale, generation, language, warnings);
                }

                return Reject(
                    ContextReasonCodes.RejectedFutureRevision,
                    $"仅有新于请求 CL 的 Wiki 快照，拒绝返回以免混入未来内容",
                    warnings);
            }
        }

        // 非数字 revision（git sha 等）：无法精确比较
        warnings.Add(new ContextWarning
        {
            ReasonCode = ContextReasonCodes.UnknownSnapshot,
            Message =
                $"无法将请求版本 '{requested}' 与快照版本 '{resolvedRevision}' 做数值比较，使用当前发布快照",
            Severity = "warning"
        });
        return Success(ContextCompatibility.Unknown, generation, language, warnings);
    }

    public async Task<SnapshotResolveResult> ResolveForBuildIdentityAsync(
        string repositoryId,
        BuildIdentity identity,
        string? languageCode,
        EditorOperationRiskLevel riskLevel,
        SnapshotResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SnapshotResolveOptions();
        var warnings = new List<ContextWarning>();

        // 写操作必须提供 Build CL：省略版本时 ResolveAsync 会把“当前快照”标为 Exact，不能当作成功握手
        if (riskLevel == EditorOperationRiskLevel.Write
            && string.IsNullOrWhiteSpace(identity.BuildChangelist))
        {
            return Reject(
                ContextReasonCodes.RejectedWriteRequiresExact,
                "写操作要求提供 BuildChangelist，以证明 Wiki 与编辑器 Build 精确匹配",
                warnings);
        }

        var branchName = identity.Branch;
        var baseResult = await ResolveAsync(
            repositoryId,
            branchName,
            languageCode,
            identity.BuildChangelist,
            options with
            {
                AllowStale = riskLevel == EditorOperationRiskLevel.Query && options.AllowStale,
                RequireExactForWrite = riskLevel == EditorOperationRiskLevel.Write,
                // 写操作不允许“省略版本即当前 Exact”
                AllowCurrentWhenRevisionOmitted = riskLevel != EditorOperationRiskLevel.Write
            },
            cancellationToken);

        warnings.AddRange(baseResult.Warnings);

        if (baseResult.IsRejected)
        {
            return baseResult;
        }

        if (riskLevel == EditorOperationRiskLevel.Write
            && baseResult.Compatibility != ContextCompatibility.Exact)
        {
            return Reject(
                ContextReasonCodes.RejectedWriteRequiresExact,
                $"写操作要求 Wiki 与 Build CL 精确匹配，当前兼容状态为 {baseResult.Compatibility}",
                warnings);
        }

        if (riskLevel == EditorOperationRiskLevel.Suggest
            && baseResult.Compatibility is ContextCompatibility.Stale or ContextCompatibility.Unknown)
        {
            return Reject(
                ContextReasonCodes.RejectedWriteRequiresExact,
                $"建议类操作不允许 Stale/Unknown 快照，当前兼容状态为 {baseResult.Compatibility}",
                warnings);
        }

        return new SnapshotResolveResult
        {
            Compatibility = baseResult.Compatibility,
            GenerationId = baseResult.GenerationId,
            TargetRevision = baseResult.TargetRevision,
            SnapshotIdentity = baseResult.SnapshotIdentity,
            BranchLanguageId = baseResult.BranchLanguageId,
            LanguageCode = baseResult.LanguageCode,
            ScopeConfigurationVersion = baseResult.ScopeConfigurationVersion,
            TrackedManifestHash = baseResult.TrackedManifestHash,
            Warnings = warnings
        };
    }

    private static SnapshotResolveResult Success(
        ContextCompatibility compatibility,
        WikiGeneration generation,
        BranchLanguage language,
        List<ContextWarning> warnings)
    {
        return new SnapshotResolveResult
        {
            Compatibility = compatibility,
            GenerationId = generation.Id,
            TargetRevision = generation.TargetRevision,
            SnapshotIdentity = generation.SnapshotIdentity,
            BranchLanguageId = language.Id,
            LanguageCode = language.LanguageCode,
            ScopeConfigurationVersion = generation.ScopeConfigurationVersion,
            TrackedManifestHash = generation.TrackedManifestHash,
            Warnings = warnings
        };
    }

    private static SnapshotResolveResult Reject(
        string reasonCode,
        string message,
        List<ContextWarning>? priorWarnings = null)
    {
        var warnings = priorWarnings is null
            ? new List<ContextWarning>()
            : new List<ContextWarning>(priorWarnings);

        warnings.Add(new ContextWarning
        {
            ReasonCode = reasonCode,
            Message = message,
            Severity = "error"
        });

        return new SnapshotResolveResult
        {
            Compatibility = ContextCompatibility.Rejected,
            Warnings = warnings
        };
    }

    private static string? NormalizeRevision(string? revision)
        => string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

    private static bool TryParseRevision(string? revision, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(revision))
        {
            return false;
        }

        return long.TryParse(revision.Trim(), out value);
    }

    private static long ParseRevisionOrZero(string? revision)
        => TryParseRevision(revision, out var value) ? value : 0;
}
