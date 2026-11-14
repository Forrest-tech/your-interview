# 票 311 规格：条目跨轮次邮件时间线（SPEC ONLY，未实装）

> 票号核实：基底 `c1624f69`（develop-new-features 当时 tip）上已落地台账最大票号为 306
> （`docs/fix-program/FIX-STATUS.md`，末条为票 306；307–310 按主线简报在途未落地，仓内无其条目）；
> 故本票取下一个空号 **311**。本规格任务**不写台账条目**，台账条目由实装提交时一并写。
>
> 基底：`c1624f69`；只读工作树 `~/workspace/wt-t311-timeline-spec`（完工时 `git status` 干净）。
> 约束（用户原话 + 后续补充，不可违背）：仅邮件；复用现有邮件卡片；不动导入、匹配、轮次指派、分类；
> 不碰 Mac、不碰 4200 端口、不跑前端（本规格只写文档）。

---

## 0. 用户需求（原文口径）

- 「通过时间线，可以把所有的邮件串起来，不同轮次的面试邀请，这样整个的周期就很清楚明了。」
  即：一个条目的完整面试周期（各轮次邀请 + 相关邮件）在一个按时间顺序的视图里一次看清，
  不再散落在逐轮切换的列表里。
- **已定结构（用户 09:59 补充决定，不再是开放问题）：时间线上的一个节点就是一个轮次**
  （Round 1 节点、Round 2 节点……），节点里放这个轮次相关的所有邮件；
  展开节点能看到里面的每封邮件，邮件详情仍复用现有邮件卡片。

---

## 1. 现状核实（每条均有代码出处）

### 1.1 轮次模型

- `InterviewRound`（`src/Services.Interviews/Domain/InterviewAggregates.cs:1045` 起）：
  `InterviewEntryId`、`Order`（创建序，从 1 开始，`AddRound = max+1`）、
  `Stage`（Screen / Technical / SystemDesign / Behavioral / Final）、
  `ScheduledDate`（`DateOnly?`，可空）、`Interviewers`、`Format`（Phone | Video | Onsite）、
  `Location`、`Outcome`、`Notes`、`Feedback`、`MeetingLink`、`ScheduledTime`（HH:mm）。
- **展示编号 ≠ 存储 Order**（票 30 REQ-10，`web/src/app/features/playbook/redesign/v2.helpers.ts:281` 起
  `sortRoundsByDate` + `:286` `roundDisplayNo`）：有日期轮次按 `scheduledDate` 升序、同日按 `Order`；
  无日期轮次排在有日期之后、按 `Order`。补录的早日期轮次因此拿到小编号。
  全仓 `roundNo()`（`materials-list.component.ts:495`、`comm-log.component.ts:179`、
  `intake-panel.component.ts:565`）一律调 `roundDisplayNo`，与详情页 stepper 同口径。
  **时间线节点编号必须用展示编号，不得用存储 Order**（否则补录轮次的节点号与轮次轨、详情页不一致）。
- 轮次另有 `EmailsJson`（`InterviewAggregates.cs` 内 `[{subject, from, date, snippet}]`，
  写入口 `InterviewPlaybookHandlers.cs:282` 的 `SetRoundEmails`）：这是**手工维护的关联邮件清单**，
  邮箱导入不写它。**时间线取数只认资产（assets），不得混用 `EmailsJson`**（两个总体不一致）。

### 1.2 邮件今天怎么挂到轮次上（归属规则的核心前提）

- 导入时写资产：`MailboxRefreshHandler.cs:719` 调 `ResolveRound(entry, message.ReceivedAt)`，
  得 `(roundId, phase)` 后由 `AttachAsset(..., roundId, phase, meta)`（`:747-749`）落库。
