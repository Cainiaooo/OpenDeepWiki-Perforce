using System.Text.Json.Serialization;

namespace OpenDeepWiki.Services.UeKnowledge;

/// <summary>
/// UE Knowledge Package schema version 1.0。
/// 导出器归属目标项目；OpenDeepWiki 只消费并验证此中间格式。
/// </summary>
public sealed class UeKnowledgeManifest
{
    /// <summary>Schema 版本，当前支持 1.0。</summary>
    public string SchemaVersion { get; set; } = UeKnowledgeSchema.CurrentSchemaVersion;

    public string ExporterVersion { get; set; } = string.Empty;

    public UeProjectIdentity Project { get; set; } = new();

    public string BuildChangelist { get; set; } = string.Empty;

    public string? EngineVersion { get; set; }

    public string? TargetPlatform { get; set; }

    /// <summary>Editor / Commandlet / Cook / PackagedBuild</summary>
    public string ExportSource { get; set; } = "Commandlet";

    public DateTimeOffset? ExportedAtUtc { get; set; }

    public string? CommandLineSummary { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UeKnowledgeCompletenessDto Completeness { get; set; } = UeKnowledgeCompletenessDto.Complete;

    public List<string> Errors { get; set; } = [];

    public List<string> TruncationNotes { get; set; } = [];

    public List<string> PrivacyFilterNotes { get; set; } = [];

    public List<UeKnowledgeShardDescriptor> Shards { get; set; } = [];
}

public sealed class UeProjectIdentity
{
    /// <summary>稳定项目标识，例如 SampleProject。</summary>
    public string ProjectId { get; set; } = string.Empty;

    public string? ProjectName { get; set; }

    public string? Branch { get; set; }

