# 票 88 — CI 流水线（GitHub Actions）从零建立 + 覆盖率门（系统提升 A1）

严重度：S2（护航项——给 87 张修复票上保险，没有 CI 则"测试全过"无法被正式验收引用）
来源：Forrest 补充任务单 A 组 A1（2026-10-23）

## 事实（已核实，2026-10-23 沙盒内）
- 仓库**零 CI**：无 `.github/` 目录、无 `.github/workflows/`（已 `ls` 核实）。
- 覆盖率基建**已存在**，本票是"接上 CI"而非"重建基建"：
  - `scripts/run-coverage.sh`（票 28 / ARC-08a 落地）：coverlet.console 包装 xunit console runner，出 opencover 报告。
  - `scripts/merge-coverage.py`：按 (模块, 文件, 行列) 对 SequencePoint 取并集，规避 coverlet 自带 `--merge-with` 的 ModuleHash 不匹配丢模块问题（票 28 实测结论）。
  - 各 `*.Tests.csproj` 已接入 `coverlet.msbuild` 6.0.4（中央版本 Directory.Packages.props）。
- 后端整体覆盖率基线 **8.14%**（交接书口径；Jobs 10.14% / Interviews 6.75%）。
- 本沙盒现状：`dotnet test` 的 vstest↔testhost 通道**在本沙盒是好的**（全量 502 例 45 秒通过），与脚本头注所述"沙盒通道被 egress 代理污染"不同——那是助手侧 VM 的问题。**CI 环境（GitHub-hosted runner）不是那个 VM，应走标准通道。**

## 任务
1. 建 `.github/workflows/ci.yml`，触发条件：`push` 与 `pull_request` 到 `develop-new-features`（**不是 main**——main 合并另需 Forrest 点头）。
2. **Backend job**：
   - `actions/setup-dotnet` 装 .NET 8.0.x；`dotnet restore` + `dotnet build --no-restore`（全解决方案）。
   - 起 PostgreSQL service container；建 `yourinterview` 库 + 7 个 service schema；对七个服务跑 `dotnet ef database update` 建表 —— **DB 门控套件必须先有 schema，否则测试假红或假绿**（run-coverage.sh 头注既定规程）。
   - 七个测试项目各出一份 opencover；走标准通道 `dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover`（CI 环境无代理污染，不需要 coverlet.console 包装层；若 CI 实测 vstest 通道有问题，再切 run-coverage.sh 路径并用环境变量覆盖其硬编码的 `~/workspace/.dotnet`、`~/workspace/.tools/coverlet`、`~/.nuget/.../xunit.console.dll` 三处路径）。
   - `python3 scripts/merge-coverage.py *.opencover.xml` 出整体数字。
3. **覆盖率门（只升不降）**：取合并后的 `OVERALL` 行与基线比较，低于基线即 `exit 1` 让 CI 变红。
   - ⚠️ **基线落地口径（重要，避免 CI 永远红）**：交接书给的 8.14% 是助手侧 VM 实测。CI 首次绿跑的实测数字**若因环境差异与 8.14% 不同，以 CI 首次实测数字为新基线**，并在 FIX-STATUS 覆盖率记账节同时记录"助手侧 8.14% / CI 首跑 X%"，差异原因写进报告，不许静默取小值或取大值。
4. **Frontend job**：`setup-node` 20 + `npm ci`（web/ 下）+ `npx tsc --noEmit` + `npx ng build`。
   - 已知 `pdfjs-dist` 是 CommonJS 会产生 optimization bailout warning——**warning 不算红**，只有 error 才算。
5. 两个 job 都失败时 CI 必须整体红（不要 continue-on-error）。

## 验收
- **反向验证（必须做，这是本票的核心验收）**：另开一个测试分支，故意加一个必然失败的测试（如 `Assert.True(false)`）并/或故意把覆盖率门基线抬高到不可能达到的值，推送后 CI **必须红**；截图/链接入证据。只验"绿能绿"不算验收。
- 正常分支 CI 绿的结果，需可被正式验收阶段直接引用（门禁结论可追溯：workflow run 链接 + 覆盖率数字 + 测试通过数）。
- 全量后端套件（当前 502 例）与前端 tsc/build 在 CI 上都能跑通。
- 证据落盘：`docs/fix-program/evidence-88/`（ci.yml 全文、红/绿两次 run 的链接与截图、覆盖率数字）。

## 依赖与阻塞
- **推送权限**：当前仓库远端内嵌的 GitHub PAT 已失效（直连报 `Invalid username or token`；gh-proxy 中转拒写）。CI 文件要生效必须先推送到远端。**需 Forrest 提供新的 PAT**，否则本票只能本地写好文件、无法验证"CI 真的会红"。
- 本票不改动任何产品代码，只加 `.github/workflows/ci.yml` 与（如需）基线常量文件。

## 不变项
- 不碰 main（含不设 `main` 触发、不 force-push 共享分支）。
- 不改七个服务的产品代码、不改既有测试语义。
- 不动 `scripts/run-coverage.sh` / `merge-coverage.py` 的合并口径（票 28 已验证的等价关系不容回退）。
