using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Notifications;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class IncrementalUpdateServiceTests
{
    [Fact]
    public async Task TriggerManualUpdateAsync_CreatesTaskEvenWhenNoRemoteChangeInformationExists()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "same-sha");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var taskId = await service.TriggerManualUpdateAsync(repository.Id, branch.Id);

        var task = await context.IncrementalUpdateTasks.SingleAsync(t => t.Id == taskId);
        Assert.True(task.IsManualTrigger);
        Assert.Equal(IncrementalUpdateStatus.Pending, task.Status);
        Assert.Equal("same-sha", task.PreviousCommitId);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenHeadUnchanged_ReturnsSuccessWithoutDocumentUpdates()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "same-sha");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "same-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "same-sha",
                PreviousCommitId = "same-sha"
            });

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);
        Assert.Equal(0, result.UpdatedDocumentsCount);
        analyzer.Verify(
            x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "same-sha", It.IsAny<CancellationToken>()),
            Times.Once);
        analyzer.Verify(
            x => x.GetChangedFilesAsync(It.IsAny<RepositoryWorkspace>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenCommitChanges_PreparesWorkspaceOnceAndUpdatesBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        SeedBranchLanguage(context, branch.Id, "zh");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "new-sha",
                PreviousCommitId = "old-sha"
            });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(),
                "old-sha",
                "new-sha",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/app.cs"]);

        var wikiGenerator = new Mock<IWikiGenerator>(MockBehavior.Strict);
        wikiGenerator
            .Setup(x => x.IncrementalUpdateAsync(
                It.IsAny<RepositoryWorkspace>(),
                It.IsAny<BranchLanguage>(),
                It.Is<string[]>(files => files.SequenceEqual(new[] { "src/app.cs" })),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var notificationService = new Mock<ISubscriberNotificationService>(MockBehavior.Strict);
        notificationService
            .Setup(x => x.NotifySubscribersAsync(
                It.Is<RepositoryUpdateNotification>(n => n.RepositoryId == repository.Id && n.CommitId == "new-sha"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(
            context,
            analyzer: analyzer,
            wikiGenerator: wikiGenerator,
            notificationService: notificationService);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(1, result.ChangedFilesCount);
        Assert.Equal(1, result.UpdatedDocumentsCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("new-sha", updatedBranch.LastCommitId);
        Assert.NotNull(updatedBranch.LastProcessedAt);

        analyzer.Verify(
            x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()),
            Times.Once);
        wikiGenerator.VerifyAll();
        notificationService.VerifyAll();
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenCommitAdvancesWithoutChangedFiles_StillAdvancesStoredCommit()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "new-sha",
                PreviousCommitId = "old-sha"
            });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(),
                "old-sha",
                "new-sha",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("new-sha", updatedBranch.LastCommitId);
        Assert.NotNull(updatedBranch.LastProcessedAt);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WithExternalInjection_UsesInjectedListAndAdvancesToTargetRevision()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        SeedBranchLanguage(context, branch.Id, "zh");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "1000", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                SourceType = RepositorySourceType.Perforce,
                SupportsIncrementalUpdates = true,
                // Perforce 增量：工作区 CommitId 等于上次基线，真正的目标版本由外部注入覆盖。
                CommitId = "1000",
                PreviousCommitId = "1000"
            });

        var expectedFiles = new[] { "Source/Foo.cpp", "Script/Bar.as" };
        var wikiGenerator = new Mock<IWikiGenerator>(MockBehavior.Strict);
        wikiGenerator
            .Setup(x => x.IncrementalUpdateAsync(
                It.IsAny<RepositoryWorkspace>(),
                It.IsAny<BranchLanguage>(),
                It.Is<string[]>(files => files.SequenceEqual(expectedFiles)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(context, analyzer: analyzer, wikiGenerator: wikiGenerator);

        var json = System.Text.Json.JsonSerializer.Serialize(expectedFiles);
        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id, json, "1010");

        Assert.True(result.Success);
        Assert.Equal(2, result.ChangedFilesCount);
        Assert.Equal(1, result.UpdatedDocumentsCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("1010", updatedBranch.LastCommitId);

        // 外部注入时不应回落到内部 diff。
        analyzer.Verify(
            x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        wikiGenerator.VerifyAll();
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WithExternalFilesButNoTargetRevision_StillProcessesWithoutAdvancingBaseline()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        SeedBranchLanguage(context, branch.Id, "zh");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "1000", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                SourceType = RepositorySourceType.Perforce,
                SupportsIncrementalUpdates = true,
                // 工作区版本标识等于基线；若没有"外部注入优先"的守卫，会被误判为无变更而跳过。
                CommitId = "1000",
                PreviousCommitId = "1000"
            });

        var wikiGenerator = new Mock<IWikiGenerator>(MockBehavior.Strict);
        wikiGenerator
            .Setup(x => x.IncrementalUpdateAsync(
                It.IsAny<RepositoryWorkspace>(),
                It.IsAny<BranchLanguage>(),
                It.Is<string[]>(files => files.SequenceEqual(new[] { "Source/Foo.cpp" })),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(context, analyzer: analyzer, wikiGenerator: wikiGenerator);

        var json = System.Text.Json.JsonSerializer.Serialize(new[] { "Source/Foo.cpp" });
        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id, json, externalTargetRevision: null);

        Assert.True(result.Success);
        Assert.Equal(1, result.ChangedFilesCount);
        Assert.Equal(1, result.UpdatedDocumentsCount);

        // 未提供目标版本 → 基线保持不变，但文件仍被处理。
        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("1000", updatedBranch.LastCommitId);
        wikiGenerator.VerifyAll();
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WithExternalEmptyList_AdvancesBaselineWithoutDocumentUpdates()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "1000", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                SourceType = RepositorySourceType.Perforce,
                SupportsIncrementalUpdates = true,
                CommitId = "1000",
                PreviousCommitId = "1000"
            });

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id, "[]", "1010");

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);
        Assert.Equal(0, result.UpdatedDocumentsCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("1010", updatedBranch.LastCommitId);

        analyzer.Verify(
            x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_PerforceWithoutExternalInjection_IsNoOp()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "1000", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                SourceType = RepositorySourceType.Perforce,
                SupportsIncrementalUpdates = true,
                CommitId = "1000",
                PreviousCommitId = "1000"
            });

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("1000", updatedBranch.LastCommitId);

        analyzer.Verify(
            x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TriggerExternalUpdateAsync_IsIdempotentForSameTargetRevision()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var first = await service.TriggerExternalUpdateAsync(
            repository.Id, branch.Id, "1010", new[] { "Source/Foo.cpp" });
        var second = await service.TriggerExternalUpdateAsync(
            repository.Id, branch.Id, "1010", new[] { "Source/Foo.cpp", "Source/Baz.cpp" });

        Assert.Equal(first, second);
        Assert.Single(await context.IncrementalUpdateTasks.ToListAsync());

        var task = await context.IncrementalUpdateTasks.SingleAsync(t => t.Id == first);
        Assert.Equal("1010", task.ExternalTargetRevision);
        Assert.Equal(IncrementalUpdateStatus.Pending, task.Status);
        Assert.False(task.IsManualTrigger);
        Assert.Contains("Source/Foo.cpp", task.ExternalChangedFiles);
    }

    [Fact]
    public async Task TriggerExternalUpdateAsync_RejectsDifferentTargetRevisionWhileTaskPending()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.TriggerExternalUpdateAsync(
            repository.Id, branch.Id, "1010", new[] { "Source/Foo.cpp" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TriggerExternalUpdateAsync(
                repository.Id, branch.Id, "1020", new[] { "Source/Bar.cpp" }));

        Assert.Contains("已有增量更新任务", ex.Message);
        Assert.Single(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task TriggerExternalUpdateAsync_RejectsRegressedTargetRevision()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1020");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TriggerExternalUpdateAsync(
                repository.Id, branch.Id, "1010", new[] { "Source/Foo.cpp" }));

        Assert.Contains("早于当前基线", ex.Message);
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task TriggerExternalUpdateAsync_RejectsNonPerforceSource()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: "https://github.com/demo/repo.git");
        var branch = SeedBranch(context, repository.Id, "main", "abc123");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TriggerExternalUpdateAsync(
                repository.Id, branch.Id, "1010", new[] { "src/app.cs" }));

        Assert.Contains("Perforce", ex.Message);
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task TriggerExternalUpdateAsync_RejectsOversizedTargetRevision()
    {
        using var context = CreateContext();
        var repository = SeedRepository(
            context,
            generateSkill: false,
            gitUrl: RepositorySource.EncodePerforcePath("/tmp/p4-workspace"));
        var branch = SeedBranch(context, repository.Id, "main", "1000");
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var tooLong = new string('9', IncrementalUpdateService.MaxRevisionIdLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.TriggerExternalUpdateAsync(
                repository.Id, branch.Id, tooLong, new[] { "Source/Foo.cpp" }));

        Assert.Contains("长度不能超过", ex.Message);
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_IgnoresExternalInjectionForGitSource()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                SourceType = RepositorySourceType.Git,
                SupportsIncrementalUpdates = true,
                CommitId = "new-sha",
                PreviousCommitId = "old-sha"
            });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(),
                "old-sha",
                "new-sha",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var service = CreateService(context, analyzer: analyzer);

        // 即使带上外部列表与 revision，Git 源也不应被外部 revision 覆盖基线。
        var result = await service.ProcessIncrementalUpdateAsync(
            repository.Id,
            branch.Id,
            System.Text.Json.JsonSerializer.Serialize(new[] { "evil.cpp" }),
            "999999");

        Assert.True(result.Success);
        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("new-sha", updatedBranch.LastCommitId);
        Assert.NotEqual("999999", updatedBranch.LastCommitId);
        analyzer.Verify(
            x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(), "old-sha", "new-sha", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("1000", "999", true)]
    [InlineData("1000", "1000", false)]
    [InlineData("1000", "1001", false)]
    [InlineData("p4-initial", "1000", false)]
    [InlineData(null, "1000", false)]
    public void IsRevisionRegression_ComparesNumericChangelists(
        string? baseline, string target, bool expected)
    {
        Assert.Equal(expected, IncrementalUpdateService.IsRevisionRegression(baseline, target));
    }

    private static IncrementalUpdateService CreateService(
        TestDbContext context,
        Mock<IRepositoryAnalyzer>? analyzer = null,
        Mock<IWikiGenerator>? wikiGenerator = null,
        Mock<ISubscriberNotificationService>? notificationService = null)
    {
        analyzer ??= new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        wikiGenerator ??= new Mock<IWikiGenerator>(MockBehavior.Strict);
        if (notificationService == null)
        {
            notificationService = new Mock<ISubscriberNotificationService>(MockBehavior.Strict);
            notificationService
                .Setup(x => x.NotifySubscribersAsync(It.IsAny<RepositoryUpdateNotification>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return new IncrementalUpdateService(
            analyzer.Object,
            wikiGenerator.Object,
            Mock.Of<IRepositorySkillMarkdownBuilder>(),
            notificationService.Object,
            context,
            Options.Create(new IncrementalUpdateOptions()),
            Mock.Of<ILogger<IncrementalUpdateService>>());
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static Repository SeedRepository(TestDbContext context, bool generateSkill, string? gitUrl = null)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = gitUrl ?? "https://github.com/demo/repo.git",
            OrgName = "demo",
            RepoName = "repo",
            Status = RepositoryStatus.Completed,
            GenerateSkill = generateSkill
        };

        context.Repositories.Add(repository);
        return repository;
    }

    private static RepositoryBranch SeedBranch(
        TestDbContext context,
        string repositoryId,
        string branchName,
        string? lastCommitId)
    {
        var branch = new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchName = branchName,
            LastCommitId = lastCommitId
        };

        context.RepositoryBranches.Add(branch);
        return branch;
    }

    private static BranchLanguage SeedBranchLanguage(
        TestDbContext context,
        string branchId,
        string languageCode)
    {
        var language = new BranchLanguage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryBranchId = branchId,
            LanguageCode = languageCode,
            IsDefault = true
        };

        context.BranchLanguages.Add(language);
        return language;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options)
    {
    }
}
