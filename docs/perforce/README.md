# Perforce 文档索引

本目录集中维护 OpenDeepWiki fork 的 Perforce 适配、增量更新和后续演进文档。

## 文档

- [`Perforce增量改造方案.md`](Perforce增量改造方案.md)：一期外部注入、二期服务端区间拉取与已知技术债。
- [`PERFORCE_WORKSPACE_SCOPE_PHASE3_TASKS.md`](PERFORCE_WORKSPACE_SCOPE_PHASE3_TASKS.md)：三期 UE 项目知识库目标、架构原则、里程碑与任务入口。
- [`phase3/01_SCOPE_AND_SNAPSHOT.md`](phase3/01_SCOPE_AND_SNAPSHOT.md)：Scope、P4 文件身份、Workspace Lease 和原子发布。
- [`phase3/02_HIERARCHICAL_GENERATION.md`](phase3/02_HIERARCHICAL_GENERATION.md)：超大工程分层规划、Scope Manifest、生成隔离和覆盖审计。
- [`phase3/03_UE_KNOWLEDGE_EXPORT.md`](phase3/03_UE_KNOWLEDGE_EXPORT.md)：UE 反射、资产 Schema、GameplayTag 和 MCP 契约的结构化事实导出。
- [`phase3/fixtures/ue-knowledge/`](phase3/fixtures/ue-knowledge/)：UE Knowledge Package schema 1.0 公开示例夹具。
- [`phase3/04_AGENT_CONTEXT_DELIVERY.md`](phase3/04_AGENT_CONTEXT_DELIVERY.md)：AI CR、UE 编辑器 Agent、新人导览和 Build CL 版本握手。
- [`phase3/05_INCREMENTAL_OPERATIONS_AND_QUALITY.md`](phase3/05_INCREMENTAL_OPERATIONS_AND_QUALITY.md)：增量影响、运维、可观测性和发布质量门禁。
- [`phase3/IMPLEMENTATION_REFERENCES.md`](phase3/IMPLEMENTATION_REFERENCES.md)：CodeWiki、OpenWiki、RepoAgent 等项目的可借鉴模块与引入边界。
- [`phase3/GENERATOR_BAKEOFF_PLAN.md`](phase3/GENERATOR_BAKEOFF_PLAN.md)：可单独排期执行的 UE Wiki 生成器对照实验方案。
- [`EXTERNAL_INCREMENTAL_UPDATE.md`](EXTERNAL_INCREMENTAL_UPDATE.md)：Perforce 仓库注册、外部增量注入和采集脚本使用说明。
- [`FORK_DEVELOPMENT_WORKFLOW.md`](FORK_DEVELOPMENT_WORKFLOW.md)：fork 主干、上游同步和长期特性分支开发流程。

配套脚本位于 [`scripts/perforce/`](../../scripts/perforce/)。

## 维护约定

- 文档和示例只使用 `SampleProject`、`ExampleWorkspace` 等公开占位符。
- 不提交真实用户、主机、磁盘、P4 client/server/stream/depot、组织、项目或凭据。
- 新增 Perforce 设计、部署或任务文档时，应放在本目录并更新此索引。
- 脚本实现保留在 `scripts/perforce/`，使用说明和部署边界统一在本目录维护。
