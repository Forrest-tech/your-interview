# 11: FUN-04 JD 长文本保存不再 500

**审计编号：** FUN-04（严重度 S2）
**区域：** Jobs 后端
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

JdText 列 varchar(20000) 扩为 text（迁移带 Designer+Snapshot），smart-add 对超长 JD 不再 500 整单丢失；如保留任何长度上限，超限必须在保存前给前端校验提示而非 500。证据：AUDIT.md FUN-04。

## Acceptance criteria

- [ ] 44,181 字符 JD 复现：smart-add 保存成功、投递创建、jdText 完整落库（修复前 500 Postgres 22001）
- [ ] 迁移三件套齐全；既有数据无损；dotnet test 全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
