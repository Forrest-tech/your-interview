# 03: COD-01 三服务用户隔离（模型级，方案先行）

**审计编号：** COD-01（严重度 S1）
**区域：** Interviews/Knowledge/Jobs 后端
**Blocked by:** 01 (Interviews 区域串行)
**Status:** ready-for-agent

## What to build

【动手前先在本票补写方案节并经协调者确认后才实现】Interviews、Knowledge、Jobs 三服务核心实体（投递/公司/轮次/沟通/条目/知识等全局共享实体）加 UserId 归属：写入时从 ICurrentUser 盖当前用户、全部读写查询按当前用户过滤（全局 query filter + Handler 谓词双保险）、跨用户访问返回 404。存量数据回填归属现有管理员用户（admin@your-interview.local 对应 Identity 用户），一行不丢。expand–contract：先加可空列+盖章+回填，再开过滤，最后收紧非空。证据：AUDIT.md COD-01。

## 实施方案（待协调者确认）

> 调查基线：仓库 `~/workspace/your-interview` @ `develop-new-features` 88b2607（只读调查，2026-10-21）。本节为方案，未动任何代码。

### 0. 现状速览（调查结论）

- **ICurrentUser 已存在且完备**：`src/BuildingBlocks/Security/CurrentUser.cs:15`（`ICurrentUser`/`HttpCurrentUser` 从 JWT `sub`/`NameIdentifier` claim 取 UserId；`RequireUserId()` 扩展未登录抛 401；`AddCurrentUser()` 注册扩展）。Assessment 是现成范本：Controller 注入 `ICurrentUser` → 每个 Query/Command record 首参带 `Guid UserId` → Handler 全部 `x.Id == id && x.UserId == r.UserId` 谓词（`AssessmentHandlers.cs`/`PracticeHandlers.cs` 全量如此）。**注意：全仓没有任何 DbContext 用 query filter 做用户隔离**——现有 `HasQueryFilter` 先例只有软删（`!x.IsDeleted`，三服务 DbContext 均有）。本票的「filter+谓词双保险」中 filter 一层是新建机制，谓词一层照 Assessment 先例。
- **三服务注入现状**：
  - Jobs：`JobsController.cs:16` 已注入 `ICurrentUser`，`Program.cs:74` 已 `AddCurrentUser()`；但 `Me => currentUser.UserId ?? Guid.Empty`（:19）——未登录回落空 Guid，须改 `RequireUserId()`。UserId 目前只传给用户级资产的命令（简历/求职信/问答模板），公司/投递类命令不带。
  - Interviews：`Program.cs:26` 已 `AddCurrentUser()`（为事件发布器/Outbox 拦截器而注册），但 `InterviewsController.cs:22` 未注入、全部 Handler 不带 UserId。
  - Knowledge：最差——`KnowledgeController.cs:20` 未注入、`Program.cs` **根本没调 `AddCurrentUser()`**，动手第一步就是补注册+注入。
- **DbContext 结构**：三者构造均只收 `DbContextOptions`，不注入 `ICurrentUser`。加 filter 需改构造注入（scoped `ICurrentUser`），filter 表达式用捕获属性（`x.UserId == _currentUserId && !x.IsDeleted`，与现有软删 filter 合并为一条）。`IgnoreQueryFilters()` 现有调用点共 3 处（KnowledgeHandlers.cs:312 回收站列表、:577 Restore、JobHandlers.cs:595 恢复/查删）——这些点绕过 filter 后**必须自带 UserId 谓词**，是双保险的已知缺口，逐点列入改动清单。
- **迁移目录现状**：Interviews 15 个迁移（其中 AddRoundDetails 缺 Designer，COD-09 另票，勿混）、Knowledge 3 个、Jobs 12 个，三件套除 COD-09 外齐全。

### 1. 实体清单与处置

处置口径（**待裁决点 A**）：推荐**只在聚合根加 `UserId` 列**，子实体不加列——子实体全部经 FK Cascade 挂在聚合根下、不可能脱离父独立存在，隔离在根上成立；Handler 动子实体前先按根归属校验（或经根 Include 加载）。理由：回填表数从 14 降到 5、杜绝父子 UserId 不一致、且后台组件（见 §5）直查子表时不受 filter 误伤。若协调者要求每表加列（口径 B），迁移/回填量按 §6 的 B 数执行。

