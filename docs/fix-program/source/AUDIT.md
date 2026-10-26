# your-interview 全系统严格审查清单（2026-10-21）

- **审查基线**：远端 `develop-new-features` tip **`a4fbe5a986aa66a96c913eff717e25ea333d4eaa`**（main `329d72e` 未动）。工作区有未提交的在飞改动（playbook detail/intake、ai-setting、tracker 前端与 AiGateway/Interviews 后端），涉及这些文件的发现标注「区域在改」。
- **方法映射（Skills for Real Engineers，按原方法执行）**：
- 需求轴 = code-review 的 **Spec 轴**：逐条对定稿/既定规则判定，口径只有 **符合 / 不符合 / 规格缺失** 三种，无"基本符合"。
- 代码轴 = code-review 的 **Standards 轴**：以仓库既有代码规范与该 SKILL.md 的 Fowler smell 基线为准绳（smell 类均为判断性发现并标注）。
- 功能轴 = **diagnosing-bugs 完整纪律**：每个缺陷必须有最小复现（种子数据 + 操作步骤 + 实际结果 vs 期望结果）；复现不了的一律降入「未复现观察」，不占正单编号。
- 架构轴 = **improve-codebase-architecture 的深化视角**：只报有具体耦合/浅模块量化证据的，用 module/interface/depth/seam/adapter/leverage/locality 与删除测试表述。
- 组织与定级 = **triage**：S1 阻断 / S2 严重 / S3 一般 / S4 建议，每条附一句定级理由；同根因合并为一条、列全受影响点，不拆条凑数。
- **证据纪律**：每条正单发现至少带一种可核查证据（文件:行号 / 复现步骤 / 截图或日志路径）。三条 S1（REQ-01、COD-01、COD-04）已由协调者亲自读码复核成立。
- **合并说明**：跨轴同根因已合并，被合并编号在总表中以「→ 并入 XXX」标注，不重复计数。合并组：①迁移缺陷 REQ-01⊃FUN-01；②localStorage 孤岛 COD-10⊃COD-11/ARC-05/REQ-03；③简历双实现 ARC-04⊃REQ-04/COD-16（简历部分）；④AiGateway 双客户端 ARC-10⊃COD-16（客户端部分）；⑤JSON 大字段 ARC-06⊃COD-18；⑥巨型文件 ARC-01/02/03⊃COD-19（i18n 部分→ARC-13）；⑦Mock 模式键 FUN-06⊃REQ-07①、硬编码中文 FUN-07⊃REQ-07②-⑤；⑧趋势区块 FUN-08⊃REQ-13；⑨Resume 文本维护 FUN-10⊃REQ-06；⑩解析缺口 REQ-09⊃FUN-09；⑪模板网关 ARC-17⊃COD-17（网关部分）。
- **总计数（合并后正单 55 条）**：S1×3（REQ-01、COD-01、COD-04）｜S2×20｜S3×25｜S4×7。另：persona 已修确认 14 条、已查无发现 28 面、未复现观察 8 条。

---

## 总表（编号 | 轴 | 严重度 | 模块 | 一句话）

| 编号 | 轴 | 严重度 | 模块 | 一句话 |
|---|---|---|---|---|
| REQ-01 | 需求/功能 | S1 | Assessment / AI Practice | 跨 schema 迁移无守卫使全新环境迁移链断裂，AI Practice 整模块 500 |
| COD-01 | 代码 | S1 | Interviews/Knowledge/Jobs | 三服务核心实体无 UserId、全局共享，任一注册账号可读写他人全部数据 |
| COD-04 | 代码 | S1 | Interviews 轮次 | 轮次保存整字段无条件覆盖，前端部分字段保存即静默清空转写/邮件/备考题 |
| REQ-02 | 需求 | S2 | Tracker↔Playbook | 轮次进度不回写投递状态，Dashboard 与 Tracker 统计同屏矛盾（F-09 残留） |
| REQ-16 | 需求 | S2 | 规格治理 | 七个模块零定稿/规格文件，实现无验收基线 |
| COD-02 | 代码 | S2 | Analytics | ability 接口可用请求体 UserId 替他人写能力快照（可写型 IDOR） |
| COD-05 | 代码 | S2 | 全服务并发 | xmin 并发异常无映射，并发编辑一律 500 而非 409 |
| COD-06 | 代码 | S2 | Interviews 契约 | 转写字段名 transcriptText/fullText 对不上，v2 实录正文恒空 |
| COD-07 | 代码 | S2 | Interviews 契约 | 前端调不存在的 transcribe 路由，点开始转写必 404 |
| COD-10 | 代码/架构/需求 | S2 | 全前端 | STAR/复盘/收藏/提问/分类/热力图等领域数据只存 localStorage，无后端 seam |
| FUN-02 | 功能 | S2 | Tracker | 手动建单/编辑填的 JD 与备注被静默丢弃 |
| FUN-03 | 功能 | S2 | Tracker↔Playbook | 已有条目的投递点 Prep 重复新建条目，数据散落两处 |
| FUN-04 | 功能 | S2 | Jobs Smart paste | JD 约 ≥20K 字符保存 500（列上限 20000），整单丢失无提示 |
| FUN-05 | 功能 | S2 | Tech Stack | 不填 Interview question 必 400，但 UI 只标 Title/Category 必填 |
| FUN-06 | 功能/需求 | S2 | AI Mock | 模式下拉三语均为原始 i18n 键直出（F-12 未修） |
| ARC-01 | 架构 | S2 | 前端 Tracker | 单文件 3575 行塞 4 个组件类，全仓第一热点，无 locality |
| ARC-02 | 架构 | S2 | 前端 AI Practice | 3645 行单类巨型组件，五职责挤一个实现 |
| ARC-03 | 架构 | S2 | 后端 Application 层 | 每服务全部用例压在一个 Handler 文件（Interviews 2857 行/57 类） |
| ARC-04 | 架构/需求/代码 | S2 | 简历数据 | 「用户主简历」4 个落点、两套逐字复制的后端实现，一套前端从不调 |
| ARC-06 | 架构/代码 | S2 | Interviews 模型 | 16 个 *Json 大字段无 schema/版本，解析散落各层 |
| ARC-07 | 架构 | S2 | 轮次概念 | InterviewRound 两服务各建一表，靠自由文本关键词事件勉强同步 |
| ARC-08 | 架构 | S2 | 测试面 | 约 1.9 万行服务代码零测试，最热的纯函数解析器 0 用例 |
| ARC-09 | 架构 | S2 | Program.cs 配置 | JWT 配置 7 处逐字复制且已漂移，Kestrel 上限口径不一致 |
| REQ-05 | 需求 | S3 | Tracker Documents | 文本简历回落缺失：只有文本简历时 Documents 仍显示 No resume yet（F-17） |
| REQ-08 | 需求 | S3 | Dashboard/Analytics | 无数据时六维全 0 仍列 Top 3 to improve 并渲染 Best（F-13） |
| REQ-09 | 需求/功能 | S3 | Smart paste 解析 | 规则解析对 JD 形态敏感：内联 Benefits 解不出、摘要混元数据、公司名误判（F-14 残留） |
| REQ-10 | 需求 | S3 | Playbook 轮次 | 轮次编号按创建先后非日期，默认 stage/结果无提示（F-19） |
| REQ-11 | 需求 | S3 | Tracker | 批量面板仍占近半屏纵向大面板（F-15） |
| REQ-15 | 需求 | S3 | Tracker 抽屉 | Overview 字段网格无独立 Work mode 字段，与字段齐全口径冲突 |
| REQ-17 | 需求 | S3 | 规格治理 | Tracker 只有竞品 recon 无自有定稿；日历无独立定稿 |
| REQ-18 | 需求 | S3 | Analytics | Analytics 是快照投影非同源视图，事件缺失即与真源分叉 |
| COD-03 | 代码 | S3 | Jobs | DeleteCoverLetter 唯一不校验归属，可删他人求职信 |
| COD-08 | 代码 | S3 | Interviews 契约 | toggleAction 调不存在端点且吞错，待办勾选假成功 |
| COD-09 | 代码 | S3 | EF 迁移 | AddRoundDetails 缺 Designer.cs，根目录留手工补列脚本绕迁移历史 |
| COD-12 | 代码 | S3 | Assessment | ListSessions 对空 Issues 序列 First() 抛异常冒 500 |
| COD-13 | 代码 | S3 | Interviews | AttachAsset 坏 JSON 抛未映射异常 → 500 而非 400 |
| COD-14 | 代码 | S3 | 全服务启动 | 迁移/种子失败仅记日志继续启动，服务"假健康"（REQ-01 的放大器） |
| COD-15 | 代码 | S3 | Assessment 权限 | 写操作与付费 TTS 挂在只读权限 MockRead 上，Viewer 可写可消费 |
| FUN-07 | 功能/需求 | S3 | 全局 i18n | EN/FR 界面多模块硬编码中文与后端中文消息直出 |
| FUN-08 | 功能/需求 | S3 | Analytics | 趋势接口后端不存在，区块恒显示不可用（开发者文案） |
| FUN-10 | 功能/需求 | S3 | Resume | 已存文本简历不可见不可编辑、无粘贴入口；空存 400 且话术黑话（F-16 残留） |
| ARC-10 | 架构/代码 | S3 | AiGateway 客户端 | 同名客户端 Jobs/Interviews 各写一份，DTO 与异常重复定义 |
| ARC-11 | 架构 | S3 | 前端聚合 | 组件硬编码 181 处 /api 路径自行拼多服务数据，服务 seam 在前端泄漏 |
| ARC-12 | 架构 | S3 | 文件存储 | 「落盘+库引用」实现三遍，配置键与根目录规则各写各的 |
| ARC-13 | 架构/代码 | S3 | i18n 机制 | 三语文案 5 套并行机制共 3235 行，组件常同时引两套 |
| ARC-14 | 架构 | S3 | 结果判读 | 结果→outcome 映射 TS/C# 各一份、口径不同 |
| ARC-15 | 架构 | S3 | SharedContracts | 契约模块放 LLM 实现；Worker 另维护同构契约副本靠注释对齐 |
| ARC-16 | 架构 | S3 | Playbook 前端 | 三代实现共存近万行，误改旧实现风险常态化 |
| REQ-12 | 需求 | S4 | Web 基础 | 浏览器标签标题恒为「Web」（F-21） |
| REQ-14 | 需求 | S4 | Tracker/Tech Stack | 多处删除仍用原生 confirm()、目标用 prompt()，与 F-02 修复标准不一致 |
| COD-17 | 代码 | S4 | 死代码 | AddWeaknessCommand 无端点、addKnowledgeSource 指向不存在端点（网关模板部分 → ARC-17） |
| COD-20 | 代码 | S4 | Jobs | FetchLinkedIn 失败返回 200 + 异常原文，偏离 ProblemDetails 口径 |
| COD-21 | 代码 | S4 | 前端 | 两处明知端点不存在仍每次试探调用（mock next-question、analytics trend） |
| ARC-17 | 架构/代码 | S4 | 网关 | src/Gateway 默认模板项目与真网关并存于 solution，可纯删除 |
| ARC-18 | 架构 | S4 | CORS | 网关白名单与服务级任意放行（带凭据）互相抵消 |
| → 并入 REQ-01 | 功能 | — | — | FUN-01（AI Practice 接口 500 的功能复现，即 REQ-01 的实测面） |
| → 并入 COD-10 | 架构/需求 | — | — | ARC-05、REQ-03（localStorage 孤岛的架构面与 Tracker Questions 面） |
| → 并入 ARC-04 | 需求/代码 | — | — | REQ-04、COD-16（简历部分） |
| → 并入 ARC-10 | 代码 | — | — | COD-16（AiGateway 客户端部分） |
| → 并入 ARC-06 | 代码 | — | — | COD-18（JSON 裸字符串的领域面） |
| → 并入 ARC-01/02/03/13 | 代码 | — | — | COD-19（巨型文件与双 i18n 词表的 Standards 面） |
| → 并入 FUN-06/07 | 需求 | — | — | REQ-07（F-12 残留的 Mock 键面与硬编码中文面） |
| → 并入 FUN-08 | 需求 | — | — | REQ-13（F-22 趋势文案面） |
| → 并入 FUN-10 | 需求 | — | — | REQ-06（F-16 的空存 400 与话术面） |
| → 并入 REQ-09 | 功能 | — | — | FUN-09（parse-jd 公司名缺失与保存被卡面） |
| → 并入 ARC-17 | 代码 | — | — | COD-17（模板网关部分） |

