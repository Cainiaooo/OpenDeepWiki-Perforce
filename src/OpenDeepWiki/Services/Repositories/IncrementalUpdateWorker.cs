using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Background worker that processes manual incremental tasks and performs scheduled scans.
/// </summary>
public class IncrementalUpdateWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncrementalUpdateWorker> _logger;
    private readonly IncrementalUpdateOptions _options;

    public IncrementalUpdateWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<IncrementalUpdateWorker> logger,
        IOptions<IncrementalUpdateOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IncrementalUpdateWorker started. PollingInterval: {PollingInterval}s, ScheduledEnabled: {Enabled}",
            _options.PollingIntervalSeconds,
            _options.Enabled);

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during incremental update polling");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.PollingIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("IncrementalUpdateWorker stopped gracefully");
    }

    private async Task ProcessPendingTasksAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var updateService = scope.ServiceProvider.GetRequiredService<IIncrementalUpdateService>();
        var gitPlatformService = scope.ServiceProvider.GetRequiredService<IGitPlatformService>();
        var repositoryAnalyzer = scope.ServiceProvider.GetRequiredService<IRepositoryAnalyzer>();

        var pendingTasks = await GetPendingTasksAsync(context, stoppingToken);

        foreach (var task in pendingTasks)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Cancellation requested, stopping task processing");
                break;
            }

            await ProcessSingleTaskAsync(context, updateService, task, stoppingToken);
        }

        await CheckScheduledUpdatesAsync(context, gitPlatformService, repositoryAnalyzer, stoppingToken);
    }

    private async Task<List<IncrementalUpdateTask>> GetPendingTasksAsync(
        IContext context,
        CancellationToken stoppingToken)
    {
        return await context.IncrementalUpdateTasks
            .Where(t => !t.IsDeleted && t.Status == IncrementalUpdateStatus.Pending)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ToListAsync(stoppingToken);
    }

    private async Task ProcessSingleTaskAsync(
        IContext context,
        IIncrementalUpdateService updateService,
        IncrementalUpdateTask task,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Processing task. TaskId: {TaskId}, RepositoryId: {RepositoryId}, BranchId: {BranchId}, Priority: {Priority}",
            task.Id, task.RepositoryId, task.BranchId, task.Priority);

        try
        {
            await UpdateTaskStatusAsync(
                context, task, IncrementalUpdateStatus.Processing, null, stoppingToken);

            var result = await updateService.ProcessIncrementalUpdateAsync(
                task.RepositoryId,
                task.BranchId,
                task.ExternalChangedFiles,
                task.ExternalTargetRevision,
                stoppingToken);

            if (result.Success)
            {
                task.TargetCommitId = result.CurrentCommitId;
                await UpdateTaskStatusAsync(
                    context, task, IncrementalUpdateStatus.Completed, null, stoppingToken);

                _logger.LogInformation(
                    "Task completed successfully. TaskId: {TaskId}, ChangedFiles: {ChangedFiles}, Duration: {Duration}ms",
                    task.Id, result.ChangedFilesCount, result.Duration.TotalMilliseconds);
            }
            else
            {
                await UpdateTaskStatusAsync(
                    context, task, IncrementalUpdateStatus.Failed, result.ErrorMessage, stoppingToken);

                _logger.LogWarning(
                    "Task failed. TaskId: {TaskId}, Error: {Error}",
                    task.Id, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Task processing cancelled. TaskId: {TaskId}", task.Id);
            throw;
        }
        catch (Exception ex)
        {
            await UpdateTaskStatusAsync(
                context, task, IncrementalUpdateStatus.Failed, ex.Message, stoppingToken);

            _logger.LogError(ex,
                "Task processing failed with exception. TaskId: {TaskId}",
                task.Id);
        }
    }

    private async Task UpdateTaskStatusAsync(
        IContext context,
        IncrementalUpdateTask task,
        IncrementalUpdateStatus status,
        string? errorMessage,
        CancellationToken stoppingToken)
    {
        task.Status = status;
        task.ErrorMessage = errorMessage;
        task.UpdatedAt = DateTime.UtcNow;

        switch (status)
        {
            case IncrementalUpdateStatus.Processing:
                task.StartedAt = DateTime.UtcNow;
                break;
            case IncrementalUpdateStatus.Completed:
            case IncrementalUpdateStatus.Failed:
                task.CompletedAt = DateTime.UtcNow;
                if (status == IncrementalUpdateStatus.Failed)
                {
                    task.RetryCount++;
                }
                break;
        }

        await context.SaveChangesAsync(stoppingToken);
    }

    private async Task CheckScheduledUpdatesAsync(
        IContext context,
        IGitPlatformService gitPlatformService,
        IRepositoryAnalyzer repositoryAnalyzer,
        CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Scheduled incremental scans are disabled");
            return;
        }

        var now = DateTime.UtcNow;
        var defaultInterval = Math.Max(_options.DefaultUpdateIntervalMinutes, _options.MinUpdateIntervalMinutes);
        var maxRepositoriesPerPoll = Math.Max(1, _options.MaxRepositoriesPerPoll);

        var repositoriesToCheck = await context.Repositories
            .Where(r => !r.IsDeleted && r.Status == RepositoryStatus.Completed)
            .Where(r => r.LastUpdateCheckAt == null ||
                        r.LastUpdateCheckAt.Value.AddMinutes(
                            r.UpdateIntervalMinutes == null
                                ? defaultInterval
                                : (r.UpdateIntervalMinutes.Value < _options.MinUpdateIntervalMinutes
                                    ? _options.MinUpdateIntervalMinutes
                                    : r.UpdateIntervalMinutes.Value)) <= now)
            .OrderBy(r => r.LastUpdateCheckAt ?? DateTime.MinValue)
            .Take(maxRepositoriesPerPoll)
            .ToListAsync(stoppingToken);

        foreach (var repository in repositoriesToCheck)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await SyncRepositoryVisibilityAsync(context, gitPlatformService, repository, stoppingToken);
            await CreateScheduledUpdateTasksAsync(context, repositoryAnalyzer, repository, stoppingToken);
        }
    }

    private async Task SyncRepositoryVisibilityAsync(
        IContext context,
        IGitPlatformService gitPlatformService,
        Repository repository,
        CancellationToken stoppingToken)
    {
        try
        {
            if (!IsPublicPlatform(repository.GitUrl) ||
                string.IsNullOrWhiteSpace(repository.OrgName) ||
                string.IsNullOrWhiteSpace(repository.RepoName))
            {
                return;
            }

            var repoInfo = await gitPlatformService.CheckRepoExistsAsync(repository.OrgName, repository.RepoName);
            if (!repoInfo.Exists)
            {
                return;
            }

            var shouldBePublic = !repoInfo.IsPrivate;
            if (repository.IsPublic != shouldBePublic)
            {
                _logger.LogInformation(
                    "Visibility mismatch detected for {Org}/{Repo}: DB={DbVisibility}, Actual={ActualVisibility}. Updating.",
                    repository.OrgName, repository.RepoName,
                    repository.IsPublic ? "Public" : "Private",
                    shouldBePublic ? "Public" : "Private");

                repository.IsPublic = shouldBePublic;
                repository.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to sync visibility for {Org}/{Repo}",
                repository.OrgName, repository.RepoName);
        }
    }

    private static bool IsPublicPlatform(string gitUrl)
    {
        try
        {
            var uri = new Uri(gitUrl);
            var host = uri.Host.ToLowerInvariant();
            return host is "github.com" or "gitee.com" or "gitlab.com";
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateScheduledUpdateTasksAsync(
        IContext context,
        IRepositoryAnalyzer repositoryAnalyzer,
        Repository repository,
        CancellationToken stoppingToken)
    {
        try
        {
            var branches = await context.RepositoryBranches
                .Where(b => b.RepositoryId == repository.Id && !b.IsDeleted)
                .ToListAsync(stoppingToken);

            var sourceInfo = RepositorySource.Parse(repository.GitUrl);

            // Perforce 仅事件驱动(外部注入 changelist)，不参与定时空转调度：
            // 否则 GetRemoteBranchHeadCommitAsync 对非 Git 源返回 null 会导致每轮无条件建任务。
            if (sourceInfo.SourceType == RepositorySourceType.Perforce)
            {
                _logger.LogDebug(
                    "Skipping scheduled updates for Perforce repository (external injection only). Repository: {Org}/{Repo}",
                    repository.OrgName, repository.RepoName);
                repository.LastUpdateCheckAt = DateTime.UtcNow;
                repository.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
                return;
            }

            var saveChanges = false;

            foreach (var branch in branches)
            {
                var existingTask = await context.IncrementalUpdateTasks
                    .AnyAsync(t => !t.IsDeleted &&
                                   t.RepositoryId == repository.Id &&
                                   t.BranchId == branch.Id &&
                                   (t.Status == IncrementalUpdateStatus.Pending
                                       || t.Status == IncrementalUpdateStatus.Processing),
                        stoppingToken);

                if (existingTask)
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, task already exists. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName, repository.RepoName, branch.BranchName);
                    continue;
                }

                var activeBranchGenerationTask = await context.BranchGenerationTasks
                    .AnyAsync(t => !t.IsDeleted &&
                                   t.RepositoryId == repository.Id &&
                                   t.BranchId == branch.Id &&
                                   (t.Status == BranchGenerationTaskStatus.Pending ||
                                    t.Status == BranchGenerationTaskStatus.Processing),
                        stoppingToken);

                if (activeBranchGenerationTask)
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, branch full generation is active. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName, repository.RepoName, branch.BranchName);
                    continue;
                }

                string? remoteCommitId;
                try
                {
                    remoteCommitId = await repositoryAnalyzer.GetRemoteBranchHeadCommitAsync(
                        repository,
                        branch.BranchName,
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to query remote HEAD. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName,
                        repository.RepoName,
                        branch.BranchName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(remoteCommitId))
                {
                    if (sourceInfo.SourceType == RepositorySourceType.Git)
                    {
                        _logger.LogWarning(
                            "Remote HEAD was not found. Repository: {Org}/{Repo}, Branch: {Branch}",
                            repository.OrgName,
                            repository.RepoName,
                            branch.BranchName);
                        continue;
                    }

                    CreateScheduledTask(context, repository, branch, branch.LastCommitId, null);
                    saveChanges = true;
                    continue;
                }

                if (string.Equals(remoteCommitId, branch.LastCommitId, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, remote HEAD unchanged. Repository: {Org}/{Repo}, Branch: {Branch}, Commit: {CommitId}",
                        repository.OrgName,
                        repository.RepoName,
                        branch.BranchName,
                        remoteCommitId);
                    continue;
                }

                if (ShouldNormalizeSnapshotBaseline(sourceInfo, branch.LastCommitId, remoteCommitId))
                {
                    NormalizeSnapshotBaseline(repository, branch, remoteCommitId);
                    saveChanges = true;
                    continue;
                }

                CreateScheduledTask(context, repository, branch, branch.LastCommitId, remoteCommitId);
                saveChanges = true;
            }

            repository.LastUpdateCheckAt = DateTime.UtcNow;
            repository.UpdatedAt = DateTime.UtcNow;
            saveChanges = true;

            if (saveChanges)
            {
                await context.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create scheduled update tasks. Repository: {Org}/{Repo}",
                repository.OrgName, repository.RepoName);
        }
    }

    private void NormalizeSnapshotBaseline(
        Repository repository,
        RepositoryBranch branch,
        string remoteCommitId)
    {
        var previousCommitId = branch.LastCommitId;
        branch.LastCommitId = remoteCommitId;
        branch.UpdatedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Normalized scheduled incremental baseline without creating a task. Repository: {Org}/{Repo}, Branch: {Branch}, PreviousCommit: {PreviousCommit}, CurrentCommit: {CurrentCommit}",
            repository.OrgName,
            repository.RepoName,
            branch.BranchName,
            previousCommitId,
            remoteCommitId);
    }

    private static bool ShouldNormalizeSnapshotBaseline(
        RepositorySourceInfo sourceInfo,
        string? previousCommitId,
        string remoteCommitId)
    {
        return sourceInfo.SourceType == RepositorySourceType.LocalDirectory &&
               IsGitCommitId(remoteCommitId) &&
               IsDirectorySnapshotId(previousCommitId);
    }

    private static bool IsGitCommitId(string? commitId)
    {
        return commitId is { Length: 40 } &&
               commitId.All(Uri.IsHexDigit);
    }

    private static bool IsDirectorySnapshotId(string? commitId)
    {
        return commitId is { Length: 64 } &&
               commitId.All(Uri.IsHexDigit);
    }

    private void CreateScheduledTask(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? previousCommitId,
        string? targetCommitId)
    {
        var task = new IncrementalUpdateTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            BranchId = branch.Id,
            PreviousCommitId = previousCommitId,
            TargetCommitId = targetCommitId,
            Status = IncrementalUpdateStatus.Pending,
            Priority = 0,
            IsManualTrigger = false,
            CreatedAt = DateTime.UtcNow
        };

        context.IncrementalUpdateTasks.Add(task);

        _logger.LogInformation(
            "Created scheduled update task. TaskId: {TaskId}, Repository: {Org}/{Repo}, Branch: {Branch}, TargetCommit: {TargetCommit}",
            task.Id, repository.OrgName, repository.RepoName, branch.BranchName, targetCommitId ?? "unknown");
    }
}
