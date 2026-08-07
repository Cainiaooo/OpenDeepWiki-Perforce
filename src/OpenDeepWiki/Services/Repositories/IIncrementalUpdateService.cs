namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// 增量更新服务接口
/// 封装增量更新的核心业务逻辑
/// </summary>
public interface IIncrementalUpdateService
{
    /// <summary>
    /// 处理单个仓库的增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="externalChangedFilesJson">外部注入的变更文件列表(JSON 数组)，非空时优先于内部 diff。</param>
    /// <param name="externalTargetRevision">外部注入的目标版本标识(如 Perforce changelist 号)。</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新结果</returns>
    Task<IncrementalUpdateResult> ProcessIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        string? externalChangedFilesJson = null,
        string? externalTargetRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查仓库是否需要增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否需要更新及变更信息</returns>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动触发增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>创建的任务ID</returns>
    Task<string> TriggerManualUpdateAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 外部(如 Perforce CI)注入变更文件列表以触发增量更新。
    /// 创建一个高优先级、携带外部变更列表与目标版本的增量更新任务。
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="targetRevision">目标版本标识(如 Perforce changelist 号)。</param>
    /// <param name="changedFiles">新增/修改的文件相对路径列表。</param>
    /// <param name="deletedFiles">删除的文件相对路径列表(当前引擎不处理删除，仅记录)。</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="impactPlanJson">T5.2 影响分级计划 JSON；手动兼容入口可为空。</param>
    /// <returns>创建或复用的任务ID</returns>
    Task<string> TriggerExternalUpdateAsync(
        string repositoryId,
        string branchId,
        string? targetRevision,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string>? deletedFiles = null,
        CancellationToken cancellationToken = default,
        string? impactPlanJson = null);
}

/// <summary>
/// 增量更新结果
/// </summary>
public class IncrementalUpdateResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 当前 Commit ID
    /// </summary>
    public string? CurrentCommitId { get; set; }

    /// <summary>
    /// 变更文件数量
    /// </summary>
    public int ChangedFilesCount { get; set; }

    /// <summary>
    /// 更新的文档数量
    /// </summary>
    public int UpdatedDocumentsCount { get; set; }

    /// <summary>
    /// 处理耗时
    /// </summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// 更新检查结果
/// </summary>
public class UpdateCheckResult
{
    /// <summary>
    /// 是否需要更新
    /// </summary>
    public bool NeedsUpdate { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 当前 Commit ID
    /// </summary>
    public string? CurrentCommitId { get; set; }

    /// <summary>
    /// 变更文件列表
    /// </summary>
    public string[]? ChangedFiles { get; set; }
}
