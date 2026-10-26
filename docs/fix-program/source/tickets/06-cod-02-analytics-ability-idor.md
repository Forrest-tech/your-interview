# 06: COD-02 Analytics ability 禁止替他人写入

**审计编号：** COD-02（严重度 S2）
**区域：** Analytics 后端
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

POST /api/analytics/ability 忽略请求体 UserId，一律以当前登录用户为准（AnalyticsController.cs:57）。证据：AUDIT.md COD-02。

## Acceptance criteria

- [ ] 以账号 A 身份带账号 B 的 UserId 调接口 → 数据落 A 名下、B 名下无变化（API 实测）
- [ ] 正常自写路径不变；dotnet build/test 全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
