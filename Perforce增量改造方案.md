# OpenDeepWiki Perforce 增量更新改造方案

**日期**:2026-07-22
**背景**:NeonGame(UE + AngelScript/C++,Perforce 管理)选用 OpenDeepWiki 作为模块级上下文文档平台(选型分析见 `D:/DevTools/代码库上下文工具选型对比(源码级调研).md`)。本文档描述让增量更新在 Perforce 工作区生效所需的改造。
**方案定位**:采用**"外部注入变更文件列表"**路线——不在 OpenDeepWiki 内部实现 p4 交互,由外部脚本采集 Perforce 变更并通过 API 注入。侵入面最小,便于跟随上游 rebase。

---

## 1. 现状:为什么 Perforce 工作区的增量是"静默空转"

Perforce 工作区只能以 `RepositorySourceType.LocalDirectory`(本地目录)方式导入,此时增量链路在三处被短路:

### 1.1 增量开关恒为 false

`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` 的 `PrepareWorkspaceAsync`(约 156-227 行):

```csharp
SupportsIncrementalUpdates = sourceInfo.SourceType == RepositorySourceType.Git   // 默认
...
else if (TryResolveLocalGitSource(...))   // 本地目录恰好是 exact Git 工作区
{
    workspace.SupportsIncrementalUpdates = true;
}
```

普通本地目录(无 `.git`,即 Perforce 客户端目录)恒为 `false`。测试断言可佐证:`tests/.../RepositoryAnalyzerSourceTests.cs:141, 389`(LocalDirectory)、`:39`(Archive)。

### 1.2 变更文件永远是空数组

`RepositoryAnalyzer.GetChangedFilesAsync`(约 297 行)第一行:

```csharp
if (!workspace.SupportsIncrementalUpdates) return Array.Empty<string>();
```

### 1.3 空列表被当作"无变更",快照 ID 照样推进

`src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs` 的 `ProcessIncrementalUpdateAsync`(约 179 行):

```csharp
if (changedFiles.Length == 0 && !string.IsNullOrEmpty(previousCommitId))
{
    await AdvanceBranchStateAsync(...);   // 只把新快照 ID 写回 LastCommitId
    return new IncrementalUpdateResult { Success = true, UpdatedDocumentsCount = 0, ... };
}
```

叠加效果:目录内容变化 → `ComputeDirectorySnapshotId`(`RepositoryAnalyzer.cs:1313`,按"相对路径+大小+mtime"算 SHA256)产生新快照 ID → 触发任务 → 变更列表为空 → **快照基线被推进、0 篇文档更新、任务报告"成功"**。同时 `IncrementalUpdateWorker.CreateScheduledUpdateTasksAsync`(`IncrementalUpdateWorker.cs:332-366`)对非 Git 源查不到 remote HEAD(`GetRemoteBranchHeadCommitAsync` 返回 null,`RepositoryAnalyzer.cs:118-131`),每轮轮询都会无条件建新任务——外观上"增量一直在跑",实际永不更新。

### 1.4 一个对改造有利的事实

真正执行更新的 `IWikiGenerator.IncrementalUpdateAsync`(`src/OpenDeepWiki/Services/Wiki/WikiGenerator.cs:660`)是 **LLM Agent 驱动**的:把变更文件路径列表塞进 prompt,由 Agent 用 `ReadCatalog`/`ReadDoc`/`EditDoc`/`WriteDoc` 工具自行决定更新哪些文档。它对"变更列表从哪来"毫无要求——**p4 产出的列表与 git diff 产出的列表完全等价**。整条增量引擎不需要动,只需要把正确的变更列表送进去。

---

## 2. 改造设计

### 2.1 总体思路

```
┌─────────────── 外部(新增脚本) ───────────────┐   ┌────────────── OpenDeepWiki(改造) ──────────────┐
p4 changes -m1 //depot/...#have  ──┐
p4 files //depot/...@from,@to     ──┼→ 变更文件列表 ──→ POST /api/v1/.../incremental-update/external
(或提交触发器/定时轮询)          ──┘                      │
                                                          ├→ IncrementalUpdateTask.ExternalChangedFiles
                                                          ├→ ProcessIncrementalUpdateAsync 优先用外部列表
                                                          └→ WikiGenerator.IncrementalUpdateAsync(不改)
```

要点:
- Perforce 工作区仍以本地目录模式挂载(workspace 准备逻辑复用现有 `PrepareLocalDirectoryWorkspaceAsync`,`RepositoryAnalyzer.cs:383`,建议配 `LocalDirectoryImportMode=Link` 避免整树复制);
- 版本标识用 **changelist 号**(字符串)替代 mtime 快照哈希——更精确且免去全目录扫描;
- 调度改为**事件驱动为主**(外部注入即建任务),关闭或忽略对该类型仓库的定时空转调度。

### 2.2 改动清单

#### ① 实体:`IncrementalUpdateTask` 增加外部变更字段

文件:`src/OpenDeepWiki.Entities/Repositories/IncrementalUpdateTask.cs` + EF 迁移

