# your-interview 修复工程交接书（2026-10-22）

交接方：Muse 助手（Forrest 的个人代理）→ 接管方：WorkBuddy
背景：助手侧运行平台不稳定（虚拟机数小时被整机替换一次，所有进程被杀），用户 Forrest 决定：修复工程即刻由 WorkBuddy 接管继续开发；助手稳定后回来对账接续。**接管期间 WorkBuddy 是唯一执行线**（助手侧的协调者与全部工人已于交接时停工，未推送的在飞成果已全部存档推送到远端分支，见第 4 节）。

本包结构：
- `HANDOVER.md`（本文）：总交接
- `01-governance/`：治理总纲 `forrest-dev-flow-SKILL.md`（全文，必须遵守）、项目档案 `project-archive-your-interview.md`、经验教训 `AGENTS-lessons.md`
- `02-state/`：状态总账 `FIX-STATUS.md`（2026-10-22 16:05 EDT 最新版）、审查正单 `AUDIT.md`（55 条）、全部票面 `tickets/`（94 个文件）、各票证据文本 `evidence-text/`（截图原图 53MB 未入包，在助手侧存档，需要时向用户索取）
- `03-specs/`：设计定稿（`replica/` 副本）——**唯一事实源**，注意该目录在仓库里是 gitignored 的本地目录，clone 仓库拿不到，必须以本包副本为准

---

## 1. 项目与分支纪律

- 仓库：https://github.com/Forrest-tech/your-interview
- 技术形态：Angular 17 前端（`web/`）+ .NET 8 微服务（Identity / Jobs / Interviews / Knowledge / Assessment / Analytics / AiGateway）+ YARP 网关 + Docker Compose；本地端口 Identity 5262、Jobs 5125、Interviews 5264、Knowledge 5265、Assessment 5266、Analytics 5267、AiGateway 5268、前端 4200
- **一切工作只提交到 `develop-new-features`，绝不直推 main**。main 当前 = `88b2607`，未动
- 远端 `develop-new-features` tip = `1502777`（2026-10-22 14:43 EDT 核实；接管前请先用 `git ls-remote` 复核——判远端状态只认新鲜 ls-remote，不认本地 tracking ref）
- **合并 main 的双门槛（缺一不可）**：(1) 全部测试通过且有可核查证据；(2) 用户 Forrest 亲口点头。在此之前无论修到多完都不许合并

## 2. 任务：全系统审查修复工程

- 起源：2026-10-21 对系统做全量审查，得到 55 条问题（S1×3 / S2×20 / S3×25 / S4×7），全文在 `02-state/AUDIT.md`
- 拆票：正单票 01–54（覆盖 55 条）+ 衍生/平台票 55–87，共约 87 张；票面全量在 `02-state/tickets/`（注意 80/81/82/83/84 存在同号不同 slug 的文件并存，认票必须带 slug）
- 状态总账：`02-state/FIX-STATUS.md`——每票状态、提交哈希、证据路径、验收注记、治理裁决、skill 回流 R1–R19 全在里面，**接管前必读**。注意其文件头部「状态总览（2026-10-22 接棒点数）」一段仍是旧数（tip 0440255/已关 20），以各票明细表 + 本文第 3 节为准；请接管后顺手把总览对齐刷新，并继续用同一文件结构记账（助手回来时按此对账）
- 总进度（2026-10-22 16:05 EDT）：约 87 张已关约 35 张

## 3. 已关清单（明细见 FIX-STATUS）

- 正单已关 23 张：01、02、03（含票 20）、04、05、06、07、08、09、10、11、12、13、14、15、17、18、19、22、23、24
  - 例外说明：**票 21 只是第一道（协调者）验收通过，第二道独立真人化验收未完成**（两任验收者死于平台重启，按 R19 暂缓）——不算完全关死，见第 4 节
  - 票 28 只关了 ARC-08 的解析器面（SmartParse 特征测试 + coverlet 基线），同族的 54/64/65 未做，见第 5 节
- 衍生/平台已关 12 张：55、56、57、58、59、60、61、62、63、66、67（只读盘点）、69

## 4. 在飞成果（重点：全部已推送到远端分支，一行不丢）

