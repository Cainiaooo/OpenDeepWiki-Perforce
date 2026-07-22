# OpenDeepWiki Perforce 增量更新改造方案

**日期**:2026-07-22
**背景**:NeonGame(UE + AngelScript/C++,Perforce 管理)选用 OpenDeepWiki 作为模块级上下文文档平台(选型分析见 `D:/DevTools/代码库上下文工具选型对比(源码级调研).md`)。本文档描述让增量更新在 Perforce 工作区生效所需的改造。
**方案定位**:采用**"外部注入变更文件列表"**路线——不在 OpenDeepWiki 内部实现 p4 交互,由外部脚本采集 Perforce 变更并通过 API 注入。侵入面最小,便于跟随上游 rebase。
**分期**:一期(§2-§4,已落地)为"外部注入完整变更列表";**二期(§7)为目标形态**——外界只发轻量事件,由 OpenDeepWiki 按 changelist 区间自行拉取变更并经 CL 过滤引擎筛选。二期完整复用一期的处理管线(任务实体、`ProcessIncrementalUpdateAsync` 外部列表优先、幂等/回退兜底),仅把"变更列表的生产者"从外部脚本收敛进服务端。

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

- **原生 p4 集成**(在 `RepositoryAnalyzer` 内实现 `PreparePerforceWorkspaceAsync`,内部调 `p4 sync`/`p4 files` 做 diff):功能上更自洽,但需引入 p4 CLI/P4API 依赖、处理登录态与 client 配置,改动量升至中-大,且与上游合并冲突面显著变大。**已具体化为 §7 的二期目标形态(事件驱动区间拉取 + CL 过滤引擎)**,①-⑤ 的设计(独立 SourceType、`GetChangedFilesAsync` 单点收口)已为其预留扩展位;
- **不改代码、纯定时全量重建**:可行的保底方案(`RegenerateRepositoryAsync` 会先清空旧文档再重建),但 NeonGame 规模下 token 成本与生成时长不可持续,仅建议作为增量长期漂移后的定期校准手段(如每月一次全量);
- **改用文件系统哈希自检增量**(仿 codegraph 的 size/mtime + 内容哈希):需要在 OpenDeepWiki 内新建整套指纹存储与对比逻辑,改动量大于外部注入且收益无差异,不采纳。

---

## 6. 实施顺序

1. **先验证、后改造**:不改任何代码,圈定 2-3 个核心模块目录(如 `Source/NeonGame/` 下若干子系统 + `Script/Abilities/`)以本地目录导入,全量生成,评估文档质量是否达到"Agent CR 兜底上下文"标准;
2. 质量过关 → 实施 §2 改造(建议独立分支维护,定期 rebase 上游);
3. 部署 §3 p4 采集脚本(先定时轮询,稳定后再考虑 p4 trigger);
4. 打通 §4 导出流水线,MD 落 git;
5. 观察 2-4 周:LLM Agent 决定的更新范围可能漏更/过度更新,结合 `DocFile.SourceFiles`(生成时读取的源文件列表)抽查覆盖情况,必要时按 §5 做定期全量校准。

---

## 7. 目标触发形态(二期):事件驱动的区间拉取 + CL 过滤引擎

### 7.1 与一期的关系

一期把"算变更列表"放在**外部脚本**(§3):脚本查 changelist 区间、解析文件、调注入端点。二期把这个职责**收敛进 OpenDeepWiki**:外界只发一个**轻量事件**(不携带文件列表),由服务端根据基线与最新 changelist 之间的区间**自行拉取并过滤** CL,再走一期同一条处理管线。

关键:二期不是推翻一期,而是**替换变更列表的生产者**。一期沉淀的任务实体(`ExternalChangedFiles`/`ExternalTargetRevision`)、`ProcessIncrementalUpdateAsync` 的外部列表优先分支、`IsRevisionAtOrBehind` 幂等/回退兜底、`TriggerExternalUpdateAsync` 单任务约束——**全部原样复用**。新增的只有"p4 访问 + CL 过滤 + 轻量事件入口"三块。

### 7.2 触发与区间计算