```csharp
/// <summary>外部(如 Perforce CI)注入的变更文件相对路径列表,JSON 数组;非空时优先于内部 diff。</summary>
public string? ExternalChangedFiles { get; set; }

/// <summary>外部注入的目标版本标识(如 Perforce changelist 号)。</summary>
public string? ExternalTargetRevision { get; set; }
```

改动量:小(字段 + 迁移)。

#### ② 来源类型:`RepositorySourceType` 增加 `Perforce`

文件:`src/OpenDeepWiki.Entities/Repositories/RepositorySource.cs`

- 枚举加 `Perforce`;
- 仿照 `EncodeLocalDirectoryPath` 的 `local::` + Base64 方案,增加 `p4::` 前缀编码(存本地 workspace 根路径,复用 `Repository.GitUrl` 字段的既有惯例)。

也可以偷懒不加枚举、继续用 `LocalDirectory` + 一个布尔配置,但独立类型能让 ③⑤ 的分支判断更干净,且为未来原生 p4 交互(见 §5)留位。改动量:小。

#### ③ 打开增量开关

文件:`RepositoryAnalyzer.PrepareWorkspaceAsync`(`RepositoryAnalyzer.cs:156` 起)

- `SourceType == Perforce` 时:走本地目录的 workspace 准备逻辑,但 `SupportsIncrementalUpdates = true`;
- `workspace.CommitId` 赋值:优先用外部传入的 changelist 号;拿不到时回退 `ComputeDirectorySnapshotId`(保持首次导入可用)。

改动量:小-中。

#### ④ 变更列表优先走外部注入

文件:`IncrementalUpdateService.ProcessIncrementalUpdateAsync`(`IncrementalUpdateService.cs`)

```csharp
string[] changedFiles;
if (!string.IsNullOrEmpty(task.ExternalChangedFiles))
{
    changedFiles = JsonSerializer.Deserialize<string[]>(task.ExternalChangedFiles) ?? [];
    currentCommitId = task.ExternalTargetRevision ?? currentCommitId;
}
else
{
    changedFiles = await analyzer.GetChangedFilesAsync(workspace, previousCommitId, currentCommitId, ct);
}
```

注意:保留"列表为空 → 只推进基线"的现有行为,但对 Perforce 类型**仅在外部明确注入了空列表时**才推进基线;由内部 diff 返回的空数组(理论上不该再发生)应记 warning 而非静默推进,避免回到空转老路。改动量:中,核心逻辑集中在这一个方法。

#### ⑤ 新增注入端点

文件:`src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs`

```
POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update/external
Body: { "targetRevision": "1234567", "changedFiles": ["Source/NeonGame/Foo.cpp", "Script/Abilities/Bar.as"], "deletedFiles": [...] }
```

- 创建 `Pending` 状态的 `IncrementalUpdateTask` 并填入 ①的两个字段;
- 幂等:同一 `(branchId, targetRevision)` 已存在未完成任务时合并/去重;
- 鉴权对齐现有手动触发端点。

改动量:小-中。

#### ⑥ 调度器:消除空转 + 修正版本标识判断

文件:`IncrementalUpdateWorker.cs`

- `CreateScheduledUpdateTasksAsync`(332-366 行):对 `Perforce` 类型**不再按定时器无条件建任务**(事件驱动已覆盖);若想保留定时兜底,需先实现"比较本地 changelist 与上次处理的 changelist"再决定建任务;
- **硬编码陷阱**(425-445 行):`IsGitCommitId`(40 位 hex)/`IsDirectorySnapshotId`(64 位 hex)/`ShouldNormalizeSnapshotBaseline` 这组判断会把 changelist 号(纯数字,如 `1234567`)误判处理。需扩展为识别"数字型 revision"或按 SourceType 分派,否则 changelist 基线可能被错误"标准化"。

改动量:中。这是最容易踩的坑,务必配测试。

#### ⑦ 测试

参照 `tests/.../RepositoryAnalyzerSourceTests.cs`、`IncrementalUpdateServiceTests.cs` 补:

- Perforce 类型 workspace 的 `SupportsIncrementalUpdates == true`;
- 外部注入列表优先于内部 diff;外部空列表 vs 内部空列表的不同行为;
- changelist 号不被 ⑥ 的 hex 判断误伤;
- 端点幂等性。

改动量:中。

### 2.3 改动量汇总

| 改动点 | 文件 | 量级 |
|---|---|---|
| ① 任务实体加外部字段 | `IncrementalUpdateTask.cs` + 迁移 | 小 |
| ② Perforce 来源类型 | `RepositorySource.cs` | 小 |
| ③ 打开增量开关/版本标识 | `RepositoryAnalyzer.cs` | 小-中 |
| ④ 外部列表优先 | `IncrementalUpdateService.cs` | 中 |
| ⑤ 注入端点 | `IncrementalUpdateEndpoints.cs` | 小-中 |
| ⑥ 调度器去空转 + hex 判断修正 | `IncrementalUpdateWorker.cs` | 中 |
| ⑦ 测试 | tests/ | 中 |

**合计约 300-600 行(不含测试),4-6 个核心文件,数天工作量。** 不需要动 `WikiGenerator`、不需要引入任何 p4 SDK 依赖。

