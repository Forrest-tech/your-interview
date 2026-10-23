# 61: Tech Stack 详情编辑保存清空 Question（跟进票，源自票 12 发现，数据丢失类）

**来源：** 票 12（FUN-05）票面外发现，活体旁证成立
**区域：** Tech Stack 前端 `web/src/app/features/techstack/techstack.component.ts`（`saveDetailEdit`）+ Knowledge 后端 Update 校验口径核查
**Blocked by:** 12（已落地 516d4ab）
**Status:** closed（协调者验收+裁决，待独立真人化复验留痕）

> 协调者裁决（2026-10-21）：验收项 2 的「knowledge-dialog 编辑路径」为拟票预设错误——该弹窗纯新建、无编辑模式（全仓核查在案，见 FIX-STATUS 票61 裁决条）；以调用方核查结论替代该项回归，创建路径回归由票 12 证据覆盖。实现偏差清单归空。

## 问题（基线既存，非票 12 引入）

详情页 `DETAIL_BLOCKS` 自 2026-10-18 起不含 question 块（用户当时拍板：详情不展示 question），但 `saveDetailEdit` 恒发 `question: d.blocks['question'] ?? ''` → 恒为空串；后端 `UpdateKnowledgeCommandValidator` 不校验 Question、`UpdateDetails` 直接赋值。结果：**任何一次详情编辑保存都会把条目已存的 Question 清空**（票 12 活体旁证：条目经详情编辑保存后 GET `question=''`）。与 FUN-05「Question 必填」口径直接冲突，属数据丢失。

## What to build

- 前端主修：`saveDetailEdit` 的 question 改为回填条目现存值（`item.question`），与票 09 全量 PUT 回填纪律一致——详情 UI 不展示 question，就不许在保存时改动它。
- 后端护栏（核查后决定）：核对 UpdateKnowledge 的两个调用方（详情保存、knowledge-dialog 编辑）对 Question 的真实需求；若无任何合法「清空 question」流，则把后端 Update 口径与 Create 对齐（Question 必填，空串/null 均 400），从根上堵住同类误清；若存在合法清空流，在报告里写明并只做前端修复。

## Acceptance criteria

- [ ] 活体/API 复现：建带 question 条目 → 走详情编辑保存改其它字段 → GET question 保持原值（修复前为空串）
- [ ] knowledge-dialog 编辑路径回归：改 question 正常保存；若加了后端护栏，空 question 保存被 400 字段提示（与票 12 的 applyServerError 衔接）
- [ ] `npx tsc --noEmit` 通过；Knowledge 相关测试（如有）全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据存 evidence-61/
- 不许为过测改断言口径；在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
