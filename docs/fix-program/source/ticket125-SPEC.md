# 票 125 规格：AI Practice 五项评审修复（追问文本来源 / 硬编码中文 i18n / 删外层标题行 / Take 整行点击）

- **票号**：125（本文件为规格稿，待独立评分 ≥9.9 过门后才许登记实现；评分由主 agent 另行独立派出，本稿作者不自评）
- **版本**：R1（待独立评分）
- **需求来源**：用户 2026-10-23 对 AI Practice 页的截图评审五项。主线转述为四项可定位修复（下表），**第五项原文无存档**（2026-10-24 落笔时查 `docs/fix-program/FIX-STATUS.md` 与仓库 docs 均无票 125 登记条目、无评审原文文件）——本稿不凑数：第 8 节按证据强度列两个候选 + 判别方法，第五项以用户裁认为准，本票范围不预含其修复。

| # | 评审项 | 本稿落点 |
|---|---|---|
| 1 | 「AI 追问一层」点了没反应（恒死） | 第 4 节（文本来源改 `score.recognized`；无文本给反馈） |
| 2 | ★批硬编码中文（切英文界面仍冒中文） | 第 5 节（全量普查 + i18n 迁移） |
| 3 | Score Results 标题出现两遍，删外层标题行 | 第 6 节（DOM 钉死删 `.rep-title`、留内层 `.res-head`） |
| 4 | Take 条要点「查看报告」小钮才能展开，要求整行点击 expand/collapse | 第 7 节（行点击 ≡ hi-view 钮语义） |
| 5 | 未定位 | 第 8 节开放问题（候选 + 判别证据，不在本票修复范围） |

- **事实基线**：`develop-new-features` tip `86a538f`（票 123/124 交接基线）。本规格全部代码断言（文件/行号/行为）均于落笔前在该 tip 的 worktree `~/workspace/wt-handover` 逐处实读核实（与 `f843efa` 的 ai-practice 两主文件 md5 一致），见第 1 节证据表。行号锚点以类名/选择器/方法名为第一锚、行号为第二锚；实现期开工先以第一锚复核行号漂移，漂移只许在实现报告中登记、不许改变语义口径。
- **本文件地位**：实现唯一事实源。实现与本规格不一致 = 实现错；实现中撞到本规格未覆盖的真实冲突，停工上报，不许现场即兴改稿。

---

## 0. 需求复述三件套

**做什么**：四项定点修复——① AI 追问与「加入复习」改从评分的识别文本（`score.recognized`）取答案原文，取不到文本时给用户明确 toast 反馈、绝不静默无反应；② AI Practice 面全部用户可见硬编码中文走 i18n（普查定界、已知点位全量迁移、新键三语齐备）；③ 删除报告区外层标题行 `.rep-title`（含其重复的 Score Results 标题与 toggle 钮），保留内层计分卡 `.res-head` 的同名标题；④ Take 条整行点击改为展开/收起该条报告（与行内「查看报告」钮完全同义），行内既有交互元素不误触，补齐键盘与 aria-expanded。

**不做什么**：不改评分/录音/识别链本身；不改 `reportOpen` 全局单开状态机的语义结构（不做 per-take 展开集）；不改 `selectTake` 的幂等选择语义（第 7 节是改行点击的**接线目标**，不是改 `selectTake` 本体）；不动词典卡面（票 126/127/128/131 面）；不迁移存量数据（既有复习卡的 category 文本不回溯改写）；第 8 节开放问题候选不在本票动手。

**验收长什么样**：第 9 节 R-125-1…R-125-10 逐条过 + 活体剧本 E1–E5 全过（E1/E2 的真实 LLM/评分一幕按第 12 节残余口径）。核心四幕：① 已评分 take 点「AI 追问一层」→ 真出一道追问（请求 prompt 里装的是识别原文）；识别文本为空 → 点了立刻有 toast 说明，不再装死；② 界面切 English → 报告区逐块走查零中文 UI 文案；③ 打开报告 → 全页只剩一个 Score Results 标题（内层那个）；④ 点 Take 条任意空白处 → 报告展开，再点 → 收起，点行内播放/评分/删除/进度条不误触展开状态。

---

## 1. 现状实况（代码证据，落笔前逐处实读，基线 86a538f）

### 1.1 追问恒死（评审项 1）

| # | 实况 | 证据 |
|---|---|---|
| C-1 | 后端录音 DTO `RecordingDto` 字段全集 = `id / materialId / durationSeconds / sizeBytes / contentType / createdAt / score / version`——**没有 `transcript`、没有 `text`、没有任何文本字段** | `web/src/app/core/api/practice-api.service.ts:101-111` |
| C-2 | 识别文本在评分 DTO 上：`RecordingScoreDto.recognized: string` | `practice-api.service.ts:79-99`（`recognized` 在 :85） |
| C-3 | 前端本地录音类型同构：`Recording`（recorder）的 `score: RecordingScore`，`RecordingScore.recognized: string`（注释「识别出的文本」）；会话 `activeTake()` 返回该类型 | `web/src/app/core/recorder/recorder.service.ts:9`（`Recording`）、`:50`（`recognized`）；`practice-grading-session.ts:568-573`（`activeTake`） |
| C-4 | `addToReview()` 取文本：`const transcript = (take as any)?.transcript ?? '';`——字段不存在 → **恒为 `''`**；随后 `myAnswer: transcript.slice(0, 1000)` 恒空串建卡（静默数据丢失：卡建成了、toast 报成功、「我的回答」永远是空的） | `practice-grading-session.ts:305-323`（取文本 :309、建卡 :311-318、成功 toast :319） |
| C-5 | `generateFollowUp()` 取文本：`const transcript = (take as any)?.transcript ?? (take as any)?.text ?? '';`——两字段皆不存在 → 恒 `''`；下一行 `if (!transcript.trim()) return;` **静默返回**——不发请求、不置 loading、不给任何反馈 | `practice-grading-session.ts:325-351`（取文本 :328、静默 return :330） |
| C-6 | 追问钮的渲染位置在报告内容内（`@if (activeTake()?.score; as sc)` 闸门内），即「有分才看得见钮」与 C-5 叠加：用户看到钮、点了永远没反应，100% 复现 | `ai-practice.component.html:618-634`（钮 :620-622、loading 条 :624-626、结果卡 :627-633） |
| C-7 | 两函数的其余守卫：`generateFollowUp` 的 `!take || followUpLoading()`（:327）是重入守卫；`addToReview` 的题目为空 return（:307-308）——均与恒死无关，不在本票改动面（C-7 后者在第 14 节如实登记为观察项） | `practice-grading-session.ts:307-308, 327` |

### 1.2 硬编码中文（评审项 2）

