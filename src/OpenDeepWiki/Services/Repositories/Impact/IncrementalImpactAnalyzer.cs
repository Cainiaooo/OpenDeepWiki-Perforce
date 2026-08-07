using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.UeKnowledge;

namespace OpenDeepWiki.Services.Repositories.Impact;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncrementalImpactLevel
{
    None = 0,
    LeafPages = 1,
    DomainInventory = 2,
    DomainReplan = 3,
    FullInventoryAndPlanning = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncrementalChangePathRole
{
    Unknown = 0,
    Document = 1,
    Context = 2,
    TriggerOnly = 3,
    Excluded = 4
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncrementalDependencyKind
{
    Content = 0,
    Planning = 1
}

public static class IncrementalImpactReasonCodes
{
    public const string NoRelevantChanges = "impact.no_relevant_changes";
    public const string LegacyIncrementalFallback = "impact.legacy_incremental_fallback";
    public const string RecordedPageSourceChanged = "impact.recorded_page_source_changed";
    public const string NewDocumentFile = "impact.new_document_file";
    public const string StructuralFileChanged = "impact.structural_file_changed";
    public const string DocumentMoveChangedStructure = "impact.document_move_changed_structure";
    public const string ContextDependencyChanged = "impact.context_dependency_changed";
    public const string UeFactDependencyChanged = "impact.ue_fact_dependency_changed";
    public const string UeDomainHintChanged = "impact.ue_domain_hint_changed";
    public const string ScopeConfigurationChanged = "impact.scope_configuration_changed";
    public const string MissingDocumentDependency = "impact.missing_document_dependency";
    public const string MissingContextDependency = "impact.missing_context_dependency";
    public const string MissingUeFactDependency = "impact.missing_ue_fact_dependency";
    public const string UnknownTriggeredPath = "impact.unknown_triggered_path";
    public const string DependencyReadFailed = "impact.dependency_read_failed";
    public const string DependencyScanBudgetExceeded = "impact.dependency_scan_budget_exceeded";
    public const string AnalysisFailed = "impact.analysis_failed";
    public const string PartialGenerationUnavailable = "execution.partial_generation_unavailable";
    public const string ChangelistThresholdExceeded = "threshold.changelists_exceeded";
    public const string FileThresholdExceeded = "threshold.files_exceeded";
}

/// <summary>
/// 来源无关的逻辑变化。Perforce、Git 或其他来源只负责把原生 action 转成 old/new 路径。
/// </summary>
public sealed record IncrementalSourceChange(
    string Action,
    string? OldPath,
    string? NewPath,
    string? FileType = null);

/// <summary>
/// 当前发布页面已经记录的反向依赖。T5.2 首期从 DocFile.SourceFiles 读取；
/// T5.3 将用持久化 Knowledge Dependency Index 替换该临时来源并区分更多 fact 类型。
/// </summary>
public sealed record IncrementalPageDependency(
    string SourceId,
    string PageId,
    string PagePath,
    string BranchLanguageId,
    IncrementalDependencyKind Kind = IncrementalDependencyKind.Content);

public sealed class IncrementalImpactRequest
{
    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public required IReadOnlyList<IncrementalSourceChange> Changes { get; init; }

    public IRepositoryFileSelectionPolicy? SelectionPolicy { get; init; }

    public bool CaseSensitivePaths { get; init; }

    public bool ScopeConfigurationChanged { get; init; }
}

public sealed class UeKnowledgeImpactRequest
{
    public required UeKnowledgeSemanticDiffResult SemanticDiff { get; init; }

    public IReadOnlyList<IncrementalPageDependency> RecordedDependencies { get; init; } = [];
}

public sealed class IncrementalImpactPlan
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>影响分析得到的最小安全范围。</summary>
    public required IncrementalImpactLevel RequestedLevel { get; init; }

    /// <summary>当前 worker 实际能够执行的范围；可能因能力缺口保守扩大。</summary>
    public required IncrementalImpactLevel ExecutionLevel { get; init; }

    public required IReadOnlyList<string> ReasonCodes { get; init; }

    public IReadOnlyList<string> ChangedPaths { get; init; } = [];

    public IReadOnlyList<string> AffectedPageIds { get; init; } = [];

    public IReadOnlyList<string> AffectedPagePaths { get; init; } = [];

    public IReadOnlyList<string> AffectedDomainHints { get; init; } = [];

    public bool IsFailClosed { get; init; }

    [JsonIgnore]
    public bool WasExpandedForExecution => ExecutionLevel > RequestedLevel;

    [JsonIgnore]
    public bool RequiresFullGeneration
        => ExecutionLevel == IncrementalImpactLevel.FullInventoryAndPlanning;

    [JsonIgnore]
    public string Summary
        => $"requested={RequestedLevel}, execution={ExecutionLevel}, reasons={string.Join(',', ReasonCodes)}";

    public static IncrementalImpactPlan Full(string reasonCode, IEnumerable<string>? changedPaths = null)
        => new()
        {
            RequestedLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
            ExecutionLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
            ReasonCodes = [reasonCode],
            ChangedPaths = NormalizeAndOrder(changedPaths),
            IsFailClosed = true
        };

    public static IncrementalImpactPlan None()
        => new()
        {
            RequestedLevel = IncrementalImpactLevel.None,
            ExecutionLevel = IncrementalImpactLevel.None,
            ReasonCodes = [IncrementalImpactReasonCodes.NoRelevantChanges]
        };

    private static IReadOnlyList<string> NormalizeAndOrder(IEnumerable<string>? paths)
        => paths is null
            ? []
            : paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(ScopePathUtility.NormalizeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

public static class IncrementalImpactPlanSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(IncrementalImpactPlan plan)
        => JsonSerializer.Serialize(plan, JsonOptions);

    public static IncrementalImpactPlan? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IncrementalImpactPlan>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public interface IIncrementalImpactAnalyzer
{
    Task<IncrementalImpactPlan> AnalyzeAsync(
        IncrementalImpactRequest request,
        CancellationToken cancellationToken = default);

    IncrementalImpactPlan AnalyzeUeKnowledge(UeKnowledgeImpactRequest request);
}

/// <summary>
/// T5.2 影响分级器。分类逻辑不依赖 Perforce；数据库读取仅用于复用当前已发布页面的
/// SourceFiles 内容依赖。无法证明安全的情况一律升级，并保留稳定 reason code。
/// </summary>
public sealed class IncrementalImpactAnalyzer(
    IContext context,
    ILogger<IncrementalImpactAnalyzer> logger) : IIncrementalImpactAnalyzer
{
    internal const int MaxDependencyPages = 20_000;

    public async Task<IncrementalImpactPlan> AnalyzeAsync(
        IncrementalImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var changedPaths = CollectChangedPaths(request.Changes, request.CaseSensitivePaths);
        if (request.ScopeConfigurationChanged)
        {
            return new IncrementalImpactPlan
            {
                RequestedLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ExecutionLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ReasonCodes = [IncrementalImpactReasonCodes.ScopeConfigurationChanged],
                ChangedPaths = changedPaths,
                IsFailClosed = true
            };
        }

        if (request.Changes.Count == 0)
        {
            return IncrementalImpactPlan.None();
        }

        // 无 Phase 3 Scope 配置时保持上游/历史增量行为，避免把兼容仓库意外升级成全量。
        if (request.SelectionPolicy is null)
        {
            return new IncrementalImpactPlan
            {
                RequestedLevel = IncrementalImpactLevel.LeafPages,
                ExecutionLevel = IncrementalImpactLevel.LeafPages,
                ReasonCodes = [IncrementalImpactReasonCodes.LegacyIncrementalFallback],
                ChangedPaths = changedPaths
            };
        }

        try
        {
            var dependencies = await LoadCurrentDependenciesAsync(request, cancellationToken);
            return Classify(request, dependencies, changedPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DependencyLoadException ex)
        {
            logger.LogWarning(
                ex,
                "Incremental dependency evidence is incomplete; escalating impact. RepositoryId: {RepositoryId}, BranchId: {BranchId}, Reason: {Reason}",
                request.RepositoryId,
                request.BranchId,
                ex.ReasonCode);
            return new IncrementalImpactPlan
            {
                RequestedLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ExecutionLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ReasonCodes = [ex.ReasonCode],
                ChangedPaths = changedPaths,
                IsFailClosed = true
            };
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Incremental impact analysis failed; escalating to full generation. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                request.RepositoryId,
                request.BranchId);
            return new IncrementalImpactPlan
            {
                RequestedLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ExecutionLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
                ReasonCodes = [IncrementalImpactReasonCodes.AnalysisFailed],
                ChangedPaths = changedPaths,
                IsFailClosed = true
            };
        }
    }

    public IncrementalImpactPlan AnalyzeUeKnowledge(UeKnowledgeImpactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SemanticDiff);

        if (!request.SemanticDiff.HasSemanticChange)
        {
            return IncrementalImpactPlan.None();
        }

        var factIds = CollectUeFactIds(request.SemanticDiff);
        var dependencyMap = request.RecordedDependencies
            .GroupBy(dependency => dependency.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matchedFacts = factIds.Where(dependencyMap.ContainsKey).ToArray();
        var unmatchedFacts = factIds.Where(factId => !dependencyMap.ContainsKey(factId)).ToArray();
        var matchedPages = matchedFacts
            .SelectMany(factId => dependencyMap[factId])
            .ToArray();

        // 仅当全部 fact 都能解析到已登记依赖时才允许 LeafPages，避免部分命中静默漏更。
        if (matchedFacts.Length > 0 && unmatchedFacts.Length == 0)
        {
            return BuildPlan(
                IncrementalImpactLevel.LeafPages,
                [IncrementalImpactReasonCodes.UeFactDependencyChanged],
                factIds,
                matchedPages,
                request.SemanticDiff.AffectedDomainHints,
                isFailClosed: false);
        }

        var reasons = new List<string>();
        if (matchedFacts.Length > 0)
        {
            reasons.Add(IncrementalImpactReasonCodes.UeFactDependencyChanged);
        }

        if (unmatchedFacts.Length > 0)
        {
            reasons.Add(IncrementalImpactReasonCodes.MissingUeFactDependency);
        }

        if (request.SemanticDiff.AffectedDomainHints.Count > 0)
        {
            if (matchedFacts.Length == 0)
            {
                reasons.Insert(0, IncrementalImpactReasonCodes.UeDomainHintChanged);
            }

            return BuildPlan(
                IncrementalImpactLevel.DomainReplan,
                reasons,
                factIds,
                matchedPages,
                request.SemanticDiff.AffectedDomainHints,
                isFailClosed: true);
        }

        return BuildPlan(
            IncrementalImpactLevel.FullInventoryAndPlanning,
            reasons.Count > 0 ? reasons : [IncrementalImpactReasonCodes.MissingUeFactDependency],
            factIds,
            matchedPages,
            [],
            isFailClosed: true);
    }

    private static IncrementalImpactPlan Classify(
        IncrementalImpactRequest request,
        IReadOnlyList<IncrementalPageDependency> dependencies,
        IReadOnlyList<string> changedPaths)
    {
        var comparer = request.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var dependencyMap = dependencies
            .GroupBy(dependency => NormalizeSourceId(dependency.SourceId), comparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), comparer);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var pages = new Dictionary<string, IncrementalPageDependency>(StringComparer.Ordinal);
        var domains = new HashSet<string>(comparer);
        var requestedLevel = IncrementalImpactLevel.None;
        var failClosed = false;

        void Promote(IncrementalImpactLevel level, string reasonCode, bool isFailClosed = false)
        {
            if (level > requestedLevel)
            {
                requestedLevel = level;
            }

            reasons.Add(reasonCode);
            failClosed |= isFailClosed;
        }

        foreach (var change in request.Changes)
        {
            var oldPath = NormalizeNullable(change.OldPath);
            var newPath = NormalizeNullable(change.NewPath);
            var oldClassification = ClassifyPath(request.SelectionPolicy!, oldPath, change.FileType);
            var newClassification = ClassifyPath(request.SelectionPolicy!, newPath, change.FileType);
            var changeDependencies = ResolveDependencies(dependencyMap, comparer, oldPath, newPath);
            foreach (var dependency in changeDependencies)
            {
                pages.TryAdd(PageKey(dependency), dependency);
            }

            AddDomainHint(domains, oldClassification.ScopeId, oldPath);
            AddDomainHint(domains, newClassification.ScopeId, newPath);

            var structural = IsStructuralPath(oldPath) || IsStructuralPath(newPath);
            if (structural)
            {
                Promote(IncrementalImpactLevel.DomainReplan, IncrementalImpactReasonCodes.StructuralFileChanged);
                continue;
            }

            if (IsMove(change.Action, oldPath, newPath))
            {
                ClassifyMove(
                    oldClassification,
                    newClassification,
                    changeDependencies,
                    Promote);
                continue;
            }

            var active = newPath is null ? oldClassification : newClassification;
            switch (active.Role)
            {
                case IncrementalChangePathRole.Document:
                    if (IsAdd(change.Action))
                    {
                        Promote(IncrementalImpactLevel.DomainInventory, IncrementalImpactReasonCodes.NewDocumentFile);
                    }
                    else if (changeDependencies.Count > 0)
                    {
                        Promote(IncrementalImpactLevel.LeafPages, IncrementalImpactReasonCodes.RecordedPageSourceChanged);
                    }
                    else
                    {
                        Promote(
                            IncrementalImpactLevel.DomainReplan,
                            IncrementalImpactReasonCodes.MissingDocumentDependency,
                            isFailClosed: true);
                    }

                    break;
                case IncrementalChangePathRole.Context:
                    if (changeDependencies.Count > 0)
                    {
                        Promote(IncrementalImpactLevel.LeafPages, IncrementalImpactReasonCodes.ContextDependencyChanged);
                    }
                    else
                    {
                        Promote(
                            IncrementalImpactLevel.FullInventoryAndPlanning,
                            IncrementalImpactReasonCodes.MissingContextDependency,
                            isFailClosed: true);
                    }

                    break;
                case IncrementalChangePathRole.TriggerOnly:
                case IncrementalChangePathRole.Unknown:
                    Promote(
                        IncrementalImpactLevel.FullInventoryAndPlanning,
                        IncrementalImpactReasonCodes.UnknownTriggeredPath,
                        isFailClosed: true);
                    break;
                case IncrementalChangePathRole.Excluded:
                    // Perforce 适配层只会提交至少一端命中 ChangeTrigger 的变化；单端 excluded
                    // 到达这里说明分类证据与过滤结果不一致，按未知处理。
                    Promote(
                        IncrementalImpactLevel.FullInventoryAndPlanning,
                        IncrementalImpactReasonCodes.UnknownTriggeredPath,
                        isFailClosed: true);
                    break;
            }
        }

        if (requestedLevel == IncrementalImpactLevel.None)
        {
            return IncrementalImpactPlan.None();
        }

        return BuildPlan(
            requestedLevel,
            reasons,
            changedPaths,
            pages.Values,
            domains,
            failClosed,
            request.CaseSensitivePaths);
    }

    private static void ClassifyMove(
        PathClassification oldPath,
        PathClassification newPath,
        IReadOnlyList<IncrementalPageDependency> dependencies,
        Action<IncrementalImpactLevel, string, bool> promote)
    {
        var touchesDocument = oldPath.Role == IncrementalChangePathRole.Document
                              || newPath.Role == IncrementalChangePathRole.Document;
        if (touchesDocument)
        {
            var sameDocumentScope = oldPath.Role == IncrementalChangePathRole.Document
                                    && newPath.Role == IncrementalChangePathRole.Document
                                    && string.Equals(oldPath.ScopeId, newPath.ScopeId, StringComparison.Ordinal);
            promote(
                sameDocumentScope ? IncrementalImpactLevel.DomainInventory : IncrementalImpactLevel.DomainReplan,
                IncrementalImpactReasonCodes.DocumentMoveChangedStructure,
                false);
            return;
        }

        var touchesContext = oldPath.Role == IncrementalChangePathRole.Context
                             || newPath.Role == IncrementalChangePathRole.Context;
        if (touchesContext && dependencies.Count > 0)
        {
            promote(
                IncrementalImpactLevel.LeafPages,
                IncrementalImpactReasonCodes.ContextDependencyChanged,
                false);
            return;
        }

        if (touchesContext)
        {
            promote(
                IncrementalImpactLevel.FullInventoryAndPlanning,
                IncrementalImpactReasonCodes.MissingContextDependency,
                true);
            return;
        }

        promote(
            IncrementalImpactLevel.FullInventoryAndPlanning,
            IncrementalImpactReasonCodes.UnknownTriggeredPath,
            true);
    }

    private static IncrementalImpactPlan BuildPlan(
        IncrementalImpactLevel requestedLevel,
        IEnumerable<string> reasonCodes,
        IEnumerable<string> changedPaths,
        IEnumerable<IncrementalPageDependency> pages,
        IEnumerable<string> domains,
        bool isFailClosed,
        bool caseSensitivePaths = false)
    {
        var executionLevel = requestedLevel;
        var reasons = reasonCodes.ToHashSet(StringComparer.Ordinal);
        if (requestedLevel is IncrementalImpactLevel.DomainInventory or IncrementalImpactLevel.DomainReplan)
        {
            // 当前 BranchGenerationTask 只有 Full 模式。在领域级 worker 落地前保守扩大，
            // 同时保留 requestedLevel，避免对外声称已经做了局部领域重建。
            executionLevel = IncrementalImpactLevel.FullInventoryAndPlanning;
            reasons.Add(IncrementalImpactReasonCodes.PartialGenerationUnavailable);
        }

        var pathComparer = caseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var pageArray = pages
            .GroupBy(PageKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(page => page.BranchLanguageId, StringComparer.Ordinal)
            .ThenBy(page => page.PagePath, StringComparer.Ordinal)
            .ToArray();

        return new IncrementalImpactPlan
        {
            RequestedLevel = requestedLevel,
            ExecutionLevel = executionLevel,
            ReasonCodes = reasons.Order(StringComparer.Ordinal).ToArray(),
            ChangedPaths = changedPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeSourceId)
                .Distinct(pathComparer)
                .Order(pathComparer)
                .ToArray(),
            AffectedPageIds = pageArray.Select(page => page.PageId).Distinct(StringComparer.Ordinal).ToArray(),
            AffectedPagePaths = pageArray.Select(page => page.PagePath).Distinct(StringComparer.Ordinal).ToArray(),
            AffectedDomainHints = domains
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IsFailClosed = isFailClosed
        };
    }

    private async Task<IReadOnlyList<IncrementalPageDependency>> LoadCurrentDependenciesAsync(
        IncrementalImpactRequest request,
        CancellationToken cancellationToken)
    {
        var languageIds = await context.BranchLanguages
            .AsNoTracking()
            .Where(language => language.RepositoryBranchId == request.BranchId && !language.IsDeleted)
            .Select(language => language.Id)
            .ToListAsync(cancellationToken);
        if (languageIds.Count == 0)
        {
            return [];
        }

        var publications = await context.BranchLanguagePublications
            .AsNoTracking()
            .Where(publication => languageIds.Contains(publication.BranchLanguageId)
                                  && !publication.IsDeleted
                                  && publication.CurrentGenerationId != null)
            .Select(publication => new
            {
                publication.BranchLanguageId,
                publication.CurrentGenerationId
            })
            .ToListAsync(cancellationToken);
        var currentGenerationByLanguage = publications.ToDictionary(
            publication => publication.BranchLanguageId,
            publication => publication.CurrentGenerationId!,
            StringComparer.Ordinal);

        // SQL 层按当前世代过滤，避免多代 UE 大仓先整表拉入内存再过滤。
        // GenerationId 为 GUID，跨语言碰撞可忽略；无 publication 的遗留语言单独取空 GenerationId。
        var currentGenerationIds = currentGenerationByLanguage.Values
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var publishedLanguageIds = currentGenerationByLanguage.Keys.ToList();
        var unpublishedLanguageIds = languageIds
            .Where(id => !currentGenerationByLanguage.ContainsKey(id))
            .ToList();

        var sourceDocuments = await context.DocFiles
            .AsNoTracking()
            .Where(document => !document.IsDeleted
                               && document.SourceFiles != null
                               && (
                                   (publishedLanguageIds.Contains(document.BranchLanguageId)
                                    && currentGenerationIds.Contains(document.GenerationId!))
                                   || (unpublishedLanguageIds.Contains(document.BranchLanguageId)
                                       && (document.GenerationId == null || document.GenerationId == string.Empty))))
            .Select(document => new
            {
                document.Id,
                document.BranchLanguageId,
                document.GenerationId,
                document.SourceFiles
            })
            .ToListAsync(cancellationToken);

        // 二次校验语言-世代配对（防御性；SQL 已按 generation id 集合收窄）
        var visibleDocuments = sourceDocuments
            .Where(document => currentGenerationByLanguage.TryGetValue(document.BranchLanguageId, out var generationId)
                ? string.Equals(document.GenerationId, generationId, StringComparison.Ordinal)
                : string.IsNullOrEmpty(document.GenerationId))
            .ToArray();
        if (visibleDocuments.Length > MaxDependencyPages)
        {
            throw new DependencyLoadException(IncrementalImpactReasonCodes.DependencyScanBudgetExceeded);
        }

        var documentIds = visibleDocuments.Select(document => document.Id).ToArray();
        var catalogRows = documentIds.Length == 0
            ? []
            : await context.DocCatalogs
                .AsNoTracking()
                .Where(catalog => !catalog.IsDeleted
                                  && catalog.DocFileId != null
                                  && documentIds.Contains(catalog.DocFileId)
                                  && (
                                      (publishedLanguageIds.Contains(catalog.BranchLanguageId)
                                       && currentGenerationIds.Contains(catalog.GenerationId!))
                                      || (unpublishedLanguageIds.Contains(catalog.BranchLanguageId)
                                          && (catalog.GenerationId == null || catalog.GenerationId == string.Empty))))
                .Select(catalog => new
                {
                    catalog.Id,
                    catalog.DocFileId,
                    catalog.Path,
                    catalog.BranchLanguageId,
                    catalog.GenerationId
                })
                .ToListAsync(cancellationToken);
        var catalogByDocument = catalogRows
            .Where(catalog => currentGenerationByLanguage.TryGetValue(catalog.BranchLanguageId, out var generationId)
                ? string.Equals(catalog.GenerationId, generationId, StringComparison.Ordinal)
                : string.IsNullOrEmpty(catalog.GenerationId))
            .GroupBy(catalog => catalog.DocFileId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var dependencies = new List<IncrementalPageDependency>();
        foreach (var document in visibleDocuments)
        {
            if (!catalogByDocument.TryGetValue(document.Id, out var catalog))
            {
                // 孤儿 DocFile 不应把整分支强制 Full；跳过并记 warning，由变更路径侧的
                // MissingDocumentDependency / MissingContextDependency 做 fail-closed。
                logger.LogWarning(
                    "Skipping DocFile without resolvable catalog while loading incremental dependencies. DocFileId: {DocFileId}, BranchLanguageId: {BranchLanguageId}, GenerationId: {GenerationId}",
                    document.Id,
                    document.BranchLanguageId,
                    document.GenerationId);
                continue;
            }

            string[]? sourcePaths;
            try
            {
                sourcePaths = JsonSerializer.Deserialize<string[]>(document.SourceFiles!);
            }
            catch (JsonException ex)
            {
                throw new DependencyLoadException(IncrementalImpactReasonCodes.DependencyReadFailed, ex);
            }

            if (sourcePaths is null)
            {
                throw new DependencyLoadException(IncrementalImpactReasonCodes.DependencyReadFailed);
            }

            foreach (var sourcePath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                dependencies.Add(new IncrementalPageDependency(
                    NormalizeSourceId(sourcePath),
                    catalog.Id,
                    catalog.Path,
                    document.BranchLanguageId));
            }
        }

        return dependencies;
    }

    private static PathClassification ClassifyPath(
        IRepositoryFileSelectionPolicy policy,
        string? path,
        string? fileType)
    {
        if (path is null)
        {
            return new PathClassification(IncrementalChangePathRole.Excluded, null);
        }

        var metadata = new SourceFileMetadata
        {
            FileType = fileType,
            IsTracked = true
        };
        var document = policy.EvaluateDocumentCandidate(path, metadata);
        if (document.Accepted)
        {
            return new PathClassification(IncrementalChangePathRole.Document, document.MatchedScopeId);
        }

        var context = policy.EvaluateContextRead(path, metadata);
        if (context.Accepted)
        {
            return new PathClassification(IncrementalChangePathRole.Context, context.MatchedScopeId);
        }

        var trigger = policy.EvaluateChangeTrigger(path, metadata);
        return trigger.Accepted
            ? new PathClassification(IncrementalChangePathRole.TriggerOnly, trigger.MatchedScopeId)
            : new PathClassification(IncrementalChangePathRole.Excluded, null);
    }

    private static IReadOnlyList<IncrementalPageDependency> ResolveDependencies(
        IReadOnlyDictionary<string, IncrementalPageDependency[]> dependencyMap,
        StringComparer comparer,
        params string?[] paths)
    {
        var result = new Dictionary<string, IncrementalPageDependency>(StringComparer.Ordinal);
        foreach (var path in paths.Where(path => path is not null).Cast<string>())
        {
            var normalized = NormalizeSourceId(path);
            if (!dependencyMap.TryGetValue(normalized, out var dependencies))
            {
                // Dictionary already uses the requested comparer; this explicit comparer guard
                // documents that path identity follows repository case policy.
                var pair = dependencyMap.FirstOrDefault(item => comparer.Equals(item.Key, normalized));
                dependencies = pair.Value;
            }

            if (dependencies is null)
            {
                continue;
            }

            foreach (var dependency in dependencies)
            {
                result.TryAdd(PageKey(dependency), dependency);
            }
        }

        return result.Values.ToArray();
    }

    private static IReadOnlyList<string> CollectChangedPaths(
        IEnumerable<IncrementalSourceChange> changes,
        bool caseSensitive)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return changes
            .SelectMany(change => new[] { change.OldPath, change.NewPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeSourceId(path!))
            .Distinct(comparer)
            .Order(comparer)
            .ToArray();
    }

    private static IReadOnlyList<string> CollectUeFactIds(UeKnowledgeSemanticDiffResult diff)
    {
        var facts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in diff.AddedClassIds.Concat(diff.RemovedClassIds).Concat(diff.ChangedClassIds))
        {
            facts.Add($"class:{id}");
        }

        foreach (var tag in diff.AddedTags.Concat(diff.RemovedTags))
        {
            facts.Add($"gameplay-tag:{tag}");
        }

        foreach (var tool in diff.AddedToolNames.Concat(diff.RemovedToolNames).Concat(diff.ChangedToolNames))
        {
            facts.Add($"mcp-tool:{tool}");
        }

        foreach (var kind in diff.ChangedKinds)
        {
            facts.Add($"shard:{kind}");
        }

        return facts.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddDomainHint(HashSet<string> domains, string? scopeId, string? path)
    {
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            domains.Add(scopeId);
        }

        var module = TryExtractModuleName(path);
        if (!string.IsNullOrWhiteSpace(module))
        {
            domains.Add(module);
        }
    }

    private static string? TryExtractModuleName(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("Source", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    private static bool IsStructuralPath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".Build.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".Target.cs", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("Module.cpp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith("Module.h", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMove(string action, string? oldPath, string? newPath)
        => action.Contains("move", StringComparison.OrdinalIgnoreCase)
           || (oldPath is not null && newPath is not null && !string.Equals(oldPath, newPath, StringComparison.Ordinal));

    private static bool IsAdd(string action)
        => action.Equals("add", StringComparison.OrdinalIgnoreCase)
           || action.Equals("branch", StringComparison.OrdinalIgnoreCase)
           || action.Equals("import", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeNullable(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : NormalizeSourceId(path);

    private static string NormalizeSourceId(string path)
        => ScopePathUtility.NormalizeRelativePath(path);

    private static string PageKey(IncrementalPageDependency dependency)
        => $"{dependency.BranchLanguageId}|{dependency.PageId}";

    private sealed record PathClassification(IncrementalChangePathRole Role, string? ScopeId);

    private sealed class DependencyLoadException : Exception
    {
        public DependencyLoadException(string reasonCode, Exception? innerException = null)
            : base(reasonCode, innerException)
        {
            ReasonCode = reasonCode;
        }

        public string ReasonCode { get; }
    }
}
