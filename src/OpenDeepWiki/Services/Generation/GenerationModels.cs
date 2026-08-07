using System.Text.Json.Serialization;
using OpenDeepWiki.Services.Repositories.Scope;
using OpenDeepWiki.Services.UeKnowledge;

namespace OpenDeepWiki.Services.Generation;

/// <summary>
/// 生成引擎能力声明。领域实体与发布管线只依赖此契约，不绑定具体 Agent 实现。
/// </summary>
public sealed class GenerationEngineCapabilities
{
    public required string EngineId { get; init; }

    public required string Version { get; init; }

    public IReadOnlyList<string> SupportedLanguages { get; init; } = ["zh", "en"];

    public IReadOnlyList<string> SupportedFileTypes { get; init; } = ScopeDefaults.DefaultDocumentSuffixes;

    public int? MaxContextTokens { get; init; }

    public bool SupportsDomainPlanning { get; init; }

    public bool SupportsTopicPlanning { get; init; }

    public bool SupportsLeafGeneration { get; init; }

    public bool SupportsCoverageAudit { get; init; }

    public bool SupportsIncrementalRemap { get; init; }

    public bool SupportsUeInventory { get; init; }
}

/// <summary>
/// 统一生成请求。可序列化、可追踪；引擎只生成 staging 产物，无权切换发布指针。
/// </summary>
public sealed class GenerationRequest
{
    public required SnapshotIdentity Snapshot { get; init; }

    public required ResolvedScopeConfiguration ResolvedScopes { get; init; }

    public SourceInventory? SourceInventory { get; init; }

    public required GenerationPolicy Policy { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public required string BranchLanguageId { get; init; }

    public required string LanguageCode { get; init; }

    public string? GenerationId { get; init; }

    public string? OwnerTaskId { get; init; }

    /// <summary>
    /// 可选：限制 Inventory 只接受这些相对路径（P4 tracked/#have 清单）。
    /// </summary>
    public IReadOnlySet<string>? AllowedTrackedPaths { get; init; }

    /// <summary>
    /// 可选：WorkspaceManifest 条目，用于填充 have revision 等元数据。
    /// </summary>
    public IReadOnlyDictionary<string, WorkspaceManifestEntry>? ManifestByPath { get; init; }

    /// <summary>
    /// 可选：已验证的 UE Knowledge 事实索引（WP3）。不替代 DocumentScope。
    /// </summary>
    public UeKnowledgeFactIndex? UeKnowledgeFacts { get; init; }

    public string? EngineId { get; init; }
}

/// <summary>
/// SnapshotIdentity 的结构化表示（与 Scope 中的字符串构建器一致）。
/// </summary>
public sealed class SnapshotIdentity
{
    public required string RepositoryId { get; init; }

    public required string BranchId { get; init; }

    public int? ScopeConfigurationVersion { get; init; }

    public string? TargetChangelist { get; init; }

    public string? TrackedManifestHash { get; init; }

    public required string GenerationEngineVersion { get; init; }

    public string ToStableString()
        => SnapshotIdentityBuilder.Build(
            RepositoryId,
            BranchId,
            ScopeConfigurationVersion,
            TargetChangelist,
            TrackedManifestHash,
            GenerationEngineVersion);
}

public sealed class GenerationPolicy
{
    /// <summary>领域文件预算；超出则递归拆分。</summary>
    public int DomainFileBudget { get; init; } = 400;

    /// <summary>最大规划深度（保护条件，不限制 Inventory 扫描）。</summary>
    public int MaxPlanningDepth { get; init; } = 4;

    /// <summary>叶子页最大入口文件数。</summary>
    public int MaxEntryFilesPerPage { get; init; } = 12;

    /// <summary>是否执行叶子正文生成（Hierarchical 原型首期可关闭）。</summary>
    public bool GenerateLeafContent { get; init; }

    /// <summary>是否在发布前强制覆盖审计阻断（首期报告不阻断）。</summary>
    public bool BlockOnCoverageErrors { get; init; }

    public int MaxRetryAttempts { get; init; } = 2;

    public int? TokenBudget { get; init; }

    public string PromptVersion { get; init; } = "phase3-wp2-v1";

    /// <summary>可选人工 steering 蓝图 JSON。</summary>
    public string? WikiBlueprintJson { get; init; }
}

/// <summary>
/// 统一生成产物集。引擎输出 staging 产物，由控制面负责发布。
/// </summary>
public sealed class GenerationArtifactSet
{
    public required string EngineId { get; init; }

    public required string EngineVersion { get; init; }

    public required string SnapshotIdentity { get; init; }