---
## 一、需求轴（code-review Spec 轴；判定口径：符合 / 不符合 / 规格缺失）

Playbook 部分：今日已完成定稿 v2 逐条审计 62 条（~/workspace/fix-verify/playbook-redesign/spec-audit.md）：符合 56、不符合 3（D2 日历条、B2 预设归属、F3/H 录音资产误判）已全部修复复验（44e57d0 / 7a4f754 / 9f7709e）；实现自创 3 项 X1/X2/X3 已用户裁决并修复（4c96acd / 43b5164 / c81f338）；定稿内部矛盾 1 项 M1（.ics 残留文案）未裁决；环境受限未实测 4 项 U1（TTS 发声/高亮）/U2（录音转写全链）/U3（提醒触发）/U4（段播放发声）悬置，需在配好 Azure Speech/RabbitMQ 的环境补验。本轴不重做，以下为 Playbook 之外与 persona 复查的结论。

REQ-01 | S1 | Assessment / AI Practice | [判定：不符合] 迁移 20260924120000_RemoveAiSettings 用裸 SQL 跨 schema 搬数据到 aigateway.ai_settings 且无存在性守卫，AiGateway 未先建表时整个 MigrateAsync 失败、服务 catch 后带病启动，其后全部迁移（practice_categories 表、CategoryId 列等）永不应用，素材/分类接口恒 500 | 证据：src/Services.Assessment/Infrastructure/Persistence/Migrations/20260924120000_RemoveAiSettings.cs:28-37（协调者已读码复核：INSERT INTO aigateway.ai_settings … FROM assessment.ai_settings 无守卫）；src/Services.Assessment/Program.cs:94-95；功能复现（FUN-01）：打开 /practice 恒显示 "Cannot reach the server, materials were not loaded… writing is disabled"，GET /api/assessment/categories 与 /api/assessment/materials 均 500，后端异常 42P01 relation "assessment.practice_categories" does not exist；DB __ef_migrations_history 只到 20260923130000；截图 ~/workspace/fix-verify/system-audit-20261021/shots/14-practice-error.png、evidence/fun01-assessment-500-excerpt.txt | 影响：全新环境下 AI Practice 整模块不可用，与 COD-14（迁移失败吞掉启动）构成因果链 | 定级理由：整模块在干净环境不可用且无自恢复路径，S1
REQ-02 | S2 | Tracker↔Playbook 状态 | [判定：不符合既定"同源"规则] [F-09 残留] Playbook 已排期/已进行的轮次不回写 Jobs 投递状态；Tracker 统计 interviewed 只数 Jobs 侧 Rounds，Dashboard upcoming 却来自 Interviews，两边仍可同屏矛盾（有面试 vs Saved/Interviews=0） | 证据：src/Services.Jobs/Application/JobHandlers.cs:731-735、813-815；web/src/app/features/dashboard/dashboard.component.ts:113（upcoming 走 /api/interviews/rounds/upcoming）；src/Services.Jobs/Application/EventConsumers.cs:21-58（唯一消费者只处理"已填结果"回写且 Jobs 侧无对应轮次即跳过；与 ARC-07 的双轮次模型互为因果） | 影响：用户仍无法信任任一处状态 | 定级理由：核心状态口径跨模块矛盾、直接违背同源规则，S2
REQ-05 | S3 | Tracker Documents / Resume | [判定：不符合] [F-17] 文本简历回落仍缺失：基础简历只有文本时，投递 Documents 仍显示「No resume yet」 | 证据：实测 PUT /api/jobs/resume-text → 200 后 GET /api/jobs/applications/{id}/resume 返回 {"kind":"none"}；src/Services.Jobs/Application/ResumeFileHandlers.cs:112-133（GetEffectiveResume 只查文件表、从不查文本简历）；tracker.component.ts:1845-1883 | 影响：用户以为简历丢失、重复上传 | 定级理由：单一路径功能缺失、有文件上传变通，S3
REQ-08 | S3 | Dashboard / Analytics | [判定：不符合] [F-13] 无数据时仍输出自相矛盾的弱项信号：六维全 0 仍列「Top 3 to improve」，并渲染「Best 0 / Best 空白」 | 证据：实测 GET /api/analytics/dashboard radar 六维 (current=0, best=null)（src/Services.Analytics/Application/AnalyticsHandlers.cs:113-117）；dashboard.component.ts:50-53 与 analytics.component.ts:109-111 的 weakest() 无"有数据"守卫；dashboard.component.html:190、analytics.component.html:68 无条件渲染 Best | 影响：假信号误导备考优先级 | 定级理由：不阻断操作但持续误导，S3
REQ-09 | S3 | Tracker Smart paste | [判定：不符合] [F-14 残留；并入 FUN-09] 规则解析对 JD 形态高度敏感：独立标题行形态下旧三缺口已修，但同行内联 Benefits 解不出、无标准标题时摘要仍以元数据行开头、公司名可误判或缺失 | 证据：实测 POST /api/jobs/parse-jd（useAi=false）三形态对照——形态 B「Benefits: …」同行 → benefits=null（src/Services.Jobs/Application/SmartParseRules.cs:1041-1051）；形态 B jdSummary 以「Toronto, ON - Hybrid - Full-time Salary: …」开头（:913-959）；形态 C company 误判为「About the Job」；FUN-09 两形态均无 company 字段、UI 公司框为空致 Save 禁用（evidence/fun09-parse-jd-responses.txt） | 影响：「智能粘贴」每单仍需手补，首因体验差 | 定级理由：主路径可用但高频手补，S3
REQ-10 | S3 | Playbook 轮次 | [判定：不符合] [F-19] 轮次编号仍按创建先后而非日期，补录早日期轮次仍成大编号；新建轮次默认 stage=Technical、过去日期默认 Pending 无结果提示 | 证据：src/Services.Interviews/Domain/InterviewAggregates.cs:584-587（AddRound Order=max+1）；playbook-detail.component.ts:139 按 order 展示；src/Services.Interviews/Api/InterviewsController.cs:430（Stage?? "Technical"）、:441（Outcome 默认 "Pending"） | 影响：时间线读序混乱，复盘易记错 | 定级理由：体验与数据可读性问题、不丢数据，S3
REQ-11 | S3 | Tracker | [判定：不符合] [F-15] 批量操作面板仍是占近半屏的纵向大面板，未改细工具条 | 证据：git diff f103a1e..a4fbe5a 对 tracker.component.html/scss 的 bulk-bar 段零改动；现行 tracker.component.scss:604-610 仅 display:flex 未定方向，persona 截图 s05-06 即其渲染（约 330px 高）。注：本条证据为代码与基线截图推定，本轮未做 UI 复跑 | 影响：选 1 条即大幅挤压列表 | 定级理由：纯布局比例问题，S3
REQ-12 | S4 | Web 基础 | [判定：不符合] [F-21] 浏览器标签页标题恒为「Web」 | 证据：web/src/index.html:5 `<title>Web</title>`，全仓无动态 Title 设置 | 影响：多标签场景不可辨识 | 定级理由：无功能影响，S4
REQ-14 | S4 | Tracker / Tech Stack | [判定：不符合] [F-02 同根残留] 投递删除已改全站确认弹窗，但 tailored 简历删除、求职信覆盖/删除、沟通删除、Tech Stack 删除仍用原生 confirm()，每周目标仍用原生 prompt() | 证据：tracker.component.ts:2691,2781,2827,3473（confirm）、:1053（window.prompt）；techstack.component.ts:660,1009（confirm） | 影响：无弹窗处理环境会被静默 dismiss，重现"点了没反应"，与 F-02 修复标准不一致 | 定级理由：主删除路径已修、残留为一致性风险，S4
REQ-15 | S3 | Tracker 抽屉 | [判定：不符合字段齐全口径] Overview 字段网格仍无独立 Work mode 字段，Hybrid 只能埋在正文里找（persona 步骤 8） | 证据：tracker.component.ts:1647-1660（字段仅 Location/Salary/Job type/Work authorization/Applied date/URL）；而 workMode 后端有存储（src/Services.Jobs/Domain/JobAggregates.cs:115）、Smart paste 能解析、smart-add 表单有该字段 | 影响：与用户 Oct 19 定下的"详情页字段齐全、对齐 LinkedIn 岗位页"口径冲突 | 定级理由：数据已在库、仅展示缺位，S3
REQ-16 | S2 | 规格治理（多模块） | [判定：规格缺失] Dashboard、Tech Stack、AI Practice、AI Mock、Analytics、Resume、Admin/AI 设置七个模块在 replica/ 与 docs/ 下零定稿/规格文件 | 证据：ls 查证——docs/ 目录不存在；replica/ 仅 playbook-REDESIGN.md、playbook-redesign-spec.md、playbook-qa-bugs.md、recon-playbook.md、recon-simplify-tracker.md、simplify-tracker-fresh-recon-20261018.md、recon.md、qa-findings.md | 影响：与 Playbook 定稿纪律严重不对称，这些模块变更只能靠代码即兴、回归无据（本清单中 REQ-02/05/08/18 类问题长期反复与此相关） | 定级理由：系统性治理缺口、是多条缺陷的温床，S2
REQ-17 | S3 | 规格治理（Tracker/日历） | [判定：规格缺失] Tracker 只有 Simplify 竞品 recon、无自有产品定稿（状态机、字段口径、与 Playbook 同源规则从未落规格）；日历无独立定稿，仅在 Playbook 定稿中被提及 | 证据：replica/recon-simplify-tracker.md 与 simplify-tracker-fresh-recon-20261018.md 均为竞品侦察文档；replica/ 下无 tracker/calendar 规格 | 影响：REQ-02/03/15 类口径问题无判定依据 | 定级理由：单模块族规格缺失，S3
REQ-18 | S3 | Analytics | [判定：不符合"同一底层数据"规则] Analytics 是复制式快照投影而非同源视图：雷达/mastery 读自有快照表靠事件搬运对齐，事件缺失即口径分叉（persona 步骤 65 已观察 mastery 与 Knowledge 条数矛盾） | 证据：src/Services.Analytics/Application/AnalyticsHandlers.cs:88-106（直读 AbilitySnapshots/PipelineSnapshots/MasteryBreakdowns 本地表，不查 Knowledge/Jobs 真源）；功能轴 OBS-4 亦观察到漏斗与 stats 口径差（未证伪，列观察） | 影响：与"同一底层数据、多模块只是视图"既定规则冲突，分叉时用户无从判断哪边对 | 定级理由：架构性口径风险、当前分叉为偶发，S3

