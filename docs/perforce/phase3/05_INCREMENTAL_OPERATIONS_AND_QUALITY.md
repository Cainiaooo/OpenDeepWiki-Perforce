# WP5：增量维护、运维与质量门禁

**目标**：让知识库能够长期由 Agent 维护，并且对漏更新、错误版本、预算截断和质量退化可发现、可恢复。

## 1. Perforce 增量范围对齐

### T5.1 变更获取与统一判定

- 将 `ChangeTriggerScope` 转换为明确的 workspace/depot filespec，不依赖进程当前目录解释 `...`。
- 多 Scope 查询的 changelist 和文件结果去重，同时保留 CL、action、old/new path。
- 文件级判定统一委托 `IRepositoryFileSelectionPolicy`；P4 层只处理 depot/workspace 映射、filetype 和 CL 元数据。
- 过滤后为空仍推进已成功消费的数字 CL 基线，但不创建无意义正文任务。
- 超过 CL/文件阈值转全量时，仍只重建 DocumentScope。
- 删除、move/delete 和跨 Scope move 必须进入影响分析，不能因最终文件不存在而提前丢弃。

### T5.2 影响分级

按变化类型选择最小安全更新范围：

| 变化 | 默认动作 |
|---|---|
| 叶子页已记录来源变化 | 重建相关叶子页 |
| 模块内新增普通实现文件 | 更新领域 inventory，必要时重建相关页 |
| `.Build.cs`、`.uplugin`、模块入口变化 | 局部领域重新规划 |
| DocumentScope 配置或大规模结构变化 | 全量 inventory/规划 |
| Context-only 文件变化 | 仅更新已记录依赖它的页面 |
| UE Knowledge Package 语义变化 | 更新消费对应 fact 的页面/上下文 |
| 未知或影响计算失败 | fail closed，升级到领域或全量重建 |

首期可以保守扩大重建范围，但必须记录升级原因，不能静默漏更。

## 2. 依赖记录与知识失效

### T5.3 Knowledge Dependency Index

- 建立 source file/fact → page/domain 的反向映射。
- 保存 planning dependency 和 content dependency，避免正文引用与主题边界变化混为一类。
- Context 和 UE exported fact 可以触发消费页面失效，但不能拥有主体页面。
- 人类意图文档变化按显式关联更新相关页面；AI 不自动覆盖原始人类内容。
- 页面进入 stale 后，MCP 响应必须携带状态，直到重新生成和发布。

首期不要求完整符号调用图。优先使用生成时实际读取记录、模块依赖、Scope Manifest 和 UE 导出引用形成可解释依赖。

## 3. 调度、幂等和恢复

- 使用 `(repository, branch, target revision, scope config version, engine version)` 作为任务幂等边界。
- 同一仓库同步、全量规划和发布切换互斥；无依赖领域生成可以受控并行。
- 失败、取消、超时不推进 `LastCommitId` 或 current generation。
- 重试复用已经成功且输入摘要一致的领域/页面产物。
- 配置或 workspace 在执行中变化时停止发布，后续任务从新的完整身份重新开始。
- 提供同步完成 → 发送事件 → 等待发布 → 允许下一次同步的外部 runbook。
- 保留管理员强制全量、领域重建、页面重建和回滚发布版本的能力。

## 4. 可观测性

每个任务和领域至少记录：

- snapshot/config/engine/model/prompt identity。
- Scope 候选、排除、剪枝、未跟踪、opened 和预算截断数量。
- planner/leaf/merge/audit 各阶段耗时、token、工具调用和重试。
- `Complete`、`CompletedWithCoverageWarnings`、`Failed`、`Cancelled`。
- 影响分析选择的重建级别及 reason code。
- 发布切换、回滚和上一有效 generation。

管理员界面需要能回答：

1. 当前 Wiki 对应哪个 CL、配置和引擎？
2. 哪些领域或页面失败、截断、stale 或等待重建？
3. 某个 CL 为什么触发这些页面？
4. 最近一次完整发布与当前 staging 有什么差异？
5. 成本和耗时主要消耗在哪些 Scope/领域？

## 5. 质量门禁

### 阻断发布

- Scope/manifest/snapshot 身份不一致。
- Catalog 冲突、重复稳定 ID 或孤立正文。
- 只有 Context 证据的主体页面。
- 结构化来源丢失或 digest 不符。
- UE 数据包项目/Build 身份不兼容。
- 工作区漂移、配置版本漂移或覆盖审计阻断错误。

### 允许告警发布

- 明确、可定位的预算截断。
- 非关键领域失败，且当前版本标记 `CompletedWithCoverageWarnings`。
- 允许策略下的 opened 内容；必须显示不可复现警告。
- UE Metadata 非关键分片 stale；消费者可以选择拒绝。

告警是否可接受应按仓库策略配置，AI CR 和编辑器写任务可以采用比 Web 阅读更严格的门禁。

## 6. 测试层级

### 单元测试

- 路径、glob、复合后缀、大小写和安全拒绝。
- 四类跨边界 move 和删除语义。
- snapshot/config/generation identity。
- Scope Manifest、稳定 ID、Catalog merge 和覆盖状态。
- UE manifest/schema/digest 验证。
- Context compatibility 与版本回退策略。

### 集成测试

- SQLite/PostgreSQL 迁移、回滚和原子发布。
- 伪造 P4 manifest、CL 区间、move/delete、opened/untracked 和 workspace 漂移。
- 多领域并行、局部失败、重试复用和配置中途更新。
- Legacy/分层生成器使用同一请求和产物契约。
- MCP 鉴权、版本隔离、citation 和 stale/coverage 告警传播。

### 端到端测试

- 完整工作区中只为 DocumentScope 生成主体 Wiki。
- Context 依赖变化只更新消费页面。
- UE Metadata 包变化映射到对应页面和编辑器上下文。
- P4 事件从 target CL 到新 generation 发布完成，失败时不推进基线。
- UE Build Identity 与 Wiki snapshot 精确、回退和拒绝路径。

对照实验及业务效果评估不放在本文件，见 [`GENERATOR_BAKEOFF_PLAN.md`](GENERATOR_BAKEOFF_PLAN.md)。

## 7. 隐私与安全检查

提交前至少执行：

```powershell
rg -n -i "真实用户名|真实主机名|真实项目名|真实Client名|本机盘符路径" `
  docs scripts tests src
git diff --check
git status --short
```

真实敏感词列表只存放在本地或 CI 私密配置中。日志和测试夹具使用公开占位符，token、ticket 和 server 地址不得进入仓库。

## 8. 验收标准

- 空目标 CL 不重复扫描，失败 CL 不丢失，成功后才推进基线。
- changed/moved/deleted 文件能解释性映射到最小安全重建范围。
- Context 和 UE fact 变化不会生成自己的主体页面，但能使消费页面失效。
- 旧的完整 generation 在新版本失败时保持可用。
- 管理员能够定位每个覆盖缺口、失败领域、版本回退和成本热点。
- AI CR/编辑器上下文不会隐藏 stale、coverage warning 或版本不兼容。
- Git 和其他来源既有行为拥有持续回归覆盖。

## 9. 建议提交拆分

1. Perforce Scope filespec 与跨边界 action。
2. Source/fact → page/domain 依赖索引。
3. 增量影响分级和升级策略。
4. 幂等、局部重试、回滚和运维 API。
5. 指标、管理视图和质量门禁。
6. 跨来源与端到端回归。

