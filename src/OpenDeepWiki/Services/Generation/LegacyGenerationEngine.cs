using System.Diagnostics;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 将当前 OpenDeepWiki WikiGenerator 包装为兼容基线引擎。
/// 不在 WikiGenerator 内部做渐进改造；分层能力由 Hierarchical 引擎提供。
/// </summary>
public sealed class LegacyGenerationEngine(
    IWikiGenerator wikiGenerator,
    ISourceInventoryBuilder inventoryBuilder,
    ICoverageAuditor coverageAuditor) : IGenerationEngine
{
    // 保留对 IWikiGenerator 的依赖，明确 Legacy 适配边界；完整目录/正文仍由控制面调用 WikiGenerator。
    private readonly IWikiGenerator _wikiGenerator = wikiGenerator
        ?? throw new ArgumentNullException(nameof(wikiGenerator));

    public GenerationEngineCapabilities Capabilities { get; } = new()
    {
        EngineId = GenerationEngineIds.Legacy,
        Version = GenerationEngineVersions.Legacy,
        SupportsDomainPlanning = false,
        SupportsTopicPlanning = false,
        SupportsLeafGeneration = true,
        SupportsCoverageAudit = true,
        SupportsIncrementalRemap = false,
        SupportsUeInventory = true,
        MaxContextTokens = null
    };

    public Task<SourceInventory> BuildInventoryAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(inventoryBuilder.Build(request));
    }

    public Task<IReadOnlyList<PlannedDomain>> PlanDomainsAsync(
        GenerationRequest request,
        SourceInventory inventory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Legacy 不拆领域；输出单一兼容域，便于统一评测格式。
        IReadOnlyList<PlannedDomain> domains =
        [
            new PlannedDomain
            {
                DomainId = "domain-legacy-all",
                ScopeId = "legacy",
                DisplayName = "Legacy Full Repository",
                IncludedRoots = [string.Empty],
                Depth = 0,
                FileCount = inventory.TotalDocumentFiles,
                ModuleIds = inventory.Modules.Select(module => module.ModuleId).ToArray()
            }
        ];
        return Task.FromResult(domains);
    }

    public Task<IReadOnlyList<ScopeManifest>> PlanTopicsAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Legacy 由 WikiGenerator 自行规划目录；此处不伪造叶子 Manifest。
        IReadOnlyList<ScopeManifest> manifests = [];
        return Task.FromResult(manifests);
    }

    public Task<IReadOnlyList<GeneratedLeafPage>> GenerateLeavesAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<ScopeManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 叶子生成仍走现有 IWikiGenerator 管线（由控制面调用），此处不重复执行。
        IReadOnlyList<GeneratedLeafPage> leaves = [];
        return Task.FromResult(leaves);
    }

    public Task<MergedCatalogArtifact> MergeCatalogAsync(
        GenerationRequest request,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MergedCatalogArtifact
        {
            Roots = [],
            ValidationWarnings = ["Legacy engine catalog is owned by WikiGenerator/CatalogStorage."]
        });
    }

    public Task<CoverageAuditReport> AuditCoverageAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(coverageAuditor.Audit(
            request.Snapshot.ToStableString(),
            inventory,
            domains,
            manifests,
            leaves));
    }

    public async Task<GenerationArtifactSet> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var summary = new GenerationExecutionSummary
        {
            PromptVersion = request.Policy.PromptVersion,
            TokenBudget = request.Policy.TokenBudget,
            Model = "legacy-wiki-generator"
        };

        try
        {
            var inventory = request.SourceInventory ?? await BuildInventoryAsync(request, cancellationToken);
            var domains = await PlanDomainsAsync(request, inventory, cancellationToken);
            var manifests = await PlanTopicsAsync(request, inventory, domains, cancellationToken);
            var leaves = await GenerateLeavesAsync(request, inventory, manifests, cancellationToken);
            var catalog = await MergeCatalogAsync(request, domains, manifests, leaves, cancellationToken);
            var coverage = await AuditCoverageAsync(request, inventory, domains, manifests, leaves, cancellationToken);

            // Legacy 完整生成仍由 RepositoryBranchProcessor → IWikiGenerator 驱动。
            // 本适配器提供统一 GenerationRequest/Artifact 评测入口与 Inventory/Coverage。
            summary.Warnings.Add(
                $"LegacyGenerationEngine emits inventory/coverage artifacts; catalog/doc generation remains on { _wikiGenerator.GetType().Name }.");
            summary.DurationMs = stopwatch.ElapsedMilliseconds;

            return new GenerationArtifactSet
            {
                EngineId = Capabilities.EngineId,
                EngineVersion = Capabilities.Version,
                SnapshotIdentity = request.Snapshot.ToStableString(),
                LanguageCode = request.LanguageCode,
                Inventory = inventory,
                Domains = domains,
                ScopeManifests = manifests,
                Leaves = leaves,
                Catalog = catalog,
                Coverage = coverage,
                Summary = summary,
                Completeness = coverage.HasBlockingErrors
                    ? GenerationCompletenessStatus.CompletedWithCoverageWarnings
                    : GenerationCompletenessStatus.InventoryOnly
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.Errors.Add(ex.Message);
            summary.DurationMs = stopwatch.ElapsedMilliseconds;
            return new GenerationArtifactSet
            {
                EngineId = Capabilities.EngineId,
                EngineVersion = Capabilities.Version,
                SnapshotIdentity = request.Snapshot.ToStableString(),
                LanguageCode = request.LanguageCode,
                Summary = summary,
                Completeness = GenerationCompletenessStatus.Failed
            };
        }
    }
}
