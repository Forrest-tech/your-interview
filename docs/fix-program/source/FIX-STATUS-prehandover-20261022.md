# 修复工程状态表（AUDIT 55 条）

- 工程基线：`develop-new-features` tip `88b2607`（= main 已合并点；修复只进 develop-new-features，合并 main 等用户点头）
- 票据目录：`~/workspace/fix-verify/fix-program/tickets/`（INDEX.md 为执行序）
- 方法：to-tickets 拆票 → implement+tdd 关闭（复现失败→修复→复现通过+回归）→ code-review 双轴抽查

## 状态总览（2026-10-22 接棒点数，新协调者核实）

- 远端 `develop-new-features` tip = `15c6639`（2026-10-23 00:23 EDT 核实；票 91 关闭提交；main = `88b2607` 未动。总览下方各行计数为接棒时点快照，最新动态以状态表各行与本节末尾接棒后记录为准）。关键提交祖先核验：a3321de/cb37c52/ab629ba/02510fb/7ddfc67/b192084/3144454/7cc5132 全部 ∈ tip 祖先链。
- **正单（票 01–54，覆盖 55 条）**：
  - 已关 20 张：01、02、03（含票 20 COD-03）、04、05、06、07、08、09、10、11、12、13、14、15、17、18、19、22、23 → 审计条目全关 23 条（COD-01/02/03/04/05/06/07/09/12、REQ-01/02、FUN-02/03/04/05/06、ARC-04/06/07/09/14、COD-14）+ COD-10 的 a/b 两面。
  - 进行中 2 张：**16**（COD-10c：cb37c52 已入 tip、第一道过；第二道独立真人化验收死于环境准备阶段——evidence-16/independent-acceptance.md 仅环境段、无结论，待重派）；**21**（COD-08：实现完成但**未提交**，残留在 wt-21 两文件（playbook-detail.component.ts + v2.i18n.ts，diff 与 evidence-21/07 报告一致）；API 层红→绿证据齐、tsc 过；UI 活体因自起前端 17200 连接拒绝未跑成（06-ui-verify.log 为失败日志，对照表 UI 两项系预填未证）→ 待收尾：提交 + UI 活体补验 + 第二道）。
  - 排队 31 张：24、25、26、27、28、29、30、31、32、33、34、35、36、37、38、39、40、41、42、43、44、45、46、47、48、49、50、51、52、53、54。
- **衍生/跟进/平台票**：已关 11 张（55、56、57、58、59、60、61、62、63、66、69）；待派 12 张（64、65、67、70、71、72、74、75、76-超限体、77、78、79）；待用户拍板 2 张（68 口径选择、73 呈现新需求定性——不派，终报 flag）。
- **编号冲突处置**：本文件末尾新登记的「票 76」（用户实测 Randstad 徽章/分组口径打架）与平台表既有票 76（超限请求体 500 非 413，已有票面文件）撞号。接棒时改编为 **票 80**（票面文件 80-detail-badge-outcome-consistency.md），原登记处保留交叉注记；验收阶段「票 76 场景」即指票 80 场景。
- worktree 巡检（R10）：除 wt-21 外，其余 worktree 均 clean 或停在各自已关提交，无其他未提交残留；verify15-wt / wt-ind-16 为验收残留（clean）。主 checkout 的 `sandbox-start.sh` 未提交修改系环境脚本本地修复（RabbitMQ 目录创建），非工程代码，保留不提交。
- **接棒派工（2026-10-22，新协调者）**：Batch A1 已派——票 21 收尾接管（wt-21：rebase+提交+UI 活体补验，回填 evidence-21）；票 16 第二道独立真人化验收重派（wt-ind-16b，逐任务落盘）；票 24 COD-13（wt-24）；票 28 ARC-08a 特征测试+coverlet 基线（wt-28）。待 A1 回批后验收并续派 A2（25/26/27/67）。
- **票面双编号系列注记（2026-10-22 协调者核实，R20 续）**：tickets/ 下 81–84 号存在两套票面——本账系列（81-logreview-no-activity-write【已关 4bd0aa8】、82-firstlogin-import-drops-legacy-keys【在跑】、83-category-name-overlimit-silent、84-delete-category-stale-chip）与并行 workflow 系列（81-knowledge-category-name-swallow、82-knowledge-heatmap-not-coloring、82-review-activity-write-missing、83-knowledge-legacy-key-import-timing、84-knowledge-stale-category-label）。内容映射：并行 82-review-activity≈本账 81（已关）；并行 83-legacy-key-import-timing 与本账 82 相邻（时序面 vs 丢键面，关 16 复验时合并核）；并行 81-category-name-swallow≈本账 83；并行 84-stale-category-label≈本账 84；**并行 82-heatmap-not-coloring 为本账未立之新发现**，票 16 复验时一并定性立票。COD-10 行内 F→票映射系并行系列口径，以本注记为准。
- **票 80 关闭（c6ee0a6，用户实测缺陷）**：详情页头部徽章与分组共用 outcome 单一事实源（v2.helpers 新增 entryStatusBadgeKey 纯函数、helper 断言 28/28；statusText 删 result 直出/Pending 分支/Analyzed 分支/默认 Completed 兜底）；改前 Randstad 同形态矛盾复现、改后三形态全过、盒模型徽章高 22/y 141 不变；对照表+偏差清单（仅验证方法申报）在 evidence-80/。已登记为正式验收永久回归场景。
- **票 86 关闭（cf376aa，COD-15 同族）**：#14 pronunciation/assess→MockAnswer、#34 speech/test→MockManage（定责：前端零调用方+凭据验证同族全 MockManage，报告论证）；单测 22→27 例红恰 2 红→全套 90/90；**活体双角色本票内完成**（改前 Viewer 两端点 503 过授权层→改后 403，User #14 非 403 不回退，读端点 200 不变，26/26×2）——票 25 延期活体的同法可行性已证，验收阶段按此补验。票面外发现 F-86-1（前端 testSpeech 死代码）→ 票 89。
- **票 82 关闭（0a92e70，票 16 复验 F2 跟进）**：真缺陷=根单例本地数据服务一次性闩锁三病（失败也闩锁/不认账号致跨账号串数据/未登录不设防），RED 两场景实测（换账号零导入见他人分类、宕机首载后同会话永不重试）；helpers 新增 shouldEnsureLoad/shouldAttemptImport 纯函数+§7 边界断言 75 例、tsc 0 错；票 14/15 同病面同型修复逐面实测（收藏/故事/复盘导入落库 DB 核对）。**诚实修正（入验收记录）**：F2 原形态「旧键被清」以正确植入复现不成立——第二道执行者 init-script 箭头函数字符串从未执行、产品代码从不删 ts.* 键；票 16 第二道「不过」结论中 F2 一项据此修正，T5 场景已入本票 GREEN 回归通过（热力图着色 lv-2/lv-1 正常——并行系列 82-heatmap-not-coloring 亦据此定性为同源误报，关 16 复验时合并核销）。票 16 待 83/84 落地后合并复验 T2/T5/D4b。
- **回流 R21（2026-10-22 22:05 协调者登记）**：agent 自起的 worktree 服务实例在 agent 死亡/完成后不退出，十余个孤儿 dotnet 实例把 load 打到 70、连环饿死在跑票。规矩：①执行者关闭报告前必须杀掉自己起的全部实例（端口/进程清单写入 STATUS）；②协调者在每波死亡后巡检 `pgrep -f wt-` 清孤儿；③并发重活维持 ≤3，前端构建错峰（R19 续）。本次已清场（仅保留看门狗标准实例）。
- **票 85 关闭（66487c3）**：InvitePasteParser 英文日期改 TryParseExact 格式表+InvariantCulture（双宿主探针逐形态对齐旧 en-US 口径含怪形态）；Jobs.Tests 双宿主各 226/226、负对照旧代码 invariant 下 20 例全红、活体旧实例三形态 400 CultureNotFound→修复 200；全仓 GetCultureInfo 仅此 2 处已清，解析域外两观察只清单不修。
- **票 76 关闭（a9e55b4）**：MapExceptionToStatusCode 补 BadHttpRequestException 透传框架 StatusCode（非硬编码 413，431/408/400 同理）；映射测试 32/32（红恰 6 先行）、全套回归 664/664、活体三服务 65MiB 改前 500→改后 413（21/21×3 轮、ProblemDetails 形状逐字不变）。框架异常缺口盘点（DbUpdate 等 7 项）只列不修 → 票 90 排尾待处置。
- **票 33 关闭（纯文档）**：9 份现状规格 + spec-conflicts-index（14 条现行冲突）落 replica/，详见状态表 REQ-16/17 行。
- **票 83 关闭（44523c4，票 16 复验 F3）**：分类名上限 100（读码三处一致）前后端双拦——前端字段级校验+错误透传（applyServerError 先例）、后端 RenameTopic 校验器+Import 文案含 100；API 活体 17/17（200/101 零落库、100 落库）、UI 13/13、Knowledge.Tests 72/72、票 82 闩锁冒烟 75/75 无回退。接管审查抓到前任半成品关键缺口（service 改 Observable 后组件未订阅=冷请求永不发）已补齐——接管先审 diff 的价值实证。
- **票 84 关闭（0d0af6a，票 16 复验 F4）**：删分类经共享服务 categoryDeleted$ 即时本地同步全部消费方（条目卡/详情头/复习队列/chips/计数/筛选），取舍=本地同步非重拉（报告论证）；同病追加：改名即时同步（categoryRenamed$）、删/改名连带自定义行防刷新复活（活体首跑 20/21 抓到后补齐）；活体 24/24、冒烟 26/26+82/83 复跑无回退。**票 16 跟进四票（81/82/83/84）至此全落地，待合并复验 T2/T5/D4b 后关 16。**
- **回流 R22（2026-10-22，票 84 执行者自报插曲）**：VM 替换后恢复 Postgres 时 rm -rf 了另一 postmaster 在用的 pgdata（替换后全新空库、无用户数据、共用库事后 psql 核验健康）。规矩：恢复/清理 pgdata 前必须先确认 postmaster 进程归属与数据目录所有权，禁止对未知归属的数据目录做破坏性删除；沙盒库一律视为可弃，但「在用」状态必须先停服务再动目录。
- **票 87 关闭（03afc0c）**：Assessment 快照漂移 D1/D2 清零——D2 补 HasMaxLength(20) 零 DB 变更；D1 手工空迁移（Up/Down 空）剔 UserResume 快照残留，**经协调者亲自复核迁移文件后才 apply**（门槛流程实证）；存量库 apply 后 resumes 表 3 行 checksum 逐字不变、空库全链 13 迁移通、has-pending 双轮归零、Assessment.Tests 90/90×2。自此 Assessment 自动 scaffold 迁移的捎带删表地雷拆除，传播警告解除。
- **票 16（COD-10c）正式关闭**：合并复验三场景全过（见状态表 COD-10 行）。
- **票 91 关闭（15c6639，REQ-18 发布半）**：Knowledge 建/删/改级/Restore 补发事件（新契约 KnowledgeItemDeleted 双载 + Analytics 删除消费者以票 34 台账为权威防双减）；死契约三删一保留标注；Knowledge.Tests 85/85、Analytics.Tests 58/58（双树复跑）；活体真总线台账逐步迁移+事件精确计数零重复发。REQ-18 至此双半齐；InterviewAnalysisCompleted 的 Worker 侧接线点建议另立票（见 evidence-91/CLOSE-REPORT）。
- **票 46 关闭（ca24c6f，COD-17）**：纯删除 40 行（AddWeaknessCommand + 前端两死包装），双证 grep+活弱项路径冒烟无回退；范围外申报（域 AddWeakness 方法/RemoveWeaknessCommand）→ 票 93。
- 票 24 关闭（9c97312，接棒后首关）：共享映射 +JsonException→400 一行；ReadFromJsonAsync 请求体直读全仓盘点仅 AttachAsset 1 处（其余 5 处为响应体读取，已列影响面）。口径备注（实现者申报、协调者接受）：400 响应 Type 仍为通用 ProblemDetails 分支既有硬编码 15.6.1 URI——全站既有形态、非本票引入；如需按状态细分 Type 属独立治理决定，暂不立票。

