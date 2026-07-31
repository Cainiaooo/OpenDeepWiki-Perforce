# WP3：UE 结构化事实与资产知识导出

**目标**：弥补源码扫描对 Blueprint、资产配置和编辑器能力的天然盲区，为 Wiki 与 UE Agent 提供可验证事实。

## 1. 基本边界

- `.uasset`、`.umap` 默认不作为文本交给 LLM。
- OpenDeepWiki 不自行解析不稳定的 UE 私有二进制格式。
- 数据由与目标项目/引擎版本匹配的 UE Commandlet、Editor 插件或构建步骤导出。
- 导出结果是确定性事实输入，不等于最终面向人的 Wiki 正文。
- 未导出的资产事实必须标记为未知，不能从 C++ 类型声明推断实例中的实际配置。

### 导出器归属与分发

- 导出器代码归属目标项目仓库（UE 插件/Commandlet/构建步骤），随项目和引擎版本一起演进与分支，不在 OpenDeepWiki 仓库内维护。
- 导出能力必须能进入打包编辑器构建，或由构建流水线预先产出数据包；不得假设所有消费者（如仅持有打包编辑器的策划）拥有源码引擎。
- OpenDeepWiki 侧只维护 Knowledge Package schema、公开示例夹具和摄取/验证代码，并文档化 exporter version 与 schema version 的兼容矩阵。

## 2. UE Knowledge Package

定义版本化、可校验的中间格式，建议使用一份 manifest 加多份分片 JSON：

```text
ue-knowledge/
  manifest.json
  reflection/classes-*.json
  assets/primary-assets-*.json
  schemas/data-assets-*.json
  gameplay/tags.json
  workflows/editor-actions.json
  mcp/tool-contracts.json
```

`manifest.json` 至少包含：

- schema version、exporter version。
- project identity、branch、build changelist、engine version、target/platform。
- 导出时间、命令参数和各分片 digest。
- 是否来自 Editor、Commandlet、Cook 或 packaged build。
- 完整性、错误、截断和隐私过滤摘要。

### T3.1 最小可用导出内容

首期优先导出对策划 Agent 和跨模块理解最有价值、且相对稳定的内容：

- Blueprint 可见类层级与 native/Blueprint 来源标识。
- `UCLASS`、`UFUNCTION`、`UPROPERTY` 的公开元数据和调用边界。
- Module、Plugin、PrimaryAssetType 及显式依赖。
- GameplayTag 定义、层次和来源。
- DataAsset/DataTable 的类型及字段 Schema；默认不导出敏感实例值。
- 项目公开的 Editor action、验证规则、命名规范和常用工作流入口。
- UE MCP 工具契约、参数 Schema、前置状态和副作用说明。

### T3.2 后续可选内容

- StateTree、BehaviorTree、Mass 配置等结构摘要。
- Blueprint 调用关系、接口实现和软引用关系。
- 配置层级、Console Variable 和项目设置的允许列表。
- 关卡/World Partition 摘要；必须采用白名单和规模限制。
- 资产验证结果及常见修复建议，不直接导出大体积内容。

## 3. 隐私、规模和稳定性

- 每类导出均配置 include/exclude 和最大对象数、字段数、文件大小。
- 默认排除用户数据、密钥、路径、未发布内容、关卡实例值和大规模文本字段。
- 对对象使用稳定项目相对标识；不把本机绝对路径写入产物。
- 排序、分片和 digest 必须确定性，避免无语义变更触发全量更新。
- Schema 采用向后兼容版本；未知字段应被忽略，破坏性变更提升 major version。
- 导出器崩溃或部分失败时，manifest 必须明确 `Partial`，不能产出看似完整的数据包。

## 4. OpenDeepWiki 摄取

### T3.3 Trusted Fact Source Provider

- 新增来源类型或 provider，验证 manifest、schema、digest、Build CL 和 project identity。
- UE 元数据只能关联到兼容的 repository/branch/snapshot。
- 产物进入事实索引，并可成为 Scope Manifest 的 `entryFiles`/evidence，但不能绕过 DocumentScope 主体规则创建任意页面。
- 来源记录区分 `SourceDocument`、`SourceContext` 和 `UeExportedFact`。
- 页面显示数据包版本、导出完整性和最后兼容 Build CL。

### T3.4 更新语义

- 对结构化 JSON 做语义 diff，不以文件时间戳判断变化。
- 类型/工具契约/GameplayTag 等变化映射到相关领域和页面。
- 只有实例数量变化、但页面不消费该事实时不触发无意义重建。
- 导出包缺失或版本不兼容时保留上一份兼容知识，但标记 stale，不能静默视为最新。

## 5. 与 UE MCP 的边界

| OpenDeepWiki/导出包负责 | UE MCP 实时负责 |
|---|---|
| 项目稳定能力、类型、Schema、约束、工具说明 | 当前打开工程、关卡、对象、选择和 PIE 状态 |
| 对应 Build CL 的可复现事实 | 实例的实时属性与操作结果 |
| 工作流说明和安全前置条件 | 在编辑器内执行、验证和回滚具体操作 |

知识库不能声称某个资产“当前已配置”为某值，除非实时 UE MCP 刚刚读取并确认。

## 6. 验收标准

- 同一项目和 Build CL 的重复导出产生稳定逻辑内容与 digest。
- 数据包可追溯到 exporter、引擎、项目、分支和 Build CL。
- 部分失败、截断和被隐私规则过滤的内容在 manifest 中可见。
- OpenDeepWiki 拒绝损坏、digest 不符、schema 不支持或项目身份不匹配的数据包。
- Wiki 能引用 Blueprint/GameplayTag/DataAsset Schema 等事实，并明确其导出版本。
- 未导出的二进制资产配置不会被正文描述为已经确认。
- UE MCP 与 Wiki 对同一 Build CL 的工具契约可以完成自动兼容检查。

## 7. 建议提交拆分

1. UE Knowledge Package Schema 和公开示例夹具。
2. 最小 UE Exporter/Commandlet。
3. OpenDeepWiki provider、验证和事实索引。
4. 语义 diff 和页面依赖映射。
5. UE MCP tool contract 导出与兼容检查。

