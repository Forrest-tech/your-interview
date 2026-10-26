# 票 125 规格独立评分 R1

- **待评方案**：`~/workspace/ticket125/SPEC.md`（R1，449 行）
- **原始需求**：用户 2026-10-24 对 AI Practice 页的截图评审五项（追问恒死 / 批硬编码中文 i18n / 删 Score Results 外层标题行 / Take 整行点击 / 第五项候选与判别、不凑数不预修）
- **核查基线**：`develop-new-features` tip `86a538f`（规格自称基线；两主文件与 `f843efa` 的 md5 一致性已实测成立，见末节）
- **核查方法**：规格全文逐节读 + 基线代码逐处复核（`git show 86a538f:<path>` 行号/grep/剥注释全目录 CJK 重扫）；只看原始需求 + 本稿 + 代码实据，未看作者其他工作记录

## 总分：9.95 / 10 — PASS（≥9.9 过门）

本稿可作定稿、可登记实现。唯一扣分项 K-1（−0.05）为证据行号勘误级小瑕，折入实现首步，不重开评分轮。

## 扣分清单

| # | 扣分 | 问题 | 证据 |
|---|---|---|---|
| K-1 | −0.05 | 证据行号精度：三处第二锚行号与基线实测不符，但实质全部成立、无任何决策/验收/实现步骤依赖这些行号 | 见下 |

K-1 逐项：

1. **C-12 三处 host.t 先例行号全错**：规格称 `practice-grading-session.ts:259 / 262 / 396`；实测 86a538f 与 f843efa 均为 `:256`（`rec.noAudio` toast）、`:260`（`retryLater` toast）、`:385`（`prevTakeDiscarded` toast）。三处先例本身确实存在，口径不一致的论断成立。
2. **5.3 表 pace 单位行**：规格称 html `:752`；实测 `{{ pace.wpm }} 词/分钟 · {{ pace.verdict }}` 在 `:754`（pace-chip span 开于 :752，文本行在 :754）。
3. **C-10 `.replace` 先例**：规格称 html `:675-676`；实测 `.replace` 调用在同 cost-chip 块的 `:672` 与 `:679`，:675-676 为该块内 [title] 行。先例本身存在。

处置（折入实现首步，不重开评分轮）：实现报告以方法名/选择器重锚并订正上述行号；规格自定锚点规则（方法名/选择器为第一锚、行号为第二锚）下，此类漂移不改变任何语义口径。

## 亲验通过面（代码实据，非采信规格自述）

### 根因链 §1.1 / §4（行号逐字吻合）

- `RecordingDto` 字段全集 `id / materialId / durationSeconds / sizeBytes / contentType / createdAt / score / version`（`practice-api.service.ts:101-111`），无 `transcript`/`text`/任何文本字段；`RecordingScoreDto.recognized` 在 `:85`。
- `addToReview` `:305-323`：取文本 `:309`（`(take as any)?.transcript ?? ''`）、建卡 `:311-318`（`myAnswer: transcript.slice(0, 1000)`）、成功 toast `:319`。
- `generateFollowUp` `:325-351`：取文本 `:328`（`transcript ?? text ?? ''`）、静默 return `:330`（`if (!transcript.trim()) return;`）；重入守卫 `:327` 原样。
- 追问区 `ai-practice.component.html:618-634`：钮 `:620-622`、loading 条 `:624-626`、结果卡 `:627-633`，且位于 `@if (activeTake()?.score)` 闸内——「有分才看得见钮、点了恒死」100% 复现链成立。
- `recorder.service.ts`：`Recording` `:9`、`RecordingScore.recognized` `:50`（注释「识别出的文本」）。

### 改法钉死（§4）

- 类型化访问 `take?.score?.recognized ?? ''`，两处旧 `as any` 形态须 grep 自证零残留；切片长度不变（复习 1000、追问 prompt 2000，代码实测一致）。
- 无文本判定四情形（take null / score null / recognized 空 / 纯空白）合一；追问不发请求、不置 loading、不改现值，复习不建卡，同键 `practice.noRecognizedText` toast；`:307-308` 题目为空守卫排前保留。
- prompt/systemPrompt（`:337-338`）实测为 LLM 提示词（英文指令），不动它、不入 i18n 面的口径成立。

### i18n（§5）

- 5.3 表 HTML 10 处与 session 15 行逐项实存（除 K-1 两处行号外全部吻合）：HTML `:277 / :278 / :611 / :621 / :629 / :631 / :744 / :748 / :769`；session verdict `:246-248`、oneFix title `:272` / detail `:273`、四维 label `:277-280`、fixes `:287-290`、priority `:294`、fallback `:295`、category `:316`、toasts `:319 / :321 / :344 / :347`。
- 本人剥注释重扫 `features/ai-practice/` 全目录产品文件：主文件命中集与 5.3 表完全一致；其余产品文件剥注释后 CJK 命中为 0（`material-transfer.*`、`practice-tts-player.ts`、`practice-dictionary.ts`、词典三产品文件皆 0）。C-9「两主文件 + component.ts:535 之外用户可见中文为 0」的操作性结论成立。
- 5.2 豁免逐项有据：FILLERS 数据表（session `:216`，中文词确为匹配数据）、LLM 提示词（`:337-338`）、语种自称（component.ts `:535`，且规格明标注明可推翻）、注释；存疑从严规则已钉死。
- 5.5 键表 26 键 + 1 条件键：基线无键名冲突（grep 0 命中）；`dim.*` 复用已验键存在（`dim.pronunciation` 在 i18n `:185`，胶囊区确用 `dim.*`）；占位符与 `.replace` 先例一致。
- i18n 机制锚点：`_lang` signal `:3033`、`t()` `:3059`、`tn()` 的 `{n}` 替换 `:3071`，全部吻合。