## 第 0 步复核结论（基线 a4fbe5a → 88b2607，2026-10-21）

两笔新提交：`8087787` 详情整批（35 文件）、`88b2607` 多图聚合。对 55 条逐条复核：

- **结论：55 条全部仍在，无一被两批次顺带修掉。**
- 首要嫌疑项逐行复验（7 项）：
  - COD-06 仍在：playbook-v2.api.ts:68 仍读 `transcriptText`，InterviewsController.cs:231 仍返 `fullText`（两批次对 v2.api 仅 +1 行 language 参数）。
  - COD-07 仍在：playbook-v2.api.ts:147 仍 POST `/assets/{id}/transcribe`，后端只有 `{id}/transcription/start`（InterviewsController.cs:203）。
  - COD-08 仍在：playbook-detail.component.ts:537-541 仍 PUT `/actions/{id}/toggle` + `.catch(() => undefined)` 吞错。
  - COD-13 仍在：8087787 对 InterviewsController 仅改 GeneratePrepReport 的 Language 参数，AttachAsset 未触及。
  - FUN-02 仍在：tracker.component.ts 的 `create()` 负载与 `updateAppPayload()` 均不含 jdText（JD 两路皆丢）；updateAppPayload 另把 workMode/jdSummary/source/deadline 硬编码 null（编辑误清，归 09 票一并修）。
  - FUN-03 仍在：CreateEntryCommandHandler（InterviewHandlers.cs:824-）无 JobApplicationId 去重，恒新建。
  - ARC-16 仍在：playbook/components/ 旧代（brief/qa/analysis、rounds-timeline）全部在位，未删一件。
- 锚点抽验（5 项）：COD-01 三服务 Domain 仍无核心实体 UserId（仅 Jobs 用户级资产有）；COD-04 InterviewAggregates.cs:1067-1078 与审查原文逐字一致（仅 ChecklistJson 有守卫）；COD-10 yi-tracker-favorites/questions 键仍在（4 处引用）；FUN-06 `mock.modeTopicDrill` 在 i18n DICT 仍 0 定义；REQ-14 tracker/techstack 原生 confirm/prompt 计数 6/2 未变；REQ-15 抽屉无 workMode 展示绑定；ARC-17 src/Gateway 仍在 sln。
- 其余 43 条：其证据文件均不在 `git diff a4fbe5a..88b2607` 的 35 个改动文件内，原证据成立，判仍在。

## 状态表（每票：编号 | 状态 | 提交哈希 | 证据路径）

状态口径：已立票 / 进行中 / 已修（协调者验收） / 批次顺带已修 / 经复核不成立。第 0 步后：批次顺带已修 0 条、不成立 0 条，55 条全部立票。

