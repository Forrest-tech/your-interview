# FIX-STATUS · 对账台账

> **运行回执（按 PROTOCOL §4 首次启动）**
> - **远端 tip**：`13e2e144`
> - **未完成总数**：29（TODO + DOING + PUSHED + BLOCKED 估算；票 41 已 VERIFIED；精确值待逐票状态机对齐后刷新）
> - **执行模型**：Kimi-K2.7-Code
> - **事实源**：`docs/fix-program/source/`（AUDIT.md + 票面 + replica 定稿 + HANDOVER.md）
> - **本文件纪律**：队列与状态唯一事实源；没到远端且验过的成果，不报「已完成」。

> **当前 DOING**：票 42 · ARC-11 前端 API 层收敛（已实现并推送 `277919c`，待 CI E2E 复跑转 VERIFIED；下一票 43 ARC-13 i18n 收口）

---

## 队列速览（状态机：PROTOCOL §2）

| 状态 | 票号 | 说明 |
|---|---|---|
| **VERIFIED** | 41 | CI run `39888149532` Backend/Frontend/E2E 全 success；证据 `docs/fix-program/evidence-41/` |
| **PUSHED** | 42 | ARC-11 前端 API 层收敛收口；远端 `277919c`；证据 `docs/fix-program/evidence-42/`（PLAN + VERIFICATION）；features/** 字面量归零、tsc --noEmit 0 错；**待 CI E2E 黄金路径复跑确认后转 VERIFIED**（协议 §3 第四步） |
| **TODO（S3 优先）** | 43, 45, 48, 49, 50 | 42 VERIFIED 后按 S2→S3→S4 顺序清；票 47 已由并行执行线落远端，本台账仅登记为 PUSHED 待其补证据 |
| **PUSHED（非本执行线）** | 47 | 远端 `13e2e144` 已含票 47 实现；CI 同 run `39888149532` 全绿；但非我实现，证据不由我登记 |
| **TODO（重构批）** | 51, 52, 53, 54 | 纯重构，S4 功能票清完后再动 |
| **TODO（平台/跟进）** | 64, 65, 67, 70, 71, 72, 74, 75, 77, 78, 79, 88 | 主线清完后或按需要插队 |
| **PUSHED 待补验** | 21 | 已推远端，第二道独立真人化验收未完成（PROTOCOL §3 第四步） |
| **BLOCKED-DECISION** | 68, 73 | 需 Forrest 拍板，跳过不做 |
| **BLOCKED-EXTERNAL** | 94 | 无票面文件（source/tickets/ 缺 94 规格），无法开工，已登记待补规格 |
| **B 组锁定** | 90, 92 | 主线队列清零前不开工（PROTOCOL §5 + 交接方协调口径） |

---

## 一、81–84 撞号（一次说清）

| 票号 | 真相 | 处置 |
|---|---|---|
| **票 81** | Log review 接线复习活动记录 | 正式版，`4bd0aa8` |
| **82-review-activity** | 与票 81 是同一件事——分支 `t82-work` 的 tip 就是 `4bd0aa8`（票81 提交本身） | **作废，吸收进票 81** |
| **票 82（正式）** | 首登路径导入被一次性闩锁吞掉 | 正式版，`0a92e70`，已落 develop |
| **票 83** | 超长分类名假成功静默不落库 | `44523c4` |
| **票 84** | 删分类后条目卡/chip 旧名不刷新 | `0d0af6a` |

即：历史上存在两个「82」——一个是 review-activity（与 81 重复，作废），一个是首登修复（正式版 `0a92e70`）。

---

## 二、待确认区（阻塞项 · 需 Forrest 提供）

本仓库与当前沙盒中**不存在**以下文件，经查全部分支 git 历史确认**从未进过版本控制**：

- `AUDIT.md`（55 条审查单 = 票的完整清单与审查要求）
- `replica/` 定稿
- 原始 `FIX-STATUS` 主账

因此**无法判定「哪些票尚未修」**——没有提交就没有证据，但也不能反推「没提交 = 不用修」。
要完成真正的全量对账（逐票比对状态/sha/测试数字），需提供上述任一份事实源。

**已确认状态（据交接方口径，非本人推断）**：

| 票号 | 状态 | 说明 |
|---|---|---|
| 票 16 | 待第二道复验 | 须等 81–84 全部落定后做 T2/T5/D4b 复验，通过才许关 |
| 票 21 | 欠独立真人化验收 | `b1a3d7a` 已实现，验收未完成 |
| 票 68 | **待 Forrest 拍板** | Jobs 删除权限口径选择，不许自行实现 |
| 票 73 | **待 Forrest 拍板** | Tracker 收藏入口，属呈现新需求，需先出效果图 |
| 票 94 | 排进主线队列待修 | 网关 /health/live 恒 503、OCR 集群无服务，挡着票 93 起后自检口径 |
| 票 90/91/92 | **B 组暂停** | 剩余审查票清完前不开工 |

---

## 三、已落地票清单（远端 develop 有提交 = 硬事实）


### 票 1
- `a370180` COD-04 轮次扩展字段部分更新(票01) — null保持/空串清空/非空赋值,不再静默清转写/备考题/邮件/录音

### 票 2
- `952b1dd` REQ-01+COD-14/票02 RemoveAiSettings 跨 schema 迁移加存在性守卫 + 全服务启动迁移失败 fail-fast

### 票 3
- `cb7e1fd` COD-01/票03 Knowledge 用户隔离 — Program.cs 补 AddCurrentUser 注册+KnowledgeItem 加 UserI
- `2589a55` COD-01/票03 Interviews 用户隔离 — InterviewEntry 加 UserId(expand→backfill→contract 3 
- `c60e682` COD-01/票03 Jobs 用户隔离 — Company/JobApplication/ApplicationCommunication 加 UserId(

### 票 4
- `7390730` REQ-02+ARC-07/票04 轮次结果回写同源 — SharedContracts InterviewOutcomeRecorded 加结构化 Inter

### 票 5
- `760876a` ARC-14/票05 结果判读单一事实源 — Interviews 列表/详情 DTO 新增结构化 Outcome(后端 DeriveOutcome 单一派生:
  - 测试: Interviews.Tests 88/88；88/88；EntryOutcomeDerivationTests/20

### 票 6
- `8db7904` COD-02/票06 ability 接口禁止替他人写入 — RecordAbility 一律以当前登录用户 Me 为准,RecordAbilityBody 移

### 票 7
- `1cab100` COD-05/票07 并发冲突统一映射 409 — ServiceDefaults 全局异常管线新增 DbUpdateConcurrencyException→

### 票 8
- `639f867` COD-06+COD-07/票08 转写契约对齐 — getTranscript 在 playbook-v2.api 适配层映射 fullText→transc

### 票 9
- `0804344` FUN-02/票09 手动建单/编辑 JD 与备注不再丢,全量 PUT 不再误清 — 前端 create() 负载补齐 jdText/notes(备注含 [DO

### 票 10
- `73f82ab` FUN-03/票10 Prep 打开既有条目不重复新建 — CreateEntryCommandHandler 对 JobApplicationId 幂等:当前
  - 测试: Interviews.Tests 93/93；93/93；CreateEntryDedupeTests/5

### 票 11
- `13b99c1` FUN-04/票11 JD 长文本保存不再 500 — JdText 列 varchar(40000) 扩为 text(迁移 20261022031113_Ex
  - 测试: Jobs.Tests 16/16；16/16；JdTextLengthGateTests/4

### 票 12
- `516d4ab` FUN-05 新建弹窗 Interview question 必填口径对齐 — question 标必填(*+required 属性),canSubmit/va

### 票 13
- `026e949` FUN-06 Mock 模式文案三语修复 — 实查缺失不止审计的 8 键:mock.component.ts + 会话页共引用 96 键、DICT 零定义 28

### 票 14
- `7ddfc67` COD-10a Tracker 收藏与自备问题上后端 — Jobs 新增 application_favorites/application_questions
  - 测试: Jobs.Tests 37/37

### 票 15
- `a3321de` COD-10b Playbook 本地数据上后端 — Interviews 新增 star_stories/legacy_debriefs/legacy_pas

### 票 16
- `cb37c52` COD-10c Tech Stack 分类与复习记录上后端 — Knowledge 新增 tech_stack_categories/review_activi

### 票 17
- `2edb30b` ARC-04 主简历单一真源 — 删除 Assessment 侧简历副本

### 票 18
- `ca629eb` ARC-09 JWT 与 Kestrel 上限收进 ServiceDefaults — 校验参数唯一定义+Jobs 兜底统一口径,七服务 Program.cs 

### 票 19
- `70bc7e7` ARC-06 Json 大字段强类型适配层——16 字段经 JsonFieldAdapter 版本信封({"v":1,"data":载荷原文})收敛,聚合 ba
- `5d772b2` ARC-06 特征测试先行——16 个 *Json 字段读写往返/旧数据兼容/三处形状判定查询锁定现状(基线 122→142 全绿)

### 票 21
- `b1a3d7a` COD-08 待办勾选接真实端点且不吞错——playbook-detail toggleAction 由 PUT /api/interviews/{id}/ac

### 票 22
- `0440255` COD-09 补齐 AddRoundDetails 迁移 Designer 并删手工脚本

### 票 23
- `a3463f7` COD-12 ListSessions 空 Issues 不再 500 — WeakestDimension 投影空集安全

### 票 24
- `9c97312` COD-13 AttachAsset 坏 JSON 返回 400 而非 500 — 共享映射加 JsonException→400

### 票 25
- `0c2b18a` COD-15 写操作改挂写权限 — SaveRecordingScore/Synthesize 由 MockRead 改 MockAnswer(仅两行授权属性)
- `ebe4a33` COD-15 红测先行 — AssessmentEndpointPermissionsTests(22例:两目标端点断言 perm:mock.answer 现红

### 票 26
- `4fd9247` REQ-05 GetEffectiveResume 文本简历回落 + Tracker Documents text 形态

### 票 27
- `1b6422e` REQ-08 Dashboard/Analytics 无数据不列弱项/Best——weakest 加 scored 守卫，零数据走既有空态

### 票 28
- `1502777` ARC-08a SmartParseRules 特征测试先行 + coverlet 接入与后端基线

### 票 29
- `8d29a69` REQ-09 SmartParse 形态缺口修复 — 内联Benefits可解/摘要跳元数据行/公司名FUN-09两形态+About误判

### 票 30
- `f99cfdd` REQ-10 轮次展示编号按日期序 + 新建轮次默认值旁注

### 票 31
- `6e37ad2` REQ-11 bulk-bar 改一行紧凑工具条 — 显式 flex-direction:row 覆盖 mat-card column(改前实测328px纵向堆

### 票 32
- `68bc118` REQ-15 抽屉 Overview 字段网格补 Work mode 展示 — 追加第7格(detail.workMode||'—',复用既有 i18n 键 t

### 票 34
- `faedc0b` REQ-18 投影对账与分叉修复 — KnowledgeLevelChanged 消费者改单条目台账增量迁移(废 CONF-AN-3 原值回写)+等级语义映射(
  - 测试: Analytics.Tests 51/51

### 票 37
- `7d8676c` FUN-10 文本简历可见可编辑 — 编辑态补 textarea(复用既有 resumeLabel/resumePh 三语键与 .resume-field 样式

### 票 46
- `ca24c6f` COD-17 死代码清理 — 删无端点 AddWeaknessCommand(record+Validator+Handler,全仓仅定义 6 行、零发送方/零
  - 测试: Interviews.Tests 184/184

### 票 55
- `e3864f6` 陈旧测试裁决修复 — AttachAssetValidator_MissingRoundId_Invalid 改为 EntryLevelWithoutRound

### 票 56
- `b192084` Analytics 回写可信通道(协调者裁决解法A)——SharedContracts 新增 analytics.writeback 权限并注册 Permiss
  - 测试: AbilityOwnershipTests 10/10；Worker.Tests 54/54；10/10

### 票 57
- `c3af5b7` OCR 邀请卡会议时间抽取 — extractInterviewWhen 扩展两类格式:ISO 日期与时间之间可夹星期(2026-10-13 周二 10:00～

### 票 58
- `6c8b8ed` LegacyOutcomeTextAdapter 补英文受控词整串精确匹配 — 关键词回落之前先做与 Interviews DeriveOutcome 规则2 
  - 测试: Jobs.Tests 12/12；消费者测试/4

### 票 59
- `09997d8` 共享 OCR 服务 + Playbook 入口切换 — 新建无状态 Python 服务 src/ocr-service(FastAPI+PaddleOCR PP

### 票 60
- `7f67913` 本地 OCR 文本头部适配 — 新增 adaptLocalOcrEmailText:仅当 source='local' 且 parsePastedEmail 无

### 票 61
- `6a37e73` §7 补测落地 — 新增 Services.Knowledge.Tests 项目(UpdateKnowledgeValidatorTests 22 例:Ques
  - 测试: 22/22
- `7011af9` 详情编辑保存不再清空 Question — saveDetailEdit 全量 PUT 的 question 改为回填 item.question(请求负载与本

### 票 62
- `7cc5132` SmartAdd 四长文本列扩 text + 200,000 统一闸门 (票62)

### 票 63
- `3144454` Mock「完整模拟」提交值 FullMock 与后端枚举 FullLoop 不一致 — 前端 modeOptions 提交值改 FullLoop 并订正 71-
  - 测试: 31/31

### 票 66
- `02510fb` MockSession 补软删除全局过滤器 (票66)

### 票 69
- `ab629ba` AddQuestions 对已持久化会话恒 409 修复 (票69)

### 票 76
- `a9e55b4` 超限请求体返回 413 而非 500 — 共享映射透传 BadHttpRequestException.StatusCode(Kestrel 超限框架带 413
  - 测试: 664/664

### 票 80
- `c6ee0a6` 详情页头部徽章与分组共用 outcome 单一事实源

### 票 81
- `4bd0aa8` Log review 接线复习活动记录——review() 成功回调补 recordReviewActivity(),热力图据实着色

### 票 82
- `0a92e70` 首登路径导入被一次性闩锁吞掉——三面(Tech Stack/票14 Tracker/票15 Playbook)同型改失败不闩锁+按账号闩锁+未登录不触发

### 票 83
- `44523c4` 超长分类名假成功静默不落库——前端提交前纯校验(validateCategoryName,UTF-16计与后端100同口径)+字段级报错含上限、service错

### 票 84
- `0d0af6a` 删分类后条目卡/chip旧名不刷新——删除成功经共享服务通知(categoryDeleted$)即时本地同步:条目卡/详情头/复习队列副本topic回落Unca

### 票 85
- `66487c3` InvitePasteParser 英文日期去宿主 culture 依赖 — GetCultureInfo("en-US") 在 invariant 宿主(生产
  - 测试: Ticket85/20

### 票 86
- `cf376aa` Assessment 另两 POST 端点授权对齐 COD-15 — AssessPronunciation 改 MockAnswer、TestSpeech 改
- `20cca9f` 红测先行 — AssessmentEndpointPermissionsTests 扩展(AssessPronunciation 期望 perm:mock.an

### 票 87
- `03afc0c` Assessment 模型快照对齐 — 空迁移剔 UserResume 快照残留 + LinkedSource 补 HasMaxLength(20)

### 票 88
- `c0a5010` A1 CI 流水线从零建立——后端 build+全量测试+覆盖率门 / 前端 tsc+build

### 票 89
- `b95147b` A2 E2E 接入 CI——每次 push/PR 自动跑黄金路径回归
- `d7398a1` feat(e2e): 票89 A2 补齐黄金路径 E2E-2/3/4/5,套件 5 绿 + 1 显式跳过
  - 测试: 5
- `e725bcb` feat(e2e): 票89 A2 E2E 冒烟回归套落地,首个回归用例取票80 场景

### 票 93
- `e99d971` B4 一条命令起环境 + 起后自检 + README 前置条件
---

## 核查异议区（核查方登记，执行者必须回执并处理）

### 异议 1 — 2026-10-23 01:12 EDT 登记：票 95 编外实现（PROTOCOL §5）
- 事实：提交 `d5c541f`、`bf699b4`、`4626aba` 以「票 95」名义落地 AI 模拟面试编排（后端/测试/前端），但 AUDIT.md 55 条、票面目录、台账队列中均无 95 号。属范围外未登记实现。
- 处置：等 Forrest 裁决（A：确认为某票衍生→改名正票号补登记 / B：新功能→候选区，裁决纳入或 revert）。裁决前不许再推 95 相关提交。

### 异议 2 — 2026-10-23 01:39 EDT 登记：票 92 违反 B 组锁定（PROTOCOL §5/B 组锁，明确违规）
- 事实：提交 `3c0f8f7`（后端）、`7254d20`（前端）为票 92（B 组：OCR 金标准集）内容，主线队列仍有 TODO/PUSHED 待补验时已推上 develop-new-features。协议规定 90/91/92 在主线清零前一律不开工。
- 处置：已推提交停在原地（不许再跟进 90/91/92 任何新工作），立即回主线队列；待 Forrest 裁决 92 代码是否保留或回退。裁决前 92 不计入验收清单。

### 异议 3 — 2026-10-23 02:40 EDT 登记：票 98、票 99 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `642582f`、`8b5c9ee`（票 98）、`beb937f`（票 99）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 98/99 号。这是异议 1（票 95）之后第二次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 98/99 相关提交。

### 异议 4 — 2026-10-23 03:38 EDT 登记：票 100 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `20a5246` 以「票 100」名义落地 Assessment WavSegmenter IVT + 边界测试 50 例，但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 100 号。这是异议 3（票 98/99）之后第三次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100 相关提交。

### 异议 5 — 2026-10-23 04:08 EDT 登记：票 103 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `3b963aa` 以「票 103」名义修 Assessment WavSegmenter.Split 在 maxSeconds<3 时的死循环（WavSeg-01, Assessment.Tests 268→278），但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 103 号。该修法对象是异议 4（票 100）落地代码的后续缺陷——票 100 本身裁决未定，其上再叠新票号属同一模式第四次出现。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100/103 相关提交。