| # | 实况 | 证据 |
|---|---|---|
| C-8 | 已知点位全量表见第 5.3 节（逐行实读核实：HTML 10 处、session TS 15 行、component TS 1 处待判定） | 第 5.3 节表 |
| C-9 | 普查边界实测：对 `features/ai-practice/` 全目录做「剥注释后抽字符串字面量 + 模板文本」扫描——两主文件与 `ai-practice.component.ts:535` 之外，**用户可见硬编码中文 = 0**；其余命中全是注释（`material-transfer-dialog.component.html` 的 `<!-- -->`、`material-transfer.ts:510`、`practice-tts-player.ts:600` 等）；词典面三文件（126/127/128）零命中 | 落笔期扫描（方法即第 5.1 节普查法的预演） |
| C-10 | i18n 机制：`I18nService.t(key)` 读 `_lang` signal（语言切换即时重算模板与 computed）；键表条目形态 `{ zh, en, fr }` 三语一键；占位符先例 = 模板侧 `.replace('{n}', …)` | `web/src/app/core/i18n/i18n.service.ts:3033`（`_lang` signal）、`:3059`（`t()`）、`:3071`（`tn()` 的 `{n}` 替换）；`ai-practice.component.html:675-676`（`.replace` 先例） |
| C-11 | 既有可复用键：四维名 `dim.pronunciation / dim.fluency / dim.completeness / dim.accuracy` 已被报告胶囊使用（html:695/702/715/722 区）；`practice.scoreResults` 等 practice.* 键体系现行 | `ai-practice.component.html` 胶囊区；`i18n.service.ts` practice.* 段 |
| C-12 | 会话类已有走 i18n 先例：`this.host.t('rec.noAudio')`、`this.host.t('practice.retryLater')`、`this.host.t('practice.prevTakeDiscarded')`——`oneFix`/`speechPace` 两 computed 内的中文是同文件内的口径不一致，不是机制缺失 | `practice-grading-session.ts:259, 262, 396` |

### 1.3 外层标题行（评审项 3）

| # | 实况 | 证据 |
|---|---|---|
| C-13 | 报告区结构（票 121 把原独立 section 整块搬进 take 循环后）：`@if (isTakeReportOpen(r.id))` → `<section class="report-section">`，其首子元素即外层标题行 `<div class="rep-title">`：assessment 图标 + `<h3>{{ t('practice.scoreResults') }}</h3>` + `@if (score)` 的 rep-toggle 钮 | `ai-practice.component.html:582-595`（`.rep-title` :584、图标 :585、外层 h3 :586、rep-toggle 钮 :590） |
| C-14 | 内层计分卡标题在「总分 + 四维胶囊」块的 `.res-head` 内：`<h3>{{ t('practice.scoreResults') }}</h3>`——**与外层同一键、同文案，一屏两遍**，这就是用户截图里看到的重复 | `ai-practice.component.html:660-661` |
| C-15 | rep-toggle 钮（:590）调组件 `toggleReport()`（component.ts:963-970）→ 会话 `toggleReport()`（session:711-713）翻 `reportOpen`；但 section 的渲染条件 `isTakeReportOpen` 本身含 `reportOpen`（session:754-756）——钮把自己连同整个 section 一起关掉，其「查看详细」文案态（reportOpen=false 时）在该钮上**永远不可达**：票 121 搬家时原样搬入的残留（原独立 section 时代标题行常驻、钮在其上切换内容，才成双态） | `ai-practice.component.ts:963-970`；`practice-grading-session.ts:708-713, 744-756` |
| C-16 | `reportOpen` 信号本身是活状态机的一部分：`toggleTakeReport()`（session:744-751）写它、`isTakeReportOpen()` 读它、模板 :598 还有一层 `@if (reportOpen())` 闸（在 section 已渲染时恒真，121 既有形态）——删标题行**不许**误伤这些 | `practice-grading-session.ts:744-756`；`ai-practice.component.html:598` |
| C-17 | 焦点归位（票 121）：组件 `toggleTakeReport()` 与 `toggleReport()` 在收起后经 `collapseFocusTarget()` + `refocusAfterCollapse()` 把焦点送回行内 `.hi-view` 钮；`collapseFocusTarget()` 有两分支——锚点是 `.hi-view` 本人，或锚点在 `section.report-section` 内（后者专为 rep-toggle 钮服务，删钮后成死分支） | `ai-practice.component.ts:976-985, 1003-1013` |

### 1.4 Take 条点击（评审项 4）

| # | 实况 | 证据 |
|---|---|---|
| C-18 | 行本体：`<div class="hist-item">`，现行 `(click)="selectTake(r.id)"`、`tabindex="0"`、`(keyup.enter)="selectTake(r.id)"`——点行只是**选中**（幂等：点已选中行无任何效果），不展开/收起报告 | `ai-practice.component.html:525-529` |
| C-19 | 行内交互元素共 **5 个**（主线转述的「三按钮」与代码不符，以代码为准）：播放钮 `.hi-play`（:531）、进度条 `.track.hi-track`（:547，点击 seek）、评分钮 `.hi-score`（:556）、查看/收起报告钮 `.hi-view`（:562，仅 `@if r.score` 时在席）、删除钮 `.hi-del`（:571）——**5 个现行全部已带 `$event.stopPropagation()`** | `ai-practice.component.html:531, 547, 556, 562, 571` |
| C-20 | 展开/收起的现行唯一入口 = `.hi-view` 钮 → 组件 `toggleTakeReport(id)`（component.ts:976-985）：调会话同名法（session:744-751：已开 → 关；否则选中 + 开），并附带两件包装行为——切换 take 时 `closeWordPopOnSwitch(prev)`（票 120 词弹层随切换关闭）、收起时焦点归位（票 121） | `ai-practice.component.ts:976-1001`；`practice-grading-session.ts:744-751` |
| C-21 | `selectTake`（session:735-741）与 `toggleTakeReport` 的副作用差：前者切行时 `stopPlayback()`，后者**不**停播（现行 hi-view 钮切行展开即如此）——第 7 节的语义统一须显式处理这一差，见 D7 | `practice-grading-session.ts:735-751` |
| C-22 | 无分 take 的报告区不是空白：section 内有空状态分支 `<p class="rep-empty">{{ t('practice.reportEmpty') }}</p>`（引导产出报告、不显示假分数）；现行无分 take 没有任何入口能打开它（hi-view 钮不在席、行点击只选中） | `ai-practice.component.html:826-832`（空状态文本 :829） |

---

## 2. 决策总表

