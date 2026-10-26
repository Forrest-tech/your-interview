# Tracker（求职跟踪）自有现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-17
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：本产品的自有定稿（现状行为 + 口径定义）。`recon-simplify-tracker.md` / `simplify-tracker-fresh-recon-20261018.md` 是竞品侦察，**不是**本规格依据。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/tracker`：投递全生命周期管理——公司/投递 CRUD、看板（按状态列）与列表双视图、详情抽屉（Overview/Documents/Resume Score/Email/Questions 五页签）、Smart paste / Smart add（JD 粘贴解析建单）、LinkedIn 抓取、批量操作、收藏、归档、求职信、沟通记录、Answer Library（答题模板）、备份导出/导入、周目标。
- 前端 `web/src/app/features/tracker/tracker.component.ts`（3613 行，含 4 个组件类——ARC-01 现状）；后端 Jobs 服务，路由 `api/jobs`（`src/Services.Jobs/Api/JobsController.cs:13`，类级 `[Authorize]` + 逐端点 JobsRead/Write/Delete 策略；全部数据按 UserId 隔离 + 全局过滤器，票 03 后现状）。

## 核心实体与数据真源（Jobs 库）
- **Company**（`src/Services.Jobs/Domain/JobAggregates.cs` Company 类）：Name/Website/Industry/Location/LogoUrl（由官网 host 经 Google favicon 服务推导）/CompanyType（DirectEmployer/Agency/Staffing/Unknown）/EmployeeCount/IsBlacklisted/**Profile（公司情报长文本）+ ProfileSourcesJson**——公司情报按公司存一份、多岗位共享（实体注释明示此口径）。
- **JobApplication**（同文件 JobApplication 类）：CompanyId、Role、Location、Link、Salary、**WorkMode**、Source、JdSummary、**JdText（text 列，长度闸门 200,000 字符统一口径，票 11）**、CompanyDescription、WorkAuthorization、JobType、Requirements、Benefits、Status、Priority（Low/Medium/High/Critical）、AppliedDate、Deadline、NextFollowUpAt、ResumeScore/PassRateEstimate/MatchKeywords、PosterName、NeedsConnectFirst、OutreachMessage/OutreachSentAt、RejectionReason、IsArchived、Notes（含 `[DOCS:]` 文档名前缀的兼容约定，票 09 composeNotes）。
- **子实体**：ApplicationStatusChange（状态史）、InterviewRound（**Jobs 侧轮次投影**，见同源规则）、ApplicationCommunication（Type/Subject/Content/ContactName/ContactEmail/OccurredAt/MetaJson/HandlingStatus——与 Playbook 沟通记录同一份数据，Jobs 为真源）、ApplicationAnswerTemplate（Answer Library）、CoverLetter（一投递一封，Status/GeneratedByModel/ResumeVersion）。
- **用户级资产**：UserResume / ResumeFile（见 `resume-spec.md`）、ApplicationFavorite 与 ApplicationQuestion（收藏与按投递自备问题，票 14 上后端的两表，`Domain/TrackerPreferences.cs`；前端收敛为单一 TrackerPreferencesService 读写，首次加载把 localStorage 旧数据一次性导入、本地键保留不清除）。

## 状态机（字段口径 · 唯一定义处）
- 枚举 **ApplicationStatus**（`JobAggregates.cs:576`）：Saved=0 / Applied=1 / Screen=2 / Interview=3 / Offer=4 / Accepted=5 / Rejected=6 / Ghosted=7 / Paused=8 / Withdrawn=9。
- 合法流转表 = 聚合内 `AllowedTransitions`（同文件 :206-218），`ChangeStatus` 越界直接抛 InvalidOperationException（:270）；要点：Accepted 为终态（无出边）；Rejected/Ghosted/Withdrawn 均可回 Saved 重开；Paused 可回 Saved/Applied/Withdrawn；Offer 只能去 Accepted/Rejected/Paused。
- 初始状态落库走 `TryWalkToStatus`（BFS 沿合法边逐级流转、每级照发事件，同文件 :230）——导入/建单指定跨级状态（如 Saved→Interview）不丢状态（F-09 修复口径）。
- 流转副作用：进 Applied 且 AppliedDate 为空 → 自动补当天；进 Rejected/Ghosted → 记录 RejectionReason；直接切 Interview 且非建轮次路径 → 按现有轮次数 +1 发「下一轮」邀请事件（RaiseInvitation）。
- **Jobs 侧 InterviewRound 是投影不是主数据**：RoundOutcome 枚举 Pending/Passed/Failed/Cancelled/NoShow/Ghosted（同文件 :593）；轮次主数据在 Interviews 服务（ARC-07 双表结构的现状定性，见冲突 CONF-TR-1）。
- 前端视图口径：看板主列只显 Saved/Applied/Screen/Interview/Offer 五列（`tracker.component.ts:3031` statusOrder），其余状态经筛选/列表达；建单状态选项子集 = Saved/Applied/Screen/Interview/Paused/Rejected（同文件 :46）。

## 与 Playbook 同源规则（口径定义）
1. **关联键 = JobApplicationId**：Interviews 条目以 JobApplicationId 关联投递；Tracker 点 Prep 对已有条目**幂等打开不新建**（后端 CreateEntry 按 (UserId, JobApplicationId) 去重返回既有，票 10/FUN-03 修复后现状）。
2. **结果回写单向**：Playbook 轮次出结果 → `InterviewOutcomeRecorded` 集成事件 → Jobs 消费者以**结构化 outcome 枚举为准**推进轮次投影与投递状态，自由文本 Result 只作展示；无枚举的旧事件才走 `LegacyOutcomeTextAdapter` 关键词回落，且该映射**只许存在于此一处**（`Services.Jobs/Application/EventConsumers.cs:22` 文件头口径，票 04/58）。
3. **沟通同源**：Tracker Email 页签与 Playbook 沟通记录是同一份 ApplicationCommunication / Interviews 材料数据，两处可看不复制（Playbook 定稿 §五）。
4. **简历同源**：抽屉 Documents 的有效简历口径见 `resume-spec.md`（tailored → 基础文件 → 基础文本）。

## 主要流程
1. 建单三路：手动表单（jdText/notes 随 POST 一并落库，票 09 后现状）、Smart paste（`parse-jd` 规则解析、可选 AI 精修 → `smart-add`；解析口径票 29 已补内联 Benefits/摘要跳元数据行/公司名形态）、bulk-import 批量导入。
2. 详情抽屉五页签：Overview 字段网格 + JD 正文编辑/重解析；Documents（简历/求职信）；Resume Score（读 jdText||jdSummary 与 Overview 同源，F-08 口径）；Email（沟通 CRUD + invite 解析建轮次 `applications/{id}/invite`）；Questions（自备问题，后端表）。
3. 批量：勾选 → bulk-bar 改状态/归档/删除（现状为横向工具条形态，`tracker.component.html:202` bulk-bar 卡片，scss :604 起——REQ-11 的「近半屏纵向大面板」指控以票 31 验收为准，本规格只记结构事实）。
4. 跟进面：`stats` / `action-center` / `follow-ups` / `deadlines` 四个聚合端点供本页与 Dashboard 共用。

## 现状与规则冲突
- **CONF-TR-1（→ AUDIT ARC-07 + REQ-02 残留，票 04/05 已部分治理）**：轮次双主结构仍在——Jobs InterviewRound（9 字段投影）与 Interviews InterviewRound（17+ 字段主数据）靠事件同步；结果回写已结构化（票 04/05/58），但**排期/进行中轮次不回写 Jobs 状态**（与 `dashboard-spec.md` CONF-DASH-1 同根）：Tracker 统计 interviewed 只数投递当前状态，轮次已排期未出结果时与 Playbook/Dashboard upcoming 口径可不一致。
- **CONF-TR-2（→ AUDIT REQ-15，票 32 未实施）**：抽屉 Overview 字段网格仍无独立 **Work mode** 字段（实查字段仅 Location/Salary/Job type/Work authorization/Applied date/URL，`tracker.component.ts` detail 模板 fields 段）；WorkMode 后端有存、Smart paste 能解、编辑负载有回填，唯展示缺位——与用户 2026-10-19「详情字段齐全、对齐 LinkedIn 岗位页」口径冲突。
- **CONF-TR-3（→ AUDIT REQ-14，票 45 未实施）**：原生对话框残留实查四 + 一处：tailored 简历删除 `confirm()`（:2728）、求职信覆盖（:2824）/删除（:2870）、沟通删除（:3511）仍用原生 confirm；每周目标仍用 `window.prompt`（:1053）。
- **CONF-TR-4（→ AUDIT REQ-11，票 31 未在本基线核销）**：bulk-bar 结构仍为 mat-card 容器 + 状态下拉 + 归档/删除按钮；其纵向占用是否仍达「近半屏」需 UI 实测判定，本规格不代票 31 下结论，仅索引。
