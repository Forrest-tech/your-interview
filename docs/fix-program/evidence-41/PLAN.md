# 票 41 · ARC-16 Playbook 旧代实现下线 — 执行计划

## 影响面清单（变更安全协议 §8）

| 文件/目录 | 处置 | 理由 |
|---|---|---|
| `web/src/app/features/playbook/components/analysis/*` | **整目录删除** | grep 全仓零引用；overview-widgets/next-actions/analysis-report 仅在自身文件出现 |
| `web/src/app/features/playbook/components/qa/*` | **整目录删除** | grep 全仓零引用；question-bank/coverage-mini/qa-confirm-dialog 仅在自身文件出现 |
| `web/src/app/features/playbook/components/brief/brief.component.*` | **删除** | 全仓无引用；旧版面已按定稿取消 |
| `web/src/app/features/playbook/components/brief/playbook-header.component.*` | **删除** | 全仓无引用； redesign 详情头自包含 |
| `web/src/app/features/playbook/components/brief/rounds-timeline.component.*` | **删除** | 全仓无引用；selector `app-rounds-timeline` 无模板使用；且为整字段写入方，现已不可达 |
| `web/src/app/features/playbook/components/brief/brief.helpers.ts` | **保留** | redesign/intake-panel、v2.helpers、prep-plan-dialog 仍在引用 |
| `web/src/app/features/playbook/components/brief/brief.i18n.ts` | **保留** | playbook.component 仍在引用 |
| `web/src/app/features/playbook/components/brief/brief.models.ts` | **保留** | playbook.component 仍在引用 |
| `web/src/app/features/playbook/playbook-detail.component.ts` | **不动** | 只编排 redesign 一代，已符合票 41 目标 |
| `web/src/app/features/playbook/redesign/*` | **不动** | 现行一代保留 |
| Playbook 路由 (`app.routes.ts`) | **不动** | 仅指向 `PlaybookDetailComponent`，无需调整 |

## 不变项清单

- redesign 一代组件与 API 调用行为不变
- `brief.helpers.ts` / `brief.i18n.ts` / `brief.models.ts` 继续为 redesign 与列表页服务
- 路由、URL、数据模型不变
- Playbook 日历独立页不受影响

## 验证剧本

1. `npx tsc --noEmit` 在 `web/` 目录通过
2. `npx ng build` 通过（生产构建）
3. Playbook E2E 黄金路径回归：列表 → 详情 → 轮次/材料/沟通记录 无回归
4. 删除前后 `find web/src/app/features/playbook -type f | xargs wc -l` 行数对比

## 风险点

- `rounds-timeline` 曾是全字段写入方；现已无路由/模板引用，且 redesign 的 `round-card` / `comm-log` 使用部分更新协议（票 01），删除不影响活路径
- 旧组件若被动态字符串加载则可能漏删；已用 selector/app- 全模式 grep 复核
