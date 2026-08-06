using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Scope;

public class WikiGenerationAndManifestTests
{
    [Fact]
    public void SnapshotIdentity_IsStableAndIncludesLanguageForPublication()
    {
        var snapshot = SnapshotIdentityBuilder.Build(
            "repo-1",
            "branch-1",
            3,
            "1234567",
            "abc123",
            "opendeepwiki-legacy-1");

        var again = SnapshotIdentityBuilder.Build(
            "repo-1",
            "branch-1",
            3,
            "1234567",
            "abc123",
            "opendeepwiki-legacy-1");

        Assert.Equal(snapshot, again);
        Assert.Equal(
            snapshot + "|lang:zh",
            SnapshotIdentityBuilder.BuildPublicationIdentity(snapshot, "zh"));
    }

    [Fact]
    public void Manifest_SubmittedHaveOnly_ExcludesOpenedFiles()
    {
        var service = new WorkspaceManifestService();
        var manifest = service.BuildManifest(
            "repo",
            "branch",
            1,
            "100",
            [
                new WorkspaceManifestEntry
                {
                    RelativePath = "SampleProject/Source/A.cpp",
                    HaveRevision = "10",
                    MatchesHaveContent = true
                },
                new WorkspaceManifestEntry
                {
                    RelativePath = "SampleProject/Source/B.cpp",
                    IsOpened = true,
                    OpenedAction = "edit",
                    MatchesHaveContent = false,
                    LocalDigest = "deadbeef"
                }
            ],
            WorkspaceContentPolicy.SubmittedHaveOnly);

        Assert.Single(manifest.Entries);
        Assert.Equal("SampleProject/Source/A.cpp", manifest.Entries[0].RelativePath);
        Assert.Contains(manifest.Warnings, warning => warning.Contains("Opened", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(manifest.ManifestHash));
    }

    [Fact]
    public void Manifest_VerifyReadFiles_DetectsDrift()
    {
        var service = new WorkspaceManifestService();
        var manifest = service.BuildManifest(
            "repo",
            "branch",
            1,
            "100",
            [
                new WorkspaceManifestEntry
                {
                    RelativePath = "SampleProject/Source/A.cpp",
                    LocalDigest = "aaa",
                    MatchesHaveContent = true
                }
            ],
            WorkspaceContentPolicy.SubmittedHaveOnly);

        var result = service.VerifyReadFiles(
            manifest,
            [
                new WorkspaceReadObservation
                {
                    RelativePath = "SampleProject/Source/A.cpp",
                    LocalDigest = "bbb"
                }
            ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Publish_SwitchesPointer_FailKeepsPrevious()
    {
        using var context = CreateContext();
        var seed = SeedGraph(context);
        var service = new WikiGenerationService(context);

        var first = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            ScopeConfigurationVersion = 1,
            TargetRevision = "100",
            TrackedManifestHash = "hash-1"
        });

        await service.PublishAsync(context, first.Id);

        var publication = await service.GetPublicationAsync(seed.BranchLanguageId);
        Assert.NotNull(publication);
        Assert.Equal(first.Id, publication!.CurrentGenerationId);
        Assert.False(await service.AreDerivativesCurrentAsync(seed.BranchLanguageId));

        var second = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            ScopeConfigurationVersion = 1,
            TargetRevision = "101",
            TrackedManifestHash = "hash-2"
        });

        await service.FailAsync(context, second.Id, "workspace drift");

        publication = await service.GetPublicationAsync(seed.BranchLanguageId);
        Assert.Equal(first.Id, publication!.CurrentGenerationId);

        var failed = await context.WikiGenerations.SingleAsync(item => item.Id == second.Id);
        Assert.Equal(WikiGenerationStatus.Failed, failed.Status);

        var third = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            ScopeConfigurationVersion = 1,
            TargetRevision = "102",
            TrackedManifestHash = "hash-3"
        });

        await service.PublishAsync(context, third.Id);

        publication = await service.GetPublicationAsync(seed.BranchLanguageId);
        Assert.Equal(third.Id, publication!.CurrentGenerationId);

        var superseded = await context.WikiGenerations.SingleAsync(item => item.Id == first.Id);
        Assert.Equal(WikiGenerationStatus.Superseded, superseded.Status);
    }

    [Fact]
    public async Task Publish_AbortsWhenScopeVersionChanged()
    {
        using var context = CreateContext();
        var seed = SeedGraph(context);
        context.RepositoryScopeConfigurations.Add(new RepositoryScopeConfiguration
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            ConfigurationVersion = 2,
            ConfigurationJson = """{"schemaVersion":1,"documentScopes":[{"id":"x","root":"A"}]}""",
            ContentHash = "new-hash",
            IsCurrent = true
        });
        await context.SaveChangesAsync();

        var service = new WikiGenerationService(context);
        var generation = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            ScopeConfigurationVersion = 1,
            ScopeContentHash = "old-hash",
            TargetRevision = "100",
            TrackedManifestHash = "hash"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(context, generation.Id));

        var stored = await context.WikiGenerations.SingleAsync(item => item.Id == generation.Id);
        Assert.Equal(WikiGenerationStatus.Abandoned, stored.Status);
    }

    [Fact]
    public async Task Readers_DoNotSeeStagingCatalog_UntilPublish()
    {
        using var context = CreateContext();
        var seed = SeedGraph(context);
        var service = new WikiGenerationService(context);

        // Published baseline content
        var published = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            TargetRevision = "1",
            TrackedManifestHash = "h1"
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = seed.BranchLanguageId,
            Title = "Published",
            Path = "published",
            Order = 0,
            GenerationId = published.Id
        });
        await context.SaveChangesAsync();
        await service.PublishAsync(context, published.Id);

        // Staging content for a new generation must not be visible to readers
        var staging = await service.BeginStagingAsync(context, new BeginWikiGenerationRequest
        {
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            LanguageCode = "zh",
            TargetRevision = "2",
            TrackedManifestHash = "h2"
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = seed.BranchLanguageId,
            Title = "Staging Only",
            Path = "staging-only",
            Order = 1,
            GenerationId = staging.Id
        });
        await context.SaveChangesAsync();

        var publishedId = await WikiPublicationQuery.GetPublishedGenerationIdAsync(context, seed.BranchLanguageId);
        var visible = await WikiPublicationQuery
            .FilterVisibleCatalogs(context.DocCatalogs.AsQueryable(), seed.BranchLanguageId, publishedId)
            .Select(c => c.Path)
            .ToListAsync();

        Assert.Contains("published", visible);
        Assert.DoesNotContain("staging-only", visible);

        await service.FailAsync(context, staging.Id, "boom");
        var stagingCatalog = await context.DocCatalogs.SingleAsync(c => c.Path == "staging-only");
        Assert.True(stagingCatalog.IsDeleted);
    }

    [Fact]
    public async Task WorkspaceLease_BlocksConcurrentAcquire()
    {
        using var context = CreateContext();
        var seed = SeedGraph(context);
        var leaseService = new SourceWorkspaceLeaseService(context);

        await using var first = await leaseService.TryAcquireAsync(
            context,
            seed.RepositoryId,
            WorkspaceLeasePurposes.Generate,
            "owner-a");
        Assert.NotNull(first);

        var second = await leaseService.TryAcquireAsync(
            context,
            seed.RepositoryId,
            WorkspaceLeasePurposes.Sync,
            "owner-b");
        Assert.Null(second);

        await first!.DisposeAsync();

        await using var third = await leaseService.TryAcquireAsync(
            context,
            seed.RepositoryId,
            WorkspaceLeasePurposes.Sync,
            "owner-b");
        Assert.NotNull(third);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static Seed SeedGraph(TestDbContext context)
    {
        var repositoryId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        var languageId = Guid.NewGuid().ToString();

        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OwnerUserId = "user-1",
            GitUrl = "p4::dGVzdA==",
            OrgName = "sample",
            RepoName = "project",
            Status = RepositoryStatus.Completed
        });
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = "main"
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = languageId,
            RepositoryBranchId = branchId,
            LanguageCode = "zh",
            IsDefault = true
        });
        context.SaveChanges();

        return new Seed(repositoryId, branchId, languageId);
    }

    private sealed record Seed(string RepositoryId, string BranchId, string BranchLanguageId);

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);
}
