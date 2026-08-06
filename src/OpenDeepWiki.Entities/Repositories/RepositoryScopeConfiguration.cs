using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 版本化仓库 Scope 配置。每个保存动作产生新版本；当前生效版本由 IsCurrent 标记。
/// </summary>
public class RepositoryScopeConfiguration : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>
    /// 单调递增配置版本号，从 1 开始。
    /// </summary>
    public int ConfigurationVersion { get; set; }

    /// <summary>
    /// 规范化配置 JSON（schemaVersion、documentScopes、contextScope 等）。
    /// </summary>
    [Required]
    public string ConfigurationJson { get; set; } = string.Empty;

    /// <summary>
    /// 规范化配置的稳定内容摘要（SHA-256 hex）。
    /// </summary>
    [Required]
    [StringLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// 是否为当前生效配置。
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Scope 语义变化时为 true，要求重新索引/全量重建。
    /// </summary>
    public bool ReindexRequired { get; set; }

    /// <summary>
    /// 迁移提示：无 Scope 的仓库返回兼容行为时使用。
    /// </summary>
    public bool IsLegacyFallback { get; set; }

    [StringLength(36)]
    public string? CreatedByUserId { get; set; }

    [StringLength(200)]
    public string? ChangeSummary { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }
}