| 审计编号 | 票 | 状态 | 提交哈希 | 证据路径 |
|---|---|---|---|---|
| COD-04 | 01 | 已修（协调者验收） | a370180 | Services.Interviews.Tests/RoundPartialUpdateTests.cs |
| REQ-01 | 02 | 已修（协调者验收） | 952b1dd | fix-program/evidence-02/ |
| COD-14 | 02 | 已修（协调者验收） | 952b1dd | fix-program/evidence-02/ |
| COD-01 | 03 | 已修（协调者验收） | c60e682 / 2589a55 / cb7e1fd | 本文件下方验收注记 + /tmp/cod01_verify.py 一次性脚本 |
| REQ-02 | 04 | 已修（协调者验收） | 7390730 | fix-program/evidence-04/（实现者报告+协调者复跑 Jobs 8/8、Interviews 68/68） |
| ARC-07 | 04 | 已修（协调者验收） | 7390730 | 同 REQ-02 |
| ARC-14 | 05 | 已修（协调者验收） | 760876a | fix-program/evidence-05/ |
| COD-02 | 06 | 已修（协调者验收） | 8db7904 | AnalyticsController.cs diff 复核 + 实现者实测（攻击请求落调用方名下、受害方 0 行） |
| COD-05 | 07 | 已修（协调者验收） | 1cab100 | fix-program/evidence-07/ |
| COD-06 | 08 | 已修（协调者验收） | 639f867 | fix-program/evidence-08/ |
| COD-07 | 08 | 已修（协调者验收） | 639f867 | fix-program/evidence-08/ |
| FUN-02 | 09 | 已修（协调者验收） | 0804344 | fix-program/evidence-09/ |
| FUN-03 | 10 | 已修（协调者验收） | 73f82ab | fix-program/evidence-10/ |
| FUN-04 | 11 | 已修（协调者验收） | 13b99c1 | fix-program/evidence-11/ |
| FUN-05 | 12 | 已修（协调者验收） | 516d4ab | fix-program/evidence-12/ |
| FUN-06 | 13 | 已修（协调者验收） | 026e949 | fix-program/evidence-13/ |
| COD-10 | 14/15/16 | **已关（双道齐）**：14/15 第一道关；16 第一道过（cb37c52）+ 第二道首轮不过开 F1–F4 → 跟进票 81（4bd0aa8）/82（0a92e70）/83（44523c4）/84（0d0af6a）全落地 → **合并复验三场景全过、破坏配额无新 S1/S2、F2 正式核销（验收侧植入失效非产品缺陷，真缺陷闩锁三病已由 82 修）**（evidence-16/reverify-20261022/verdict.md）。复验新发现 N1（双击竞态 500+异常外泄，S3）→ 票 92 | 7ddfc67 / a3321de / cb37c52 + 跟进四票 | fix-program/evidence-14/、evidence-16/ |
| ARC-04 | 17 | 已修（协调者验收：独立复跑 Assessment.Tests 55/55、grep 残留双证为零、活体三端点 404；纯删除无 UI 面，真人化验收不适用） | 2edb30b | fix-program/evidence-17/ |
| ARC-09 | 18 | 已修（协调者验收：独立复跑 Interviews.Tests 184/184 + BuildingBlocks 18/18；七服务探针改前改后仅一处预期差异） | ca629eb | fix-program/evidence-18/ |
| ARC-06 | 19 | 已修（协调者验收：独立复跑 Interviews.Tests 184/184、零迁移核对；四项口径裁定见 evidence-19 裁定记录） | 5d772b2 + 70bc7e7 | fix-program/evidence-19/ |
| COD-03 | 20（并入 03） | 已修（协调者验收，随票 03） | c60e682 | CoverLetterHandlers.cs:185-192 读码确认 |
| COD-08 | 21 | 已修（第一道通过：收尾接管完成 rebase+提交 b1a3d7a、API 复跑绿、UI 场景 A 全绿（06c-results.json）+ 失败 snack 截图在盘；**第二道独立真人化复验两任均死于环境（负载/daemon 重启，检查点在 evidence-21/independent-acceptance.md，环境已搭好）——按 R19 暂缓，空位时续派**） | b1a3d7a | fix-program/evidence-21/ |
| COD-09 | 22 | 已修（协调者验收：收尾 agent 独立 facet 核验 Designer 为历史时点模型（107→113→114 vs 现行 170、UserId 零出现）、空库 19/19 迁移、has-pending 归零、Tests 184/184 原始工件已由协调者直读） | 0440255 | fix-program/evidence-22/ |
| COD-12 | 23 | 已修（协调者验收：独立复跑 Assessment.Tests 63/63；修复为单点 FirstOrDefault 对齐四先例，红→绿 3 例实证） | a3463f7 | fix-program/evidence-23/ |
| COD-13 | 24 | 已修（协调者验收：diff 2 文件与报告一致、独立核 tests-after.xml 逐例耗时真实、活体三形态 500→400 且邻居映射全不回退；纯后端无 UI 面，第二道不适用） | 9c97312 | fix-program/evidence-24/ |
| COD-15 | 25 | 已修（协调者验收：六任接力后由协调者收尾——diff 恰 2 文件（授权属性 2 行 + 测试 170 行）、红测恰 2 红→绿、全套 Assessment.Tests 协调者按定版规程在 rebase 后树复跑 85/85（coordinator-regression.xml）；**双角色活体经协调者裁决延期至正式验收阶段补验**（理由与追踪见提交 0c2b18a 信息及 evidence-25/STATUS.md），不静默豁免。盘点票面外发现 #14/#34 → 票 86） | 0c2b18a | fix-program/evidence-25/ |
| REQ-05 | 26 | 已修（第一道通过：GetEffectiveResume 回落文本简历（kind=text 加性 DTO）+ 抽屉 text 形态呈现；新测 9 例、Jobs.Tests 186/186、三形态 UI 截图与消费方逐项回归在 evidence-26；执行者最终消息丢失但落盘报告完整，验收据文件完成。**待第二道独立真人化复验**） | 4fd9247 | fix-program/evidence-26/ |
| REQ-08 | 27 | 已修（第一道通过：守卫抽纯函数 radar.helpers、零数据两页不再列弱项/Best、有数据截图与改前 SHA256 逐字节一致、偏差清单空；**待第二道独立真人化复验**。边角口径：部分有分时未评分维度 Best 仍空白 → 票 88） | 1b6422e | fix-program/evidence-27/ |
| ARC-08 | 28/54/64/65 | 进行中（28 解析器面已修：SmartParse 0→97.84%、后端基线 8.14% 已立；54/64/65 待派） | 1502777 | fix-program/evidence-28/ |
| REQ-09 | 29 | 已修（第一道通过：内联 Benefits/摘要跳元数据行/公司名 FUN-09 两形态/About 误判四缺口全翻绿，活体+单测双证；Jobs.Tests 206/206；SmartParseRules 97.94%≥基线 97.84%；偏差清单空。**待第二道**。观察项：role-first 后缀形态摘要仍以公司行开头（非审计元数据行），报告单列待后续定性） | 8d29a69 | fix-program/evidence-29/ |
| REQ-10 | 30 | 已修（协调者验收：日期序展示编号全消费面统一（helpers 四纯函数+断言 33/33）、新建轮次默认值旁注三语 hint 活体 HINT_OK、改前复现/改后三场景/邻居回归全过、R21 收尾干净；偏差清单 5 条含 CommLog/MaterialsList 无种子未活体点验（同 helper+tsc 覆盖，验收阶段可抽查）） | f99cfdd | fix-program/evidence-30/ |
| REQ-11 | 31 | 已修（协调者验收：改前 328px column 实测复现→仅 tracker.component.scss 一文件改细工具条，三状态全 48px/row、批量动作逐个点验全过（改状态落库/归档/删除/全选/清除/退出）、邻居回归过、对照表在 evidence-31、R21 收尾干净） | 6e37ad2 | fix-program/evidence-31/ |
| REQ-15 | 32 | 已修（协调者验收：抽屉 Overview 末格加 Work mode（复用既有 i18n 键、空态「—」同既有写法）；改前 6 格缺位实锤→改后 Hybrid/— 两形态、既有 6 格盒模型逐格零位移、邻居 5 页签+Smart-add 回归过；偏差清单空） | 68bc118 | fix-program/evidence-32/ |
| REQ-16 | 33 | 已修（协调者验收：9 份现状规格落 replica/（基线以执行时 tip f99cfdd 实查，主 checkout 未动、git check-ignore 验证不入库），关键口径带代码出处；冲突索引 21 标签归并 14 条现行冲突全回指既有编号；读码新发现 CONF-AN-3 为 REQ-18 分叉成因、CONF-CAL-2 提醒无后端实现、CONF-CAL-3 .ics 端点与定稿裁决不一致——后两条随索引待相关票/用户定性） | —（纯文档不入 git） | fix-program/evidence-33/ + replica/spec-conflicts-index.md |
| REQ-17 | 33 | 已修（随票 33：Tracker 自有定稿 tracker-spec.md（状态机 AllowedTransitions/十状态、字段口径、与 Playbook 同源四规则）+ 日历定稿 calendar-spec.md 落盘） | —（纯文档） | fix-program/evidence-33/ |
| REQ-18 | 34 | 已修（协调者验收·消费侧：CONF-AN-3 原值回写废除、改单条目台账增量迁移（MasteryItemState 新表+迁移全套）；等级语义错位同修（事件 Level=枚举 int 旧按 0-100 消费致 Mastered 恒 0/雷达 0-4 → MasteryLevelMapping 对齐）；reconcile/rebuild 内部端点落地（幂等、真源不可达零写库）；Analytics.Tests 51/51、邻居 68+226 回归；活体一阶段真总线实证、二阶段 HTTP 一跳被沙盒策略拦截以单测+失败语义降级验证（报告明示）。**发布侧缺事件未扩围 → 票 91**） | faedc0b | fix-program/evidence-34/ |
| FUN-07 | 35 | 已立票 | — | — |
| FUN-08 | 36 | 已立票 | — | — |
| COD-21 | 36/48 | 已立票（Analytics 面 36 / Mock 面 48） | — | — |
| FUN-10 | 37 | 已修（待协调者验收：基线 Edit 后 textarea=0、空存 PUT 400 弹黑话双面复现 → 编辑态 textarea 带现值可改可存刷新仍在、非编辑态无 PDF 时全文只读可见、空/纯空白存零 PUT 前端拦截且三语人话提示实测、同步失败黑话三语改明确提示；§7 helpers 断言 16/16；邻居 effective-resume kind=text 回读一致、txt/PDF 文件路径回归过；tsc 0 错；对照表+偏差清单（为空）在 evidence-37/CLOSE-REPORT.md） | 7d8676c | fix-program/evidence-37/ |
| ARC-10 | 38 | 已立票 | — | — |
| ARC-12 | 39 | 已立票 | — | — |
| ARC-15 | 40 | 已立票 | — | — |
| ARC-16 | 41 | 已立票 | — | — |
| ARC-11 | 42 | 已立票 | — | — |
| ARC-13 | 43 | 已立票 | — | — |
| REQ-12 | 44 | 已立票 | — | — |
| REQ-14 | 45 | 已立票 | — | — |
| COD-17 | 46 | 已修（待协调者验收：三处纯删除 40 行/2 文件——AddWeaknessCommand record+Validator+Handler（删前双证：全仓仅定义 6 行、零发送方/无端点/零测试引用，MediatR 与 Validator 均程序集反射扫描无显式注册条目、快照零提及）+ playbook-v2.api.ts 的 addKnowledgeSource（目标端点 POST /knowledge/{id}/sources 不存在）与 runAnalysis（后端 /analyze 活端点保留）；删后 grep 残留 0、全解构建 0 错误、Interviews.Tests 184/184、tsc app 口径 0 错；活体冒烟：分析回写弱项经详情回读命中（AiAnalysis 来源）、/analyze 202 与 Draft 409 口径不变、弱项标题 Playbook 详情页可见，弱项活路径零误伤；域 AddWeakness 方法与 RemoveWeaknessCommand 不在票面范围保留申报，详见 evidence-46/CLOSE-REPORT.md） | ca24c6f | fix-program/evidence-46/ |
| COD-20 | 47 | 已立票 | — | — |
| ARC-17 | 49 | 已立票 | — | — |
| ARC-18 | 50 | 已立票 | — | — |
| ARC-03 | 51 | 已立票（重构批） | — | — |
| ARC-01 | 52 | 已立票（重构批） | — | — |
| ARC-02 | 53 | 已立票（重构批） | — | — |