| # | 决策 | 一行理由 |
|---|------|----------|
| D1 | 文本来源两处统一改 `take?.score?.recognized ?? ''`，**类型化访问、去掉该处的 `as any`**；切片长度不动（addToReview 1000、followUp prompt 2000） | C-1/C-2/C-3：文本只存在于 score 上；`as any` 正是这个 bug 躲过类型检查的原因，去掉它让同类错误下次编译期现形 |
| D2 | 无识别文本的反馈口径：两函数共用一个新键 `practice.noRecognizedText` 走 `host.toast(...)`；追问**不发请求**、复习**不建卡**（中止并反馈） | 「不许静默」是评审项 1 的明示口径；复习卡的核心内容就是「我的回答」，无回答建空卡 = 把 C-4 的静默数据丢失换个壳继续（卡在 Tech Stack 里看着像建好了、内容是空的），故中止 + 明示，不建残卡 |
| D3 | 普查边界 = `features/ai-practice/` 全目录（第 5.1 节方法）；`core/` 服务（recorder 等）不在本票普查面 | C-9 实测：面内用户可见硬编码中文只在两主文件 + component.ts 一处待判定点；core 服务是跨页基础设施，其文案口径不属本页评审范围，扩面即扩票 |
| D4 | i18n 迁移：既有键复用优先（四维名用 `dim.*`），新键一律 `practice.*` 命名、zh/en/fr 三语齐备才许落地；占位符沿 `.replace('{x}', …)` 先例 | C-10/C-11：键表形态与页面既有写法一致；三语缺一语 = 该语言用户看到键名或空串，比硬编码更糟 |
| D5 | `oneFix`/`speechPace` 两 computed 改为在 computed 内调 `this.host.t(...)` 产出文案（返回值形态不变、模板这两处零改动） | C-10/C-12：`t()` 读 lang signal，computed 内调用即随语言切换重算；改返回值来源、不改 shape，波及面最小 |
| D6 | 外层标题行整块删（`.rep-title` 含图标、h3、rep-toggle 钮）；连带死代码（组件 + 会话的 `toggleReport()`、`collapseFocusTarget()` 的 section 分支）一并清除；`reportOpen` 状态机本体不动 | C-13/C-15：标题行的钮与行同生同灭（钮的双态已不可达、功能与 hi-view 钮完全重复）；留着死代码会让下个改这块的人误判还有第二入口 |
| D7 | 行点击改接组件 `toggleTakeReport(r.id)`——与 hi-view 钮**同一方法、同一副作用集**（含词弹层随切换关闭、收起焦点归位）；明确接受其与旧行点击的唯一行为差：切行展开时不再 `stopPlayback()`（与现行 hi-view 钮切行行为一致，非新引入） | C-20/C-21：整行点击的用户心智就是「点这条 = 展开/收起这条」，与 hi-view 钮同义是唯一自洽口径；两套展开语义（行一套钮一套）必然互相打架 |
| D8 | 无障碍落点：行 div 加 `[attr.aria-expanded]`、hi-view 钮加 `[attr.aria-expanded]`；行**不加** `role="button"`；键盘 Enter（现行）之外补 Space | 行内含 5 个交互元素，容器再披 button 角色 = 嵌套交互，读屏模型非法；状态暴露靠 aria-expanded + 既有钮文案切换（查看/收起），已足够 |

---

## 3. 与既有规格/行为的关系（显式登记）

| 条款 | 关系 |
|---|---|
| 票 121（报告内嵌 take 行 + 收起焦点归位） | **继承并保全**：hi-view 路径的焦点归位（component.ts:976-985）逐字不动；第 6 节删 rep-toggle 后，其专属的 `collapseFocusTarget()` section 分支成死代码可清（D6），hi-view 分支不许动 |
| 票 120 规则②（切换 take 后词弹层必须关闭，`closeWordPopOnSwitch`） | **保全且受益于 D7**：行点击改走组件 `toggleTakeReport()` 后，行点击切行同样触发词弹层关闭——比现行（行点击走 `selectTake` 包装层也有同处理，component.ts:971-975）不回退 |
| 票 120（填充词条移出网格、回放提示条件显示） | 第 5 节只迁其文案入 i18n，**显示条件与结构一字不改**（html:744-753 的 `@if` 守卫不动） |
| 票 122（点词分派）/ 票 126/127/128（词典卡） | **零交集**：本票不动 `dictionary-*`、`word-click*` 任何文件；报告区内的逐词点击（html:763-770）只迁 tooltip 里的中文词缀（第 5.3 节），点击分派逻辑不动 |
| i18n 既有口径（键表三语、模板 `t()`、`.replace` 占位） | 全部沿用，不引入新机制（不引 i18n 库、不搞管道） |

---

## 4. 评审项 1：追问与复习的文本来源（改法钉死）

作用点唯一：`practice-grading-session.ts` 的 `addToReview()`（:305-323）与 `generateFollowUp()`（:325-351）。两函数的其余逻辑（prompt 模板、systemPrompt、API 调用、切片长度、loading/结果信号的置位顺序）一字不改——注意 :337-338 的 prompt/systemPrompt 是**发给模型的提示词、用户不可见**，不属评审项 2 的 i18n 面（第 5.2 节豁免登记），本节也不动它。

### 4.1 取文本（两处同口径）

```ts
const transcript = take?.score?.recognized ?? '';
```

- `take` 为 `activeTake()` 的返回值（C-3 类型），`score` 可空、`recognized` 为 string——类型化访问，不许再出现 `(take as any)?.transcript` / `(take as any)?.text` 形态（实现报告须以 grep 输出自证两处旧形态零残留）。
- `addToReview`：`myAnswer: transcript.slice(0, 1000)`（长度不变）；`generateFollowUp`：prompt 中 `transcript.slice(0, 2000)`（长度不变）。

### 4.2 无文本反馈（D2 落点，逐函数钉死）

判定点 = 上式 `transcript.trim()` 为空（含 take 为 null、score 为 null、recognized 为空/纯空白四种情形，不再细分）。

- **generateFollowUp**：命中判定 → `this.host.toast(this.host.t('practice.noRecognizedText'))` 后 return。**不许**调 `aiApi.complete`、**不许**置 `followUpLoading`、**不许**改 `followUpQuestion` 现值。原 :330 的静默 return 由此取代。（:327 的重入守卫 `!take || followUpLoading()` 保留原样——loading 中的重复点击静默是对的，不在此列；但 `!take` 与无文本判定重合时走哪条都归到「无请求」结果，toast 与否以实现期不重复打扰为准：take 为 null 时钮本不可达，不作判据。）
- **addToReview**：命中判定 → 同一 toast 后 return，**不许**调 `knowledgeApi.createItem`。（题目为空的 :307-308 守卫排在其前、原样保留。）

### 4.3 成功/失败路径（仅为第 5 节文案迁移让路，行为不变）

- `addToReview` 成功 → toast 改走 `practice.addedToReview`；失败（catch）→ toast 改走 `practice.addToReviewFailed`。建卡入参除 myAnswer 来源外逐字不变（title/question/topic/mastery 不动；category 的值改走第 5.4 节口径）。
- `generateFollowUp` 成功但返回空文本（:343-344）与请求抛错（:346-347）→ 两处 toast 均改走 `practice.followUpFailed`（现行两处同文案，迁移后仍同键）。`followUpQuestion` 的置位逻辑不动。

---

## 5. 评审项 2：硬编码中文全量 i18n

### 5.1 普查方法（实现期必须照此执行并留档）

1. **普查面**：`web/src/app/features/ai-practice/` 下全部 `.ts` / `.html`（含 dialog、子组件、helpers、session、player）；`web/src/app/core/i18n/i18n.service.ts` 只作复用键来源，不是普查对象；`core/` 其余服务不在面内（D3）。
2. **抽取**：脚本扫描，先剥离多行注释（TS 的 `//`、`/* */`，HTML 的 `<!-- -->`，含跨行续行——本面实测有跨行注释块，只剥单行必漏判/误判），再抽三类候选：(a) TS 字符串/模板字面量中的中文；(b) HTML 文本节点与插值相邻文本中的中文（**注意插值相邻文本**：如 `{{ pace.wpm }} 词/分钟` 这类不在标签 `>` 后紧邻的文本，朴素正则会漏——落笔期普查预演就漏过它，靠逐行实读才捞回，见 5.3 表 html:752）；(c) 用户可见属性字面量（`matTooltip` / `title` / `placeholder` / `aria-label`）中的中文。
3. **逐条判定**：每个候选按 5.2 标准判「用户可见 / 豁免」，结果全量写入实现报告的普查表：`文件:行号 | 原文 | 判定 | 处置（复用键名 / 新键名 / 豁免理由）`。普查表条目数与脚本原始命中数必须对得上（差额逐条说明，如同行多字面量）。
4. **基线对照**：本稿 5.3 表是落笔期实测的已知点位全集（普查预演结果）；实现期普查若多出新点位 → 照迁并在普查表标注「规格外新增」；若与 5.3 表对不上（少检）→ 以 5.3 表为准补齐，不许以脚本结果覆盖实读结果。

