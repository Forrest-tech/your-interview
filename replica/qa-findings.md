# Playbook 重做 QA 验收报告（分支 rabbit/playbook-redesign，20 commits）

验收时间：2026-10-16 凌晨。验收方式：代码级契约核对 + 独立构建/测试复跑。未改任何代码。

## [P0] Assets tab 缺少"粘贴文本"按钮（A6/F 未实现）

- **位置**：`web/src/app/features/playbook/playbook-detail.component.html` Assets tab（504–630 行）
- **现象**：Assets tab 只有音频文件 `<input type="file" accept="audio/*">`，没有任何创建文本资产的入口。全 repo 搜索：前端没有任何代码向 `POST /api/interviews/{id}/assets` 发送 `textContent`。
- **期望**：spec A6/F 明确要求"前端加'粘贴文本'按钮调用之"——按钮打开文本框，调用已有 endpoint（body.textContent，后端已支持，`InterviewsController.cs:142-147`）。
- **影响**：R01 的"粘贴文本→跑分析"链路后端已通（e7e8d0c），但用户在 UI 上走不到这条路，等于没修。

## [P1] Next actions 删除仍用原生 confirm()

- **位置**：`web/src/app/features/playbook/components/analysis/next-actions.component.ts:166`
- **现象**：`if (!confirm(this.t('pb.an.confirmDelete'))) return;` —— 与 B7 同类问题。原生 confirm 在自动化环境会被 dismiss（=点取消），用户之前已因此困惑过一次。
- **期望**：统一用 MatDialog（Q&A 题库已示范：`qa-confirm-dialog.component.ts`），新功能不应引入已知坑。

## [P2] 死代码清理（不影响功能，建议合 main 前清掉）

1. `playbook-detail.component.ts:743-790`：`addQuestion`/`removeWeakness` 残留的 `addQuestion`/`removeQuestion` 方法（含原生 confirm）已无人调用（Questions tab 改用 `<app-question-bank>`），约 50 行死代码。
2. `playbook-detail.component.scss:287`：`.weakness` 样式残留；`playbook.component.ts:42`：`totalWeaknesses` 接口字段残留；`api.models.ts`：`weaknessCount` 残留；`i18n.service.ts`：`pb.weakness.*` 文案 keys 残留。用户要求 Weaknesses 彻底消失，这些是最后的尾巴。
3. 后端 `GET/POST/DELETE /api/interviews/{id}/weaknesses` 三个 endpoint 仍在（spec 允许只删 UI，但用户情绪上要求"彻底"，建议评估是否连 API 一起下掉）。

## [P2] 题库增删后 tab 计数最多延迟 15 秒

- **位置**：`question-bank.component.ts` 无 output 事件通知父组件；父组件靠 15 秒轮询 `load(false)` 同步。
- **现象**：在题库里新增/删除题目后，Questions tab 标题的计数最多 15 秒后才更新。会自愈，可接受，列为 P2。

## 结论：除上述 1 个 P0 外，其余全部通过

### 抽查覆盖率

**API 契约（A1–A7 + analysis/latest）—— 全部通过**
| 契约 | 路由 | DTO 字段 | 状态 |
|---|---|---|---|
| A1 掌握度 | `PUT {id}/questions/{qid}/mastery` | `{mastery: 0-3}`，后端 `InclusiveBetween(0,3)` 校验 | ✅ |
| A2 待办 | `GET/POST/PATCH/DELETE {id}/actions[/{aid}]` | ActionDto(Id/Title/Detail?/DueDate?/IsDone/CreatedAt) | ✅ |
| A3 快照+趋势 | `GET {id}/analysis/trend`（asc，只返回 id/createdAt/overallScore） | AnalysisTrendPointDto | ✅ |
| A3 最新快照 | `GET {id}/analysis/latest`（无快照返回 null） | AnalysisSnapshotDto | ✅ |
| A3 快照写入 | `ApplyAnalysisCommandHandler` 每次成功回写记一条快照 | — | ✅ |
| A4 历史回答 | `GET {id}/questions/{qid}/history`（归一化精确匹配，按日期 desc，排除自己） | QuestionHistoryDto | ✅ |
| A5 覆盖图谱 | `GET /api/interviews/coverage`（按 askedCount desc） | CoverageCategoryDto | ✅ |
| A6 文本资产 | `POST {id}/assets` 支持 `textContent`（Kind=Text，默认 pasted-text.txt） | — | ✅ 后端 |
| A7 轮次邮件 | `PUT {id}/rounds/{rid}/emails` body `{emails: [{from,subject,date,snippet}]}` | RoundEmailItem | ✅ |
| migration | `20261016043124_AddPlaybookRedesign`：Mastery 列、QuestionsToAskJson 列、actions 表、analysis_snapshots 表 | — | ✅ |