- `ResolveRound`（`MailboxRefreshHandler.cs:791-808`，机制原文）：
  只看**有日期**的轮次（`Where(r => r.ScheduledDate is not null)`，按日期升序），
  取第一个 `ScheduledDate >= 收件日` 的轮次，否则取最晚轮次；没有任何有日期轮次时返回 `(null, Pre)`。
  Phase：收件日 < 轮次日 → Pre；收件日 > 轮次日 → Post；同日 → Pre
  （前端 `derivePhase`，`v2.helpers.ts:185`，展示时按「材料日期 vs 归属轮次日期」重算，存量写错的 phase 在展示时纠正）。
- **此前诊断「全部导入指派到 Round 1」的定性修正**：这不是硬编码，而是上述机制在
  「仅 Round 1 有日期」的数据下的必然结果——只要 Round 2 没有 `ScheduledDate`，
  它就进不了 `ResolveRound` 的候选集，所有邮件只能落到 Round 1（或 null）。
  **推论（规格第 3 节的立论基础）**：存储的 `roundId` 是「收件日贴近有日期轮次」的启发式，
  在单日期轮次的常见数据下会把所有邮件压进一个轮次，**不能单独作为时间线分组的依据**；
  否则 Round 2 节点永远是空的，用户要看的「Round 2 邮件」恰恰不在里面。

### 1.3 邮件元数据（导入时写进 `metaJson`，`MailboxRefreshHandler.cs:720-739`）

- `channel="email"`、`category`（`ClassifyEmail`，见票 310）、`from`、`fromEmail`、`subject`（原文）、
  `date`（收件日 UTC `yyyy-MM-dd`，**只有日粒度，没有时刻**）、`threadKey`、
  `mailboxImport`、`mailboxNew`、`mailboxProvider`、`mailboxMessageId`、`dedupeKey`；
  可选：`interviewAt`（`:736`）、`meetingLink`（`:738`，首个 Teams/Zoom/Meet 链接）、
  `mailboxThreadId`（`:739`，服务商侧 thread id）、`rawPath`（`:752`）。
- `ExtractInterviewWhen`（`MailboxMatching.cs:170` 起）：三路正则（中文年月日 / ISO / 英文月名），
  全部过日历合法性校验；输出 `"yyyy-MM-dd HH:mm"`；无时间或无日期 → null。
  **关键语义：`Regex.Match` 只取正文里第一个匹配**——周报类邮件的 `interviewAt` 是它列出的第一场会议，
  不是「这封邮件属于哪轮」的可靠证据（见第 3 节规则 3 的 caveat）。
- 后端导入**不写** `format`：`parsed2` 里 `m.format` 行（`materials-list.component.ts:125/219`）
  只对带 `format` 的资产（手工/粘贴类）出现；邮箱导入邮件的 `parsed2` 只有
  `interviewAt`（When）、`interviewersOf`（轮次面试官，见 `:863`）、`meetingLink` 三类行。
  时间线节点内复用卡片时，`format` 行有则显示、无则不占位，与今天一致，不补推断。

### 1.4 今天的邮件分组与渲染（时间线要复用的全部现成件）

- 分组键 `threadKey`：`MailboxMatching.NormalizeThreadKey`（`MailboxMatching.cs:122-132`，
  前端同款 `normalizeThreadSubject`，`v2.helpers.ts:237`）：压缩空白、最多剥 6 层
  `Re:/Fwd:/Fw:/回复:/转发:/答复:` 前缀、转小写。**它不剥轮次标记**：
  `"1st round of ..."` 与 `"2nd round of ..."` 归一化后仍是两个不同的 key。
- 显示短标题 `shortEmailTitle`（`v2.helpers.ts:386`，票 210）：去 thread 计数后缀、
  去 `Nst/nd/rd/th round of ` 前缀、去 ` position`（仅 `position at` 前）、去末尾句点、
  首个 ` for ` 转 ` — `。**它是纯展示函数**：两轮邀请在卡片上看起来标题相同
  （轮次序数被抹掉），但分组 key 仍不同——显示已统一、分组未统一，这正是时间线要补的另一半。
