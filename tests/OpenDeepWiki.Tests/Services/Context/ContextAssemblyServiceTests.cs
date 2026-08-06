using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Context;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.UeKnowledge;
using OpenDeepWiki.Tests.Chat.Config;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Context;

public class ContextAssemblyServiceTests
{
    [Fact]
    public async Task ChangeReview_MapsSourceFiles_AndFlagsOutOfScope()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        PublishPage(
            context,
            seed,
            title: "Combat Module",
            path: "modules/combat",
            content: "# Combat\n\n## 风险\n修改伤害计算需跑 Automation 测试。\n",
            sourceFiles: ["SampleProject/Source/Combat/Damage.cpp", "SampleProject/Source/Combat/Health.h"],
            generationId: seed.GenerationId);

        var service = CreateService(context);
        var envelope = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TargetChangelist = "1000",
            ChangedFiles =
            [
                "SampleProject/Source/Combat/Damage.cpp",
                "Engine/Source/Runtime/Core/Private/Misc.cpp"
            ],
            LanguageCode = "zh",
            MaxItems = 30,
            MaxChars = 50_000
        });

        Assert.Equal(ContextCompatibility.Exact, envelope.Compatibility);
        Assert.Equal(seed.GenerationId, envelope.ResolvedSnapshotId);
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.WikiPage && i.Path == "modules/combat");
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.Gap);
        Assert.Contains(envelope.Warnings, w => w.ReasonCode == ContextReasonCodes.FileOutsideDocumentScope);
        Assert.Contains(envelope.Warnings, w => w.ReasonCode == ContextReasonCodes.AuthorMetadataUntrusted);
        Assert.NotEmpty(envelope.Citations);
        Assert.All(envelope.Items.Where(i => i.Kind == ContextItemKind.WikiPage), i => Assert.NotEmpty(i.CitationIds));
    }

    [Fact]
    public async Task ChangeReview_RejectedSnapshot_ReturnsEmptyItems()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context, publish: false);
        var service = CreateService(context);

        var envelope = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TargetChangelist = "1000",
            ChangedFiles = ["SampleProject/Source/Combat/Damage.cpp"]
        });

        Assert.Equal(ContextCompatibility.Rejected, envelope.Compatibility);
        Assert.Empty(envelope.Items);
        Assert.Empty(envelope.Citations);
        Assert.Contains(envelope.Warnings, w => w.Severity == "error");
    }

    [Fact]
    public async Task ChangeReview_DeletedFile_StillAttemptsHistoryMapping()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        PublishPage(
            context,
            seed,
            title: "Inventory",
            path: "modules/inventory",
            content: "Inventory system overview",
            sourceFiles: ["SampleProject/Source/Inventory/Bag.cpp"],
            generationId: seed.GenerationId);

        var service = CreateService(context);
        var envelope = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TargetChangelist = "1000",
            DeletedFiles = ["SampleProject/Source/Inventory/Bag.cpp"],
            LanguageCode = "zh"
        });

        Assert.Equal(ContextCompatibility.Exact, envelope.Compatibility);
        Assert.Contains(envelope.Items, i =>
            i.Kind == ContextItemKind.WikiPage
            && i.Attributes != null
            && i.Attributes.TryGetValue("action", out var action)
            && action == "deleted");
    }

    [Fact]
    public async Task EditorTask_Query_IncludesLiveChecklistAndConstraints()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        PublishPage(
            context,
            seed,
            title: "Editor Workflow",
            path: "workflows/editor",
            content: "Spawn actor workflow and naming rules",
            sourceFiles: ["SampleProject/Source/EditorTools/Spawn.cpp"],
            generationId: seed.GenerationId);

        // UE package with MCP tools
        context.UeKnowledgePackages.Add(new UeKnowledgePackage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            IsCurrent = true,
            Status = UeKnowledgePackageStatus.Validated,
            Completeness = UeKnowledgeCompleteness.Complete,
            SchemaVersion = "1.0",
            ExporterVersion = "1.0.0",
            ProjectIdentity = "SampleProject",
            BuildChangelist = "1000",
            PackageDigest = "digest1",
            SemanticDigest = "sem1",
            ManifestJson = "{}",
            FactIndexJson = JsonSerializer.Serialize(new UeKnowledgeFactIndex
            {
                SchemaVersion = "1.0",
                PackageDigest = "digest1",
                SemanticDigest = "sem1",
                ProjectId = "SampleProject",
                BuildChangelist = "1000",
                McpTools =
                [
                    new UeMcpToolContract
                    {
                        ToolName = "ue.spawn_actor",
                        Description = "Spawn an actor in the current level",
                        Preconditions = ["editor world loaded"],
                        SideEffects = ["creates actor"]
                    }
                ],
                EditorActions =
                [
                    new UeEditorActionFact
                    {
                        StableId = "spawn",
                        Name = "Spawn Actor",
                        Description = "Spawn actor workflow",
                        Category = "Editor"
                    }
                ]
            }),
            IngestedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var envelope = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TaskDescription = "spawn actor in level",
            BuildIdentity = new BuildIdentity
            {
                ProjectId = "SampleProject",
                Branch = seed.BranchName,
                BuildChangelist = "1000",
                EngineVersion = "5.4"
            },
            RiskLevel = EditorOperationRiskLevel.Query,
            LanguageCode = "zh"
        });

        Assert.Equal(ContextCompatibility.Exact, envelope.Compatibility);
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.Constraint);
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.LiveQueryRequired);
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.McpToolContract && i.Title == "ue.spawn_actor");
        Assert.Equal("Query", envelope.Metadata?["riskLevel"]);
    }

    [Fact]
    public async Task EditorTask_Write_RejectsNonExactSnapshot()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        var service = CreateService(context);

        var envelope = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TaskDescription = "rename asset",
            BuildIdentity = new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = "1500"
            },
            RiskLevel = EditorOperationRiskLevel.Write
        });

        Assert.Equal(ContextCompatibility.Rejected, envelope.Compatibility);
        Assert.Empty(envelope.Items);
        Assert.Contains(envelope.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedWriteRequiresExact);
    }

    [Fact]
    public async Task EditorTask_CrossBranch_Rejects()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        var service = CreateService(context);

        var envelope = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TaskDescription = "query tags",
            BuildIdentity = new BuildIdentity
            {
                Branch = "release",
                BuildChangelist = "1000"
            },
            RiskLevel = EditorOperationRiskLevel.Query
        });

        Assert.Equal(ContextCompatibility.Rejected, envelope.Compatibility);
        Assert.Empty(envelope.Items);
        Assert.Contains(envelope.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedCrossBranch);
    }

    [Fact]
    public async Task ModuleOverview_ReturnsProgressiveStructure()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        PublishPage(
            context,
            seed,
            title: "Combat Domain",
            path: "domains/combat",
            content: "# Combat\n\n## 职责\n处理伤害与生命。\n\n## 入口\nACombatComponent\n\n## 风险\n并发修改需锁。\n\n## 测试\n跑 CombatAutomation。\n",
            sourceFiles: ["SampleProject/Source/Combat/CombatComponent.cpp"],
            generationId: seed.GenerationId);
        PublishPage(
            context,
            seed,
            title: "Combat UI",
            path: "domains/combat-ui",
            content: "HUD for combat",
            sourceFiles: ["SampleProject/Source/CombatUI/HUD.cpp"],
            generationId: seed.GenerationId);

        var service = CreateService(context);
        var envelope = await service.GetModuleOverviewAsync(new ModuleOverviewRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            ModuleOrDomainQuery = "Combat",
            TargetChangelist = "1000",
            LanguageCode = "zh"
        });

        Assert.Equal(ContextCompatibility.Exact, envelope.Compatibility);
        Assert.Equal(nameof(ContextPersona.Onboarding), envelope.Persona);
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.ModuleOverview && i.Title.StartsWith("1."));
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.ModuleOverview && i.Title.StartsWith("5."));
        Assert.True(envelope.Items.Count(i => i.Kind == ContextItemKind.ModuleOverview) >= 5);
    }

    [Fact]
    public async Task Budget_TruncatesDeterministically_AndDoesNotReportComplete()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        for (var i = 0; i < 10; i++)
        {
            PublishPage(
                context,
                seed,
                title: $"Page {i}",
                path: $"pages/p{i}",
                content: new string('x', 500) + $" Combat {i}",
                sourceFiles: [$"SampleProject/Source/Combat/F{i}.cpp"],
                generationId: seed.GenerationId);
        }

        var service = CreateService(context);
        var first = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TargetChangelist = "1000",
            ChangedFiles = Enumerable.Range(0, 10)
                .Select(i => $"SampleProject/Source/Combat/F{i}.cpp")
                .ToList(),
            MaxItems = 3,
            MaxChars = 50_000
        });
        var second = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TargetChangelist = "1000",
            ChangedFiles = Enumerable.Range(0, 10)
                .Select(i => $"SampleProject/Source/Combat/F{i}.cpp")
                .ToList(),
            MaxItems = 3,
            MaxChars = 50_000
        });

        Assert.True(first.Budget?.Truncated);
        Assert.Equal(3, first.Items.Count);
        Assert.NotEqual(ContextCoverageStatus.Complete, first.CoverageStatus);
        Assert.Equal(
            first.Items.Select(i => i.Id).ToArray(),
            second.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task ChangeReview_PathPrefix_RequiresDirectorySegmentBoundary()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        PublishPage(
            context,
            seed,
            title: "Combat Core",
            path: "modules/combat",
            content: "Combat core",
            sourceFiles: ["SampleProject/Source/Combat/Damage.cpp"],
            generationId: seed.GenerationId);
        PublishPage(
            context,
            seed,
            title: "Combat UI",
            path: "modules/combat-ui",
            content: "Combat UI",
            sourceFiles: ["SampleProject/Source/CombatUI/HUD.cpp"],
            generationId: seed.GenerationId);

        var service = CreateService(context);
        var envelope = await service.GetChangeReviewContextAsync(new ChangeReviewContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TargetChangelist = "1000",
            ChangedFiles = ["SampleProject/Source/Combat/Health.cpp"],
            LanguageCode = "zh",
            MaxItems = 20,
            MaxChars = 50_000
        });

        // 同目录段 Combat/ 可命中；CombatUI/ 不得因 StartsWith(".../Combat") 误命中
        Assert.Contains(envelope.Items, i => i.Kind == ContextItemKind.WikiPage && i.Path == "modules/combat");
        Assert.DoesNotContain(envelope.Items, i => i.Path == "modules/combat-ui");
    }

    [Fact]
    public async Task EditorTask_MissingBuildBranch_BindsToSelectedBranch()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        // 额外较早创建的分支，若 resolver 选错默认分支会混用
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            BranchName = "main-old",
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        await context.SaveChangesAsync();

        PublishPage(
            context,
            seed,
            title: "Release Notes",
            path: "release/notes",
            content: "release branch wiki",
            sourceFiles: ["SampleProject/Source/Release/Notes.cpp"],
            generationId: seed.GenerationId);

        var service = CreateService(context);
        var envelope = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TaskDescription = "release notes",
            BuildIdentity = new BuildIdentity
            {
                // 故意省略 Branch，应绑定到已选 Wiki 分支 main
                BuildChangelist = "1000",
                ProjectId = "SampleProject"
            },
            RiskLevel = EditorOperationRiskLevel.Query
        });

        Assert.Equal(ContextCompatibility.Exact, envelope.Compatibility);
        Assert.Equal(seed.BranchName, envelope.Branch);
        Assert.NotEqual(ContextCompatibility.Rejected, envelope.Compatibility);
    }

    [Fact]
    public async Task EditorTask_WriteWithoutBuildCl_Rejects()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        var service = CreateService(context);

        var envelope = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchName = seed.BranchName,
            TaskDescription = "mutate asset",
            BuildIdentity = new BuildIdentity
            {
                Branch = seed.BranchName
                // BuildChangelist 缺省
            },
            RiskLevel = EditorOperationRiskLevel.Write
        });

        Assert.Equal(ContextCompatibility.Rejected, envelope.Compatibility);
        Assert.Empty(envelope.Items);
        Assert.Contains(envelope.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedWriteRequiresExact);
    }

    [Fact]
    public async Task EditorTask_LiveQueryIds_AreStable()
    {
        using var context = CreateContext();
        var seed = SeedRepo(context);
        var service = CreateService(context);

        var first = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TaskDescription = "inspect selection",
            BuildIdentity = new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = "1000"
            },
            RiskLevel = EditorOperationRiskLevel.Query
        });
        var second = await service.GetEditorTaskContextAsync(new EditorTaskContextRequest
        {
            RepositoryId = seed.RepositoryId,
            TaskDescription = "inspect selection",
            BuildIdentity = new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = "1000"
            },
            RiskLevel = EditorOperationRiskLevel.Query
        });

        var liveIds = first.Items
            .Where(i => i.Kind == ContextItemKind.LiveQueryRequired)
            .Select(i => i.Id)
            .ToArray();
        Assert.NotEmpty(liveIds);
        Assert.All(liveIds, id => Assert.StartsWith("live:", id));
        Assert.Equal(
            liveIds,
            second.Items.Where(i => i.Kind == ContextItemKind.LiveQueryRequired).Select(i => i.Id).ToArray());
    }

    private static ContextAssemblyService CreateService(TestConfigDbContext context)
    {
        var resolver = new WikiSnapshotResolver(context);
        var scopeService = new ScopeConfigurationService(
            context,
            new ScopeConfigurationValidator(),
            new ScopeConfigurationNormalizer(),
            new RepositoryFileSelectionPolicyFactory(),
            new RepositoryGenerationLockService(context));

        var ueService = new UeKnowledgePackageService(
            context,
            new UeKnowledgePackageLoader(),
            new UeKnowledgePackageValidator(new UeKnowledgeFactIndexBuilder()),
            new UeKnowledgeSemanticDiff(),
            new UeKnowledgeMcpContractChecker());

        return new ContextAssemblyService(context, resolver, scopeService, ueService);
    }

    private static TestConfigDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestConfigDbContext(options);
    }

    private static SeedData SeedRepo(TestConfigDbContext context, bool publish = true)
    {
        var repoId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        var languageId = Guid.NewGuid().ToString();
        var generationId = Guid.NewGuid().ToString();

        context.Repositories.Add(new Repository
        {
            Id = repoId,
            OrgName = "SampleOrg",
            RepoName = "SampleProject",
            Description = "test",
            Status = RepositoryStatus.Completed,
            CreatedAt = DateTime.UtcNow
        });
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repoId,
            BranchName = "main",
            CreatedAt = DateTime.UtcNow
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = languageId,
            RepositoryBranchId = branchId,
            LanguageCode = "zh",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow
        });

        // DocumentScope configuration so Combat is in scope and Engine is not
        var scopeDoc = new ScopeConfigurationDocument
        {
            SchemaVersion = 1,
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "game-source",
                    Root = "SampleProject/Source",
                    IncludedPathGlobs = ["**"],
                    IncludedSuffixes = [".h", ".cpp", ".cs"]
                }
            ],
            ContextScope = new ContextScopeRuleDto
            {
                Roots = ["Engine/Source"],
                ReadOnly = true
            }
        };
        var normalized = new ScopeConfigurationNormalizer().Normalize(scopeDoc);
        context.RepositoryScopeConfigurations.Add(new RepositoryScopeConfiguration
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repoId,
            ConfigurationVersion = 1,
            ConfigurationJson = normalized.NormalizedJson,
            ContentHash = normalized.ContentHash,
            IsCurrent = true,
            CreatedAt = DateTime.UtcNow
        });

        if (publish)
        {
            context.WikiGenerations.Add(new WikiGeneration
            {
                Id = generationId,
                RepositoryId = repoId,
                BranchId = branchId,
                BranchLanguageId = languageId,
                Status = WikiGenerationStatus.Published,
                TargetRevision = "1000",
                SnapshotIdentity = "snap|1000",
                LanguageCode = "zh",
                ScopeConfigurationVersion = 1,
                PublishedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
            context.BranchLanguagePublications.Add(new BranchLanguagePublication
            {
                Id = Guid.NewGuid().ToString(),
                BranchLanguageId = languageId,
                CurrentGenerationId = generationId,
                PublishedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        context.SaveChanges();
        return new SeedData(repoId, branchId, languageId, generationId, "main");
    }

    private static void PublishPage(
        TestConfigDbContext context,
        SeedData seed,
        string title,
        string path,
        string content,
        IReadOnlyList<string> sourceFiles,
        string generationId)
    {
        var fileId = Guid.NewGuid().ToString();
        context.DocFiles.Add(new DocFile
        {
            Id = fileId,
            BranchLanguageId = seed.BranchLanguageId,
            Content = content,
            SourceFiles = JsonSerializer.Serialize(sourceFiles),
            GenerationId = generationId,
            CreatedAt = DateTime.UtcNow
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = seed.BranchLanguageId,
            Title = title,
            Path = path,
            Order = 0,
            DocFileId = fileId,
            GenerationId = generationId,
            CreatedAt = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    private sealed record SeedData(
        string RepositoryId,
        string BranchId,
        string BranchLanguageId,
        string GenerationId,
        string BranchName);
}
