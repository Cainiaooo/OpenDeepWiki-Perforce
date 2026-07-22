# Perforce 增量更新

让 OpenDeepWiki 在 Perforce 工作区上做增量文档更新。实现遵循仓库根目录的
`Perforce增量改造方案.md`——采用「外部注入变更文件列表」路线：OpenDeepWiki 内部不做
p4 交互，由外部脚本采集 Perforce 变更并通过 API 注入。

## 一、注册 Perforce 源

Perforce 工作区以本地目录形式挂载（建议配 `LocalDirectoryImportMode=Link` 避免整树复制，
并在 `AllowedLocalPathRoots` 中放行工作区根路径）。

```
POST /api/v1/repositories/submit-perforce
{
  "orgName": "neon",
  "repoName": "NeonGame",
  "workspaceRootPath": "D:/p4/NeonGame",   // p4 client root（须在 AllowedLocalPathRoots 内）
  "branchName": "main",
  "languageCode": "zh"
}
```

来源被编码为 `p4::<base64>` 存入 `Repository.GitUrl`，解析为 `RepositorySourceType.Perforce`。
首次导入走全量生成；此后由下面的注入端点驱动增量。

## 二、注入变更触发增量

```
POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update/external
{
  "targetRevision": "1234567",                              // Perforce changelist 号
  "changedFiles": ["Source/NeonGame/Foo.cpp", "Script/Abilities/Bar.as"],
  "deletedFiles": ["Source/NeonGame/Old.cpp"]               // 可选，当前引擎暂不处理删除
}
```

**鉴权**：该端点写入高影响内容（变更列表进入 LLM 提示、可推进版本基线），要求调用方为
**仓库所有者或管理员**。请携带 `Authorization: Bearer <JWT>`（该 JWT 属于仓库 owner 或 Admin），
匿名或越权调用返回 401/403。

行为要点：

- 创建 `Pending` 高优先级 `IncrementalUpdateTask`，携带 `ExternalChangedFiles` 与
  `ExternalTargetRevision`；处理时**外部变更列表优先于内部 diff**，版本基线推进为该 changelist。
- **幂等**：同一 `(branchId, targetRevision)` 已存在未完成任务时复用，不重复建任务。
- `changedFiles` 为空数组表示「推进基线但无文档变更」；无外部注入时 Perforce 不做定时空转调度
  （事件驱动为主）。
- `deletedFiles` 与 Git 增量行为一致，当前引擎（`WikiGenerator.IncrementalUpdateAsync`）不处理删除，
  仅记录，为后续删除感知留扩展位。

## 三、外部采集脚本

`inject-perforce-changes.ps1` 是模板：读取上次处理的 changelist → `p4 sync` → 取当前 changelist →
解析变更文件（depot 路径经 `p4 where` 转工作区相对路径，按 `Source/`、`Script/` 圈定，排除
`Content/`、`Intermediate/` 等）→ 注入上面的端点 → **轮询任务至 `Completed` 后**再推进本地状态文件。

```powershell
./inject-perforce-changes.ps1 `
  -ApiBaseUrl http://localhost:5085 `
  -ApiToken <owner-or-admin-jwt> `
  -RepositoryId <repoId> -BranchId <branchId> `
  -DepotPath //depot/NeonGame/...
```

关键：脚本**只有在增量任务真正 `Completed` 后**才把状态文件推进到当前 changelist；任务 `Failed`/
`Cancelled`/超时则保留原基线，下次运行会重投该 changelist，避免入队后任务失败导致变更永久漏投。

触发方式二选一：**定时轮询**（简单，推荐起步，如每 30–60 分钟）或 **p4 服务端 change-commit
trigger**（实时，需 P4 管理员权限）。单个 changelist 涉及文件过多时（脚本 `-MaxChangedFiles` 阈值），
建议改为触发全量重建而非增量。