| 服务 | 实体 | 文件 | 现状 | 处置（口径 A） |
|---|---|---|---|---|
| Jobs | Company | Domain/JobAggregates.cs:9 | 无 UserId，全局共享；Name 全局唯一索引 | **加 UserId**；唯一索引 Name → (UserId, Name)（contract 步换） |
| Jobs | JobApplication | Domain/JobAggregates.cs:100 | 无 UserId | **加 UserId**（构造盖章） |
| Jobs | ApplicationCommunication | Domain/JobAggregates.cs:651 | 无 UserId；注释自认「跟随投递」；但有独立 DbSet 且按自身 Id 直查/改/删 | **例外加 UserId**（写入时从所属 Application 盖章，不从请求另传） |
| Jobs | ApplicationStatusChange | Domain/JobAggregates.cs:452 | 子实体（ApplicationId） | 不加列，经 Application 归属 |
| Jobs | InterviewRound (Jobs) | Domain/JobAggregates.cs:469 | 子实体（ApplicationId） | 不加列，经 Application 归属 |
| Jobs | UserResume / CoverLetter / ResumeFile / ApplicationAnswerTemplate | JobAggregates.cs:561/606、CoverLetter.cs:45、ResumeFile.cs:23 | **已有 UserId**（对照组，隔离已成立） | 不动；仅 CoverLetter 删除路径补谓词（CoverLetterHandlers.cs:185 DeleteCoverLetter 不带 UserId，即 COD-03——本票顺手收口，删除命令补 UserId 参数+谓词） |
| Interviews | InterviewEntry | Domain/InterviewAggregates.cs:122 | 无 UserId（聚合根） | **加 UserId**（构造盖章） |
| Interviews | InterviewAsset / InterviewQuestion / InterviewWeakness / InterviewRound / GeneratedMaterial / InterviewAction / AnalysisSnapshot | InterviewAggregates.cs:716/822/934/977/1102/1143/1187 | 子实体（InterviewEntryId） | 不加列，经 Entry 归属；按自身 Id 直查的端点（如 `GET rounds/{roundId}/pass-probability`）改走 Entry 联查校验 |
| Interviews | AnalysisJob | Domain/AnalysisJob.cs:49 | 台账/发件箱（InterviewEntryId），由后台派发器直查 | 不加列、不加 filter（PayloadJson 已含发起用户 UserId，足够审计） |
| Knowledge | KnowledgeItem | Domain/KnowledgeAggregate.cs:24 | 无 UserId（聚合根） | **加 UserId**（构造盖章） |
| Knowledge | KnowledgeReviewLog / KnowledgeRelation | KnowledgeAggregate.cs:367/394 | 子实体（ItemId；Relation 另有 RelatedItemId） | 不加列，经 Item 归属；AttachRelation 须校验 RelatedItemId 同属当前用户（防关联他人条目泄漏标题） |

跨服务引用字段（InterviewEntry.CompanyId/JobApplicationId、KnowledgeItem.SourceInterviewEntryId/SourceJobApplicationId）只是外部 Id 引用、无 FK：归属以本行 UserId 为准，他人 Id 被填入也只影响自己行的展示关联，不构成越权读——接受，不额外校验（Knowledge 的 ImportInterviewCandidates 同理）。

### 2. expand–contract 分步（每步可独立部署/验证/回滚）

**Step 1 · expand（加列+盖章，过滤未开）**
- 迁移 ×3（每服务 1 个 `AddUserIdOwnership`）：口径 A 下 Jobs 给 companies/applications/application_communications、Interviews 给 entries、Knowledge 给 knowledge_items 加**可空** `UserId uuid` 列 + 普通索引 `(UserId)`；Company 的 Name 唯一索引此步不动。
- 代码：聚合根构造加 `Guid userId` 参数并存属性；Controller 全部命令/查询 record 首参加 `Guid UserId`（Assessment 式），由 Controller 以 `RequireUserId()` 传入（Jobs 的 `Me` 同步改掉 Guid.Empty 回落）；创建路径全量盖章——含 JobHandlers、SmartAddHandlers（按名 find-or-create 公司改为 **(Name, UserId)** 双条件）、BulkImport、Knowledge 的 Create/Import/ImportInterviewCandidates、Interviews 的 Create。Handler 查询谓词此步**不加**。
- 验证标志：双账号各新建一条，SQL 查新行 UserId 与各自 token 用户一致；旧数据列表可见性与改前一致（过滤未开）；`dotnet test` 全绿（Interviews.Tests 构造签名连带改测试夹具，用固定测试 Guid）。
- 回滚：还原代码即可（可空列留着无害）。

