# 票 41 · ARC-16 Playbook 旧代实现下线 — 验证记录

## 删除清单（逐项确认不可达）

| # | 文件/目录 | 可达性结论 | 删除前 selector |
|---|---|---|---|
| 1 | `components/analysis/*` | 全仓零引用；无路由、无模板使用 | `app-analysis-report`, `app-next-actions`, `app-overview-widgets` |
| 2 | `components/qa/*` | 全仓零引用；无路由、无模板使用 | `app-question-bank`, `app-coverage-mini`, `app-qa-confirm-dialog` |
| 3 | `components/brief/brief.component.*` | 全仓零引用；旧版面已取消 | `app-brief` |
| 4 | `components/brief/playbook-header.component.*` | 全仓零引用；redesign 详情头自包含 | `app-playbook-header` |
| 5 | `components/brief/rounds-timeline.component.*` | 全仓零引用；曾为全字段写入方，现已不可达 | `app-rounds-timeline` |

## 保留文件（ redesign 仍有引用）

| 文件 | 引用方 |
|---|---|
| `components/brief/brief.helpers.ts` | `redesign/intake/intake-panel.component.ts`, `redesign/v2.helpers.ts`, `prep-plan-dialog.component.ts` |
| `components/brief/brief.i18n.ts` | `playbook.component.ts`, `brief.helpers.ts` |
| `components/brief/brief.models.ts` | `playbook.component.ts`, `brief.helpers.ts` |

## 行数对比

- 删除前：`14817` 行
- 删除后：`9716` 行
- **减少：`5101` 行（34.4%）**

## 验证结果

| 检查项 | 命令 | 结果 | 备注 |
|---|---|---|---|
| TypeScript 类型检查 | `npx tsc --noEmit` | ✅ 通过 | 删除无编译影响 |
| Angular 生产构建 | `npx ng build` | ⚠️ 被系统 OOM kill（exit 137） | 沙盒内存不足，非代码问题；CI 构建做最终验证 |
| Angular development 构建 | `NODE_OPTIONS=--max-old-space-size=4096 npx ng build --configuration development` | ⚠️ 被系统 OOM kill（exit 137） | 同上 |
| Playbook 全流程冒烟 | E2E 黄金路径 | ⏳ 推远端后由 CI 自动跑 | 见 GitHub Actions E2E job |

## 证据文件

- `baseline-lines.txt`：删除前总行数
- `after-lines.txt`：删除后总行数
- `deletion-list.txt`：待删文件完整清单
- `PLAN.md`：影响面与不变项清单

## 后续

- push 后以 `git ls-remote` 确认 sha 到远端
- 以 GitHub Actions E2E 结论作为 Playbook 冒烟最终证据