- `displayGroups`（`materials-list.component.ts:901-940`，票 165/236/306）：
  `assets + allRoundsMail` 按 id 去重并集 → 滤掉被任一邮件 `screenshotAssetId` 引用的资产 →
  邮件按 `th:` + `meta.threadKey`（无则主题归一化兜底）分组，非邮件单条 →
  多封组内按 `displayDate` 正序。截图去重是展示层既有行为，时间线内嵌卡片时自动继承，不另立规则。
- 渲染（`materials-list.component.ts:75-135` thread 容器、`~180-230` 单卡）：
  多封组 = thread 容器（容器头：归一化主题 + 封数 + 最新一封发件人/收件日 + 最新封有链接时附 Join 芯片；
  组内各封按收件日正序，最新一封默认全展开，旧封收起为一行栏、点栏内联展开/收起；
  **不做 Expand all / Collapse all**，票 236 已定）；
  单封组 = 旧 email-card；展开体内依次是 `eo-sender`（发件人 + Received + 分类 chip，票 307 在修其重复头）、
  `eo-title`、`parsed2`、正文（`ensureEmailBody` 取 transcript → `segmentEmailBody` 时间线/全文，
  票 309 将来在此处换标准 HTML 渲染）。
- 票 306 的并集输入（时间线总体可直接复用的模式）：
  `round-card.component.ts:339-343` `allRoundsMail` = 本条目 `channel='email'` 且 `roundId ≠ 本轮` 的资产
  （含未分轮次邮件）；`comm-log.component.ts:186` `unlinkedCommAssets`（`roundId` 为空 OR `channel='email'`）。
  **时间线总体 = 上述并集的同一总体**：条目全部 `channel='email'` 资产
  （= `roundAssets ∪ allRoundsMail`），只是分组方式从 threadKey 换成轮次归属。
  非邮件资产（笔记、录音、指导）不在时间线内。

### 1.5 入口页结构（决定 placement 的关键事实）

- `playbook-detail.component.html:284`：详情页一次只渲染**一个选中轮**的详情卡
  （`<app-round-card [entry]="e" [round]="sr" [displayNo]="roundNo(sr)">`），
  上方是轮次选择轨道（票 164 横向可选单元，点选切换下方详情卡；票 233 后轨道行上移至卡片头行）。
- `round-card` 内的页签是**按轮**的（`round-card.component.ts:29` `RoundTab`、
  `:351-357`：prep / record / qa / materials / comm；Materials 页签 `:169` 内嵌
  `materials-list [assets]="roundAssets()" [allRoundsMail]="allRoundsMail()"`，`:181`）。
- `materials-list` 全仓仅三处挂载：round-card Materials 页签、comm-log 两分支
  （`:110` 已关联、`：115` 未关联）。**今天不存在条目级邮件列表**——时间线是第一个条目级视图，
  因此它不能塞进 `round-card`（那是按选中轮的），必须挂在 `playbook-detail` 这一层（见第 6 节决策 1）。

---

## 2. 已定结构：节点 = 轮次（用户 09:59 决定）

- 时间线 = 按轮次排布的节点列。**一个节点 = 一个轮次**，节点头即轮次身份，节点体即该轮次的邮件。
- 节点顺序：`sortRoundsByDate` 顺序（有日期升序 → 无日期按 `Order` 在后），与轮次轨、详情页 stepper 同序。
- 节点头（沿用轮次轨既有语言，`intake-panel.component.ts:115` 同款）：
  `Round N · Stage · ScheduledDate（无日期则不写日期）· M 封邮件 · 邮件日期跨度（首封–末封 displayDate）`。
  其中 N = 展示编号（`roundDisplayNo`），M = 按第 3 节归属到本节点的邮件数，
  日期跨度取节点内邮件的 `displayDate` 最小/最大值；节点内无邮件时不写跨度、写无邮件空态行。
- **所有轮次都有节点，包括零邮件轮次**（否则周期不完整——某轮在时间线上消失比空节点更误导）。
  空节点体只一行空态（复用 `pb.v2.m.empty` 口径文案，不新编语义）。