### persona 26 条复查结论表（F-01~F-24 逐条；F-25/F-26 见未复现观察）

| persona 编号 | 结论 | 去向/证据一句话 |
|---|---|---|
| F-01 | 已修 | 实测 GET /api/knowledge?page=1&pageSize=30 → 200、建条目后列表可见；注：Knowledge 代码自 f103a1e 零改动，原 404 在当前形态不复现，疑似当时旧 build/转发通道问题，无法再定位 |
| F-02 | 已修（残留 → REQ-14） | 统一 ConfirmDialog + 真 DELETE，实测删后 total=0；其余删除点仍原生 confirm |
| F-03 | 已修 | 建条目返回真实 GUID 首轮（非 __synthetic_r1）；幽灵轮次出处组件已 orphaned；invite consumer 改建真实 Round 实体（41a2e49） |
| F-04 | 已修（被定稿取代） | PUT round 带 checklist → 204、GET 原样返回（41a2e49 透传）；v2 UI 已无 checklist 编辑入口，数据仅供准备度计分 |
| F-05 | 已修 | 搜索 300ms 防抖服务端查询 + 无结果空状态；实测 total 随关键词正确变化 |
| F-06 | 已修 | v2 生成失败 catch 显示含原因的 notice、不再静默关窗；空 JD/简历不再 400；实测 400 的 detail 为 AiGateway 连接失败（沙盒 egress 环境）且有映射提示 |
| F-07 | 已修 | 旧 Run analysis 按钮随 v2 取消；新路径失败显示原因；实测无转写 POST /analyze → 409 code=Analysis.InvalidState（后端行为合理）。小尾巴：playbook-v2.api.ts:150-151 runAnalysis 无调用方（死代码，并入 COD-17 同类） |
| F-08 | 已修 | Resume Score 改读 jdText\|\|jdSummary 与 Overview 同源，空态区分缺 JD/缺简历 |
| F-09 | 仍在 → REQ-02 | 状态回写缺失，跨服务口径仍可矛盾 |
| F-10 | 已修（运行时被 REQ-01 阻断验证） | 前端改名即落库且失败不静默；后端 PUT materials 在位；但当前沙盒该接口 500，根因 REQ-01 |
| F-11 | 已修（范围注记） | Tracker 页头 ≤640px 纵向堆叠、顶栏图标化导航已落地；其它模块页头未逐页做 390px 验证 |
| F-12 | 仍在 → FUN-06 / FUN-07（原 REQ-07 合并） | Mock 键未定义、三语直出；硬编码中文多处残留 |
| F-13 | 仍在 → REQ-08 | weakest() 无数据守卫 |
| F-14 | 部分仍在 → REQ-09 | 旧三缺口在标准形态已修，内联/无标题形态仍缺 |
| F-15 | 仍在 → REQ-11 | bulk-bar 与基线字节一致 |
| F-16 | 仍在 → FUN-10 | 无粘贴入口、空存 400 与黑话话术未改（dc81750 只删摘录行） |
| F-17 | 仍在 → REQ-05 | GetEffectiveResume 不查文本简历 |
| F-18 | 部分已修 | 沟通/邮件已同源；Guidance 随 v2 取消消失；Questions 孤岛未修 → 并入 COD-10 |
| F-19 | 仍在 → REQ-10 | 编号与默认值逻辑未改 |
| F-20 | 已修 | Dashboard Prepare 已直达条目；v2 页签收敛、双套编辑入口消除 |
| F-21 | 仍在 → REQ-12 | `<title>Web</title>` 未改（语言按账号持久化为加分项保留） |
| F-22 | 仍在（形态变化）→ FUN-08 | 文案已三语化但内容仍是开发者语言，且根因是趋势接口根本不存在 |
| F-23 | 已修 | v2 轮次卡 stage 恒有值；旧出处组件 orphaned；全角冒号仅存于解析正则非展示 |
| F-24 | 已修（代码层） | hover 显隐门控仅存于 orphaned 旧组件；v2 组件 grep 零命中；原条为 PERSONA-INFERRED，触屏实机未验，置信度中 |

**需求轴小计**：正单 13 条（S1×1、S2×2、S3×8、S4×2；另 REQ-03/04/06/07/13 五编号已按同根因并入他轴正单）；persona 已修确认 14 条、仍在/部分仍在 9 条（全部有正单去向）、无法判定 2 条（F-25 不适用、F-26 环境受限）。

**需求轴覆盖声明**：查了——persona F-01~F-24 逐条以读码 + 沙盒 API 实测复查（探针数据已清理）；MEMORY/AGENTS 既定规则 × 9 个模块的规格存在性（ls 实查 replica/ 与 docs/）；Playbook 62 条审计结论引用未重做。没查及原因——F-26 的 AI 链路（录音转写全链、Mock 评分、Auto-fetch 成功路径）受沙盒 egress/凭据限制无法判定；各模块 390px 逐页验证仅覆盖 Tracker 与导航（时间所限）；FR 文案逐项未查（由功能轴抽查覆盖一部分）。

---
## 二、代码轴（code-review Standards 轴；smell 类为判断性发现）

