namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 生成引擎边界：PlanDomains → PlanTopics → GenerateLeaves → MergeCatalog → AuditCoverage。
/// 引擎只输出 staging 产物，无权切换当前发布版本。
/// </summary>
public interface IGenerationEngine
{
    GenerationEngineCapabilities Capabilities { get; }

    /// <summary>
    /// 完整管线（具体阶段由引擎能力决定）。产物可序列化、可追踪。
    /// </summary>
    Task<GenerationArtifactSet> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default);

    Task<SourceInventory> BuildInventoryAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlannedDomain>> PlanDomainsAsync(
        GenerationRequest request,
        SourceInventory inventory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScopeManifest>> PlanTopicsAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeneratedLeafPage>> GenerateLeavesAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<ScopeManifest> manifests,
        CancellationToken cancellationToken = default);

    Task<MergedCatalogArtifact> MergeCatalogAsync(
        GenerationRequest request,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves,
        CancellationToken cancellationToken = default);

    Task<CoverageAuditReport> AuditCoverageAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 按 EngineId 解析生成引擎。同一任务禁止混用两个引擎生成不可区分的页面。
/// </summary>
public interface IGenerationEngineRegistry
{
    IGenerationEngine GetRequired(string engineId);

    IGenerationEngine GetDefault();

    IReadOnlyList<GenerationEngineCapabilities> ListCapabilities();
}

public static class GenerationEngineIds
{
    public const string Legacy = "opendeepwiki-legacy";
    public const string Hierarchical = "opendeepwiki-hierarchical";
}

public static class GenerationEngineVersions
{
    public const string Legacy = "opendeepwiki-legacy-1";
    public const string Hierarchical = "opendeepwiki-hierarchical-1";
}