- **未指派节点**：仅当存在无法归属的邮件时才渲染，排在全部轮次节点之后（见第 3 节规则 4），
  节点头标注「未指派」（三语新键，见第 7 节），不冒充某一轮。
- 节点体内邮件顺序：`displayDate` 正序（与 `displayGroups` 组内同口径）；**同日邮件保持后端资产原序**
  （稳定排序——`meta.date` 只有日粒度， intra-day 真实收件时刻今天没有字段可排，
  不得编造时刻；此为已知局限，写进走查诚实缺口）。
- 节点与 thread 的关系（必须写清的派生结论）：**轮次归属优先于 thread 分组**。
  同一服务商 thread 的邮件若被归属到不同轮次，在时间线里就分属不同节点、
  各节点内只按本节点邮件做 thread 容器（即 `displayGroups` 的分组逻辑在节点邮件子集上重跑一遍）。
  跨轮长 thread 在时间线里会被拆开——这是「节点=轮次」的直接代价，换来的是每轮邮件一目了然；
  完整 thread 仍可在原 Materials/Comm 页签里看到（时间线不删改任何现有视图）。

---

## 3. 轮次归属规则（规格核心；展示时分组，不写库）

**性质先行**：归属是纯前端展示时计算（与 `displayGroups` 同类的 computed），
输入 = 条目全部 `channel='email'` 资产 + 条目轮次列表；输出 = 每封邮件的节点。
它**不改写 `asset.roundId`、不改动导入、不改动匹配、不改动分类**；
轮次日期被编辑、新邮件被导入后，下次加载自动重算，无迁移、无回填。

### 3.1 优先级（按序命中，先命中者胜；未命中则落入下一条）

| 优先级 | 规则 | 输入 | 命中结果 |
|---|---|---|---|
| 1 | **主题轮次标记** | `meta.subject`（原文主题，非短标题） | 标记的轮次节点 |
| 2 | **存储的 `roundId`** | `asset.roundId`（非空且对应轮次存在） | 该轮次节点 |
| 3 | **解析面试时间贴近度** | `meta.interviewAt` 的日期部 + 各轮 `ScheduledDate` | 最近日期轮次节点 |
| 4 | **未指派** | 以上皆无 | 未指派节点 |

### 3.2 规则 1：主题轮次标记（必须优先于规则 2，理由见 3.5）

- 先剥回复/转发前缀：与 `NormalizeThreadKey` 同款正则、最多 6 层
  （`^(re|fwd|fw|回复|转发|答复)\s*[:：]\s*`，循环剥离），得到待检主题。
  不剥这一步则 `Re: 2nd round of ...` 会漏检——回复/转发链里的轮次标记同样有效。
- 再按序匹配（第一个命中的序数胜出）：
  1. `^\d+(st|nd|rd|th)\s+round\s+of\s+`（前缀，大小写不敏感）——
     即 `shortEmailTitle` 今天为展示而剥掉的那个模式，是真实主题里已证实存在的写法
     （如 `1st round of technical interview — ...`）。
  2. `round\s+(\d+)`（任意位置，大小写不敏感）——覆盖 `Round 2`、`round 2 interview`、
     `Interview Round 2` 等写法。
  3. `第(\d+)轮`（任意位置）——与仓内中文模式（回复/转发/安排面试/邀请您参加）同款双语口径。
- 序数映射到**展示编号**（`roundDisplayNo`），不是存储 `Order`：
  招聘方说的「第 2 轮」是时间顺序上的第 2 场面试，与补录轮次导致编号重排后的展示编号一致；
  无日期条目下展示编号退化为 `Order` 顺序，映射依然成立。
- 序数无对应轮次（如主题写 `3rd round` 但条目只有 2 轮）→ **不建 phantom 节点、不丢邮件**，
  落入规则 2。序数 < 1 同理。
- 规则 1 只读 `meta.subject` 原文，不读 `shortEmailTitle` 的输出（短标题已把序数剥掉，读它就读不到了）。

### 3.3 规则 2：存储的 `roundId`

