# 13: FUN-06 AI Mock 模式文案三语修复

**审计编号：** FUN-06（严重度 S2）
**区域：** 前端 i18n + Mock 组件
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

补齐 mock 模式 8 个缺失 i18n 键（zh/en/fr 真实文案），会话页模式标签显示本地化名而非枚举原文 TopicDrill。证据：AUDIT.md FUN-06（mock.component.ts:76-79 引用键在 DICT 零定义）。

## Acceptance criteria

- [ ] EN/FR/ZH 三语下模式下拉与会话页标签均为真实文案、无原始键/枚举直出（截图或 DOM 断言）
- [ ] tsc 通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
