# 12: FUN-05 Tech Stack 新建必填口径对齐

**审计编号：** FUN-05（严重度 S2）
**区域：** Tech Stack 前端 (+Knowledge 校验提示)
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

新建条目弹窗把 Interview question 标为必填并给字段级错误提示，与后端 Question required 口径一致；保存失败时弹窗内指出具体字段，不再只回 400 静默。证据：AUDIT.md FUN-05。

## Acceptance criteria

- [ ] 按 AUDIT 复现：不填 question 时前端即拦截并提示（或后端 400 被明确映射为字段提示），补填后 201 成功
- [ ] tsc 通过；已存条目编辑路径回归正常

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
