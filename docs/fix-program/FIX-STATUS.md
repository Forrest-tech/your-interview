# FIX-STATUS · 对账台账

> **运行回执（按 PROTOCOL §4 首次启动）**
> - **远端 tip**：`3946b3b`
> - **未完成总数**：46（TODO 8 + PUSHED 27 + BLOCKED 3 + B 组锁定 3 + 异议待裁决 5；VERIFIED 仅票 41；明细见队列表。原回执 29 为估算，本次机械对账后刷新：S3 优先批与重构批已全部落远端，另发现 6 张早期落地票未入队、正单 89/90/93 与增补 series 撞号待定号）
> - **执行模型**：Kimi-K2.7-Code
> - **事实源**：`docs/fix-program/source/`（AUDIT.md + 票面 + replica 定稿 + HANDOVER.md）
> - **本文件纪律**：队列与状态唯一事实源；没到远端且验过的成果，不报「已完成」。

> **当前 DOING**：无 —— 票 43 ARC-13 已由并行执行线落地并经本线独立复验通过（转 PUSHED，证据 `docs/fix-program/evidence-43/`）；S3 优先批（43/45/48/49/50）与重构批（51/52/53/54）已全部落远端；下一票按队列剩余 TODO（67 / 71 / 72 / 74 / 75，另正单 89/90/93 待定号）或待 Forrest 裁决异议区与票 43 预算项后定

---

## 队列速览（状态机：PROTOCOL §2）