    public required string LanguageCode { get; init; }

    public SourceInventory? Inventory { get; init; }

    public IReadOnlyList<PlannedDomain> Domains { get; init; } = [];

    public IReadOnlyList<ScopeManifest> ScopeManifests { get; init; } = [];

    public IReadOnlyList<GeneratedLeafPage> Leaves { get; init; } = [];

    public MergedCatalogArtifact? Catalog { get; init; }

    public CoverageAuditReport? Coverage { get; init; }

    public GenerationExecutionSummary Summary { get; init; } = new();

    public GenerationCompletenessStatus Completeness { get; init; }
        = GenerationCompletenessStatus.NotStarted;
}

public sealed class GenerationExecutionSummary
{
    public string? Model { get; set; }

    public string? PromptVersion { get; set; }

    public int? TokenBudget { get; set; }

    public int RetryCount { get; set; }

    public int ToolCallCount { get; set; }

    public long DurationMs { get; set; }

    public List<string> Warnings { get; set; } = [];

    public List<string> Errors { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GenerationCompletenessStatus
{
    NotStarted = 0,
    InventoryOnly = 1,
    Planned = 2,
    Completed = 3,
    CompletedWithCoverageWarnings = 4,
    Failed = 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceKind
{
    Document = 0,
    Context = 1,
    UeExportedFact = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageItemStatus
{
    Covered = 0,
    IntentionallyExcluded = 1,
    BudgetTruncated = 2,
    Failed = 3,
    Unassigned = 4,
    DuplicateAssignment = 5,
    ContextOnlyEvidence = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryNodeKind
{
    File = 0,
    Module = 1,
    Plugin = 2,
    Project = 3,
    Target = 4,
    Directory = 5
}

/// <summary>
/// 确定性 Source Inventory：列事实，不直接决定最终 Wiki 页面。
/// </summary>
public sealed class SourceInventory
{
    public required string SnapshotIdentity { get; init; }

    public required string ContentHash { get; init; }

    public required string WorkingDirectory { get; init; }

    public required DateTimeOffset BuiltAtUtc { get; init; }

    public required IReadOnlyList<InventoryFileEntry> Files { get; init; }

    public required IReadOnlyList<InventoryModuleEntry> Modules { get; init; }

    public required IReadOnlyList<InventoryPluginEntry> Plugins { get; init; }

    public required IReadOnlyList<InventoryProjectEntry> Projects { get; init; }

    public required IReadOnlyList<InventoryTargetEntry> Targets { get; init; }

    public IReadOnlyList<ModuleDependencyEdge> Dependencies { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int TotalDocumentFiles => Files.Count;
}

public sealed class InventoryFileEntry
{
    public required string RelativePath { get; init; }

    public required string ScopeId { get; init; }

    public required string FileKind { get; init; }

    public string? Suffix { get; init; }

    public long SizeBytes { get; init; }

    public string? ContentDigest { get; init; }

    public string? HaveRevision { get; init; }

    public string? ModuleId { get; init; }

    public string? PluginId { get; init; }

    public string? ProjectId { get; init; }

    public bool IsEntryPoint { get; init; }
}

public sealed class InventoryModuleEntry
{
    public required string ModuleId { get; init; }

    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public required string ScopeId { get; init; }

    public string? BuildCsPath { get; init; }

    public string? PluginId { get; init; }

    public string? ProjectId { get; init; }

    public bool HasPublic { get; init; }

    public bool HasPrivate { get; init; }

    public int FileCount { get; init; }

    public IReadOnlyList<string> PublicDependencies { get; init; } = [];

    public IReadOnlyList<string> PrivateDependencies { get; init; } = [];
}

public sealed class InventoryPluginEntry
{
    public required string PluginId { get; init; }

    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public required string ScopeId { get; init; }

    public string? UpluginPath { get; init; }

    public IReadOnlyList<string> ModuleIds { get; init; } = [];
}

public sealed class InventoryProjectEntry
{
    public required string ProjectId { get; init; }

    public required string Name { get; init; }

    public required string RootPath { get; init; }

    public required string ScopeId { get; init; }

    public string? UprojectPath { get; init; }
}

public sealed class InventoryTargetEntry
{
    public required string TargetId { get; init; }

    public required string Name { get; init; }

    public required string TargetCsPath { get; init; }

    public required string ScopeId { get; init; }
}

public sealed class ModuleDependencyEdge
{
    public required string FromModuleId { get; init; }

    public required string ToModuleName { get; init; }

    public required string Kind { get; init; }
}

public sealed class PlannedDomain
{
    public required string DomainId { get; init; }

    public required string ScopeId { get; init; }

    public required string DisplayName { get; init; }

    public required IReadOnlyList<string> IncludedRoots { get; init; }

    public string? ParentDomainId { get; init; }

    public int Depth { get; init; }

    public int FileCount { get; init; }

    public bool BudgetTruncated { get; init; }

    public IReadOnlyList<string> ModuleIds { get; init; } = [];

    public IReadOnlyList<PlannedTopic> Topics { get; init; } = [];

    public DomainPlanStatus Status { get; init; } = DomainPlanStatus.Planned;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainPlanStatus
{
    Planned = 0,
    Failed = 1,
    BudgetTruncated = 2,
    RetryPending = 3
}

public sealed class PlannedTopic
{
    public required string PageId { get; init; }

    public required string DomainId { get; init; }

    public required string ScopeId { get; init; }

    public required string Title { get; init; }

    public required string TopicSummary { get; init; }

    public required IReadOnlyList<string> IncludedRoots { get; init; }

    public IReadOnlyList<string> ExcludedTopics { get; init; } = [];

    public required IReadOnlyList<string> EntryFiles { get; init; }

    public IReadOnlyList<string> RelatedPages { get; init; } = [];

    public required IReadOnlyList<EvidenceKind> RequiredEvidenceKinds { get; init; }
}

/// <summary>
/// 叶子页边界清单。正文生成必须消费此 Manifest，不能只凭标题猜范围。
/// </summary>
public sealed class ScopeManifest
{
    public required string PageId { get; init; }

    public required string ScopeId { get; init; }

    public required string DomainId { get; init; }

    public required string TopicSummary { get; init; }

    public required IReadOnlyList<string> IncludedRoots { get; init; }

    public IReadOnlyList<string> ExcludedTopics { get; init; } = [];

    public required IReadOnlyList<string> EntryFiles { get; init; }

    public IReadOnlyList<string> RelatedPages { get; init; } = [];

    public required IReadOnlyList<EvidenceKind> RequiredEvidenceKinds { get; init; }

    public string? ModuleId { get; init; }

    public string ContentHash { get; init; } = string.Empty;
}

public sealed class GeneratedLeafPage
{
    public required string PageId { get; init; }

    public required string DomainId { get; init; }

    public required string Title { get; init; }

    public string? Markdown { get; init; }

    public LeafPageStatus Status { get; init; } = LeafPageStatus.Pending;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<StructuredSourceRecord> Sources { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LeafPageStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    RetryPending = 3,
    Skipped = 4
}

public sealed class StructuredSourceRecord
{
    public required string Path { get; init; }

    public required string ScopeKind { get; init; }

    public string? ScopeId { get; init; }

    public string? HaveRevision { get; init; }

    public string? Digest { get; init; }

    public required string ReadPurpose { get; init; }

    public EvidenceKind EvidenceKind { get; init; } = EvidenceKind.Document;
}

public sealed class MergedCatalogArtifact
{
    public required IReadOnlyList<MergedCatalogNode> Roots { get; init; }

    public IReadOnlyList<string> ValidationErrors { get; init; } = [];

    public IReadOnlyList<string> ValidationWarnings { get; init; } = [];
}

public sealed class MergedCatalogNode
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? PageId { get; init; }

    public string? DomainId { get; init; }

    public int Order { get; init; }

    public IReadOnlyList<MergedCatalogNode> Children { get; init; } = [];
}

public sealed class CoverageAuditReport
{
    public required string SnapshotIdentity { get; init; }

    public required DateTimeOffset AuditedAtUtc { get; init; }

    public required IReadOnlyList<CoverageAuditItem> Items { get; init; }

    public int CoveredCount { get; init; }

    public int UnassignedCount { get; init; }

    public int FailedCount { get; init; }

    public int BudgetTruncatedCount { get; init; }

    public int DuplicateAssignmentCount { get; init; }

    public int ContextOnlyEvidenceCount { get; init; }

    public int IntentionallyExcludedCount { get; init; }

    public bool HasBlockingErrors { get; init; }

    public IReadOnlyList<string> BlockingReasons { get; init; } = [];

    public string ContentHash { get; init; } = string.Empty;
}

public sealed class CoverageAuditItem
{
    public required string SubjectKind { get; init; }

    public required string SubjectId { get; init; }

    public string? RelativePath { get; init; }

    public required CoverageItemStatus Status { get; init; }

    public string? AssignedDomainId { get; init; }

    public string? AssignedPageId { get; init; }

    public string? Detail { get; init; }
}
