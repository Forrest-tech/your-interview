# 10: FUN-03 Prep 打开既有条目不重复新建

**审计编号：** FUN-03（严重度 S2）
**区域：** Interviews 后端 + Tracker 前端
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

CreateEntry 对 JobApplicationId 幂等：已存在同 JobApplicationId 的条目时返回既有条目 id 不新建；Tracker 两条 Prep 路径（PrepPlanDialog 与一键创建）都带上 jobApplicationId。证据：AUDIT.md FUN-03；第 0 步复核 CreateEntryCommandHandler 无去重。

## Acceptance criteria

- [ ] 按 AUDIT 复现：对已有关联条目的投递连点两次 Prep → 始终跳既有条目、条目总数不增（修复前复现新建第二条）
- [ ] 无关联投递的 Prep 仍正常新建；dotnet test 全绿；tsc 通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
