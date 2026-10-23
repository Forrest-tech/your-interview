# AI Mock（AI 模拟面试）现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/mock`（列表/新建）与 `/mock/:id`（会话页）：AI 扮演面试官按模式出题 → 用户作答（文本/录音转写）→ 逐题六维评分 → 会话总评。
- 前端 `web/src/app/features/mock/`；后端 Assessment 服务 sessions 组端点（`src/Services.Assessment/Api/AssessmentController.cs:29-176`），领域 `Domain/AssessmentAggregates.cs`。

## 核心实体与数据真源（Assessment 库，按 UserId 隔离，MockSession 带软删过滤器——票 66）
- **MockSession**：Title、**Mode**、Topic、CompanyStyle、TargetQuestionCount、Language、Status、OverallScore/OverallSummary/PriorityAction、StartedAt/CompletedAt。
- **SessionMode 四值（唯一合法集，逐字枚举名）**（`AssessmentAggregates.cs:30`）：`TopicDrill`=按主题 / `CompanyStyle`=公司风格 / `WeaknessFocus`=薄弱点强化 / `FullLoop`=完整模拟（技术+系统设计+行为混合）。建会话解析口径 = 纯函数 `SessionModeParsing`（`Application/SessionModeParsing.cs`）：逐字匹配（不区分大小写），非法值（含旧前端误值 `FullMock`、数字、空）一律 400 字段级错误，**不静默回退**（票 63 修复后的现状；`FullMock` 仅保留为前端显示别名映射到同一 i18n 键）。
- **SessionStatus**：Draft=0 / InProgress=1 / Completed=2 / Abandoned=3（同文件 :17）。
- **MockQuestion**：Sequence、QuestionText、Type（Technical/SystemDesign/Behavioral/Coding，同文件 :313）、ExpectedPointsJson、Difficulty、**State**（Asked/Answered/Scored/Skipped，同文件 :43）、AnswerText/AnswerAudioPath/AnswerTranscriptJson、Comment/RecommendedAnswer/BetterStructure、FillerWordsJson、Scores。
- **六维评分 DimensionScores**：Pronunciation / Fluency / SentenceIntegrity / Structure / TechnicalDepth / Relevance（0-100 clamp），与 SharedContracts 分析契约同维度——真实面试（Interviews）与 Mock 分数可同雷达对比（领域注释明示）；逐维问题清单 DimensionIssue（Dimension/Title/Detail/Evidence/Suggestion/Severity）。

## 主要流程
1. 新建：选模式（四选项文案走 i18n `mock.mode*` 键，票 13 补齐三语后的现状）→ POST `sessions`（MockManage 权限）→ 跳会话页。
2. 答题循环：POST `sessions/{id}/questions` 加题 → `.../answer` 提交回答 → `.../score` 评分，或 `.../skip` 跳过。
3. 收尾：POST `sessions/{id}/complete`（出总评）或 `/abandon`；DELETE 软删。
4. 智能建议：`GET /api/assessment/suggest`（`SuggestSessionCommand`，AssessmentController.cs:160）按薄弱面给出建议会话（WeaknessFocus 向）。
5. 统计：`GET stats` / `GET dimensions` 供列表页与（经 Analytics 事件）雷达消费——Mock 分数进 Analytics 走 `MockAnswerSubmitted` 集成事件，不直写 Analytics 表（见 `analytics-spec.md`）。

## 与既定规则对照结论
- 模式键合法集单一事实源 = 后端 SessionMode 枚举 + SessionModeParsing；前端提交值与之逐字一致（票 63 后对齐）。
- 评分维度与真实面试同构：符合「同源可比」口径（同一六维契约）。

## 现状与规则冲突
- **CONF-MOCK-1（→ AUDIT COD-21，票 48/72 未实施）**：会话页「下一题」仍是投机性调用——前端先试 `POST /api/assessment/sessions/{id}/next-question`（后端**无此路由**，`mock-session.component.ts:266-275` 注释自认），404 后本地降级。每次操作多一发必失败请求；将来若同名端点上线且语义不同会被静默误用。
- **CONF-MOCK-2（→ AUDIT OBS-1，未判定）**：会话建后题目生成依赖 LLM/分析 worker（RabbitMQ + 用户 AI 配置），无可用模型时题面为空（「No questions yet」）——环境受限未判定为产品缺陷，本规格仅记录依赖链：Mock 出题不可用时先查 AiGateway 配置与 worker，而非前端。
- **CONF-MOCK-3（→ AUDIT FUN-06，已修复，备查）**：模式下拉曾直出 i18n 键名；票 13 已补齐 28 个缺失键三语 + 会话页标签映射，现行以 `mock.component.ts:77-81` modeOptions 为准——仅索引留痕。
