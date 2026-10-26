# 14: COD-10a Tracker 收藏与自备问题上后端（Jobs）

**审计编号：** COD-10 (Jobs 面)（严重度 S2）
**区域：** Jobs 后端 + Tracker 前端
**Blocked by:** 03 (Jobs 区域串行)
**Status:** ready-for-agent

## What to build

Tracker 收藏（yi-tracker-favorites，现列表与详情双实现同 key 可分叉）与按投递自备问题（yi-tracker-questions-{id}，现与 Playbook/Tech Stack 不通）落 Jobs 后端：加表+迁移+API，前端改读后端、双实现合一；首次加载时把本地已有数据一次性导入后端，不丢用户既有收藏/问题。证据：AUDIT.md COD-10。

## Acceptance criteria

- [ ] 换浏览器/清 localStorage 后收藏与自备问题仍在（后端可查）
- [ ] 本地旧数据首次导入后条数对账一致；双实现收敛为单一读写路径
- [ ] 迁移三件套齐全；dotnet test 全绿；tsc 通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
