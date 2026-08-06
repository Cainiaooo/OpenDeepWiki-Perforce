# OpenDeepWiki Perforce 三期：UE 项目知识库目标与实施路线图

**状态**：进行中（WP1 Scope/快照、WP2 分层生成、WP3 知识包摄取已落地；WP4/WP5 待实施）  
**适用范围**：由 Perforce 管理的超大型 Unreal Engine 项目  
**前置条件**：一期外部变更注入和二期 changelist 区间拉取/过滤管线已经可用

本文档是三期工作的总入口，只维护目标、架构边界、里程碑和任务包索引。具体设计、工作项和验收标准拆分到 `phase3/` 子目录，避免一份任务文档同时承担产品说明、详细设计和测试计划。

## 1. 产品定位

三期不再把 OpenDeepWiki 只视为“把代码生成网页”的工具，而是把它建设成面向 UE 项目的**版本化知识控制面**：

- 用 Perforce changelist 和可验证工作区快照描述知识对应的源码版本。
- 只为游戏项目侧的有效范围生成 Wiki，同时允许 Agent 按需查证引擎和共享依赖。
- 把大型项目拆成可独立规划、生成、失败和发布的领域知识单元。
- 同一套知识通过 Web、搜索和 MCP 服务于 AI CR、UE 编辑器 Agent 及人员阅读。
- 接纳 UE 导出的结构化资产元数据，避免把不可读的 `.uasset`、`.umap` 当作已理解内容。

OpenDeepWiki 继续承担仓库接入、任务调度、版本、权限、发布、Web 和 MCP 等控制面职责。文档生成算法通过稳定接口演进，可保留当前实现，也可引入 CodeWiki 风格的分层生成引擎进行对照和替换。

## 2. 目标用户与成功结果

| 使用者 | 主要诉求 | 三期应交付的结果 |
|---|---|---|
| AI Code Review Agent | 快速理解变更涉及的模块、约束、生命周期和测试 | 按 changed files + CL 返回带来源的评审上下文 |
| UE 编辑器 Agent / 策划 | 在只有特定打包编辑器时理解项目能力并安全操作 | 按 Build CL、任务和当前资产返回兼容知识，并与 UE MCP 协同 |
| 新人和跨模块开发者 | 建立领域地图，理解入口、依赖和关键流程 | 稳定的模块概览、跨模块关系和渐进阅读路径 |
| 平台维护者 | 控制成本、范围、质量、版本和失败恢复 | 可预览 Scope、覆盖审计、原子发布、可回滚快照和可观测任务 |

成功不以“生成了多少页”衡量，而以以下结果衡量：

1. 来源和版本可证明，不把猜测包装成事实。
2. 关键项目能力有覆盖，非目标引擎目录不会污染主体 Wiki。
3. 单个领域失败不会阻断其他领域，旧的完整版本仍可服务。
4. 增量更新只影响必要知识单元，并能解释为什么需要更新。
5. AI CR 和 UE 编辑器任务在实测中因知识库获得可量化提升。

## 3. 知识分层

知识库必须区分不同可信度和维护方式，不能全部作为自由文本交给 Agent 重写。

| 层级 | 示例 | 维护方式 |
|---|---|---|
| 确定性事实 | P4 revision、文件、模块/插件清单、接口签名、UE 导出元数据 | 程序生成，可重复验证 |
| AI 综合知识 | 架构、流程、跨模块交互、使用方式 | Agent 生成，必须携带来源和 snapshot ID |
| 人类意图与约束 | 设计原因、不变量、所有权、禁止事项 | 人类拥有；Agent 只能提议或经批准更新 |
| 运行时事实 | 当前关卡、选中对象、PIE 状态、实例属性 | 由 UE MCP 实时查询，不持久化为长期事实 |

## 4. 总体架构

```text
Perforce Workspace / Changelist
              │
              ▼
Scope + Snapshot Control
WorkspaceScope / DocumentScope / ContextScope / ChangeTriggerScope
              │
      ┌───────┴────────┐
      ▼                ▼
Source Inventory    UE Metadata Export
C++ / AS / Config   Reflection / Asset Schema / Tags / Tool Contracts
      └───────┬────────┘
              ▼
Generation Engine Boundary
Inventory → Domain Planning → Leaf Generation → Merge → Coverage Audit
              │
              ▼
Staging Generation → Atomic Published Wiki Snapshot
              │
     ┌────────┼───────────┐
     ▼        ▼           ▼
 AI CR MCP  UE Task MCP  Web / Search / Onboarding
                 │
                 ▼
             Live UE MCP
```

