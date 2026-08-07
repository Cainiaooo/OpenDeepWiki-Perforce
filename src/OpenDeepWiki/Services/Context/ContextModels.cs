using System.Text.Json.Serialization;

namespace OpenDeepWiki.Services.Context;

/// <summary>
/// 版本兼容状态。Rejected 时必须返回空 items 与拒绝原因。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextCompatibility
{
    Exact = 0,
    CompatibleFallback = 1,
    Stale = 2,
    Unknown = 3,
    Rejected = 4
}

/// <summary>
/// 覆盖/完整性状态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextCoverageStatus
{
    Complete = 0,
    Partial = 1,
    CompletedWithCoverageWarnings = 2,
    Unknown = 3,
    None = 4
}

/// <summary>
/// 知识条目种类。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextItemKind
{
    WikiPage = 0,
    SourceCitation = 1,
    UeFact = 2,
    McpToolContract = 3,
    Constraint = 4,
    TestHint = 5,
    LiveQueryRequired = 6,
    Gap = 7,
    ModuleOverview = 8,
    Workflow = 9
}

/// <summary>
/// 知识可信度标记。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextEvidenceKind
{
    Document = 0,
    ContextOnly = 1,
    AiSynthesis = 2,
    HumanIntent = 3,
    UeExport = 4,
    LiveUnknown = 5
}

/// <summary>
/// 编辑器操作风险级别：写操作采用更严格版本门禁。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EditorOperationRiskLevel
{
    Query = 0,
    Suggest = 1,
    Write = 2
}

/// <summary>
/// Persona 影响排序，不改变事实实体。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextPersona
{
    CodeReview = 0,
    EditorAgent = 1,
    Onboarding = 2,
    General = 3
}

/// <summary>
/// 统一 Context Envelope。所有面向 Agent 的响应共用此封装。
/// </summary>
public sealed class ContextEnvelope
{
    public string SchemaVersion { get; init; } = ContextSchema.CurrentVersion;

    public required string RepositoryId { get; init; }

    public string? RepositoryFullName { get; init; }

    public required string Branch { get; init; }

    public string? RequestedRevision { get; init; }

    public string? ResolvedSnapshotId { get; init; }

    public string? ResolvedTargetRevision { get; init; }

    public string? SnapshotIdentity { get; init; }

