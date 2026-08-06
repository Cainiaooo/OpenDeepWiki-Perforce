using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Scope 配置变更审计。
/// </summary>
public class RepositoryScopeAuditLog : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    public int? PreviousConfigurationVersion { get; set; }

    public int ConfigurationVersion { get; set; }

    [StringLength(64)]
    public string? PreviousContentHash { get; set; }

    [StringLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    [StringLength(36)]
    public string? ActorUserId { get; set; }

    [StringLength(100)]
    public string Action { get; set; } = "Update";

    /// <summary>
    /// 影响预览 JSON（新增/移除候选、失效页面等）。
    /// </summary>
    public string? ImpactPreviewJson { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }
}
