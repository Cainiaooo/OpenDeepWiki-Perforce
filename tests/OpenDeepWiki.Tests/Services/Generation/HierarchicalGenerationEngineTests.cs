using OpenDeepWiki.Services.Generation;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Generation;

public class HierarchicalGenerationEngineTests
{
    [Fact]
    public async Task GenerateAsync_PlansStableDomainsTopicsAndCoverage()
    {
        var root = SourceInventoryBuilderTests.CreateSampleWorkspace();
        try
        {
            var request = SourceInventoryBuilderTests.CreateRequest(root);
            var engine = CreateEngine();

            var first = await engine.GenerateAsync(request);
            var second = await engine.GenerateAsync(request);

            Assert.Equal(GenerationEngineIds.Hierarchical, first.EngineId);
            Assert.True(
                first.Completeness is GenerationCompletenessStatus.Planned
                    or GenerationCompletenessStatus.CompletedWithCoverageWarnings,
                $"Unexpected completeness: {first.Completeness}");
            Assert.NotNull(first.Inventory);
            Assert.NotEmpty(first.Domains);
            Assert.NotEmpty(first.ScopeManifests);
            Assert.NotNull(first.Catalog);
            Assert.NotNull(first.Coverage);
            Assert.Equal(0, first.Coverage.Items.Count(item =>
                item.SubjectKind == "Module" && item.Status == CoverageItemStatus.Unassigned));

            // 稳定 domain/page ID
            Assert.Equal(
                first.Domains.Select(domain => domain.DomainId),
                second.Domains.Select(domain => domain.DomainId));
            Assert.Equal(
                first.ScopeManifests.Select(manifest => manifest.PageId),
                second.ScopeManifests.Select(manifest => manifest.PageId));
            Assert.Equal(first.Inventory!.ContentHash, second.Inventory!.ContentHash);

            // 每页必须有 Document 入口
            Assert.All(first.ScopeManifests, manifest =>
            {
                Assert.NotEmpty(manifest.EntryFiles);
                Assert.Contains(EvidenceKind.Document, manifest.RequiredEvidenceKinds);
            });

            // 覆盖审计能定位模块与文件
            Assert.Contains(first.Coverage!.Items, item => item.SubjectKind == "Module");
            Assert.Contains(first.Coverage.Items, item => item.SubjectKind == "File");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public async Task DomainPlanner_SplitsOversizedDomain_ByBudget()
    {
        var root = SourceInventoryBuilderTests.CreateSampleWorkspace();
        try
        {
            // 制造超过 budget 的文件
            for (var i = 0; i < 30; i++)
            {
                var dir = Path.Combine(
                    root,
                    "SampleProject",
                    "Source",
                    "SampleGameplay",
                    "Private",
                    $"Feature{i:D2}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"Feature{i:D2}.cpp"), $"// {i}");
                File.WriteAllText(Path.Combine(dir, $"Feature{i:D2}.h"), $"// {i}");
            }

            var request = SourceInventoryBuilderTests.CreateRequest(root);
            request = new GenerationRequest
            {
                Snapshot = request.Snapshot,
                ResolvedScopes = request.ResolvedScopes,
                Policy = new GenerationPolicy
                {
                    DomainFileBudget = 8,
                    MaxPlanningDepth = 3,
                    GenerateLeafContent = false
                },
                WorkingDirectory = request.WorkingDirectory,
                RepositoryId = request.RepositoryId,
                BranchId = request.BranchId,
                BranchLanguageId = request.BranchLanguageId,
                LanguageCode = request.LanguageCode
            };

            var engine = CreateEngine();
            var artifacts = await engine.GenerateAsync(request);

            Assert.True(artifacts.Domains.Count >= 2);
            Assert.Contains(artifacts.Domains, domain =>
                domain.DomainId.Contains("samplegameplay", StringComparison.OrdinalIgnoreCase)
                || domain.DisplayName.Contains("SampleGameplay", StringComparison.OrdinalIgnoreCase)
                || domain.IncludedRoots.Any(rootPath =>
                    rootPath.Contains("SampleGameplay", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    [Fact]
    public async Task Registry_ResolvesEngines_AndRejectsUnknown()
    {
        var inventoryBuilder = new SourceInventoryBuilder();
        var planner = new DomainTopicPlanner();
        var merger = new CatalogMerger();
        var auditor = new CoverageAuditor();
        var hierarchical = new HierarchicalGenerationEngine(inventoryBuilder, planner, merger, auditor);
        var legacy = new LegacyGenerationEngine(new NoopWikiGenerator(), inventoryBuilder, auditor);
        var registry = new GenerationEngineRegistry([legacy, hierarchical], GenerationEngineIds.Legacy);

        Assert.Equal(GenerationEngineIds.Legacy, registry.GetDefault().Capabilities.EngineId);
        Assert.Equal(
            GenerationEngineIds.Hierarchical,
            registry.GetRequired(GenerationEngineIds.Hierarchical).Capabilities.EngineId);
        Assert.Equal(2, registry.ListCapabilities().Count);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("does-not-exist"));
    }

    [Fact]
    public async Task LegacyEngine_PropagatesCancellation()
    {
        var root = SourceInventoryBuilderTests.CreateSampleWorkspace();
        try
        {
            var request = SourceInventoryBuilderTests.CreateRequest(root);
            var engine = new LegacyGenerationEngine(
                new NoopWikiGenerator(),
                new SourceInventoryBuilder(),
                new CoverageAuditor());
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.GenerateAsync(request, cts.Token));
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static HierarchicalGenerationEngine CreateEngine()
        => new(
            new SourceInventoryBuilder(),
            new DomainTopicPlanner(),
            new CatalogMerger(),
            new CoverageAuditor());

    private sealed class NoopWikiGenerator : OpenDeepWiki.Services.Wiki.IWikiGenerator
    {
        public Task GenerateMindMapAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage branchLanguage,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task GenerateCatalogAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage branchLanguage,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task GenerateDocumentsAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage branchLanguage,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RegenerateDocumentAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage branchLanguage,
            string documentPath,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task IncrementalUpdateAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage branchLanguage,
            string[] changedFiles,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<OpenDeepWiki.Entities.BranchLanguage> TranslateWikiAsync(
            OpenDeepWiki.Services.Repositories.RepositoryWorkspace workspace,
            OpenDeepWiki.Entities.BranchLanguage sourceBranchLanguage,
            string targetLanguageCode,
            CancellationToken cancellationToken = default)
            => Task.FromResult(sourceBranchLanguage);
    }
}