## 批次汇报

- S1 批（01/02/03）：**全清**（01 a370180、02 952b1dd、03 c60e682+2589a55+cb7e1fd，均经协调者独立验收）
- S2 批：进行中（04/06/08 + 跟进票 55 已放行）

## 进度注记

- 2026-10-21：第 0 步复核完成（55 条全部仍在）；54 张票落盘 fix-program/tickets/；S1 批开工：01（wt-cod04）、02（wt-req01）实现中，03 方案撰写中。

- 2026-10-21：票 03 方案完成并经协调者裁决确认——A 只在 5 个聚合根加列；B Worker 回写走 OwnerUserId+校验、不给服务账号全量豁免；C Knowledge mastery 发布器 Guid.Empty 并入本票修；D 回填改 EF 数据迁移（expand→backfill→contract 共 9 迁移，按邮箱子查询取 admin、带守卫），否决独立脚本（用户真实库部署须零手动步）；COD-03 随本票 Jobs 面收口。

- 2026-10-21：票 02 验收通过（952b1dd）。协调者复核：diff 12 文件与票面一致；守卫 SQL 已读码（Up/Down 双向 IF EXISTS + DROP TABLE IF EXISTS）；七服务 Program.cs「继续启动」catch 残留 grep=0；新测试 3 用例读码为真行为断言（原异常透传/Error 日志/成功放行）。实现者运行时证据：空库冷启动 12 迁移全应用、categories/materials 200；只读库七服务全 fail-fast（退出 134+Error 日志）；全量测试 161 绿。注：协调者本机复跑 BuildingBlocks.Tests 时 testhost IPC 环境异常（连接后收到杂字节断开，6 次尝试皆然，23:31 实现者同命令曾通过），复跑未成——已以读码+证据复核替代并在此明示。
- 票 02 票面外发现（登记待办）：迁移文件 20261017120000_AddCategoryLinkedSource.Designer.cs 的 [Migration] ID 实为 20260927164710，与文件名不一致——并入票 22（COD-09 迁移卫生）范围，实施时一并核对。

- 2026-10-21：票 01 验收通过（a370180，基 952b1dd）。协调者独立复核：领域层 ApplyPartial 协议 diff 读码无误（七字段统一 null保持/空串清空/非空赋值）；新增 RoundPartialUpdateTests 4 用例经 xunit console runner 独立复跑 4/4 通过；Interviews.Tests 全量实测 Total 56、Failed 1（唯一失败为既有陈旧测试，见下）。前端两处适配（清空改发空串）经报告论证成立——旧写入方靠 null 表达清空，不改会被新协议吞掉。注：票 02 报告中「Interviews 115」计数与实测 56 不符，应为其全量口径误记，不影响票 02 结论（其 AC 证据为运行时实测，独立成立）。
- 陈旧测试裁决（票 01 票面外发现 1）：AttachAssetValidator 对 RoundId 无规则属有意设计（条目级材料挂 EntryId，已上线功能依赖），断言相反的测试陈旧——立跟进票 55（fix-program/tickets/55-stale-attachasset-test.md）：改测试、不动校验器。
- 环境注记：沙盒 dotnet test 的 vstest↔testhost TCP 被引向 egress 代理端口而损坏（票 01/02 两位实现者与协调者三方实证）；后续各票测试执行统一可用 xunit console runner（DOTNET_ROLL_FORWARD=LatestMajor + ~/.nuget/packages/xunit.runner.console/2.9.2/tools/net6.0/xunit.console.dll）作为等价通道，环境修复另行处理、不阻塞工程。

- 2026-10-22：票 03 验收通过（实现者交付前被运行时重启打断、报告丢失，验收由协调者全量自做）。复核清单：9 条迁移逐条带 Designer.cs + 3 份 Snapshot 更新、回填 SQL 带存在性守卫+邮箱子查询取 admin（diff grep 无硬编码 GUID）；活库五聚合根 UserId NULL=0、三服务迁移史各 +3、admin 基线计数与方案基线一致（apps 9/companies 5/comms 5），B/C 验收账号数据各归其主；全量构建 0 错误。双账号独立实测（协调者自注册 verify-d 账号）：三服务列表对 D 皆空、跨用户 GET 404×3、D 自建仅 D 可见且 admin 总数不变、D 以 jobs.write 权限 PUT admin 投递被 Handler 谓词挡 404 且数据未变、D DELETE 因默认角色无 jobs.delete 在授权层先行 403（数据完好——比票面 404 更严，口径差异在此注明）。Knowledge 一度显示 admin 列表为空，追查为实现者验收留下的 3 条软删测试数据（过滤正确），已用活数据正反双向补验（含 includeDeleted 仅见自己人的删除项）。Worker 回写：错 OwnerUserId→404、正确→过归属门（业务 409 停下）、D 普通路径→404。Interviews.Tests 回归 56 用例仅陈旧测试 1 失败（票 55）。COD-03 读码确认命令带 UserId+双条件删除。

- 2026-10-22：跟进票 55 已修验收（e3864f6）：仅动 AssetRoundPhaseTests.cs（+11/−1，校验器零改动 diff 自证）；陈旧用例改 EntryLevelWithoutRoundId_Valid + 新增 MissingEntryId_Invalid；协调者独立复跑 Interviews.Tests 全量 Total 57 / Failed 0（基线 56/1 转全绿）。

- 2026-10-22：票 06 验收通过（8db7904）：RecordAbility 一律 Me、DTO 移除 UserId，全仓同型模式仅此一处（实现者 grep 自证）。**连带裁决**：Analysis.Worker 的服务账号回写此前正走这条 body-UserId 通道，堵死后快照改落服务账号名下（同步刷新降级；事件驱动的异步读模型不受影响、无数据泄漏）——立跟进票 56（fix-program/tickets/56-…md）：照裁决 B 模式开可信通道（ownerUserId 仅服务账号身份 honoured），排队待 04/08 波次后执行。

- 票08 独立验收（协调者, 639f867）：diff 与报告 5 文件一致（Interviews+前端 api）；领域 StartTranscription(assetId) 精确选材/无回落读码确认；独立复跑 Interviews.Tests 62/62 全绿、web tsc 通过；活体证据（多轮条目资产级转写 204 且台账 AssetId 精确指向第 2 轮录音、文本材料/假材料 409、条目级旧入口回归 204）在 evidence-08/。注意：新 worktree 跑 xunit runner 前需从 nuget 缓存补 Microsoft.Extensions.Caching.Abstractions.dll 到测试输出目录（copy-local 差异，非代码问题）。

- 票04 独立验收（协调者, 7390730）：diff 11 文件与报告一致；SharedContracts InterviewOutcome 枚举在位、LegacyOutcomeTextAdapter 全仓仅 EventConsumers.cs 一处、Jobs RoundOutcome.Ghosted=5（字符串存库无迁移）；独立复跑 Jobs.Tests 8/8 + Interviews.Tests 68/68 全绿。活体 RabbitMQ 真链证据（Rejected→status Rejected+补建 Failed 投影、Passed→Interview、stats inInterview 9→11 与 Playbook 一致）在实现者报告内，测试数据已清、基线复原。深层根源（UpdateRound 旧码根本不发事件）已随本票修复。

- 票07 独立验收（协调者, 1cab100）：diff 3 文件与报告一致；MapExceptionToStatusCode 映射档位读码确认（并发→409 带 code=concurrency_conflict，前端 ApiClient 已接）；独立复跑 BuildingBlocks.Tests 10/10、web tsc 通过；活体 8 路并发证据（前 500×4 → 后 409×5，其余映射回归不变）在 evidence-07/。

