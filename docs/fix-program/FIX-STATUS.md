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
- **票 120 登记（先登记后开工）** — 2026-10-23：AI Practice 评分报告 Recognition 版式重构。来源：Forrest 截图指认识别结果参考文本被挤进右侧约 200px 窄列（词中折行 "continuin g"、"ex- pected"）、左侧大片空白，并给出竞品结果页为目标版式。诊断：`.recog-grid` 两列网格（宽列 + 168–220px 图例列）被塞了三个子元素（filler-bar / recog-text / err-cards），自动排版把 recog-text 排进窄图例列，叠加其 `overflow-wrap:anywhere` 造成词中折行；err-cards 掉到第二行左列。修法：filler-bar 移出网格置顶通栏；文本回宽列自然换行（词不中断，逐词状态色/hover tooltip/点词回放与弹层全不动）；err-cards 改右侧紧凑图例栏（五类计数 key/cls 不变）；下方逐词评分网格已存在且常驻，保留不动。只动 ai-practice.component.html 的 Recognition 段与对应 SCSS，不动 TS/后端/打分逻辑/数据形状；练习页篇章渲染归票 119，不碰。规格：工作区 `ticket120-recognition/SPEC.md`。状态：DOING（2026-10-23 实施启动：票 121/122 已落地（tip bffb865），同执行方接手；独立评分 R3 9.95 PASS，S120-R3-1 旁注更正随本登记折入工作区 SPEC）。
- **票 118 实现完成**（2026-10-23）：设计系统落地——midnight 壳 + Space Grotesk 标题/数字（自托管）+ 语义色系统（signal=进行/go=已赢/stop=已输/attention=需注意/parked=停放）+ 扁平发丝边卡片 + 圆角阶梯与 1240 内容宽全站归一。提交：`a3ba864`（令牌/主题）→ `3775717`（Dashboard：F-3/D-8/D-6）→ `5447c91`（Playbook F-4/D-7、Tech Stack 色阶、Calendar 宽/月题）→ `0e4b064`（Tracker 全扫 + 弹窗 D-6/D-11 + 抽屉 + 登录 F-5）→ `251392c`（验收修正：内容区 paper 底色、着色规则移全局、D-11 !important、看板收藏锚定）。与票 73（`0710951b`）无冲突并入，其收藏行为逐项保留。证据：工作区 `design-overhaul/`（DESIGN-PLAN.md 含处置表与边界登记、效果图 3 张、实拍验收 10 张、D-11 与着色探针实测）。Jest 259/259、tsc 0。状态：DONE（待 9.5+ 同口径复测确认）。
- **票 121 登记（先登记后开工）** — 2026-10-23：AI Practice Score Results 内嵌所属 Take 行下。来源：Forrest 2026-10-23 12:13 指评报告区渲染在整个录音列表下方、与所属录音脱钩（在 Take #6 点开报告却要滚到列表底部找）。诊断：报告 `<section class="report-section">` 写在录音 `@for` 循环之外、靠 `activeTake()` 绑定，位置与所属行无关；状态机（activeTakeId + reportOpen + toggleTakeReport/isTakeReportOpen）早已按条就绪，缺的只是渲染位置。修法：报告整块原样移入循环内 `.hist-item` 之后、`@if (isTakeReportOpen(r.id))` 门控（不 fork、不改内部结构/绑定/状态机），循环外原 section 整体删除（常驻空外框随之消失，引导由每行按钮承担）；唯一 TS 侧补丁 = 收起时焦点归位该条行 View Report/Collapse 按钮（销毁前记录、销毁后 focus），滚动零程序化处理。规格：工作区 `ticket121-recognition/SPEC.md`。评分门：独立评分 R1 9.70 FAIL → R2 修订（焦点/滚动写死、键盘走查、种子 7 条钉死、最后一行展开验证）→ 独立复评 R2 **9.96 PASS**（SCORE-121-R2.md）。执行注（2026-10-23 13:15）：与票 122 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 登记（先登记后开工）** — 2026-10-23：逐词时间偏移落库（重载报告点词回放可用化）。来源：票 120 规格验证时发现、Forrest 批准立项；缺陷经独立评分员与修订作者各自查证属实：运行时 `WordScore`（PronunciationAssessor.cs:575）与 assess 响应（AssessmentController.cs:288）均带 OffsetMs/DurationMs，前端保存路径三处 `words: score.words` 整组透传（recorder.service.ts:1020/:1067、practice-api.service.ts:418），丢失点唯一 = 保存入参 `WordScoreIn`（:903）与读出 `WordScoreDto`（PracticeDtos.cs:43）均只有三字段。修法：两处记录各 additive 补 `OffsetMs/DurationMs`（默认值参数），保存路径逐字序列化零管线改动即进 WordsJson；旧记录新字段得 0 → 分发恒走 pickWord 弹层（与今天逐字相同），不回填、不假 seek；不动 schema/迁移/分发语义。规格：`docs/fix-program/tickets/122-word-offset-persistence.md`（含 R2 评分员 S122-1 的证据链补核：保存路径透传三处引用 + 活体 1 前提注记，已折入规格）。评分门：独立评分 R2（冷评）**9.95 PASS**（SCORE-122-R2.md）。执行注（2026-10-23 13:15）：与票 121 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 实现完成**（2026-10-23）：提交 `f1820aa`（变基于 `02979cc` §2.3 对账之上）——`WordScoreIn`（AssessmentController.cs:903）与 `WordScoreDto`（PracticeDtos.cs:43）各 additive 补 `OffsetMs/DurationMs`（默认值参数，旧绑定/旧库形状得 0 = 无偏移）；前端 `WordScoreDto` 接口补可选 `offsetMs?/durationMs?`；保存路径逐字序列化零管线改动即进 WordsJson，分发表达式逐字不变。新增后端单测 7 例（往返/绑定新旧两态/旧库形状/降级 Theory）+ 前端分发 spec 5 例（重载填充词 offsetMs=3200 → replay + 逐字分发契约）。验证：Assessment 全套 346/346（含新 7）、Jest 264/264（基线 259 + 新 5）、tsc 0；API 活体：新传录音 POST score（um/offsetMs=3200/durationMs=180）→ GET 回读 WordsJson 原值，旧种子条 words[0] 回读得 0/0；UI 活体 1：重载后点新条 "um" → Audio 被构造、currentTime=2.7（3200ms − 0.5s 前置量）±0.3 内、word-pop 未开；UI 活体 2（隔离复测）：旧条 #6 hero 点 "um" → word-pop 开（wp-word=um）、新增 Audio=0、pageerror/console/≥400 响应全净。证据：工作区 `ticket122-recognition/`（VERIFY-122.md + live1-um-replay.png + live2-um-popover.png）。已知发现（另立后续票，本票不动）：重载条 take 经 fromDto 得 `blob: new Blob([])` 空占位（truthy），replayAt 可构造 Audio 并设 currentTime（断言通过）但源为空 → play() 必败（实测 MEDIA_ERR_SRC_NOT_SUPPORTED，弹 practice.retryLater toast），听不到真音频；全仓无处把真 blob 回写到 DTO take（togglePlay/waveAudioSource 走鉴权拉流但不回写）。状态：DONE。
- **票 121 实现完成**（2026-10-23）：提交 `af84a74` —— `<section class="report-section">` 整块（237 行）从 `@for` 循环后原样搬入循环内 `.hist-item` 之后，外包 `@if (isTakeReportOpen(r.id))`；循环外原 section 整体删除（常驻空外框 + `reportEmpty` 空态引导随之消失，规格 (c) 已记后果）。逐字对照：去缩进/注释后新旧 218 行逐字一致，内部结构与绑定一字未改（含票 120 重构后的 Recognition 段；`.recog-*` 内部未动，窄列挤压属票 120 领地）。TS 唯一补丁（+30/−2 行）：`toggleReport()`/`toggleTakeReport(id)` 收起时销毁前记录该条行 `.hi-view` 按钮（行内钮即当前焦点；头部 toggle 从所在 section 向前找相邻 `.hist-item`）、`setTimeout(0)` 销毁后 `.focus()` 回去；切换不移焦点；滚动零程序化（grep `scrollIntoView` 零命中）；状态机零改动。SCSS 唯一 margin 微调（规格允许）：`.hist-card .report-section` `margin-top: 20px→6px`，紧贴所属行。验证：六步 20/20 PASS（#6 展开相邻 gap=6px 且单节；两条收起路径焦点均回行按钮；#3 切换随行、scrollY Δ=0；点他行切换不误关；删展开条报告随条消失且零报错；Tab 走查报告内控件先于 #5 行控件；末行 #1 报告完整在卡内、底缘距=padding 节奏、无横向滚动）；Jest 264/264、tsc 0、`check-templates` 23/23。证据：工作区 `ticket121-recognition/`（VERIFY-121.md + 4 截图 + Tab 顺序清单）。状态：DONE。
- **票 120 实现完成**（2026-10-23）：提交 `281f0a8` + 补正 `7da70df` ——filler-bar 移出 `.recog-grid` 置其上方通栏（回放提示显隐 = `count>0 && hasSeekableFiller(words, isFiller)` 谓词注入，FILLERS 仍归 session 单一事实源）；`.recog-text` 回宽列并改正向三声明（`word-break: normal; overflow-wrap: break-word; hyphens: none`、色改 `var(--ink)`）；err-cards 改右侧单列图例（行 = chip+ 标签、chip 同正文类值、n=0 转 parked 弱化、遗漏数字 parked 例外、≤1100px 改 grid wrap、<600px 单列）；逐词 span 加 roving tabindex（整段单停靠、←/→/Home/End 经 `clampFocusIndex` 钳制不回绕、Enter/Space 分发同点击 `: wordClickAction(isFiller, offsetMs)`、focus-visible 品牌色 outline）；无词级数据时渲染 `.res-note` + `practice.noWordData`（三语）；删 `.err-legend`/`.lg` 死样式；票 122 分发契约 spec 同步改钉。验证：Jest 276/276（基线 264 + 新 helpers 12 例，含 ③(a) 四桩谓词用例）、tsc 0、check-templates 23/23；活体 36/38（verify120.py：几何四档文本列宽 706/524/826/216px 与图例位置全过、任意点断词四档为 0、tooltip/点词 pop/③(b) pick fallback/③(c) 负分支 DOM 缺席、全 0 图例、键盘全项含切换 Take 关 pop + 停靠钳制 [63]/64、390 长词压力、E 无词数据占位行、pageerror 0；截图 9 张）。**偏差登记 3 项（详见 VERIFY-120.md）**：①【规格内冲突，待 R4 裁定】chip 数字对比度——规格钉死色值（发音 #9a6b00/#fff4d6、遗漏 parked 例外、零态 parked/parked-fill）与自身 ≥4.5:1 断言不可兼得，WCAG 实测发音 4.28 / 遗漏 4.49 / 零态 4.37（插入 5.10、中断 4.81、停顿 5.33、标签 6.13 过线），实现按钉死色值交付未擅自改色，断言如实记 FAIL；②规则 ②(ii) 的 focusTake 在现行接线零调用点，组件包装层于真正切换时补调 `focusTake(now)` 关 pop（幂等，session/状态机未动）；③正向换行声明之外另有根因——Angular 模板空白剥离使词 span 间无空格文本节点、无词间换行机会点，`7da70df` 在 span 后补真实空格节点 `{{ ' ' }}` 后断词归零（段高不变）；量测口径注记："14-year-old" 于自身连字符后断裂（UAX#14 固有断点）不计任意点断词；③(c) 正分支按规格只在 Jest 谓词级覆盖、活体只验负分支，不冒充。证据：工作区 `ticket120-recognition/`（VERIFY-120.md + verify120-results.json + after-*.png 9 张）。状态：DONE（偏差 ① 待规格 R4 裁定）。
- **票 119 登记（先登记后开工）** — 2026-10-23：AI Practice 练习页篇章「点词查词典」。来源：Forrest 2026-10-23 12:10 提需求（篇章单词悬停高亮、点击弹词典卡：音标/词性＋英文释义/中文释义/词形/发音播放，仿 ynwac），三轮裁决钉死判据（12:35 发音须真人/自然、机械 TTS 不合格；12:38 发音为学习级判据、释义要专业来源；12:44 牛津包为顶层数据源、呈现精简、牛津提取物永不入仓）。修法：四层架构——Tier 0 牛津本地包（仓库外数据目录：释义/IPA/词形/例句/录音）→ Tier 1 开放层（WordNet 义项结构 + ECDICT 子集 v2 中文/词形 + Wikimedia 真人录音核心包/懒缓存）→ Tier 2 神经 TTS（可选配置）→ Tier 3 设备 TTS；Assessment 新增 Dictionary 模块与三端点（查词/发音/音频流），归一化四步 + 合并优先 P1–P6 + 负缓存 10 分钟；前端只动练习页篇章区（词跨度 + 词典卡 + 键盘全路径），Recognition 区（票 120）不碰。验收例词：Teenage girls 篇 `disappeared`、三种数据状态。规格：工作区 `ticket119-dict/SPEC.md` + `RESEARCH.md`。评分门：独立评分 R1 9.0 FAIL（15 项扣分）→ R2 修订（发音/释义双层实测重建 + 牛津真实包探针）→ 独立复评 R2 **9.96 PASS**（SCORE-R2.md）。五处 ≤0.05 小修随本登记折入：EVD-R2-1（WordNet 许可留档以 WNdb 分发包 LICENSE 原文覆盖误存的页面框架文本，SPEC §5.3 表述对齐）、SPC-R2-1（SPEC §3.1 按构建脚本实况更正选词规则描述：信号种子 + exchange 不动点闭包）、SPC-R2-2（SPEC §3.2/§9 核心包投影口径注：4,147 文件/63.5 MB 为 rank≤6000 口径、不含考试标签增量）、TST-R2-1（SPEC §7 频带抽样容差 ±5pt→±10pt 并注明同种子＋同数据集前提）、RSK-R2-1（SPEC §7 补中文行回归样本断言；RESEARCH §11 指针改指 SPEC §7、§9.1「定义兜底」改「中文译义兜底」）。已知缺件：牛津音频归档 `V14_3.4.mdd` 未到手（用户待补交），Tier 0 音频回落 Tier 1、不阻塞；验收状态 A 待归档到手后验。状态：DOING（2026-10-23 实施启动：票 120 已落地（tip e8736a3），同队列接手）。
- **票 119 实现完成**（2026-10-23）：提交 `249d357` ——Assessment 新增 Dictionary 模块：三端点 `GET dictionary/{word}` / `{word}/pronounce?accent=` / `audio/{key}`（类级 Authorize + MockRead 策略；负卡 404 体 `{query, found:false}`；音频 `Cache-Control: private, max-age=86400`）。分层供给：Tier 0 牛津包（仓外 PackDir 装包：61,589 词条 + redirects；0 义项交叉引用词条按释义缺席回落开放层；音频归档 absent 时按 §8 状态 B 回落 Tier 1 且分源标注 `attribution.audioSource`）→ Tier 1 开放层（ECDICT 子集 91,599 行确定性构建、WordNet 子集 43,630 词元/94,546 synset、音频索引 40,608 词/54,337 文件逐文件许可，均嵌入资源分发 + manifest/NOTICE 入仓；归一四步 ECDICT 0: 链接 ∪ Morphy ∪ .exc ∪ 连字符头；合并 P1–P6；正缓存 24h/负 10min）→ 音频链 核心包（manifest 校验、**4,455 文件 / 79,988,919 B ≤ 80 MB**，MP3 本体仓外 dict-data）→ 懒拉取（cache-index 记源与许可、200 MB LRU）→ 神经 TTS（默认关）→ device。前端仅篇章区：token 化渲染（标点/空白为 sep token、词间空格由 token 承载）、悬停/已查词样式、词典卡（音标 + UK/US 播放 + 口音切换、词性组折叠、词形行播放、来源行含 audioSource、骨架/负卡/错误卡重试）、发音统一走 pronounce 端点取流播放、失败 speechSynthesis 兜底；键盘全路径（roving、Enter 开卡、Esc 关闭并焦点返回、Tab 首尾回绕、落定移入首个播放钮）；i18n `dict.*` 15 键三语。Recognition 区（票 120）零改动、打分逻辑与既有数据形状零改动（additive only）。构建脚本 7 件入仓 `scripts/dictionary/`；牛津提取物永不入仓（git ls-tree 核：仅提取脚本与解析代码，无数据文件、无 .mdx/.mdd）。验证：后端全套件 **383/383 PASS**（xunit 控制台 runner；词典专项合成夹具 25/25：Normalization 8 + P1–P6 合并 9 + 缓存/音频 9；数据断言：计数/NOTICE/中文行快照 39 词/频带抽样/状态 C 形状/gitignore 7/启动预算套件内 PASS）；Jest **293/293**（12 suites，新增 helpers 17）、tsc **0**、check-templates **23/23**、ng build 成功。活体（沙盒栈、我的 Assessment 构建于 5266、前端 4400，4200 无监听）：材料经 API 播种 Teenage girls 篇；状态 B 牛津卡全项（redirect 归元、5 义项 foldedCount 3、词形合并标签、分源标注、US 音频 kind=commons 且核心包 key c001408 字节与 manifest sha256 一致）、懒拉取真 MP3 落缓存记许可、状态 C 开放层卡（ecdict-link + WordNet 义项 + 中文行）、year-old 连字符回退、the 功能词卡、zorblax 负卡 + pronounce device；**TST-1 语料覆盖 B/C 两态均 39/39 = 100%**（音频 94.87%）。**偏差登记 10 项（详见工作区 `ticket119-dict/VERIFY-119.md`）**：D-1 Kaikki 流式拉取静默截断事故（全量本地校验重跑修复）；D-2 载入预算三轮优化路径；D-3 频带参照按 TST-R2-1 换代重定基线（RESEARCH 原值留档夹具）；D-4 音频索引 15.1 MB 超 §9 预计（逐文件许可字段不可省，流式解析吸收）；D-5 核心包构成口径调和（字面 rank≤3000 全档实测仍 109 MB，最终 = rank≤6000 经典档主体 + 标签增量填充 + 80 MB 硬顶修剪）；D-6 状态 A 不可验（音频归档未到，链路由合成夹具单测覆盖）；D-7 方向键线性口径；D-8 has 中文行 P4 归组既定形态；D-9 启动预算测量口径（套件内 PASS、沙盒冷进程单跑 2.5–2.9 s 如实记录，CI 结论待 Actions 回填）；D-10 Wikimedia UA 礼仪 403（已修：描述性 UA）+ 出口 429 间歇限流（重试即通）。**悬置两项**：①前端 UI 交互路径（悬停/点击/键盘 + 截图）未由浏览器执行——执行方无实况浏览器权限，API 契约已逐项活体核实，需父线以浏览器任务对 4400 复核后才算全项验收；②状态 A 待 `V14_3.4.mdd` 到手补验。证据：工作区 `ticket119-dict/`（VERIFY-119.md、corpus-coverage.json / corpus-coverage-stateC.json、SPEC §9 已回写实测）。状态：DONE（实现与 API 层验收完成；UI 浏览器复核与状态 A 为已登记悬置项）。
- **chore 登记（守护线摘取）** — 2026-10-23：WorkBuddy 分支 `agents-skills-config` 的配置提交 `f46b30a6`（Matt Pocock 工程技能套件仓库内配置：CLAUDE.md + docs/agents/{issue-tracker,triage-labels,domain}.md，172 行纯文档）经守护线 diff 评估 + Forrest 裁决 A，登记摘取入 develop。同批 `ticket43-my-impl`（7baa40b0）经评估为票 43 落地版（92aa138+239412f）的逐文件同构实现，纯存档、绝不可合（基线 5517817e，与当前 develop 净 diff 286 文件）。摘取后守护线另行订正 CLAUDE.md 两处过时内容（「Genie 沙盒」起停命令为 WorkBuddy 环境产物、本仓不适用；后端测试基线例数过时）。评估全文：工作区 `wb-verify/DIFF-REVIEW-20261023.md`。状态：DONE。
- **票 119 补记（音频归档复核）**（2026-10-23 晚）：用户补交的 `V14_3.4.mdd`（567,821,063 B，与 Drive 元数据逐字一致，sha256-16 `4266516a97fcfe99`）已按 SPEC §3.3 接入重提取（`--audio-mdd`；`--verify` 普查门 PASS、audioCensusHits 如实记 0；manifest 更新 `audioArchive: "present"`，词条产物与首轮 sha256 逐字一致）。**定案：该归档为例句美音卷，非词头录音卷**——自测普查：57,082 键全部为 `\_<词头>__uss/__ams_<n>.mp3`（例句键族）；词条登记的例句引用 US 50,388 个全部在席（100%），词头/词形引用 149,505 个命中 **0**，故 `audioFilesExtracted: 0`、`oxford-audio/` 为空目录。活体复核（独立实例读新包）：disappeared 卡与状态 B 逐项一致（US 音频 kind=commons、未虚报 oxford、直取缺席词头键 404）——运行时逐文件核存，归档在席而文件不在不可能虚报 Tier 0。**状态 A 继续悬置**：尚缺含 `__us_/__gb_` 词头/屈折形录音的卷，到后重跑提取即生效、无需改码。全文：工作区 `ticket119-dict/VERIFY-119.md` 偏差 D-6（更新版）。状态：DONE（不变；悬置项收窄为词头录音卷 + UI 浏览器复核）。
- **票 126 登记（先登记后开工）** — 2026-10-23：AI Practice 词典卡修复批（票 119 复核缺陷批）。来源：独立 UI 复核 `ticket119-dict/UI-REVIEW.md` §9 缺陷 F-1…F-5 + Forrest 三路裁定（(a) 词典卡改 popup 浮层锚定所点词、零版面位移；(b) 去掉已查词点状虚线下划线；S-1 发音先只用美音；S-2 18:44 追加——关掉机器兜底：词典发音只用真人录音，无录音标「暂无发音」，负卡去掉机器朗读钮）。内容：F-1 负卡贯通（判定改读转换后错误 .status，不动 ApiClient 全局语义；TJ-1 必须经真实 ApiClient + 真实 404/500 flush 集成断言堵测试盲区）；F-2 前端分词规则 R-T1…R-T6（纯数字不入跨度、数字-连字符-字母复合切开、连字符回退 UI 可达，后端归一不动）；F-3 popup 浮层（锚定/翻转/移动端 sheet/零版面位移量测/Tab 序 Close 末位/键盘模型沿用 119 §6.3）；F-4 口音记忆 localStorage（键 dict.accent）+ UK 无录音 tab 禁用标注（P2）；F-5 归元标注由 forms 角色 label 生成 + 后端标签映射补全至实测可达全集（种子清单含 definite article，TB-2 枚举断言无透传）；S-2 发音呈现 kind 只余 {oxford, commons}、pronounce 耗尽返 200 {available:false}、前端词典代码 speechSynthesis 调用数=0、i18n 新增 dict.noAudio 三语一键。规格：工作区 `ticket126/SPEC.md`（D1–D8 决策 + R-126-1…14 + TJ-1…7/TB-1…3/E1…E8）。评分门：独立评分 R1 **9.96 PASS**（SCORE-R1.md）；微瑕 M-1…M-6 按过门约定折入实现首步（S-1 专属验收 / E1 记忆腿换 UK 有录音词 / §8.4 禁用态 TJ 断言 / §9.2 种子清单冻结回写 / §6.6 滚动关闭返焦不回滚 / 首焦点按 §6.8 Close 末位口径 + 键名钉死 dict.accent）。状态：DOING。
- **票 126 实现完成**（2026-10-23）：实现提交 `fe213a1`（登记 `1dc4967`）。内容：D1 词典卡迁 CDK Overlay 浮层（flexibleConnectedTo 四位序 下8px→上翻转→右对齐及翻转、视口边距 8、<640px 底部 sheet；内容落定/折叠后 updatePosition 按真实高度重选位序——开卡骨架高度会让 CDK 误选、flexibleDimensions 开着不走回退，两处均活体实测后修）；D2 已查词零描边（dict-looked 呈现全删）；F-1 404 判定改 errorStatusOf（原生/转换后两路），ApiClient 全局 rethrow 一字未动；F-2 前端分词 R-T1…T5（14-year-old 只查 year-old 段、纯数字零请求）；F-3 Close 钮 DOM 末位 + 绝对定位右上、Tab 序=视觉序；F-4 口音记忆 dict.accent（换词不复位、无录音 tab 禁用+「暂无发音·UK/US」标注）；S-2 发音呈现 kind 只余 {oxford, commons}、链耗尽 available=false 无 kind、词典代码 speechSynthesis=0、无录音=「暂无发音」状态行（新键 dict.noAudio 三语）、负卡删朗读钮；F-5 后端 PosLabel 映射补全+组合标签逐分量、form 英文码映射、标签值域断言、归元标注由 forms 角色生成。验证：后端 Assessment **433/433 PASS**（基线 383，+50：TB-1…TB-3 与 supersede 改写；StartupBudget 一例环境抖动、整套复跑全绿）；前端 Jest **335/335**（基线 293，+42：TJ-1 经真实 ApiClient+真实 HttpErrorResponse flush 404/500——构造期抓出两例假绿并修替身基建、TJ-2 用例表、TJ-3…TJ-7、M-1/M-3/M-5）、tsc 0、check-templates 23/23。活体 E1–E8（本票构建 5276 + 前端 4401，未扰 119 复核线 5266/4400）：E1 位移 374→374=0、E2 翻转后卡底=词顶−8、E3 负卡无 Retry 无发音钮、E4 点 year-old 段出 year 卡/点 80 零请求、E5 真实差集词 tintinnabulation 无钮+「暂无发音」、E6 纯键盘全路径 Close 末位、E7 sheet 三边 8px、E8 已查词 computed 无下划线+pageerror=0。**偏差 2 条（如实登记，全文工作区 `ticket126/VERIFY-126.md`）**：D-126-1 §8.7 前提与实况不符——词形钮在 119 即恒渲染、FormDto 无逐词形音频可用性，「无录音不渲染」需契约扩展（§12 未授权）未擅动；现行为 = 词形钮保留、pronounce 无录音静默不播、零机器兜底（规格问题 Q-1 待裁）；D-126-2 E1/M-1 the 的 UK 音频流遭 Wikimedia 对沙盒共享出口 429 限流未取到（钮在席、pronounce 200+commons key、日志实证；119 D-10 同型环境问题）。另 Q-2：§6.7「默认播放钮」括注与 119 已验行内 DOM 序（口音 tag 为行首个可交互元素）表述差，按继承条款保 119 行为、待裁。状态：DONE（推送与 CI 见 VERIFY-126 回填）。
