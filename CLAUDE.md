# CLAUDE.md

本仓库是 **Your Interview**（.NET 微服务后端 + Angular 前端）的面试准备/求职追踪平台。

## 最高执行规则

**`docs/fix-program/PROTOCOL.md`（Forrest 制定的修复工程执行协议）是本仓库的最高执行规则。**
所有修复/重构工作按其执行：启动对账（回执三数）→ 工单状态机（TODO/DOING/PUSHED/VERIFIED/BLOCKED-*）→
关票四步（实现 → 测试套件+邻居全绿 → 活体证据 → `git ls-remote` 远端确认）。
工单队列与状态唯一事实源：`docs/fix-program/FIX-STATUS.md`。与任何其他口径冲突时，以 PROTOCOL 为准。

## Agent skills

### Issue tracker

本地 Markdown 工单；主台账为 `docs/fix-program/FIX-STATUS.md`（PROTOCOL 队列），
新功能/试验工作按 `.scratch/<feature-slug>/` 约定落地。见 `docs/agents/issue-tracker.md`。

### Triage labels

五个标准分诊角色（`needs-triage` / `needs-info` / `ready-for-agent` / `ready-for-human` / `wontfix`），
本地 Markdown 台账下写进工单文件的 `Status:` 行。见 `docs/agents/triage-labels.md`。

### Domain docs

单上下文布局（`GLOSSARY.md` + `docs/adr/` 在仓库根）；本仓库既有领域文档以
`docs/fix-program/` 体系为优先事实源。见 `docs/agents/domain.md`。

## 技术栈要点

- 后端：.NET 微服务（Identity / Jobs / Interviews / Knowledge / Analytics / Assessment / AiGateway / Analysis.Worker），共享契约在 `src/SharedContracts`，横切关注点在 `src/BuildingBlocks`
- 前端：Angular（`web/`，standalone components + signals + Material），API 访问经 `web/src/app/core/api/*.service.ts` typed 服务层（票 42 ARC-11 收敛成果，组件不自己拼 `/api/` 路径）
- 测试：后端 xUnit（九个 `*.Tests` 项目，基线以台账对账为准）；前端 `ng build` + `tsc --noEmit`；E2E 黄金路径接入 CI（票 88/89）
- 错误口径：后端 4xx/5xx 走 ProblemDetails；前端 `ApiClient.rethrow` 统一转可读文案

## 常用命令

```bash
# 前端类型检查（全量构建 OOM 时用）
cd web && npx tsc --noEmit

# 后端构建与测试
dotnet build src/YourInterview.sln   # 或对应 .sln
dotnet test

# 本地起停（Mac）：见仓库根 README-本地运行Mac.md；后端全栈 docker compose up -d（改哪个服务只编哪个：docker compose up -d --build <service>）
```

## 当前交接状态（2026-11-13，由云端会话移交给本机桌面版会话）

开发已从 Muse 移交给 Claude（PROTOCOL v1.1）。新会话开始时先读：
1. `docs/fix-program/PROTOCOL.md`
2. `docs/fix-program/FIX-STATUS.md` 末尾（票 307–313 与 311a 条目）
3. `docs/fix-program/handover/MUSE-HANDOVER-NOTES.md`

### 进行中
- **票 311a**（邮件→轮次归属纯函数）：**VERIFIED**（2026-11-13 本机真 Jest 72/72、全量 65 套件 / 1141 例），PR #3 已合入 `develop-new-features`（`a62ca59`）。
- Windows 本机后端测试已知环境失败（非代码回归，CI 绿）：Interviews 15（系统时区非 UTC）、BuildingBlocks 17（测试写死 Unix 路径）、
  Assessment 测试宿主栈溢出（`OpenLayerStore.LoadEcdict` 循环内 `stackalloc`）。详见 `docs/fix-program/evidence-311a/LOCAL-VERIFY-2026-11-13.md`。
- 规则 3 平局 → 未指派（不取更早轮次），以代码与台账为准。

### 票序（用户已同意）
1. 给 CI 加 Jest 与 check-templates 步骤（先问用户再改 CI；可能暴露既有失败）
2. **314a** 邮箱刷新透明度：快照增加"无信号跳过"等计数（复用同一 reason 枚举）
3. **311b1 → 311b3 → 311b2**（一起合入后才暴露入口）：按 311a 的归属结果分组展示。开工前需要 Muse 的完整 311 SPEC.md（目前只拿到部分）
4. **314b** 新表：只存发件人域名、日期、reason 枚举、不透明 run id、计数；**不得存主题、正文、Gmail message id**；写失败吞掉；每用户 200 次、90 天清理；可回滚迁移；先只写不读
5. **314c** 前端读取 + 确认对话框 + 撤销（只移除新加的域名，只走既有 rules 端点）

### 工作约定
- 和用户用中文沟通；用户不想亲自协调，需求确认、和 Muse 对接由 Claude 处理。
- 方案取舍做独立评分（另起子代理、给中性需求，不让它看到自评）。
- 每张票：功能分支 → 本机跑 Jest / tsc / check-templates / ng build / 相关 dotnet test → PR → CI 绿 → 台账记录。不推 develop-new-features 直推。
- 商业方向（用户已定）：个人用户、北美、按月/年/永久收费；不同订阅的功能和限额由管理员配置。路线图见审计报告与交接文档（claude.ai 文档）。

### Windows 本机命令
```powershell
cd web; npm ci; npm test; npx tsc --noEmit; node tools/check-templates.mjs; npx ng build
cd ..; dotnet test src/YourInterview.sln
```