**前端接线（F1–F6）—— 全部通过**
- F1 题库：聚合头（总数/平均难度/题型分布/卡壳数）、状态+题型筛选、排序（卡壳→待练→练习中→已掌握，难度 desc）、逐题卡片 5 段结构、行为题用 "Suggested Approach" 中性标题、卡壳原因、历史回答折叠、MatDialog 删除确认 ✅
- F2 报告：总分环 + 较上次分差（<2 个点时不显示）、结论 bullets、无快照显示"首次分析"、维度条 + SVG 雷达、逐题手风琴、证据折叠（默认收起）、底部 Next actions ✅
- F3 Brief：6 区顺序（倒计时→公司研究→JD 关键词→必答 Top5→问面试官的问题→Checklist）✅；questionsToAsk 保存用 PUT 全字段回显，无数据丢失风险 ✅
- F4 时间线：顶部"下一轮"卡片 + 垂直时间线 + 结果徽章配色 + 邮件时间线 ✅；RoundDto 含 meetingLink/scheduledTime/emailsJson/transcript ✅
- F5 总览：页头双行信息条（公司/职位/状态/倒计时 + recruiter/上次联系）、待办区、最新复盘结论、技术覆盖小条 ✅
- F6 列表页：技术覆盖图谱默认收起、懒加载 ✅
- Tab 顺序：Overview / Rounds / Guidance / Questions / Analysis / Assets / Edit ✅（与 spec 一致）
- API 路径/方法：抽查 mastery=PUT、actions=PATCH，全部与后端路由一致 ✅

**i18n 审计 —— 通过**
- playbook 相关 359 个 key 逐个核对：全部在字典中有 zh + en（fr 允许兜底，实际多数也有 fr）。0 缺失。

**独立复跑 —— 通过**
- `dotnet build src/YourInterview.sln`：0 error（16 warnings，全是 pre-existing 的 NU1902 等）
- `/tmp/testrunner`：**98/98 PASS**（54 个原有 + 44 个新增：InterviewAction/PlaybookText/QuestionMastery/QuestionsToAsk/RoundEmails）
- `ng build`：Application bundle generation complete，0 error

**R01–R08 回归 —— 通过**
- R01 上传/转写/分析状态机、R06 SpeakerLabelStage 候选人分离、粘贴文本 consumer 链路：代码均在，未动 ✅
- R02 Tracker"备战"按钮、Playbook→Mock 跳转（`goMock()`）：均在 ✅
- R03 多轮字段、R04 guidance 生成/版本、R05 TechStack 导入（import-bar 在 Questions tab 保留）：均在 ✅
- R07 轮次 emails/transcript/prep：增强未破坏 ✅

**Weaknesses 清除 —— 通过**（UI 层面）
- playbook 详情页/列表页/卡片：无任何 weakness 展示。残留仅为上述 P2 死代码。

**"不做的"清单 —— 确认都没做**
- flashcards：无；JD vs 简历对照：代码注释明确写"v1 不做"；语义相似匹配：handler 注释明确"只做归一化精确匹配"；R08 日历：无日历 UI（`rounds/upcoming` 只用于倒计时/下一轮）✅

**历史 bug**
- B8 weeklyApplied：已修（逐页拉全量再计数）✅
- B9 Mock 锚点：已修（`id="overview"` 接上）✅
- B1/B3/B7：等用户手动验证；代码侧：Q&A 删除已换 MatDialog，B7 疑似项在新路径上已消除 ✅

---

## Round 2 复验（2026-10-16，分支 rabbit/playbook-redesign @ bae2d5c，独立执行）

- **P0 粘贴文本资产**：PASS —— Assets tab 有"粘贴文本"按钮（`pasteTextAsset()`）；`PasteTextDialogComponent` 存在（textarea + `[disabled]="!text.trim()"` 空禁用确认）；确认后调 `POST /api/interviews/{id}/assets` body `{ textContent }`；成功后 `this.load(false)` 刷新；i18n `pb.asset.paste*`（pasteText/pasteTitle/pasteHint/pasteLabel/pastePlaceholder/create/textSaved）在 zh/en（及 fr）齐全。
- **P1 next-actions 无原生 confirm**：PASS —— `next-actions.component.ts` 内 `confirm(` 出现 0 次；删除走 `MatDialog` + `QaConfirmDialogComponent`（danger 样式二次确认）。
- **P2a 死代码清理**：PASS —— detail TS 中 `addQuestion/removeQuestion/QuestionForm/questionForm/emptyQuestionForm/totalWeaknesses/weaknessCount/InterviewWeakness/weaknesses` 残留 0；`.weakness` 样式 0；`pb.weakness.*` i18n 0；`InterviewDetail.weaknesses`（api.models）0；后端三个 weaknesses 路由（GET/POST/DELETE `{id}/weaknesses`）已删，Controller 内无路由残留。
  - 观察项（非阻塞）：`InterviewHandlers.cs` 内 `AddWeaknessCommand/RemoveWeaknessCommand` 及其 validator、handler（1396/1414行）仍在，已不可达（无路由调用），可找机会清掉。
- **P2b question-bank changed 输出**：PASS —— `changed = output<void>()`（61行）；删除成功后 emit（307行）、新增成功后经 `afterAdd()` emit（377行）；父模板绑定 `(changed)="onQuestionBankChanged()"`（detail html 492行）；父 handler 调 `this.load(false)` 刷新。

**独立复跑**：
- `dotnet build src/YourInterview.sln`：Build succeeded，0 error
- `/tmp/testrunner`：PASS=98 FAIL=0
- `npx ng build`（web）：exit=0

**结论：可合 main。**
