# 01: COD-04 轮次保存部分字段更新不再清数据

**审计编号：** COD-04（严重度 S1）
**区域：** Interviews 后端
**Blocked by:** None (can start immediately)
**Status:** ready-for-agent

## What to build

轮次扩展字段（Transcript/PrepQuestionsJson/EmailsJson/RecordingUrl/MeetingLink/ScheduledTime/ChecklistJson）改为「部分更新」语义：调用方未提供的字段保持原值，只有明确提供才覆盖。协议：JSON 字段为 null/缺省 = 保持；空字符串 = 清空（存 null）；非空 = 赋值。前端 round-card 保存与 comm-log 同步只传基础字段时，后端不得再抹除扩展字段。证据：AUDIT.md COD-04（InterviewAggregates.cs:1067-1078、InterviewHandlers.cs:1505-1509）。

## Acceptance criteria

- [ ] 先在 Services.Interviews.Tests 写复现测试：建带全部扩展字段的轮次 → 以仅基础字段的 UpdateRoundDetails 命令保存 → 断言扩展字段原样保留（修复前失败、修复后通过）
- [ ] 清空语义测试：显式传空字符串可清空对应字段
- [ ] 全字段写入方（rounds-timeline 路径）行为不变；dotnet test 全绿；前端 tsc 通过（如动前端）

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