**Step 2 · 回填（独立 SQL 脚本，非 EF 迁移；脚本存放位置待裁决点 D）**
- 口径见 §3。执行后对账通过才进 Step 3。
- 验证标志：每表 `UserId IS NULL` 计数 = 0；回填前后总行数逐表相等；admin 登录三服务列表计数与 Step 1 前基线一致。

**Step 3 · 开过滤（双保险生效）**
- DbContext ×3：构造注入 `ICurrentUser`，聚合根 filter 合并为 `!x.IsDeleted && x.UserId == _userId`（子表 filter 不动/无）；注意 DbContext 被 Seeder/后台组件以无 HTTP 上下文方式解析时 UserId=null、filter 匹配零行（fail-closed）——Seeder 只跑迁移+原生 SQL、不查实体，已核实安全。
- Handler 谓词全量补齐：`FirstOrDefaultAsync(x => x.Id == id && x.UserId == r.UserId)` 型改写（现存 Id 单条件查询实测 Interviews 29 处 / Knowledge 14 处 / Jobs JobHandlers 17 处，另加全部列表/统计/聚合查询的 Where——Stats、TrackerStats、Topics、DuePlan、CompanySummaries、Coverage 等统计面最易漏，逐个点名核查）；查不到统一走现有 NotFound 映射 → **404**（不返回 403，不泄漏存在性）。
- 3 处 `IgnoreQueryFilters()` 调用点自带 UserId 谓词（回收站列表只看自己的已删项、Restore 只恢复自己的）。
- 非请求路径按 §5 改造（消费者/发布器/Worker 回写）与本步同批上线。
- 验证标志：验收标准 2 的双账号实测逐服务通过（B 看 A 的详情/改/删全 404、列表为空集）；Worker 转写→分析全链路仍通（服务账号回写回归，见 §5 风险 1）；邀请事件自动建 Playbook 草稿仍通（消费者回归）。

**Step 4 · contract（收紧非空）**
- 迁移 ×3（每服务 1 个 `RequireUserIdOwnership`）：`UserId` 改 NOT NULL；Jobs 换 Company 唯一索引为 (UserId, Name)；EF 配置 `IsRequired()`。
- 验证标志：空库（全新 migrate）与存量库（Step 1–3 后）各完整跑一遍迁移链无错；`dotnet test` 全绿；前端 Tracker/Playbook/Tech Stack 冒烟通过（验收标准 4）。

### 3. 回填 SQL 口径与对账

- **归属人**：Identity 用户 `admin@your-interview.local`。获取方式（只读）：`SELECT "Id" FROM identity.users WHERE "Email" = 'admin@your-interview.local' AND NOT "IsDeleted";`（邮箱在库中为 Normalize 后的原形，Seeder `IdentityDbSeeder.cs:53` 同口径）。本地沙箱实测值 `d89dcf17-cad5-463d-9eb3-a6aec8898a2a`（**仅本地值，目标环境执行前必须重新查，不许硬编码进脚本**——脚本用子查询取 Id，不写字面 Guid）。
- **交叉锚点（已验证）**：既有用户级表 jobs.resumes / jobs.resume_files 现存行的 UserId 恰为该 admin Id，证明三服务共用同一 Identity 用户空间、回填目标无歧义。对账时加查：`SELECT DISTINCT "UserId" FROM jobs.resumes` 应只有 admin 一行。
- **回填范围**：口径 A 共 5 表——jobs.companies、jobs.applications、jobs.application_communications、interviews.entries、knowledge.knowledge_items；**含软删行一并回填**（`WHERE "UserId" IS NULL`，不加 IsDeleted 条件），保证回收站/恢复语义一致。每表一条参数化 UPDATE，子查询取 admin Id；写成单个事务脚本，任一表行数不对即整体回滚。
- **基线计数（本地沙箱 2026-10-21 实测，目标环境执行前须重测作基线）**：entries 4、assets 10、questions 2、rounds 8（Interviews 子表仅作总数对账）、companies 5、applications 9、application_communications 5、application_status_changes 27；**本地库尚无 knowledge schema**（该服务未在此沙箱跑过），knowledge_items 基线必须在目标环境（用户 Mac 的 docker 库）补测——这是本方案唯一没拿到的实测数。
- **对账方法**：回填前逐表 `count(*)` 存档 → 回填 → 复查 ① 总行数逐表不变 ② `count(*) WHERE "UserId" IS NULL` = 0 ③ `count(*) WHERE "UserId" = admin` = 总行数（三者同时成立才算过）→ admin 登录调三服务列表 API，计数与基线一致（验收标准 3）。

### 4. 过滤落点汇总（双保险）

