using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 增量更新任务状态
/// </summary>
public enum IncrementalUpdateStatus
{
    /// <summary>
    /// 等待处理
    /// </summary>
    Pending = 0,

    /// <summary>
    /// 正在处理
    /// </summary>
    Processing = 1,

    /// <summary>
    /// 处理完成
    /// </summary>
    Completed = 2,

    /// <summary>
    /// 处理失败
    /// </summary>
    Failed = 3,

    /// <summary>
    /// 已取消
    /// </summary>
    Cancelled = 4
}

/// <summary>
/// 增量更新任务实体
/// 跟踪增量更新任务的状态
/// </summary>
public class IncrementalUpdateTask : AggregateRoot<string>
{
    /// <summary>
    /// 仓库ID
    /// </summary>
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>
    /// 分支ID
    /// </summary>
    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    [StringLength(40)]
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 当前目标 Commit ID
    /// </summary>
    [StringLength(40)]
    public string? TargetCommitId { get; set; }

    /// <summary>
    /// 外部（如 Perforce CI）注入的变更文件相对路径列表，JSON 数组；非空时优先于内部 diff。
    /// </summary>
    public string? ExternalChangedFiles { get; set; }

    /// <summary>
    /// 外部注入的目标版本标识（如 Perforce changelist 号）。
    /// 长度与 <see cref="TargetCommitId"/> / <c>RepositoryBranch.LastCommitId</c> 对齐为 40，
    /// 避免推进基线时因列长不一致在 PostgreSQL 上写入失败。
    /// </summary>
    [StringLength(40)]
    public string? ExternalTargetRevision { get; set; }

    /// <summary>
    /// T5.2 可解释影响计划 JSON。包含请求的最小范围、实际执行范围和稳定 reason code。
    /// </summary>
    public string? ImpactPlanJson { get; set; }

    /// <summary>
    /// 任务状态
    /// </summary>
    public IncrementalUpdateStatus Status { get; set; } = IncrementalUpdateStatus.Pending;

    /// <summary>
    /// 任务优先级 (数值越大优先级越高)
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    /// 是否为手动触发
    /// </summary>
    public bool IsManualTrigger { get; set; } = false;

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 开始处理时间
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 仓库导航属性
    /// </summary>
    [ForeignKey("RepositoryId")]
    public virtual Repository? Repository { get; set; }

    /// <summary>
    /// 分支导航属性
    /// </summary>
    [ForeignKey("BranchId")]
    public virtual RepositoryBranch? Branch { get; set; }
}