```
外界事件(webhook / 定时器 / p4 change-commit trigger,仅携带 repo+branch 标识,可选 latestCl)
  → OpenDeepWiki:
      baseCl   = branch.LastCommitId                       // 上次更新 Wiki 的 changelist(一期已改为纯数字 revision)
      latestCl = 事件携带 or `p4 changes -m1 //<view>#have` // 服务端查询更自洽
      if latestCl <= baseCl:  no-op                         // 复用 IsRevisionAtOrBehind 幂等兜底
      cls = `p4 changes //<view>@>baseCl,@latestCl`         // 左开右闭 (baseCl, latestCl],避免重复处理基线本身
      → §7.3 过滤 cls → 汇总变更文件 → TriggerExternalUpdateAsync(changedFiles=过滤结果, targetRevision=latestCl)
```

要点:
- **首次导入**(baseCl 为哨兵 `p4-initial`,非数字):跳过区间拉取,直接以 latestCl 作为新基线(全量已由首次导入完成),或触发一次全量重建;
- 事件可只带 repo+branch(服务端查 latestCl),也允许携带 latestCl(省一次 p4 查询、支持 trigger 直接传参);
- **过滤后全空的区间**:仍要**推进基线到 latestCl**(关键——否则下次又从 baseCl 重算整个区间,资产 CL 被反复扫描);一期空列表推进基线的行为正好满足此需求。

### 7.3 CL 过滤引擎(核心)

新形态的核心价值:并非所有 CL/文件都值得触发 Wiki 更新。UE 项目里资产类 CL(`.uasset`/`.umap`/贴图/模型/音频)占绝大多数,只有**代码**(`.cpp/.h/.cs/.as`)与**配置**(`.ini/.json/.uproject/*.Build.cs`)变更才影响文档。过滤引擎必须**可组合、可配置、默认安全、跨仓库复用**。

**抽象**:一条过滤管线(filter pipeline),作用在两个粒度——
- **文件级** `IFileChangeFilter`:决定单个变更文件是否进入变更列表;
- **CL 级** `IChangelistFilter`:决定整个 CL 是否被整体跳过(文件级过滤后为空的 CL 自然丢弃;也可有独立的 CL 级规则,如按描述标签/作者跳过)。

**过滤维度**(每个都是可插拔 filter,按配置组合成链):

| 维度 | 说明 | UE 示例 |
|---|---|---|
| 扩展名白/黑名单 | 按后缀,大小写不敏感 | 白名单 `.cpp .h .hpp .cs .as .ini .json .uproject .Build.cs .Target.cs`;或黑名单 `.uasset .umap .png .fbx .wav …` |
| 路径 glob/正则 | 按 depot 路径或 workspace 相对路径 | 只留 `//depot/NeonGame/Source/…`、`Config/…`;排除 `Content/`、`Intermediate/`、`Saved/`、`DerivedDataCache/` |
| 变更动作(action) | add/edit/delete/branch/integrate/move | 可选忽略纯 `integrate` 噪音;`delete` 纳入以驱动文档清理(对齐一期的删除并入) |
| CL 元数据 | CL 描述正则(如 `[skip-wiki]`/`#nodoc`)、作者/机器人账户 | 跳过自动化提交、跳过显式标注不需文档的 CL |
| filetype 兜底 | 按 p4 `filetype`(`text`/`binary`) | p4 已知为 binary 的一律排除,免维护后缀表,最健壮的兜底 |

**配置模型**:
- 规则挂**仓库级**(每个 Perforce 仓库可配自己的白/黑名单与路径规则),内置一套 **UE 友好默认**作为兜底;
- 存储:新增 `PerforceFilterConfig`(JSON 落 `Repository` 或独立表),或先用 appsettings 全局默认 + 仓库覆盖;
- **组合语义显式化**:默认 `包含路径命中 AND 未命中排除 AND (白名单命中 OR filetype=text)`;各 filter 的 AND/OR 与短路顺序必须写清,避免"多种过滤形式"叠加后语义含糊。

**健壮性要求**(用户强调的重点):
- **路径归一化**:depot 路径 ↔ client 相对路径用 `p4 where` 映射;统一 Windows/Unix 分隔符;大小写按服务器 casing 配置处理;
- **可观测**:记录每个 CL/文件被哪条 filter 以何理由过滤,并输出"区间共 N 个 CL、过滤后剩 M 个、涉及 K 个文件"的统计,便于调参;
- **可测试**:每类 filter 与典型组合都要单元测试(尤其 UE 资产 vs 代码 vs 配置的分类);
- **可演进**:规则热更新,新增 filter 类型不改管线骨架;
- **大区间保护**:过滤后文件数超阈值(基线落后过多或大规模重构) → 转全量重建(§3 已有阈值转全量思路),而非塞给增量 Agent。

### 7.4 需要的 p4 访问能力

二期必须让服务端能执行(这将打破一期"零 p4 交互"定位,是二期的主要代价):
- `p4 changes -m1 //<view>#have` → 最新 changelist;
- `p4 changes //<view>@>base,@latest` → 区间 CL 列表;
- `p4 describe -s <cl>`(summary,不拉文件内容)或 `p4 files @cl,@cl` → 每个 CL 的文件 + action + filetype;
- `p4 where` → depot ↔ workspace 相对路径映射。

落地选择(沿用 §5 权衡):
- **A. 内嵌 p4 CLI**(推荐):`Process` 调 `p4 -ztag …` 并解析 `-ztag` 结构化输出(比裸文本稳);需处理登录态(`P4TICKETS`/`p4 login`)、client/`P4CONFIG`、超时与重试。把 p4 访问封装成 `IPerforceClient` 接口(便于 mock 测试、便于未来换 P4API.NET);过滤引擎 `ICLFilterPipeline` 独立于 p4 访问,二者可分别测试;
- **B. 采集 sidecar**:p4 查询留在外部小服务,只把"区间原始 CL/文件清单"交给服务端过滤——但过滤引擎放服务端才通用,故 sidecar 至多承担 p4 原始查询,过滤与基线语义仍在 OpenDeepWiki 内。

### 7.5 复用边界一览

| 组件 | 一期 | 二期 |
|---|---|---|
| 任务实体 `ExternalChangedFiles`/`ExternalTargetRevision` | ✅ | ✅ 复用(过滤后文件列表 + latestCl 写入) |
| `ProcessIncrementalUpdateAsync` 外部列表优先 + 幂等/回退兜底 | ✅ | ✅ 完全复用 |
| `TriggerExternalUpdateAsync` 入队(单任务/防回退) | ✅ | ✅ 复用(改由内部区间计算后调用) |
| 注入端点 `/incremental-update/external` | ✅ 主通路 | 🔶 降级为手动/强制通路;主通路改为轻量事件端点 |
| 变更列表生产者 | 外部脚本(§3) | 🆕 服务端 `IPerforceClient` + `ICLFilterPipeline` |
| p4 交互 | ❌ 零交互 | 🆕 新增(§7.4) |
| `IncrementalUpdateWorker` Perforce 定时跳过 | 跳过 | 🔁 纯事件驱动可保持跳过;若加定时兜底则反转该分支 |

**净新增**集中在 `IPerforceClient`(p4 访问) + `ICLFilterPipeline`(过滤引擎) + 一个轻量事件端点三处,处理管线与基线语义**零改动**。

### 7.6 二期实施顺序

1. 抽 `IPerforceClient` 接口 + 内嵌 p4 CLI 实现(`-ztag` 解析、登录态、`p4 where` 映射),配契约测试;
2. 抽 `ICLFilterPipeline` + 各维度 filter + UE 默认配置,配单元测试(资产/代码/配置分类为重点);
3. 新增轻量事件端点(鉴权对齐一期):事件 → 区间计算 → 过滤 → `TriggerExternalUpdateAsync`;
4. 灰度:先与一期外部脚本**并行**(事件端点旁挂,注入端点保留),对比两条通路产出的变更列表是否一致,验证过滤规则;
5. 切换主通路到事件驱动,注入端点降级为手动/强制通道;按需决定是否加定时兜底(反转 `IncrementalUpdateWorker` 的 Perforce 跳过)。

### 7.7 二期落地状态与未完成项(2026-07-22)

#### 已落地(本分支)

- `IPerforceClient` / `PerforceCliClient` / `PerforceCommandRunner`(`-ztag`、超时/重试、`where` 批量映射);
- `IChangelistFilterPipeline` 与 UE 默认过滤配置(`appsettings.json` → `Perforce` 段 + 仓库级覆盖);
- 轻量事件端点 `POST .../incremental-update/perforce-event` + `PerforceIncrementalEventService`(区间拉取 → 过滤 → 复用 `TriggerExternalUpdateAsync` / 超阈值转全量);
- 全量晋升时 `EnqueueFullGenerationAsync(targetCommitId)` 首次落库即带数字 CL;Worker 在 Perforce 场景保留事件目标基线,避免回落 `p4-initial`;
- 审查修复:`latestChangelist` 必须 `<= #have`、HTTP 错误码分流(404/400/409/500)、无语言失败闭环、区间 last-action 文件聚合、多行 `desc` 解析、Glob 正则缓存等;
- 对应单测覆盖主路径与上述边界。

#### 未完成 / 后续债(必须单独迭代)

以下项在 code review 中已识别,但**尚未做成完整生产形态**,需要后续排期。优先顺序建议从上到下。

| 优先级 | 项 | 现状 | 目标形态 | 风险若不做 |
|---|---|---|---|---|
| **P0** | **事件处理异步化** | `perforce-event` 仍在 **HTTP 请求内同步**跑 `p4 changes` + 逐 CL `describe`/`where` + 过滤 | 接事件后快速校验并 **入队后台任务**(HostedService / 专用 worker):记录 intent(`targetCl`)+ 分支租约 → 后台拉区间/过滤 → 再调 `TriggerExternalUpdateAsync` 或全量入队;HTTP 返回 `Accepted`/`AlreadyQueued` 与 taskId | 大 UE 区间可跑数分钟,反向代理/Kestrel 超时、请求取消后无任务、DbContext 长占用;触发风暴时放大 p4 压力 |
| **P1** | **分支级租约 / 事件合并** | 仅在 p4 扫描前查是否已有在途增量/全量任务;不同 `targetCl` 并发仍可能各自扫完整区间再 409 | 扫描前获取短生命周期 branch lease;同一分支只保留一条 "perforce event" 工作项,`targetCl` 取 max 合并(coalesce) | trigger 风暴下重复 p4 查询、冲突 409、无效 CPU/p4 负载 |
| **P1** | **describe 路径预过滤 / 降本** | 每个纳入的 CL 先 `describe -s` 再对全部 depot 文件 `where`,资产 CL 成本高 | 在 `where` 前按 depot 路径后缀/glob 粗滤;或改用 `p4 files //view@cl,@cl` 限制在 client view;对纯资产 CL 短路 | N×CL 的 CLI 往返,UE 大仓上是主耗时源 |
| **P2** | **可观测性补齐** | 目前以 Log 为主,过滤理由多在 Debug | 结构化指标/任务结果字段:区间 CL 总数、过滤后 CL/文件数、晋升全量原因、单 CL describe 耗时;可选审计表 | 线上调参与对账困难 |
| **P2** | **定时兜底策略** | Worker 对 Perforce 仍可按一期"跳过空转"思路运行;二期主通路为事件 | 明确是否需要低频定时兜底(仅当 `latestCl > baseCl` 时发内部事件),并反转/收紧调度分支 | 事件丢失时基线长期停滞 |
| **P3** | **灰度与双通路对账** | 注入端点与事件端点并存 | 并行期对比外部脚本列表 vs 服务端过滤列表一致性工具/测试夹具;主通路切换 runbook | 规则漂移不易发现 |
| **P3** | **CommandRunner 契约测试** | 单测以 mock CLI 为主 | 可选集成测试(有 p4 环境时)覆盖 transient 重试、timeout drain、登录态环境变量 | 真实 p4 版本差异难以及早暴露 |

#### 实施建议(针对 P0)

```
POST perforce-event
  → 鉴权 + 校验 workspace / latestCl<=#have / 基线
  → Upsert PerforceEventWorkItem(branchId, targetCl=max, status=Pending)  // 同分支 coalesce
  → 202 Accepted { workItemId, targetCl, action=Queued|AlreadyQueued }
  → (后台) 持有 branch lease
        → GetChangelists / filter / describe
        → TriggerExternalUpdateAsync 或 EnqueueFullGeneration(targetCommitId)
        → 推进 workItem 状态;失败可重试且不误推进基线
```

约束:
- **失败默认不推进 `LastCommitId`**(与当前"取消则不入队"一致);
- 过滤后空列表仍走一期空列表基线推进语义;
- 全量晋升继续写入数字 `TargetCommitId`,Worker 侧保留现有基线保护逻辑。

#### 明确不在本债列表内(已修)

- 客户端 `latestChangelist` 越过 `#have` 推飞基线;
- 全量任务 `TargetCommitId` 二次 Save 竞态;
- `InvalidOperationException` 一律 409;
- 无语言配置却 Completed 并推进基线;
- 区间 add→delete / delete→add 聚合错误、多行 desc 丢弃、`MaxFiles` 双重计数、Glob 重复编译等。
