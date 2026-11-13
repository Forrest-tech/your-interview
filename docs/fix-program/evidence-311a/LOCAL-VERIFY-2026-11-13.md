# 票 311a 本机验证证据（2026-11-13）

执行者：Claude（本机桌面版会话）。分支 `claude/311a-email-round-attribution`，验证时 HEAD = `aaff477`
（311a 代码提交 `e59e326`；与 `develop-new-features` = `69e716f` 的差异只有 4 个文件：
本功能 `.ts` + `.spec.ts`、`CLAUDE.md`、`FIX-STATUS.md`，**无后端改动**）。

## 环境

- Windows 10 Pro 10.0.19045，系统时区 Eastern Standard Time（UTC-5/-4）
- Node v20.20.2 / npm 10.8.2（与 CI `node-version: '20'` 一致），`NODE_OPTIONS=--max-old-space-size=6144`
- .NET SDK 8.0.428，dotnet-ef 8.0.34
- PostgreSQL 16.16（EDB 二进制，本机 5432），库 `yourinterview` + 7 schema，7 服务迁移全部成功

## 前端（web/）

| 命令 | 结果 |
|---|---|
| `npm ci` | exit 0 |
| `npx jest src/app/features/playbook/redesign/round/email-round-attribution.spec.ts` | **1 套件 / 72 例全过**（真 Jest，非垫片） |
| `npm test` | **65 套件 / 1141 例全过**（= 64 / 1069 基线 + 本票 1 / 72） |
| `npx tsc --noEmit` | exit 0 |
| `node tools/check-templates.mjs` | 30 个模板结构通过 |
| `node tools/check-mail-html-bypass.mjs` | OK |
| `npx ng build` | 成功（仅 NG8107/NG8102 既有警告，0 错误） |

## 后端（Git Bash 按 ci.yml Backend job 复现；项目列表与过滤条件同 ci.yml）

| 项目 | 结果 | 说明 |
|---|---|---|
| Services.Jobs.Tests | 514/514 | |
| Services.Interviews.Tests | 560/575（15 失败） | 时区：Npgsql 拒写非 0 偏移 DateTimeOffset（-04:00/-05:00），提醒/轮次部分更新路径；台账基线 575/575 为 TZ=UTC 环境。Windows 上 .NET 不读 `TZ` 变量，`TZ=UTC` 重跑仍 15 失败，未改系统时区 |
| Services.Knowledge.Tests | 170/170 | |
| Services.Assessment.Tests | 45 例后测试宿主崩溃（Stack overflow） | 平台：`OpenLayerStore.LoadEcdict` 的 `stackalloc Range[8]` 位于逐行循环内，栈随行数累积（91,599 行 × 64 B ≈ 5.6 MB），超出 Windows 默认 1 MB 线程栈；Linux 8 MB 不触发，故 CI 绿 |
| Services.Analytics.Tests | 83/83 | |
| BuildingBlocks.Tests | 213/230（17 失败） | 平台：测试期望值写死 Unix 路径（`/data/...`、`/tmp/...`），Windows 解析为 `C:\data\...` |
| Analysis.Worker.Tests | 116/116 | |
| StartupBudget 隔离单跑 | 宿主崩溃（同上 Stack overflow） | 平台 |

失败分类：311a 引起 **0**；时区/环境 **15 + 17 + Assessment 崩溃**；无新增失败。
RabbitMQ 本机未装，未观察到因其导致的失败。
