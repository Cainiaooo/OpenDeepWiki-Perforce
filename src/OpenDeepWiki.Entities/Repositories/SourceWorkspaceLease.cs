using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// 工作区租约：禁止同步与生成并发修改同一工作区。
/// </summary>
public class SourceWorkspaceLease : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string Purpose { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string OwnerId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? OwnerDescription { get; set; }

    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }
}