COD-01 | S1 | Interviews/Knowledge/Jobs 核心数据 | 三服务核心实体无 UserId 列、全系统全局共享，与 Identity「普通用户:自己的数据」模型矛盾——匿名自助注册（默认 User 角色含 jobs/interviews/knowledge 读写权）即可读写全部求职/机经/知识数据，Viewer 只读角色亦可读全部 | 证据：src/Services.Interviews/Api/InterviewsController.cs:22（未注入 ICurrentUser，协调者已复核）；src/Services.Knowledge/Api/KnowledgeController.cs:20；src/Services.Jobs/Api/JobsController.cs:18（注释自认"单租户空间"，协调者已复核原文）；src/Services.Interviews/Application/InterviewHandlers.cs:876、src/Services.Knowledge/Application/KnowledgeHandlers.cs:544、src/Services.Jobs/Application/JobHandlers.cs:413（均按 Id 单条件查询）；协调者复核：三服务 Domain 聚合 grep UserId 零命中（Jobs 仅 UserResume/AnswerTemplate 两类用户级资产有）；src/SharedContracts/Security/Permissions.cs:76-88；src/Services.Identity/Api/AuthController.cs:27；src/Services.Identity/Infrastructure/Persistence/IdentityDbSeeder.cs:123 | 影响：任一注册账号可查看/修改/删除他人全部投递、面试条目（含录音转写）、知识库；多用户隔离实际不存在 | 定级理由：当前单用户使用未爆，但数据模型层隔离完全缺失、公网自助注册即全量越权，S1
COD-02 | S2 | Analytics | POST /api/analytics/ability 接受请求体任意 UserId 覆盖当前用户，可替他人写能力快照 | 证据：src/Services.Analytics/Api/AnalyticsController.cs:57（`body.UserId?? Me`） | 影响：跨用户污染他人雷达图/仪表盘数据，可写型 IDOR | 定级理由：可写越权但仅限能力快照一类数据，S2
COD-03 | S3 | Jobs | DeleteCoverLetter 是同组端点中唯一不校验归属的：命令不带 UserId、Handler 按 ApplicationId 单条件删 | 证据：src/Services.Jobs/Api/JobsController.cs:668；src/Services.Jobs/Application/CoverLetterHandlers.cs:185、:192（对比同文件:64 的 UserId 过滤） | 影响：知 applicationId 即可删他人求职信（在 COD-01 全局模型下影响被放大） | 定级理由：单端点归属缺失、范围窄，S3
COD-04 | S1 | Interviews 轮次保存 | 后端 UpdateDetails 对轮次扩展字段无条件整字段赋值（未传即写 null，仅 ChecklistJson 有非空守卫），而前端三个写入方字段集不一致：round-card 保存与 comm-log 同步只传基础字段，保存一次即清空该轮的 Transcript/PrepQuestionsJson/EmailsJson/RecordingUrl | 证据：src/Services.Interviews/Domain/InterviewAggregates.cs:1067-1078（协调者已读码复核：六个赋值仅 checklist 有守卫）；src/Services.Interviews/Application/InterviewHandlers.cs:1505-1509；web/src/app/features/playbook/redesign/round/round-card.component.ts:272-282；comm-log.component.ts:297-307（对照全字段写入方 components/brief/rounds-timeline.component.ts:491-508） | 影响：正常操作路径下静默丢数据——编辑轮次或同步沟通安排后，转写稿/备考题/邮件时间线被抹除 | 定级理由：主流程正常操作即触发不可恢复的数据丢失，S1
COD-05 | S2 | 全服务并发 | 聚合普遍配了 xmin 乐观并发令牌，但 DbUpdateConcurrencyException 无任何映射：管线原样上抛、全局异常处理器只映射 Validation/401/404/Argument，其余一律 500 | 证据：src/BuildingBlocks/Hosting/ServiceDefaults.cs:145-152；src/BuildingBlocks/Behaviors/PipelineBehaviors.cs:74-78；src/Services.Interviews/Infrastructure/Persistence/InterviewsDbContext.cs:102；KnowledgeDbContext.cs:105；JobsDbContext.cs:123 | 影响：双标签页/双设备并发编辑同一条目时用户看到 500 而非 409，且无从知道改动未保存 | 定级理由：并发场景必现且反馈误导，S2
COD-06 | S2 | 前后端契约（Interviews 转写） | getTranscript 字段名对不上：前端按 transcriptText 读，后端返回 fullText，转写正文在 v2 详情页恒为空 | 证据：web/src/app/features/playbook/redesign/playbook-v2.api.ts:67-69；src/Services.Interviews/Api/InterviewsController.cs:231；消费方 round-record.component.ts:354-358、materials-list.component.ts:240-244 | 影响：面试实录/材料正文展示失效，用户误以为转写丢失 | 定级理由：核心展示链路恒失效，S2
COD-07 | S2 | 前后端契约（Interviews 转写） | startTranscription 调 POST /assets/{assetId}/transcribe，后端无此路由（仅有条目级 POST {id}/transcription/start），点「开始转写」必 404 | 证据：playbook-v2.api.ts:146-148；调用方 round-record.component.ts:490；后端路由表 InterviewsController.cs（transcription/start 为唯一转写入口） | 影响：v2 录音转写功能不可用（与历史 resume-files 404 同型） | 定级理由：整功能入口失效，S2
COD-08 | S3 | 前后端契约（Interviews 待办） | toggleAction 用 PUT.../actions/{id}/toggle 调不存在的端点（后端为 PATCH.../actions/{actionId} 传 isDone），且.catch 吞掉错误后照常 reload | 证据：web/src/app/features/playbook/playbook-detail.component.ts:537-541；src/Services.Interviews/Api/InterviewsController.cs:339 | 影响：待办勾选假成功，界面刷新后状态回退 | 定级理由：单功能假成功、影响面小，S3
COD-09 | S3 | EF 迁移（Interviews） | 15 个迁移中 AddRoundDetails 缺 Designer.cs（三件套缺件），且仓库根目录留有手工补列脚本绕过迁移历史补这批列 | 证据：src/Services.Interviews/Infrastructure/Persistence/Migrations/20261008010000_AddRoundDetails.cs:8（同目录无同名.Designer.cs；Snapshot 已含其列:536、:552）；fix-missing-columns.sql:1-14 | 影响：新环境按迁移建库时模型与 Snapshot 不一致风险；后续 scaffold 可能重复生成这批列 | 定级理由：迁移可信度受损但现网库已对齐，S3
COD-10 | S2 | 前端 localStorage 孤岛（合并 COD-11、ARC-05、REQ-03） | 多类真实领域数据只有浏览器存储、无任何后端 seam：Playbook STAR 故事库、复盘 pb.debrief.*、粘贴材料 pb-pasted-*；Tracker 收藏（且列表组件与详情弹窗两套独立实现读写同一 key、状态可分叉）、按投递自定义面试问题（抽屉 Questions 页签唯一存储，与 Playbook 轮次问题/Tech Stack 完全不通）；Tech Stack 空分类与复习热力图 | 证据：web/src/app/features/playbook/playbook.component.ts:509-520（pb.starStories 仅 localStorage，无后端端点）；tracker.component.ts:566-623（备份/恢复靠整包导出枚举这些键兜底）、:815-824 与:2942-2956（同 key yi-tracker-favorites 双实现）、:3076-3092（questionsKey=yi-tracker-questions-{id}，saveAppQuestions 只写 localStorage 零 API 调用）；techstack/category-manager.component.ts:61-68（ts.customCategories）；techstack.component.ts:1221-1228（ts.reviewActivity）；全仓 localStorage 引用实测 152 处 | 影响：换设备/清缓存即丢长期积累的故事、收藏、自备问题、复习记录；直接违背用户"同一份底层数据"既定规则（REQ 判定：不符合） | 定级理由：多类用户资产无服务端备份且部分双写分叉，S2
COD-12 | S3 | Assessment 错误路径 | ListSessions 对「已评分但 Issues 为空」的会话执行 GroupBy(...).First()，序列为空时抛 InvalidOperationException 冒成 500 | 证据：src/Services.Assessment/Application/AssessmentHandlers.cs:109-113（守卫只判 scored.Count，未判 Issues 是否为空；评分端点允许 Issues 为空集） | 影响：单条合法数据即可把会话列表接口打成 500、整页不可用 | 定级理由：需特定数据形态触发，S3
COD-13 | S3 | Interviews 错误路径 | AttachAsset 的 JSON 形态用 ReadFromJsonAsync 直读请求体，坏 JSON 抛 JsonException，而全局处理器未映射该类型 → 500 而非 400 | 证据：src/Services.Interviews/Api/InterviewsController.cs:160；src/BuildingBlocks/Hosting/ServiceDefaults.cs:151 | 影响：畸形请求得到误导性 500，排障成本高 | 定级理由：仅畸形请求触发，S3
COD-14 | S3 | 全服务启动 | 全部 7 个业务服务把数据库迁移/种子失败 catch 后仅记日志继续启动，/health/live 照常返回 ok | 证据：src/Services.Jobs/Program.cs:127-128；src/Services.Interviews/Program.cs:96-97；src/Services.Knowledge/Program.cs:74-75；src/Services.AiGateway/Program.cs:87-88；src/BuildingBlocks/Hosting/ServiceDefaults.cs:191 | 影响：迁移失败时系统"假健康"，问题延后到用户操作时才以满屏 500 暴露（REQ-01 正是经此链路放大） | 定级理由：系统性可观测性缺陷、单独存在时无直接数据损害，S3
COD-15 | S3 | Assessment 权限 | 写操作挂在只读权限 MockRead 上：保存录音评分（POST）、TTS 合成（POST，消耗 Azure 额度）；而 Viewer 角色定义为"仅可查看"却持有 MockRead | 证据：src/Services.Assessment/Api/AssessmentController.cs:469、:618；src/SharedContracts/Security/Permissions.cs:84-88；src/Services.Identity/Infrastructure/Persistence/IdentityDbSeeder.cs:124 | 影响：只读角色可产生写入与付费 API 消耗，最小权限原则被穿透 | 定级理由：权限口径错误但需 Viewer 账号场景，S3
COD-17 | S4 | 死代码（残余；模板网关部分并入 ARC-17） | AddWeaknessCommand 有 Handler+Validator 但无任何 Controller 端点；前端 addKnowledgeSource 无调用方且其目标端点 POST /knowledge/{id}/sources 后端不存在；playbook-v2.api.ts 的 runAnalysis 亦无调用方（需求轴复查 F-07 时发现） | 证据：src/Services.Interviews/Application/InterviewHandlers.cs:1540；web/src/app/features/playbook/redesign/playbook-v2.api.ts:219-221、:150-151 | 影响：误导维护者，徒增审查面 | 定级理由：无运行时影响，S4
COD-20 | S4 | Jobs 错误处理 | FetchLinkedIn 的 catch 返回 HTTP 200 + `fetch_error: ` + ex.Message，把内部异常原文下发前端，与全站 ProblemDetails 口径不一致 | 证据：src/Services.Jobs/Api/JobsController.cs:327-330 | 影响：监控按状态码无法感知失败；内部错误细节有外泄面 | 定级理由：单端点口径问题，S4
COD-21 | S4 | 前端投机性调用（Speculative Generality，判断性） | 两处明知端点不存在仍每次调用试探：mock 的 next-question（注释自认后端未提供、靠 404 后本地降级）、analytics 的 ability/trend（靠 catchError 静默；趋势面另见 FUN-08） | 证据：web/src/app/features/mock/mock-session.component.ts:266-276；web/src/app/features/analytics/analytics.component.ts:167-170 | 影响：每次操作多一发必失败请求与日志噪声；将来同名端点语义不同时会被静默误用 | 定级理由：无当前功能损害，S4