- OCR 接入复核（父 agent 派单，协调者实证，evidence-57/repro-before.txt）：「只吃 body 字段」陷阱在生产不存在——AiGateway 只回全文转写、parsePastedEmail 的 p.body 含会议卡（两形态实测）；但 extractInterviewWhen 抽不到卡片格式 `2026-10-13 周二 10:00～11:00`（星期夹中间+全角波浪号），生产形态实测 when=null，会议时间实际仍漏抽 → 立跟进票 57 修复（抽取域保持去头 body——全文跑会误抽 Sent 发信时间，实测已证）。subject 窗口前缀与签名单字误读按派单口径不立票（预览校对兜底）。

- 票05 独立验收（协调者, 760876a）：diff 6 文件一致；全部 entryResultOutcome 调用点改传 e.outcome、v2.helpers 关键词正则零残留（grep 实证）；独立复跑 Interviews.Tests 88/88、tsc 通过。五例矩阵与三项已知差异（不通过/挂了→Rejected、定论轮次压过等待文本，均向票 04 口径靠拢并锁定测试）见 evidence-05/comparison-matrix.md。
- 票05 票面外发现 1 立跟进票 58：纯文本路径手填英文受控词（Rejected/Passed）时 Playbook 分组有结论（DeriveOutcome 规则 2）但 Tracker 不回写（发布端留空 + Legacy adapter 只认中文关键词）——给 adapter 补「整串精确匹配英文受控词」与 DeriveOutcome 规则 2 对齐。等票 09 落地后派（Jobs 区域串行）。

- 票57 独立验收（协调者, c3af5b7）：单文件改 extractInterviewWhen（+10/−3）；协调者对拉取后的主 checkout 源码复跑机械抽取 harness 8/8（新格式 3 类+生产形态整段=2026-10-13 10:00、未误抽 Sent 时间；旧格式不回归），tsc 通过。OCR 接入复核派单闭环。

- 票09 独立验收（协调者, 0804344）：深层根源后端 CreateApplicationCommand 本无 JdText/Notes 字段已一并补齐（落库+校验上限、Update 补 JdText 且 null=保持语义）；活体证据全套（建单落库/编辑四字段保留/局部 PUT 不误清/空串清空/超长 400/Smart paste 回归）在 evidence-09/；独立复跑 Jobs.Tests 8/8、tsc 通过（实现者测试口径补正：vstest 通道沙盒超时，console runner 定版，与协调者通道一致）。

## 平台能力票流（非 55 条正单，独立小节）

| 票 | 内容 | 状态 | 提交 | 证据 |
|---|---|---|---|---|
| 57 | OCR 入口会议时间抽取（extractInterviewWhen 扩卡片格式） | 已修（协调者验收） | c3af5b7 | fix-program/evidence-57/ |
| 59 | 共享 OCR 服务（PaddleOCR 主 + DeepSeek 视觉兜底）+ Playbook 入口切换 | 已修（协调者验收） | 09997d8 | fix-program/evidence-59/ |
| 60 | 本地 OCR 文本头部适配（邮件截图本地成卡） | 已修（协调者验收） | 7f67913 | fix-program/evidence-60/ |
| 66 | Assessment 已删会话 GET 仍可读回（MockSession 漏配软删过滤器） | 已修（协调者亲验：活体 before 10/10 复现→after 13/13；Tests 39/39） | 02510fb | fix-program/evidence-66/ |
| 67 | Assessment 模型快照漂移盘点（票 66 动手前基线实测先存漂移） | 已修（盘点关闭：漂移 2 条成文——D1 快照残留 UserResume（票 17 引入、处置高危）、D2 LinkedSource 长度；票 66 过滤器经机理实证不在快照口径内；处置 → 票 87） | —（只读盘点，无提交） | fix-program/evidence-67/drift-inventory.md |
| 68 | JobsDelete 权限与默认角色缺口核查（票 14 票面外发现 1） | 待派（口径选择需用户拍板） | — | fix-program/tickets/68-jobsdelete-role-gap.md |
| 69 | Assessment AddQuestions 对全新会话恒回 409（票 66 复现现场发现） | 已修（协调者验收：独立复跑 Tests 55/55、diff 读码口径保守） | ab629ba | fix-program/evidence-69/ |
| 62 | SmartAdd 四列（JdSummary/Requirements/Benefits/CompanyDescription）扩 text + 200,000 统一闸门 | 已修（协调者验收：独立复跑 Jobs.Tests 54/54=基线 37+新 17；迁移 Up/Down 各四列 AlterColumn 对称；存量 38 行 md5 无损；活体八探针 500→落库/400） | 7cc5132 | fix-program/evidence-62/ |
| 56 | Analytics 回写可信通道（票 06 连带裁决） | 已修（协调者验收：独立复跑 Analytics.Tests 10/10、diff 9 文件与裁决范围一致；活体五场景 16/16 含 PowerUser 不 honoured 关键分界与票 06 攻击回归） | b192084 | fix-program/evidence-56/ |
| 76 | 超限请求体返回 500 而非 413（票 18 票面外发现） | 待派（S4） | — | fix-program/tickets/76-oversize-body-500-not-413.md |
- 票18 验收注记（协调者）：实现者报告中 Interviews.Tests 记 122 系其 rebase 前基线跑数；协调者在最终树 ca629eb 独立复跑为 184/184（票 19 已并入），零回退结论成立、数字口径在此更正。另：审计 ARC-09 对录音端点的旁证与实测有出入（AttachAsset 本有 [RequestSizeLimit(210MB)]，30MB 默认不咬合该端点；真咬合点在无属性端点，改后 500→400）——总表回填时按实测口径注记。票面外发现（超限体 500 非 413）立票 76。
- 票15 关闭注记：独立验收 agent 在报告落盘后、交付前被运行时杀死（R13 同型），因逐任务落盘纪律报告完整无损（evidence-15/independent-acceptance.md），验收结论有效、无需重跑——检查点纪律第二次实证有效（第一次为票 17/19 盘点）。复验 F4（双页签 last-write-wins）按任务书口径属可接受行为，仅观察记录；与票 70（Knowledge 同型现象）一并留待用户若要全局冲突提示时定性。
| 72 | Mock 出题主流程接线核查：next-question 端点未实现（票 69 票面外发现） | 待派（核查票，先定性后决定是否立修复票） | — | fix-program/tickets/72-mock-next-question-wiring.md |
| 81 | Log review 不写复习活动、热力图永不据实着色（票 16 复验 F1，S2） | 已修（协调者验收：定责前端——Log review 成功回调漏调既有 recordReviewActivity()，1 行修复 + 后端特征测试 1 例；diff 2 文件 +30 与报告一致；改前/改后活体全套 + Knowledge.Tests 68/68 在 evidence-81；**另有并行 runner 独立同修一份并完成红/绿验证（3 次 Log→lv-2，evidence-82/report.md）后自检发现重复而停推——独立旁证成立**；票 16 的 T2 场景独立复验待 82/83 落地后合并派） | 4bd0aa8 | fix-program/evidence-81/、evidence-82/report.md（旁证） |
| 82 | 首登路径旧数据导入失效且静默清旧键（票 16 复验 F2，S3） | 待派（首派撞上并行 runner 同名 worktree 未果，改 wt-82b + evidence-82-firstlogin/ 重派） | — | fix-program/tickets/82-firstlogin-import-drops-legacy-keys.md |
| 83 | 超长分类名对话框假成功、静默不落库（票 16 复验 F3，S3） | 待派（排 82 后） | — | fix-program/tickets/83-category-name-overlimit-silent.md |
| 84 | 删分类后条目卡/chip 旧名不刷新（票 16 复验 F4，S4） | 待派（S4 批） | — | fix-program/tickets/84-delete-category-stale-chip.md |
| 85 | InvitePasteParser 英文日期在 invariant 宿主（生产形态）抛 CultureNotFound（票 28 发现） | 待派（Jobs 解析域，可与票 29 同批串行） | — | fix-program/tickets/85-invite-parser-culture-invariant-host.md |
| 86 | Assessment 另两 POST 端点（pronunciation/assess、speech/test）同挂 MockRead（票 25 盘点发现） | 待派（Assessment 区域，排票 25 后） | — | fix-program/tickets/86-assessment-more-mockread-posts.md |
| 87 | Assessment 模型快照对齐（D1 剔 UserResume 快照残留 + D2 补 HasMaxLength(20)，手工空迁移）（票 67 盘点处置） | 待派（Assessment 区域；**落地前其余 Assessment 迁移票禁止 apply 自动 scaffold 的迁移——必逐行剔 DropTable**，见 evidence-67 §4） | — | fix-program/tickets/87-assessment-snapshot-alignment.md |
| 88 | 部分有分时未评分维度进 Top 3 仍显示空白 Best（票 27 透明说明，S4） | 待派（S4 批） | — | fix-program/tickets/88-partial-radar-blank-best.md |
| 73 | Tracker 列表/流程视图无收藏入口（票 14 复验 F1，非回归、既有覆盖不全） | 待用户定性（补入口=呈现新需求，先效果图） | — | fix-program/tickets/73-tracker-listview-favorite-entry.md |
| 74 | Tracker 自备问题超长无空格文本横向溢出（票 14 复验 F2） | 待派 | — | fix-program/tickets/74-tracker-question-overflow.md |
| 75 | 长度超限校验错误文案不指明上限（票 14 复验 F3） | 待派（可并 S4 批） | — | fix-program/tickets/75-validation-error-copy.md |
| 14 | COD-10a Tracker 收藏/自备问题上后端（Jobs 面） | 已修（双道验收通过，见上注） | 7ddfc67 | fix-program/evidence-14/ |
| 15 | COD-10b Playbook 本地数据上后端（Interviews 面） | 已修（双道验收：第一道独立复跑 122/122（票 19 后基线 184 另验）；真人化复验 A1–A5/D1 过，A6 随机后缀变体不过→票 77、D2 竞态 500→票 78、D4 零反馈→票 79） | a3321de | fix-program/evidence-15/ |
| 77 | 孤儿键后缀匹配不上现存条目被静默跳过永不归档（票 15 复验 F1，S3） | 待派 | — | fix-program/tickets/77-orphan-keys-unmatched-entry.md |
| 78 | STAR 故事连点保存并发 upsert 竞态 500（票 15 复验 F2，S3） | 待派 | — | fix-program/tickets/78-starstory-upsert-race-500.md |
| 79 | STAR 故事空标题拦截零反馈（票 15 复验 F3，S4，与票 75 同类） | 待派（可并 S4 批） | — | fix-program/tickets/79-starstory-form-validation-feedback.md |
| 16 | COD-10c Tech Stack 分类/复习记录上后端（Knowledge 面） | 已交付待真人化复验（第一道过：独立复跑 Knowledge.Tests 67/67；日期口径本地日历日保真已核） | cb37c52 | fix-program/evidence-16/ |
| 70 | Knowledge 详情双页签编辑静默覆盖（S3，票 61 独立复验 D3 发现） | 待派 | — | fix-program/tickets/70-knowledge-dual-tab-overwrite.md |
| 71 | Knowledge 界面文案三处 S4（票 61 独立复验发现） | 待派（可与 S4 批合并） | — | fix-program/tickets/71-knowledge-ui-copy-s4.md |
| 61 | 详情编辑清空 Question（数据丢失，票 12 票面外发现） | 已修（协调者验收 + 独立真人化复验通过） | 7011af9 + 6a37e73 | fix-program/evidence-61/ |