核心原则：

- **先确定边界，再调用模型**：文件身份、Scope、模块清单和版本由确定性逻辑决定。
- **先拆领域，再生成页面**：不让一次浅层目录观察决定整个超大项目的 Wiki。
- **正文与范围绑定**：每个叶子页必须持久化 `ScopeManifest`，重试时不能重新猜边界。
- **生成与发布分离**：所有内容先进入 staging，覆盖审计通过后原子切换当前版本。
- **稳定知识与实时状态分离**：OpenDeepWiki 提供项目模型，UE MCP 提供当前编辑器事实与执行能力。

## 5. 范围模型

| 概念 | 含义 |
|---|---|
| `WorkspaceScope` | 完整、只读且具有版本身份的工作区边界 |
| `DocumentScope` | 允许进入结构分析、主题规划和正文生成的主要范围 |
| `ChangeTriggerScope` | 变化后可以触发增量分析的范围，默认继承 `DocumentScope` |
| `ContextScope` | Agent 可按需查证但不能独立产生 Wiki 页面的参考范围 |

默认策略：

- 全量生成只枚举 `DocumentScope`，不递归扫描完整工作区。
- Agent 先搜索 Document，证据不足时才能显式、有限额地进入 Context。
- Context 可以成为引用，但不能因为被读取而扩张 Wiki 主题。
- P4 默认只接受 tracked 且与 `#have` 一致的内容；opened/untracked 内容必须采用显式策略。
- `.uasset`、`.umap` 不直接进入文本生成范围，其有效信息必须通过 UE Metadata Export 进入事实层。

范围模型、配置示例和工作区一致性详见 [`01_SCOPE_AND_SNAPSHOT.md`](phase3/01_SCOPE_AND_SNAPSHOT.md)。

## 6. 任务包与依赖

| 工作包 | 目标 | 主要依赖 | 可独立交付 |
|---|---|---|---|
| WP1 Scope 与快照 | 建立统一范围、文件身份和原子发布基础 | Perforce 二期 | 是 |
| WP2 分层生成 | 支持超大 DocumentScope 的领域拆分和覆盖审计 | WP1 | 是 |
| WP3 UE 事实导出 | 将代码之外的 UE 项目结构变为可信输入 | WP1；可与 WP2 并行 | 是 |
| WP4 上下文交付 | 面向 AI CR、编辑器 Agent 和新人提供任务化 MCP | WP1；逐步消费 WP2/WP3 | 是 |
| WP5 增量、运维与质量 | 依赖感知更新、可观测性、兼容和发布门禁 | WP1–WP4 | 分阶段 |

详细任务：

1. [`01_SCOPE_AND_SNAPSHOT.md`](phase3/01_SCOPE_AND_SNAPSHOT.md)
2. [`02_HIERARCHICAL_GENERATION.md`](phase3/02_HIERARCHICAL_GENERATION.md)
3. [`03_UE_KNOWLEDGE_EXPORT.md`](phase3/03_UE_KNOWLEDGE_EXPORT.md)
4. [`04_AGENT_CONTEXT_DELIVERY.md`](phase3/04_AGENT_CONTEXT_DELIVERY.md)
5. [`05_INCREMENTAL_OPERATIONS_AND_QUALITY.md`](phase3/05_INCREMENTAL_OPERATIONS_AND_QUALITY.md)

### 6.1 最小纵向切片（首个可交付路径）

五个工作包合计接近一次平台级改造，禁止按文档顺序全面铺开。首个端到端切片建议限定为：

1. `RepositoryScopeConfiguration` 以仓库配置形式加载并解析（管理 API、预览和审计可后置）。
2. `IRepositoryFileSelectionPolicy` 落地，接管现有 Perforce 过滤和全量扫描的文件判定。
3. 确定性 Source Inventory 输出模块/文件清单。
4. 覆盖审计先以报告形式输出，不接入发布门禁。

Workspace Lease、staging 原子发布、生成引擎抽象、UE 导出和新 MCP 契约均在该切片验证后再展开。M0 对照实验只依赖第 1–3 项。

实现前可查阅 [`IMPLEMENTATION_REFERENCES.md`](phase3/IMPLEMENTATION_REFERENCES.md)。外部项目只作为设计参考；引入代码前必须单独核对许可证、依赖、安全和维护成本。

## 7. 里程碑

### M0：生成器对照实验

