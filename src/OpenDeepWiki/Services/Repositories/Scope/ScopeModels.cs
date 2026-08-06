using System.Text.Json.Serialization;

namespace OpenDeepWiki.Services.Repositories.Scope;

/// <summary>
/// 工作区内容策略。默认只接受 tracked 且与 #have 一致的内容。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceContentPolicy
{
    SubmittedHaveOnly = 0,
    AllowOpenedFiles = 1
}

/// <summary>
/// 文件选择操作类型。
/// </summary>
public enum FileSelectionOperation
{
    DocumentCandidate = 0,
    ChangeTrigger = 1,
    ContextRead = 2,
    DirectoryPrune = 3
}

/// <summary>
/// 稳定 reason code，供预览、日志和测试使用。
/// </summary>
public static class FileSelectionReasonCodes
{
    public const string AcceptedDocument = "accepted.document";
    public const string AcceptedTrigger = "accepted.trigger";
    public const string AcceptedContext = "accepted.context";
    public const string AcceptedKeepDirectory = "accepted.keep_directory";

    public const string SafetyAbsolutePath = "safety.absolute_path";
    public const string SafetyUncPath = "safety.unc_path";
    public const string SafetyDriveLetter = "safety.drive_letter";
    public const string SafetyDotDot = "safety.dot_dot";
    public const string SafetySensitive = "safety.sensitive";
    public const string SafetySymlinkEscape = "safety.symlink_escape";

    public const string PathExcluded = "path.excluded";
    public const string PathNotIncluded = "path.not_included";
    public const string OutsideAnyScope = "scope.outside";
    public const string SuffixNotIncluded = "suffix.not_included";
    public const string ContextNotDocument = "scope.context_not_document";
    public const string OperationDenied = "operation.denied";
    public const string WorkspaceUntracked = "workspace.untracked";
    public const string WorkspaceOpened = "workspace.opened";
    public const string WorkspaceContentMismatch = "workspace.content_mismatch";
    public const string LegacyAccept = "legacy.accept";
    public const string PruneDirectory = "directory.prune";
}

/// <summary>
/// 可序列化的 Scope 配置文档（schemaVersion=1）。
/// </summary>
public sealed class ScopeConfigurationDocument
{
    public int SchemaVersion { get; set; } = 1;

    public WorkspaceContentPolicy WorkspaceContentPolicy { get; set; } =
        WorkspaceContentPolicy.SubmittedHaveOnly;

    public List<DocumentScopeRuleDto> DocumentScopes { get; set; } = [];

    public ContextScopeRuleDto? ContextScope { get; set; }

    public ChangeTriggerScopeRuleDto? ChangeTriggerScope { get; set; }
}

public sealed class DocumentScopeRuleDto
{
    /// <summary>稳定页面/范围身份，改名 root 不得改变此 id。</summary>
    public string Id { get; set; } = string.Empty;

    public string Root { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public List<string> IncludedPathGlobs { get; set; } = ["**"];

    public List<string> ExcludedPathGlobs { get; set; } = [];

    /// <summary>
    /// 未配置时使用全局默认后缀集合；不允许隐式全收。
    /// 如需接受全部文本文件必须显式声明 AcceptAllTextFiles=true。
    /// </summary>
    public List<string>? IncludedSuffixes { get; set; }

    public bool AcceptAllTextFiles { get; set; }
}

public sealed class ContextScopeRuleDto
{
    public List<string> Roots { get; set; } = [];

    public bool ReadOnly { get; set; } = true;

    public bool PreferTrackedFiles { get; set; } = true;

    public long MaxFileBytes { get; set; } = 2 * 1024 * 1024;

    public List<string> IncludedPathGlobs { get; set; } = ["**"];

    public List<string> ExcludedPathGlobs { get; set; } = [];
}

public sealed class ChangeTriggerScopeRuleDto
{
    /// <summary>未配置时默认 true：继承全部 documentScopes。</summary>
    public bool InheritsDocumentScopes { get; set; } = true;

    public List<string> AdditionalRoots { get; set; } = [];

    public List<string> IncludedPathGlobs { get; set; } = ["**"];

    public List<string> ExcludedPathGlobs { get; set; } = [];
}

/// <summary>
/// 解析后的只读 Scope 模型。
/// </summary>
public sealed class ResolvedScopeConfiguration
{
    public required int SchemaVersion { get; init; }

    public required WorkspaceContentPolicy WorkspaceContentPolicy { get; init; }

    public required IReadOnlyList<ResolvedDocumentScope> DocumentScopes { get; init; }

    public required ResolvedContextScope? ContextScope { get; init; }

    public required ResolvedChangeTriggerScope ChangeTriggerScope { get; init; }

    public required string ContentHash { get; init; }

    public required string NormalizedJson { get; init; }

    public bool IsLegacyFallback { get; init; }
}

public sealed class ResolvedDocumentScope
{
    public required string Id { get; init; }

    public required string Root { get; init; }

    public string? DisplayName { get; init; }

    public required IReadOnlyList<string> IncludedPathGlobs { get; init; }

    public required IReadOnlyList<string> ExcludedPathGlobs { get; init; }

    public required IReadOnlyList<string> IncludedSuffixes { get; init; }