### 外层标题（§6）

- 结构实存：section `@if (isTakeReportOpen(r.id))` → `.rep-title` `:584`（图标 `:585`、外层 h3 `:586`、rep-toggle 钮 `:590`、文案三态 `:591`）；内层 `.res-head` `:660` / h3 `:661`；其余 `.res-head` `:642 / :730 / :805`；空状态 `.rep-empty`（p `:827`、文本 `:829`）；模板 `:598` 的 `@if (reportOpen())` 闸。
- rep-toggle 双态不可达推理成立：section 渲染蕴含 `reportOpen() === true`，故 `:591` 三态恒取「收起报告」一支；钮调 `toggleReport()`（component `:963-970` → session `:711-713`）连同 section 一起关闭，与 hi-view 钮功能重复。
- 连带清理范围封顶正确：两端 `toggleReport()` 删后零调用方（grep 自证要求已写）；`collapseFocusTarget()` section 分支（`:1003-1013`，`closest('section.report-section')` 实存）可清、`.hi-view` 分支与 `refocusAfterCollapse()` 保全；`reportOpen` 状态机本体不动。

### 整行点击（§7）

- 行本体 `:525-529`（`(click)=selectTake`、`tabindex="0"`、`keyup.enter=selectTake`）实存；5 元素 `:531 / :547 / :556 / :562 / :571` 全部带 `$event.stopPropagation()`（逐个实读确认）。
- 组件包装层 `selectTake` `:971-975` 与 `toggleTakeReport` `:976-985` 均含 `closeWordPopOnSwitch`（票 120 保全成立）；会话 `selectTake` `:735-741`（幂等 + `stopPlayback()`）、`toggleTakeReport` `:744-751`（无停播）、`isTakeReportOpen` `:754-756` 全部吻合——§7.2 行为差表逐项与代码一致，「不停播」代价已如实登记并论证为与 hi-view 钮同义的必然结果。
- hi-view 重入陷阱（行与钮同方法、stopPropagation 为正确性必需、TJ-7 专测恰翻转一次）已点名；D8（aria-expanded、行不加 role、Space 补齐 + 防滚页）与需求「aria-expanded 钉死」一致。

### 第五项（§8，符合「不凑数、不预修」）

- Q-125-1（rep-toggle 双态不可达）：证据即 C-15 实测链；「删外层标题行即连带消除」的推论标注为「大概率」，非断言。
- Q-125-2（追问结果跨 take 残留）：信号会话级（`:301-302`）；全仓写入点仅在 `generateFollowUp()` 内（`followUpQuestion.set` `:333 / :343 / :346`，loading `:332 / :349` 同函数内）；`selectTake` / `toggleTakeReport` / `focusTake`（`:353-356`，仅设 `activeTakeId` + `pickedWord`）均不清它；追问区只受 score 闸控制、不校验归属 take——证据链完整，活体判别法可执行。
- 「恒死时根本生成不出追问，故评审时第五项不太可能是它」的推理如实记录为参考层，非结论。
- 非候选排除（`:307` 题目为空守卫现行 UI 不可达、仅登记备查）理由成立。
- 两候选均明确不在本票范围、无虚构条目、无预修代码，符合原始需求⑤。

### 验收、测试与诚实边界

- R-125-1…R-125-10 与 TJ-1…TJ-7 / E1–E5 映射完整（附录 A 逐项可追溯；「三按钮」转述与代码不符处已按代码订正为 5 元素）。
- TJ-7 装配替代路径（状态机等价性 + 活体主证）明示申报、不许静默降级；回归清单含 126/127/128/122 相关 spec；与新行为正面冲突的既有断言须逐条 supersede 说明。
- 基线口径与当期算术一致：Jest 399（399+11+12+31+23 = 476，与现 tip 实测一致）、Assessment 445；CI Interviews 死锁 flake 按既有口径排除。
- 诚实边界到位：E1「真出题渲染」与 E2 空识别数据登记为 Mac/残余、TJ 为主证、「不许拿别的数据冒充」已写明；§12 两幕外无其他 Mac-only 项；§13 失败态与 §14 不做清单（不做 per-take 展开集、不动两候选、不迁存量数据、不动词典面、不引新机制）逐条自洽。
- 两主文件 86a538f 与 f843efa 的 md5 一致性声明实测成立（session `3b00a93c…`、html `74f564fc…`）。

## 观察项（不扣分）

- **O-1**：C-9「其余命中全是注释」表述偏松——词典 spec 文件与部分测试文件含中文测试描述串（非注释），但均非用户可见 UI 文案；TJ-6 的门禁口径本就限定在两主文件 + component.ts，且该范围内已验零残留。实现期普查表按 5.2 标准逐条判定即可。
- **O-2**：C-9「词典面三文件零命中」宜理解为三件词典产品文件（`dictionary-card.component.ts`、`dictionary-shadow.ts`、`dictionary.helpers.ts`），三者剥注释后 CJK 命中确为 0；实现期以 TJ-6 口径为准。
- **O-3**：D2「复习无文本不建卡」是规格在需求「无文本给 i18n 反馈」之上的加严决策（理由：卡的核心内容即回答，空卡是静默丢失换壳），已在 D2 与 §4.2 明示并论证，非暗改；实现须照此执行。
- **O-4**：§5.4 category「写入时语言值」口径（英文界面建卡即英文 category、存量不回溯）是规格的明示取舍；跨语言分组碎片化风险已由「存量不回溯」登记在案，实现照此执行。

## 结论

**9.95 / 10 — PASS。** 规格 R1 过门，可作定稿、可登记实现。K-1 行号勘误折入实现首步（实现报告以方法名/选择器重锚并订正），不重开评分轮。