    /// <summary>可选：与 OpenDeepWiki Repository 对齐的外部键。</summary>
    public string? RepositoryExternalKey { get; set; }
}

public sealed class UeKnowledgeShardDescriptor
{
    /// <summary>分片逻辑名，如 reflection/classes、gameplay/tags。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>相对 package root 的路径，例如 reflection/classes-01.json。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>分片内容 SHA-256 hex（小写）。</summary>
    public string Digest { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public int? ObjectCount { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UeKnowledgeCompletenessDto
{
    Complete = 0,
    Partial = 1,
    Truncated = 2
}

public sealed class UeReflectionShard
{
    public List<UeClassFact> Classes { get; set; } = [];
}

public sealed class UeClassFact
{
    /// <summary>项目相对稳定标识，禁止本机绝对路径。</summary>
    public string StableId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? SuperClass { get; set; }

    public string Source { get; set; } = "Native"; // Native | Blueprint

    public string? ModuleName { get; set; }

    public string? Path { get; set; }

    public List<UePropertyFact> Properties { get; set; } = [];

    public List<UeFunctionFact> Functions { get; set; } = [];
}

public sealed class UePropertyFact
{
    public string Name { get; set; } = string.Empty;

    public string? Type { get; set; }

    public string? Category { get; set; }

    public List<string> Specifiers { get; set; } = [];
}

public sealed class UeFunctionFact
{
    public string Name { get; set; } = string.Empty;

    public string? ReturnType { get; set; }

    public string? Category { get; set; }

    public List<string> Specifiers { get; set; } = [];

    public List<string> Parameters { get; set; } = [];
}

public sealed class UeAssetShard
{
    public List<UePrimaryAssetFact> PrimaryAssets { get; set; } = [];
}

public sealed class UePrimaryAssetFact
{
    public string StableId { get; set; } = string.Empty;

    public string AssetType { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Bundle { get; set; }

    public List<string> ExplicitDependencies { get; set; } = [];
}

public sealed class UeDataSchemaShard
{
    public List<UeDataAssetSchemaFact> DataAssets { get; set; } = [];
}

public sealed class UeDataAssetSchemaFact
{
    public string StableId { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    public string? SuperType { get; set; }

    public List<UeSchemaFieldFact> Fields { get; set; } = [];
}

public sealed class UeSchemaFieldFact
{
    public string Name { get; set; } = string.Empty;

    public string? Type { get; set; }

    public bool IsArray { get; set; }
}

public sealed class UeGameplayTagShard
{
    public List<UeGameplayTagFact> Tags { get; set; } = [];
}

public sealed class UeGameplayTagFact
{
    public string Tag { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string? Comment { get; set; }
}

public sealed class UeWorkflowShard
{
    public List<UeEditorActionFact> EditorActions { get; set; } = [];
}

public sealed class UeEditorActionFact
{
    public string StableId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string? Description { get; set; }

    public List<string> Preconditions { get; set; } = [];
}

public sealed class UeMcpContractShard
{
    public List<UeMcpToolContract> Tools { get; set; } = [];
}

public sealed class UeMcpToolContract
{
    public string ToolName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? InputSchemaJson { get; set; }

    public List<string> Preconditions { get; set; } = [];

    public List<string> SideEffects { get; set; } = [];

    public string? MinBuildChangelist { get; set; }

    public string? MaxBuildChangelist { get; set; }
}

/// <summary>
/// 已验证并加载的完整包。
/// </summary>
public sealed class UeKnowledgePackageDocument
{
    public required UeKnowledgeManifest Manifest { get; init; }

    public required string PackageRootPath { get; init; }

    public required string PackageDigest { get; init; }

    public required string SemanticDigest { get; init; }

    public required IReadOnlyDictionary<string, string> ShardContentsByRelativePath { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// 预计算事实索引，进入生成管线与 MCP。
/// </summary>
public sealed class UeKnowledgeFactIndex
{
    public required string SchemaVersion { get; init; }

    public required string PackageDigest { get; init; }

    public required string SemanticDigest { get; init; }

    public required string ProjectId { get; init; }

    public required string BuildChangelist { get; init; }

    public string? EngineVersion { get; init; }

    public string Completeness { get; init; } = nameof(UeKnowledgeCompletenessDto.Complete);

    public bool IsPartial { get; init; }

    public IReadOnlyList<UeClassFact> Classes { get; init; } = [];

    public IReadOnlyList<UePrimaryAssetFact> PrimaryAssets { get; init; } = [];

    public IReadOnlyList<UeDataAssetSchemaFact> DataAssetSchemas { get; init; } = [];

    public IReadOnlyList<UeGameplayTagFact> GameplayTags { get; init; } = [];

    public IReadOnlyList<UeEditorActionFact> EditorActions { get; init; } = [];

    public IReadOnlyList<UeMcpToolContract> McpTools { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> PrivacyFilterNotes { get; init; } = [];
}

public sealed class UeKnowledgeValidationResult
{
    public bool IsValid { get; init; }

    public List<string> Errors { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public UeKnowledgePackageDocument? Package { get; init; }

    public UeKnowledgeFactIndex? FactIndex { get; init; }
}

public sealed class UeKnowledgeSemanticDiffResult
{
    public required string FromSemanticDigest { get; init; }

    public required string ToSemanticDigest { get; init; }

    public bool HasSemanticChange => ChangedKinds.Count > 0;

    public List<string> ChangedKinds { get; init; } = [];

    public List<string> AddedClassIds { get; init; } = [];

    public List<string> RemovedClassIds { get; init; } = [];

    public List<string> ChangedClassIds { get; init; } = [];

    public List<string> AddedTags { get; init; } = [];

    public List<string> RemovedTags { get; init; } = [];

    public List<string> AddedToolNames { get; init; } = [];

    public List<string> RemovedToolNames { get; init; } = [];

    public List<string> ChangedToolNames { get; init; } = [];

    public List<string> AffectedDomainHints { get; set; } = [];
}

public sealed class UeMcpContractCompatibilityResult
{
    public required string BuildChangelist { get; init; }

    public required string PackageBuildChangelist { get; init; }

    public bool IsCompatible { get; set; }

    public List<string> MissingTools { get; init; } = [];

    public List<string> IncompatibleTools { get; init; } = [];

    public List<string> Warnings { get; init; } = [];
}

public sealed class IngestUeKnowledgePackageRequest
{
    public required string BranchId { get; init; }

    /// <summary>
    /// 包根目录。可在服务器可访问路径上指向 CI 产出；路径不得包含凭据。
    /// </summary>
    public string? PackageRootPath { get; init; }

    /// <summary>
    /// 可选：直接提交 manifest JSON（与 PackageRootPath 二选一，优先 Root）。
    /// 当仅提交 manifest 时，Shards 必须内联或 Root 可读。
    /// </summary>
    public string? ManifestJson { get; init; }

    /// <summary>
    /// 期望的项目标识；不匹配则拒绝。
    /// </summary>
    public string? ExpectedProjectId { get; init; }

    /// <summary>
    /// 期望 Build CL；不匹配时拒绝（严格模式）或仅警告。
    /// </summary>
    public string? ExpectedBuildChangelist { get; init; }

    public bool StrictBuildChangelistMatch { get; init; } = true;

    /// <summary>
    /// 设为当前包；同分支旧 current 会被 superseded。
    /// </summary>
    public bool SetAsCurrent { get; init; } = true;
}

public sealed class UeKnowledgePackageSummaryDto
{
    public required string Id { get; init; }

    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public bool IsCurrent { get; init; }

    public bool IsStale { get; init; }

    public required string Status { get; init; }

    public required string Completeness { get; init; }

    public required string SchemaVersion { get; init; }

    public required string ExporterVersion { get; init; }

    public required string ProjectIdentity { get; init; }

    public required string BuildChangelist { get; init; }

    public string? EngineVersion { get; init; }

    public required string PackageDigest { get; init; }

    public required string SemanticDigest { get; init; }

    public DateTime? ExportedAtUtc { get; init; }

    public DateTime? IngestedAtUtc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
