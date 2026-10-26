# 票 80 — Playbook deleteAction 同款吞错（票 21 盘点发现）

严重度：S3（前端静默失败，与 COD-08 同病灶）
来源：票 21 COD-08 实施盘点（evidence-21/00-inventory.md + 07 报告）。

## 问题
`playbook-detail.component.ts` 的 `deleteAction` 仍是 `.catch(() => undefined)` 吞错：删待办失败时静默无提示，随后 `load()` 重拉——用户以为删成功（或无感），失败无任何反馈。与票 21 已修的 toggleAction 同病灶、同文件。

## 修复口径（与票 21 一致）
- 失败走 `snack.open(...)` 提示（新增 i18n 键或复用同族键，additive 三语）。
- 不本地乐观删除；成败都以服务端真值 `load()` 重渲染。
- §7：无新纯逻辑则声明口径；§8 影响面仅 deleteAction 路径，不变项 = 其他编辑动作/后端契约不动。
- 验收：API 层失败路径实证（删不存在 actionId → 非 2xx → 前端 catch 成立）+ UI 活体尽力（环境允许时）。
