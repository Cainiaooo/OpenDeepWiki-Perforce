using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Repositories.Impact;
using OpenDeepWiki.Services.Repositories.Perforce;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// 增量更新端点日志类（用于泛型日志记录器）
/// </summary>
public class IncrementalUpdateEndpointsLogger { }

/// <summary>
/// 增量更新 API 端点
/// 提供手动触发增量更新、查询任务状态和重试失败任务的功能
/// </summary>
public static class IncrementalUpdateEndpoints
{
    /// <summary>
    /// 注册所有增量更新相关端点
    /// </summary>
    public static IEndpointRouteBuilder MapIncrementalUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // 仓库增量更新触发端点
        var repoGroup = app.MapGroup("/api/v1/repositories")
            .WithTags("增量更新");

        repoGroup.MapPost("/{repositoryId}/branches/{branchId}/incremental-update", TriggerIncrementalUpdateAsync)
            .WithName("TriggerIncrementalUpdate")
            .WithSummary("手动触发增量更新")
            .WithDescription("为指定仓库和分支创建一个高优先级的增量更新任务");

        repoGroup.MapPost("/{repositoryId}/branches/{branchId}/incremental-update/external", TriggerExternalIncrementalUpdateAsync)
            .WithName("TriggerExternalIncrementalUpdate")
            .WithSummary("外部注入变更触发增量更新")
            .WithDescription("由外部(如 Perforce CI)提交变更文件列表与目标版本(changelist 号)，创建高优先级增量更新任务");

        repoGroup.MapPost("/{repositoryId}/branches/{branchId}/incremental-update/perforce-event", TriggerPerforceEventAsync)
            .WithName("TriggerPerforceIncrementalEvent")
            .WithSummary("触发 Perforce changelist 区间增量更新")
            .WithDescription("只提交可选的最新 changelist；服务端查询区间、过滤 CL/文件，并复用外部增量任务管线");

        // 增量更新任务管理端点
        var taskGroup = app.MapGroup("/api/v1/incremental-updates")
            .WithTags("增量更新任务");

        taskGroup.MapGet("/{taskId}", GetTaskStatusAsync)
            .WithName("GetIncrementalUpdateTaskStatus")
            .WithSummary("获取任务状态")
            .WithDescription("获取指定增量更新任务的详细状态");

        taskGroup.MapPost("/{taskId}/retry", RetryFailedTaskAsync)
            .WithName("RetryFailedIncrementalUpdateTask")
            .WithSummary("重试失败任务")
            .WithDescription("重试一个失败的增量更新任务");

