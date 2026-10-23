# 04: REQ-02+ARC-07 结果回写同源（轮次主数据与结构化 outcome）

**审计编号：** REQ-02, ARC-07（严重度 S2）
**区域：** Interviews↔Jobs 事件 + SharedContracts
**Blocked by:** 03
**Status:** ready-for-agent

## What to build

Interviews 轮次出结果后，Jobs 侧投递状态与轮次投影可靠同步，不再同屏矛盾：InterviewOutcomeRecorded 事件载荷加结构化 outcome 枚举（入 SharedContracts，保留自由文本原文作展示），Jobs 消费者以枚举为准更新投递状态/轮次投影；关键词 MapOutcome 只保留在 Jobs 侧单一 adapter 内作旧事件回落。Dashboard upcoming 与 Tracker interviewed 口径对齐同一事实。证据：AUDIT.md REQ-02、ARC-07。

## Acceptance criteria

- [ ] 复现 AUDIT 场景：Playbook 给轮次填结果 → Tracker 该投递状态与计数随之变化、Dashboard 与 Tracker 不再矛盾（修复前复现矛盾、修复后一致）
- [ ] 枚举契约入 SharedContracts，两侧编译期共用；旧事件（无枚举）仍可被消费（回落路径有测试）
- [ ] dotnet test 全绿（含新增消费者测试）；前端 tsc 通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