### 5.2 判定标准（什么叫用户可见）

- **用户可见**（必须迁移）：任一界面语言下用户能看到/读到的文案——模板文本与插值文本、按钮/标签字面量、用户可见属性（tooltip/title/placeholder/aria-label）、toast 文案、由 computed 返回并被模板渲染的字符串（`oneFix` 的 title/detail、`speechPace` 的 verdict）、以及**经 API 写到别页展示的数据标签**（`addToReview` 的 category，见 5.4）。
- **豁免**（逐项登记理由，不迁移）：注释（一切形态）；`console.*` 日志；发给模型的提示词（session:337-338 的 systemPrompt/prompt 模板——用户不可见，且其语言是模型指令口径）；**数据常量**——填充词匹配表 `FILLERS` 的中文词（session:216：`'那个','就是','然后','嗯','啊','这个','其实'`，是拿去匹配识别文本的功能数据，译掉即功能损坏）；语种自称标签（component.ts:535 的 `'中文'`——语言选项以自身文字书写是行业惯例，与同列 English 等自称一致，见 5.3 表判定行）。
- 判定存疑时默认**从严**（按用户可见迁移），并在普查表标注存疑点交主线裁——不许实现者自行按豁免处理存疑项。

### 5.3 已知点位表（落笔期逐行实读，基线 86a538f）

**`ai-practice.component.html`**（10 处用户可见）：

| 行 | 原文 | 处置 |
|---|---|---|
| :277 | `练习题: {{ cq }}`（自定义题横幅） | 新键 `practice.customQuestionLabel`（模板拼插值） |
| :278 | `matTooltip="清除"` | 复用优先：先查键表既有「清除」键（如 `common.*`），无才建 `practice.clearTooltip` |
| :611 | `加入复习`（oneFix 横幅钮） | 新键 `practice.addToReviewBtn` |
| :621 | `AI 追问一层`（追问钮） | 新键 `practice.followUpBtn` |
| :629 | `面试官追问`（追问卡标签） | 新键 `practice.followUpLabel` |
| :631 | `换一个`（追问卡钮） | 新键 `practice.followUpAnother` |
| :744 | `填充词 <b>{{ fs.count }}</b> · {{ fs.perMinute }}/分钟` | 新键 `practice.fillerStats`（`{count}`/`{per}` 占位，`.replace` 拼装；`<b>` 结构保留在模板侧） |
| :748 | `点击橙色词回放该时刻` | 新键 `practice.fillerReplayHint` |
| :752 | `{{ pace.wpm }} 词/分钟 · {{ pace.verdict }}`（语速 chip 的单位词） | 单位走新键 `practice.wordsPerMinute`（`{wpm}` 占位）；`verdict` 本身在 session 侧迁移（下表） |
| :769 | 逐词 tooltip 拼接 `'填充词' + ' · 点击回放'` | 新键 `practice.fillerWord`、`practice.fillerClickReplay`（拼接结构不变、只换字面量来源） |

同文件 :326、:352、:415-416、:664-665 的中文均为 `<!-- -->` 注释续行——豁免（普查脚本须正确剥跨行注释，不许把它们误报为点位、更不许「迁」注释）。

**`practice-grading-session.ts`**（15 行用户可见 + 2 项豁免）：

| 行 | 原文 | 处置 |
|---|---|---|
| :246-248 | `speechPace` verdict `偏慢 / 适中 / 偏快` | 新键 `practice.paceSlow / paceGood / paceFast`，computed 内 `host.t`（D5） |
| :272 | oneFix title `减少填充词` | 新键 `practice.oneFixFillersTitle` |
| :273 | oneFix detail 模板串（含 count/perMinute 插值） | 新键 `practice.oneFixFillersDetail`（`{count}`/`{per}` 占位，computed 内 `.replace`） |
| :277-280 | 四维 label `流利度 / 准确度 / 发音 / 完整度` | **复用既有键** `dim.fluency / dim.accuracy / dim.pronunciation / dim.completeness`（C-11），不建新键 |
| :287-290 | fixes 四条详情文案 | 新键 `practice.oneFixFluency / oneFixAccuracy / oneFixPron / oneFixCompleteness` |
| :294 | title 模板串 `优先改进:{label} ({score}分)` | 新键 `practice.oneFixPriority`（`{label}`/`{score}` 占位） |
| :295 | `继续练习。`（fallback detail） | 新键 `practice.oneFixFallback` |
| :316 | `category: '口语练习'`（建卡数据） | 见 5.4：新键 `practice.reviewCategory`，建卡时取当前语言值写入 |
| :319 | toast `已加入 Tech Stack 复习` | 新键 `practice.addedToReview` |
| :321 | toast `加入失败,请重试` | 新键 `practice.addToReviewFailed` |
| :344、:347 | toast `AI 追问失败,请检查 AI 配置`（两处） | 新键 `practice.followUpFailed`（两处同键） |

豁免：:216 填充词数据表（功能数据）；:337-338 LLM 提示词（用户不可见）。

**`ai-practice.component.ts`**：:535 `'中文'` = 语种自称标签 → **豁免**（判定理由见 5.2；同文件其余中文仅 :625 行尾注释，豁免）。此判定在普查表中单列，评分/主线可推翻——推翻则按新键迁移，不许实现者自行改判。

### 5.4 category 的写入时口径（:316 单独钉死）

`category` 是写进 Tech Stack 数据、在别页长期展示的标签，不是当屏文案。口径：建卡时以 `this.host.t('practice.reviewCategory')` 取**建卡当刻界面语言**的值写入（英文界面建的卡，category 就是英文）。存量卡不回溯（第 14 节）。不许把键名本身写进数据、不许双语拼接。

### 5.5 新键总表（命名与三语值钉死；en 为用户主用语言，值须自然书面英语）

