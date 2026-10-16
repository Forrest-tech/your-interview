# Playbook 重做方案 v1（P0+P1+资产库）

产品定位：**面试飞行记录仪 + 教练**。每次面试沉淀为下一次的训练数据。
分支：`rabbit/playbook-redesign`。用户规则：分支开发，他点头才合 main；自己测完再报结果。

## API 契约（后端先实现，前端按契约并行）

### A1. 题目掌握度
- `InterviewQuestion` 新增 `Mastery int`（0=待练Todo, 1=练习中Practicing, 2=已掌握Mastered, 3=还卡壳Stuck），migration。
- `PUT /api/interviews/{id}/questions/{qid}/mastery` body `{ mastery: int }`。

### A2. Next actions（待办）
- 新实体 `InterviewAction`：Id, InterviewEntryId, Title, Detail?, DueDate?, IsDone, CreatedAt。
- `GET /api/interviews/{id}/actions` → 列表；`POST` body `{title, detail?, dueDate?}`；
  `PATCH /api/interviews/{id}/actions/{aid}` body `{isDone?, title?, dueDate?}`；
  `DELETE /api/interviews/{id}/actions/{aid}`。

### A3. 分析快照（趋势）
- 新实体 `AnalysisSnapshot`：Id, InterviewEntryId, CreatedAt, OverallScore int?, DimensionsJson（6维）, SummaryBulletsJson（3~5条一句话结论）。
- 每次分析完成时写入一条（ consumer/ApplyAnalysis 处）。
- `GET /api/interviews/{id}/analysis/trend` → 按时间 asc 的快照列表（id, createdAt, overallScore）。

### A4. 题目历史回答（回答库）
- `GET /api/interviews/{id}/questions/{qid}/history` → 该题在**其他面试**中的回答版本：
  按 normalized question text（小写去标点）匹配，返回 `[{ interviewId, companyName, role, interviewDate, myAnswerText, mastery }]`，按日期 desc。

### A5. 技术覆盖图谱
- `GET /api/interviews/coverage` → 全局聚合：`[{ category, askedCount, stuckCount, masteredCount, interviews: [companyNames...] }]` 按 askedCount desc。

### A6. 文本资产入口（确认现有 endpoint）
- 已有 `POST /api/interviews/{id}/assets` 支持文本？P1 修过 consumer 文本链路；前端加"粘贴文本"按钮调用之。若 endpoint 缺 `textContent` 参数则补。

### A7. 联系人/邮件时间线
- `InterviewRound.EmailsJson` 已有；补 `PUT /api/interviews/{id}/rounds/{rid}/emails` body `{ emails: [{from, subject, date, snippet}] }`（若无则加）。前端在 Rounds 时间线内展示。

## 前端重做（按 tab）

### F1. Q&A 题库（P0）
- 顶部聚合头：题目总数 | 平均难度★ | 题型分布条（各 category 计数）| 卡壳 n 道。
- 筛选条：状态（全部/卡壳/待练/已掌握）+ 题型（全部/各 category）；默认排序：卡壳优先 → 待练 → 难度 desc。
- 逐题卡片（固定结构）：① 题型标签 + 题干 + 难度★ + 掌握度徽章（灰待练/蓝练习中/绿已掌握/红卡壳，可点切换）② 我的回答（可编辑 textarea）③ 参考答案（RecommendedAnswer；行为题标题用中性 "Suggested Approach"）④ 卡壳原因（GotStuck 时显示 StuckReason）⑤ "历史回答"折叠区（调 A4，同题在其他公司的回答版本对比）。
- 保留：新增题目表单、删除（B7 的 confirm 改成 MatDialog 自定义确认框，顺手修掉原生 confirm 自动化问题）。

### F2. Analysis 报告（P0）
- 页顶结论区：左侧总分环（大数字 + 较上次快照 ↑/↓n 分）+ 右侧 3~5 条结论 bullets（SummaryBulletsJson；无快照时显示"首次分析"）。
- 中部：六个维度横向条形（已有，保留样式）+ 轻量 SVG 雷达图。
- 底部：逐题手风琴（复用 F1 卡片组件只读版）+ "证据"折叠区（计分文本/转写，默认收起）。
- 最底部 Next actions：AI/手动添加的 3~5 条可勾选事项（调 A2），勾选同步到 Overview 待办。

### F3. Guidance 一页式 Brief（P1）
自上而下固定顺序，全部可编辑，一页看完：
1. 倒计时条（下一轮 ScheduledDate + Format/MeetingLink/Location）
2. 公司研究（CompanyProfile，3~5 条要点）
3. JD 关键词区（JdText 简单关键词高亮：取 JD 高频实词黄底显示；v1 不做简历对照）
4. 必答题 Top5（按卡壳优先+难度取 5 道，一句话答案要点，可跳 Q&A）
5. "我要问面试官的问题"清单（InterviewEntry 新增 `QuestionsToAskJson`，可增删）
6. Checklist（复用 A2 actions 中 tag=checklist 的事项，或独立小清单：着装/设备/感谢信草稿）

### F4. Rounds 垂直时间线（P1）
- 顶部"下一轮"卡片（最近的未过期 round：倒计时 + Brief 入口跳 Guidance）。
- 垂直时间线：每轮 = 日期 + Stage + 面试官 + 结果徽章（Outcome：绿 Passed/黄 Pending/红 Rejected/Ghosted/灰其他）+ Feedback 摘要 + Notes + 邮件时间线（调 A7，按日期列 recruiter 往来）+ Transcript 折叠。
- 保留：Add round / Edit round 表单。

### F5. Overview 备战总览（P0+P1 整合）
- 页头信息条（两行内）：公司+职位+状态徽章+下一轮倒计时；第二行：recruiter（从 EmailsJson 取最近发件人）+ 上次联系时间。
- 待办区（A2 未完成事项，跨 tab 同步）。
- 最新复盘结论（最近快照的 3 条 bullets）。
- 技术覆盖（本面试各 category 计数小条）。

### F6. Playbook 列表页
- 新增"技术覆盖图谱"区（调 A5）：每类一行：类别名 | 被问 n 次 | 掌握度小条（绿/红）| 涉及公司 chips。默认收起，可展开。

## 历史欠账 bug（本轮顺手修）
- B8: tracker `weeklyApplied` 只统计当前页 → 改为调后端统计或全量 items（看现有 API）。
- B9: Mock 页 overview 侧边锚点未接线 → 接上。
- B1/B3/B7：等用户手动验证（删除按钮亲手点），不动代码——F1 把 confirm 换成 MatDialog 后 B7 疑似项自动消除。
- R08 全局日历：不在本轮范围，记为 follow-up。

## 测试要求
- 后端：新实体/API 加单元测试（沿用现有测试风格）；`dotnet build` 0 error。
- 前端：`ng build` 通过。
- 回归：用 tunnel 跑一遍 Playbook 全 tab（只读+新建测试数据验证新功能），确认无报错再报结果。
- i18n：新增文案至少 en/zh（沿用现有 t()/tn() 机制）。

## 不做的
- JD 关键词 vs 简历自动对照（v2）
- 回答库语义相似匹配（v1 只做 normalized 文本精确匹配）
- flashcards 速记模式（P2，v2）
- R08 日历（follow-up）
