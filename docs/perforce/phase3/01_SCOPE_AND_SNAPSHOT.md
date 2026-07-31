# WP1：Scope、文件身份与 Wiki Snapshot

**目标**：先建立可信、统一、可发布的知识边界，供后续生成器、UE 元数据和 MCP 共同使用。

## 1. 交付范围

### T1.1 领域模型与配置 API

- 为仓库增加版本化 `RepositoryScopeConfiguration`。
- 定义 `DocumentScopeRule`、`ContextScopeRule`、`ChangeTriggerScopeRule` 和解析后的只读模型。
- 每个 Document scope 使用稳定 `id`；展示名和 root 改名不能隐式改变页面身份。
- 保存前校验相对路径、glob、复合后缀、重叠 Scope、符号链接逃逸及安全拒绝规则。
- 配置更新返回影响预览：新增/移除候选、受影响语言、失效页面、预计重建范围。
- 保存规范化配置的 `ConfigurationVersion` 和稳定内容摘要，并记录操作者和审计信息。

首期配置至少覆盖：

```json
{
  "schemaVersion": 1,
  "workspaceContentPolicy": "SubmittedHaveOnly",
  "documentScopes": [
    {
      "id": "game-source",
      "root": "SampleProject/Source",
      "includedPathGlobs": ["**"],
      "excludedPathGlobs": [],
      "includedSuffixes": [
        ".h", ".hpp", ".inl", ".c", ".cc", ".cpp", ".as",
        ".ini", ".json", ".yaml", ".yml", ".uproject", ".uplugin",
        ".Build.cs", ".Target.cs", ".md"
      ]
    },
    {
      "id": "game-plugins",
      "root": "SampleProject/Plugins",
      "includedPathGlobs": ["**"],
      "excludedPathGlobs": [
        "**/Binaries/**", "**/Intermediate/**", "**/Content/**",
        "**/Saved/**"
      ]
    }
  ],
  "contextScope": {
    "roots": ["Engine/Source", "Shared/Plugins", "Shared/Source"],
    "readOnly": true,
    "preferTrackedFiles": true,
    "maxFileBytes": 2097152
  }
}
```

### T1.2 统一文件选择策略

新增来源无关的 `IRepositoryFileSelectionPolicy`，作为扫描、增量和 Agent 工具的唯一判定入口：

```csharp
bool IsDocumentCandidate(string path, SourceFileMetadata? metadata = null);
bool ShouldTriggerUpdate(string path, SourceFileMetadata? metadata = null);
bool CanReadAsContext(string path, SourceFileMetadata? metadata = null);
bool ShouldPruneDirectory(string path, FileSelectionOperation operation);
```

要求：

- 判定顺序固定为：安全拒绝 → 路径排除 → 路径包含 → 后缀/filetype → 操作权限。
- 排除优先于包含；最具体 root 获胜，无法消歧的重叠配置拒绝保存。
- 复合后缀采用最长匹配，正确识别 `.Build.cs`、`.Target.cs`。
- glob 同时覆盖 scope 根目录和嵌套目录语义。
- 结果返回稳定 reason code，供预览、日志和测试使用。
- move 同时评估 old/new path，区分 Document→Document、Document→Context、Context→Document、Document→Excluded。
- `ContextScope` 永远不能通过 filetype 兜底成为 Document candidate。

### T1.3 P4 文件身份与 Workspace Lease

- 为 Document/Context 建立 P4 tracked/`#have` manifest，不从磁盘任意枚举文本文件。
- 默认 `SubmittedHaveOnly`：只接受 tracked 且本地内容与 have 状态一致的文件。
- 可选 `AllowOpenedFiles`：记录 opened action、本地摘要和“不可复现快照”警告。
- 任务记录 target CL、配置版本、manifest hash、实际 have 状态和完成 revision。
- 引入 `ISourceWorkspaceLease`，禁止同步与生成并发修改同一工作区。
- 生成期间复验实际读取文件；发现 workspace 漂移时失败关闭，不推进基线。
- 不自动 revert、清理或修改来源不明的本地文件。

`#have` 不是天然的单一全仓 revision。发布身份至少由以下信息组成：

```text
Repository + Branch + ScopeConfigurationVersion
+ TargetChangelist + TrackedManifestHash + GenerationEngineVersion
```

### T1.4 Staging 与原子发布

- `BranchLanguage` 保存当前已发布 `GenerationId`。
- Catalog、DocFile、来源、Scope Manifest、覆盖审计先写入 staging generation。
- 所有阻断校验通过后事务性切换当前指针。
- 失败、取消或超时保留上一完整版本，不能展示半棵目录或新旧混合正文。
- Scope 语义变化标记 `ReindexRequired`；旧版本可以继续读，但不能冒充新配置下的 current。
- 配置更新和生成任务共用仓库级协调锁；执行中版本变化时不发布结果。

## 2. 兼容要求

- 没有 Scope 配置的现有 Git、Archive、LocalDirectory 和 Perforce 仓库保持当前行为，并显示迁移提示。
- 通用策略不能依赖 P4 类型或 UE 私有目录名。
- SQLite/PostgreSQL 均提供增量迁移和启动兼容测试。
- API 只返回规范化相对路径，不回显凭据或宿主绝对路径。

## 3. 验收标准

- 相同路径在全量、增量、预览和 Agent 工具中得到同一判定与 reason code。
- 非法绝对路径、UNC、盘符、`..`、越界符号链接和敏感文件安全拒绝。
- 未跟踪、opened 和内容不一致文件严格遵守 `workspaceContentPolicy`。
- Scope 收缩会清除当前发布版本中的旧页面；Scope 扩大无需等待偶然增量即可重建。
- 单个发布版本只对应一个配置版本、manifest 和 generation。
- 工作区在任务期间变化时不产生成功发布，也不推进 `LastCommitId`。
- 新配置生成失败时，读者仍可访问上一完整快照。

## 4. 建议提交拆分

1. Scope 实体、DTO、验证器和数据库迁移。
2. 预览/更新 API、审计与配置版本。
3. 统一文件选择策略及跨来源回归。
4. P4 manifest、内容策略和 Workspace Lease。
5. staging generation、发布指针和失败恢复。

