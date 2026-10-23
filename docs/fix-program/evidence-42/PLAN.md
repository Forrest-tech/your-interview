# 票 42 · ARC-11 前端 API 层收敛 — 执行计划

## 现状盘点

- `features/**` 中 `/api/` 字面量剩余：65 处（AUDIT 原记 181 处，已迁移部分至 tracker-api / practice-api）
- 已存在 typed API 层：`tracker-api.service.ts`、`practice-api.service.ts`
- 待补 typed API 模块：admin / analytics / interviews / jobs / knowledge / assessment / ai / mock

## 影响面清单

| 新增/改动 | 影响 |
|---|---|
| `core/api/admin-api.service.ts` | `features/admin/admin.component.ts` 不再自拼 `/api/admin/*` |
| `core/api/analytics-api.service.ts` | `features/analytics/analytics.component.ts`、`features/dashboard/dashboard.component.ts` 不再自拼 `/api/analytics/*` |
| `core/api/interviews-api.service.ts` | `features/playbook/*`、`features/dashboard/*`、`features/tracker/*` 不再自拼 `/api/interviews/*` |
| `core/api/jobs-api.service.ts` | `features/dashboard/*`、`features/playbook/*`、`features/resume/*`、`features/tracker/*` 不再自拼 `/api/jobs/*` |
| `core/api/knowledge-api.service.ts` | `features/ai-practice/*`、`features/playbook/*`、`features/techstack/*` 不再自拼 `/api/knowledge/*` |
| `core/api/assessment-api.service.ts` | `features/mock/*`、`features/ai-practice/*` 不再自拼 `/api/assessment/*` |
| `core/api/ai-api.service.ts` | `features/ai-practice/*` 不再自拼 `/api/ai/complete` |
| `core/api/mock-api.service.ts` | `features/mock/*` 不再自拼 `/api/assessment/sessions/*` |

## 不变项

- 所有后端路径、请求/响应 DTO 形状不变
- `ApiClient` 基类行为不变
- 不新建或删除业务逻辑，只做路径封装迁移
- 现有 `tracker-api` / `practice-api` 保持原样，仅在其缺口的端点补充方法

## 验证

1. `grep -rn "['/\"]/api/" web/src/app/features --include="*.ts" | grep -v "core/api\|practice-api\|tracker-api"` 计数归零
2. `npx tsc --noEmit` 通过
3. CI E2E 黄金路径全绿