- **第一层 · 全局 query filter**：三 DbContext 聚合根实体（Company、JobApplication、ApplicationCommunication、InterviewEntry、KnowledgeItem）`HasQueryFilter(x => !x.IsDeleted && x.UserId == _currentUserId)`，`_currentUserId` 为 DbContext 构造注入 `ICurrentUser` 后捕获的 `Guid?` 属性（EF 可翻译的标准写法）。
- **第二层 · Handler 谓词**：所有 Query/Command record 带 UserId、查询显式谓词（§2 Step 3 清单）。作用：filter 被 IgnoreQueryFilters 绕过、AsNoTracking 直查子表、联查（如 Jobs 按 Company 名搜 Applications 的子查询 `db.Companies.Any(...)`，JobHandlers.cs:372）时仍有第二道闸；且谓词使「查不到 → 404」的语义在 Handler 层显式成立。

### 5. 风险点：非请求上下文的写入归属（必须先想清的）

1. **Analysis.Worker 服务账号回写（最高风险）**：Worker 以独立服务账号 `service.analysis@your-interview.local`（docker-compose 配置，`Analysis.Worker/Consumers/ServiceTokenProvider.cs` 登录取 token）调 Interviews 的回写端点（POST transcript / analysis / failure、GET transcript）。其 token UserId ≠ 条目主人——Step 3 过滤一开，**全部回写会 404、转写/分析流水线整体断掉**。请求体目前不带主人 UserId，但链路源头有：`InterviewAnalysisRequested.UserId` 与 AnalysisJob.PayloadJson 都带着发起用户 Id。推荐方案（**待裁决点 B**）：回写三端点+GetTranscript 的命令加 `OwnerUserId` 参数（Worker 从事件 `m.UserId` 传入），Handler 对这组端点以「调用方须具 InterviewsAnalyze 权限（服务账号）+ 按 Id 查（IgnoreQueryFilters）+ 校验 `entry.UserId == OwnerUserId`」处理，不走当前用户谓词；备选方案：服务账号身份全量豁免 filter（实现简单，但服务账号泄露即全量可写，且与现状等价、不算回退）。二选一须协调者拍板。
2. **MassTransit 消费者（无 HTTP 上下文，ICurrentUser.UserId = null）**：
   - Interviews `InterviewInviteRecordedConsumer`（Jobs 邀请 → 自动建 Playbook 草稿）：事件 `InterviewInviteRecorded` **已带 UserId**（IntegrationEvents.cs:63）。改造：查既有草稿走 `IgnoreQueryFilters()` + (JobApplicationId, RoundNo) 幂等键不变；新建 Entry 盖章 `e.UserId`。
   - Jobs `InterviewOutcomeRecordedConsumer`（Playbook 结果 → 回写 Tracker 轮次）：事件已带 UserId（:83）。改造：`IgnoreQueryFilters()` 按 Id 取 Application 后**校验 `app.UserId == e.UserId`**，不一致记日志跳过（沿用现有「对不上就跳过、不进死信」风格）。
   - 通则：消费者一律 IgnoreQueryFilters + 事件 UserId 显式校验，禁止依赖 filter（null 用户下 filter 是零行 fail-closed，靠它只会静默丢消息处理）。
3. **领域事件发布器改从实体取 UserId（隔离后顺带修的既有隐患）**：Jobs `JobStatusChangedPublisher` 现从 `ICurrentUser` 取 UserId 填 `JobApplicationStatusChanged`/`InterviewInviteRecorded`——在消费者作用域内转发时（如结果回写引发状态变更）HttpContext 为空、会发出 UserId=Guid.Empty 的事件污染 Analytics 投影。聚合根有 UserId 后，发布器应改读**聚合自带的 UserId**（领域事件在聚合内产生时直接带入），不再依赖 ICurrentUser。Interviews `InterviewsIntegrationEventPublisher` 的 InterviewOutcomeRecorded 同型同改。Knowledge `KnowledgeMasteryChangedPublisher.cs:25` 现在干脆写死 `Guid.Empty` 发 `KnowledgeLevelChanged`——改填 item.UserId（否则 Analytics 雷达按空用户记账；是否属本票范围见待裁决点 C）。
4. **后台服务**：AnalysisJobDispatcher（直查 analysis_jobs + 联查 entries 判卡单——entries 联查在 Step 3 后会被 filter 拦成零行！须给该联查加 IgnoreQueryFilters，它是系统级自愈逻辑、跨全部用户）与 AudioIntegritySweepService（直查 assets 子表，口径 A 下 assets 无 filter、不受影响）。Dispatcher 这一处是 Step 3 最易漏的回归点，列入验证清单。
5. **统计/聚合面**：所有跨行聚合（TrackerStats、InterviewStats、Topics、DuePlan、Coverage、GetCompanySummaries）必须确认走带 filter 的 DbSet 且无原生 SQL 旁路；InterviewsDbSeeder 的原生 SQL 只是补列 DDL、不读数据，安全。
6. **测试面**：只有 Interviews 有测试项目（构造签名变更连带夹具）；Jobs/Knowledge 无测试项目，双账号验证只能靠 API 实测脚本（验收标准 2 本就如此要求）。

