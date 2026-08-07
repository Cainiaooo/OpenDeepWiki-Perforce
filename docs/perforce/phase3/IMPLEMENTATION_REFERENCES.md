# 三期实现参考项目与借鉴边界

**目的**：记录值得在具体任务中研究的公开实现，减少正式开发时重复调研。这里的“参考”不代表直接复制，也不代表已经完成许可证、安全、性能或 UE 适用性验证。

## 1. 总体选型结论

- OpenDeepWiki 保持知识控制面：仓库、任务、数据库、权限、Web、MCP、发布和 Perforce 集成。
- CodeWiki 优先作为分层生成内核的研究对象，并通过 `IGenerationEngine` 隔离。
- OpenWiki 主要参考 repo-native 指令、Agent 消费入口和 Mermaid 修复闭环。
- RepoAgent、RepoDoc 主要参考增量映射、代码对象关系和知识图谱/模块聚类思想。
- deepwiki-open 主要参考 Wiki 浏览、RAG 问答和产品交互，不作为 P4 核心底座。

## 2. CodeWiki

项目：<https://github.com/FSoft-AI4Code/CodeWiki>

最相关的公开模块：

| 模块 | 三期参考点 | 对应工作包 |
|---|---|---|
| [`cluster_modules.py`](https://github.com/FSoft-AI4Code/CodeWiki/blob/main/codewiki/src/be/cluster_modules.py) | 层次模块聚类、递归拆分、预算/深度控制 | WP2 Domain/Topic Planner |
| [`documentation_generator.py`](https://github.com/FSoft-AI4Code/CodeWiki/blob/main/codewiki/src/be/documentation_generator.py) | 文档生成编排、模块产物和增量入口 | WP2 Leaf Generator、WP5 增量 |
| [`dependency_analyzer/`](https://github.com/FSoft-AI4Code/CodeWiki/tree/main/codewiki/src/be/dependency_analyzer) | 语言依赖分析和模块关系 | WP2 Inventory、WP5 影响分析 |
| [`agent_tools/`](https://github.com/FSoft-AI4Code/CodeWiki/tree/main/codewiki/src/be/agent_tools) | Agent 文件工具和写文档边界 | WP2 叶子生成工具 |
| [`mcp/`](https://github.com/FSoft-AI4Code/CodeWiki/tree/main/codewiki/mcp) | CLI 产物通过 MCP 暴露的轻量方式 | WP4 MCP 契约参考 |

值得借鉴：

- 先做分层模块拆解，再递归生成，避免一次模型调用理解全仓。
- 主模型、聚类模型、叶子模块预算分离。
- 模块树和 metadata 作为生成中间产物，便于增量与评测。
- C/C++/C# 多语言入口和 Mermaid 产物验证。

不能直接照搬：

- Git commit、仓库内 `docs/` 和 CLI 生命周期不能替代 P4 snapshot/staging publication。
- `max-depth` 不能等同文件扫描深度；OpenDeepWiki 必须用确定性 Inventory 证明深层文件覆盖。
- CodeWiki 模块树不应直接成为 OpenDeepWiki 数据库公共契约，先映射到自有稳定 DTO。
- 其 C/C++ 效果必须通过真实 UE Scope 对照实验验证。

## 3. OpenWiki

项目：<https://github.com/langchain-ai/openwiki>

值得借鉴：

- `openwiki/INSTRUCTIONS.md` 作为人类维护的生成 brief，适合作为 Wiki Blueprint/Steering 的设计参考。
- 只维护 `AGENTS.md`/`CLAUDE.md` 中自己的标记区块，适合研究“让开发 Agent 主动发现知识库”的方式。
- Markdown 文档随仓库更新、通过 CI 提交变更的审阅闭环，可用于可选的导出/镜像能力。
- Mermaid 先验证、失败降级为可读文本、后续修复的闭环可用于 OpenDeepWiki 图表质量门禁。

不适合直接承担：中央 P4 多版本、Workspace Lease、数据库发布、角色权限和 UE Build CL 握手。

## 4. deepwiki-open

项目：<https://github.com/AsyncFuncAI/deepwiki-open>

重点参考：

- Wiki 页面、问答和代码检索的一体化用户体验。
- 多模型/Embedding provider 和本地部署配置。
- 生成缓存与查询端的分离方式。

限制：主要围绕 GitHub/GitLab/Bitbucket URL 和缓存式仓库处理，不能替代 P4 Scope、have manifest 和原子 snapshot 发布。

## 5. RepoAgent

项目：<https://github.com/OpenBMB/RepoAgent>

重点参考：

- 以代码对象为单位生成文档并维护调用/引用关系。
- 基于 Git diff 的增量更新和 pre-commit 工作流。
- source object → document 的精细映射思路。

限制：其历史实现更偏 Python AST。对 UE C++、UHT、宏、反射和 AngelScript 的适用性必须单独评估，不能作为首期解析器假设。

## 6. RepoDoc

项目：<https://github.com/SYSUSELab/RepoDoc>

重点参考：

- repository knowledge graph → module clustering → 模块化、交叉引用文档的研究路线。
- 知识图谱与增量更新结合的长期方向。

限制：三期前半段不应先构建完整全仓知识图谱。优先落地实际读取来源、模块声明依赖和 Scope Manifest，再用评测证明是否需要更重的图模型。

## 7. GitDiagram

项目：<https://github.com/ahmedkhaleel2004/gitdiagram>

仅参考可视化产物验证、路径引用和 Mermaid 图生成体验。它不是文档控制面、版本系统或大仓库生成内核。

## 8. DeepWiki 与 Zread 的产品启发

- DeepWiki 的显式页面/说明配置说明超大仓库仍需要人工 steering；三期应提供必选页面、页面 purpose、entry files 和 page notes。
- Zread 的本地 wiki/current/versions/drafts 思路可参考发布版本与草稿隔离，但正式实现仍以 OpenDeepWiki staging generation 为准。
- 闭源产品行为只能作为体验参考，不能假定内部架构、生成质量或版本语义。

## 9. 引入外部代码前检查表

- 核对目标 commit 的许可证和文件级声明，而不是只看项目首页。
- 记录上游 URL、commit SHA、修改范围和安全审计结果。
- 禁止把外部实体/配置格式直接暴露为本项目长期 API。
- 评估 Python/Node 服务引入后的部署、隔离、升级和故障恢复成本。
- 使用统一 `GenerationRequest`/`GenerationArtifactSet` 适配，确保可替换。
- 在对照实验达到门槛前，不删除 Legacy 引擎。