### 代码轴已查无发现（8 面）

- Identity 鉴权面：Auth/Admin 按 token 用户与权限策略执行，Google OAuth 有 state CSRF 校验与开放重定向防护（GoogleAuthController.cs:51、:95），改密/资料以当前用户 id 为准。
- Assessment 用户隔离：sessions/materials/recordings/categories/storage 的 Handler 均带 UserId 过滤（PracticeHandlers.cs:92、:239），服务内未见 IDOR。
- AiGateway 用户隔离：设置表以 UserId 为主键、Complete 以当前用户取凭据（AiGatewayDbContext.cs:30、:46）。
- Jobs 用户级资产隔离：resume-text/resume-file/cover-letter 读与存、answer-templates 均按 UserId 过滤（CoverLetterHandlers.cs:64），例外见 COD-03。
- 文件缺失 500 路径：录音回放查不到文件返回 NotFound 而非抛异常（InterviewHandlers.cs:1042-1045，Assessment 同型）。
- EF 迁移完整性（Interviews 以外）：AiGateway 2、Analytics 1、Assessment 12、Identity 1、Jobs 12、Knowledge 3 个迁移三件套齐全，例外见 COD-09（REQ-01 的迁移缺陷属逻辑内容问题、非缺件）。
- 前后端契约高频面：jobs 投递/公司/沟通、interviews 条目/轮次/待办、knowledge CRUD、assessment sessions/materials、ai settings/complete、auth 全组端点抽查对照一致，例外见 COD-06/07/08/17/21。
- Analytics 洞察 First() 调用：有 scored 为空的前置守卫（AnalyticsHandlers.cs:159-166）。

**代码轴小计**：正单 17 条（S1×2、S2×5、S3×7、S4×3；另 COD-11/16/18/19 四编号已按同根因并入 COD-10/ARC-04/ARC-10/ARC-06/ARC-01/02/03/13）；已查无发现 8 面。

**代码轴覆盖声明**：查了——7 个业务服务的 Controller/Handler/DbContext 逐个读码（鉴权归属、错误映射、并发令牌、迁移三件套为全量核对；契约对照为高频面抽查）；前端 localStorage 以全量 grep 盘点 + 逐个落点读码。据本轴报告，未查：逐行全覆盖审查（本轴定位为高风险面查透）、运行时渗透与并发压测（仅静态判定）、前端每个组件的内部状态逻辑（仅契约面与孤岛面）。

---
## 三、功能轴（diagnosing-bugs 纪律；每条含最小复现）

证据根目录：~/workspace/fix-verify/system-audit-20261021/（截图 shots/、响应摘录 evidence/、复现脚本 scripts/）。

FUN-02 | S2 | Tracker | 手动建单与编辑时填写的 JD（及备注）被静默丢弃 | 最小复现：种子=Tracker 无该公司记录 → 1.点 + 新建 2.公司填 AuditTest Co、职位填 Senior.NET Developer (Audit)、JD 框填 "Audit JD: Build.NET Core APIs…" 3.点 Create 4.打开抽屉看 Job Description 并查 GET /api/jobs/applications/{id} → 期望：JD 保存且渲染；实际：抽屉 "No JD yet"，API jdText=null、notes=null，Resume Score 页签亦 "No JD yet"；代码定位：create() 的 POST 负载与 updateAppPayload() 均不含 jdText 字段 | 证据：shots/20-tracker-create-filled.png、shots/22-tracker-drawer.png；evidence/fun02-fnn03-api-notes.txt；scripts/audit4.py | 影响：手动录入的核心字段无声丢失，连带简历评分/求职信无 JD 可用 | 定级理由：核心字段丢失且零提示，有 Smart paste 替代路径故未到 S1，S2
FUN-03 | S2 | Tracker ↔ Playbook 跨模块 | 对已有 Playbook 条目的投递点 Prep，重复新建条目而非打开既有条目 | 最小复现：种子=Corpay 投递已有关联 Playbook 条目（jobApplicationId 相同）→ 1.Tracker 点 Corpay 卡的 Prep → 期望：跳转既有条目；实际：POST /api/interviews 201 新建第二条同 app 关联条目，列表出现两条（复现后已 DELETE 清理，204 确认） | 证据：shots/45-prep-landing.png；evidence/fun02-fnn03-api-notes.txt；scripts/audit21.py | 影响：违反同源数据规则，Playbook/Dashboard 出现重复备考记录，轮次/材料散落两处 | 定级理由：跨模块一致性被破坏且会持续累积重复，S2
FUN-04 | S2 | Jobs Smart paste | JD ≥约 20K 字符时保存 500，整单丢失且无截断/校验提示 | 最小复现：种子=44,181 字符 JD → 1.Smart paste 粘贴 2.Parse（解析成功）3.点 Save as application → 期望：保存成功或给长度提示；实际：POST /api/jobs/applications/smart-add 500，后端 Postgres 22001 value too long for character varying(20000)（JdText 列上限 20000），投递未创建 | 证据：evidence/fun04-jobs-500-excerpt.txt；scripts/audit25.py 运行输出 | 影响：真实 LinkedIn 长 JD（含公司样板段）极易超 20K，主录入路径整单失败 | 定级理由：常见真实数据形态下主路径整单失败，S2
FUN-05 | S2 | Tech Stack | 新建条目不填 Interview question 必失败（400），而 UI 只把 Title/Category 标为必填 | 最小复现：种子=Tech Stack 任意状态 → 1.点 + 新建 2.填 Title、Category、Concept explained，Interview question 留空 3.点 Save（按钮可点）→ 期望：创建成功；实际：POST /api/knowledge 400 {"errors":{"Question":["The Question field is required."]}}，条目未创建；对照组补 question 后 201 成功；弹窗内无字段级提示指向 question | 证据：evidence/fun05-post-knowledge-400.json；shots/33-techstack-after-create2.png；scripts/audit9.py | 影响：按 UI 引导操作的用户新建必撞墙 | 定级理由：合规填法下新建主流程必失败，S2
FUN-06 | S2 | AI Mock（合并 REQ-07 的 Mock 键面；原 F-12 未修复） | 模式下拉三语均为原始 i18n 键直出，会话页模式标签为枚举原文 | 最小复现：种子=Mock 列表任意 → 1.点新建 2.看 Mode 下拉 3.任选建会话进会话页 → 期望：本地化模式名；实际：选项恒为 mock.modeTopicDrill — mock.modeTopicDrillHint 等四组键名（EN/FR 实测相同），会话页显示枚举原文 TopicDrill；代码定位（需求轴）：mock.component.ts:76-79 引用的 8 键在 i18n DICT 中零定义，i18n.service.ts:2382-2383 缺键回显键名 | 证据：shots/38-mock-dialog2.png、16-mock-modes.png、39-mock-session.png、59-fr-mock.png；scripts/audit3.py、audit26.py | 影响：建会话第一步控件完全不可读，三语用户皆然 | 定级理由：核心创建控件文案整体失效，功能本身可走通，S2
FUN-07 | S3 | 全局 i18n（合并 REQ-07 的硬编码中文面；同根因：硬编码中文串 + 后端中文消息/字段直出） | EN/FR 界面多模块中文残留 | 最小复现：种子=界面切 English 或 Français → 依次打开 Dashboard、Analytics、Tracker 新建弹窗、抽屉 Documents、Tech Stack 详情、日历 → 期望全英文/法文；实际：Dashboard 弱项区"口语练习"×3、"从「AI 实战模拟」新建一次练习开始"、Upcoming 条目"第 2 轮"；Analytics 空态整段中文 + Top 3 维度名"发音/流利度/语句完整性"（API radar 的 nameZh 被 EN 界面直出）；Tracker 新建弹窗"投递文档 (如 Forrest_Lin_Resume_Corpay.pdf)"/"职位描述 (JD)"及中文 placeholder（tracker.component.ts:178、:189 硬编码，tracker.component.html:36,39,58-69 备份/恢复备份/幽灵横幅同）；抽屉求职信卡警告中文；Smart paste 后端消息中文直出"只解析出部分字段,请补全后再保存。AI 精修未成功,已只按规则解析。"；Tech Stack 详情"口语练习"按钮、"复习热力图/少/多"（techstack.component.html:105）；日历星期头"一 二 三 四 五 六 日"；dashboard.component.html:115「口语练习」。FR 下同批字符串原样残留 | 证据：shots/01-dashboard.png、07-analytics.png、09-calendar.png、10-tracker-create-dialog.png、23-smart-parsed.png、34-techstack-detail-source.png；scripts/audit1.py、audit2.py、audit26.py | 影响：非中文用户在几乎每个模块都会撞到不可读文案，含个人文件名示例硬编码 | 定级理由：覆盖面广但纯文案、不阻断操作，S3
FUN-08 | S3 | Analytics（合并 REQ-13 的文案面；原 F-22） | 趋势区块接口不存在，恒显示不可用 | 最小复现：种子=任意账号 → 打开 /analytics 看 "Last 30 days" 区块 → 期望：趋势图渲染；实际：GET /api/analytics/ability/trend?days=30 返回 404（Analytics 服务无此路由），区块恒显示 "Trend API unavailable — skipping this section."（该文案已三语化但仍是开发者语言，i18n.service.ts:2055）；同页 dashboard 接口 200、其余区块正常 | 证据：shots/07-analytics.png；scripts/audit1.py、audit19.py | 影响：一个完整区块永久失效；前端试探调用面另见 COD-21 | 定级理由：单区块失效且有降级提示，S3
FUN-10 | S3 | Resume（合并 REQ-06 的空存/话术面；原 F-16 残留） | 已保存的文本简历在页面上不可见、不可编辑，也无粘贴文本入口；空存必吃 400 并弹开发者话术 | 最小复现：种子=后端 resume-text 有值、无 resume-file（GET resume-text 200 有文本、/resume-file/meta 404）→ 1.打开 /resume 2.点 Edit → 期望：出现文本框可查看/修改/粘贴；实际：卡片仅显示 "Saved resume" 标签，编辑态只有 Upload/Save/Cancel，组件模板内无 textarea 元素；上传 txt 提取的文本同样无处查看修改，Save 只是回存不可见的 draft；空存面（需求轴）：saveResume 无空值守卫（resume.component.ts:110-135），后端空文本 Validation 拒绝（ResumeHandlers.cs:50-51，实测 PUT 空 → 400 Resume.Empty），提示原文「Saved locally, but sync to server failed」（i18n.service.ts:1879） | 证据：shots/54-resume-edit-state.png、52-resume-upload.png、50-resume-initial.png；scripts/audit23.py、audit24.py | 影响：文本简历用户无法维护内容，只能整文件替换；新用户第一击即报错 | 定级理由：单一维护入口缺失但文件路径可用、既有文本不丢，S3