        return app;
    }

    /// <summary>
    /// Perforce 二期轻量事件入口。
    /// POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update/perforce-event
    /// </summary>
    private static async Task<IResult> TriggerPerforceEventAsync(
        string repositoryId,
        string branchId,
        [FromBody] PerforceIncrementalEventRequest? request,
        [FromServices] IPerforceIncrementalEventService eventService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        var (authorizationResult, _) = await AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        var branchExists = await context.RepositoryBranches
            .AsNoTracking()
            .AnyAsync(
                branch => branch.Id == branchId && branch.RepositoryId == repositoryId && !branch.IsDeleted,
                cancellationToken);
        if (!branchExists)
        {
            return Results.NotFound(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = "分支不存在",
                ErrorCode = "BRANCH_NOT_FOUND"
            });
        }

        try
        {
            var result = await eventService.TriggerAsync(
                repositoryId,
                branchId,
                request?.LatestChangelist,
                cancellationToken);

            logger.LogInformation(
                "Perforce incremental event completed. RepositoryId: {RepositoryId}, BranchId: {BranchId}, Action: {Action}, TargetCL: {TargetCL}, TaskId: {TaskId}",
                repositoryId, branchId, result.Action, result.TargetChangelist, result.TaskId ?? "none");

            return Results.Ok(new PerforceIncrementalEventResponse
            {
                Success = true,
                Action = result.Action.ToString(),
                TargetChangelist = result.TargetChangelist.ToString(),
                TaskId = result.TaskId,
                TotalChangelists = result.TotalChangelists,
                IncludedChangelists = result.IncludedChangelists,
                InspectedFiles = result.InspectedFiles,
                IncludedFiles = result.IncludedFiles,
                Changes = result.Changes?.Select(change => new PerforceLogicalChangeResponse
                {
                    Changelist = change.Changelist.ToString(),
                    Action = change.Action,
                    OldPath = change.OldWorkspaceRelativePath,
                    NewPath = change.NewWorkspaceRelativePath
                }).ToList() ?? [],
                ImpactPlan = result.ImpactPlan,
                Message = result.Message
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid Perforce event request. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            return Results.BadRequest(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = "INVALID_REQUEST"
            });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogWarning(ex, "Perforce event resource not found. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            var errorCode = ex.Message.Contains("分支", StringComparison.Ordinal)
                ? "BRANCH_NOT_FOUND"
                : "REPOSITORY_NOT_FOUND";
            return Results.NotFound(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = errorCode
            });
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.LogWarning(ex, "Perforce workspace missing. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            return Results.BadRequest(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = "WORKSPACE_NOT_FOUND"
            });
        }
        catch (PerforceCommandException ex)
        {
            logger.LogError(ex, "Perforce command failed. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "Perforce 查询失败",
                    ErrorCode = "PERFORCE_COMMAND_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Perforce event could not be queued. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            // Only true queue / generation conflicts map to 409. Full-gen enqueue failures are 500.
            if (ex.Message.Contains("无法创建全量任务", StringComparison.Ordinal))
            {
                return Results.Json(
                    new IncrementalUpdateErrorResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        ErrorCode = "FULL_GENERATION_ENQUEUE_FAILED"
                    },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var errorCode = ex.Message.Contains("full generation", StringComparison.OrdinalIgnoreCase)
                ? "GENERATION_IN_PROGRESS"
                : "UPDATE_CONFLICT";
            return Results.Conflict(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = errorCode
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process Perforce event. RepositoryId: {RepositoryId}, BranchId: {BranchId}", repositoryId, branchId);
            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "处理 Perforce 增量事件失败",
                    ErrorCode = "PERFORCE_EVENT_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 手动触发增量更新
    /// POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update
    /// </summary>
    private static async Task<IResult> TriggerIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        [FromServices] IIncrementalUpdateService updateService,
        [FromServices] IContext context,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Manual incremental update requested. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
            repositoryId, branchId);

        try
        {
            // 验证仓库是否存在
            var repository = await context.Repositories
                .FirstOrDefaultAsync(r => r.Id == repositoryId, cancellationToken);

            if (repository == null)
            {
                logger.LogWarning("Repository not found. RepositoryId: {RepositoryId}", repositoryId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "仓库不存在",
                    ErrorCode = "REPOSITORY_NOT_FOUND"
                });
            }

            // 验证分支是否存在
            var branch = await context.RepositoryBranches
                .FirstOrDefaultAsync(b => b.Id == branchId && b.RepositoryId == repositoryId, cancellationToken);

            if (branch == null)
            {
                logger.LogWarning("Branch not found. BranchId: {BranchId}", branchId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "分支不存在",
                    ErrorCode = "BRANCH_NOT_FOUND"
                });
            }

            // 触发增量更新
            var taskId = await updateService.TriggerManualUpdateAsync(repositoryId, branchId, cancellationToken);

            // 获取任务状态
            var task = await context.IncrementalUpdateTasks
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            logger.LogInformation(
                "Incremental update task created/found. TaskId: {TaskId}, Status: {Status}",
                taskId, task?.Status);

            return Results.Ok(new TriggerIncrementalUpdateResponse
            {
                Success = true,
                TaskId = taskId,
                Status = task?.Status.ToString() ?? "Unknown",
                Message = task?.Status == IncrementalUpdateStatus.Processing
                    ? "任务正在处理中"
                    : "增量更新任务已创建"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to trigger incremental update. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                repositoryId, branchId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "触发增量更新失败",
                    ErrorCode = "TRIGGER_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }


    /// <summary>
    /// 外部注入变更触发增量更新
    /// POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update/external
    /// </summary>
    private static async Task<IResult> TriggerExternalIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        [FromBody] ExternalIncrementalUpdateRequest request,
        [FromServices] IIncrementalUpdateService updateService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "External incremental update requested. RepositoryId: {RepositoryId}, BranchId: {BranchId}, TargetRevision: {TargetRevision}, ChangedFiles: {Count}",
            repositoryId, branchId, request?.TargetRevision, request?.ChangedFiles?.Count ?? 0);

        if (request?.ChangedFiles == null)
        {
            return Results.BadRequest(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = "changedFiles 不能为空",
                ErrorCode = "INVALID_REQUEST"
            });
        }

        // 该端点写入高影响内容(变更列表进入 LLM 提示、可推进版本基线)，必须校验调用方为
        // 仓库所有者或管理员，避免匿名者知道 repo/branch ID 即可注入任意变更。
        // 鉴权时已加载并校验仓库存在，直接复用该实例，避免对同一行重复查询。
        var (authorizationResult, repository) = await AuthorizeRepositoryMutationAsync(
            context, userContext, repositoryId, cancellationToken);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        try
        {
            var branch = await context.RepositoryBranches
                .FirstOrDefaultAsync(b => b.Id == branchId && b.RepositoryId == repositoryId && !b.IsDeleted, cancellationToken);

            if (branch == null)
            {
                logger.LogWarning("Branch not found. BranchId: {BranchId}", branchId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "分支不存在",
                    ErrorCode = "BRANCH_NOT_FOUND"
                });
            }

            // 服务层会再次校验 Perforce 源 / 版本长度 / 单调性；端点先拦一层以便返回明确 400。
            if (!RepositorySource.IsPerforce(repository!.GitUrl))
            {
                return Results.BadRequest(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "外部注入增量仅支持 Perforce 源仓库",
                    ErrorCode = "INVALID_SOURCE_TYPE"
                });
            }

            if (!string.IsNullOrWhiteSpace(request.TargetRevision)
                && request.TargetRevision.Length > IncrementalUpdateService.MaxRevisionIdLength)
            {
                return Results.BadRequest(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = $"targetRevision 长度不能超过 {IncrementalUpdateService.MaxRevisionIdLength} 字符",
                    ErrorCode = "INVALID_TARGET_REVISION"
                });
            }

            var taskId = await updateService.TriggerExternalUpdateAsync(
                repositoryId,
                branchId,
                request.TargetRevision,
                request.ChangedFiles,
                request.DeletedFiles,
                cancellationToken: cancellationToken);

            var task = await context.IncrementalUpdateTasks
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            logger.LogInformation(
                "External incremental update task created/reused. TaskId: {TaskId}, Status: {Status}",
                taskId, task?.Status);

            return Results.Ok(new TriggerIncrementalUpdateResponse
            {
                Success = true,
                TaskId = taskId,
                Status = task?.Status.ToString() ?? "Unknown",
                Message = "外部增量更新任务已创建"
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex,
                "External incremental update rejected. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                repositoryId, branchId);

            return Results.BadRequest(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = "INVALID_REQUEST"
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex,
                "External incremental update conflict. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                repositoryId, branchId);

            var errorCode = ex.Message.Contains("full generation", StringComparison.Ordinal)
                ? "GENERATION_IN_PROGRESS"
                : "UPDATE_IN_PROGRESS";

            return Results.Conflict(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = ex.Message,
                ErrorCode = errorCode
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to trigger external incremental update. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                repositoryId, branchId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "触发外部增量更新失败",
                    ErrorCode = "TRIGGER_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 校验当前调用方是否有权对该仓库执行写操作(仓库所有者或管理员)。
    /// 返回 Error 非 null 表示鉴权/存在性校验未通过的响应；Error 为 null 时 Repository 为已加载的仓库实例，供调用方复用。
    /// </summary>
    private static async Task<(IResult? Error, Entities.Repository? Repository)> AuthorizeRepositoryMutationAsync(
        IContext context,
        IUserContext userContext,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrWhiteSpace(userContext.UserId))
        {
            return (Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "请先登录",
                    ErrorCode = "UNAUTHORIZED"
                },
                statusCode: StatusCodes.Status401Unauthorized), null);
        }

        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);

        if (repository == null)
        {
            return (Results.NotFound(new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = "仓库不存在",
                ErrorCode = "REPOSITORY_NOT_FOUND"
            }), null);
        }

        if (repository.OwnerUserId == userContext.UserId || userContext.User?.IsInRole("Admin") == true)
        {
            return (null, repository);
        }

        return (Results.Json(
            new IncrementalUpdateErrorResponse
            {
                Success = false,
                Error = "无权限操作该仓库",
                ErrorCode = "FORBIDDEN"
            },
            statusCode: StatusCodes.Status403Forbidden), null);
    }

    /// <summary>
    /// 获取任务状态
    /// GET /api/v1/incremental-updates/{taskId}
    /// </summary>
    private static async Task<IResult> GetTaskStatusAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("Getting task status. TaskId: {TaskId}", taskId);

        try
        {
            var task = await context.IncrementalUpdateTasks
                .Include(t => t.Repository)
                .Include(t => t.Branch)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                logger.LogWarning("Task not found. TaskId: {TaskId}", taskId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "任务不存在",
                    ErrorCode = "TASK_NOT_FOUND"
                });
            }

            return Results.Ok(new IncrementalUpdateTaskResponse
            {
                Success = true,
                TaskId = task.Id,
                RepositoryId = task.RepositoryId,
                RepositoryName = task.Repository != null
                    ? $"{task.Repository.OrgName}/{task.Repository.RepoName}"
                    : null,
                BranchId = task.BranchId,
                BranchName = task.Branch?.BranchName,
                Status = task.Status.ToString(),
                Priority = task.Priority,
                IsManualTrigger = task.IsManualTrigger,
                PreviousCommitId = task.PreviousCommitId,
                TargetCommitId = task.TargetCommitId,
                RetryCount = task.RetryCount,
                ErrorMessage = task.ErrorMessage,
                CreatedAt = task.CreatedAt,
                StartedAt = task.StartedAt,
                CompletedAt = task.CompletedAt,
                ImpactPlan = IncrementalImpactPlanSerializer.Deserialize(task.ImpactPlanJson)
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get task status. TaskId: {TaskId}", taskId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "获取任务状态失败",
                    ErrorCode = "GET_STATUS_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 重试失败任务
    /// POST /api/v1/incremental-updates/{taskId}/retry
    /// </summary>
    private static async Task<IResult> RetryFailedTaskAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Retry requested for task. TaskId: {TaskId}", taskId);

        try
        {
            var task = await context.IncrementalUpdateTasks
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                logger.LogWarning("Task not found. TaskId: {TaskId}", taskId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "任务不存在",
                    ErrorCode = "TASK_NOT_FOUND"
                });
            }

            // 只能重试失败的任务
            if (task.Status != IncrementalUpdateStatus.Failed)
            {
                logger.LogWarning(
                    "Cannot retry task with status {Status}. TaskId: {TaskId}",
                    task.Status, taskId);

                return Results.BadRequest(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = $"只能重试失败的任务，当前状态: {task.Status}",
                    ErrorCode = "INVALID_TASK_STATUS"
                });
            }

            // 重置任务状态
            task.Status = IncrementalUpdateStatus.Pending;
            task.RetryCount++;
            task.ErrorMessage = null;
            task.StartedAt = null;
            task.CompletedAt = null;
            task.UpdatedAt = DateTime.UtcNow;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Task reset for retry. TaskId: {TaskId}, RetryCount: {RetryCount}",
                taskId, task.RetryCount);

            return Results.Ok(new RetryTaskResponse
            {
                Success = true,
                TaskId = task.Id,
                Status = task.Status.ToString(),
                RetryCount = task.RetryCount,
                Message = "任务已重置，将在下次轮询时重新处理"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retry task. TaskId: {TaskId}", taskId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "重试任务失败",
                    ErrorCode = "RETRY_FAILED",
                    Details = ex.Message
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}


#region 请求模型

/// <summary>
/// 外部注入增量更新请求(如 Perforce CI)
/// </summary>
public class ExternalIncrementalUpdateRequest
{
    /// <summary>
    /// 目标版本标识(如 Perforce changelist 号)。
    /// </summary>
    public string? TargetRevision { get; set; }

    /// <summary>
    /// 新增/修改的文件相对路径列表。
    /// </summary>
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>
    /// 删除的文件相对路径列表(当前引擎不处理删除，仅记录)。
    /// </summary>
    public List<string>? DeletedFiles { get; set; }
}

/// <summary>
/// Perforce 二期轻量事件。省略 latestChangelist 时由服务端查询工作区 #have。
/// </summary>
public sealed class PerforceIncrementalEventRequest
{
    public string? LatestChangelist { get; set; }
}

#endregion

#region 响应模型

/// <summary>
/// 触发增量更新响应
/// </summary>
public class TriggerIncrementalUpdateResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

public sealed class PerforceIncrementalEventResponse
{
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetChangelist { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public int TotalChangelists { get; set; }
    public int IncludedChangelists { get; set; }
    public int InspectedFiles { get; set; }
    public int IncludedFiles { get; set; }
    public List<PerforceLogicalChangeResponse> Changes { get; set; } = [];
    public IncrementalImpactPlan? ImpactPlan { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class PerforceLogicalChangeResponse
{
    public string Changelist { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? OldPath { get; set; }
    public string? NewPath { get; set; }
}

/// <summary>
/// 增量更新任务详情响应
/// </summary>
public class IncrementalUpdateTaskResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 仓库ID
    /// </summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>
    /// 仓库名称 (org/repo)
    /// </summary>
    public string? RepositoryName { get; set; }

    /// <summary>
    /// 分支ID
    /// </summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>
    /// 分支名称
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 任务优先级
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 是否为手动触发
    /// </summary>
    public bool IsManualTrigger { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 目标 Commit ID
    /// </summary>
    public string? TargetCommitId { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 开始处理时间
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>T5.2 可解释影响计划。</summary>
    public IncrementalImpactPlan? ImpactPlan { get; set; }
}

/// <summary>
/// 重试任务响应
/// </summary>
public class RetryTaskResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 增量更新错误响应
/// </summary>
public class IncrementalUpdateErrorResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// 错误代码
    /// </summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// 详细信息
    /// </summary>
    public string? Details { get; set; }
}

#endregion
