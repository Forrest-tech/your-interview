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
