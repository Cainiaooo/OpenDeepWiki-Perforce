using System.Diagnostics;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 分层生成引擎：Inventory → Domain/Topic 规划 →（可选叶子）→ Catalog 合并 → 覆盖审计。
/// 首期叶子正文生成默认关闭，产出稳定规划与审计报告供对照实验与后续接入。
/// </summary>
public sealed class HierarchicalGenerationEngine(
    ISourceInventoryBuilder inventoryBuilder,
    IDomainTopicPlanner domainTopicPlanner,
    ICatalogMerger catalogMerger,
    ICoverageAuditor coverageAuditor) : IGenerationEngine
{
    public GenerationEngineCapabilities Capabilities { get; } = new()
    {
        EngineId = GenerationEngineIds.Hierarchical,
        Version = GenerationEngineVersions.Hierarchical,
        SupportsDomainPlanning = true,
        SupportsTopicPlanning = true,
        SupportsLeafGeneration = false,
        SupportsCoverageAudit = true,
        SupportsIncrementalRemap = true,
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
        return Task.FromResult(domainTopicPlanner.PlanDomains(inventory, request.Policy));
    }

    public Task<IReadOnlyList<ScopeManifest>> PlanTopicsAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<PlannedDomain> domains,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(domainTopicPlanner.PlanTopics(inventory, domains, request.Policy));
    }

    public Task<IReadOnlyList<GeneratedLeafPage>> GenerateLeavesAsync(
        GenerationRequest request,
        SourceInventory inventory,
        IReadOnlyList<ScopeManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.Policy.GenerateLeafContent)
        {
            IReadOnlyList<GeneratedLeafPage> pending = manifests
                .Select(manifest => new GeneratedLeafPage
                {
                    PageId = manifest.PageId,
                    DomainId = manifest.DomainId,
                    Title = manifest.TopicSummary,
                    Status = LeafPageStatus.Skipped,
                    Sources = manifest.EntryFiles.Select(path => new StructuredSourceRecord
                    {
                        Path = path,
                        ScopeKind = "Document",
                        ScopeId = manifest.ScopeId,
                        ReadPurpose = "entry",
                        EvidenceKind = EvidenceKind.Document,
                        Digest = inventory.Files
                            .FirstOrDefault(file => string.Equals(
                                file.RelativePath,
                                path,
                                StringComparison.OrdinalIgnoreCase))
                            ?.ContentDigest,
                        HaveRevision = inventory.Files
                            .FirstOrDefault(file => string.Equals(
                                file.RelativePath,
                                path,
                                StringComparison.OrdinalIgnoreCase))
                            ?.HaveRevision
                    }).ToArray()
                })
                .ToArray();
            return Task.FromResult(pending);
        }

        // 叶子 Agent 生成将在后续提交接入；首期显式失败而非伪成功占位正文。
        IReadOnlyList<GeneratedLeafPage> failed = manifests
            .Select(manifest => new GeneratedLeafPage
            {
                PageId = manifest.PageId,
                DomainId = manifest.DomainId,
                Title = manifest.TopicSummary,
                Status = LeafPageStatus.Failed,
                ErrorMessage = "Hierarchical leaf content generation is not implemented yet."
            })
            .ToArray();
        return Task.FromResult(failed);
    }

    public Task<MergedCatalogArtifact> MergeCatalogAsync(
        GenerationRequest request,
        IReadOnlyList<PlannedDomain> domains,
        IReadOnlyList<ScopeManifest> manifests,
        IReadOnlyList<GeneratedLeafPage> leaves,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(catalogMerger.Merge(domains, manifests, leaves));
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
            Model = "deterministic-planner"
        };

        try
        {
            var inventory = request.SourceInventory ?? await BuildInventoryAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var domains = await PlanDomainsAsync(request, inventory, cancellationToken);
            var manifests = await PlanTopicsAsync(request, inventory, domains, cancellationToken);
            var leaves = await GenerateLeavesAsync(request, inventory, manifests, cancellationToken);
            var catalog = await MergeCatalogAsync(request, domains, manifests, leaves, cancellationToken);
            var coverage = await AuditCoverageAsync(request, inventory, domains, manifests, leaves, cancellationToken);

            if (catalog.ValidationErrors.Count > 0)
            {
                summary.Errors.AddRange(catalog.ValidationErrors);
            }

            if (catalog.ValidationWarnings.Count > 0)
            {
                summary.Warnings.AddRange(catalog.ValidationWarnings);
            }

            if (coverage.UnassignedCount > 0)
            {
                summary.Warnings.Add($"Coverage unassigned subjects: {coverage.UnassignedCount}");
            }

            if (coverage.BudgetTruncatedCount > 0)
            {
                summary.Warnings.Add($"Budget truncated subjects: {coverage.BudgetTruncatedCount}");
            }

            summary.DurationMs = stopwatch.ElapsedMilliseconds;

            var completeness = DetermineCompleteness(request, coverage, leaves, catalog);
            if (request.Policy.BlockOnCoverageErrors && coverage.HasBlockingErrors)
            {
                summary.Errors.AddRange(coverage.BlockingReasons);
                completeness = GenerationCompletenessStatus.Failed;
            }

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
                Completeness = completeness
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

    private static GenerationCompletenessStatus DetermineCompleteness(
        GenerationRequest request,
        CoverageAuditReport coverage,
        IReadOnlyList<GeneratedLeafPage> leaves,
        MergedCatalogArtifact catalog)
    {
        if (catalog.ValidationErrors.Count > 0)
        {
            return GenerationCompletenessStatus.Failed;
        }

        if (request.Policy.GenerateLeafContent
            && leaves.Any(leaf => leaf.Status == LeafPageStatus.Failed))
        {
            return GenerationCompletenessStatus.Failed;
        }

        if (coverage.HasBlockingErrors
            || coverage.BudgetTruncatedCount > 0
            || coverage.UnassignedCount > 0
            || coverage.DuplicateAssignmentCount > 0)
        {
            return GenerationCompletenessStatus.CompletedWithCoverageWarnings;
        }

        return request.Policy.GenerateLeafContent
            ? GenerationCompletenessStatus.Completed
            : GenerationCompletenessStatus.Planned;
    }
}
