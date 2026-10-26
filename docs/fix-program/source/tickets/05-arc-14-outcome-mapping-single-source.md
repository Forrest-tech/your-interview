# 05: ARC-14 结果判读单一事实源

**审计编号：** ARC-14（严重度 S3）
**区域：** Interviews DTO + Playbook 前端
**Blocked by:** 04
**Status:** ready-for-agent

## What to build

前端 v2.helpers.ts 的 entryResultOutcome 与后端判读口径合一：条目列表/详情 DTO 由后端给出结构化 outcome（与 04 票的枚举同一事实源），前端分组（In Progress/Completed）只消费该字段，不再本地关键词判读。证据：AUDIT.md ARC-14。

## Acceptance criteria

- [ ] 同一结果原文在列表分组与 Tracker 回写的结论一致（对照用例：Rejected/Passed/Pending/等回复/无结果）
- [ ] 前端删除本地判读实现（或仅保留对枚举的展示映射）；tsc 通过；列表分组回归（Completed 计数/徽章）与现行为一致

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
