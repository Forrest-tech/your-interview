# 02: REQ-01+COD-14 迁移守卫与启动失败不再假健康

**审计编号：** REQ-01, COD-14（严重度 S1）
**区域：** Assessment + 全服务启动 (BuildingBlocks/Program.cs)
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

①Assessment 迁移 20260924120000_RemoveAiSettings 的跨 schema 裸 SQL 加存在性守卫（源表/目标 schema 不存在时安全跳过或建表），全新环境下迁移链完整应用、practice_categories 等表就位、/practice 可用。②全 7 服务启动时迁移/种子失败不再 catch 后带病启动：必须使进程启动失败，或至少 /health 明确非健康且日志为 Error 级——口径七服务一致，落在 ServiceDefaults 共享路径而非逐个 Program.cs 复制。证据：AUDIT.md REQ-01、COD-14。

## Acceptance criteria

- [ ] 空库（新 schema）冷启动 Assessment+AiGateway：迁移全部应用、GET /api/assessment/categories 与 materials 不再 500（复现 AUDIT 的 FUN-01 场景转通过）
- [ ] 人为制造迁移失败（如指向只读库）时服务不报健康：进程退出或 /health/live 非 200，七服务口径一致
- [ ] 既有库升级路径不受影响；新迁移带 Designer.cs+ModelSnapshot；dotnet build/test 全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
