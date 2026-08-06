using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Wiki 发布世代。主体 Catalog/DocFile 以 BranchLanguage 当前指针发布；
/// 首期采用 staging 标记 + 校验通过后事务性翻转，不做完整双缓冲。
/// </summary>
public class WikiGeneration : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string BranchLanguageId { get; set; } = string.Empty;

    public WikiGenerationStatus Status { get; set; } = WikiGenerationStatus.Staging;

    /// <summary>
    /// Scope 配置版本。
    /// </summary>
    public int? ScopeConfigurationVersion { get; set; }

    [StringLength(64)]
    public string? ScopeContentHash { get; set; }

    /// <summary>
    /// 目标 changelist / commit。
    /// </summary>
    [StringLength(40)]
    public string? TargetRevision { get; set; }

    /// <summary>
    /// Tracked/#have manifest 摘要。
    /// </summary>
    [StringLength(64)]
    public string? TrackedManifestHash { get; set; }

    /// <summary>
    /// 生成引擎版本标识。
    /// </summary>
    [StringLength(64)]
    public string? GenerationEngineVersion { get; set; }

    /// <summary>
    /// SnapshotIdentity 稳定字符串（不含 Language；发布身份 = SnapshotIdentity + Language）。
    /// </summary>
    [StringLength(500)]
    public string? SnapshotIdentity { get; set; }

    /// <summary>
    /// 发布身份 = SnapshotIdentity + LanguageCode。
    /// </summary>
    [StringLength(550)]
    public string? PublicationIdentity { get; set; }

    [StringLength(50)]
    public string? LanguageCode { get; set; }

    public string? ManifestJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public DateTime? FailedAt { get; set; }

    /// <summary>
    /// 关联的全量/增量任务 ID（可选）。
    /// </summary>
    [StringLength(36)]
    public string? OwnerTaskId { get; set; }

    [StringLength(50)]
    public string? OwnerTaskType { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }

    [ForeignKey(nameof(BranchId))]
    public virtual RepositoryBranch? Branch { get; set; }

    [ForeignKey(nameof(BranchLanguageId))]
    public virtual BranchLanguage? BranchLanguage { get; set; }
}

public enum WikiGenerationStatus
{
    Staging = 0,
    Published = 1,
    Failed = 2,
    Superseded = 3,
    Abandoned = 4
}