---

## 3. p4 侧变更采集脚本(外部,新增)

与 OpenDeepWiki 解耦,单独放在 CI 或定时任务里。伪代码:

```powershell
# 1. 读取上次处理到的 changelist(存本地状态文件,或查询 OpenDeepWiki 分支状态 API)
$last = Get-Content .last-processed-cl

# 2. 同步工作区并取当前 changelist
p4 sync //depot/NeonGame/...
$current = (p4 changes -m1 //depot/NeonGame/...#have) -replace 'Change (\d+).*','$1'

if ($current -ne $last) {
    # 3. 取两个 changelist 之间的变更文件(区分修改与删除)
    $changed = p4 files "//depot/NeonGame/...@$([int]$last+1),@$current"
    #    → 解析出相对路径列表;action 为 delete/move/delete 的进 deletedFiles

    # 4. 注入 OpenDeepWiki
    POST /api/v1/repositories/{id}/branches/{bid}/incremental-update/external
         { targetRevision: $current, changedFiles: [...], deletedFiles: [...] }

    # 5. 成功后推进本地状态
    Set-Content .last-processed-cl $current
}
```

注意事项:
- depot 路径 → workspace 相对路径的映射用 `p4 where` 或按 client view 规则转换;
- 触发方式二选一:**定时轮询**(简单,推荐起步,如每 30-60 分钟)或 **p4 服务端 trigger**(change-commit 触发,实时,但需 P4 管理员权限);
- 过滤规则与导入时的目录圈定保持一致(如只投喂 `Source/`、`Script/`,排除 `Content/`、`Intermediate/` 等二进制/生成目录);
- 单个 changelist 涉及文件过多时(如大规模整合),建议脚本侧设阈值,超过则改为触发全量重建(`AdminRepositoryService.RegenerateRepositoryAsync`,`src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:812`)而非增量。

---

## 4. 配套:导出 MD 进 git 的流水线

目标:文档产物通过 git 管理(可落 NeonGame 的 `DevDocs/` 体系),Human 走 wiki 界面,Agent 读 MD。

现有导出能力:`WikiGenerator.ExportAsync`(`src/OpenDeepWiki/Services/Wiki/WikiGenerator.cs:615-711`),`GET /{owner}/{repo}/export`,把 `DocCatalog` 树 + `DocFile.Content` 打包为含 .md 文件树的 ZIP(另附自动生成的 `SKILL.md`)。两个自动化障碍:

1. **限流**:同一仓库+分支+语言 5 分钟只能导出一次、全局最多 10 并发(`WikiGenerator.cs:25-30`)——对流水线通常够用,但失败重试逻辑要感知 429;
2. **开关**:要求仓库启用 `GenerateSkill`(`ExportAsync` 内 `if (!repository.GenerateSkill) return BadRequest`,约 640 行)。

建议:要么直接启用 `GenerateSkill` + 流水线容忍限流;要么加一个内部无限流导出端点(几十行,复用 `AddFilesToArchive`)。流水线本身:增量任务完成后(轮询任务状态或加 webhook)→ 下载 ZIP → 解压到 git 仓库目录 → `git diff` 非空则提交。MD 是多文件目录树,diff/review 体验良好。

---

## 5. 备选与不采纳的路线

- **原生 p4 集成**(在 `RepositoryAnalyzer` 内实现 `PreparePerforceWorkspaceAsync`,内部调 `p4 sync`/`p4 files` 做 diff):功能上更自洽,但需引入 p4 CLI/P4API 依赖、处理登录态与 client 配置,改动量升至中-大,且与上游合并冲突面显著变大。**留作二期**,①-⑤ 的设计(独立 SourceType、`GetChangedFilesAsync` 单点收口)已为其预留扩展位;
- **不改代码、纯定时全量重建**:可行的保底方案(`RegenerateRepositoryAsync` 会先清空旧文档再重建),但 NeonGame 规模下 token 成本与生成时长不可持续,仅建议作为增量长期漂移后的定期校准手段(如每月一次全量);
- **改用文件系统哈希自检增量**(仿 codegraph 的 size/mtime + 内容哈希):需要在 OpenDeepWiki 内新建整套指纹存储与对比逻辑,改动量大于外部注入且收益无差异,不采纳。

---

## 6. 实施顺序

1. **先验证、后改造**:不改任何代码,圈定 2-3 个核心模块目录(如 `Source/NeonGame/` 下若干子系统 + `Script/Abilities/`)以本地目录导入,全量生成,评估文档质量是否达到"Agent CR 兜底上下文"标准;
2. 质量过关 → 实施 §2 改造(建议独立分支维护,定期 rebase 上游);
3. 部署 §3 p4 采集脚本(先定时轮询,稳定后再考虑 p4 trigger);
4. 打通 §4 导出流水线,MD 落 git;
5. 观察 2-4 周:LLM Agent 决定的更新范围可能漏更/过度更新,结合 `DocFile.SourceFiles`(生成时读取的源文件列表)抽查覆盖情况,必要时按 §5 做定期全量校准。
