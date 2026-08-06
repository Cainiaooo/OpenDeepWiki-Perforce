using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// UE Knowledge Package 摄取记录。导出器属于目标项目仓库；本实体仅保存已验证的事实包元数据与索引。
/// </summary>
public class UeKnowledgePackage : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string BranchId { get; set; } = string.Empty;

    /// <summary>
    /// 是否为该分支当前可用包（兼容 Build CL 的最新完整/部分包）。
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// 包是否因 schema/Build CL 不兼容而被标记为 stale（仍可查询，但不得当作最新）。
    /// </summary>
    public bool IsStale { get; set; }

    public UeKnowledgePackageStatus Status { get; set; } = UeKnowledgePackageStatus.Validated;

    public UeKnowledgeCompleteness Completeness { get; set; } = UeKnowledgeCompleteness.Complete;

    /// <summary>
    /// Package schema major.minor，例如 1.0。
    /// </summary>
    [Required]
    [StringLength(20)]
    public string SchemaVersion { get; set; } = "1.0";

    [Required]
    [StringLength(64)]
    public string ExporterVersion { get; set; } = string.Empty;

    [Required]
    [StringLength(120)]
    public string ProjectIdentity { get; set; } = string.Empty;

    [StringLength(120)]
    public string? BranchName { get; set; }

    /// <summary>
    /// UE Build / Perforce changelist 身份。
    /// </summary>
    [Required]
    [StringLength(40)]
    public string BuildChangelist { get; set; } = string.Empty;

    [StringLength(80)]
    public string? EngineVersion { get; set; }

    [StringLength(80)]
    public string? TargetPlatform { get; set; }

    [StringLength(40)]
    public string? ExportSource { get; set; }

    /// <summary>
    /// 整个包的确定性逻辑摘要（manifest + 已排序分片 digest）。
    /// </summary>
    [Required]
    [StringLength(64)]
    public string PackageDigest { get; set; } = string.Empty;

    /// <summary>
    /// 语义事实摘要，用于语义 diff（忽略导出时间等非语义字段）。
    /// </summary>
    [Required]
    [StringLength(64)]
    public string SemanticDigest { get; set; } = string.Empty;

    /// <summary>
    /// 验证通过后持久化的 manifest JSON。
    /// </summary>
    [Required]
    public string ManifestJson { get; set; } = string.Empty;

    /// <summary>
    /// 预计算事实索引 JSON，供生成与 MCP 查询。
    /// </summary>
    public string? FactIndexJson { get; set; }

    /// <summary>
    /// 可选：包根目录相对或受控绝对路径（仅记录，不自动同步）。
    /// </summary>
    [StringLength(500)]
    public string? PackageRootPath { get; set; }

    public string? ValidationWarningsJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? ExportedAtUtc { get; set; }

    public DateTime? IngestedAtUtc { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }

    [ForeignKey(nameof(BranchId))]
    public virtual RepositoryBranch? Branch { get; set; }
}

public enum UeKnowledgePackageStatus
{
    Validated = 0,
    Partial = 1,
    Rejected = 2,
    Superseded = 3
}

public enum UeKnowledgeCompleteness
{
    Complete = 0,
    Partial = 1,
    Truncated = 2
}
