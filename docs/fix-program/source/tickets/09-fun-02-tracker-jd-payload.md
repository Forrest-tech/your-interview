# 09: FUN-02 Tracker 建单/编辑 JD 不再丢（附带 workMode 等全量 PUT 误清）

**审计编号：** FUN-02（严重度 S2）
**区域：** Tracker 前端
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

create() 与 updateAppPayload() 的负载补齐 jdText；updateAppPayload 不得把 workMode/jdSummary/source/deadline 硬编码 null 误清（表单有的带真实值、表单没有的字段从现有投递值回填）。手动建单填的 JD 与备注保存后抽屉可见、API jdText/notes 有值。证据：AUDIT.md FUN-02；第 0 步复核定位 tracker.component.ts create() 与 updateAppPayload()。

## Acceptance criteria

- [ ] 按 AUDIT 复现步骤：手动建单填 JD+备注 → 抽屉 Job Description 渲染、GET 投递 jdText/notes 非空（修复前复现为空）
- [ ] 编辑一条带 workMode/jdSummary 的投递后，这些字段不被清掉
- [ ] tsc 通过；Smart paste 建单路径回归正常

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
