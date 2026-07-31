# Fork 上游同步与特性开发流程

本文档用于长期维护带有自定义特性的 OpenDeepWiki fork。目标是在持续接收官方上游更新的同时，
保留并继续开发 fork 中的 Perforce 等自定义能力，且不改写已经共享的 Git 历史。

## 一、远程与分支职责

| 名称 | 职责 |
|---|---|
| `upstream` | 官方仓库，只用于获取官方更新，不向其直接推送 |
| `origin` | 个人 fork，保存集成主干和自定义特性分支 |
| `main` | fork 的集成与发布主干，同时包含官方更新和已经验收的自定义特性 |
| `feat/perforce-incremental-external-update` | Perforce 长期开发分支，用于承接后续 Perforce 迭代 |
| `feat/perforce-*` | 推荐的短期开发分支，每个独立改动使用一个分支 |

首次参与开发时，先确认远程指向正确：

```powershell
git remote -v
git branch -vv
```

预期 `origin` 指向个人 fork，`upstream` 指向官方仓库，本地 `main` 跟踪 `origin/main`。

## 二、日常特性开发

优先从最新的 fork 主干创建短期分支：

```powershell
git fetch origin --prune
git switch main
git pull --ff-only origin main
git switch -c feat/perforce-具体功能
```

完成实现和验证后，提交并推送：

```powershell
git status --short
git add <本次改动的明确文件>
git commit -m "feat(perforce): 简短中文说明"
git push -u origin feat/perforce-具体功能
```

随后向 fork 的 `main` 发起 Pull Request。不要使用 `git add .` 或 `git add -A`，
避免把其他本地工作意外带入提交。

如果确实需要直接在长期分支继续开发，开始前先把 fork 主干合入：

```powershell
git fetch origin --prune
git switch feat/perforce-incremental-external-update
git merge --no-edit origin/main
```

长期分支已推送并由多人共享，因此不要对它执行 rebase 或 force push。

## 三、同步官方上游

先刷新两个远程引用，并确认工作区干净：

```powershell
git fetch origin --prune
git fetch upstream --prune
git status --short --branch
```

在 fork 主干上合入官方主干：

```powershell
git switch main
git pull --ff-only origin main
git merge --no-edit upstream/main
```

发生冲突时，只解决当前上游同步涉及的文件。解决完成后：

```powershell
git status --short
git add <已确认解决的文件>
git commit
```

完成验证后，把集成结果推送到 fork 主干：

```powershell
git push origin main
```

然后让仍在开发的长期分支跟上新的 fork 主干：

```powershell
git switch feat/perforce-incremental-external-update
git merge --no-edit main
git push origin feat/perforce-incremental-external-update
```

如果长期分支没有独有提交，合并会直接快进；如果已有新开发提交，Git 会保留双方历史并生成普通
merge commit。

## 四、把长期特性合入 fork 主干

长期特性验收完成后，推荐通过 Pull Request 合入 `origin/main`。需要在本地预演时：

```powershell
git fetch origin --prune
git switch main
git pull --ff-only origin main
git merge --no-edit origin/feat/perforce-incremental-external-update
```

验证通过后再推送：

```powershell
git push origin main
```

合入后将长期分支同步到新主干，使下一轮开发从统一基线开始：

```powershell
git switch feat/perforce-incremental-external-update
git merge --ff-only main
git push origin feat/perforce-incremental-external-update
```

如果 `--ff-only` 失败，说明长期分支上已经出现新的独有提交。此时不要强推，应使用普通
`git merge main`，解决冲突并验证后再推送。

## 五、验证要求

后端改动至少运行：

```powershell
dotnet build OpenDeepWiki.sln
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
```

前端改动至少运行：

```powershell
Set-Location web
npm run lint
npm run build
Set-Location ..
```

只改文档时至少检查：

```powershell
git diff --check
git status --short
```

如果完整测试存在已知失败，应记录通过数、失败数和失败用例，区分既有基线问题与本次改动引入的
回归，不要把部分测试成功描述成全量通过。

## 六、提交或推送前检查

```powershell
git status --short --branch
git log --graph --oneline --decorate --all -20
git diff --check
```

需要确认上游和长期特性都已经进入 fork 主干时：

```powershell
git merge-base --is-ancestor upstream/main main
git merge-base --is-ancestor feat/perforce-incremental-external-update main
```

两条命令都返回退出码 `0`，才表示 `main` 同时包含上游更新和长期特性历史。

## 七、历史与安全边界

- 不对已经推送的 `main` 或长期特性分支执行 rebase。
- 不使用 `git push --force` 或 `git push --force-with-lease` 更新共享分支。
- 不使用 `git reset --hard` 处理来源不明的本地改动。
- 上游同步使用 merge 保留双方历史，便于确认每次同步的范围。
- 提交时只暂存本次确认过的文件；推送前分别核对本地提交和远程分支状态。
- 若 `main`、`origin/main` 与 `upstream/main` 的关系不清晰，先停止写操作并检查提交图。