- `asset.roundId` 非空**且**在条目轮次列表里找得到 → 该轮次节点。
- `roundId` 指向的轮次在列表里不存在（防御性分支：轮次曾被删或数据漂移）→ 视同空，落入规则 3。
- 规则 2 是「系统自己的指派」，对无标记邮件是最好的可用信号（它已编码收件日与有日期轮次的贴近关系）。

### 3.4 规则 3：解析面试时间贴近度

- 取 `meta.interviewAt` 的日期部（`yyyy-MM-dd`），在**有日期**轮次里取
  `|面试日期 − ScheduledDate|` 天数最小者；并列 → 取 `ScheduledDate` 更早者，再并列 → 展示编号更小者。
- 无 `interviewAt`，或条目没有任何有日期轮次 → 跳过本规则，落入规则 4。
- **Caveat（必须向用户明说）**：`interviewAt` 是正文第一个日期+时间（见 1.3），
  周报类邮件取到的是它列出的第一场会议——用本规则归属的周报可能只是「与该轮同周」而非「属于该轮」。
  接受此近似的理由：节点语义是「相关邮件」而非「系统指派」；周报留在时间线里可见，
  比把它扔进未指派桶更有用；且规则 1/2 已先行，规则 3 只处理两者皆无的剩余邮件，影响面有限。

### 3.5 为什么规则 1 必须优先于规则 2（本节最关键的判断）

- 反例（Corpay 实证结构）：Round 1 有日期 2026-10-07，Round 2 无日期。
  按 1.2 的机制，Round 2 邀请邮件（收件日在 Round 1 日期前后皆可能）的存储 `roundId` 只能是
  Round 1——因为 Round 2 根本进不了候选集。若规则 2 优先，Round 2 节点永远为空，
  时间线与用户今天「Round 2 页签看不到邮件」的困境一模一样，等于没修。
- 招聘方写在主题里的序数（`2nd round of ...`）是**对轮次归属的显式陈述**，
  可靠性高于收件日启发式；只有它能把单日期轮次压扁的数据重新撑开成多轮。
- 反向风险可控：主题序数误标（如跟进帖沿用旧主题）时，邮件进错节点——
  但邮件本身仍在时间线里可见（节点体就是卡片），且 Materials/Comm 原视图不受影响；
  规则 1 的误标代价是「放错节点」，规则 2 优先的代价是「整轮消失」，后者更严重。

### 3.6 规则 4：未指派节点

- 命中条件：无主题标记、无可用 `roundId`、无 `interviewAt` 或无有日期轮次。
- 渲染：单独一个节点，排在全部轮次节点之后，头标注「未指派」+ 封数 + 日期跨度；
  体内邮件按 `displayDate` 正序（同 2 节），每封仍是完整邮件卡片。
- **不存在未指派邮件时不渲染该节点**（不留永久空桶）。
- 未指派 ≠ 垃圾桶：它的存在本身就是信号——提醒用户这些邮件缺标记、缺日期、缺指派，
  给轮次补日期或给邮件补标记后，下次加载会自动归入对应轮次节点（规则是纯计算，无需手动迁移）。

### 3.7  worked example（Corpay 结构，验证规则排序）

条目：Round 1（Technical，有日期 2026-10-07）+ Round 2（Technical，无日期）。
邮件（存储 `roundId` 均为 Round 1，符合 1.2 机制）：

| 邮件 | 主题 | 存储 roundId | interviewAt | 归属（规则） | 说明 |
|---|---|---|---|---|---|
| Round 1 邀请 | `1st round of technical interview — ...` | Round 1 | 2026-10-07 13:00 | Round 1 节点（规则 1，规则 2 同意） | 两规则一致 |
| Round 2 邀请 | `2nd round of technical interview — ...` | Round 1 | 2026-10-13 13:00 | **Round 2 节点（规则 1 覆盖规则 2）** | 本规格要修的核心情形 |
| Round 2 跟进 | `Following up on your interview`（无序数） | Round 1 | 无 | Round 1 节点（规则 2） | 无标记时沿用系统指派；若用户认为它属 Round 2，补 Round 2 日期后重算仍可能不变——此为规则 2 的已知局限，见 3.4 caveat 的同类说明 |
| Otter 周报 | `Your upcoming meetings`（无序数） | Round 1 | 2026-10-07 周内某场 | Round 1 节点（规则 2） | 规则 2 先于规则 3，周报不走到贴近度 |
| 手工粘贴邮件 | 无主题相关标记 | null | 无 | 未指派节点（规则 4） | 三无邮件的归宿 |