- 票58 独立验收（协调者, 6c8b8ed）：diff 2 文件一致，adapter 精确匹配与 DeriveOutcome 规则 2 同口径读码确认（TryParse+名字全等防数字串）。
- ⚠️ 测试环境定版（票58 发现、协调者复核）：新构建的测试输出缺多个 Microsoft.Extensions.* 程序集时，DB 门控测试的 CanConnectAsync 抛 FileNotFound 被 catch 吞掉 → 用例 ~0.001s **空过假绿**（主 checkout 旧 bin 同样缺 Configuration.Abstractions/Primitives）。定版规程：跑 DB 门控套件前先把 nuget 缓存 microsoft.extensions.*/8.0.0 全套 dll 拷入测试输出，并以 -xml 核对逐例耗时。按此规程复验：Jobs.Tests 12/12（总 9.4s、逐例 0.014–4.9s 真实执行）、Interviews.Tests 88/88（RoundPartialUpdate 逐例 0.03–1.1s、PrepPlanFlow 1.28s）——票 01/04/05 的 DB 门控部分在真实执行下依然全绿，先前验收结论不变。

- 票10 独立验收（协调者, 73f82ab）：diff 3 文件一致；按定版规程（全套 dll+XML 逐例耗时）复跑 Interviews.Tests 93/93，同轮 DB 门控类耗时真实（PrepPlanFlow 1.3s、RoundPartialUpdate 1.17s），CreateEntryDedupe 5 例真实落库口径成立；活体复现/修复证据（同 id 两调返回同一 id、计数 +1；无关联仍新建；跨用户不复用）在 evidence-10/。

- 票59 独立验收（协调者, 09997d8）：diff 9 文件一致；协调者用主 checkout 代码+venv 直跑实测——health ok、小图（crop-header）POST /v1/ocr → status ok、10 行、text 含会议卡行 `2026-10-13 周二10:00~11:00`（全角～已归一半角）；tsc 通过。未验证边界（如实）：①经网关端到端被沙盒 other_tcp 策略拦截 .NET 出站（502）——路由注册与转发目标经网关日志证实，配置无误，需在用户机/docker 部署复验经网关一跳；②docker 镜像未实建（沙盒无 docker CLI，compose 仅 YAML 校验）。③邮件截图本地成卡缺头部适配（票 59 发现 1）→ 跟进票 60 已派；当前此类截图按设计回落视觉路径，不丢功能。

- 票60 独立验收（协调者, 7f67913）：单文件 +53/−2；对主 checkout 源码复跑机械抽取 harness 31/31（真实 OCR 文本成卡、when=2026-10-13 10:00、对照组逐字段一致、无头散文本不硬造），tsc 通过。至此邮件截图本地 OCR 成卡链路闭环（服务 59 + 适配 60），视觉仅作兜底。

- 票12 独立验收（协调者, 516d4ab）：diff 3 文件一致，字段校验+applyServerError 读码在位，tsc 通过；Playwright 活体证据（标签带 *、空 question 时 POST 数=0 且弹窗内字段红字、补填 201）与 API 基线/回归在 evidence-12/。票面外发现「详情编辑保存清空 Question」（数据丢失）已立跟进票 61 并派出。

- 票11 独立验收（协调者, 13b99c1）：迁移三件套齐全（diff 核对仅 JdText 一列 AlterColumn），活库实查 JdText 列型已为 text；闸门盘点表与四路径活体证据（44,181 四路径全过、200,001→400、迁移前后 6 行 md5 一致）在 evidence-11/；独立复跑 Jobs.Tests 16/16、tsc 通过。基线口径更正：列实为 varchar(40000)（非审计记的 20000），缺陷同一。票面外发现（SmartAdd 四列 JdSummary/Requirements/Benefits/CompanyDescription 仍 varchar(20000) 无校验、同型 500 隐患）立跟进票 62，排票 14 之后（Jobs 区域串行）。

- 票13 独立验收（协调者, 026e949）：实查缺失 28 键（非审计的 8 键）已全补三语 + statusDraft；协调者对主 checkout 复跑机械检查脚本 RESULT: PASS、tsc 通过（拉取后复验）。票面外发现 1（功能 bug：前端提交 FullMock、后端枚举 FullLoop，TryParse 失败静默回退 TopicDrill——选「完整模拟」实际按主题专项出题）立跟进票 63 已派；发现 2（题型/维度字符串直出）属另一契约层，未立票，留待总表备注。

## 单元测试立规（2026-10-21 用户立，总纲 skill 第七节，即刻生效）

- **每票关闭条件新增**：新写/改动的纯函数与规则逻辑必须带单测且含 boundary 类别（空/null、空集合、超长±1、特殊字符/Unicode、0 与负数、日期时区临界、文件类 0 字节/损坏/超限），缺测协调者不予验收。已验收票不回头补，存量由 ARC-08 批次兜底（先特征测试锁现状）。在跑票 14/61/63 已发补充指令；未派票据 46 张已批量加附则。
- **ARC-08 验收标准 = 第七节全文**：票 28（coverlet 接入 + 后端整体基线 + SmartParse ≥80%）、票 64（前端 Jest 接入 + 共享 helpers/状态逻辑首批 spec）、票 65（后端点名模块逐个 ≥80%：DeriveOutcome/adapter/转写选材/ApplyPartial/SpeechMetrics 等）、票 54（Handler 行为测试，原「前端不做」豁免已作废）。
- flaky 按缺陷立票；陈旧测试按票 55 先例。

### 覆盖率记账（每批更新，只许升不许降）

| 批次 | 后端整体（coverlet） | 关键模块 | 前端（Jest） | 备注 |
|---|---|---|---|---|
| 基线（票 28 产出，2026-10-22） | **8.14%**（4133/50795，分母含 EF 迁移代码行；分模块：Worker 38.21 / BuildingBlocks 23.58 / Jobs 10.14 / SharedContracts 9.41 / Interviews 6.75 / Knowledge 4.87 / Assessment 3.74 / Analytics 0.77） | SmartParseRules **97.84%**（905/925，≥80% 达成；改前 0%） | 无基建（票 64 接入） | 命令：scripts/run-coverage.sh（仓内）+ merge-coverage.py；工件 evidence-28/coverage/；全后端用例 450→573（Jobs 54→177） |

