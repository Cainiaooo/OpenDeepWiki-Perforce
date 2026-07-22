using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class BranchGenerationTaskServiceTests
{
    [Fact]
    public async Task BranchCleaner_CleansOnlyTargetBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var target = SeedBranchWithDocument(context, repository.Id, "target");
        var other = SeedBranchWithDocument(context, repository.Id, "other");
        context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "target-incremental",
            RepositoryId = repository.Id,
            BranchId = target.BranchId,
            Status = IncrementalUpdateStatus.Pending
        });
        context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "other-incremental",
            RepositoryId = repository.Id,
            BranchId = other.BranchId,
            Status = IncrementalUpdateStatus.Pending
        });
        await context.SaveChangesAsync();

        var branch = await context.RepositoryBranches.SingleAsync(item => item.Id == target.BranchId);
        await new BranchFullGenerationCleaner().CleanAsync(context, branch);
        await context.SaveChangesAsync();

        Assert.Empty(await context.DocCatalogs.Where(item => item.BranchLanguageId == target.BranchLanguageId).ToListAsync());
        Assert.Empty(await context.DocFiles.Where(item => item.BranchLanguageId == target.BranchLanguageId).ToListAsync());
        Assert.Single(await context.DocCatalogs.Where(item => item.BranchLanguageId == other.BranchLanguageId).ToListAsync());
        Assert.Single(await context.DocFiles.Where(item => item.BranchLanguageId == other.BranchLanguageId).ToListAsync());
        Assert.DoesNotContain(await context.IncrementalUpdateTasks.ToListAsync(), item => item.Id == "target-incremental");
        Assert.Contains(await context.IncrementalUpdateTasks.ToListAsync(), item => item.Id == "other-incremental");
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenRepositoryLockExists_ReturnsConflict()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            OwnerType = RepositoryGenerationLockOwnerType.Repository,
            OwnerId = repository.Id,
            Scope = RepositoryGenerationLockScope.Repository
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);

        Assert.False(result.Success);
        Assert.Equal("GENERATION_LOCK_CONFLICT", result.ErrorCode);
        Assert.NotNull(result.ActiveLock);
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenActiveTaskExists_ReturnsActiveTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "active-task",
            RepositoryId = repository.Id,
            BranchId = branch.BranchId,
            Status = BranchGenerationTaskStatus.Pending,
            Mode = BranchGenerationTaskMode.Full
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);

        Assert.False(result.Success);
        Assert.Equal("BRANCH_GENERATION_ACTIVE", result.ErrorCode);
        Assert.Equal("active-task", result.Task?.Id);
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_PersistsTargetCommitIdOnFirstSave()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.EnqueueFullGenerationAsync(
            repository.Id,
            branch.BranchId,
            targetCommitId: "4242");

        Assert.True(result.Success);
        Assert.Equal("4242", result.Task?.TargetCommitId);
        var stored = await context.BranchGenerationTasks.SingleAsync();
        Assert.Equal("4242", stored.TargetCommitId);
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenTaskSaveFails_ReleasesRepositoryLock()
    {
        await using var context = CreateFailingContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        await context.SaveChangesAsync();
        context.ThrowOnSaveNumber = context.SaveCount + 2;
        var service = CreateService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId));

        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task AuthorizeRepositoryMutationAsync_WhenAnonymous_ReturnsUnauthorized()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();

        var result = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context,
            new TestUserContext(null, isAuthenticated: false),
            repository.Id,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, await ExecuteStatusCodeAsync(result));
    }

    [Fact]
    public async Task AuthorizeRepositoryMutationAsync_WhenNonOwner_ReturnsForbidden()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();

        var result = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context,
            new TestUserContext("user-2"),
            repository.Id,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status403Forbidden, await ExecuteStatusCodeAsync(result));
    }

    [Fact]
    public async Task AuthorizeRepositoryMutationAsync_WhenOwnerOrAdmin_ReturnsNull()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();

        var ownerResult = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context,
            new TestUserContext(repository.OwnerUserId),
            repository.Id,
            CancellationToken.None);
        var adminResult = await BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync(
            context,
            new TestUserContext("admin-user", isAdmin: true),
            repository.Id,
            CancellationToken.None);

        Assert.Null(ownerResult);
        Assert.Null(adminResult);
    }

    [Fact]
    public async Task AuthorizeTaskMutationAsync_UsesRepositoryOwnerPolicy()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "task-1",
            RepositoryId = repository.Id,
            BranchId = branch.BranchId,
            Status = BranchGenerationTaskStatus.Failed,
            Mode = BranchGenerationTaskMode.Full
        });
        await context.SaveChangesAsync();

        var forbiddenResult = await BranchGenerationEndpoints.AuthorizeTaskMutationAsync(
            context,
            new TestUserContext("user-2"),
            "task-1",
            CancellationToken.None);
        var ownerResult = await BranchGenerationEndpoints.AuthorizeTaskMutationAsync(
            context,
            new TestUserContext(repository.OwnerUserId),
            "task-1",
            CancellationToken.None);

        Assert.NotNull(forbiddenResult);
        Assert.Equal(StatusCodes.Status403Forbidden, await ExecuteStatusCodeAsync(forbiddenResult));
        Assert.Null(ownerResult);
    }

    private static BranchGenerationTaskService CreateService(TestDbContext context)
    {
        return new BranchGenerationTaskService(
            context,
            new RepositoryGenerationLockService(context),
            new BranchFullGenerationCleaner());
    }

    private static Repository SeedRepository(TestDbContext context, RepositoryStatus status)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = "https://example.com/org/repo.git",
            OrgName = "org",
            RepoName = "repo",
            Status = status
        };
        context.Repositories.Add(repository);
        return repository;
    }

    private static (string BranchId, string BranchLanguageId) SeedBranchWithDocument(
        TestDbContext context,
        string repositoryId,
        string branchName)
    {
        var branchId = Guid.NewGuid().ToString();
        var branchLanguageId = Guid.NewGuid().ToString();
        var docFileId = Guid.NewGuid().ToString();

        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = branchName,
            LastCommitId = $"{branchName}-commit",
            LastProcessedAt = DateTime.UtcNow
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = branchLanguageId,
            RepositoryBranchId = branchId,
            LanguageCode = "zh"
        });
        context.DocFiles.Add(new DocFile
        {
            Id = docFileId,
            BranchLanguageId = branchLanguageId,
            Content = "# Existing"
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = branchLanguageId,
            Title = "Existing",
            Path = $"existing-{branchName}",
            DocFileId = docFileId
        });

        return (branchId, branchLanguageId);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static TestDbContext CreateFailingContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static async Task<int> ExecuteStatusCodeAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .Configure<JsonOptions>(_ => { })
                .BuildServiceProvider(),
            Response =
            {
                Body = new MemoryStream()
            }
        };

        await result.ExecuteAsync(httpContext);
        return httpContext.Response.StatusCode;
    }

    private sealed class TestUserContext(string? userId, bool isAuthenticated = true, bool isAdmin = false) : IUserContext
    {
        public string? UserId { get; } = userId;
        public string? UserName => UserId;
        public string? Email => UserId is null ? null : $"{UserId}@example.com";
        public bool IsAuthenticated { get; } = isAuthenticated;
        public ClaimsPrincipal? User { get; } = CreatePrincipal(userId, isAuthenticated, isAdmin);

        private static ClaimsPrincipal CreatePrincipal(string? userId, bool isAuthenticated, bool isAdmin)
        {
            if (!isAuthenticated || string.IsNullOrWhiteSpace(userId))
            {
                return new ClaimsPrincipal(new ClaimsIdentity());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId)
            };
            if (isAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options)
    {
        public int SaveCount { get; private set; }

        public int? ThrowOnSaveNumber { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (ThrowOnSaveNumber == SaveCount)
            {
                throw new InvalidOperationException("Simulated task save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