| 键 | zh | en | fr |
|---|---|---|---|
| `practice.noRecognizedText` | 还没有识别文本，先完成一次评分再试 | No recognized speech yet — score a recording first. | Aucun texte reconnu pour le moment — lancez d'abord une évaluation. |
| `practice.addToReviewBtn` | 加入复习 | Add to review | Ajouter à la révision |
| `practice.addedToReview` | 已加入 Tech Stack 复习 | Added to Tech Stack review | Ajouté à la révision Tech Stack |
| `practice.addToReviewFailed` | 加入失败，请重试 | Couldn't add it — please try again. | Échec de l'ajout — réessayez. |
| `practice.followUpBtn` | AI 追问一层 | AI follow-up question | Question de relance IA |
| `practice.followUpLabel` | 面试官追问 | Interviewer's follow-up | Relance de l'intervieweur |
| `practice.followUpAnother` | 换一个 | Another one | Une autre |
| `practice.followUpFailed` | AI 追问失败，请检查 AI 配置 | The follow-up failed — check your AI settings. | La relance a échoué — vérifiez vos réglages IA. |
| `practice.customQuestionLabel` | 练习题： | Practice question: | Question d'exercice : |
| `practice.fillerStats` | 填充词 {count} · {per}/分钟 | Fillers {count} · {per}/min | Mots de remplissage {count} · {per}/min |
| `practice.fillerReplayHint` | 点击橙色词回放该时刻 | Click an orange word to replay that moment | Cliquez un mot orange pour réécouter ce moment |
| `practice.wordsPerMinute` | {wpm} 词/分钟 | {wpm} words/min | {wpm} mots/min |
| `practice.paceSlow` | 偏慢 | Slow | Lent |
| `practice.paceGood` | 适中 | Good pace | Rythme correct |
| `practice.paceFast` | 偏快 | Fast | Rapide |
| `practice.fillerWord` | 填充词 | filler word | mot de remplissage |
| `practice.fillerClickReplay` | 点击回放 | click to replay | cliquer pour réécouter |
| `practice.oneFixFillersTitle` | 减少填充词 | Cut the filler words | Réduisez les mots de remplissage |
| `practice.oneFixFillersDetail` | 检测到 {count} 个填充词（{per}/分钟）。下次在想词时停顿 1 秒，而不是说 "um"。点击橙色词回放。 | {count} filler words detected ({per}/min). Next time, pause for a second instead of saying "um". Click an orange word to replay it. | {count} mots de remplissage détectés ({per}/min). La prochaine fois, marquez une pause d'une seconde au lieu de dire « euh ». Cliquez un mot orange pour le réécouter. |
| `practice.oneFixPriority` | 优先改进：{label}（{score}分） | Top fix: {label} ({score}) | À corriger d'abord : {label} ({score}) |
| `practice.oneFixFluency` | 语速不均。试着用 2 倍速听一遍自己的录音，标记卡顿处，重录时在这些地方刻意放慢。 | Uneven pace. Listen to your recording at 2x, mark where you stumble, and slow down there on the next take. | Rythme irrégulier. Écoutez votre enregistrement en 2x, repérez les accrochages et ralentissez à ces endroits au prochain essai. |
| `practice.oneFixAccuracy` | 个别词发音不准。点击下方红色波浪线词，看音标，跟读 3 遍。 | A few words are mispronounced. Click a red-underlined word below, check its phonetics, and shadow it 3 times. | Quelques mots sont mal prononcés. Cliquez un mot souligné en rouge ci-dessous, regardez sa phonétique et répétez-le 3 fois. |
| `practice.oneFixPron` | 整体发音需加强。每天跟读 5 分钟，重点模仿语调起伏。 | Overall pronunciation needs work. Shadow 5 minutes a day, focusing on intonation. | Prononciation globale à renforcer. Cinq minutes de shadowing par jour, en imitant l'intonation. |
| `practice.oneFixCompleteness` | 内容不完整。有漏读的词，对照原文检查。 | Incomplete — some words were skipped. Check against the original text. | Contenu incomplet — des mots ont été sautés. Vérifiez par rapport au texte original. |
| `practice.oneFixFallback` | 继续练习。 | Keep practicing. | Continuez à vous entraîner. |
| `practice.reviewCategory` | 口语练习 | Speaking practice | Pratique orale |

条件键：`practice.clearTooltip`（清除 / Clear / Effacer）仅当 5.3 的复用查询无果时才建。键表落地位置 = `i18n.service.ts` 的 practice.* 段，条目形态 `{ zh, en, fr }` 与存量一致；三语任一缺失 = 该键不许合入（第 9 节判据）。

---

## 6. 评审项 3：删 Score Results 外层标题行（DOM 钉死）

### 6.1 删哪

删 `ai-practice.component.html:584-595` 的**整个** `<div class="rep-title">…</div>` 块，含：
- :585 的 assessment `<mat-icon>`；
- :586 的外层 `<h3>{{ t('practice.scoreResults') }}</h3>`；
- :588-594 的 `@if (activeTake()?.score)` 包裹的 rep-toggle 钮（:590）及其 spacer。

删后 `<section class="report-section">` 的首子元素即原 `@if (activeTake()?.score; as sc)` 内容块（现 :597 起）/ 空状态分支（现 :826 起），section 本体与 `@if (isTakeReportOpen(r.id))` 渲染条件不动。

### 6.2 留哪（逐条不许动）

- 内层 `<div class="res-head">`（:660）内的 `<h3>{{ t('practice.scoreResults') }}</h3>`（:661）——删后它成为报告区**唯一**的 Score Results 标题；同键继续使用，键表不动。
- 其余三个 `.res-head`（:642 波形、:730 Recognition、:805 Word Scores）及其标题键全部不动。
- 空状态 `.rep-empty`（:826-832）与 `practice.reportEmpty` 键不动。
- `reportOpen` 信号、`toggleTakeReport()`、`isTakeReportOpen()`、模板 :598 的 `@if (reportOpen())` 闸——全部原样（C-16）。

### 6.3 连带死代码清理（D6，范围封顶）

- 组件 `toggleReport()`（component.ts:963-970）与会话 `toggleReport()`（session:711-713）：删钮后无任何调用方 → **删除**（实现报告须以 grep 自证两方法名在 web/src 下零引用残留，测试代码除外若有须同步说明）。
- `collapseFocusTarget()`（component.ts:1003-1009）的 `section.report-section` 分支：服务对象仅为已删的 rep-toggle 钮 → 可删；`.hi-view` 分支与 `refocusAfterCollapse()` **逐字保全**（票 121 行为，TJ-7/E4 连带回归）。
- SCSS 中 `.rep-title` / `.rep-toggle` 样式成孤儿：删除与否不作判据，但不许借机改动其他任何样式。

---

## 7. 评审项 4：Take 条整行点击 expand/collapse

### 7.1 接线变更（唯一改动点）

`ai-practice.component.html` 的 `.hist-item`（:525-529）：

- `(click)` 的目标由 `selectTake(r.id)` 改为 `toggleTakeReport(r.id)`（组件包装方法，component.ts:976-985——**不许**绕过包装层直调会话方法，否则票 120 词弹层关闭与票 121 焦点归位会从行点击路径上脱落）；
- `(keyup.enter)` 同步改走 `toggleTakeReport(r.id)`；
- 新增 Space 触发：Space 键在行聚焦时须与 Enter 同效，且不许引发页面滚动（keydown 阶段 `preventDefault` 或等价处理；具体事件绑定形态归实现，行为以 TJ-7/E5 为判据）；
- `tabindex="0"` 保留。

改后语义（由 `toggleTakeReport` 现行定义给出，不新写状态机）：点本行 → 本条报告开着则收起（`reportOpen=false`）；没开（或当前展开的是别条）→ 选中本条并展开。**无分 take 同样适用**：行点击展开其报告区 → 显示空状态 `.rep-empty`（C-22，该分支自此有了入口）。

### 7.2 与旧行点击的行为差（如实登记，D7）