## 变更安全协议（2026-10-21 用户立，总纲 skill 第八节，即刻生效）

- 剩余各票（含在跑 14/61/63，已发补充指令）交付前必须补**影响面**与**不变项**两张清单（写进票面/报告）；验收范围扩为「本票功能 + 影响面全部邻居」，邻居逐项按第六节剧本回归、不变项逐条打勾才许关票。未派票据已批量加附则，后续派工简报与协调者验收清单同步执行此口径。
- 已验收的票不回头补；若后续发现某已关票的改动外溢破坏邻居，按新缺陷立票处理并在此注明来源票。
- 界面呈现类新需求先出效果图经确认再实现（约束新需求，不追溯在修票）。

- 票63 独立验收（协调者, 3144454）：前端提交值 FullMock→FullLoop + 后端 SessionModeParsing 纯函数（逐字枚举名匹配、数字/组合/空值全拒）+ 解析失败回 400 字段级 errors.Mode；新建 Services.Assessment.Tests 31 例（boundary 类别齐全：合法名/大小写/空-null/旧误值/0 与负数/特殊字符-Unicode/逗号组合/超长），协调者独立复跑 31/31；活体证据（基线 FullMock 静默落库 TopicDrill → 修复后全 400 零落库、四模式落库逐行一致、FullLoop 无 topic 可建而 TopicDrill 无 topic 被 400）在提交信息与 evidence-63/。存量 FullMock 行数 0。
- 票61 主体已推送（7011af9：前端回填 + 后端 Validator 与 Create 对齐，活体/Playwright 证据齐全，见 evidence-61/）；原实现者在补 §7 测试时被运行时重启打断，收尾（Knowledge.Tests 22 例提交+复跑+§8 清单）已派收尾 agent，完成后再正式关票。

## 第八节六关补充（偏差零容忍 + 对照表交付，即刻生效）

- 第 5 关：票面/定稿/审计已定口径零偏离，未批偏差按缺陷立票返工；真实冲突停工上报、给两个解法等用户拍板。在跑票 14、61 收尾已发补充指令；未派票据已批量加附则。
- 第 6 关：UI 类票交付必须附对照表（设计依据 vs 成果逐项打勾 + 偏差清单单列，非空须注原因与批准出处），无对照表不予验收；修复类票以审计条目期望行为 + 改前形态为设计依据、比例适用。协调者验收清单已同步加此两项。

## 真人化测试协议（2026-10-21 用户立，总纲第六节，即刻生效）

- **独立制**：功能/UI 类票的最终验收由独立 agent 执行（非该票实现者），只给设计依据（定稿/审计条目）+ 用户任务书，不给实现细节。实现者自验证据仍须交付，但不再是关票的唯一依据。协调者自查（diff/测试套件/契约核对）保留为第一道，独立真人化复验为第二道。
- **任务制**：验收用例写成用户任务（真实意图 + 完成标准，只许用界面完成），每任务附破坏配额（无焦点粘贴、多图连粘、超大/坏文件、重复提交、回退、多页签、空/满两极），逐步截图判读（布局/截断/文案/语言泄漏/按钮状态），DOM 断言仅作辅助。双角色（目标驱动 + 粗心急躁）按协议第 5 层执行。
- **覆盖留痕**：本文件每条验收结论自此注明覆盖范围（任务/破坏项/未测项），未测项明示，禁止「全测过」式全称表述。
- 不追溯已验收票；用户在已验收区域实测发现问题 → 新缺陷立票 + 回溯该票验收方式并在此注明。在跑票 14、61 已知会新验收安排。

- 票61 偏差清单裁决（协调者）：收尾报告单列的「验收项 2 无对象」属**票面预设错误、非实现偏差**——票 61 的 knowledge-dialog「编辑路径」系协调者拟票时误设，代码事实为该弹窗纯新建（全仓 grep + 读码证实，清单一在案），且票面本就要求「核查后决定」，实现者以调用方核查结论（全量 PUT 仅 saveDetailEdit 一个调用方）替代，符合票面意图；创建路径的 question 必填回归由票 12 活体+Playwright 证据覆盖。故：验收项 2 字面豁免、以核查结论结案，实现偏差清单归空。此裁决即批准出处。票面文件已同步修订存档。
- 票61 收尾提交 6a37e73 已核：仅 sln + Services.Knowledge.Tests 两文件（22 例，收尾 agent 独立复跑 22/22、全解构建 0 错误）；关票尚待独立真人化复验（新协议第二道）完成。
- **票61 关闭**（主体 7011af9 + §7 补测 6a37e73）：独立真人化复验（非实现者）通过——T1 建条目/T2 只改 Concept 后 question 逐字存活（API 辅助读回）/T3 空 question 建单被拦（弹窗红字、条目未产生）+ 后端旧式负载 PUT question="" 回 400 errors.Question；破坏配额 D1 连点保存/D2 回退前进/D4 清空可选字段/D5 空态均已执行（报告 evidence-61/independent-acceptance.md，覆盖范围与未测项已如实列明）。复验发现：S3×1 双页签静默覆盖 → 立票 70；S4×3（英文界面中文残留/必填双星号/空态文案位置不符）→ 立票 71。按 §6 回溯条款注明：本票验收方式已含独立真人化一道，S3 属新发现问题非验漏。
- 票66 关闭（协调者亲自实施并验收，02510fb）：两任实现者死于 daemon 重启后协调者接手——盘点沿死者 2 号笔记（notes.md）核实，处置为 MockSession 补全局软删过滤器（一处覆盖全部读写路径，无 schema 变更不带迁移；基线先存快照漂移另立票 67）。证据三件套：活体 before 10/10 复现 / after 13/13、Assessment.Tests 39/39（新 8 例 XML 逐例耗时真实）、§8 清单入票面关闭记录。复现现场另发现 AddQuestions 对全新会话恒 409（既有缺陷）→ 立票 69。
- 票14 已交付（7ddfc67）：协调者第一道核查进行中（diff/迁移民三件套已对报告核读，Jobs.Tests 独立复跑与真人化第二道待完成）；删问题端点 JobsWrite 口径与 DeleteCoverLetter 先例一致、知悉存档；票面外发现的 JobsDelete 角色缺口立票 68。
- **票14 关闭**（7ddfc67）：第一道协调者验收（迁移三件套、Jobs.Tests 独立复跑 37/37、§8 清单与对照表齐全）+ 第二道独立真人化复验通过——T1 收藏基本流/T2 列表↔详情双向同步/T3 跨浏览器存续（双变体+API 读回旁证）/T4 旧键自动导入/T5 问题流/T6 按投递隔离全过，破坏配额 D1/D2/D4/D5 过、D3 部分过（证据 evidence-14/independent-acceptance.md，60+ 截图；第一任验收 agent 被运行时重启打断后重派完成，见 R13）。复验发现定性：F1 列表/流程视图无收藏入口 = 改前既有覆盖不全、非本票回归 → 立票 73（需用户定性）；F2 超长无空格问题横向溢出 → 立票 74；F3 超限错误文案不指明上限 → 立票 75；F4 图标为心形非星形 = 协调者简报措辞问题、非缺陷，不立票。

## 治理对齐（2026-10-21 深夜）

修复工程即刻起正式以总纲 skill 当前版（v2.4，十节+第十一节自我迭代章程）为治理文件，取代此前零散下达的单条指令；已收编的单条（单测门槛、六关影响面/不变项/对照表、真人化验收、OCR 全文为源）与文件一致，后续以文件为准，冲突时以文件为准并回报父 agent。各票实质修复不受回流义务阻塞。

## skill 回流（按第十一节登记：问题 | 出处 | 建议处置）