---

## 4. 节点内邮件排布与交互（已定部分）

- 邮件详情 = 现有邮件卡片（`materials-list` 的 thread 容器 / 单卡），**不另写第二套邮件渲染**。
  票 307 的展开头去重、票 309 的标准 HTML 正文，落到卡片上即自动落到时间线节点内；
  时间线本身不分支「HTML 有/无」（卡片自己处理兜底：有 HTML 走 iframe、无则纯文本时间线）。
- 节点展开/收起：点节点头切换；节点体内邮件卡片的展开/收起沿用卡片自身行为
  （thread 容器：最新一封默认展开；单卡：点行展开）。
- **不做 Expand all / Collapse all**（沿用票 236 的决定：整页全展开会失去概览意义）。
- **v1 不做分类筛选**（category chip 已在每封卡片上可见；节点结构本身就是按轮筛选；
  分类筛选可在不改结构的前提下后加，不属本票）。
- 多实例注意（实现时须确认，非新增需求）：时间线内每节点各嵌一份 `materials-list`，
  票 305 的轮询/增量重载随组件实例走。实现时确认多实例轮询不互相干扰；
  自然缓解：节点体只在展开时渲染（`@if`），收起节点不挂载列表、不轮询。
  若实测仍有干扰，退化方案是时间线内列表共用 Materials 页签的刷新信号——实现时定，不改本规格结构。

---

## 5. 范围边界（显式）

- **仅邮件**：总体 = 条目全部 `channel='email'` 资产（= 票 306 并集总体），
  笔记/录音/指导等非邮件资产不进时间线；`round.EmailsJson` 手工清单不混入（见 1.1）。
- **不动**：导入流程、匹配规则、存储的 `roundId`/phase 指派、分类器、Materials/Comm 原视图、
  轮次本身的增删改。归属是展示时计算（第 3 节），不产生写操作。
- **不新开后端接口、不做迁移**：入口 DTO 已含 `assets`（带 `metaJson`/`roundId`/`phase`）与
  `rounds`（带 `scheduledDate`/`stage`/`order`）——时间线是纯前端组件 +
  纯函数分组计算（`displayGroups` 同款形态）。实现中若发现 DTO 缺字段（如需 intra-day 排序时刻），
  那是实现发现、另行评估，不得在本票内顺手扩 DTO 口径。
- **时序外事件是 phase 2，不属本票**：tracker 状态变更、手工备忘、非邮件的轮次结果/反馈、
  面试官评价等，不画成时间线节点；节点头只写轮次元数据（Stage/日期），不写事件。
  若用户日后要「完整周期事件轴」，另立一票（那时才需要事件源、事件类型、与邮件节点的混排规则）。
- **不做**：附件下载入口（票 309 已定不在 309 内，时间线亦不加）、新邮件导入入口、
  轮次编辑入口（节点头不挂编辑控件，编辑仍走轮次卡）、跨条目时间线（本票只做单条目内）。

---

## 6. 开放决策（4 项，每项带推荐；用户定夺后方可实装）

### 决策 1：放哪里（placement）——推荐：详情页条目级区域，轮次轨下方、选中轮详情卡上方

- 推荐理由：时间线是条目级视图，只能挂在 `playbook-detail` 这一层；
  `round-card` 是按选中轮的（其 Materials/Comm 页签都随选中轮走），塞进去就等于又按轮过滤了一遍，
  与「跨轮一次看清」的目的自相矛盾。放在轮次轨与详情卡之间，节点→轮次的视觉对应最近，
  且与决策 3 的「去该轮」导航天然相邻。