| 分支（远端） | 票 | 状态与下一步 |
|---|---|---|
| `t81-work` @ `4bd0aa8` | 票 81（81-logreview-no-activity-write） | 修复已提交：Log review 成功回调补 `recordReviewActivity()`，热力图据实着色。**未验收**——需走协调者验收（复跑 Knowledge.Tests + 活体/真人化复验热力图着色）后合入 develop-new-features 并关票 |
| `t26-work` @ `b1f5e89` | 票 26（26-req-05-resume-text-fallback） | 实现提交 `80f072c`（后端文本回落 + Tracker text 形态呈现）+ 交接存档提交 `b1f5e89`（单测文件 `EffectiveResumeFallbackTests.cs` **已写未跑**）。下一步：跑该单测 + Jobs.Tests 回归 + 验收 |
| `t27-work` @ `1cc1bd6` | 票 27（27-req-08-weakest-guard） | 实现进行中的 WIP 存档（api.models / analytics 组件 / dashboard / 新增 radar.helpers.ts，共 5 文件）。**未完成、未验收**，按票面继续做完 |
| `t82-work` @ `8198562` | 票 82（82-review-activity-write-missing） | 一行 WIP（techstack.component.ts）。与票 81 同属「复习活动写入/热力图」一系——**先对账两票关系再动手**：若 81 的修法已覆盖 82 的诉求，82 以证据关票即可，不要重复改同一处 |

其余在飞事项（无代码在分支上，从检查点续作）：
- **票 25**（COD-15 Assessment 写操作改挂写权限）：盘点已完成（结论：改挂 MockAnswer、无种子/迁移变更；盘点全文在 evidence-25/00，本包 evidence-text 内），三任实现者均死于平台重启、工无损。待从复现步正式开修。盘点时另发现 #14/#34 问题 → 已立票 86
- **票 16**（COD-10c）：实现 `cb37c52` 已在 develop-new-features 线内、第一道过；第二道独立真人化复验已完成并挖出 F1–F4 → 立票 81/82/83/84。待 81–84 全部关闭后，复验 T2/T5/D4b 三项再关票 16
- **票 21**（COD-08）：实现 `b1a3d7a` 已在远端 tip 祖先链、第一道过；第二道独立真人化验收续作检查点在 evidence-21/independent-acceptance.md（本包内），空位时续派验收者从 T1 接续

## 5. 排队未开工（约 44 张，票面都在包内）

- 正单：29、30、31、32、33、34、35、36、37、38、39、40、41、42、43、44、45、46、47、48、49、50、51、52、53、54（REQ-09/10/11/15/16/17/18、FUN-07/08/10、COD-17/20/21、ARC-01/02/03/10/11/12/13/15/16/17/18、REQ-12/14 及 ARC-08 余下部分）
- ARC-08 余下：票 54（Handler 行为测试）、票 64（前端 Jest 接入 + 共享 helpers 首批 spec）、票 65（后端点名模块逐个补测到 ≥80%：DeriveOutcome / adapter / 转写选材 / ApplyPartial / SpeechMetrics 等）
- 衍生：64、65、70（双页签静默覆盖）、71（界面文案）、72（Mock next-question 接线核查）、74、75、76（超限请求体 500 应 413）、77、78、79、80（见下）、83、84、85（InvitePasteParser 文化区问题）、86、87（Assessment 快照对齐——**注意 evidence-67 §4 的警告：87 落地前，其余 Assessment 迁移票禁止 apply 自动 scaffold 的迁移，必须逐行剔 DropTable**）
- **票 80**（80-detail-badge-outcome-consistency，用户本人实测发现，根因已定位）：Playbook 详情页 Randstad 条目（无轮次、无 Result）头部徽章显示 "Completed"，左侧导航却在 "In Progress" 组。分组是对的（走后端 outcome 单一事实源 `entryResultOutcome(e.outcome)`，合定稿"宁漏不错"）；错在 `web/src/app/features/playbook/playbook-detail.component.ts` 的 `statusText`（约 216–223 行）：result 为空且无 Pending 轮次时兜底 return "Completed"。修法：statusText 与分组共用 outcome 单一事实源、删默认 Completed 兜底。此票同时是最终验收的必测回归场景
- **不许派工的两张（等用户拍板）**：票 68（JobsDelete 权限与默认角色缺口——口径选择）、票 73（Tracker 列表/流程视图收藏入口——属呈现新需求，需先出效果图给用户确认）。终报时 flag 给用户即可

## 6. 治理规则摘要（全文见 `01-governance/forrest-dev-flow-SKILL.md`，冲突以全文为准）

