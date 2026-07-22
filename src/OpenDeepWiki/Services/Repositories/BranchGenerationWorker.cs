using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

public sealed class BranchGenerationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BranchGenerationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Branch generation worker started. Polling interval: {PollingInterval}s", PollingInterval.TotalSeconds);

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
                logger.LogError(ex, "Branch generation polling failed");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingTasksAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var branchProcessor = scope.ServiceProvider.GetRequiredService<IRepositoryBranchProcessor>();
        var lockService = scope.ServiceProvider.GetRequiredService<IRepositoryGenerationLockService>();
        var processingLogService = scope.ServiceProvider.GetService<IProcessingLogService>();

        await RecoverProcessingTasksWithoutLocksAsync(context, processingLogService, stoppingToken);

        var pendingTasks = await context.BranchGenerationTasks
            .AsNoTracking()
            .Where(item => !item.IsDeleted && item.Status == BranchGenerationTaskStatus.Pending)
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Take(10)
            .ToListAsync(stoppingToken);

        foreach (var task in pendingTasks)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ProcessTaskAsync(context, branchProcessor, lockService, processingLogService, task, stoppingToken);
        }
    }

    private async Task ProcessTaskAsync(
        IContext context,
        IRepositoryBranchProcessor branchProcessor,
        IRepositoryGenerationLockService lockService,
        IProcessingLogService? processingLogService,
        BranchGenerationTask task,
        CancellationToken stoppingToken)
    {
        var claim = await TryClaimTaskAsync(context, lockService, task, stoppingToken);
        if (claim is null)
        {
            logger.LogInformation(
                "Branch generation task was not claimed. TaskId: {TaskId}, RepositoryId: {RepositoryId}",
                task.Id, task.RepositoryId);
            return;
        }

        task = claim.Task;

        try
        {
            var repository = claim.Repository;
            var branch = claim.Branch;

            if (repository is null || branch is null)
            {
                throw new InvalidOperationException($"Repository or branch not found. RepositoryId: {task.RepositoryId}, BranchId: {task.BranchId}");
            }

            if (processingLogService is not null)
            {
                await processingLogService.LogAsync(
                    task.RepositoryId,
                    task.BranchId,
                    task.Id,
                    ProcessingStep.Workspace,
                    $"Starting branch full generation: {branch.BranchName}",
                    cancellationToken: stoppingToken);
            }

            // A phase-two Perforce event can pre-populate the numeric target when a large interval
            // is promoted to full generation. PrepareWorkspaceAsync intentionally uses the
            // non-numeric p4-initial sentinel for a forced full scan, so preserve the event target
            // after successful generation instead of regressing the branch baseline to that sentinel.
            var requestedTargetCommitId = task.TargetCommitId;
            var processedCommitId = await branchProcessor.ProcessBranchAsync(
                context,
                repository,
                branch,
                task.Id,
                forceFullGeneration: true,
                stoppingToken);

            var targetCommitId = ResolveCompletedTargetCommitId(
                repository.SourceType,
                requestedTargetCommitId,
                processedCommitId);
            task.TargetCommitId = targetCommitId;
            task.Status = BranchGenerationTaskStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.UpdateTimestamp();

            branch.GenerationStatus = BranchGenerationTaskStatus.Completed;
            branch.LastGenerationTaskId = task.Id;
            branch.LastGenerationError = null;
            branch.LastGenerationCompletedAt = task.CompletedAt;
            branch.LastCommitId = targetCommitId;
            branch.UpdateTimestamp();

            await context.SaveChangesAsync(stoppingToken);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Branch generation task cancelled while processing. TaskId: {TaskId}", task.Id);
            await MarkTaskFailedAsync(
                context,
                processingLogService,
                task,
                "Branch generation was cancelled while processing",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Branch generation task failed. TaskId: {TaskId}", task.Id);
            await MarkTaskFailedAsync(context, processingLogService, task, ex.Message, CancellationToken.None);
        }
        finally
        {
            await lockService.ReleaseAsync(
                context,
                task.RepositoryId,
                RepositoryGenerationLockOwnerType.BranchTask,
                task.Id,
                CancellationToken.None);
        }
    }

    internal static string? ResolveCompletedTargetCommitId(
        RepositorySourceType sourceType,
        string? requestedTargetCommitId,
        string? processedCommitId)
    {
        if (sourceType == RepositorySourceType.Perforce
            && long.TryParse(requestedTargetCommitId, out var requestedCl)
            && requestedCl > 0)
        {
            return requestedCl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return processedCommitId;
    }

    private async Task<ClaimedBranchGenerationTask?> TryClaimTaskAsync(
        IContext context,
        IRepositoryGenerationLockService lockService,
        BranchGenerationTask pendingTask,
        CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;

        if (context is DbContext dbContext &&
            !string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);
            var updatedRows = await context.BranchGenerationTasks
                .Where(item => item.Id == pendingTask.Id &&
                               !item.IsDeleted &&
                               item.Status == BranchGenerationTaskStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, BranchGenerationTaskStatus.Processing)
                        .SetProperty(item => item.StartedAt, now)
                        .SetProperty(item => item.CompletedAt, (DateTime?)null)
                        .SetProperty(item => item.ErrorMessage, (string?)null)
                        .SetProperty(item => item.UpdatedAt, now),
                    stoppingToken);

            if (updatedRows != 1)
            {
                await transaction.RollbackAsync(stoppingToken);
                return null;
            }

            var lockAcquired = await lockService.TryAcquireAsync(
                context,
                pendingTask.RepositoryId,
                RepositoryGenerationLockOwnerType.BranchTask,
                pendingTask.Id,
                RepositoryGenerationLockScope.Branch,
                stoppingToken);

            if (!lockAcquired)
            {
                await transaction.RollbackAsync(stoppingToken);
                return null;
            }

            var task = await context.BranchGenerationTasks
                .FirstAsync(item => item.Id == pendingTask.Id, stoppingToken);
            var repository = await context.Repositories
                .FirstOrDefaultAsync(item => item.Id == task.RepositoryId && !item.IsDeleted, stoppingToken);
            var branch = await context.RepositoryBranches
                .FirstOrDefaultAsync(item => item.Id == task.BranchId && item.RepositoryId == task.RepositoryId && !item.IsDeleted, stoppingToken);

            if (branch is not null)
            {
                branch.GenerationStatus = BranchGenerationTaskStatus.Processing;
                branch.LastGenerationTaskId = task.Id;
                branch.LastGenerationError = null;
                branch.LastGenerationStartedAt = now;
                branch.LastGenerationCompletedAt = null;
                branch.UpdateTimestamp();
                await context.SaveChangesAsync(stoppingToken);
            }

            await transaction.CommitAsync(stoppingToken);
            return new ClaimedBranchGenerationTask(task, repository, branch);
        }

        var fallbackTask = await context.BranchGenerationTasks
            .FirstOrDefaultAsync(item => item.Id == pendingTask.Id &&
                                         !item.IsDeleted &&
                                         item.Status == BranchGenerationTaskStatus.Pending,
                stoppingToken);
        if (fallbackTask is null)
        {
            return null;
        }

        var fallbackLockAcquired = await lockService.TryAcquireAsync(
            context,
            fallbackTask.RepositoryId,
            RepositoryGenerationLockOwnerType.BranchTask,
            fallbackTask.Id,
            RepositoryGenerationLockScope.Branch,
            stoppingToken);

        if (!fallbackLockAcquired)
        {
            return null;
        }

        var fallbackRepository = await context.Repositories
            .FirstOrDefaultAsync(item => item.Id == fallbackTask.RepositoryId && !item.IsDeleted, stoppingToken);
        var fallbackBranch = await context.RepositoryBranches
            .FirstOrDefaultAsync(item => item.Id == fallbackTask.BranchId && item.RepositoryId == fallbackTask.RepositoryId && !item.IsDeleted, stoppingToken);

        fallbackTask.Status = BranchGenerationTaskStatus.Processing;
        fallbackTask.StartedAt = now;
        fallbackTask.CompletedAt = null;
        fallbackTask.ErrorMessage = null;
        fallbackTask.UpdateTimestamp();

        if (fallbackBranch is not null)
        {
            fallbackBranch.GenerationStatus = BranchGenerationTaskStatus.Processing;
            fallbackBranch.LastGenerationTaskId = fallbackTask.Id;
            fallbackBranch.LastGenerationError = null;
            fallbackBranch.LastGenerationStartedAt = now;
            fallbackBranch.LastGenerationCompletedAt = null;
            fallbackBranch.UpdateTimestamp();
        }

        await context.SaveChangesAsync(stoppingToken);
        return new ClaimedBranchGenerationTask(fallbackTask, fallbackRepository, fallbackBranch);
    }

    private async Task RecoverProcessingTasksWithoutLocksAsync(
        IContext context,
        IProcessingLogService? processingLogService,
        CancellationToken cancellationToken)
    {
        var orphanedTasks = await context.BranchGenerationTasks
            .Where(task => !task.IsDeleted &&
                           task.Status == BranchGenerationTaskStatus.Processing &&
                           !context.RepositoryGenerationLocks.Any(generationLock =>
                               !generationLock.IsDeleted &&
                               generationLock.RepositoryId == task.RepositoryId &&
                               generationLock.OwnerType == RepositoryGenerationLockOwnerType.BranchTask &&
                               generationLock.OwnerId == task.Id))
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var task in orphanedTasks)
        {
            logger.LogWarning(
                "Recovering branch generation task stuck in Processing without active lock. TaskId: {TaskId}, RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                task.Id,
                task.RepositoryId,
                task.BranchId);
            await MarkTaskFailedAsync(
                context,
                processingLogService,
                task,
                "Branch generation stopped before reaching a terminal state",
                cancellationToken);
        }
    }

    private async Task MarkTaskFailedAsync(
        IContext context,
        IProcessingLogService? processingLogService,
        BranchGenerationTask task,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        task.Status = BranchGenerationTaskStatus.Failed;
        task.ErrorMessage = errorMessage;
        task.CompletedAt = DateTime.UtcNow;
        task.RetryCount++;
        task.UpdateTimestamp();

        var branch = await context.RepositoryBranches
            .FirstOrDefaultAsync(item => item.Id == task.BranchId && !item.IsDeleted, cancellationToken);

        if (branch is not null)
        {
            branch.GenerationStatus = BranchGenerationTaskStatus.Failed;
            branch.LastGenerationTaskId = task.Id;
            branch.LastGenerationError = errorMessage;
            branch.LastGenerationCompletedAt = task.CompletedAt;
            branch.UpdateTimestamp();
        }

        if (processingLogService is not null)
        {
            await processingLogService.LogAsync(
                task.RepositoryId,
                task.BranchId,
                task.Id,
                ProcessingStep.Content,
                $"Branch generation failed: {errorMessage}",
                cancellationToken: cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private sealed record ClaimedBranchGenerationTask(
        BranchGenerationTask Task,
        Repository? Repository,
        RepositoryBranch? Branch);
}
