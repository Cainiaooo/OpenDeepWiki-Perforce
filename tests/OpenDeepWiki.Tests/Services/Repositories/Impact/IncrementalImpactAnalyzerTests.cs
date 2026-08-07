using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Impact;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.UeKnowledge;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Impact;

public class IncrementalImpactAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_LegacyScopePreservesIncrementalCompatibility()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(new IncrementalImpactRequest
        {
            RepositoryId = "repo",
            BranchId = "branch",
            Changes = [new IncrementalSourceChange("edit", null, "Source/Game/Foo.cpp")],
            SelectionPolicy = null
        });

        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.LegacyIncrementalFallback, plan.ReasonCodes);
        Assert.False(plan.RequiresFullGeneration);
    }

    [Fact]
    public async Task AnalyzeAsync_RecordedDocumentSourceTargetsPublishedLeaf()
    {
        await using var context = CreateContext();
        await SeedPublishedPageAsync(context, "Source/Game/Foo.cpp", sourceFilesJson: null);
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("edit", null, "Source/Game/Foo.cpp")));

        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.RecordedPageSourceChanged, plan.ReasonCodes);
        Assert.Equal(["page-1"], plan.AffectedPageIds);
        Assert.Equal(["game/foo"], plan.AffectedPagePaths);
        Assert.False(plan.IsFailClosed);
    }

    [Fact]
    public async Task AnalyzeAsync_NewDocumentFileRequestsInventoryAndRecordsExecutionExpansion()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("add", null, "Source/Game/NewFeature.cpp")));

        Assert.Equal(IncrementalImpactLevel.DomainInventory, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.True(plan.WasExpandedForExecution);
        Assert.Contains(IncrementalImpactReasonCodes.NewDocumentFile, plan.ReasonCodes);
        Assert.Contains(IncrementalImpactReasonCodes.PartialGenerationUnavailable, plan.ReasonCodes);
        Assert.Contains("Game", plan.AffectedDomainHints);
    }

    [Theory]
    [InlineData("Source/Game/Game.Build.cs")]
    [InlineData("Plugins/Demo/Demo.uplugin")]
    [InlineData("Source/Game/GameModule.cpp")]
    public async Task AnalyzeAsync_StructuralFileRequestsDomainReplan(string path)
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("edit", null, path)));

        Assert.Equal(IncrementalImpactLevel.DomainReplan, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.StructuralFileChanged, plan.ReasonCodes);
        Assert.Contains(IncrementalImpactReasonCodes.PartialGenerationUnavailable, plan.ReasonCodes);
    }

    [Fact]
    public async Task AnalyzeAsync_ContextChangeTargetsOnlyRecordedConsumer()
    {
        await using var context = CreateContext();
        await SeedPublishedPageAsync(context, "Shared/Contracts/Common.ini", sourceFilesJson: null);
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("edit", null, "Shared/Contracts/Common.ini")));

        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.ContextDependencyChanged, plan.ReasonCodes);
        Assert.Equal(["page-1"], plan.AffectedPageIds);
    }

    [Fact]
    public async Task AnalyzeAsync_ContextChangeWithoutDependencyFailsClosed()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("edit", null, "Shared/Contracts/Common.ini")));

        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.MissingContextDependency, plan.ReasonCodes);
        Assert.True(plan.IsFailClosed);
    }

    [Fact]
    public async Task AnalyzeAsync_DocumentMoveRequestsInventoryRefresh()
    {
        await using var context = CreateContext();
        await SeedPublishedPageAsync(context, "Source/Game/Old.cpp", sourceFilesJson: null);
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange(
                "move",
                "Source/Game/Old.cpp",
                "Source/Game/New.cpp")));

        Assert.Equal(IncrementalImpactLevel.DomainInventory, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.DocumentMoveChangedStructure, plan.ReasonCodes);
        Assert.Equal(["page-1"], plan.AffectedPageIds);
    }

    [Fact]
    public async Task AnalyzeAsync_MalformedPublishedDependencyFailsClosed()
    {
        await using var context = CreateContext();
        await SeedPublishedPageAsync(context, "unused", "{not-json");
        var analyzer = CreateAnalyzer(context);

        var plan = await analyzer.AnalyzeAsync(Request(
            new IncrementalSourceChange("edit", null, "Source/Game/Foo.cpp")));

        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.DependencyReadFailed, plan.ReasonCodes);
        Assert.True(plan.IsFailClosed);
    }

    [Fact]
    public async Task AnalyzeAsync_ScopeConfigurationChangeAlwaysRebuildsFullPlan()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        var request = Request(new IncrementalSourceChange("edit", null, "Source/Game/Foo.cpp"));

        var plan = await analyzer.AnalyzeAsync(new IncrementalImpactRequest
        {
            RepositoryId = request.RepositoryId,
            BranchId = request.BranchId,
            Changes = request.Changes,
            SelectionPolicy = request.SelectionPolicy,
            ScopeConfigurationChanged = true
        });

        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.ScopeConfigurationChanged, plan.ReasonCodes);
    }

    [Fact]
    public async Task AnalyzeUeKnowledge_RecordedFactTargetsLeafPage()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);
        var diff = CreateUeDiff();

        var plan = analyzer.AnalyzeUeKnowledge(new UeKnowledgeImpactRequest
        {
            SemanticDiff = diff,
            RecordedDependencies =
            [
                new IncrementalPageDependency(
                    "class:/Script/Game.Hero",
                    "page-hero",
                    "game/hero",
                    "lang-zh")
            ]
        });

        Assert.Equal(IncrementalImpactLevel.LeafPages, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.UeFactDependencyChanged, plan.ReasonCodes);
        Assert.Equal(["page-hero"], plan.AffectedPageIds);
    }

    [Fact]
    public async Task AnalyzeUeKnowledge_MissingFactDependencyUsesDomainHintThenExpandsExecution()
    {
        await using var context = CreateContext();
        var analyzer = CreateAnalyzer(context);

        var plan = analyzer.AnalyzeUeKnowledge(new UeKnowledgeImpactRequest
        {
            SemanticDiff = CreateUeDiff()
        });

        Assert.Equal(IncrementalImpactLevel.DomainReplan, plan.RequestedLevel);
        Assert.Equal(IncrementalImpactLevel.FullInventoryAndPlanning, plan.ExecutionLevel);
        Assert.Contains(IncrementalImpactReasonCodes.MissingUeFactDependency, plan.ReasonCodes);
        Assert.Contains(IncrementalImpactReasonCodes.PartialGenerationUnavailable, plan.ReasonCodes);
        Assert.True(plan.IsFailClosed);
    }

    [Fact]
    public void ImpactPlanSerializer_RoundTripsStableEnumsAndReasons()
    {
        var original = new IncrementalImpactPlan
        {
            RequestedLevel = IncrementalImpactLevel.DomainReplan,
            ExecutionLevel = IncrementalImpactLevel.FullInventoryAndPlanning,
            ReasonCodes =
            [
                IncrementalImpactReasonCodes.StructuralFileChanged,
                IncrementalImpactReasonCodes.PartialGenerationUnavailable
            ],
            ChangedPaths = ["Source/Game/Game.Build.cs"],
            AffectedDomainHints = ["Game"]
        };

        var json = IncrementalImpactPlanSerializer.Serialize(original);
        var restored = Assert.IsType<IncrementalImpactPlan>(
            IncrementalImpactPlanSerializer.Deserialize(json));

        Assert.Equal(original.RequestedLevel, restored.RequestedLevel);
        Assert.Equal(original.ExecutionLevel, restored.ExecutionLevel);
        Assert.Equal(original.ReasonCodes, restored.ReasonCodes);
        Assert.Null(IncrementalImpactPlanSerializer.Deserialize("{invalid"));
    }

    private static IncrementalImpactRequest Request(params IncrementalSourceChange[] changes)
        => new()
        {
            RepositoryId = "repo",
            BranchId = "branch",
            Changes = changes,
            SelectionPolicy = new RepositoryFileSelectionPolicyFactory().Create(CreateScopeConfiguration())
        };

    private static ResolvedScopeConfiguration CreateScopeConfiguration()
        => new()
        {
            SchemaVersion = 1,
            WorkspaceContentPolicy = WorkspaceContentPolicy.SubmittedHaveOnly,
            DocumentScopes =
            [
                new ResolvedDocumentScope
                {
                    Id = "source",
                    Root = "Source",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = [],
                    IncludedSuffixes = ScopeDefaults.DefaultDocumentSuffixes,
                    AcceptAllTextFiles = false
                },
                new ResolvedDocumentScope
                {
                    Id = "plugins",
                    Root = "Plugins",
                    IncludedPathGlobs = ["**"],
                    ExcludedPathGlobs = [],
                    IncludedSuffixes = ScopeDefaults.DefaultDocumentSuffixes,
                    AcceptAllTextFiles = false
                }
            ],
            ContextScope = new ResolvedContextScope
            {
                Roots = ["Shared"],
                ReadOnly = true,
                PreferTrackedFiles = true,
                MaxFileBytes = 1024 * 1024,
                IncludedPathGlobs = ["**"],
                ExcludedPathGlobs = []
            },
            ChangeTriggerScope = new ResolvedChangeTriggerScope
            {
                InheritsDocumentScopes = true,
                AdditionalRoots = ["Shared"],
                IncludedPathGlobs = ["**"],
                ExcludedPathGlobs = []
            },
            ContentHash = "scope-hash",
            NormalizedJson = "{}"
        };

    private static UeKnowledgeSemanticDiffResult CreateUeDiff()
        => new()
        {
            FromSemanticDigest = "old",
            ToSemanticDigest = "new",
            ChangedKinds = [UeKnowledgeSchema.ShardKinds.Reflection],
            ChangedClassIds = ["/Script/Game.Hero"],
            AffectedDomainHints = ["Game"]
        };

    private static async Task SeedPublishedPageAsync(
        TestDbContext context,
        string sourcePath,
        string? sourceFilesJson)
    {
        const string languageId = "lang-zh";
        const string generationId = "generation-current";
        var document = new DocFile
        {
            Id = "doc-1",
            BranchLanguageId = languageId,
            GenerationId = generationId,
            Content = "content",
            SourceFiles = sourceFilesJson ?? JsonSerializer.Serialize(new[] { sourcePath })
        };
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = languageId,
            RepositoryBranchId = "branch",
            LanguageCode = "zh"
        });
        context.BranchLanguagePublications.Add(new BranchLanguagePublication
        {
            Id = "publication-1",
            BranchLanguageId = languageId,
            CurrentGenerationId = generationId
        });
        context.DocFiles.Add(document);
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = "page-1",
            BranchLanguageId = languageId,
            GenerationId = generationId,
            Path = "game/foo",
            Title = "Foo",
            DocFileId = document.Id
        });
        await context.SaveChangesAsync();
    }

    private static IncrementalImpactAnalyzer CreateAnalyzer(TestDbContext context)
        => new(context, NullLogger<IncrementalImpactAnalyzer>.Instance);

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);
}
