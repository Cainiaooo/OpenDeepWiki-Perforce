# WP4：面向 AI CR、UE 编辑器 Agent 与人员的上下文交付

**状态**：已落地（Context Envelope、Snapshot Resolver、CR/Editor/Module 组装、MCP + HTTP 入口；Scope 级细粒度授权与匿名化反馈观测可后置）

**目标**：从“搜索若干 Wiki 页面”升级为“按任务、角色和版本组装可验证上下文”。

## 0. 实现触点

| 组件 | 路径 |
|---|---|
| ContextEnvelope / reason codes | `src/OpenDeepWiki/Services/Context/ContextModels.cs` |
| Wiki Snapshot Resolver | `src/OpenDeepWiki/Services/Context/WikiSnapshotResolver.cs` |
| Context Assembly（CR / Editor / Module） | `src/OpenDeepWiki/Services/Context/ContextAssemblyService.cs` |
| MCP 工具 | `src/OpenDeepWiki/MCP/McpContextTools.cs`（`GetChangeReviewContext` / `GetEditorTaskContext` / `GetModuleOverview`） |
| HTTP 入口（Web/Chat 复用） | `src/OpenDeepWiki/Endpoints/ContextEndpoints.cs` → `/api/v1/repositories/{id}/context/*` |
| 测试 | `tests/OpenDeepWiki.Tests/Services/Context/` |

兼容策略：`Exact` → 有限距离 `CompatibleFallback` → 可选 `Stale` → `Rejected`（写操作要求 Exact；Rejected 时 `items` 为空）。

## 1. 统一 Context Envelope

所有面向 Agent 的响应应使用统一封装：

```json
{
  "repositoryId": "...",
  "branch": "main",
  "requestedRevision": "123456",
  "resolvedSnapshotId": "...",
  "compatibility": "Exact",
  "coverageStatus": "Complete",
  "items": [],
  "citations": [],
  "warnings": []
}
```

必须包含：

- 请求版本与实际解析 Wiki Snapshot。
- `Exact`、`CompatibleFallback`、`Stale`、`Unknown` 或 `Rejected` 兼容状态。
- Scope/覆盖告警、来源和知识种类。
- 对 Context-only、AI 综合和实时未知项的明确标记。
- 可控制的 token/条目预算和确定性排序。
- `compatibility` 为 `Rejected` 时返回空 `items`，并在 `warnings` 中给出拒绝原因 reason code；不得部分返回可执行建议。

## 2. AI Code Review 上下文

### T4.1 `get_change_review_context`

输入建议：

- repository/branch。
- base CL、target CL。
- changed、deleted、moved files；服务端可在缺省时从 P4 查询。
- review focus、语言和 token budget。

输出优先包含：

- 文件对应的 module/domain/page。
- 相关能力、关键生命周期、不变量和已知风险。
- 跨模块依赖与可能受影响消费者。
- 建议验证的测试、命令和运行场景。
- 人类维护的设计约束及其所有权。
- 直接源码/导出事实引用，而不只是 Wiki 摘要。

安全要求：

- 不把变更作者或 CL 描述当作可信技术结论。
- changed file 不在 DocumentScope 时说明原因，不自动扩大主体范围。
- 删除和 move 不能因文件不存在而丢失历史页面映射。
- 没有足够证据时返回缺口，不生成确定性风险结论。

## 3. UE 编辑器任务上下文

### T4.2 Build Identity Handshake

UE MCP 或启动时生成的 build manifest 至少报告：

- project identity、branch/stream。
- build changelist、build version、engine version。
- target/platform、UE MCP contract version。

OpenDeepWiki 按该身份选择 Wiki Snapshot：

1. 优先精确 CL 和兼容导出包。
2. 允许配置最近的向后兼容版本，但必须在响应中告警。
3. 跨分支、未来版本或 tool contract 不兼容时默认拒绝提供可执行建议。
4. 查询类任务可以返回 stale 知识；写操作建议采用更严格门禁。

### T4.3 `get_editor_task_context`

输入建议：任务描述、Build Identity、当前资产/类型摘要、操作风险级别和预算。

输出优先包含：

- 相关项目概念、工作流和资产 Schema。
- 可用 UE MCP 工具、参数、前置条件和副作用。
- 项目命名、校验、安全约束和完成验证步骤。
- 需要 UE MCP 实时确认的状态清单。
- 与当前 Build 不兼容或知识缺失的明确告警。

推荐的 Agent 协作模式：

```text
任务 → OpenDeepWiki 获取稳定项目知识和约束
     → UE MCP 查询当前编辑器/资产事实
     → Agent 制定并执行步骤
     → UE MCP 验证结果
     → 必要时回查 OpenDeepWiki 的相关限制
```

OpenDeepWiki 不代理 UE 编辑器写操作，也不把 Wiki 内容直接转换为未确认的写命令。

## 4. 新人与跨模块导览

### T4.4 `get_module_overview`

返回渐进式结构：

1. 模块/能力职责和边界。
2. 关键入口、数据流和生命周期。
3. 上下游依赖及 related pages。
4. 常见修改位置、测试入口和风险提示。
5. 进一步阅读路径和源码引用。

Web 页面使用同一 Context Assembly 服务，避免 Web、Chat 和 MCP 分别维护不同检索逻辑。

## 5. 检索与组装策略

- 先通过稳定映射和结构化索引定位 domain/page，再做语义检索补充，避免纯向量搜索漂移。
- Context Assembly 按 persona 使用不同排序策略，但引用同一事实和页面实体。
- 摘要不能丢失版本、来源和覆盖告警。
- 返回内容应支持“为什么选择这条知识”的 reason code。
- 对敏感 Scope、角色权限和仓库访问执行服务端授权，不能依赖客户端隐藏。现有权限模型是仓库级的，Scope 级授权是本工作包的显式交付项；在其交付前，敏感内容只能通过“不纳入 Scope 配置”规避，不得声称已有细粒度权限。
- 记录匿名化的查询类型、命中、缺口、版本回退和反馈，用于质量评估；不记录不必要的源码或用户任务正文。

## 6. 现有 MCP 兼容

- 保留通用 `search_knowledge`、`get_page` 或现有仓库级 MCP 能力。
- 新接口构建在统一检索/授权层上，而不是复制一套独立索引。
- 契约包含 schema version；字段增加保持向后兼容。
- 大结果使用分页或资源链接，避免一次工具调用返回完整 Wiki。
- MCP 返回的 citation 应可进一步读取原页面或来源片段，但继续遵守 Scope 权限。

## 7. 验收标准

- 相同 changed files + CL 在稳定 snapshot 上产生稳定的核心评审上下文。
- AI CR 响应包含相关约束、来源和测试入口，且不混入未来 CL 内容。
- UE Build CL 与 Wiki 精确匹配、兼容回退和拒绝三条路径均有测试。
- 编辑器任务上下文明确区分长期知识与必须实时查询的 UE 状态。
- 不具备仓库/Scope 权限的调用方无法通过 MCP citation 绕过授权。
- Web、Chat 和 MCP 对同一页面的版本与覆盖状态一致。
- 新人导览能从概览逐步落到模块、流程和源码入口，而非一次返回大量无结构片段。

## 8. 建议提交拆分

1. Context Envelope、Snapshot Resolver 和兼容策略。
2. AI CR context assembler 与 MCP 契约。
3. Build Identity Handshake。
4. Editor task context assembler 与 UE MCP contract 关联。
5. Module overview、Web/Chat 复用和反馈观测。