- 备选（不推荐）：① 条目级新页签——详情页今天没有条目级页签栏（页签都在 `round-card` 内），
  新起一套页签是更大的结构改动；② `round-card` 内新页签——按选中轮过滤，跨轮目的落空；
  ③ 详情卡下方（mail-rules 之后）——详情卡很长，时间线会被埋在页底，违背「一目了然」。
- 形态：可收起的 section（标题如 Timeline），默认展开状态见决策 4；标题行不挂新增功能按钮。

### 决策 2：节点内邮件排布（紧凑列表 vs 完整卡片展开）——推荐：节点内直接嵌完整邮件卡片

- 推荐（完整卡片）：节点展开后，体内直接渲染 `materials-list`（输入 = 该节点邮件子集，
  `allRoundsMail` 传空——`displayGroups` 的并集逻辑在子集上重跑即得节点内 thread 容器 + 单卡），
  与 Materials 页签内的卡片逐像素一致。理由：① 用户已定「邮件详情复用现有邮件卡片」，
  完整卡片是该决定的直接落实；② 票 307/309 的修复自动继承，无需在时间线里再分支；
  ③ thread 容器、分类 chip、Join 芯片、解析行、正文兜底全部现成，零重复逻辑。
- 备选（紧凑列表，不推荐）：节点体内先列一行一条的紧凑行（发件人·日期·短标题），
  点行再内联展开完整卡片。代价：新写一套紧凑行渲染 + 行→卡片的展开状态机，
  与卡片组件形成第二套邮件渲染，日后 307/309 类修复要改两处；且 thread 容器在紧凑行里无处安放。
- 代价说明（推荐方案的）：展开节点会比较高（多封全卡片）；由决策 4 的默认收起状态缓解，
  且用户本来就是为了「展开看每封邮件」才点开节点——高度是按需付出的，不是常驻成本。

### 决策 3：节点头是否提供「去该轮」导航——推荐：提供，但不与展开/收起共用同一次点击

- 推荐：节点头主点击 = 展开/收起节点；头右侧另放一个小 affordance（如「查看本轮」文字链/图标按钮），
  点它 → 选中轮次轨里的对应轮（复用详情页既有选中轮状态），详情卡随之切到该轮。
  未指派节点无此 affordance（它不对应任何轮）。
- 不这样做的后果：用户在时间线里看到某轮邮件后，想改该轮信息仍需手动去轮次轨找轮——
  多一步，但功能不缺；若用户觉得导航多余，砍掉它不影响时间线本体（纯加法，可逆）。
- 反模式（明确不要）：节点头点击既展开又跳转——两种意图打架，点开节点想看邮件却被带走。

### 决策 4：节点默认展开状态——推荐：最新轮次节点默认展开，其余默认收起

- 推荐理由：时间线打开时，节点头列本身就是周期概览（Round 1 → Round 2 → … → 未指派）；
  最新轮次（日期序最后的轮次节点）默认展开，给出最近动态的即时内容，又不把整页撑高。
  「最新」定义：`sortRoundsByDate` 顺序的最后一个**轮次**节点（未指派节点不参与此默认）。
- 备选（可接受）：全部默认收起——更干净的概览，但看任何邮件都要多点一次；
  若实现中「最新轮次」判定引发歧义（如无日期轮次的顺序争议），退化到全部收起，不阻塞本票。
- 不推荐：全部默认展开——多轮多封时页极高，概览意义丧失（与票 236 不做全展开的决定同理）。

---

## 7. 实装要点（给实现者，非本规格的开放问题）

- 新组件位置建议：`web/src/app/features/playbook/redesign/round/` 下与 `materials-list` 并列
  （如 `mail-timeline.component.ts`），输入 `[entry]`；分组计算为纯函数（可单测，
  与 `displayGroups` 同款可测性：输入资产 + 轮次 → 输出节点分配）。
