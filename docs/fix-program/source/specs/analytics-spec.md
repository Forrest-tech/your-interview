# Analytics 现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16、REQ-18
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/analytics`：能力雷达（六维）、求职漏斗、主题掌握度分布、能力趋势四区块；Dashboard 的雷达/insight 亦由本服务供数。
- 后端独立 Analytics 服务（CQRS 读模型侧），路由 `api/analytics`（`src/Services.Analytics/Api/AnalyticsController.cs:18`）：仅 `dashboard`、`radar`、`pipeline` 三个读端点 + `ability`（写快照）、`reset`（清读模型）两个写端点。**没有 trend 端点**（见冲突 CONF-AN-1）。

## 核心实体与数据真源（关键口径：本服务不存业务事实）
- 三张快照表 + 一张幂等台账（`src/Services.Analytics/Domain/AnalyticsReadModels.cs`，文件头注释明示设计原则「不存业务事实、每张表都能从集成事件重建」）：
  - **AbilitySnapshot**：UserId/Date/Dimension（六维 key：pronunciation/fluency/sentenceIntegrity/structure/technicalDepth/relevance）/Score（0-100 clamp）/Source（mock/interview/knowledge）。
  - **PipelineSnapshot**：UserId/Date + Saved/Applied/Screening/Interviewing/Offered/Rejected 六计数。**口径（本规格首次落字，解 AUDIT OBS-4 之惑）**：由状态流转事件**逐日增量计数**（`EventConsumers.cs` JobApplicationStatusChangedConsumer：每收到一次 ToStatus=X 的事件，当日对应桶 +1）——即「当日到达某状态的次数」，**不是**「当前处于某状态的投递数」。故与 Jobs `stats`（当前状态计数）天然不同口径，两数不一致不必然是 bug。
  - **MasteryBreakdown**：UserId/Date/Topic/Total/Mastered/Learning/Fresh。MasteryRate 分母 0 返回 null（非 0）。
  - **ProcessedEvent**：EventId 幂等台账，at-least-once 投递下防重复计数。
- 数据入口只有两条：①集成事件消费者（`Application/EventConsumers.cs`：MockAnswerSubmitted / KnowledgeLevelChanged / JobApplicationStatusChanged / InterviewAnalysisCompleted / AuditEventRecorded，统一基类 AnalyticsConsumerBase 带幂等）；②`POST /api/analytics/ability` 人工/服务回写——归属口径经票 56/06 定版：普通用户只能写自己（Me），仅带 `analytics.writeback` 可信 claim 的服务通道可传 `?ownerUserId=` 代写，请求体 UserId 一律不信（`Application/AbilityOwnership.cs`）。
- 读口径：`GetDashboardQueryHandler`（`Application/AnalyticsHandlers.cs:69`）直读三张本地快照表组装 Radar/Trends/Pipeline/Mastery/Insight，TrendDays 校验 7-365（默认 30）；**从不回查 Knowledge/Jobs/Interviews 真源**——这是 CQRS 设计本体，也是 REQ-18 冲突的根（见文末）。

## 主要流程
1. 业务服务发生评分/状态/掌握度变化 → 发集成事件 → Analytics 消费者幂等落快照（当日同维同源 upsert）。
2. 前端 `/analytics` 并行拉 `dashboard` 与（不存在的）`ability/trend`；Dashboard 页只拉 `dashboard`。
3. `POST reset` 清当前用户全部快照行（重建靠事件重放，无前端入口）。

## 与既定规则对照结论
- 「同一份底层数据、多模块只是视图」：**字面不符合、设计上有意为之**——Analytics 是事件驱动的复制式投影，领域注释自认读模型定位。规则与架构的张力未裁决，现状按「投影可重建、真源在业务服务」理解；分叉风险见冲突清单。
- 无数据假信号（REQ-08）：已对齐（票 27，Dashboard/Analytics 共用 `radar.helpers.ts` 守卫）。

## 现状与规则冲突
- **CONF-AN-1（→ AUDIT FUN-08 + COD-21，票 36 未实施）**：前端恒调用 `GET /api/analytics/ability/trend?days=30`（`analytics.component.ts:170`），后端无此路由 → 趋势区块恒走 `trendUnavailable` 降级文案（现文案已三语化，`analytics.component.html:170`）。接口缺失是现状，不是前端 bug。
- **CONF-AN-2（→ AUDIT REQ-18 + OBS-4，票 34 未实施）**：快照投影与真源可分叉：事件丢失/未达即雷达、mastery、漏斗与 Knowledge/Jobs 不一致，且无对账/重建入口（reset 只清不清真源重算）。OBS-4 的漏斗差现已由本规格 PipelineSnapshot 口径定义澄清（累计到达 vs 当前状态），但 mastery 分叉仍无口径工具可判。
- **CONF-AN-3（→ REQ-18 子项，代码实查新发现，索引回 REQ-18 不新造 AUDIT 编号）**：`KnowledgeLevelChangedConsumer` 更新当日已有 MasteryBreakdown 时调用 `bucket.Update(bucket.Total, bucket.Mastered, bucket.Learning, bucket.Fresh)`（`EventConsumers.cs` KnowledgeLevelChangedConsumer 分支）——以原值回写，**计数不随本次等级变化迁移**；只有当日首条事件建桶时按该事件等级计数。故同日多次掌握度变化后分布计数可与实际不符，是 CONF-AN-2 的一个具体成因点。