    public required bool AcceptAllTextFiles { get; init; }
}

public sealed class ResolvedContextScope
{
    public required IReadOnlyList<string> Roots { get; init; }

    public required bool ReadOnly { get; init; }

    public required bool PreferTrackedFiles { get; init; }

    public required long MaxFileBytes { get; init; }

    public required IReadOnlyList<string> IncludedPathGlobs { get; init; }

    public required IReadOnlyList<string> ExcludedPathGlobs { get; init; }
}

public sealed class ResolvedChangeTriggerScope
{
    public required bool InheritsDocumentScopes { get; init; }

    public required IReadOnlyList<string> AdditionalRoots { get; init; }

    public required IReadOnlyList<string> IncludedPathGlobs { get; init; }

    public required IReadOnlyList<string> ExcludedPathGlobs { get; init; }
}

/// <summary>
/// 可选来源文件元数据（P4 filetype、tracked/opened 状态等）。
/// </summary>
public sealed class SourceFileMetadata
{
    public string? DepotPath { get; init; }

    public string? FileType { get; init; }

    public bool? IsTracked { get; init; }

    public bool? IsOpened { get; init; }

    public string? OpenedAction { get; init; }

    public bool? MatchesHaveContent { get; init; }

    public long? SizeBytes { get; init; }

    public string? LocalDigest { get; init; }
}

public sealed class FileSelectionDecision
{
    public required bool Accepted { get; init; }

    public required string ReasonCode { get; init; }

    public string? MatchedScopeId { get; init; }

    public string? Detail { get; init; }

    public static FileSelectionDecision Accept(string reasonCode, string? scopeId = null, string? detail = null)
        => new() { Accepted = true, ReasonCode = reasonCode, MatchedScopeId = scopeId, Detail = detail };

    public static FileSelectionDecision Reject(string reasonCode, string? detail = null, string? scopeId = null)
        => new() { Accepted = false, ReasonCode = reasonCode, MatchedScopeId = scopeId, Detail = detail };
}

public sealed class MovePathSelectionResult
{
    public required FileSelectionDecision OldPath { get; init; }

    public required FileSelectionDecision NewPath { get; init; }

    public required MoveScopeTransition Transition { get; init; }
}

public enum MoveScopeTransition
{
    DocumentToDocument,
    DocumentToContext,
    ContextToDocument,
    DocumentToExcluded,
    ContextToExcluded,
    ExcludedToDocument,
    ExcludedToContext,
    UnchangedOutside,
    Other
}

/// <summary>
/// SnapshotIdentity = Repository + Branch + ScopeConfigurationVersion
/// + TargetChangelist + TrackedManifestHash + GenerationEngineVersion
/// 发布身份 = SnapshotIdentity + Language。
/// WP5 增量幂等边界必须引用同一实现。
/// </summary>
public static class SnapshotIdentityBuilder
{
    public const string GenerationEngineVersion = "opendeepwiki-legacy-1";

    public static string Build(
        string repositoryId,
        string branchId,
        int? scopeConfigurationVersion,
        string? targetChangelist,
        string? trackedManifestHash,
        string? generationEngineVersion = null)
    {
        var engine = string.IsNullOrWhiteSpace(generationEngineVersion)
            ? GenerationEngineVersion
            : generationEngineVersion.Trim();

        return string.Join('|',
            Normalize(repositoryId),
            Normalize(branchId),
            scopeConfigurationVersion?.ToString() ?? "none",
            Normalize(targetChangelist) ?? "none",
            Normalize(trackedManifestHash) ?? "none",
            engine);
    }

    public static string BuildPublicationIdentity(string snapshotIdentity, string languageCode)
        => $"{snapshotIdentity}|lang:{Normalize(languageCode) ?? "default"}";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ScopeValidationResult
{
    public bool IsValid => Errors.Count == 0;

    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];
}

public sealed class ScopeImpactPreview
{
    public int? PreviousConfigurationVersion { get; init; }

    public int NextConfigurationVersion { get; init; }

    public bool ReindexRequired { get; init; }

    public IReadOnlyList<string> AddedDocumentScopeIds { get; init; } = [];

    public IReadOnlyList<string> RemovedDocumentScopeIds { get; init; } = [];

    public IReadOnlyList<string> ChangedDocumentScopeIds { get; init; } = [];

    public IReadOnlyList<string> AffectedLanguageCodes { get; init; } = [];

    public int EstimatedInvalidatedPages { get; init; }

    public string RebuildScopeSummary { get; init; } = string.Empty;

    public bool MigrationHint { get; init; }
}

public static class ScopeDefaults
{
    public static readonly IReadOnlyList<string> DefaultDocumentSuffixes =
    [
        ".h", ".hpp", ".inl", ".c", ".cc", ".cpp", ".as",
        ".ini", ".json", ".yaml", ".yml", ".uproject", ".uplugin",
        ".Build.cs", ".Target.cs", ".md"
    ];

    public static readonly IReadOnlyList<string> SensitiveFileNamePatterns =
    [
        ".env", ".pem", ".key", "id_rsa", "id_dsa", "credentials",
        "secrets.json", "appsettings.production.json"
    ];
}