1. **定稿纪律**：`03-specs/`（replica 定稿）是唯一事实源；实现 ≠ 定稿 = 实现错，不许反过来改定稿迁就实现；设计变更先落定稿再实装；交付前做 requirement-by-requirement 对照，偏差清单为空才算交付
2. **每票流程**：复现失败 → 修复 → 复现通过 + 回归证据，才许关票
3. **影响面 + 不变项**：每票交付前列两张清单随票走；验收范围 = 本票功能 + 影响面全部邻居逐项回归、不变项逐条打勾
4. **单元测试（第七节）**：新写/改动的纯逻辑必须带单测且含 boundary 类别（空/null、空集合、超长±1、特殊字符/Unicode、0 与负数、日期时区临界、文件 0 字节/损坏/超限）；改无覆盖的旧逻辑先补特征测试锁现状；关键模块行覆盖 ≥80%（coverlet），整体基线只许升不许降——当前后端整体基线 **8.14%**、SmartParse **97.84%**（基线产出方法与数字在 FIX-STATUS 覆盖率记账节）；flaky 按缺陷立票，禁止放宽断言凑绿
5. **真人化验收（第六节）**：功能/UI 类票最终验收由**非实现者**独立执行：只用界面完成真实用户任务、真实数据连续性、逐步截图判读、带破坏配额（无焦点粘贴、多图连粘、超大/坏文件、重复提交、回退、多页签、空/满两极）；未测项必须明示，禁止"全测过"式表述
6. **EF 迁移**：必须带 Designer.cs + ModelSnapshot 同步更新，禁止手写无 Designer 的迁移
7. **数据规则**：模块共用同一底层数据（同源不同视图）；打通只做 additive，永不隐藏/替换既有 UI 与功能；数据不丢失
8. **变更安全**：动手前复述需求对齐；设计稿/截图即需求，与成果偏差零容忍——撞到与定稿的真实冲突，停工上报、给两个解法等用户拍板，不要自行改口径

## 7. 环境与坑（助手侧经验，环境不同按需取用）

- 测试通道：助手侧 `dotnet test` 的 vstest↔testhost TCP 被代理污染不可用，等价通道 = xunit console runner（`DOTNET_ROLL_FORWARD=LatestMajor` + `~/.nuget/packages/xunit.runner.console/2.9.2/tools/net6.0/xunit.console.dll`）
- **DB 门控测试防假绿三件套（R2，血泪）**：新构建的测试输出缺 Microsoft.Extensions.* 程序集时，DB 门控用例会抛 FileNotFound 被 catch 吞掉、~0.001s 空过假绿。验收必须：全套 dll 拷入测试输出 + 以 -xml 核对逐例耗时真实 + 落库核验，三件齐全
- 新 DB/新环境跑 DB 集成测试前，先逐个起对应服务跑完迁移建 schema（R15），否则大面积假红
- curl 查 localhost 要绕开 http_proxy 环境变量，否则全 000 假阴性
- 前端干净构建：`npm ci` 按仓库 lock 文件，不要混装 @material/tokens 新版（会打断 Sass）；前端改 angular.json assets/package.json 需重启 ng serve + npm install
- 后端改哪个服务只编哪个（`docker compose up -d --build <service>`，且 --no-cache），不要全量重编
- .NET SDK 8.0.425
- **凭据不经聊天传递**：GitHub PAT、本地管理员密码（admin@your-interview.local）、DeepSeek OCR key 都由用户本人提供/配置，接管方直接向用户索取，不要向助手索要（助手也不会给出）。OCR 平台方向：全系统共享一条 OCR 管线，定稿在 `03-specs/platform-ocr-spec.md`（PaddleOCR 主 + DeepSeek 视觉兜底），票 59/60 已落地

## 8. 收官标准（全部票关完后，不许直接报完成）

正式验收阶段，产出 `ACCEPTANCE.md` 交用户：
1. 55 条逐条反跑复现，出判定表（已修属实/未修/口径差异逐条注明）
2. 独立真人化验收（非实现者）+ 覆盖地图（未测格明示）；票 80 场景必测
3. 全量测试结果 + coverlet 数字：后端整体基线对比 8.14% 只升不降，关键模块逐个列明是否 ≥80%
4. 合并预检清单逐项打勾；FIX-STATUS 总账先全量对齐刷新
5. 结论交用户 Forrest，由他决定是否合并 main（双门槛见第 1 节）

## 9. 汇报纪律（用户明令）

- 只报最终核实过的成果，不报半成品、不报过程流水；未全部修完+验收通过前，不要向用户宣称完成
- 票 68、73 两项在终报时 flag 给用户拍板

—— 交接完毕。助手稳定后会回来：以 GitHub 远端状态 + FIX-STATUS.md 对账接续。祝顺利。
