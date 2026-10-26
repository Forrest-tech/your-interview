# 07: COD-05 并发冲突映射 409

**审计编号：** COD-05（严重度 S2）
**区域：** BuildingBlocks 全局异常管线
**Blocked by:** 02 (ServiceDefaults 串行)
**Status:** ready-for-agent

## What to build

DbUpdateConcurrencyException 在共享管线统一映射为 409 ProblemDetails（ServiceDefaults/PipelineBehaviors），前端收到 409 时给出「已被他人修改，请刷新」类可理解提示（至少后端契约落地，前端全局错误处理如有统一入口则一并接）。证据：AUDIT.md COD-05。

## Acceptance criteria

- [ ] 并发复现：同一条目两路并发保存，后保存者收到 409 而非 500（API 实测）
- [ ] 其它异常映射（Validation/401/404）不变；dotnet test 全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
