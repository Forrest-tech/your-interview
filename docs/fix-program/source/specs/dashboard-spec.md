# Dashboard 现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree `fix-program/wt-33` 实查；任务指定基线 68bc118 已被后续提交推进，见 evidence-33/STATUS.md）
> 性质：描述现状行为，不是目标设计。与既定规则不一致处见文末「现状与规则冲突」，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/dashboard`（登录后默认落地页，`web/src/app/app.routes.ts:21-23`）：一屏回答「现在什么情况、下一步做什么」。
- 无自有后端服务、无自有实体/表：纯聚合视图，前端组件 `web/src/app/features/dashboard/dashboard.component.ts`（177 行）在 `load()` 里并行拉三路接口后本地拼装（同文件 `load()`，约 :127-145）。

## 核心实体与数据真源
| 区块 | 接口 | 真源服务/口径（代码出处） |
|---|---|---|
| 行动中心 + 顶部四数（total/active/interview/offer） | `GET /api/jobs/action-center` | Jobs `GetActionCenterQueryHandler`（`src/Services.Jobs/Application/JobHandlers.cs:846` 起）；interviewed 口径 = 投递**当前状态** ∈ {Interview, Offer, Accepted} 的计数（同文件 :880 附近），**只数 Jobs 侧状态，不数 Interviews 轮次** |
| 能力雷达 + insight（headline/suggestedAction） | `GET /api/analytics/dashboard` | Analytics 快照表（见 `analytics-spec.md`）；`GetDashboardQueryHandler`（`src/Services.Analytics/Application/AnalyticsHandlers.cs:69`） |
| Upcoming 面试轮次 | `GET /api/interviews/rounds/upcoming?days=30` | Interviews `InterviewRound.ScheduledDate`（`GetUpcomingRoundsQueryHandler`，`src/Services.Interviews/Application/InterviewHandlers.cs:1458`） |

- 弱项口径：前端纯函数 `weakestRadarPoints`（`web/src/app/core/models/radar.helpers.ts`）——无 scored 数据（六维 current=0 且 best 为 null/undefined）时返回空数组、走空态；有数据时按 current 升序取前 3。此为票 27（REQ-08）修复后的现状，Dashboard 与 Analytics 共用同一 helper。
- 行动卡优先级（组件 `cards` computed）：跟进到期 > Upcoming 轮次 > 截止临近 > 弱项 > 漏斗总览；弱项「一键刷题」分流：口语类维度 → `/practice`，其余 → `/tech-stack?topic=`（组件 `drillWeakness()`）。

## 主要流程
1. 进页并行三请求，任一失败不阻断其余区块（analytics/upcoming 失败仅 `done()` 计时、dashboard 保持 null；action-center 失败写 `error` 信号）。
2. 点行动卡/弱项按上表分流跳转到 Tracker / Playbook / AI Practice / Tech Stack。

## 与既定规则对照结论
- 「同一份底层数据、多模块只是视图」：Dashboard 本身**符合**——不存数据、只聚合三服务真源，无第四份拷贝。
- 无数据不造假信号：REQ-08 已对齐（票 27，helper 守卫 + Analytics 同步采用）。

## 现状与规则冲突
- **CONF-DASH-1（→ AUDIT REQ-02，票 04 已部分修复、残留）**：同屏两个「面试」口径仍不同源——顶部 interview 数来自 Jobs 投递当前状态，Upcoming 来自 Interviews 轮次排期。票 04 已补「结果回写」（结构化 outcome 枚举 → Jobs 轮次投影 + 状态推进，`Services.Jobs/Application/EventConsumers.cs:22`），但**已排期未出结果**的轮次不推进 Jobs 状态：排了面试、状态仍 Saved 时，Upcoming 有轮次而 interview 数为 0 的同屏矛盾仍可出现。
- **CONF-DASH-2（→ AUDIT FUN-07，票 35 未实施）**：弱项区仍有硬编码中文残留（AUDIT 证据 `dashboard.component.html:115`「口语练习」等），EN/FR 界面直出中文。
