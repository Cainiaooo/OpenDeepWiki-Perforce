using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Repositories.Perforce;
using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class PerforceIncrementalEventServiceTests
{
    [Fact]
    public async Task TriggerAsync_FiltersAssetsAndQueuesPhaseOneTask()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 102);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 102, 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PerforceChangelist(101, "alice", "asset and code"),
                new PerforceChangelist(102, "alice", "config")
            ]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Change(101, "Content/Hero.uasset", "edit", "binary"),
                Change(101, "Source/Game/Foo.cpp", "edit", "text")
            ]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 102, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(102, "Config/DefaultGame.ini", "edit", "text")]);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "102",
                It.Is<IReadOnlyList<string>>(files =>
                    files.SequenceEqual(new[] { "Config/DefaultGame.ini", "Source/Game/Foo.cpp" })),
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("incremental-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "102");

        Assert.Equal(PerforceIncrementalEventAction.IncrementalQueued, result.Action);
        Assert.Equal("incremental-task", result.TaskId);
        Assert.Equal(2, result.TotalChangelists);
        Assert.Equal(2, result.IncludedChangelists);
        Assert.Equal(3, result.InspectedFiles);
        Assert.Equal(2, result.IncludedFiles);
        fixture.Incremental.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_FilteredEmptyIntervalStillQueuesBaselineAdvance()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 101);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 101, 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PerforceChangelist(101, "alice", "asset only")]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(101, "Content/Hero.uasset", "edit", "binary")]);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "101",
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("baseline-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "101");

        Assert.Equal(0, result.IncludedFiles);
        Assert.Contains("基线", result.Message);
        fixture.Incremental.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_TargetAtBaselineIsNoOp()
    {
        await using var fixture = CreateFixture("101");
        SetupHave(fixture, 101);

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "101");

        Assert.Equal(PerforceIncrementalEventAction.NoOp, result.Action);
        fixture.Perforce.Verify(client => client.GetChangelistsAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Incremental.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerAsync_LatestChangelistAboveHaveIsRejected()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 150);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "200"));

        Assert.Contains("#have", ex.Message, StringComparison.Ordinal);
        fixture.Perforce.Verify(client => client.GetChangelistsAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Incremental.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerAsync_DuplicateActiveTargetReusesTaskWithoutQueryingRange()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 101);
        fixture.Context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "active-task",
            RepositoryId = fixture.Repository.Id,
            BranchId = fixture.Branch.Id,
            Status = IncrementalUpdateStatus.Pending,
            ExternalTargetRevision = "101"
        });
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "101");

        Assert.Equal(PerforceIncrementalEventAction.AlreadyQueued, result.Action);
        Assert.Equal("active-task", result.TaskId);
        fixture.Perforce.Verify(client => client.GetChangelistsAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Incremental.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerAsync_NonNumericInitialBaselineDoesNotReplayHistory()
    {
        await using var fixture = CreateFixture("p4-initial");
        SetupHave(fixture, 200);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "200",
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("initial-baseline-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "200");

        Assert.Equal(PerforceIncrementalEventAction.IncrementalQueued, result.Action);
        fixture.Perforce.Verify(client => client.GetChangelistsAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Incremental.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_FileThresholdQueuesFullGenerationWithNumericTarget()
    {
        await using var fixture = CreateFixture("100", options => options.Filter.MaxFiles = 1);
        SetupHave(fixture, 101);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 101, 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PerforceChangelist(101, "alice", "large refactor")]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Change(101, "Source/Game/Foo.cpp", "edit", "text"),
                Change(101, "Source/Game/Bar.cpp", "edit", "text")
            ]);
        var fullTask = new BranchGenerationTask
        {
            Id = "full-task",
            RepositoryId = fixture.Repository.Id,
            BranchId = fixture.Branch.Id,
            TargetCommitId = "101"
        };
        fixture.FullGeneration.Setup(service => service.EnqueueFullGenerationAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                null,
                100,
                "101",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BranchGenerationTaskResult(true, fullTask));

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "101");

        Assert.Equal(PerforceIncrementalEventAction.FullGenerationQueued, result.Action);
        Assert.Equal("101", fullTask.TargetCommitId);
        fixture.FullGeneration.Verify(service => service.EnqueueFullGenerationAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            null,
            100,
            "101",
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Incremental.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TriggerAsync_MaxChangelistsQueuesFullGenerationWithTargetOnEnqueue()
    {
        await using var fixture = CreateFixture("100", options => options.Filter.MaxChangelists = 1);
        SetupHave(fixture, 103);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 103, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PerforceChangelist(101, "alice", "one"),
                new PerforceChangelist(102, "alice", "two")
            ]);
        var fullTask = new BranchGenerationTask
        {
            Id = "full-task-cl",
            RepositoryId = fixture.Repository.Id,
            BranchId = fixture.Branch.Id,
            TargetCommitId = "103"
        };
        fixture.FullGeneration.Setup(service => service.EnqueueFullGenerationAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                null,
                100,
                "103",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BranchGenerationTaskResult(true, fullTask));

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "103");

        Assert.Equal(PerforceIncrementalEventAction.FullGenerationQueued, result.Action);
        fixture.Perforce.Verify(client => client.GetFileChangesAsync(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.FullGeneration.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_AddThenDeleteInIntervalEndsAsDeleteOnly()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 102);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 102, 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PerforceChangelist(101, "alice", "add"),
                new PerforceChangelist(102, "alice", "delete")
            ]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(101, "Source/Game/Temp.cpp", "add", "text")]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 102, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(102, "Source/Game/Temp.cpp", "delete", "text")]);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "102",
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.Is<IReadOnlyList<string>>(files => files.SequenceEqual(new[] { "Source/Game/Temp.cpp" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("delete-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "102");

        Assert.Equal(1, result.IncludedFiles);
        fixture.Incremental.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_DeleteThenAddInIntervalEndsAsChangeOnly()
    {
        await using var fixture = CreateFixture("100");
        SetupHave(fixture, 102);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace, 100, 102, 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PerforceChangelist(101, "alice", "delete"),
                new PerforceChangelist(102, "alice", "re-add")
            ]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 101, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(101, "Source/Game/Temp.cpp", "delete", "text")]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace, 102, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Change(102, "Source/Game/Temp.cpp", "add", "text")]);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "102",
                It.Is<IReadOnlyList<string>>(files => files.SequenceEqual(new[] { "Source/Game/Temp.cpp" })),
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("readd-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "102");

        Assert.Equal(1, result.IncludedFiles);
        fixture.Incremental.VerifyAll();
    }

    [Fact]
    public async Task TriggerAsync_CrossScopeMoveRetainsBothEndpointsAndQueuesAcceptedSide()
    {
        await using var fixture = CreateFixture("100");
        var configuration = CreateSourceScopeConfiguration();
        var expectedFilespec = Path.Combine(fixture.Workspace, "Source", "...");
        fixture.Scope
            .Setup(service => service.GetResolvedCurrentAsync(
                fixture.Repository.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(configuration);
        fixture.Scope
            .Setup(service => service.GetPolicyAsync(
                fixture.Repository.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryFileSelectionPolicyFactory().Create(
                configuration,
                fixture.Workspace));
        SetupHave(fixture, 101);
        fixture.Perforce.Setup(client => client.GetChangelistsAsync(
                fixture.Workspace,
                100,
                101,
                501,
                It.Is<IReadOnlyList<string>>(filespecs => filespecs.SequenceEqual(new[] { expectedFilespec })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PerforceChangelist(101, "alice", "move out of scope")]);
        fixture.Perforce.Setup(client => client.GetFileChangesAsync(
                fixture.Workspace,
                101,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PerforceFileChange(
                    101,
                    "//depot/Game/Source/Old.cpp",
                    "Source/Old.cpp",
                    "move/delete",
                    "text",
                    "//depot/Game/Shared/New.cpp",
                    "Shared/New.cpp"),
                new PerforceFileChange(
                    101,
                    "//depot/Game/Shared/New.cpp",
                    "Shared/New.cpp",
                    "move/add",
                    "text",
                    "//depot/Game/Source/Old.cpp",
                    "Source/Old.cpp")
            ]);
        fixture.Incremental.Setup(service => service.TriggerExternalUpdateAsync(
                fixture.Repository.Id,
                fixture.Branch.Id,
                "101",
                It.Is<IReadOnlyList<string>>(files => files.Count == 0),
                It.Is<IReadOnlyList<string>>(files => files.SequenceEqual(new[] { "Source/Old.cpp" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("move-task");

        var result = await fixture.Service.TriggerAsync(
            fixture.Repository.Id,
            fixture.Branch.Id,
            "101");

        var move = Assert.Single(result.Changes!);
        Assert.Equal("Source/Old.cpp", move.OldWorkspaceRelativePath);
        Assert.Equal("Shared/New.cpp", move.NewWorkspaceRelativePath);
        Assert.Equal(1, result.IncludedFiles);
        fixture.Incremental.VerifyAll();
    }

    private static void SetupHave(Fixture fixture, long haveCl)
    {
        fixture.Perforce.Setup(client => client.GetLatestChangelistAsync(
                fixture.Workspace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(haveCl);
    }

    private static PerforceFileChange Change(long cl, string path, string action, string fileType)
    {
        return new PerforceFileChange(cl, $"//depot/Game/{path}", path, action, fileType);
    }

    private static ResolvedScopeConfiguration CreateSourceScopeConfiguration()
    {
        return new ResolvedScopeConfiguration
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
                    IncludedSuffixes = [".cpp"],
                    AcceptAllTextFiles = false
                }
            ],
            ContextScope = null,
            ChangeTriggerScope = new ResolvedChangeTriggerScope
            {
                InheritsDocumentScopes = true,
                AdditionalRoots = [],
                IncludedPathGlobs = ["**"],
                ExcludedPathGlobs = []
            },
            ContentHash = "scope-hash",
            NormalizedJson = "{}"
        };
    }

    private static Fixture CreateFixture(string? baseline, Action<PerforceOptions>? configure = null)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"odw-p4-event-{Guid.NewGuid():N}"));
        var dbOptions = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var context = new TestDbContext(dbOptions);
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = RepositorySource.EncodePerforcePath(workspace.FullName),
            OrgName = "demo",
            RepoName = "game",
            Status = RepositoryStatus.Completed
        };
        var branch = new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            BranchName = "main",
            LastCommitId = baseline
        };
        context.Repositories.Add(repository);
        context.RepositoryBranches.Add(branch);
        context.SaveChanges();

        var p4 = new Mock<IPerforceClient>(MockBehavior.Strict);
        var incremental = new Mock<IIncrementalUpdateService>(MockBehavior.Strict);
        var fullGeneration = new Mock<IBranchGenerationTaskService>(MockBehavior.Strict);
        var options = new PerforceOptions();
        configure?.Invoke(options);
        var monitor = new Mock<IOptionsMonitor<PerforceOptions>>();
        monitor.SetupGet(value => value.CurrentValue).Returns(options);
        var pipeline = new ChangelistFilterPipeline(
            [new ChangelistUserFilter(), new ChangelistDescriptionFilter()],
            [new FileActionFilter(), new FilePathFilter(), new FileContentTypeFilter()]);
        var scopeService = new Mock<IScopeConfigurationService>();
        scopeService
            .Setup(s => s.GetResolvedCurrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OpenDeepWiki.Services.Repositories.Scope.ResolvedScopeConfiguration?)null);
        var service = new PerforceIncrementalEventService(
            context,
            p4.Object,
            pipeline,
            incremental.Object,
            fullGeneration.Object,
            scopeService.Object,
            monitor.Object,
            Mock.Of<ILogger<PerforceIncrementalEventService>>());

        return new Fixture(
            workspace.FullName,
            context,
            repository,
            branch,
            p4,
            incremental,
            fullGeneration,
            scopeService,
            service);
    }

    private sealed class Fixture(
        string workspace,
        TestDbContext context,
        Repository repository,
        RepositoryBranch branch,
        Mock<IPerforceClient> perforce,
        Mock<IIncrementalUpdateService> incremental,
        Mock<IBranchGenerationTaskService> fullGeneration,
        Mock<IScopeConfigurationService> scope,
        PerforceIncrementalEventService service) : IAsyncDisposable
    {
        public string Workspace { get; } = workspace;
        public TestDbContext Context { get; } = context;
        public Repository Repository { get; } = repository;
        public RepositoryBranch Branch { get; } = branch;
        public Mock<IPerforceClient> Perforce { get; } = perforce;
        public Mock<IIncrementalUpdateService> Incremental { get; } = incremental;
        public Mock<IBranchGenerationTaskService> FullGeneration { get; } = fullGeneration;
        public Mock<IScopeConfigurationService> Scope { get; } = scope;
        public PerforceIncrementalEventService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            if (Directory.Exists(Workspace))
            {
                Directory.Delete(Workspace, recursive: true);
            }
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);
}