### 6. 预估迁移数量

- **口径 A（推荐）：共 6 个**——每服务 expand 1 + contract 1（Jobs 的 contract 含 Company 唯一索引替换）。回填为独立 SQL 脚本，不占迁移数。
- 口径 B（每表加列）：仍可每服务合并为 expand 1 + contract 1 = 6 个迁移文件，但回填表数 5 → 14（子表按父表 join 回填），且 §5 风险 4 的后台直查面全部要加绕过，工作量与回归面显著变大。

### 7. 前端影响面（已验证：零改动）

- `web/src/app/core/interceptors/auth.interceptor.ts` 对全部请求统一挂 `Authorization: Bearer <token>` 并处理 401 刷新——用户上下文只走 token，服务端从 claim 取 UserId，前端无需传任何用户标识。
- grep 核查 tracker / playbook / techstack 三模块及其 API service：**没有任何调用传 userId 参数**（全仓前端 `userId` 仅出现在 admin 审计日志模型 `api.models.ts:841`，与三服务无关）。结论：前端零改动，仅 Step 4 后做主流程冒烟。

## 协调者裁决（2026-10-21，方案确认，本节为实现准绳）

- **A（加列口径）→ 采纳推荐口径 A**：只在 5 个聚合根加 UserId（Jobs: Company、JobApplication、ApplicationCommunication；Interviews: InterviewEntry；Knowledge: KnowledgeItem），子实体经父归属隔离，Handler 动子实体前先校验根归属。
- **B（Worker 回写）→ 采纳推荐方案**：回写端点（POST transcript/analysis/failure、GET transcript）命令加 OwnerUserId（Worker 从事件传入），Handler 以「 InterviewsAnalyze 权限 + IgnoreQueryFilters 按 Id 查 + 校验 entry.UserId == OwnerUserId」处理；**不给服务账号全量豁免**。
- **C（Knowledge mastery 发布器）→ 并入本票**：KnowledgeMasteryChangedPublisher 的 Guid.Empty 改填 item.UserId，随本票一并修，不留给后续票（同属归属链，拆开会留污染窗口）。
- **D（回填位置）→ 否决独立脚本，改为 EF 数据迁移**：expand → backfill → contract 三段迁移/服务（共 9 个）。理由：用户真实库在其 Mac，部署必须零手动步——pull+rebuild 自动跑迁移；独立脚本一旦漏跑，contract 的 NOT NULL 会炸或数据被 filter 吞。回填迁移要求：按邮箱子查询取 admin Id（不硬编码 GUID）、带存在性守卫（identity 表/用户不存在时安全跳过、只填 NULL 行、幂等）；本地库对账锚点（jobs.resumes/resume_files 现存 UserId）实现时复核。残余 NULL 导致 contract 失败属可接受的响亮失败（与票 02 fail-fast 同向），在迁移注释里写明。
- **附带收口**：COD-03（DeleteCoverLetter 无归属校验）随本票 Jobs 面一并修（票 20 届时按「已修（并入 03）」关闭）。
- **实现门禁**：本票实现等票 01、票 02 均落地验收后开工（Program.cs 与 Interviews 区域串行）；Knowledge 面动手前先在沙盒启动 Knowledge 服务建出 schema 并补测基线行数（方案 §3 的缺口在实现报告里闭环）。

## Acceptance criteria

- [ ] 方案节先行：实体清单、回填口径、过滤落点、迁移清单写进本票并获确认
- [ ] 双账号实测：账号 B 登录后看不到/改不了/删不了账号 A 的投递、Playbook 条目、知识条目（API 层逐服务验证，返回 404/空集）
- [ ] 存量数据回填后原账号可见且条数与回填前一致（计数对账）
- [ ] 每条迁移带 Designer.cs+ModelSnapshot；dotnet test 全绿；前端主流程（Tracker/Playbook/Tech Stack）冒烟通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
