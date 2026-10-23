# 日历（应用内日历）现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-17
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。日历在 Playbook 定稿中只有原则性提及（应用内日历 + 提醒、.ics 导出取消），本规格按代码把实现面落全。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/playbook/calendar`：全局面试日历月视图——把全部 Playbook 条目的**未来轮次排期**铺到月历格子上，点击直达条目详情轮次页签。
- 无自有后端、无自有实体：日历是 Interviews 轮次排期的一个视图（与 Dashboard Upcoming、Playbook 列表 upcoming 共用同一接口与口径）。
- 前端 `web/src/app/features/playbook/playbook-calendar.component.ts`（140 行）。

## 核心实体与数据真源
- 唯一数据源：`GET /api/interviews/rounds/upcoming?days=60`（组件 `reload()`，playbook-calendar.component.ts:108 附近写死 60 天）。
- 后端口径 = `GetUpcomingRoundsQueryHandler`（`src/Services.Interviews/Application/InterviewHandlers.cs:1458`）：跨当前用户全部 InterviewEntry 的 Rounds，过滤 `ScheduledDate ∈ [今天, 今天+Days]`（UTC 日期），按 ScheduledDate、ScheduledTime 排序；返回 UpcomingRoundDto（RoundId/EntryId/CompanyName/Role/Order/Stage/ScheduledDate/ScheduledTime/Interviewers/Format/Location/MeetingLink/Outcome，DTO 定义同文件 :53）。**过去的轮次永不出现在日历上**——历史轮次只能在条目详情里看（现状口径，非缺陷判定，见对照结论）。
- 轮次排期的真源字段在 Interviews：`InterviewRound.ScheduledDate`（DateOnly）+ `ScheduledTime`（字符串，可空）——注意时间是自由字符串不是时刻类型，月视图只用日期部分分组（前端 `roundsByDate` 按 `scheduledDate.slice(0,10)` 分组）。
- 月历结构：固定 42 格（6 周 × 7 天）、**周一起始**（组件 `weeks` computed，getDay 转换注释明示）、月份切换/回今天为纯前端状态（viewMonth 信号），切换月份**不重新请求**——数据窗口恒为「今天起 60 天」，翻到窗口外月份即空历（见冲突 CONF-CAL-1）。

## 主要流程
1. 进页拉 upcoming 60 天 → 按日期分组 → 渲染月格，格内显示轮次 chip（公司/阶段）。
2. 点 chip → `navigate(['/playbook', entryId], {tab:'rounds'})` 跳条目详情（组件 `goRound()`）。
3. 轮次的创建/改期不在日历页发生：一律在 Playbook 条目内（统一入口添加材料解析出邀约时间 → 建 Round 并「同步应用内日历」，Playbook 定稿 §二——其实现即轮次 ScheduledDate 落库后本页自然可见，无独立日历事件实体/同步任务）。

## 与既定规则对照结论
- 「日历 = 应用内日历 + 提醒（提前 1 天 + 提前 1 小时）」（Playbook 定稿 §二/全局规则②）：**应用内日历部分符合**（即本页 + Dashboard/列表 upcoming）；**提醒部分在代码中无实现证据**——全仓实查 Interviews 侧无 reminder 实体/调度/推送通道（grep reminder 仅命中备考框架文案），定稿 U3「提醒触发」在 Playbook 审计中本就列为环境受限未实测项；现状定性：提醒未落地，见冲突 CONF-CAL-2。
- .ics：Playbook 定稿已于 2026-10-21 **取消** .ics 导出（定稿 §二注），但后端仍保留轮次 .ics 导出端点——见冲突 CONF-CAL-3。

## 现状与规则冲突
- **CONF-CAL-1（规格缺失衍生，无 AUDIT 编号，索引回 REQ-17）**：数据窗口（今天起 60 天）与月视图导航（可任意翻月）不对齐：翻到 60 天外或任一过去月份，格子恒空且无任何提示，用户易误判「没有安排」。另 Dashboard 用 days=30、本页用 60、Playbook 列表另有调用——同一 upcoming 概念三处窗口参数不一致（30/60/组件默认），无统一口径定义。
- **CONF-CAL-2（→ Playbook 定稿 §二 提醒口径 / 定稿审计 U3）**：提醒（提前 1 天 + 提前 1 小时）无后端实现：无提醒实体、无调度器、无推送/站内通知通道；「已加入日历 + 提醒」在 Round 卡头（定稿 §四）中，日历在位、提醒缺位。
- **CONF-CAL-3（→ Playbook 定稿 2026-10-21 取消 .ics 的裁决）**：后端仍有 `GET /api/interviews/{id}/rounds/{roundId}/ics` 端点（`src/Services.Interviews/Api/InterviewsController.cs:656`，注释自称「v2:导出本轮日历 .ics（提醒提前 1 天 + 提前 1 小时）」，生成带 VALARM 的 .ics 文件下载）——与定稿「只有应用内日历、不做导出」裁决不一致的残留实现（前端 v2 是否还有入口属 Playbook 验收面，本规格只记后端端点仍在位这一事实）。
- **CONF-CAL-4（→ AUDIT FUN-07，票 35 未实施）**：星期头为硬编码中文 `['一','二','三','四','五','六','日']`（playbook-calendar.component.ts weekHeaders），EN/FR 界面直出。