### 功能轴已查无发现（16 条）

- Dashboard 顶部四数与同刻 GET /api/jobs/stats 对账一致（测时点 2/2/2/0）。
- Dashboard Upcoming：API 建未来轮次后行动中心出现该轮次（含日期/时间/形式 + Prepare 入口），验后已删。
- Tracker 手动建单的公司/职位/地点/初始状态落库与看板显示一致（JD/备注除外，见 FUN-02）。
- Tracker 抽屉五页签均可打开渲染，Email 可 Add、Questions 可 Add question，Documents 无简历时为优雅空态。
- Tracker 改状态：抽屉下拉 Saved→Applied，POST /status 204，API 一致（竖排 stepper 为展示件，实际控件是下拉）。
- Tracker 收藏/归档/删除闭环：图标切换、移入 Archived 可见、确认后 GET 404。
- Smart paste 除 FUN-09（并入 REQ-09）外：role/location/keywords/summary/Requirements/Benefits 提取正确，补公司后 smart-add 成功且 jdText 落库。
- Tech Stack 补填 question 后新建成功列表可见；asked sources 在详情与列表筛选均正确；详情编辑改名落库；Log review 四档可选且计数更新；勾选删除走 confirm 后软删进 Trash 可见。
- Tech Stack 空标题或缺 Category 时 Save 按钮禁用（前端门禁有效）。
- AI Mock 建会话 POST 201 自动跳会话页，状态 Draft、进度 0% 正常（模式文案见 FUN-06，出题见 OBS-1）。
- Analytics 除趋势外区块（雷达/漏斗/精通度）均加载渲染，无原始错误文案直出（口径差见 OBS-4）。
- Resume 上传 txt 可提取文本进入待保存态；0 字节 PDF 给出 "Extraction failed, please paste manually" 不崩溃、无脏数据。
- 日历月视图：未来轮次出现在对应日期格与侧栏列表；过去轮次不进 upcoming 列表，与后端 upcoming?days=60 设计口径一致。
- 公司名含 & < > ' " 特殊字符建单：看板原文正确渲染（无转义损坏/无注入），CSV 导出引号转义正确，导出文件名不含公司名故无文件名问题。
- FR 界面六模块页面正文未见 xxx.yyy 原始键泄漏（Mock 弹窗选项除外，见 FUN-06）；Playbook 详情 FR 为真实法语渲染。

### 未复现观察（不占正单编号）

- OBS-1 | AI Mock 会话建后无题目：会话页示 "No questions yet..."，等待 7 秒与刷新仍无；题目生成依赖 LLM/分析 worker（RabbitMQ），环境受限，未能判定产品缺陷还是无模型可用。
- OBS-2 | 120 段转写渲染：API 建 Transcript 资产（120 段，POST 201）后打开详情页所到视图未渲染段落；详情页轮次记录区正在飞改动，未能完成 UI 渲染确认（保存本身正常；正文展示的契约面另见 COD-06）。
- OBS-3 | 超大文件上传上限：Resume 上传 25MB txt 客户端直接进入编辑态、无大小提示；为避免污染简历数据未点 Save，服务端上限未测出（与 ARC-09 的 Kestrel 上限不一致互为旁证）。
- OBS-4 | Analytics 漏斗与 Jobs stats 口径差：漏斗 Applied=2 而 stats 当前状态 Applied=0（两条均为 Interview），疑为"累计到达"vs"当前状态"两口径，无口径定义文档可证伪（与 REQ-18 同源）。
- OBS-5 | Tech Stack 复习计数：一次 Log review split-button 操作后 reviewCount 0→2，疑主按钮与菜单项各记一次；单次观察未做隔离复现。
- OBS-6 | 走查中一度出现 vite-error-overlay（在飞 intake-panel 代码的瞬时编译错误），后续自行恢复，属在飞改动环境状态。
- 需求轴无法判定：F-25（persona 契约字段 ASSUMPTION，非产品缺陷，不适用）；F-26（UNTESTED 清单，本轮同样受沙盒 egress/凭据限制无法判定，需用户 Mac 环境验）。

**功能轴小计**：正单 8 条（S2×5、S3×3；另 FUN-01 并入 REQ-01、FUN-09 并入 REQ-09）；已查无发现 16 条；未复现观察 6 条（另需求轴无法判定 2 条）。

**功能轴覆盖声明**：已测——Dashboard 对账/Upcoming/弱项渲染（数值恒 0，深对账受练习数据为空限制）；Tracker 手动建单、Smart paste、抽屉五页签、改状态、收藏、归档、删除、Prep 跨模块；Tech Stack 新建/列表/详情编辑/复习/删除+Trash/asked sources；AI Mock 建会话与模式文案；Analytics 各区块与趋势接口；Resume 上传/0 字节/编辑入口；日历未来轮次；边界（45K JD、特殊字符、0 字节、空标题门禁；超大文件仅客户端、120 段转写保存通而渲染未确认）；i18n（EN 全模块扫、FR 六模块抽查 + Playbook 详情 FR）。未测及原因——AI Practice 全流程被 FUN-01/REQ-01 阻断；Tracker 批量操作、CSV/备份导入、Answer Library、看板拖拽（时间所限）；Tech Stack 分类管理与重复检测弹窗、Due review 专页（时间所限）；AI Mock 答题评分（LLM 环境受限，OBS-1）；LinkedIn URL 抓取（需登录态，环境受限）；AI 生成类链路（求职信、AI 精修）无可用 LLM 凭据；录音/TTS/语音链无设备与 Azure 凭据；真机/移动视口未测（仅 1440×900 桌面）；Playbook 详情主流程按任务要求不重测（今日已多轮验过），仅测跨模块与转写边界。

---
## 四、架构轴（improve-codebase-architecture 深化视角；量化证据均为 grep/wc 实测）

热点依据：近 200 次提交文件级频次——tracker.component.ts 70 次、i18n.service.ts 38 次、SmartParseRules.cs 21 次、ai-practice.component.ts 19 次、smart-add-dialog.component.ts 18 次、playbook-detail.component.ts 16 次。仓库无 GLOSSARY/ADR（仅 ARCHITECTURE.md + replica/ 定稿作领域词汇参考）。