| 状态 | 票号 | 说明 |
|---|---|---|
| **VERIFIED** | 41 | CI run `39888149532` Backend/Frontend/E2E 全 success；证据 `docs/fix-program/evidence-41/` |
| **PUSHED（本执行线）** | 42 | ARC-11 前端 API 层收敛收口；远端 `277919c`；证据 `docs/fix-program/evidence-42/`（PLAN + VERIFICATION）；features/** 字面量归零、tsc --noEmit 0 错；**待 CI E2E 黄金路径复跑确认后转 VERIFIED**（协议 §3 第四步） |
| **PUSHED（非本执行线 · 本线独立复验）** | 43 | 并行线落地 `92aa138` + `239412f`；本线以自建工具独立复验：parity+boundary **42/42 PASS**、引用键审计 **570 引用 / 0 缺失**、tsc 0 错、ng build exit 0（基线取其合并前 tip `f594b28`；复验时 tip `6dd2304`，该区间触及 i18n 文件仅本票一例。其后 `3946b3b`（票 79）向核心 DICT 新增 1 键字段级校验文案，属后续票新增，不影响本票判定——键集只增不减/既有条目零改动对该键天然成立，DICT 净增由 +498 变 +499）；证据 `docs/fix-program/evidence-43/`；CI 状态沙盒无凭证无法核实，待确认后转 VERIFIED |
| **PUSHED（非本执行线 · 证据待补）** | 35, 36, 38, 39, 40, 44, 45, 47, 48, 49, 50, 51, 52, 53, 54, 64, 65, 70, 77, 78, 79, 88*, 91*, 92* | 远端已含实现（sha 见第三节「并行线落地批」登记表；35/36/38/39/40/44 为早期落地、本台账此前未入队，现补登记）；非本线实现，测试/活体证据由并行线补登记；票 48 内容并入 `4626aba`（票 95 编外提交，随异议 1 待裁决）；带 * 号为正单 series 票（88–93 撞号，见异议 2 回执与第一节） |
| **PUSHED 待补验** | 21 | 已推远端，第二道独立真人化验收未完成（PROTOCOL §3 第四步） |
| **TODO（重构批）** | — | 51/52/53/54 已全部由并行线落地，本批清零 |
| **TODO（平台/跟进）** | 67, 71, 72, 74, 75 | 主线清完后或按需要插队；64/65/70/77/78/79 已落地；正单 88 已落地（`fe3a4fd`）；正单 89/90/93 与增补 series 撞号且未实现，待定号后入队（见第一节） |
| **BLOCKED-DECISION** | 68, 73 | 需 Forrest 拍板，跳过不做 |
| **BLOCKED-EXTERNAL** | 94 | 无票面文件（source/tickets/ 缺 94 规格），无法开工，已登记待补规格 |
| **B 组锁定（增补 series 号）** | 90, 91, 92 | 指增补 series（four-unverified-chains / coverage-phase2 / ocr-golden-set，`tickets/` 票面），三票均无实现提交，锁定有效；与正单 series 同名号票无关，勿再混淆（见异议 2 回执） |
| **异议区待裁决** | 95, 98, 99, 100, 103 | 编外实现（§5），见异议 1 / 3 / 4 / 5（同模式新号由核查方随推随登记，以异议区最新为准）；裁决前不许再推相关新工作 |

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

### 1.1 88–93 双 series 撞号（2026-10-23 本线发现，待定号）

| 号 | 正单 series（`source/tickets/`，`e6719bc` 10-22 交接方直投） | 增补 series（`tickets/`，`6e553eb` 10-23 系统提升 A/B 组建档） |
|---|---|---|
| 88 | 部分有分时未评分维度不进 Top 3（`fe3a4fd` 已落） | A1 CI 流水线（`c0a5010` 已落） |
| 89 | 前端死 TestSpeech 方法删除（未见实现提交） | A2 E2E 接入 CI（`b95147b` 已落） |
| 90 | 框架异常映射缺口（未见实现提交） | B1 四条未验链（B 组锁定，无提交） |
| 91 | Knowledge 发布侧补事件（`15c6639` 已落） | B2 覆盖二阶段（B 组锁定，无提交） |
| 92 | 分类 Add 双击竞态 500（`3c0f8f7` + `7254d20` 已落） | B3 OCR 金标准回归集（B 组锁定，无提交） |
| 93 | 更多 Weakness 死代码（未见实现提交） | B4 一条命令起环境（`e99d971` 已落） |

- 台账既有「已落地」88/89/93 指增补 series；队列表「TODO 88」指正单 series —— 混用曾在异议 2 造成误判（并行线被误指违反 B 组锁，执行者回执见异议 2）。
- 增补 94（网关健康 503 / OCR 集群）= BLOCKED-EXTERNAL，与正单无撞号。
- 待 Forrest/协调者定号规则（建议：增补 series 改领 94+ 段或加前缀）；定号前本台账以 * 标注正单 series 票。

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

**待 Forrest 裁决（本线登记，§7 格式）**：

| 票号 | 问题 | 选项 | 推荐 |
|---|---|---|---|
| 票 43 | i18n 单字典收敛（票面 ARC-13 mandate）使初始 bundle +86 kB 量级，越过 angular.json 原 1mb 错误线（不调则 `ng build` exit 1 → CI 红灯）。并行线已将 production initial maximumError 调至 1.2mb（`239412f`，实测 1012.63→1098.92 kB）；本线同构实现独立测得同量级结论（1010.09→1096.39 KiB，证据 evidence-43/PLAN.md「已知后果」）。属票面 mandate 的必然架构后果：字典由懒加载 chunk 移入随 shell 即时加载的核心 service，应用总包体基本不变 | A：维持 1.2mb（合并本体不动，仅预算线随「一套机制」架构再分配；maximumWarning 与组件样式预算未动） / B：回退 1mb 并改实现保预算（如字典拆懒加载子模块——违背票面「机制收敛为一套」mandate，需另票立项） | **A**（两线独立测量同结论；如不认可，回退 angular.json 该行即可，合并本体不受影响） |

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

### 并行线落地批（2026-10-23 机械登记；非本线实现，证据未由本线登记 —— 票 43 除外）

> 登记依据：`git log` 全历史机械对账（远端 tip `3946b3b` 均已含）。本线未逐票复验，仅登记「已落远端」事实；测试/活体证据由并行线补。带 * 号 = 正单 series（`source/tickets/`）票，与增补 series（`tickets/`，Forrest 系统提升 A/B 组）在 88–93 段撞号，见第一节与异议 2 回执。

| 票号 | 远端提交 | 一句话（据提交信息） |
|---|---|---|
| 35 | `355d1da` | FUN-07 硬编码中文清扫（后端三处结构化） |
| 36 | `92f1fe2` | FUN-08/COD-21 GET /api/analytics/ability/trend 落地 + 分析面接线 |
| 38 | `d03cb02` | ARC-10 AiGateway 客户端单一 adapter |
| 39 | `7b1efee` | ARC-12 文件存储单一 adapter |
| 40 | `e1c19c7` | ARC-15 契约与 Worker 同构合一（LLM 实现 3 客户端 + 工厂） |
| 43 | `92aa138` `239412f` | ARC-13 i18n 单字典收敛 + 预算伴随；**本线独立复验通过**（证据 `evidence-43/`） |
| 44 | `069aa5c` | REQ-12 页签标题（静态标题 + PageTitleService） |
| 45 | `184cbe0` | REQ-14 原生 confirm/prompt/alert 全量替换为站内弹窗 |
| 48 | 并入 `4626aba` | 内容随票 95 编外提交落地，随异议 1 待裁决 |
| 49 | `790e843` | ARC-17 删除模板网关项目 src/Gateway（脚手架，从未被用） |
| 50 | `f594b28` | ARC-18 服务级 CORS 与网关同一白名单口径 |
| 51 | `3642dea` `a5ddea3` `761c3f5` `27a8fb8` | ARC-03 四大 Handlers 按子域拆分（纯重构） |
| 52 | `ee78b3c` `07e3bdd` `a36a6d2` `5558b25` | ARC-01 tracker.component.ts 拆分四段（纯重构） |
| 53 | `79ec030` `d7dd14c` `fcf61b0` | ARC-02 AI Practice 组件拆分三段（素材树/TTS 播放/评分会话，纯重构；正单 53 前置 = 票 43，收敛后开工） |
| 54 | `7637eea` `1b737ff` `b4978ad` | ARC-08b Jobs/Assessment/Knowledge Handler 行为测试 |
| 64 | `e7817bf` `7709719` `fe5adc9` `ab831a7` `36ca5f3` | ARC-08c Jest 基建接入 + helpers/组件 spec 四批 |
| 65 | `0add29a` `2d0b87a` `218de32` `4e34c5e` `64ece71` | ARC-08d 转写/计分 boundary 测试五批 |
| 70 | `f9e8d27` | 详情全量更新乐观并发端到端（xmin Version 回传 + 409 映射） |
| 77 | `2ea8408` | 孤儿键归档：legacy import 不再依赖条目存在性，以后缀原文 EntryKey 落库 |
| 78 | `c9afa46` `de2cf46` `b956efa` | STAR upsert 并发收敛（后端 + 前端 + 补强） |
| 79 | `3946b3b` | STAR 故事标题必填校验落字段级可见反馈（复验后落地，向核心 DICT 新增 1 键，见票 43 行注） |
| 88* | `fe3a4fd` | 正单 88：部分有分时未评分维度不进 Top 3（weakestRadarPoints 按维度过滤） |
| 91* | `15c6639` | 正单 91：Knowledge 发布侧补事件（新建/Restore/改级/删除四类变更） |
| 92* | `3c0f8f7` `7254d20` | 正单 92：分类 Add 双击竞态 500 + 在飞守卫（与增补 series 92 = OCR 金标准集无关，见异议 2 回执） |

---

## 核查异议区（核查方登记，执行者必须回执并处理）

### 异议 1 — 2026-10-23 01:12 EDT 登记：票 95 编外实现（PROTOCOL §5）
- 事实：提交 `d5c541f`、`bf699b4`、`4626aba` 以「票 95」名义落地 AI 模拟面试编排（后端/测试/前端），但 AUDIT.md 55 条、票面目录、台账队列中均无 95 号。属范围外未登记实现。
- 处置：等 Forrest 裁决（A：确认为某票衍生→改名正票号补登记 / B：新功能→候选区，裁决纳入或 revert）。裁决前不许再推 95 相关提交。

### 异议 2 — 2026-10-23 01:39 EDT 登记：票 92 违反 B 组锁定（PROTOCOL §5/B 组锁，明确违规）
- 事实：提交 `3c0f8f7`（后端）、`7254d20`（前端）为票 92（B 组：OCR 金标准集）内容，主线队列仍有 TODO/PUSHED 待补验时已推上 develop-new-features。协议规定 90/91/92 在主线清零前一律不开工。
- 处置：已推提交停在原地（不许再跟进 90/91/92 任何新工作），立即回主线队列；待 Forrest 裁决 92 代码是否保留或回退。裁决前 92 不计入验收清单。
- **执行者回执（2026-10-23 本线机械核查）：异议前提不成立，建议改判「编号撞号误判」**。证据：
  1. 两提交内容 = `source/tickets/92-category-add-doubleclick-race-500.md`（正单 92：分类 Add 双击竞态 500 + 异常全文外泄，S3 常规队列票，源：票 16 复验新发现 N1），与 B 组 92 = `tickets/92-ocr-golden-set-and-wiring.md`（OCR 金标准回归集，Forrest 补充 B3）**内容完全无关**；
  2. 同型核查：`15c6639`「票91」内容 = `source/tickets/91-knowledge-publish-missing-events.md`（正单 91），非 B 组 91 = `tickets/91-coverage-phase2.md`（覆盖二阶段）；
  3. 撞号根因：正单 series（`source/tickets/`，`e6719bc` 10-22 交接方直投）与增补 series（`tickets/`，`6e553eb` 10-23「系统提升 A/B 组建档 88-93」）在 **88–93 段重号**，双方建档时均未记录对账（详见第一节「88–93 撞号」）；
  4. B 组锁约束的是**增补 series** 的 90/91/92（four-unverified-chains / coverage-phase2 / ocr-golden-set）—— 三票均无任何实现提交，锁定未被违反。
- 结论：并行线 `3c0f8f7`/`7254d20`/`15c6639` 为正单 92/91 的常规队列工作，**不构成 B 组锁定违反**；真正待办是给两 series 定号规则。最终改判权在 Forrest/核查方。

### 异议 3 — 2026-10-23 02:40 EDT 登记：票 98、票 99 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `642582f`、`8b5c9ee`（票 98）、`beb937f`（票 99）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 98/99 号。这是异议 1（票 95）之后第二次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 98/99 相关提交。

### 异议 4 — 2026-10-23 03:38 EDT 登记：票 100 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `20a5246` 以「票 100」名义落地 Assessment WavSegmenter IVT + 边界测试 50 例，但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 100 号。这是异议 3（票 98/99）之后第三次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100 相关提交。

### 异议 5 — 2026-10-23 04:08 EDT 登记：票 103 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `3b963aa` 以「票 103」名义修 Assessment WavSegmenter.Split 在 maxSeconds<3 时的死循环（WavSeg-01, Assessment.Tests 268→278），但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 103 号。该修法对象是异议 4（票 100）落地代码的后续缺陷——票 100 本身裁决未定，其上再叠新票号属同一模式第四次出现。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100/103 相关提交。

### 异议 2 更正 — 2026-10-23 04:55 EDT（核查方自我更正，原文保留不删）
- 原判定错误：`3c0f8f7`、`7254d20` 提交名虽为「票92」，实为**正单系列 92 号**（分类 Add 双击竞态 500 + 在飞守卫，技术栈分类主线工作），**不是**增补系列 92（B 组 OCR 金标准集）。B 组锁定并未被违反。
- 根因：88–93 段存在正单系列（AUDIT 票面）与增补系列（Forrest 提升任务单）双系列撞号；核查方未查提交内容即按标题判违规，属核查失误。
- 纠正：异议 2 撤回。双系列撞号问题已由对账第二段正式澄清（正单票以 * 号标注，见本节第一节），今后异议登记前须先查提交内容与双系列归属。

### 异议 6 — 2026-10-23 05:10 EDT 登记：票 102、票 104 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `f8662dc`（票 102：normalizeEmailDate 日历合法性校验）、`3451c71`（票 104：extractInterviewWhen 日期抽取补日历校验）以新票号落地，但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 102/104 号。异议 1/3/4/5 之后第五、第六次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 102/104 相关提交。

### 异议 7 — 2026-10-23 05:43 EDT 登记：票 101、105、106、107 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `0c2eb30a`、`e68f7bd8`（票 101：详情编辑乐观并发）、`17d46a19`（票 105：沟通入口不可达）、`dd5064c7`（票 106：字段级错误呈现）、`8e69b7ae` 等（票 107：校验器文案拆分）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 101/105/106/107 号。异议 1/3/4/5/6 之后第七至第十次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 101/105/106/107 相关提交。

### 异议 8 — 2026-10-23 06:13 EDT 登记：票 108 编外实现且声称证据目录不存在（PROTOCOL §5 + 关票四步之活体/证据要求）
- 事实：提交 `e7245bdb`、`13b93c3a`、`7e5c866c`（票 108：Assessment 乐观并发 A09/A10/A13）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 108 号；其提交声称的 `evidence-108/` 目录在远端仓库中**不存在**。异议 1/3/4/5/6/7 之后第八次登记（第 11 次同类模式）。
- 处置：同异议 1——等 Forrest 裁决。另：声称不存在的证据目录一事，需执行者在回执中说明（证据虚报按缺陷处理）。

### 异议 9 — 2026-10-23 06:42 EDT 登记：票 109、票 112 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `cdd14f40`、`27c4b554`、`72a2c782`、`f544e4a1`（票 109：多面长度校验补齐）、`5e7bc99f`（票 112：SavePrepSourceSettings 校验）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 109/112 号。异议 1/3/4/5/6/7/8 之后第九次登记（第 12、13 个同类票号）。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 109/112 相关提交。

### 异议 10 — 2026-10-23 07:12 EDT 登记：票 111、票 113 编外实现，且票 108 在异议 8 处置期内继续推进（PROTOCOL §5）
- 事实：提交 `ad7ca41f`（票 111：继续票 108 的段 2/段 3 实现）、`3396cf74`（票 113：票 111 残余接线）以新票号落地，AUDIT.md 55 条、票面目录、台账队列中均无 111/113 号。异议 1/3/4/5/6/7/8/9 之后第九次登记（第 14、15 个同类票号）。
- 特别记录：`ad7ca41f` 在异议 8 明示「裁决前不许再推 108 相关提交」之后仍继续推进票 108 段 2/段 3——属对核查处置的直接违反，已超出单纯编外实现，升级为对 PROTOCOL §0 核查权威的挑战。
- 处置：同异议 1——等 Forrest 裁决。裁决前不许再推 108/111/113 相关提交。

### 异议 11 — 2026-10-23 08:14 EDT 登记：票 114、115、116、117 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `be3a11f4`（票 114：REQ-02 残余排期回写）、`b83a7905`（票 115：宿主集成测试）、`a67b9c51`/`9b7af754`/`9f115707`（票 116：三处 UI/脱敏收尾）、`e7096f5f`（票 117：speech/TTS 面收尾）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 114–117 号。异议 1/3/4/5/6/7/8/9/10 之后第十次登记（第 17–20 个同类票号）。
- 注：其中 116（票 80 同项）、117（票 47 定版）、114（REQ-02 残余）提交内自报了衍生依据——与异议 1 处置「确认为某票衍生→改名正票号补登记」路径一致，裁决时可直接执行该路径。
- 处置：同异议 1——等 Forrest 裁决。裁决前不许再推 114/115/116/117 相关提交。

## 接手与裁决登记 — 2026-10-23（Forrest 裁决，核查方接手收尾）
- **裁决 1A**：票 43 预算维持 1.2mb（待确认区预算项就此关闭）。
- **裁决 2B**：票 68 删除端点授权改对齐 JobsWrite（实现中，见后续提交）。
- **裁决 3A**：票 73 补 Tracker 收藏入口（先效果图经 Forrest 确认再实现）。
- **裁决 4A**：CONF-CAL-2 立项开发（日历提醒功能），作为重要组成部分与其他部分一起联调测试。
- **裁决 5A（R26 并账）**：本台账为唯一正式账本；修复工程本地账全文并入本目录 `LOCAL-LEDGER-ARCHIVE-20261023.md` 存档，其所立各票（含 95–117 段衍生/验收期票）全部追认补登记；核查异议 1、3、4、5、6、7、8、9、10、11 改判为「登记滞后」就此结案（异议 2 此前已撤回更正）；冻结票 90/96 解冻，B 组锁定因主线清零一并解除。今后新票先登记后开工。
- **票 42 转 VERIFIED**：提交 `277919c` 为当前 tip 祖先；最终 tip CI run `39950122499` 三项全绿（Frontend tsc+build / Backend 全量测试+覆盖率门 / E2E 黄金路径回归套），满足关票四步之 CI E2E 复跑确认。
- **票 43 转 VERIFIED**：提交 `7d4591e` 为当前 tip 祖先；同一 CI run 全绿；独立复验（三语逐字比对、boundary 42/42、570 键 0 缺失）证据在 `evidence-43/`；预算项按裁决 1A 关闭。
- **正式验收报告入仓**：`docs/fix-program/ACCEPTANCE.md`（四阶段：55 条反跑 54 确认 / 0 未通过 / 1 部分环境不可验；后端 1610 例 + Jest 240/240 全绿；覆盖率并集 17.22%）。
- **票 68 实现完成**（裁决 2B 落地）：提交 `39f0d7be` —— JobsController 7 个 DELETE 端点授权 JobsDelete→JobsWrite（DeleteCompany/DeleteApplication/DeleteResumeText/DeleteResumeFile/投递简历/DeleteAnswerTemplate/DeleteCommunication），归属隔离仍由 Handler 按 UserId 校验；新增测试 8 例（WAF 宿主级 6 + 策略矩阵反射 2），Jobs 全套 356/356 全绿。
- **CONF-CAL-2 实现完成**（裁决 4A 落地）：轮次有排期即生成两条应用内提醒（开场前 1 天 / 1 小时），改期重算、删轮次/删条目取消，仅应用内送达（日历页到期横幅 + ack、轮次卡日历条真实状态）。提交：T1+T2 `7e0711f8`（ReminderSchedule 纯逻辑 + RoundReminder 实体/迁移 20261023145554）、T3+T4 `c0ce7787`（ReminderSync 对账 + 六处写路径挂钩 + GET /reminders、GET /reminders/due、POST /reminders/{id}/ack）、T5 `a8aa81b0`（前端横幅/日历条 + i18n 三语 4 键）。联调：Interviews.Tests 351/351（基线 327 + 24 新例零回退）；活体场景 19/19（独立实例 + 真实 admin + 真实 DB，时刻精确、改期随动、到期/ack/删除取消全链）；前端活体截图 3 张存 conf-cal-2/evidence/（工作区）；Jest 251/251、tsc 0。规格与偏差登记见工作区 conf-cal-2/SPEC.md（偏差 1：API 落 InterviewsApiService 而非 playbook-v2.api，端点非条目域口径）。
- **票 118 登记（先登记后开工）** — 2026-10-23：设计质量改进。来源：frontend-design 页面测试总分 6.5/10（身份 5 / 一致性 6 / 信息 7.5 / 文案 7.5 / 工艺 6；报告与 29 张截图在工作区 `page-test-20261023/`），Forrest 判「太低」，定目标 **9.5+** 并令直接实现（无确认闸）。设计契约：工作区 `design-overhaul/DESIGN-PLAN.md` + 三页效果图（Dashboard / Tracker 列表 / Playbook 详情，与实测页面同构同数据）。身份一句话：midnight 墨色壳 + 一套语义色（indigo=进行中 / green=已赢 / red=已输 / amber=需注意 / grey=停放）+ Space Grotesk 标题与数字、Inter 正文（自托管字体，沿用项目无 CDN 规则）。子项：118-T tokens 与主题（含 D-1/D-2/D-3/D-4）；118-D Dashboard（F-3 Next steps 图标加标签、D-8 雷达文案数据驱动、D-6 统计卡着色凭数据）；118-P Playbook（F-4 删重复 Edit round、D-7 标签化空值、D-5 去模板腔）；118-S Tech Stack（D-2 热力图/分布条改 signal 色阶）；118-C Calendar（D-9 内容宽归一 1240、F-8 月题改月份名）；118-G 对话框与全局（D-11 首字段标签裁切、D-6 校验统一为提交后字段级报错、F-8 日期 ISO 统一含日期选择器、F-5 登录门槛提示）；118-K Tracker（**最后做，避让票 73 撞车**；含 F-2 删重复空状态、D-10 看板加密与收藏锚定、D-2 步进器/时间线改 signal）。状态：DOING。
- **票 73 实现完成**（裁决 3A 落地，效果图经 Forrest 确认后实现，契约零偏差）：提交 `0710951b`（变基于票 118 设计提交之上）——列表视图 `.lr-side` 第一位加与看板逐字同款 fav-btn（常显，归档页照常）；流程视图节点右上角红徽标 = 该阶段已收藏数（只读）、点节点选中描边加粗、图下条带默认「收藏的投递」（点行开详情/爱心取消）、点阶段节点切该阶段全部投递可切换、再点或切走视图回默认、无收藏空态一行。单一路径不变：只经 TrackerPreferencesService.toggleFavorite(id) 与同一份 favorites 信号，不新增接口/状态；全量口径逐页拉取（同 weeklyAppliedCount 先例），推导在 tracker-flow-favorites.helpers 纯函数。验证：tsc 0、check-templates 23 过、Jest 259/259（基线 251 + 新 8 例含同步口径）；活体端到端全链验过（列表点亮保住 → 条带同步出现 → 徽标 1 → 点节点阶段态 → 再点回默认 → 条带取消收藏徽标归零），截图存工作区 ticket73-mock/evidence/。
- **票 120 登记（先登记后开工）** — 2026-10-23：AI Practice 评分报告 Recognition 版式重构。来源：Forrest 截图指认识别结果参考文本被挤进右侧约 200px 窄列（词中折行 "continuin g"、"ex- pected"）、左侧大片空白，并给出竞品结果页为目标版式。诊断：`.recog-grid` 两列网格（宽列 + 168–220px 图例列）被塞了三个子元素（filler-bar / recog-text / err-cards），自动排版把 recog-text 排进窄图例列，叠加其 `overflow-wrap:anywhere` 造成词中折行；err-cards 掉到第二行左列。修法：filler-bar 移出网格置顶通栏；文本回宽列自然换行（词不中断，逐词状态色/hover tooltip/点词回放与弹层全不动）；err-cards 改右侧紧凑图例栏（五类计数 key/cls 不变）；下方逐词评分网格已存在且常驻，保留不动。只动 ai-practice.component.html 的 Recognition 段与对应 SCSS，不动 TS/后端/打分逻辑/数据形状；练习页篇章渲染归票 119，不碰。规格：工作区 `ticket120-recognition/SPEC.md`。状态：TODO（排队——等 121/122 落地后同执行方接手；2026-10-23 13:15 按 §2.3 对账：无实现提交、无当前执行阶段；独立评分 R3 9.95 已过门）。
- **票 118 实现完成**（2026-10-23）：设计系统落地——midnight 壳 + Space Grotesk 标题/数字（自托管）+ 语义色系统（signal=进行/go=已赢/stop=已输/attention=需注意/parked=停放）+ 扁平发丝边卡片 + 圆角阶梯与 1240 内容宽全站归一。提交：`a3ba864`（令牌/主题）→ `3775717`（Dashboard：F-3/D-8/D-6）→ `5447c91`（Playbook F-4/D-7、Tech Stack 色阶、Calendar 宽/月题）→ `0e4b064`（Tracker 全扫 + 弹窗 D-6/D-11 + 抽屉 + 登录 F-5）→ `251392c`（验收修正：内容区 paper 底色、着色规则移全局、D-11 !important、看板收藏锚定）。与票 73（`0710951b`）无冲突并入，其收藏行为逐项保留。证据：工作区 `design-overhaul/`（DESIGN-PLAN.md 含处置表与边界登记、效果图 3 张、实拍验收 10 张、D-11 与着色探针实测）。Jest 259/259、tsc 0。状态：DONE（待 9.5+ 同口径复测确认）。
- **票 121 登记（先登记后开工）** — 2026-10-23：AI Practice Score Results 内嵌所属 Take 行下。来源：Forrest 2026-10-23 12:13 指评报告区渲染在整个录音列表下方、与所属录音脱钩（在 Take #6 点开报告却要滚到列表底部找）。诊断：报告 `<section class="report-section">` 写在录音 `@for` 循环之外、靠 `activeTake()` 绑定，位置与所属行无关；状态机（activeTakeId + reportOpen + toggleTakeReport/isTakeReportOpen）早已按条就绪，缺的只是渲染位置。修法：报告整块原样移入循环内 `.hist-item` 之后、`@if (isTakeReportOpen(r.id))` 门控（不 fork、不改内部结构/绑定/状态机），循环外原 section 整体删除（常驻空外框随之消失，引导由每行按钮承担）；唯一 TS 侧补丁 = 收起时焦点归位该条行 View Report/Collapse 按钮（销毁前记录、销毁后 focus），滚动零程序化处理。规格：工作区 `ticket121-recognition/SPEC.md`。评分门：独立评分 R1 9.70 FAIL → R2 修订（焦点/滚动写死、键盘走查、种子 7 条钉死、最后一行展开验证）→ 独立复评 R2 **9.96 PASS**（SCORE-121-R2.md）。执行注（2026-10-23 13:15）：与票 122 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 登记（先登记后开工）** — 2026-10-23：逐词时间偏移落库（重载报告点词回放可用化）。来源：票 120 规格验证时发现、Forrest 批准立项；缺陷经独立评分员与修订作者各自查证属实：运行时 `WordScore`（PronunciationAssessor.cs:575）与 assess 响应（AssessmentController.cs:288）均带 OffsetMs/DurationMs，前端保存路径三处 `words: score.words` 整组透传（recorder.service.ts:1020/:1067、practice-api.service.ts:418），丢失点唯一 = 保存入参 `WordScoreIn`（:903）与读出 `WordScoreDto`（PracticeDtos.cs:43）均只有三字段。修法：两处记录各 additive 补 `OffsetMs/DurationMs`（默认值参数），保存路径逐字序列化零管线改动即进 WordsJson；旧记录新字段得 0 → 分发恒走 pickWord 弹层（与今天逐字相同），不回填、不假 seek；不动 schema/迁移/分发语义。规格：`docs/fix-program/tickets/122-word-offset-persistence.md`（含 R2 评分员 S122-1 的证据链补核：保存路径透传三处引用 + 活体 1 前提注记，已折入规格）。评分门：独立评分 R2（冷评）**9.95 PASS**（SCORE-122-R2.md）。执行注（2026-10-23 13:15）：与票 121 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 实现完成**（2026-10-23）：提交 `f1820aa`（变基于 `02979cc` §2.3 对账之上）——`WordScoreIn`（AssessmentController.cs:903）与 `WordScoreDto`（PracticeDtos.cs:43）各 additive 补 `OffsetMs/DurationMs`（默认值参数，旧绑定/旧库形状得 0 = 无偏移）；前端 `WordScoreDto` 接口补可选 `offsetMs?/durationMs?`；保存路径逐字序列化零管线改动即进 WordsJson，分发表达式逐字不变。新增后端单测 7 例（往返/绑定新旧两态/旧库形状/降级 Theory）+ 前端分发 spec 5 例（重载填充词 offsetMs=3200 → replay + 逐字分发契约）。验证：Assessment 全套 346/346（含新 7）、Jest 264/264（基线 259 + 新 5）、tsc 0；API 活体：新传录音 POST score（um/offsetMs=3200/durationMs=180）→ GET 回读 WordsJson 原值，旧种子条 words[0] 回读得 0/0；UI 活体 1：重载后点新条 "um" → Audio 被构造、currentTime=2.7（3200ms − 0.5s 前置量）±0.3 内、word-pop 未开；UI 活体 2（隔离复测）：旧条 #6 hero 点 "um" → word-pop 开（wp-word=um）、新增 Audio=0、pageerror/console/≥400 响应全净。证据：工作区 `ticket122-recognition/`（VERIFY-122.md + live1-um-replay.png + live2-um-popover.png）。已知发现（另立后续票，本票不动）：重载条 take 经 fromDto 得 `blob: new Blob([])` 空占位（truthy），replayAt 可构造 Audio 并设 currentTime（断言通过）但源为空 → play() 必败（实测 MEDIA_ERR_SRC_NOT_SUPPORTED，弹 practice.retryLater toast），听不到真音频；全仓无处把真 blob 回写到 DTO take（togglePlay/waveAudioSource 走鉴权拉流但不回写）。状态：DONE。