| 情形 | 旧（selectTake） | 新（toggleTakeReport） |
|---|---|---|
| 点未选中行 | 选中；若 reportOpen 恰为 true，报告区随选中切换到新行（展开态被动跟随） | 选中 + 明确展开本条报告 |
| 点已选中且报告开着的行 | **无任何效果**（幂等） | **收起报告**（本项需求本体） |
| 点已选中但报告关着的行 | 无任何效果 | 展开报告 |
| 切行时正在播放 | `stopPlayback()` 停播 | **不停播**（会话 `toggleTakeReport` 无此副作用；与现行 hi-view 钮切行展开的行为一致） |
| 切行时词弹层开着 | 包装层 `closeWordPopOnSwitch` 关弹层 | 同（同一包装层路径，D7） |
| 收起时焦点 | 不涉及（旧行点击不收起） | 包装层焦点归位逻辑作用于 `.hi-view` 锚点；行本身是收起后仍存活的 DOM（消失的是其后 sibling section），键盘路径下焦点不许掉到 body（TJ-7 断言） |

「不停播」一条是语义统一的代价而非疏漏：行与 hi-view 钮自此完全同义，同一动作在两个入口上不许有两种播放后果。实现报告须在偏差/说明区复述此差，活体 E5 含一幕专门对照（播 A → 点行 B → 报告切到 B 且播放不中断，与点 B 的 hi-view 钮结果一致）。

### 7.3 行内元素不误触（C-19 的 5 个，逐个钉死）

下列 5 个元素的现行 `$event.stopPropagation()` **一处不许丢**，且各自原动作不变；除 hi-view 本身即切换钮外，其余 4 个的点击**不许**改变报告展开状态（`isTakeReportOpen` 对任一 take 的取值不变）与当前选中（`activeTakeId` 不变）：

| 元素 | 行 | 原动作 |
|---|---|---|
| `.hi-play` 播放钮 | :531 | togglePlay(r) |
| `.track.hi-track` 进度条 | :547 | seek(r, $event) |
| `.hi-score` 评分钮 | :556 | gradeRecording(r.id) |
| `.hi-view` 报告钮 | :562 | toggleTakeReport(r.id)（它就是切换本体，其 stopPropagation 防的是行 handler 重入——改后行与钮同方法，若重入会双击翻转两次 = 点了等于没点，stopPropagation 在此是**正确性必需**，不只是防误触） |
| `.hi-del` 删除钮 | :571 | removeRecording(r.id) |

特别点名 hi-view 的重入陷阱：本项落地最易犯的错就是删/漏它的 stopPropagation 导致一次点击翻转两次。TJ-7 必须有一例专测「点 hi-view 恰翻转一次」。

### 7.4 无障碍（D8 落点）

- 行 div 加 `[attr.aria-expanded]="isTakeReportOpen(r.id)"`，随展开状态实时取值（true/false 字面量）。
- `.hi-view` 钮加 `[attr.aria-expanded]="isTakeReportOpen(r.id)"`（其文案本就随状态切 查看/收起，aria 与文案同值双保险）。
- 行不加 `role="button"`、不加 `role="switch"` 等任何交互角色（D8 理由）；不新增 aria-label（行内文本本身可读）。
- 键盘焦点样式不许因本项改动消失（`.hist-item` 现行 focus 样式在 SCSS，不动）。

---

## 8. 开放问题：第五项候选（不凑数、不预修）

第五项原文无存档（见卷首）。落笔期代码复核在同屏面内找到两个**有实锤证据**的候选，按「与已定位四项的同源程度」排序如下。两候选**均不在本票修复范围**；用户/主线裁认哪条（或都不是），再另行立票或并票处理。本节的作用是把判别证据留档，不让候选靠口头流传。

### Q-125-1（候选甲，与评审项 3 同块）：rep-title 内 rep-toggle 钮的「查看详细」态永不可达

- 证据：C-15——钮的文案按 `reportOpen()` 在「收起报告 / 查看详细」间选（component.html:591），但钮存在的条件（section 渲染）已蕴含 `reportOpen()===true`，故它永远只显示「收起报告」一态，且点击效果是把整个 section（含它自己）销毁，与 hi-view 钮功能完全重复。
- 判别：若用户所指第五项是「标题行那个展开钮点了怪怪的/和下面重复」一类观感，即此候选；它与评审项 3 同块，**删外层标题行即连带消除**，无需独立修复——这是它排在候选首位的原因：大概率它就是第 3 项的组成部分而非独立一项。

### Q-125-2（候选乙，与评审项 1 同面）：追问结果跨 take 残留（A 的追问显示在 B 的报告里）

- 证据：`followUpQuestion` / `followUpLoading` 是会话级全局信号（session:301-302）；全仓写入点仅在 `generateFollowUp()` 内部（:333 置 null、:343 置结果、:346 置 null）；`selectTake`（:735）、`toggleTakeReport`（:744）、`focusTake`（:353-356）**均不清它**；而追问区渲染在「当前展开的那条 take」的报告内（html:618-634，只受 activeTake 的 score 闸控制，不校验追问属于哪条 take）。对照：词弹层在切行时有专门清理（票 120 的 `closeWordPopOnSwitch`），追问信号无任何对应清理。
- 活体判别法：在 take A 的报告里点出一条追问 → 切到 take B 的报告 → 若 B 的追问区直接躺着 A 的那条追问（无需点击），即实锤。
- 与第 1 项的关系：同属 ★#14 追问面，但根因不同（第 1 项是取错字段恒死，本候选是状态不随 take 隔离）；第 1 项修好后此候选才从「不可见」变「可见」（恒死时根本生成不出追问）——若用户评审时追问从未出过题，他截图里指的第五项**就不太可能是它**（除非指的是别的观感）。此层推理如实记录，供裁认参考。

### 非候选（如实排除一条）

`addToReview` 的题目为空静默 return（C-7，session:307-308）：钮的渲染上下文（oneFix 横幅在报告内、题目必有来源）使该守卫在现行 UI 上不可达，不构成用户可感知的评审项，仅登记备查，不列候选。

---

## 9. 验收条目（R-125-x，逐条可测）

| ID | 需求 | 可测判据（摘要） |
|---|---|---|
| R-125-1 | 追问取识别文本真出题：prompt 装的是 `score.recognized` 原文，结果渲染进追问卡 | TJ-1（假 host 断言 aiApi 入参含 recognized 原文 + followUpQuestion 置位）+ E1 |
| R-125-2 | 追问无文本必反馈：零请求、toast 出 `practice.noRecognizedText`、loading 不置位 | TJ-2 + E2 |
| R-125-3 | 加入复习带真答案：createItem 的 myAnswer = recognized 切片；无文本不建卡且有反馈 | TJ-3、TJ-4 |
| R-125-4 | i18n 全量迁移：已知点位（5.3 表）逐条处置完毕，普查面内用户可见中文零残留（豁免表除外） | TJ-6（普查脚本 + 普查表对账）+ E3 |
| R-125-5 | 语言切换即时生效：oneFix/speechPace 文案随 lang signal 重算，切 English 后无中文、切回中文恢复 | TJ-5（computed 双语断言）+ E3 |
| R-125-6 | 外层标题行消失：报告区内 Score Results 标题恰一个（内层 .res-head 那个），`.rep-title` 在 DOM 中不存在 | TJ-7 + E4 |
| R-125-7 | 整行点击 ≡ hi-view 钮：展开/收起/切行三态与钮路径结果逐一相同（含词弹层关闭、焦点归位、不停播差） | TJ-7 + E5 |
| R-125-8 | 行内不误触：播放/进度条/评分/删除四元素点击后展开状态与选中均不变；hi-view 一次点击恰翻转一次 | TJ-7 + E5 |
| R-125-9 | 键盘与 aria：行聚焦 Enter/Space 均可切换、Space 不滚页；行与 hi-view 钮的 aria-expanded 与实际状态恒等 | TJ-7 + E5 |
| R-125-10 | 回归不破：Jest 基线 399 只升不降、后端 445 不变；票 120/121 行为（词弹层随切换关、收起焦点归位）与词典面（126/127/128）零回退 | §10 基线口径 + 存量 spec 全绿 |