ARC-01 | S2 | 前端 Tracker module（吸收 COD-19 的巨型文件面） | 单文件塞 4 个组件类、interface≈全部实现，无 depth 可言 | 证据：web/src/app/features/tracker/tracker.component.ts 3575 行，含 ApplicationDialogComponent(L244)、TrackerComponent(L322–2559，约 2237 行)、ApplicationDetailDialogComponent(L2559–3516)、PrepPlanDialogComponent(L3573) 共 4 个 export class + 3 个 interface；近 200 次提交被改 70 次为全仓第一热点 | 影响/加深方向：任何 Tracker 改动都落同一文件、locality 为零；按用例拆为独立 module（列表/看板、详情抽屉、对话框），组件只留薄 interface 委托——删除测试：删任一对话框类复杂度只是搬家，证明它们本就是被压扁的独立 module | 定级理由：全仓第一热点的持续冲突与回归半径，S2
ARC-02 | S2 | 前端 AI Practice module | 3645 行单类巨型组件，录音/评分/TTS/素材树/缩放偏好全挤在一个实现里 | 证据：web/src/app/features/ai-practice/ai-practice.component.ts 3645 行（全仓最大）、单 class 约 3534 行、约 105 个方法；同目录 material-transfer.ts 563 行、material-transfer-dialog.component.ts 668 行与之纠缠 | 影响/加深方向：新人无 seam 可切入；把素材树、评分会话、TTS 播放抽成各自深 module，组件退化为编排层，提高 leverage | 定级理由：全仓最大单类、五职责耦合，S2
ARC-03 | S2 | 后端 Application 层（Interviews/Jobs/Knowledge/Assessment） | 每服务全部用例压在一个 Handler 文件，无子域 seam | 证据：Services.Interviews/Application/InterviewHandlers.cs 2857 行/57 个 Handler 类；Services.Jobs/Application/JobHandlers.cs 933 行/26 个；Services.Knowledge/Application/KnowledgeHandlers.cs 1086 行/24 个；Services.Assessment/Application/PracticeHandlers.cs 1141 行/17 个；Services.Jobs/Application/SmartParseRules.cs 1377 行多职责（热点第 3） | 影响/加深方向：改一个用例要在一两千行里定位；按子域（条目/轮次/资产/分析、公司/投递/简历/智能粘贴）拆文件组，locality 随子域收敛 | 定级理由：四服务同型问题、回归半径最大，S2
ARC-04 | S2 | 简历数据（合并 REQ-04、COD-16 简历部分） | 同一领域概念「用户主简历」有 4 个落点、两套近乎逐字复制的后端实现，且其中一套前端根本不调 | 证据：①Services.Jobs/Domain/JobAggregates.cs:561 UserResume + ResumeHandlers.cs 84 行；②Services.Assessment/Domain/UserResume.cs 独立同名类 + ResumeHandlers.cs 83 行（diff 实测仅差 DbContext/命名空间/注释）；③Services.Jobs/Domain/ResumeFile.cs + ResumeFileHandlers.cs 156 行（文件版）；④前端 localStorage yi-my-resume/yi-my-resume-meta（resume.component.ts L64-65）。两服务各暴露同形 GET/PUT/DELETE resume-text（JobsController.cs:416ff、AssessmentController.cs:699ff），前端只用 /api/jobs/resume-text（4 处引用，Assessment 版 0 处） | 影响/加深方向：双写必分叉，Assessment 侧消费者可能读到空/旧简历且无提示（REQ 判定：不符合同一底层数据规则）；简历收敛为 Jobs 单一 module + 一个 interface，其余落点删除——删除测试：删 Assessment 副本复杂度直接消失不搬家，证明它是重复而非分层 | 定级理由：核心资产双真源且一份已实际脱节，S2
ARC-06 | S2 | Interviews 领域模型 JSON 大字段（合并 COD-18） | 轮次与条目的结构化子数据以不透明 JSON 字符串落 text 列，无 schema/版本，后端不校验，解析逻辑散落各 Handler 与前端，查询靠字符串 Contains | 证据：Services.Interviews/Domain/InterviewAggregates.cs 实测 16 个 *Json 字符串属性（DebriefJson、QuestionsToAskJson、SpeechMetricsJson、TranscriptSegmentsJson、MetaJson、ReviewJson/WeaknessTagsJson/FollowUpQuestionsJson/MissedPointsJson、PrepQuestionsJson/EmailsJson/ChecklistJson/PrepReportJson、DimensionsJson/SummaryBulletsJson 等），全文件 "Json" 出现 69 次，InterviewsDbContext.cs 对应 16 处 HasColumnType("text")；全仓同类字段另有 Knowledge 6 个、Assessment 4 个、Jobs 2 个 | 影响/加深方向：字段结构变更无迁移无校验，interface（DTO 形状）与存储形状脱节（COD-04 的整字段覆盖正发生在此面）；高频查询/关联子结构（轮次邮件、逐题复盘、备考报告）结构化建表，或至少收敛为带版本的强类型值对象 + 单一 adapter 负责序列化 | 定级理由：数据完整性的结构性温床、已实际孵化 S1（COD-04），S2
ARC-07 | S2 | 轮次概念（Jobs ↔ Interviews） | 同一概念 InterviewRound 两服务各建一表、字段口径不同，仅靠两个集成事件以自由文本关键词映射勉强同步，同步语义有损 | 证据：Services.Jobs/Domain/JobAggregates.cs:469 InterviewRound（9 字段）；Services.Interviews/Domain/InterviewAggregates.cs:977 InterviewRound（17+ 字段含 5 个 Json 大字段）；同步点仅 2 个：Interviews EventConsumers.cs InterviewInviteRecordedConsumer（邀约→建条目草稿）、Jobs EventConsumers.cs InterviewOutcomeRecordedConsumer（结果回写，其 MapOutcome 靠「拒/挂/通过」关键词匹配，待定/其它直接不动）；InterviewEntry 另冗余存 CompanyName/Role/JdSummary/JobApplicationId | 影响/加深方向：任一侧改轮次语义另一侧静默漂移（REQ-02 的状态矛盾即其表征）；明确轮次主数据归属（建议 Interviews 为主、Jobs 只存投影 + outcome 枚举契约入 SharedContracts），自由文本映射只留在一个 adapter 内 | 定级理由：跨服务核心概念双主、同步有损，S2
ARC-08 | S2 | 测试面（全后端 + 前端） | 只有 2 个测试项目，约 1.9 万行服务代码测试覆盖为零，且最热的纯函数解析器无测 | 证据：测试项目仅 Services.Interviews.Tests（635 行、38 个用例）与 Analysis.Worker.Tests（两项目合计 1316 行、72 个用例）；零测试服务代码量：Jobs 6517 行、Assessment 5969 行、Knowledge 2181 行、Identity 2496 行、AiGateway 1143 行、Analytics 934 行；SmartParseRules.cs 1377 行纯静态解析 0 用例却以 21 次提交居热点第 3；前端 *.spec.ts 实测 0 个（package.json 仍挂 ng test） | 影响/加深方向：interface 即测试面不成立——多数 Handler 以主构造函数直注具体 DbContext 无可替换 seam，唯一例外 PrepPlanFlowTests 的 FakeAi；先给 SmartParseRules 与 Jobs/Assessment Handler 补特征测试并抽仓储/查询 seam，解析器 leverage 最高 | 定级理由：回归防线整体缺位、与高频变更叠加，S2
ARC-09 | S2 | Program.cs 鉴权配置漂移 | JWT Bearer 配置在 7 个 Program.cs 逐字复制而未进 ServiceDefaults，且已出现实际漂移 | 证据：逐字相同的 TokenValidationParameters 块（约 15 行、ClockSkew=30s）实测于 AiGateway/Analytics/Assessment/Interviews/Knowledge/Jobs/Identity 共 7 处；BuildingBlocks/Hosting/ServiceDefaults.cs 244 行已集中日志/异常/CORS/MediatR/健康检查却独缺鉴权；漂移证据：仅 Jobs 版带默认值兜底（Services.Jobs/Program.cs:93-94），其余 6 处无；Kestrel 64MB 上限只配在 Assessment 与 Services.Gateway 两处，接收录音上传的 Interviews 未配（默认 30MB 与网关 64MB 不一致，功能轴 OBS-3 为旁证） | 影响/加深方向：改一次鉴权策略要改 7 处且已漏改出差异；JWT 注册与 Kestrel 上限收进 AddServiceDefaults，配置 locality 一处化 | 定级理由：安全配置已实际漂移，S2
ARC-10 | S3 | 跨服务 seam：AiGateway 同步 HTTP 客户端（合并 COD-16 客户端部分） | 同一 interface 被 Jobs 与 Interviews 各写一份 adapter，异常类型与 DTO 在两边重复定义，SharedContracts 未承接 | 证据：Services.Jobs/Infrastructure/Services/AiGatewayClient.cs 117 行与 Services.Interviews/Infrastructure/Services/InterviewAiGatewayClient.cs 189 行，同名 interface IAiGatewayClient 分属两命名空间，两边各自定义同名 AiGatewayException 与同构 DTO 副本，两处 Program.cs 各注册 AddHttpClient 且兜底地址同为 http://127.0.0.1:5268/；调用点 Jobs 侧 3 处、Interviews 侧 5 个 Handler | 影响/加深方向：AiGateway 契约一变要改两份客户端；客户端下沉为 SharedContracts/BuildingBlocks 中的单一 adapter（DTO 进契约） | 定级理由：重复明确但当前同构未分叉，S3
ARC-11 | S3 | 跨服务 seam：前端聚合与直调 | 前端组件绕过 API 服务层、硬编码多服务路径自行拼装跨服务数据，网关背后的服务边界在前端完全泄漏 | 证据：web/src/app/features/** 内字面量 '/api/...' 实测 181 处、散落 16 个文件（tracker 12、techstack 10、admin 8、resume 7、playbook 6…），正式 API 层仅 3 个文件（core/api/api-client.ts 187 行、practice-api.service.ts 541 行、tracker-api.service.ts 177 行）；playbook-detail.component.ts 同文件 9 处 /api/interviews + 7 处 /api/jobs，playbook-v2.api.ts 20 处 interviews + 8 处 jobs | 影响/加深方向：后端改路由/DTO 时无单一前端 seam 承接（COD-06/07/08 的契约事故正发生在此面）；按服务补齐 typed API module 并禁止组件直写路径，聚合逻辑收进 facade module | 定级理由：契约事故的结构性成因，S3
ARC-12 | S3 | 文件存储实现（Assessment / Interviews / Jobs） | 同一「落盘存文件、库里存引用」设计被实现三遍，配置键与根目录推导规则各写各的 | 证据：①Services.Assessment/Infrastructure/Storage/IAudioStore.cs 245 行 LocalAudioStore（键 Storage:RootDirectory + 环境变量 PRACTICE_STORAGE_DIR）；②Services.Interviews/Infrastructure/Storage/InterviewAudioStore.cs 198 行（注释自称「与 Assessment 同一套设计决策」，键却是 Storage:InterviewsRoot + INTERVIEW_STORAGE_DIR + 4 级回退）；③Jobs 简历文件不走文件系统、直接存库（ResumeFileHandlers.cs 156 行） | 影响/加深方向：存储策略改一处漏两处；抽 BuildingBlocks 级文件存储 adapter（统一根目录解析与相对路径约定） | 定级理由：三份实现尚未分叉出事故，S3
ARC-13 | S3 | i18n 文案机制（吸收 COD-19 的双词表面） | 同一三语（zh/en/fr）文案有 5 套并行机制/文件，组件常同时引两套，查文案无单一 locality | 证据：core/i18n/i18n.service.ts 2424 行（全仓热点第 2、38 次提交）、playbook/redesign/v2.i18n.ts 478 行、playbook/components/brief/brief.i18n.ts 139 行、components/analysis/analysis.i18n.ts 101 行、components/qa/qa.i18n.ts 93 行，合计 3235 行；访问方式至少 3 种；brief.component.ts、question-bank.component.ts 等同文件同时 import 全局与本地 i18n | 影响/加深方向：加/改一条文案先要猜它住哪套机制，漏译风险结构性存在（FUN-06/07 的文案事故与此同源）；本地字典并入核心 I18nService 命名空间分区，机制收敛为一套 | 定级理由：文案事故的结构性成因，S3
ARC-14 | S3 | 结果→outcome 映射（前端 TS / 后端 C#） | 「自由文本结果判读为 rejected/positive/closed」同一领域规则实现两遍且口径不同 | 证据：前端 web/src/app/features/playbook/redesign/v2.helpers.ts entryResultOutcome（a4fbe5a 引入，中英关键词，两处未命中策略为留 In Progress）；后端 Services.Jobs/Application/EventConsumers.cs MapOutcome（关键词→RoundOutcome，拒/挂优先、通过次之、其余不回写） | 影响/加深方向：同一用户原话在列表分组与 Tracker 回写可能得出不同结论；判读规则定为单一事实源（后端为准、前端只消费枚举，或共享同一关键词表并加对照测试） | 定级理由：口径已双轨但影响限于分组/回写一致性，S3
ARC-15 | S3 | SharedContracts 的 Analysis 契约 | 契约 module 里放了实现、且核心分析契约并未被真正的生产者使用，Worker 另维护一份「同构」副本靠注释保持一致 | 证据：SharedContracts 共 1096 行，其中 Ai/ 下是具体 LLM 客户端实现 507 行（ILlmClient.cs 162 + OpenAiCompatibleClient.cs 122 + AnthropicClient.cs 98 + AzureOpenAiClient.cs 91 + LlmClientFactory.cs 34）而非契约；Analysis.Worker/Analysis/PipelineContracts.cs 自带 SpeechMetrics 等类型，注释自述「刻意与 SharedContracts.Analysis 保持同构」，与 SharedContracts/Analysis/InterviewAnalysisReport.cs（160 行）字段靠人工对齐、无编译期约束 | 影响/加深方向：契约改字段时 Worker 侧静默失配；Worker 直接引用 SharedContracts 分析契约、删除同构副本，LLM 实现移出契约 module | 定级理由：失配风险潜伏、无编译期防线，S3
ARC-16 | S3 | Playbook 前端三代共存 | 同一 feature 内旧 components/、现行顶层组件与 redesign/ 三代实现并存，总量近万行，导航与文案 seam 被三代瓜分 | 证据：web/src/app/features/playbook/** 实测合计 9856 行：redesign/ 5770 行（intake-panel 884、prep-report 686、round-record 571 等）、旧 components/ 含 brief/rounds-timeline.component.ts 761 行、qa/question-bank.component.ts 374 行、analysis/analysis-report.component.ts 230 行，顶层 playbook-detail.component.ts 636 行同时编排新旧 | 影响/加深方向：改一处行为要先判定属于哪一代，误改旧实现风险常态化（如需求轴发现的 orphaned 组件仍在仓内）；在飞 redesign 落定后做一次删除测试清单，逐个旧组件验证可删性并下线 | 定级理由：认知负担与误改风险，未直接丢数据，S3
ARC-17 | S4 | 网关项目重复（合并 COD-17 网关部分） | solution 里躺着一个从未被使用的默认模板网关，与真正的 YARP 网关同名混淆 | 证据：src/Gateway/Program.cs 44 行是脚手架 WeatherForecast 模板（appsettings.json 无 ReverseProxy 节），与 src/Services.Gateway/Program.cs 168 行真实网关同在 YourInterview.sln；docker-compose 网关构建参数为 SERVICE: Services.Gateway，src/Gateway 无任何编排/文档引用 | 影响/加深方向：新人易开错项目；直接删除——删除测试：删之复杂度不搬家纯消失，是全仓最便宜的一次加深 | 定级理由：零运行时影响，S4
ARC-18 | S4 | 网关 CORS 与服务 CORS 口径冲突 | 网关用白名单、服务层用任意来源放行，两道 seam 的策略互相抵消 | 证据：Services.Gateway/Program.cs CORS 为白名单 3 个来源（appsettings.json Cors:AllowedOrigins）；BuildingBlocks/Hosting/ServiceDefaults.cs 的服务级 CORS 却是 SetIsOriginAllowed(_ => true) + AllowCredentials()，7 个服务经 AddServiceDefaults 全部继承——服务端口直连时网关白名单形同虚设 | 影响/加深方向：服务级 CORS 改读同一白名单配置（或非网关部署才放行），策略只在一处定义 | 定级理由：当前部署形态下风险未兑现，S4

### 架构轴已查无发现（4 面）

- 服务基座集中化：ServiceDefaults.cs 244 行统一日志/异常（RFC 9457）/JSON/MediatR 管道/健康检查，7 个业务服务全部接入，MediatR 单点注册，此面无漂移（鉴权缺位见 ARC-09）。
- YARP 路由：Services.Gateway/appsettings.json 9 条路由与 7 个下游集群与服务清单一一对应、配置驱动，健康探测带 EnabledClusters 收敛逻辑，无发现。
- 集成事件骨架：SharedContracts 被 14 个 csproj 中 11 个引用，Analytics 5 个消费者统一继承 AnalyticsConsumerBase，Jobs↔Interviews 事件幂等键设计有注释论证，结构成立（其上的数据问题见 ARC-07/14）。
- 前端 TTS 共享 module：core/tts/tts-player.service.ts 114 行被 prep-report/round-record 复用；仅其反向依赖 features/ai-practice/tts-audio-cache.ts（core 引 feature，1 处）属小瑕疵，不单独入单。

**架构轴小计**：正单 17 条（S2×8、S3×7、S4×2；另 ARC-05 已并入 COD-10）；已查无发现 4 面。

**架构轴覆盖声明**：查了——近 200 提交热点文件、前端组件与后端 Handler 的体量与职责分布（wc/grep 全量实测）、简历/轮次/文件存储/AiGateway 客户端/结果判读五组概念的跨服务落点、全部 Program.cs 与 ServiceDefaults 的配置对照、测试项目与 spec 文件存在性、SharedContracts 引用与内容构成、网关双项目与 CORS 口径。没查及原因——运行时性能与负载特征（无压测环境）、部署拓扑与容器编排细节（超出代码审查范围）、逐个 DTO 的字段级契约一致性（由代码轴高频面抽查覆盖）、在飞改动文件的对错（仅记结构事实）。

---

## 汇总

| 轴 | 正单 | S1 | S2 | S3 | S4 | 已查无发现 | 备注 |
|---|---|---|---|---|---|---|---|
| 需求（Spec） | 13 | 1 | 2 | 8 | 2 | persona 已修确认 14 条 | 另 5 编号并入他轴 |
| 代码（Standards） | 17 | 2 | 5 | 7 | 3 | 8 面 | 另 4 编号并入他轴 |
| 功能（diagnosing-bugs） | 8 | 0 | 5 | 3 | 0 | 16 条 | 另 2 编号并入需求轴；未复现观察 6 条 |
| 架构（深化） | 17 | 0 | 8 | 7 | 2 | 4 面 | 另 1 编号并入代码轴 |
| **合计（去重后）** | **55** | **3** | **20** | **25** | **7** | **28 面** | 未复现观察共 8 条 |

**三条 S1 的处置优先级**：COD-04（正常操作即静默丢数据，用户真实数据正在受损面内）→ REQ-01+COD-14（干净环境部署即整模块不可用、且被"假健康"掩盖）→ COD-01（当前单用户未爆，但隔离缺失是模型级问题，正式多用户/公网部署前必须解决）。

**协调者复核记录**：三条 S1 的关键证据已由协调者亲自读码复核（InterviewsController.cs:22 与三聚合 UserId 零命中、JobsController.cs:18 注释原文、InterviewAggregates.cs:1067-1078、RemoveAiSettings 迁移 SQL 原文）；各轴计数已交叉核对（原始条目 18+21+10+18=67，合并消解 12 条，总表 55 条与分轴小计一致）。