- 归属函数建议形态：`attributeMailToRound(asset, rounds): { roundId: string | null; rule: 1|2|3|4 }`
  （返回命中规则号，便于走查断言与日后排障；规则号不展示给用户）。
- 复用件清单（不得重写）：`sortRoundsByDate`、`roundDisplayNo`、`normalizeThreadSubject` 的前缀剥离正则、
  `shortEmailTitle` 的序数前缀正则（规则 1 之 1）、`materials-list`（节点体内）、
  `displayDate`（日粒度）、`derivePhase`（节点体内卡片自带，不另算）。
- i18n：新增键走既有三语口径（zh/en/fr），至少：时间线标题、未指派节点名、节点空态行；
  不复用 `pb.v2.m.empty` 之外的既有键去贴新语义。
- 门禁（与既有票同口径）：tsc exit 0、check-templates 30/30、Jest 全量（基线 1022，
  新增归属函数单测 + 节点分组单测）、`ng build --configuration production` 显式 exit 0（后台跑）。
  后端零改动则后端套件不跑（diff 零后端文件时注明）。
- 与在途票的时序：**实装须在票 307 落地之后**（否则节点内卡片带着重复头上线）；
  票 309 落地前后均可（卡片自己处理 HTML/纯文本分支，时间线不感知）；
  票 308（整邮箱检索）只改变邮件总体，不改变归属规则；票 310（分类）只改变 chip 值，不改变节点结构。

---

## 8. 走查草图（沙盒真浏览器走查，ng serve 非 4200 端口 + 桩后端）

桩条目（覆盖全部规则与边界，一次走完）：

| 桩数据 | 预期 |
|---|---|
| Round 1（Technical，有日期 2026-10-07）+ Round 2（Technical，无日期） | 节点顺序 Round 1 → Round 2；节点号用展示编号 |
| 邮件 A：主题 `1st round of technical interview — ...`，存储 roundId=Round 1 | Round 1 节点（规则 1） |
| 邮件 B：主题 `2nd round of technical interview — ...`，存储 roundId=Round 1（模拟单日期轮次压扁） | **Round 2 节点（规则 1 覆盖规则 2）——本票最关键断言** |
| 邮件 C：主题 `Re: 2nd round of ...`（带回复前缀），存储 roundId=Round 1 | Round 2 节点（前缀剥离后规则 1 命中） |
| 邮件 D：主题 `Following up`，无序数，存储 roundId=Round 1 | Round 1 节点（规则 2） |
| 邮件 E：无序数，存储 roundId=null，`interviewAt`=2026-10-13 13:00（Round 2 轮次日若补日期则为 2026-10-13；本例 Round 2 无日期 → 规则 3 跳过） | 本例中落入未指派节点（规则 4）；另备变体：给 Round 2 补日期 2026-10-13 后重算，E 应进 Round 2 节点（规则 3） |
| 邮件 F：无序数，存储 roundId=null，无 interviewAt | 未指派节点（规则 4），排在最后 |
| Round 3（空轮次，无邮件） | 仍有节点，体为空态行 |
| 同 threadKey 的两封邮件被分属 Round 1 / Round 2 节点 | 各节点内独立成组（thread 不跨节点），原 Materials 页签内 thread 仍完整 |
| 全部邮件都有 roundId 且无未指派邮件的条目 | 不渲染未指派节点 |

走查断言清单：节点顺序与编号（展示编号）、节点头（Stage/日期/封数/日期跨度）、
节点展开/收起、节点体内卡片与 Materials 页签内同一封卡片逐项一致（头、chip、解析行、正文）、
决策 3 的「去该轮」 affordance（如采纳）切轮正确、Materials/Comm 原视图零回归、
页面无 console error；证据目录 `~/workspace/t311-timeline/`（walk-result.json + 截图）。
诚实缺口预留：同日多封的 intra-day 顺序只能断言「稳定」（后端原序），不得断言真实收件时刻序。