在真实但去标识的代表性 UE Scope 上比较当前引擎和候选方案，先验证分层生成是否带来实际收益。计划见 [`GENERATOR_BAKEOFF_PLAN.md`](phase3/GENERATOR_BAKEOFF_PLAN.md)。

M0 不阻塞 WP1 的范围与快照基础建设，但会影响 WP2 的具体生成器选型。

### M1：可信边界

- Scope 配置可创建、预览、版本化和审计。
- 全量/增量使用同一选择策略。
- 任务绑定可验证的 P4 内容清单和配置版本。
- staging generation 可以完整失败而不污染当前发布版本。

### M2：可扩展生成

- 通过确定性清单拆分 UE 模块/插件/领域。
- 各领域独立预算、生成、重试和失败。
- 每页绑定 Scope Manifest 和结构化来源。
- 发布前产生覆盖审计和完整性状态。

### M3：UE 知识补全

- UE 工具链可导出版本化、可校验的项目元数据包。
- Wiki 明确区分源码事实、导出事实、AI 综合和未知项。
- 资产驱动工作流不再仅依据 C++ 声明推断实际配置。

### M4：面向任务的上下文服务

- AI CR、UE 编辑器任务和新人导览有独立 MCP 契约。
- UE Build CL 与 Wiki Snapshot 完成版本握手。
- 返回结果带来源、覆盖状态、版本兼容性和置信边界。

### M5：持续维护闭环

- P4 变更可以映射到领域、页面和消费者上下文。
- Context 或 UE 元数据变化可按已记录依赖触发相关页面，而不是生成自己的页面。
- 有质量回归集、成本/耗时观测和发布门禁。

## 8. 三期非目标

- 不为整个 UE 引擎和所有第三方依赖生成主体 Wiki。
- 不允许生成 Agent 修改 Perforce 工作区源文件。
- 不把特定私有项目目录或插件名称硬编码进通用产品。
- 不在首个里程碑构建完整的符号级全仓调用图。
- 不把实时编辑器状态保存成脱离会话的长期知识。
- 不以替换 UE MCP 为目标；OpenDeepWiki 与 UE MCP 分工协作。
- 不承诺由 AI 自动创造缺失的人类设计意图。

## 9. 全局完成标准

- Wiki 主体严格限制在一个或多个 `DocumentScope`，完整工作区仅作为受控上下文。
- 每个发布版本对应唯一的 Scope 配置版本、P4 内容清单和生成版本。
- 超大项目按领域独立处理，不依赖一次全局浅层目录快照。
- 每个叶子页具有稳定身份、Scope Manifest、来源记录和覆盖状态。
- UE 二进制资产相关结论来自结构化导出或明确标记为未知，不允许凭空推断。
- 全量、增量、回退和手动重建使用同一文件选择与版本语义。
- 失败、取消、预算截断和 workspace 漂移不会被报告为完整成功。
- AI CR 和编辑器 Agent 只能取得与请求 CL/Build 兼容的知识版本。
- Git、Archive、LocalDirectory 等既有来源在无 Scope 配置时保持兼容。
- 文档、日志、测试夹具和示例不包含真实用户、主机、路径、项目或凭据。

## 10. 实施与提交边界

- 每个工作包再拆成可回归验证的提交，禁止一次提交横跨所有管线。
- 来源无关能力放在通用层；Perforce 适配只处理 depot/workspace、CL 和 filetype 语义。
- 生成引擎通过接口接入，避免把某个候选项目的数据结构扩散到领域实体和 API。
- 对上游已有同类能力优先适配，不在 fork 内维护重复实现。
- 新能力优先落在新表/新实体，通过外键关联 `DocCatalog`、`DocFile`、`BranchLanguage` 等上游活跃表；非必要不修改上游表结构和唯一约束，降低同步上游时的迁移冲突。
- 本 fork 新增的 EF 迁移使用可识别的名称前缀（如 `P4Phase3`），便于同步上游后甄别、重放或重建。
- 每次同步上游后运行所有来源类型回归，重点确保默认 Git 流程未被 P4 Scope 逻辑改变。

## 11. 隐私和示例约定

- 示例统一使用 `ExampleWorkspace`、`SampleProject`、`//depot/SampleProject/...`。
- 配置、日志和夹具不得包含真实账号、server、client、stream、depot 或绝对路径。
- `P4TICKETS`、API token 和服务地址只能通过外部密钥配置注入。
- 私有敏感词扫描模式保存在本地或 CI 私密配置中，不提交到公开仓库。
