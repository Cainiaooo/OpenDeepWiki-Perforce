using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// BranchLanguage 的当前已发布 Generation 指针。
/// 新表关联上游 BranchLanguage，避免改动其既有唯一约束。
/// </summary>
public class BranchLanguagePublication : AggregateRoot<string>
{
    [Required]
    [StringLength(36)]
    public string BranchLanguageId { get; set; } = string.Empty;

    /// <summary>
    /// 当前已发布主体 Generation。
    /// </summary>
    [StringLength(36)]
    public string? CurrentGenerationId { get; set; }

    /// <summary>
    /// 衍生产物（翻译/思维导图/Graphify）对齐的 Generation。
    /// 允许异步落后；读取端应识别落后并明示或降级。
    /// </summary>
    [StringLength(36)]
    public string? DerivativeSourceGenerationId { get; set; }

    public DateTime? PublishedAt { get; set; }

    [ForeignKey(nameof(BranchLanguageId))]
    public virtual BranchLanguage? BranchLanguage { get; set; }

    [ForeignKey(nameof(CurrentGenerationId))]
    public virtual WikiGeneration? CurrentGeneration { get; set; }
}