---

## 10. 测试计划

基线口径：**纯前端票，后端零改动**——Assessment 后端全套件计数保持 445/445（CI Backend 的 Interviews 死锁 flake 是既有环境项，按台账先例口径处理，不计入本票）。Jest 基线 399，**只升不降**；新增用例入新文件 `web/src/app/features/ai-practice/practice-ticket125.spec.ts`（或就近分文件，命名随实现报告登记）。

### 10.1 会话级（假 host 注入 PracticeGradingSession，行为断言）

- **TJ-1（追问真出题）**：假 host 给 activeTake（score.recognized = 一段可辨识文本，如 `RECOG-TEXT-125`）→ `generateFollowUp()` → 断言 `aiApi.complete` 收到恰一次调用、其 prompt 含 `RECOG-TEXT-125` 原文；假返回文本 → `followUpQuestion()` 等于该文本。**判别性**：现行代码下 prompt 永远不含该文本（transcript 恒空、根本不发请求）→ 必红。
- **TJ-2（无文本反馈）**：recognized = ''（及 score = null 变体）→ `generateFollowUp()` → `aiApi.complete` 调用数 = 0、toast 收到 `t('practice.noRecognizedText')` 的当前语言值、`followUpLoading()` 全程 false。
- **TJ-3（复习带真答案）**：同 TJ-1 的 take → `addToReview()` → `knowledgeApi.createItem` 入参 `myAnswer === recognized.slice(0, 1000)`、`category === t('practice.reviewCategory')` 当前语言值；成功 toast 为 `practice.addedToReview` 的值。
- **TJ-4（复习无文本不建卡）**：recognized = '' → `createItem` 调用数 = 0、toast 为 `practice.noRecognizedText` 的值。
- **TJ-5（computed 双语）**：构造 fillerStats/speechPace/oneFix 可达的 score 夹具 → lang = en 时 `speechPace().verdict` 与 `oneFix().title/detail` **不含任何 CJK 字符**且等于 5.5 表 en 值；切 lang = zh 后重读，两 computed 自动给出 zh 值（不断言实现机制、只断言切换后取值——D5 的行为判据）；四维 label 在 oneFix title 中出现的是 `dim.*` 当前语言值。

### 10.2 普查闸（脚本 + 对账，非 Jest）

- **TJ-6**：实现期按 5.1 方法跑普查脚本（剥跨行注释后抽取），产出普查表入实现报告：5.3 表的每个已知点位有且只有一行处置记录；脚本在两主文件 + component.ts 上的「用户可见中文」命中集 ⊆ 豁免集（:216 数据表、:535 自称、注释、LLM 提示词）；新键逐键核 zh/en/fr 三值齐备（键表 diff 对照 5.5 表）。

### 10.3 模板/DOM 级（组件装配测试或等价 DOM 断言；装配不可行时的替代路径见本节末）

- **TJ-7**：渲染历史列表 + 展开报告后断言——
  1. `.rep-title` 元素不存在；报告区内文本为 Score Results 的 h3 恰 1 个且位于 `.res-head` 内；
  2. 行体点击（非行内元素区域）→ `isTakeReportOpen(id)` 由 false 变 true，再点 → 变 false；点另一行 → 展开切到该行；
  3. 点 `.hi-play` / `.hi-track` / `.hi-score` / `.hi-del` → 展开状态与 `activeTakeId` 均不变（各元素自身动作的调用另以 spy 断言发生）；
  4. 点 `.hi-view` 一次 → 展开状态恰翻转一次（不是两次、不是零次）；
  5. 行 `keyup.enter` 与 Space 路径均触发切换，Space 不产生滚动副作用（以 preventDefault 被调或等价断言）；
  6. 行与 `.hi-view` 的 `aria-expanded` 属性在开/合两态分别等于 `'true'`/`'false'`；
  7. 无分 take 行点击 → 报告区渲染且含 `.rep-empty` 空状态。
- 装配说明：`AiPracticeComponent` 依赖面大，若完整 TestBed 装配成本过高，允许以「组件包装方法 + 会话」的组合单测覆盖 2/3/4 的**状态机等价性**（行 handler 与钮 handler 指向同一组件方法的代码对照入实现报告），DOM 层断言（1/5/6/7）则由第 11 节活体 E4/E5 作主证——但须在实现报告中明示走了替代路径及理由，不许静默降级证据等级。

### 10.4 存量回归

`dictionary-ticket126.spec.ts`、`dictionary-ticket127.spec.ts`、`dictionary-ticket128.spec.ts`、`dictionary.helpers.spec.ts`、`ticket122-word-dispatch.spec.ts`、`word-click.helpers.spec.ts` 全绿不改判；与本票相邻的既有断言若有与新行为正面冲突者（尤其行点击语义类），须在实现报告中逐条列明 supersede 理由（引本稿节号），不许静默改断言。

---

## 11. 活体验证剧本（沙盒全栈 + 真实浏览器 + 截图存证；数据经应用自身流程造，不直写库）

- **E1 追问真出题（R-125-1）**：用一条已评分、recognized 非空的 take 展开报告 → 点「AI 追问一层」→ 断言：网络日志有恰一次 ai complete 请求且其 prompt 含该 take 的 recognized 原文；追问卡渲染出一条真追问（截图）。沙盒账号未配 AI 时「真出题渲染」一幕按第 12 节残余移 Mac（请求与 prompt 一幕沙盒照验——请求在到达网关前即可从浏览器侧证伪/证实）。
- **E2 无文本反馈（R-125-2）**：以 score 存在但 recognized 为空的 take（构造路径：静音/无声录音送评，或既有空识别数据）展开报告点追问钮 → 无 ai 请求发出、toast 显示当前语言的 `practice.noRecognizedText`（截图）。数据不可造时以 TJ-2 为主证、本剧本登记为残余（第 12 节），不许拿别的数据冒充。
- **E3 语言切换零残留（R-125-4/R-125-5）**：界面切 English → 依次走查：自定义题横幅、oneFix 横幅（标题/详情/加入复习钮）、追问区（钮/标签/换一个）、填充词条 + 语速 chip、逐词 tooltip、加入复习与追问失败两条 toast（可达时）→ 逐屏截图，画面内 UI 文案零中文（用户内容——识别原文、填充词本体——不在此列，截图说明中逐项标出哪些是内容不是文案）；切回中文 → 同批位置恢复中文（验 D5 重算）。
- **E4 外层标题消失（R-125-6）**：展开任一已评分 take 的报告 → 截图与 DOM 快照：Score Results 只出现一次（总分块 res-head）、`.rep-title` 查询为空；与落地前同位截图对照入实现报告。连带：点行内 hi-view 收起 → 焦点回到该钮（票 121 回归目视 + 焦点位断言）。
- **E5 整行点击（R-125-7/R-125-8/R-125-9）**：① 点行体空白处 → 展开；再点 → 收起；② 点另一行行体 → 展开切过去；③ 播放 take A 中点行 B → 报告切 B 且 A 播放不中断（与点 B 的 hi-view 钮重做一遍结果一致，两幕都留证）；④ 逐个点行内播放/进度条/评分/删除 → 展开状态不变（删除走其确认流、取消即可）；⑤ 键盘：Tab 聚焦行 → Enter 展开、Enter 收起、Space 展开（页面不滚动）；每步断言行与 hi-view 的 aria-expanded 与实际状态一致；⑥ 点一条无分 take 的行 → 报告区展开为空状态（截图）。

