using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Repositories.Scope;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories.Scope;

public class ScopeConfigurationServiceTests
{
    [Fact]
    public async Task Get_WithoutConfiguration_ReturnsMigrationHint()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context);
        var service = CreateService(context);

        var response = await service.GetAsync(repository.Id);

        Assert.False(response.HasConfiguration);
        Assert.True(response.MigrationHint);
        Assert.Contains("尚未配置 Scope", response.MigrationMessage);
    }

    [Fact]
    public async Task Update_VersionsConfig_AndWritesAudit()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context);
        var service = CreateService(context);

        var document = new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "game-source",
                    Root = "SampleProject/Source",
                    IncludedPathGlobs = ["**"],
                    IncludedSuffixes = [".cpp", ".h"]
                }
            ]
        };

        var saved = await service.UpdateAsync(repository.Id, document, "user-1", "initial scope");
        Assert.True(saved.HasConfiguration);
        Assert.Equal(1, saved.ConfigurationVersion);
        Assert.False(string.IsNullOrWhiteSpace(saved.ContentHash));

        document.DocumentScopes[0].Root = "SampleProject/Source/Game";
        var updated = await service.UpdateAsync(repository.Id, document, "user-1", "narrow root");
        Assert.Equal(2, updated.ConfigurationVersion);
        Assert.True(updated.ReindexRequired);

        var versions = await context.RepositoryScopeConfigurations
            .Where(item => item.RepositoryId == repository.Id)
            .OrderBy(item => item.ConfigurationVersion)
            .ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.False(versions[0].IsCurrent);
        Assert.True(versions[1].IsCurrent);

        var audits = await context.RepositoryScopeAuditLogs
            .Where(item => item.RepositoryId == repository.Id)
            .ToListAsync();
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task Preview_ReportsImpact()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context);
        var service = CreateService(context);

        var preview = await service.PreviewAsync(repository.Id, new ScopeConfigurationDocument
        {
            DocumentScopes =
            [
                new DocumentScopeRuleDto
                {
                    Id = "game-source",
                    Root = "SampleProject/Source"
                }
            ]
        });

        Assert.True(preview.IsValid);
        Assert.NotNull(preview.Impact);
        Assert.Contains("game-source", preview.Impact!.AddedDocumentScopeIds);
        Assert.True(preview.Impact.ReindexRequired);
    }

    [Fact]
    public async Task GetPolicy_UsesLegacyFallbackWithoutConfig()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context);
        var service = CreateService(context);

        var policy = await service.GetPolicyAsync(repository.Id);
        Assert.True(policy.Configuration.IsLegacyFallback);
    }

    private static ScopeConfigurationService CreateService(TestDbContext context)
    {
        return new ScopeConfigurationService(
            context,
            new ScopeConfigurationValidator(),
            new ScopeConfigurationNormalizer(),
            new RepositoryFileSelectionPolicyFactory(),
            new RepositoryGenerationLockService(context));
    }

    private static Repository SeedRepository(TestDbContext context)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = "https://github.com/example/sample.git",
            OrgName = "example",
            RepoName = "sample",
            Status = RepositoryStatus.Completed
        };
        context.Repositories.Add(repository);

        var branch = new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            BranchName = "main"
        };
        context.RepositoryBranches.Add(branch);
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryBranchId = branch.Id,
            LanguageCode = "zh",
            IsDefault = true
        });
        context.SaveChanges();
        return repository;
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);
}
