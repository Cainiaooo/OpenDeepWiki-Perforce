using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Context;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Tests.Chat.Config;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Context;

public class WikiSnapshotResolverTests
{
    [Fact]
    public async Task Resolve_ExactMatch_ReturnsExact()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "1000");

        Assert.Equal(ContextCompatibility.Exact, result.Compatibility);
        Assert.Equal(seed.GenerationId, result.GenerationId);
        Assert.Equal("1000", result.TargetRevision);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.ExactMatch);
    }

    [Fact]
    public async Task Resolve_NewerRequestWithinDistance_ReturnsCompatibleFallback()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "1500",
            new SnapshotResolveOptions { MaxCompatibleFallbackDistance = 1000 });

        Assert.Equal(ContextCompatibility.CompatibleFallback, result.Compatibility);
        Assert.Equal(seed.GenerationId, result.GenerationId);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.CompatibleFallback);
    }

    [Fact]
    public async Task Resolve_FarFutureWithoutStale_Rejects()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "99999",
            new SnapshotResolveOptions
            {
                MaxCompatibleFallbackDistance = 100,
                AllowStale = false
            });

        Assert.Equal(ContextCompatibility.Rejected, result.Compatibility);
        Assert.Null(result.GenerationId);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedFutureRevision);
    }

    [Fact]
    public async Task Resolve_FarFutureWithStale_ReturnsStale()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "99999",
            new SnapshotResolveOptions
            {
                MaxCompatibleFallbackDistance = 100,
                AllowStale = true
            });

        Assert.Equal(ContextCompatibility.Stale, result.Compatibility);
        Assert.Equal(seed.GenerationId, result.GenerationId);
    }

    [Fact]
    public async Task Resolve_HistoricalExact_PrefersMatchingSuperseded()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "2000");

        var older = new WikiGeneration
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = seed.RepositoryId,
            BranchId = seed.BranchId,
            BranchLanguageId = seed.BranchLanguageId,
            Status = WikiGenerationStatus.Superseded,
            TargetRevision = "1000",
            SnapshotIdentity = "snap-old",
            LanguageCode = "zh",
            PublishedAt = DateTime.UtcNow.AddHours(-2),
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };
        context.WikiGenerations.Add(older);
        await context.SaveChangesAsync();

        var resolver = new WikiSnapshotResolver(context);
        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "1000");

        Assert.Equal(ContextCompatibility.Exact, result.Compatibility);
        Assert.Equal(older.Id, result.GenerationId);
    }

    [Fact]
    public async Task ResolveForBuildIdentity_WriteRequiresExact()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveForBuildIdentityAsync(
            seed.RepositoryId,
            new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = "1500"
            },
            "zh",
            EditorOperationRiskLevel.Write,
            new SnapshotResolveOptions { MaxCompatibleFallbackDistance = 1000 });

        Assert.Equal(ContextCompatibility.Rejected, result.Compatibility);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedWriteRequiresExact);
    }

    [Fact]
    public async Task ResolveForBuildIdentity_WriteWithoutBuildCl_Rejects()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveForBuildIdentityAsync(
            seed.RepositoryId,
            new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = null
            },
            "zh",
            EditorOperationRiskLevel.Write);

        Assert.Equal(ContextCompatibility.Rejected, result.Compatibility);
        Assert.Null(result.GenerationId);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedWriteRequiresExact);
        Assert.Contains(result.Warnings, w => w.Message.Contains("BuildChangelist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveForBuildIdentity_QueryAllowsFallback()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000");
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveForBuildIdentityAsync(
            seed.RepositoryId,
            new BuildIdentity
            {
                Branch = seed.BranchName,
                BuildChangelist = "1500"
            },
            "zh",
            EditorOperationRiskLevel.Query,
            new SnapshotResolveOptions { MaxCompatibleFallbackDistance = 1000 });

        Assert.Equal(ContextCompatibility.CompatibleFallback, result.Compatibility);
        Assert.False(result.IsRejected);
    }

    [Fact]
    public async Task Resolve_NoPublication_Rejects()
    {
        using var context = CreateContext();
        var seed = Seed(context, targetRevision: "1000", publish: false);
        var resolver = new WikiSnapshotResolver(context);

        var result = await resolver.ResolveAsync(
            seed.RepositoryId,
            seed.BranchName,
            "zh",
            "1000");

        Assert.Equal(ContextCompatibility.Rejected, result.Compatibility);
        Assert.Contains(result.Warnings, w => w.ReasonCode == ContextReasonCodes.RejectedNoPublication);
    }

    private static TestConfigDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestConfigDbContext(options);
    }

    private static SeedData Seed(
        TestConfigDbContext context,
        string targetRevision,
        bool publish = true)
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

        if (publish)
        {
            context.WikiGenerations.Add(new WikiGeneration
            {
                Id = generationId,
                RepositoryId = repoId,
                BranchId = branchId,
                BranchLanguageId = languageId,
                Status = WikiGenerationStatus.Published,
                TargetRevision = targetRevision,
                SnapshotIdentity = $"snap|{targetRevision}",
                LanguageCode = "zh",
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

    private sealed record SeedData(
        string RepositoryId,
        string BranchId,
        string BranchLanguageId,
        string GenerationId,
        string BranchName);
}