    public string? LanguageCode { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContextCompatibility Compatibility { get; init; } = ContextCompatibility.Unknown;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContextCoverageStatus CoverageStatus { get; init; } = ContextCoverageStatus.Unknown;

    public string Persona { get; init; } = nameof(ContextPersona.General);

    public IReadOnlyList<ContextItem> Items { get; init; } = [];

    public IReadOnlyList<ContextCitation> Citations { get; init; } = [];

    public IReadOnlyList<ContextWarning> Warnings { get; init; } = [];

    public ContextBudgetInfo? Budget { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Rejected 时强制 items 为空。
    /// </summary>
    public static ContextEnvelope Rejected(
        string repositoryId,
        string branch,
        string? requestedRevision,
        string reasonCode,
        string message,
        string? repositoryFullName = null,
        string? languageCode = null,
        ContextPersona persona = ContextPersona.General)
    {
        return new ContextEnvelope
        {
            RepositoryId = repositoryId,
            RepositoryFullName = repositoryFullName,
            Branch = branch,
            RequestedRevision = requestedRevision,
            Compatibility = ContextCompatibility.Rejected,
            CoverageStatus = ContextCoverageStatus.None,
            LanguageCode = languageCode,
            Persona = persona.ToString(),
            Items = [],
            Citations = [],
            Warnings =
            [
                new ContextWarning
                {
                    ReasonCode = reasonCode,
                    Message = message,
                    Severity = "error"
                }
            ]
        };
    }
}

public sealed class ContextItem
{
    public required string Id { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ContextItemKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    public string? Content { get; init; }

    public string? Path { get; init; }

    public string? DomainId { get; init; }

    public string? ModuleId { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContextEvidenceKind EvidenceKind { get; init; } = ContextEvidenceKind.Document;

    /// <summary>为什么选择这条知识。</summary>
    public string? SelectionReasonCode { get; init; }

    public double Score { get; init; }

    public IReadOnlyList<string> RelatedPaths { get; init; } = [];

    public IReadOnlyList<string> CitationIds { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

public sealed class ContextCitation
{
    public required string Id { get; init; }

    /// <summary>wiki_page | source_file | ue_fact | external</summary>
    public required string SourceKind { get; init; }

    public required string Label { get; init; }

    public string? Path { get; init; }

    public string? SnapshotId { get; init; }

    public string? Fragment { get; init; }

    public string? ScopeKind { get; init; }
}

public sealed class ContextWarning
{
    public required string ReasonCode { get; init; }

    public required string Message { get; init; }

    /// <summary>info | warning | error</summary>
    public string Severity { get; init; } = "warning";

    public string? RelatedPath { get; init; }
}

public sealed class ContextBudgetInfo
{
    public int MaxItems { get; init; }

    public int MaxChars { get; init; }

    public int ReturnedItems { get; init; }

    public int ReturnedChars { get; init; }

    public bool Truncated { get; init; }
}

public static class ContextSchema
{
    public const string CurrentVersion = "1.0";
}

/// <summary>
/// 稳定 reason code，供告警、测试与质量评估使用。
/// </summary>
public static class ContextReasonCodes
{
    public const string ExactMatch = "snapshot.exact";
    public const string CompatibleFallback = "snapshot.compatible_fallback";
    public const string StaleSnapshot = "snapshot.stale";
    public const string UnknownSnapshot = "snapshot.unknown";
    public const string RejectedNoPublication = "snapshot.rejected.no_publication";
    public const string RejectedFutureRevision = "snapshot.rejected.future_revision";
    public const string RejectedCrossBranch = "snapshot.rejected.cross_branch";
    public const string RejectedWriteRequiresExact = "snapshot.rejected.write_requires_exact";
    public const string RejectedIncompatibleContract = "snapshot.rejected.incompatible_contract";
    public const string RejectedRepositoryNotFound = "auth.repository_not_found";
    public const string RejectedBranchNotFound = "auth.branch_not_found";
    public const string RejectedLanguageNotFound = "auth.language_not_found";

    public const string FileMappedToPage = "file.mapped_page";
    public const string FileOutsideDocumentScope = "file.outside_document_scope";
    public const string FileDeletedHistory = "file.deleted_history";
    public const string FileMoved = "file.moved";
    public const string FileNoEvidence = "file.no_evidence";
    public const string PathPrefixMatch = "match.path_prefix";
    public const string TitleMatch = "match.title";
    public const string SourceFilesMatch = "match.source_files";
    public const string ModulePathMatch = "match.module_path";
    public const string UeFactMatch = "match.ue_fact";
    public const string LiveQueryRequired = "live.query_required";
    public const string CoverageWarning = "coverage.warning";
    public const string BudgetTruncated = "budget.truncated";
    public const string GapInsufficientEvidence = "gap.insufficient_evidence";
    public const string AuthorMetadataUntrusted = "safety.author_metadata_untrusted";
}

public sealed record SnapshotResolveOptions
{
    /// <summary>
    /// 允许向后兼容回退的最大 CL 距离（数字 CL）。默认 5000。
    /// </summary>
    public long MaxCompatibleFallbackDistance { get; init; } = 5000;

    /// <summary>
    /// 查询类任务允许返回 stale；写操作应关闭。
    /// </summary>
    public bool AllowStale { get; init; } = true;

    /// <summary>
    /// 写操作要求 Exact 匹配。
    /// </summary>
    public bool RequireExactForWrite { get; init; }

    /// <summary>
    /// 当未指定 requestedRevision 时使用当前发布快照。
    /// </summary>
    public bool AllowCurrentWhenRevisionOmitted { get; init; } = true;
}

public sealed class SnapshotResolveResult
{
    public ContextCompatibility Compatibility { get; init; }

    public string? GenerationId { get; init; }

    public string? TargetRevision { get; init; }

    public string? SnapshotIdentity { get; init; }

    public string? BranchLanguageId { get; init; }

    public string? LanguageCode { get; init; }

    public int? ScopeConfigurationVersion { get; init; }

    public string? TrackedManifestHash { get; init; }

    public IReadOnlyList<ContextWarning> Warnings { get; init; } = [];

    public bool IsRejected => Compatibility == ContextCompatibility.Rejected;
}

public sealed class BuildIdentity
{
    public string? ProjectId { get; init; }

    public string? Branch { get; init; }

    public string? BuildChangelist { get; init; }

    public string? BuildVersion { get; init; }

    public string? EngineVersion { get; init; }

    public string? TargetPlatform { get; init; }

    public string? UeMcpContractVersion { get; init; }
}

public sealed class ChangeReviewContextRequest
{
    public required string RepositoryId { get; init; }

    public string? BranchName { get; init; }

    public string? BaseChangelist { get; init; }

    public string? TargetChangelist { get; init; }

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<string> DeletedFiles { get; init; } = [];

    public IReadOnlyList<MovedFilePair> MovedFiles { get; init; } = [];

    public string? ReviewFocus { get; init; }

    public string LanguageCode { get; init; } = "zh";

    public int MaxItems { get; init; } = 20;

    public int MaxChars { get; init; } = 24_000;
}

public sealed class MovedFilePair
{
    public required string OldPath { get; init; }

    public required string NewPath { get; init; }
}

public sealed class EditorTaskContextRequest
{
    public required string RepositoryId { get; init; }

    public string? BranchName { get; init; }

    public required string TaskDescription { get; init; }

    public BuildIdentity? BuildIdentity { get; init; }

    public IReadOnlyList<string> CurrentAssetOrTypeHints { get; init; } = [];

    public EditorOperationRiskLevel RiskLevel { get; init; } = EditorOperationRiskLevel.Query;

    public string LanguageCode { get; init; } = "zh";

    public int MaxItems { get; init; } = 20;

    public int MaxChars { get; init; } = 24_000;

    public IReadOnlyList<string>? RequiredMcpToolNames { get; init; }
}

public sealed class ModuleOverviewRequest
{
    public required string RepositoryId { get; init; }

    public string? BranchName { get; init; }

    public required string ModuleOrDomainQuery { get; init; }

    public string? TargetChangelist { get; init; }

    public string LanguageCode { get; init; } = "zh";

    public int MaxItems { get; init; } = 15;

    public int MaxChars { get; init; } = 20_000;
}
