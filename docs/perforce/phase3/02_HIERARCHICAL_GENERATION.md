# WP2：超大 UE 工程的分层生成与覆盖审计

**目标**：把一次全局目录规划改造成可隔离、可重试、可比较的分层生成管线。

## 1. 生成引擎边界

先抽象 `IGenerationEngine`，避免领域实体和发布管线绑定当前 Agent 实现或任何外部项目的数据结构。

```text
GenerationRequest
  - SnapshotIdentity
  - ResolvedScopes
  - SourceInventory
  - GenerationPolicy
        │
        ▼
IGenerationEngine
  PlanDomains → PlanTopics → GenerateLeaves → MergeCatalog → AuditCoverage
        │
        ▼
GenerationArtifactSet
```

请求和产物必须可序列化、可追踪。引擎只生成 staging 产物，无权切换当前发布版本。

### T2.1 引擎接口与能力声明

- 定义引擎 ID、版本、支持的语言/文件类型、最大上下文和可选能力。
- 当前 OpenDeepWiki 实现包装成 `LegacyGenerationEngine`，作为兼容基线。
- 新分层实现注册为独立引擎，可按仓库或实验任务选择。
- 记录模型、prompt version、token budget、重试和工具调用摘要。
- 同一任务禁止混用两个引擎生成不可区分的页面。

## 2. 确定性 Source Inventory

模型规划前先用程序建立稳定工程清单：

- Document root、相对路径、文件类型和内容摘要。
- `.uproject`、`.uplugin`、`.Build.cs`、`.Target.cs`。
- UE Module、Plugin、Source/Public/Private 等结构信号。
- 可选的 AngelScript、配置、文档和 UE Metadata 包入口。
- 模块间声明依赖和明确的项目引用；首期不要求完整调用图。

Inventory 的职责是列事实，不直接决定最终 Wiki 页面。相同 snapshot + config 必须产生稳定排序和摘要。

## 3. 分层规划

### T2.2 Domain Planner

- 第一级只划分稳定领域/模块分区，不生成整棵最终 Catalog。
- 每个领域拥有独立输入、预算、状态和重试次数。
- 领域 ID 来自稳定 scope/module identity，不依赖模型自由命名。
- 超预算领域继续递归拆分，直到达到叶子阈值或最大规划深度。
- 最大深度只是停止递归的保护条件，不表示更深层文件不扫描；文件枚举完整性由 Inventory 和覆盖审计负责。

### T2.3 Topic Planner 与 Scope Manifest

领域规划器只处理所属子树，并为每个叶子主题输出：

```json
{
  "pageId": "stable-id",
  "scopeId": "game-source",
  "domainId": "sample-gameplay",
  "topicSummary": "描述该页回答的问题",
  "includedRoots": ["SampleProject/Source/SampleGameplay"],
  "excludedTopics": ["编辑器资产实际配置"],
  "entryFiles": ["SampleProject/Source/SampleGameplay/SampleGameplay.Build.cs"],
  "relatedPages": ["sample-runtime-lifecycle"],
  "requiredEvidenceKinds": ["Document"]
}
```

- `requiredEvidenceKinds` 取值为 `Document`、`Context`、`UeExportedFact`，且必须包含 `Document`。
- 正文生成必须消费持久化的 Manifest，不能只凭标题和 path 重新猜范围。
- 每页至少有一个 Document 入口文件；只有 Context 证据的候选页拒绝进入 Catalog。
- `excludedTopics` 用于防止邻接领域重复生成同一主题。
- 页面重试、模型切换和增量更新必须保留相同 page ID 和边界，除非重新运行规划阶段。

### T2.4 Leaf Generator

- 每个叶子页使用独立 Agent 上下文、预算、状态和来源记录。
- 先读 Manifest 入口和 Document 证据；证据不足时才能显式扩大到 Context。
- 结构化来源至少记录 path、scope kind、scope ID、have revision、digest、read purpose。
- 生成内容明确区分已证实事实、AI 综合、未知项和未读取的资产配置。
- 单页失败不影响无依赖页面；失败页保留可重试状态，不生成伪成功占位正文。

### T2.5 Catalog Merger

- 按 scope/domain/page 稳定 ID 合并子树，而不是按模型输出顺序拼接。
- 校验 slug 冲突、重复专题、空领域、循环 related page 和无意义单子节点分组。
- 领域重试不能改变无关领域的路径或顺序。
- 允许手工 Wiki Blueprint/Steering 配置关键必选页面、目的、入口文件和备注，作为模型规划的受控逃生口。

## 4. 覆盖审计

每个 root、模块、领域和叶子页输出状态：

- `Covered`
- `IntentionallyExcluded`
- `BudgetTruncated`
- `Failed`
- `Unassigned`
- `DuplicateAssignment`
- `ContextOnlyEvidence`

审计必须回答：

1. 哪些模块和目录没有分配到任何领域？
2. 哪些内容因预算或深度限制没有展开？
3. 哪些主题重复归属？
4. 哪些页面缺少 Document 主体或必要来源？
5. 哪些页面引用了与 snapshot 不一致的输入？

阻断错误不得发布为完整成功。可接受截断可以发布为 `CompletedWithCoverageWarnings`，并把未覆盖子树暴露给管理员和消费者。

## 5. 增量接口预留

- 持久化 file/module/domain/page 的稳定映射。
- 记录页面实际读取的 Document、Context 和 UE Metadata 来源。
- 允许增量管线重建受影响叶子或领域，而不重新生成全局目录。
- 当变更导致模块结构或主题边界变化时，升级到局部 replan；只有 Scope/Inventory 大幅变化才升级到全量规划。

## 6. 验收标准

- 深层 UE 模块不会因为目录观察深度限制而从 Inventory 和覆盖报告消失。
- 一个超大领域耗尽预算不会占用其他领域预算或阻止其发布。
- 相同 snapshot、配置和规划输入产生稳定领域 ID、page ID、路径和合并顺序。
- 每个可发布叶子页有 Scope Manifest、Document 入口和结构化来源。
- 领域或页面可独立重试，且不改变无关产物。
- 覆盖审计能定位未分配、重复、Context-only、失败和预算截断内容。
- Legacy 与新引擎可在相同 `GenerationRequest` 上运行并输出统一评测格式。

## 7. 建议提交拆分

1. `IGenerationEngine`、请求/产物契约和 Legacy 适配器。
2. 确定性 UE Source Inventory。
3. Domain/Topic Planner 与稳定 ID。
4. Scope Manifest 与叶子生成器。
5. Catalog Merger 和覆盖审计。
6. 局部重试、replan 和增量映射扩展点。

## 8. 现状基线（实现触点）

- 当前生成流程集中在 `src/OpenDeepWiki/Services/Wiki/WikiGenerator.cs`（约 2800 行的单体编排），按 T2.1 整体包装为 `LegacyGenerationEngine`，不在其内部做渐进改造。
- 目录写入经由 `src/OpenDeepWiki/Services/Wiki/CatalogStorage.cs`；Catalog Merger 的产物需映射到该存储或其 generation 化替代，注意其“目录急剧收缩时拒绝整树替换”的现有保护逻辑。
