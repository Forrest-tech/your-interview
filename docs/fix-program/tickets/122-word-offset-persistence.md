# 票 122 — 逐词时间偏移落库：重载报告的点词回放（seek）可用化

来源：Forrest 2026-10-23 批准立项。缺陷由票 120 规格验证设计时发现、经独立
评分员（SCORE-120-R1）与本轮修订作者**各自独立查证代码属实**（见下「代码
核实」，行号以 `~/workspace/wt-t120` 当前代码为准）。

## 问题

AI Practice 评分报告里，点一个填充词本应回放到该词在录音中的时刻
（seek）。但这个行为**只对刚评完、还在内存里的那条 Take 成立**；页面一旦
重载（历史 Take 从落库数据重读），点填充词永远只打开 word-pop 弹层，
seek 结构性不可达——因为逐词的时间偏移在保存时被丢掉了。

## 代码核实（本轮逐条对过，非转抄）

偏移在源头是有的，丢失发生在**保存入参的绑定环节**：

| 环节 | 位置 | 现状 |
|---|---|---|
| 运行时评分词条 | `PronunciationAssessor.cs:575` `WordScore` | 带 `OffsetMs = 0`、`DurationMs = 0` 两字段（默认值参数） |
| assess 响应下发 | `AssessmentController.cs:283-291`（:288 `offsetMs = w.OffsetMs`） | 逐词下发 word/accuracy/errorType/**offsetMs/durationMs**，前端拿得到 |
| 前端 assess 侧类型 | `web/src/app/core/recorder/recorder.service.ts:52` | 词类型已声明 `offsetMs?: number; durationMs?: number` |
| 前端保存路径透传 | `recorder.service.ts:1020`（persistScore）、`:1067`（flushPendingScore）、`web/src/app/core/api/practice-api.service.ts:418` | 三处均为 `words: score.words` 整组透传、无字段裁剪——偏移在进门前未被任何一层裁剪，丢失点唯一 = WordScoreIn 绑定（R2 评分员 S122-1 补核，2026-10-23） |
| **保存入参（丢失点）** | `AssessmentController.cs:903` `WordScoreIn` | **只有 Word/Accuracy/ErrorType 三字段**。前端保存时即便把 offsetMs 放在请求体里，STJ 绑定也直接忽略未知属性——偏移在进门时就没了 |
| 保存路径 | `AssessmentController.cs:554-563`（:563 `JsonSerializer.Serialize(body.Words)`）→ `SaveRecordingScoreCommand.WordsJson`（`PracticeRecordingHandlers.cs:298`、:320 落 `RecordingScore.WordsJson`） | WordsJson = 保存入参列表的逐字序列化；入参没有的字段，库里永远不会有 |
| 落库读出 DTO | `PracticeDtos.cs:43` `WordScoreDto` | 同样只有三字段 |
| 重载路径 | `PracticeRecordingHandlers.cs:98-104` `DeserializeWords`（ToDto :91 与列表 handler :354 共用） | 按三字段 DTO 反序列化 WordsJson——即便库里有偏移也读不出来（双向都堵） |
| 前端分发 | `ai-practice.component.html:755` `(click)="isFiller(w.word) && w.offsetMs ? replayAt(w.offsetMs!) : pickWord($index)"` | 重载词条 `w.offsetMs` 恒 undefined → 恒走 pickWord |
| seek 本体 | `practice-grading-session.ts:253-259` `replayAt` | `audio.currentTime = max(0, offsetMs/1000 − 0.5)`，本身无问题，缺的只是数据 |

## 目标

把 OffsetMs/DurationMs 沿既有路径 additive 地补齐两处契约，使**新保存的**
评分在重载后逐词带偏移、点填充词走 replayAt 回放；**已落库的旧记录**保持
今天的行为（点词开弹层），不崩、不假 seek、不伪造回填。

## 改动（仅两处后端契约 + 至多一处前端类型）

1. **保存入参** `WordScoreIn`（AssessmentController.cs:903）补两字段：
   `double OffsetMs = 0, double DurationMs = 0`（带默认值的位置记录参数，
   与运行时 `WordScore` 的既有写法同款）。效果：新客户端传来的偏移被绑定
   保留；旧客户端不传 → 默认 0，向后兼容。保存路径 :563 是对入参列表的
   逐字序列化，**零管线改动**即自动进 WordsJson。
2. **读出 DTO** `WordScoreDto`（PracticeDtos.cs:43）补同两字段（同为
   默认值参数）。STJ 对带默认值的构造参数按可选处理：旧 WordsJson 里
   没有这两个键 → 绑定默认值 0（见「向后兼容」）；新 WordsJson → 原值
   读出。两个读路径共用 `DeserializeWords`，一处改动全覆盖。
3. **前端**：模板与分发表达式（html:755）、`replayAt` 一字不动。若实现时
   发现承载落库分数词条的前端类型未声明 offsetMs（assess 侧类型
   recorder.service.ts:52 已声明），仅许给该类型补
   `offsetMs?: number; durationMs?: number` 两个可选字段——纯类型补记，
   不改任何运行时代码。

不动：数据库 schema 与迁移（WordsJson 是既有文本列，内容形状变化不惊动
表结构）、SaveRecordingScoreCommand/Handler 逻辑、assess 端点、评分逻辑、
票 120 的 word-click 分发语义与提示语规则、票 121 的模板位置。

## 向后兼容（旧记录行为钉死）

- **旧记录重载**：WordsJson 为三字段对象数组 → DTO 两新字段得 0 →
  前端 `w.offsetMs` 为 0 → 分发表达式的真值判断走 pickWord 弹层——与
  今天逐字相同的行为。无崩溃路径：DTO 默认值兜底在前，
  `DeserializeWords` 既有的 try/catch → 返回空数组降级在后（该守卫
  本票不动，回归测试覆盖之）。
- **绝不假 seek**：偏移 0 在分发语义里就是「无偏移」，旧记录的词永远
  不会被当成「0ms 处」去 seek——seek 到一个伪造的时刻比不开 seek 更坏。
- **不回填**：旧 Take 的偏移从未在任何地方被捕获过，无法重建（拿原音
  频重跑 Azure 评估是重新评分、数字都会变，不是回填）。旧记录保持弹层
  行为，除非用户对该条重新评分并保存。规格层面明记此界，不许实现时
  「顺手」造数。
- **0ms 边界注记**：真实偏移恰为 0ms 的词（录音最开头的词）在现行真值
  分发下同样落 pickWord——这是**既有语义**（刚评完的内存态条今天也是
  如此），本票既不修复也不恶化；在此留痕，防后人当新 bug 重查或在本票
  里偷改分发语义。

## 前端分发（偏移在重载条上就位之后）

表达式不变、行为面扩大：重载条上填充词且 offsetMs > 0 → `replayAt` →
`audio.currentTime = max(0, offsetMs/1000 − 0.5)` 跳到该词前 0.5s 播放；
非填充词 → pickWord 弹层（如常）；填充词 offsetMs = 0/缺失（旧记录）→
pickWord。与票 120 的衔接：S120-1 的提示语规则是数据驱动的（当前词条中
存在带 offsetMs 的填充词才显示回放提示）——本票落地后，**新保存的重载条
将满足该条件、提示语如实恢复显示**；旧记录继续隐藏。两票规则不冲突，
无需互相改字。与票 121 无耦合：121 只挪模板位置，不碰数据路径。

## 自审记录（被否决的备选）

- **备选 A：给逐词数据建子表/加列**——词条只随其评分整读整写，从无按词
  查询；WordsJson 文本列就是为此设计的。建表 + 迁移换不来任何查询收益，
  只添回归面。否决。
- **备选 B：读时重算偏移（重跑对齐/评估）**——没有落盘的音频侧时间源，
  重算等于重新评估：花额度、且分数口径漂移。否决。
- **备选 C：只补 OffsetMs 一个字段**——DurationMs 与 OffsetMs 在运行时
  记录、assess 响应、前端类型里处处成对出现；只存一半等于亲手再造一个
  同型的「第二个缺口」。两字段同补的边际成本为零。否决。

## 验证

- 后端单测（Services.Assessment 测试工程，xunit console runner 口径）：
  (a) **持久化往返**：带偏移的词列表按保存路径同款序列化（对照
  controller :563 的做法）生成 WordsJson → `DeserializeWords` 读回 →
  逐词断言 OffsetMs/DurationMs 与原值一致、三老字段不变；
  (b) **保存入参绑定**：含 offsetMs/durationMs 的请求体 JSON 绑定
  `RecordingScoreBody`/`WordScoreIn` 后值完整；不含两字段的旧版请求体
  绑定得 0、不报错；
  (c) **旧库形状**：三字段对象组成的 WordsJson 字符串（照旧记录形状
  造 fixture）→ `DeserializeWords` 得 DTO 列表，OffsetMs = 0、
  DurationMs = 0，其余字段逐字无损、无异常；
  (d) **降级回归**：畸形 WordsJson 仍走既有 catch → 空数组（票 120
  已知的既有守卫，不许因本票改 DTO 而破）。
- 前端 Jest：票 120 的 `word-click.helpers` 分发用例补重载语境三例——
  重载填充词 offsetMs > 0 → replay；重载旧记录填充词（offsetMs = 0/
  缺失）→ pick；非填充词带 offsetMs → pick。（若实现顺序上本票先于
  helpers 落地，则同三例改在组件 spec 里对模板分发断言，二选一在实现
  记录里写明；默认假设票 120 先行。）
- 活体（前端 4400，禁 4200）：
  1. 经 API 保存一条**带偏移**的新评分（words 里给插入填充词 "um" 写
     offsetMs = 3200），抓存下的 WordsJson 片段存证含 offsetMs；
     **整页重载**（走落库读路径，非内存态）→ 展开该条 → 点 "um" →
     断言音频 `currentTime` 落在 2.7s ± 0.3（= 3.2 − 0.5）且 word-pop
     未打开；截图存证。注（R2 评分员 S122-1）：本路经 API 直存、绕开
     前端保存路径，其有效性建立在上表已核实前提（三处 `words:
     score.words` 整组透传）之上——真实用户流程（录音 → 评分 → 前端
     保存）中偏移抵达保存端点已由该前提保证。
  2. 旧种子条（保存时无偏移）重载 → 点其填充词 → word-pop 打开、
     currentTime 不动、console/pageerror = 0；截图存证。
  3. VERIFY-122.md 汇总 1/2 两路与 WordsJson 证据。
- 既有套件全绿：Assessment 后端套件、前端 Jest、tsc 0。

## 触碰文件

- `src/Services.Assessment/Api/AssessmentController.cs` —— 仅 `WordScoreIn`
  一处（:903）补两字段。
- `src/Services.Assessment/Application/PracticeDtos.cs` —— 仅 `WordScoreDto`
  一处（:43）补两字段。
- 后端测试工程 —— 新增上列 (a)–(d) 用例。
- 前端 —— 至多落库词条类型的两个可选字段（见改动 3）；Jest 用例并入
  票 120 的 helpers spec（或组件 spec，见验证）。

## 评分记录（2026-10-23 12:21 门，12:31 强化独立性）

- 本 SPEC 与票 120/121 的 R2 修订版同批送主 agent 派独立评分员评分；
  ≥9.9 且主 agent 明示通过后，才登记票 122 并实施。评分轮次由主 agent
  回传后逐轮记入本节。
- **独立评分 R2（冷评）**（主 agent 派生评分员，SCORE-122-R2.md）：
  **9.95 / 10 — PASS（过 9.9 门，可登记并实施）**。维度：需求覆盖
  10.0 / 代码主张准确性 10.0 / 契约设计 10.0 / 测试完备性 9.8 / 范围
  纪律 10.0。唯一扣分 S122-1（−0.05）：承重前提「前端保存路径整组
  透传 words」未进代码核实表、活体 1 绕开该路径——评分员已代为核实
  三处透传属实，修复 = 补引用 + 活体 1 前提注记，已于登记前折入本
  SPEC（见代码核实表「前端保存路径透传」行与验证·活体 1 注）。
