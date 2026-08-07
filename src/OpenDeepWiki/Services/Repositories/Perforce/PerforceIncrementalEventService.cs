using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories.Impact;
using OpenDeepWiki.Services.Repositories.Scope;

namespace OpenDeepWiki.Services.Repositories.Perforce;

public enum PerforceIncrementalEventAction
{
    NoOp,
    IncrementalQueued,
    FullGenerationQueued,
    /// <summary>
    /// An in-flight task for the same target already exists; no new work was created.
    /// </summary>
    AlreadyQueued
}

public sealed record PerforceIncrementalEventResult(
    PerforceIncrementalEventAction Action,
    long TargetChangelist,
    string? TaskId,
    int TotalChangelists,
    int IncludedChangelists,
    int InspectedFiles,
    int IncludedFiles,
    string Message,
    IReadOnlyList<PerforceLogicalChange>? Changes = null,
    IncrementalImpactPlan? ImpactPlan = null);

public interface IPerforceIncrementalEventService
{
    Task<PerforceIncrementalEventResult> TriggerAsync(
        string repositoryId,
        string branchId,
        string? latestChangelist = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a lightweight Perforce event into the phase-one external change-list task.
/// </summary>
public sealed class PerforceIncrementalEventService(
    IContext context,
    IPerforceClient perforceClient,
    IChangelistFilterPipeline filterPipeline,
    IIncrementalUpdateService incrementalUpdateService,
    IBranchGenerationTaskService branchGenerationTaskService,
    IScopeConfigurationService scopeConfigurationService,
    IIncrementalImpactAnalyzer impactAnalyzer,
    IOptionsMonitor<PerforceOptions> optionsMonitor,
    ILogger<PerforceIncrementalEventService> logger) : IPerforceIncrementalEventService
{
    public async Task<PerforceIncrementalEventResult> TriggerAsync(
        string repositoryId,
        string branchId,
        string? latestChangelist = null,
        CancellationToken cancellationToken = default)
    {
        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException("仓库不存在");

        if (repository.SourceType != RepositorySourceType.Perforce)
        {
            throw new ArgumentException("Perforce 轻量事件仅支持 Perforce 源仓库", nameof(repositoryId));
        }

        var branch = await context.RepositoryBranches
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == branchId && item.RepositoryId == repositoryId && !item.IsDeleted,
                cancellationToken)
            ?? throw new KeyNotFoundException("分支不存在");

        var workspaceRoot = repository.SourceLocation;
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Perforce 工作区不存在: {workspaceRoot}");
        }

        var scopeConfig = await scopeConfigurationService.GetResolvedCurrentAsync(repositoryId, cancellationToken);
        var changeFilespecs = PerforceFilespecBuilder.BuildChangeTriggerFilespecs(workspaceRoot, scopeConfig);
        var targetCl = await ResolveTargetChangelistAsync(
            workspaceRoot,
            latestChangelist,
            cancellationToken);
        if (!targetCl.HasValue)
        {
            return new PerforceIncrementalEventResult(
                PerforceIncrementalEventAction.NoOp,
                0,
                null,
                0,
                0,
                0,
                0,
                "工作区没有已同步的 submitted changelist");
        }

        var filterOptions = ResolveFilterOptions(repository);
        ValidateFilterOptions(filterOptions);

        if (!long.TryParse(branch.LastCommitId, NumberStyles.None, CultureInfo.InvariantCulture, out var baseCl)
            || baseCl <= 0)
        {
            // Initial full generation has already produced the documents. Use the phase-one empty-list
            // behavior to establish a numeric CL baseline without replaying the whole depot history.
            var taskId = await incrementalUpdateService.TriggerExternalUpdateAsync(
                repositoryId,
                branchId,
                targetCl.Value.ToString(CultureInfo.InvariantCulture),
                [],
                impactPlanJson: IncrementalImpactPlanSerializer.Serialize(IncrementalImpactPlan.None()),
                cancellationToken: cancellationToken);

            logger.LogInformation(
                "Perforce numeric baseline initialization queued. RepositoryId: {RepositoryId}, BranchId: {BranchId}, PreviousBaseline: {PreviousBaseline}, TargetCL: {TargetCL}, TaskId: {TaskId}",
                repositoryId, branchId, branch.LastCommitId ?? "none", targetCl.Value, taskId);

            return new PerforceIncrementalEventResult(
                PerforceIncrementalEventAction.IncrementalQueued,
                targetCl.Value,
                taskId,
                0,
                0,
                0,
                0,
                "已创建数字型 changelist 基线任务",
                ImpactPlan: IncrementalImpactPlan.None());
        }

        if (targetCl.Value <= baseCl)
        {
            logger.LogInformation(
                "Perforce event is already covered by baseline. RepositoryId: {RepositoryId}, BranchId: {BranchId}, Baseline: {Baseline}, TargetCL: {TargetCL}",
                repositoryId, branchId, baseCl, targetCl.Value);

            return new PerforceIncrementalEventResult(
                PerforceIncrementalEventAction.NoOp,
                targetCl.Value,
                null,
                0,
                0,
                0,
                0,
                "目标 changelist 已被当前基线覆盖");
        }

        var targetRevision = targetCl.Value.ToString(CultureInfo.InvariantCulture);
        var activeIncrementalTask = await context.IncrementalUpdateTasks
            .AsNoTracking()
            .Where(task => !task.IsDeleted
                           && task.RepositoryId == repositoryId
                           && task.BranchId == branchId
                           && (task.Status == IncrementalUpdateStatus.Pending
                               || task.Status == IncrementalUpdateStatus.Processing))
            .OrderBy(task => task.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeIncrementalTask != null)
        {
            if (string.Equals(activeIncrementalTask.ExternalTargetRevision, targetRevision, StringComparison.Ordinal))
            {
                return new PerforceIncrementalEventResult(
                    PerforceIncrementalEventAction.AlreadyQueued,
                    targetCl.Value,
                    activeIncrementalTask.Id,
                    0,
                    0,
                    0,
                    0,
                    "相同目标 changelist 的增量任务已在队列中",
                    ImpactPlan: IncrementalImpactPlanSerializer.Deserialize(activeIncrementalTask.ImpactPlanJson));
            }

            throw new InvalidOperationException(
                $"该分支已有增量更新任务正在排队或处理中 (TaskId: {activeIncrementalTask.Id}, TargetRevision: {activeIncrementalTask.ExternalTargetRevision ?? "none"})");
        }

        var activeFullGenerationTask = await context.BranchGenerationTasks
            .AsNoTracking()
            .Where(task => !task.IsDeleted
                           && task.RepositoryId == repositoryId
                           && task.BranchId == branchId
                           && (task.Status == BranchGenerationTaskStatus.Pending
                               || task.Status == BranchGenerationTaskStatus.Processing))
            .OrderBy(task => task.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (activeFullGenerationTask != null)
        {
            if (string.Equals(activeFullGenerationTask.TargetCommitId, targetRevision, StringComparison.Ordinal))
            {
                return new PerforceIncrementalEventResult(
                    PerforceIncrementalEventAction.AlreadyQueued,
                    targetCl.Value,
                    activeFullGenerationTask.Id,
                    0,
                    0,
                    0,
                    0,
                    "相同目标 changelist 的全量任务已在队列中",
                    ImpactPlan: IncrementalImpactPlanSerializer.Deserialize(activeFullGenerationTask.ImpactPlanJson));
            }

            throw new InvalidOperationException(
                $"该分支已有 full generation 任务正在排队或处理中 (TaskId: {activeFullGenerationTask.Id})");
        }

        // Interval discovery still runs on the request path. Cap work via MaxChangelists/MaxFiles
        // and honor cancellation between CLs so reverse proxies can cancel hung scans.
        // Empty ChangeTrigger filespecs under a non-null scope config means a misconfigured
        // ChangeTriggerScope (e.g. InheritsDocumentScopes=false and AdditionalRoots=[]).
        // Fail closed so we never silently skip the interval and advance the baseline.
        if (scopeConfig is not null && changeFilespecs.Count == 0)
        {
            throw new InvalidOperationException(
                "ChangeTriggerScope 未解析出任何 filespec（请检查 InheritsDocumentScopes / DocumentScopes / AdditionalRoots）。拒绝静默漏更。");
        }

        IReadOnlyList<PerforceChangelist> changelists;
        if (scopeConfig is null)
        {
            changelists = await perforceClient.GetChangelistsAsync(
                workspaceRoot,
                baseCl,
                targetCl.Value,
                checked(filterOptions.MaxChangelists + 1),
                cancellationToken);
        }
        else
        {
            changelists = await perforceClient.GetChangelistsAsync(
                workspaceRoot,
                baseCl,
                targetCl.Value,
                checked(filterOptions.MaxChangelists + 1),
                changeFilespecs,
                cancellationToken);
        }

        if (changelists.Count > filterOptions.MaxChangelists)
        {
            var impactPlan = IncrementalImpactPlan.Full(
                IncrementalImpactReasonCodes.ChangelistThresholdExceeded);
            return await QueueFullGenerationAsync(
                repositoryId,
                branchId,
                targetCl.Value,
                changelists.Count,
                0,
                0,
                0,
                $"changelist 数 {changelists.Count} 超过阈值 {filterOptions.MaxChangelists}",
                impactPlan,
                cancellationToken);
        }

        var comparer = filterOptions.CaseSensitivePaths
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var changedFiles = new HashSet<string>(comparer);
        var deletedFiles = new HashSet<string>(comparer);
        var includedChanges = new List<PerforceLogicalChange>();
        var includedChangelists = 0;
        var inspectedFiles = 0;

        // Scope 配置存在时，文件级路径/后缀判定统一委托 IRepositoryFileSelectionPolicy；
        // 无 Scope 时保持 phase-two 过滤管线兼容行为。
        var selectionPolicy = scopeConfig is null
            ? null
            : await scopeConfigurationService.GetPolicyAsync(repositoryId, cancellationToken);

        foreach (var changelist in changelists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clDecision = filterPipeline.EvaluateChangelist(changelist, filterOptions);
            if (!clDecision.Included)
            {
                logger.LogDebug(
                    "Perforce changelist filtered. CL: {Changelist}, Reason: {Reason}",
                    changelist.Number, clDecision.Reason);
                continue;
            }

            var fileChanges = await perforceClient.GetFileChangesAsync(
                workspaceRoot,
                changelist.Number,
                cancellationToken);
            inspectedFiles += fileChanges.Count;
            var actionFilteredFiles = fileChanges
                .Where(file => IsActionIncluded(file.Action, filterOptions))
                .ToArray();
            var logicalChanges = PerforceChangeCollator.Collate(actionFilteredFiles, comparer);
            var includedAnyFile = false;

            foreach (var change in logicalChanges)
            {
                var oldIncluded = EvaluatePath(
                    change,
                    change.OldWorkspaceRelativePath,
                    change.OldDepotPath,
                    isOldPath: true,
                    selectionPolicy,
                    filterOptions);
                var newIncluded = EvaluatePath(
                    change,
                    change.NewWorkspaceRelativePath,
                    change.NewDepotPath,
                    isOldPath: false,
                    selectionPolicy,
                    filterOptions);

                if (!oldIncluded && !newIncluded)
                {
                    continue;
                }

                includedAnyFile = true;
                includedChanges.Add(change);

                if (oldIncluded && change.OldWorkspaceRelativePath is not null)
                {
                    changedFiles.Remove(change.OldWorkspaceRelativePath);
                    deletedFiles.Add(change.OldWorkspaceRelativePath);
                }

                if (newIncluded && change.NewWorkspaceRelativePath is not null)
                {
                    var isCaseOnlyMove = change.Action.Equals("move", StringComparison.OrdinalIgnoreCase)
                                         && change.OldWorkspaceRelativePath is not null
                                         && comparer.Equals(
                                             change.OldWorkspaceRelativePath,
                                             change.NewWorkspaceRelativePath)
                                         && !StringComparer.Ordinal.Equals(
                                             change.OldWorkspaceRelativePath,
                                             change.NewWorkspaceRelativePath);
                    if (!isCaseOnlyMove)
                    {
                        deletedFiles.Remove(change.NewWorkspaceRelativePath);
                    }

                    changedFiles.Add(change.NewWorkspaceRelativePath);
                }

                // Case-only moves may keep both casings in changed+deleted sets so the
                // payload preserves both path forms. Count logical files with the path
                // comparer so MaxFiles is not tripped twice for one rename.
                var uniqueFileCount = CountUniqueLogicalFiles(changedFiles, deletedFiles, comparer);
                if (uniqueFileCount > filterOptions.MaxFiles)
                {
                    var impactPlan = IncrementalImpactPlan.Full(
                        IncrementalImpactReasonCodes.FileThresholdExceeded,
                        changedFiles.Concat(deletedFiles));
                    return await QueueFullGenerationAsync(
                        repositoryId,
                        branchId,
                        targetCl.Value,
                        changelists.Count,
                        includedChangelists + 1,
                        inspectedFiles,
                        uniqueFileCount,
                        $"过滤后文件数超过阈值 {filterOptions.MaxFiles}",
                        impactPlan,
                        cancellationToken);
                }
            }

            if (includedAnyFile)
            {
                includedChangelists++;
            }
        }

        var scopeConfigurationChanged = await HasScopeConfigurationChangedAsync(
            repositoryId,
            branchId,
            scopeConfig,
            cancellationToken);
        var impactPlanResult = await impactAnalyzer.AnalyzeAsync(
            new IncrementalImpactRequest
            {
                RepositoryId = repositoryId,
                BranchId = branchId,
                Changes = includedChanges.Select(change => new IncrementalSourceChange(
                    change.Action,
                    change.OldWorkspaceRelativePath,
                    change.NewWorkspaceRelativePath,
                    change.FileType)).ToArray(),
                SelectionPolicy = selectionPolicy,
                CaseSensitivePaths = filterOptions.CaseSensitivePaths,
                ScopeConfigurationChanged = scopeConfigurationChanged
            },
            cancellationToken);

        if (impactPlanResult.RequiresFullGeneration)
        {
            return await QueueFullGenerationAsync(
                repositoryId,
                branchId,
                targetCl.Value,
                changelists.Count,
                includedChangelists,
                inspectedFiles,
                changedFiles.Count + deletedFiles.Count,
                $"影响分析升级为 {impactPlanResult.ExecutionLevel} ({string.Join(", ", impactPlanResult.ReasonCodes)})",
                impactPlanResult,
                cancellationToken);
        }

        var impactPlanJson = IncrementalImpactPlanSerializer.Serialize(impactPlanResult);
        var task = await incrementalUpdateService.TriggerExternalUpdateAsync(
            repositoryId,
            branchId,
            targetRevision,
            changedFiles.Order(comparer).ToArray(),
            deletedFiles.Order(comparer).ToArray(),
            cancellationToken: cancellationToken,
            impactPlanJson: impactPlanJson);

        var includedFileCount = changedFiles.Count + deletedFiles.Count;
        logger.LogInformation(
            "Perforce interval filtered and queued. RepositoryId: {RepositoryId}, BranchId: {BranchId}, BaseCL: {BaseCL}, TargetCL: {TargetCL}, TotalCLs: {TotalCLs}, IncludedCLs: {IncludedCLs}, InspectedFiles: {InspectedFiles}, IncludedFiles: {IncludedFiles}, RequestedImpact: {RequestedImpact}, ExecutionImpact: {ExecutionImpact}, ImpactReasons: {ImpactReasons}, TaskId: {TaskId}",
            repositoryId, branchId, baseCl, targetCl.Value, changelists.Count, includedChangelists,
            inspectedFiles, includedFileCount, impactPlanResult.RequestedLevel, impactPlanResult.ExecutionLevel,
            impactPlanResult.ReasonCodes, task);

        return new PerforceIncrementalEventResult(
            PerforceIncrementalEventAction.IncrementalQueued,
            targetCl.Value,
            task,
            changelists.Count,
            includedChangelists,
            inspectedFiles,
            includedFileCount,
            includedFileCount == 0
                ? "区间内变更均被过滤，已创建基线推进任务"
                : "已创建 Perforce 增量更新任务",
            includedChanges,
            impactPlanResult);
    }

    /// <summary>
    /// 比较当前 Scope ContentHash 与分支最近一次已发布世代；不一致则要求全量重建。
    /// 尚无带 hash 的发布记录时不强制（首次生成建立基线）。
    /// </summary>
    private async Task<bool> HasScopeConfigurationChangedAsync(
        string repositoryId,
        string branchId,
        ResolvedScopeConfiguration? scopeConfig,
        CancellationToken cancellationToken)
    {
        if (scopeConfig is null || string.IsNullOrWhiteSpace(scopeConfig.ContentHash))
        {
            return false;
        }

        var publishedHash = await context.WikiGenerations
            .AsNoTracking()
            .Where(generation => !generation.IsDeleted
                                 && generation.RepositoryId == repositoryId
                                 && generation.BranchId == branchId
                                 && generation.Status == WikiGenerationStatus.Published
                                 && generation.ScopeContentHash != null
                                 && generation.ScopeContentHash != string.Empty)
            .OrderByDescending(generation => generation.PublishedAt ?? generation.CreatedAt)
            .Select(generation => generation.ScopeContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(publishedHash))
        {
            return false;
        }

        return !string.Equals(publishedHash, scopeConfig.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long?> ResolveTargetChangelistAsync(
        string workspaceRoot,
        string? latestChangelist,
        CancellationToken cancellationToken)
    {
        // Validate against the explicit full-workspace #have filespec. A scoped filespec can
        // legitimately have no revisions at the requested target CL and must not reject an
        // otherwise synchronized empty interval.
        var haveCl = await perforceClient.GetLatestChangelistAsync(workspaceRoot, cancellationToken);
        if (!haveCl.HasValue || haveCl.Value <= 0)
        {
            if (string.IsNullOrWhiteSpace(latestChangelist))
            {
                return null;
            }

            throw new ArgumentException(
                "工作区没有已同步的 submitted changelist，无法校验 latestChangelist",
                nameof(latestChangelist));
        }

        if (string.IsNullOrWhiteSpace(latestChangelist))
        {
            return haveCl.Value;
        }

        if (!long.TryParse(
                latestChangelist.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
        {
            throw new ArgumentException("latestChangelist 必须为正整数 Perforce changelist 号", nameof(latestChangelist));
        }

        if (parsed > haveCl.Value)
        {
            throw new ArgumentException(
                $"latestChangelist {parsed} 超过工作区 #have changelist {haveCl.Value}",
                nameof(latestChangelist));
        }

        return parsed;
    }

    private PerforceFilterOptions ResolveFilterOptions(Repository repository)
    {
        var options = optionsMonitor.CurrentValue;
        var resolved = options.Filter.Clone();

        var nameKey = $"{repository.OrgName}/{repository.RepoName}";
        if (TryGetOverride(options.RepositoryFilters, nameKey, out var nameOverride))
        {
            resolved.Apply(nameOverride);
        }

        if (TryGetOverride(options.RepositoryFilters, repository.Id, out var idOverride))
        {
            resolved.Apply(idOverride);
        }

        return resolved;
    }

    private static bool TryGetOverride(
        IReadOnlyDictionary<string, PerforceFilterOverrideOptions> overrides,
        string key,
        out PerforceFilterOverrideOptions value)
    {
        foreach (var pair in overrides)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private async Task<PerforceIncrementalEventResult> QueueFullGenerationAsync(
        string repositoryId,
        string branchId,
        long targetCl,
        int totalChangelists,
        int includedChangelists,
        int inspectedFiles,
        int includedFiles,
        string reason,
        IncrementalImpactPlan impactPlan,
        CancellationToken cancellationToken)
    {
        var targetRevision = targetCl.ToString(CultureInfo.InvariantCulture);
        var result = await branchGenerationTaskService.EnqueueFullGenerationAsync(
            repositoryId,
            branchId,
            requestedBy: null,
            priority: 100,
            targetCommitId: targetRevision,
            impactPlanJson: IncrementalImpactPlanSerializer.Serialize(impactPlan),
            cancellationToken: cancellationToken);
        if (!result.Success || result.Task == null)
        {
            throw new InvalidOperationException(
                $"Perforce 区间超过增量阈值，但无法创建全量任务: {result.ErrorMessage ?? result.ErrorCode ?? "unknown"}");
        }

        logger.LogWarning(
            "Perforce interval switched to full generation. RepositoryId: {RepositoryId}, BranchId: {BranchId}, TargetCL: {TargetCL}, RequestedImpact: {RequestedImpact}, ExecutionImpact: {ExecutionImpact}, ImpactReasons: {ImpactReasons}, Reason: {Reason}, TaskId: {TaskId}",
            repositoryId, branchId, targetCl, impactPlan.RequestedLevel, impactPlan.ExecutionLevel,
            impactPlan.ReasonCodes, reason, result.Task.Id);

        return new PerforceIncrementalEventResult(
            PerforceIncrementalEventAction.FullGenerationQueued,
            targetCl,
            result.Task.Id,
            totalChangelists,
            includedChangelists,
            inspectedFiles,
            includedFiles,
            $"{reason}，已转为全量生成任务",
            ImpactPlan: impactPlan);
    }

    private bool EvaluatePath(
        PerforceLogicalChange change,
        string? workspaceRelativePath,
        string? depotPath,
        bool isOldPath,
        IRepositoryFileSelectionPolicy? selectionPolicy,
        PerforceFilterOptions filterOptions)
    {
        if (workspaceRelativePath is null || depotPath is null)
        {
            return false;
        }

        if (selectionPolicy is not null)
        {
            var scopeDecision = selectionPolicy.EvaluateChangeTrigger(
                workspaceRelativePath,
                new SourceFileMetadata
                {
                    DepotPath = depotPath,
                    FileType = change.FileType,
                    IsTracked = true
                });
            if (!scopeDecision.Accepted)
            {
                logger.LogDebug(
                    "Perforce change path scope-filtered. CL: {Changelist}, Path: {Path}, Side: {Side}, Reason: {Reason}",
                    change.Changelist,
                    workspaceRelativePath,
                    isOldPath ? "old" : "new",
                    scopeDecision.ReasonCode);
            }

            return scopeDecision.Accepted;
        }

        var action = change.Action.Equals("move", StringComparison.OrdinalIgnoreCase)
            ? isOldPath ? "move/delete" : "move/add"
            : change.Action;
        var decision = filterPipeline.EvaluateFile(
            new PerforceFileChange(
                change.Changelist,
                depotPath,
                workspaceRelativePath,
                action,
                change.FileType),
            filterOptions);
        if (!decision.Included)
        {
            logger.LogDebug(
                "Perforce change path filtered. CL: {Changelist}, Path: {Path}, Side: {Side}, Reason: {Reason}",
                change.Changelist,
                workspaceRelativePath,
                isOldPath ? "old" : "new",
                decision.Reason);
        }

        return decision.Included;
    }

    private static bool IsActionIncluded(string action, PerforceFilterOptions options)
    {
        return options.IncludedActions.Count == 0
               || options.IncludedActions.Any(included =>
                   included.Equals(action, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountUniqueLogicalFiles(
        IEnumerable<string> changedFiles,
        IEnumerable<string> deletedFiles,
        StringComparer comparer)
    {
        var unique = new HashSet<string>(comparer);
        foreach (var path in changedFiles)
        {
            unique.Add(path);
        }

        foreach (var path in deletedFiles)
        {
            unique.Add(path);
        }

        return unique.Count;
    }

    private static void ValidateFilterOptions(PerforceFilterOptions options)
    {
        if (options.MaxChangelists <= 0)
        {
            throw new ArgumentException("Perforce Filter:MaxChangelists 必须大于 0");
        }

        if (options.MaxFiles <= 0)
        {
            throw new ArgumentException("Perforce Filter:MaxFiles 必须大于 0");
        }
    }
}