| # | 流程性问题 | 出处 | 建议处置 |
|---|---|---|---|
| R1 | 沙盒 `dotnet test`/vstest 的 testhost TCP 通道被 egress 代理劫持，反复超时不可用 | 全工程（票 02 首现） | 补规矩（环境坑）：已定版为 xunit console runner 通道并全程执行；建议收编进项目档案已知坑 + 第七节基建注脚 |
| R2 | 新构建的测试输出缺 Microsoft.Extensions.* 程序集 → DB 门控测试 CanConnectAsync 抛 FileNotFound 被 catch 吞掉、用例空过**假绿**（验漏，协调者早期验收亦曾在此通道下跑数） | 票 58 发现、票 10 复证、票 61 收尾沿用定版规程 | 补规矩（强制项）：DB 门控套件验收必须「全套 dll 拷入 + XML 逐例耗时 + 落库核验」三件齐全，建议升入第六节验证剧本库「改数据/契约」类 |
| R3 | 实现者 agent 两次在运行时/daemon 重启中丢失（报告与检查点皆无），靠协调者从提交与 worktree 残留接管 | 票 03、票 61 | 改条款（强化第四节）：实现者每完成一逻辑段即把摘要落盘 evidence（现行报告多在收尾一次性产出）；本次靠 commit message 承载报告内容才未丢工，建议把「提交信息即报告」列为断线保险的明文要求 |
| R4 | 票面预设与代码事实不符：票 61 误设 knowledge-dialog 有编辑路径（实际纯新建），验收项一度无对象可执行 | 票 61（收尾 agent 按第 5 关上报、协调者裁决豁免） | 改条款：拟票时凡对调用方/路径/既有行为的断言，须先 grep/读码核实或在票面标注「预设待核」；已在本工程后续拟票（66）执行 |
| R5 | 沙盒 watchdog 从主 checkout 拉起旧二进制占用标准端口，活体验证出现假阴性 | 票 04、票 09 | 补规矩（环境坑）：活体验证一律实现者自起实例 + 自有端口，已全程执行；建议入项目档案已知坑 |
| R6 | 沙盒 other_tcp 策略拦截 .NET 进程出站，网关→下游一跳在沙盒内不可验（502 与配置无关） | 票 59 | 补规矩（环境坑）：网关类改动的端到端验收口径需含「用户机/docker 部署复验」一项，沙盒内只验配置与直连；建议入项目档案 |
| R7 | VM 更换中途丢失 pgdata 与全部进程，验收被打断 | 票 63 验收中途、票 09 曾全停恢复 | 补规矩：证据先行落盘（本次证据均先落盘未丢工）；与 R3 同源，建议并入第四节强化 |
| R8 | 外部能力接入的结构推断风险：以测试提示词的字段结构（DeepSeek body 字段）推断生产集成结构，险些立错票——生产实为全文转写单字段 | OCR 接入复核派单（协调者实证后澄清，真问题是解析格式覆盖→票 57） | 补规矩：接入/复核类任务先核生产契约的实际形状（读代码/实调），不以测试工件结构推断生产结构；建议入第六节「改解析/识别」剧本 |
| R9 | 审计基线数字与现状不符：AUDIT 记 JdText 列 varchar(20000)，实施时实为 40000（缺陷同一、口径漂移） | 票 11 | 只修实现（已在票 11 更正口径并完成）；建议：第 0 步复核对承重数字（列宽/上限/计数）做抽验，本工程后续批次沿用 |
| R10 | 前任实现者的 worktree 残留（未提交测试项目）无主，若非协调者巡检将随 worktree 清理丢失 | 票 61 收尾时发现 | 补规矩：agent 异常终止时协调者第一动作是巡检其 worktree 未提交残留与证据目录，再决定接管方式（本次已执行并成例） |
| R11 | agent 断线第三例：票 66 实现者在复现阶段被 daemon 重启打断（种子脚本崩后无检查点、零代码改动）；协调者按 R10 巡检 worktree 后整票重派 | 票 66 | 改条款（R3 强化已执行）：派工简报已加硬性「每逻辑段落盘 evidence + 提交信息即报告」；本次损失仅复现脚本，处置有效 |
| R12 | 脚本文件经写工具落盘后缩进被整体剥除（Python 文件二度损坏：票 59 app.py、票 66 repro66.py 均 IndentationError，靠重写恢复） | 票 59、票 66 | 补规矩：脚本类文件落盘后立即做语法校验（py_compile / node --check），或改用 exec heredoc 直写；建议入第四节交付纪律 |
| R13 | agent 断线第四例（运行时 restart drain）：票 14 独立验收 agent 在环境搭好、刚出 3 张截图时被杀，无任务结论；票 15 实现者同期被中断（worktree 零残留） | 票 14 验收、票 15 | 处置已执行：验收按全新任务重派（简报加「每完成一任务立即落盘」）、票 15 用 resume 恢复；改条款建议：真人化验收简报标配「逐任务落盘检查点」，不只实现者适用 |
| R14 | agent 断线第五例（daemon 重启三杀）：15/16 合并验收（环境期）、票 17（盘点+基线测试已落盘）、票 19（STATUS 初稿已落盘）同时被杀；因检查点纪律，17/19 的盘点成果零丢失可直接续作，验收无产出需重派 | 票 17、19、15/16 验收 | 处置已执行：17 按盘点文件续作重派、19 按 STATUS 续作重派、验收拆成 15/16 两单分别派（缩短单次暴露时长）；改条款建议：合并验收虽省搭建成本但单次时长过长、断线损失大，真人化验收以单票为派发单位 |
| R15 | 环境规程缺口：沙盒全新 Postgres 无各服务 schema 时，DB 集成测试大面积飘红（票 17 首跑 Assessment 15 红、Jobs 33 红），须先起对应服务跑完迁移才恢复——与假绿（R2）正好是一对（一个吞异常假绿、一个缺 schema 假红） | 票 17 | 补规矩（环境坑）：在新 DB/新 VM 上跑任何 DB 集成套件前，先逐个起对应服务完成迁移建 schema，再跑测试；建议入项目档案已知坑与第六节验证剧本前置条件 |
| R16 | daemon 重启已成常态频率（本工程累计六例）：单次派发时长越长、被杀概率越高；存活下来的全靠检查点落盘（票 22 的 Designer 与验证材料、票 15 验收报告均因此无损） | 票 21、22（第六例） | 改条款建议：派工简报统一加「小步提交/落盘」节奏要求——实现者每完成一逻辑段即 commit 到本地分支（未验收也可先本地提交，推送前再整理），把「完成才提交」改为「段段有提交」；验收类维持逐任务落盘 |
| R17 | 协调者本体断线死亡（2026-10-22 约 07:15 EDT，send 两次 agent not found、分支停滞）；状态文件完整、工无丢失，按第四节重派协调者接棒——本文件即协调者检查点，本次验证有效 | 主代理登记（票 80 节） | 只修实现（不立新规，检查点纪律已覆盖协调者） |
| R18 | 接棒日 VM 替换 ≥4 次 + 协调者直派子 agent 亦被中断（票 21 收尾、票 16 验收期间三度换 VM）：派工必须默认「执行者随时会死」设计——任务书自包含、检查点粒度 ≤30 分钟、多任接力靠落盘文件对账（票 21 三任接力零丢工实证） | 票 21、16（2026-10-22 接棒日） | 补规矩：派工简报标配自包含 + ≤30 分钟检查点；已在本工程后续派工执行 |
| R19 | 协调者并发过高反噬：2 核沙盒同时跑 6 路（含多路 ng build/服务自起），load 冲到 69、多路互相饿死并加速断线（票 21 二道环境段实录、票 25 两任皆死） | 票 21 二道、票 25（2026-10-22） | 改条款建议：本环境派工并发上限 ≈3 路重活，前端构建错峰；轻活（只读盘点）不占额。接棒协调者已按此执行（票 25 续作暂缓至空位） |
| R20 | 环境存在**并行 workflow runner** 与本工程共用 fix-program 目录：其票号语义与本账不一致（其「票82」=Log review 接线，与本账票 81 同缺陷），且会在 agent 死亡时把 worktree 存档为 wip 提交；本工程首派票 82 因此撞名 wt-82 未果 | 票 82、票 27（wip 存档先例）（2026-10-22） | 补规矩：本工程派工一律用唯一 worktree 名（带后缀）并在简报写明 evidence 目录全名；遇未知 wip 提交先读其报告对账，不默认是本工程产物 |

## 票 80（2026-10-22 用户实测新缺陷，主代理定位根因并登记；原登记编号 76 与平台票 76 撞号，2026-10-22 接棒点数时改编 80）
- 症状：Playbook 详情页 Randstad 条目——头部徽章显示 "Completed"，左侧导航却在 "In Progress" 组（Completed 组 0），同屏口径打架。
- 根因：分组走后端 outcome 单一事实源（entryResultOutcome(e.outcome)，无终局结果 → In Progress，符合定稿"宁漏不错"，无误）；错在 web/src/app/features/playbook/playbook-detail.component.ts 的 statusText（约 216–223 行）：e.result 为空且无 Pending 轮次时兜底 return statusDone("Completed")，无 result 条目被默认贴 Completed。
- 修法：statusText 与分组共用同一事实源（e.outcome 经 entryResultOutcome 映射决定文案；Pending/无 outcome 显示进行中口径）；删除默认 Completed 兜底；e.status==='Analyzed' 分支与 outcome 冲突时以 outcome 为准（或票内说明保留理由）。
- 验收：Randstad 同形态条目（无轮次、无 Result）重跑详情页，徽章与分组一致；有终局结果条目两处仍一致。本票同时登记为真人化验收永久回归任务。
- skill 回流 R17：协调者本体于 2026-10-22 约 07:15 EDT 断线死亡（send 两次 agent not found、分支停滞）；状态文件完整、工无丢失，主代理按第四节断点续跑重派协调者接棒。建议：协调者亦适用检查点纪律（本文件即其检查点，本次验证有效，只修实现不立新规）。
