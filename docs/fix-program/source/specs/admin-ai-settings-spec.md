# Admin / AI 设置 现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。
> 说明：Admin（`/admin`，Identity 服务）与 AI 设置（`/account/ai-setting`，AiGateway + Assessment）是两个独立页面/两组服务，因 AUDIT REQ-16 并列为一项，本规格合册分两部分落档。

## 第一部分 · Admin（系统管理）

### 模块职责
- 路由 `/admin`（`app.routes.ts:73`）：用户管理、角色与权限管理、审计日志、系统统计。仅持 Admin 权限者可用（逐端点策略见下）。
- 前端 `web/src/app/features/admin/admin.component.ts`（518 行，页签懒加载：切到页签才拉对应接口）；后端 Identity 服务 `api/admin`（`src/Services.Identity/Api/AuthController.cs:118` 起的 AdminController）。

### 核心实体与数据真源（Identity 库）
- **AppUser**（`src/Services.Identity/Domain/AppUser.cs`）：Email/DisplayName/PasswordHash/IsActive/MustChangePassword/FailedLoginAttempts/LockedUntil/PreferredLanguage/TimeZone + RefreshToken、AuditLog 实体同文件。
- **AppRole / RolePermission / UserRole**：角色—权限点多对多；权限点目录为代码常量（SharedContracts `Permissions` + `PermissionPolicy`），端点 `GET /api/admin/permissions` 供前端渲染勾选。
- 端点全表（`AuthController.cs:125-246`）：stats、users CRUD、users/{id}/reset-password、users/{id}/unlock、roles CRUD、permissions、audit。权限分工：读 = AdminUsersRead、用户写 = AdminUsersWrite、角色写 = AdminRolesWrite、审计 = AdminAuditRead。

### 主要流程
1. 用户：建/改（含分配角色）/重置密码（置 MustChangePassword）/解锁（清 LockedUntil 与失败计数）/停用启用/删除——前端本地信号列表，写后重拉。
2. 角色：建/改权限点集合/删除（系统角色 IsSystemRole 不可删的口径在 Identity 播种与 Handler）。
3. 审计：AuditLog 由 `AuditSaveChangesInterceptor` 落库，Admin 页只读查询。

## 第二部分 · AI 设置（账号级 AI 配置）

### 模块职责
- 路由 `/account/ai-setting`（`app.routes.ts:91`）：当前**登录用户自己**的 AI 相关配置，四张卡：①Azure Speech（发音评测）②LLM 连接 ③备考数据源（Prep sources）④说明卡（评测说明/教程，无数据）。存储偏好不在本页（在 Profile 页，见 `ai-practice-spec.md`）。
- 前端 `web/src/app/features/ai-setting/ai-setting.component.ts`（548 行），经 `core/api/practice-api.service.ts` 调三组后端。

### 核心实体与数据真源
- **LLM 配置 = AiSetting**（AiGateway 库，`src/Services.AiGateway/Domain/AiSetting.cs`）：**UserId 主键、一人一条**；Protocol（openai-compatible / azure-openai / anthropic——协议非厂商名）、ApiKey（**只存服务端，任何接口只回掩码**）、BaseUrl/Model/Endpoint/ApiVersion/DisplayName。端点 `api/ai`：GET settings（回状态+掩码）、GET providers（预设清单）、PUT/DELETE settings、POST test-credential（`src/Services.AiGateway/Api/AiController.cs:30-78`）。保存门禁：前端要求先 test 成功才可保存（`aiCanSave` = key 可用 && tested===true，ai-setting.component.ts:335）。
- **备考数据源 = PrepSourceSetting**（AiGateway 库，`Domain/PrepSourceSetting.cs`）：UserId 主键；四开关 QuestionBank/CompanyBackground/WebExperiences/Glassdoor Enabled + SearchProvider/SearchApiKey（掩码回填、提交按「不改」处理）。端点 `GET/PUT /api/ai/prep-sources/settings`、POST search（JobsWrite）。与 Playbook 定稿 §4.1「数据源在设置中配置、生成时按启用集抓取并如实标注」对应——设置面即此实体。
- **Speech 配置 = SpeechSetting**（Assessment 库，见 `ai-practice-spec.md`）：端点 `api/assessment/speech/settings|test-credential|test`（MockManage 写、MockRead 读）。前端仅留 `azure_speech_configured` localStorage 标记位，非数据真源。
- **统一调用面**：其余服务不直连厂商，经 AiGateway `POST /api/ai/complete` 与 `POST /api/ai/vision/extract-text`（视觉读图，Playbook 截图解析的兜底通道；主通道为共享 OCR，见 platform-ocr-spec）以当前用户凭据调用；Jobs/Interviews 各持一份 AiGateway 客户端适配（ARC-10，未收敛，属架构现状非本页口径）。

### 与既定规则对照结论
- 凭据单点：LLM key 只存 AiGateway、Speech key 只存 Assessment，换 key 一处生效——**符合** AiSetting 领域注释的设计三条（同文件文件头）。
- Admin 与个人设置边界清晰：Admin 管他人账号/角色；AI 设置只管自己（端点一律以当前登录 UserId 取行，无 userId 入参）——符合 COD-01 修复后的隔离口径（票 03）。

## 现状与规则冲突
- **CONF-ADM-1（→ AUDIT REQ-14 同型，票 45 范围外残留）**：Admin 页用户停用/启用、删用户、删角色仍用原生 `confirm()` 三处（`admin.component.ts:269`、`:316`、`:406` 实查），REQ-14 原文只点了 Tracker/Tech Stack，此为同型扩散面，索引回 REQ-14 不新造编号。
- **CONF-ADM-2（观察，无 AUDIT 编号）**：AI 设置页跨三服务拼装（AiGateway ×2 组 + Assessment ×1 组），任一服务不可达时对应卡片单独失败、无整页级错误汇总——现状如此，排障时按卡片定位服务，勿误判为整页故障。
- **CONF-ADM-3（→ Playbook 定稿 §4.1 对照）**：Prep sources 设置已落实体与开关，但 Playbook 备考生成是否按每个开关真实抓取/标注，属 Playbook 模块验收面（定稿审计已覆盖），本规格只确认设置面存在且口径与定稿一致，不重复判定生成面。