---

## 12. Mac-only / 环境残余（如实登记）

- **E1 的「真出题渲染」**：依赖账号级 AI 配置。用户 Mac 应用已配 DeepSeek（2026-10-23 实证、连通验过）——沙盒账号若未配， 该幕在 Mac 上随 M1/M2 批次顺带验，不单独立项；沙盒侧验到「请求发出 + prompt 含 recognized」为止。
- **E2 的空识别数据**：依赖 Azure 评分链造数（沙盒无 Azure 凭据，与票 127 的 F-S2 同源环境项）；不可造时 TJ-2 单测为主证、活体在 Mac 有空识别 take 时顺带补。
- 除上述两幕外，四项修复的全部判据沙盒可验，无其他 Mac-only 项。

---

## 13. 失败态总表

| # | 情形 | 口径 | 依据 |
|---|---|---|---|
| F-S1 | take 无 score / recognized 为空或纯空白 | 追问不发请求、复习不建卡，同键 toast 明示 | §4.2、TJ-2/TJ-4 |
| F-S2 | AI 请求失败或返回空文本 | toast 走 `practice.followUpFailed`（行为同现行、仅文案迁移），followUpQuestion 保持 null | §4.3 |
| F-S3 | 建卡 API 失败 | toast 走 `practice.addToReviewFailed`，不重试不假成功（行为同现行） | §4.3 |
| F-S4 | 某新键某语值缺失 | 不许合入（键表三语齐备是合入门槛）；运行期若仍缺，沿 i18n 既有缺键行为，不在本票另设兜底文案 | §5.5、D4 |
| F-S5 | 行点击收起时焦点 | 行本体收起后仍在 DOM，键盘路径焦点留在行上；hi-view 路径沿票 121 归位 | §7.2、TJ-7 |
| F-S6 | hi-view 与行 handler 双绑同方法 | stopPropagation 保全下一次物理点击只一次调用（重入双翻转由 TJ-7 第 4 例钉死） | §7.3 |

---

## 14. 不做清单（逐条钉死）

1. 不动评分、录音、识别（ASR/Azure）任何链路；不改 `RecordingDto` / `RecordingScoreDto` 契约（本票是纯前端消费侧修复）。
2. 不改 `reportOpen` 状态机结构：不做 per-take 展开集合、不做多条同时展开；`toggleTakeReport` / `isTakeReportOpen` / `selectTake` 三方法本体语义不改（仅删无调用方的 `toggleReport`，D6）。
3. 不动第 8 节两候选的任何代码（追问信号不加 take 隔离、不清残留）——等裁认后另行处理。
4. 不迁移存量复习卡数据（既有 category='口语练习' 的卡保持原样）。
5. 不改填充词条/语速 chip/逐词区的显示条件、结构与样式（只换文案来源）；不改 oneFix 的优先级与阈值逻辑（只换文案来源）。
6. 不动词典面与点词面任何文件（dictionary-*、word-click*、practice-dictionary.ts）。
7. 不引新 i18n 机制/库；不把 LLM 提示词、填充词数据表、注释「迁」进 i18n（5.2 豁免）。
8. 不给行加交互 role、不新增行级 aria-label（D8）。

---

## 15. 影响面 + 不变项

**影响面**（改动所及，验收须连带回归）：
- 前端：`practice-grading-session.ts`（取文本两处、无文本反馈、oneFix/speechPace 文案来源、toast 键化、删 `toggleReport`）、`ai-practice.component.html`（点位文案键化、删 `.rep-title`、行接线与 aria）、`ai-practice.component.ts`（删 `toggleReport` 包装、`collapseFocusTarget` 死分支可清）、`core/i18n/i18n.service.ts`（新增 5.5 表键）、SCSS 孤儿样式可清；新增 Jest `practice-ticket125.spec.ts` 与普查脚本产物（入实现报告）。
- 后端：**零改动**（445 基线不变的依据）。

**不变项**（逐条，交付时逐条打勾）：
1. `generateFollowUp` 的 prompt/systemPrompt 内容、maxTokens/temperature、loading 与结果信号置位顺序——除取文本来源与失败 toast 键外不变。
2. `addToReview` 的建卡入参除 myAnswer 来源与 category 取值方式外不变（title/question/topic/mastery 逐字）。
3. 票 121 焦点归位（hi-view 路径）与票 120 词弹层随切换关闭——行为不变（E4/E5 连带验）。
4. 报告区除删 `.rep-title` 外的全部结构与标题（内层 Score Results、Recognition、Word Scores、波形）不变。
5. 词典卡、点词、跟读（126/127/128 面）零改动（diff 名单核）。

---

## 附录 A：可追溯映射表（来源 → 规格节 → 验收）

| 来源条目 | 规格节 | 验收条目 |
|---|---|---|
| 评审项 1：追问恒死（取错字段） | §1.1、§4.1 | R-125-1（TJ-1、E1） |
| 评审项 1：无文本不许静默 | §2 D2、§4.2 | R-125-2（TJ-2、E2）、R-125-3（TJ-4） |
| 评审项 1 连带：加入复习 myAnswer 恒空（同根因第二处） | §1.1 C-4、§4.1/§4.3 | R-125-3（TJ-3） |
| 评审项 2：★批硬编码中文 | §1.2、§5（普查法/判定/点位表/键表） | R-125-4（TJ-6、E3）、R-125-5（TJ-5、E3） |
| 评审项 3：删外层标题行、留内层 | §1.3、§6 | R-125-6（TJ-7、E4） |
| 评审项 4：整行点击 expand/collapse | §1.4、§7.1/§7.2 | R-125-7（TJ-7、E5） |
| 评审项 4：行内按钮不误触（转述「三按钮」按代码订正为 5 元素） | §1.4 C-19、§7.3 | R-125-8（TJ-7、E5） |
| 评审项 4：展开状态与无障碍 | §2 D8、§7.4 | R-125-9（TJ-7、E5） |
| 第五项（原文无存档） | §8（候选甲/乙 + 判别法，均不在本票范围） | 无（等裁认后另立条目） |
| 票 120/121 既有行为保全 | §3、§6.3、§7.1 | R-125-10（存量回归、E4/E5 连带） |
| 基线纪律（后端 445 不变 / Jest 399 只升不降） | §10 | R-125-10 |

映射完整性自查（落笔时逐项核对）：已定位四项逐项有规格节与验收条目；第五项以「无存档 + 两候选 + 判别法」如实登记、不虚构条目；转述与代码出入两处（「三按钮」实为 5 元素、RecordingScoreDto 行号以实读为准）均已在证据表订正。

## 附录 B：评分史

- **R1**：待主 agent 另行派独立评分员（只看原始需求 + 本稿 + skill 判据）。未过 9.9 前本稿不作定稿、不登记实现。
