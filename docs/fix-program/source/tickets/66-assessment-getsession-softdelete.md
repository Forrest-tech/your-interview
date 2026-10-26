# 66: Assessment 已删会话 GET 仍可读回（跟进票，源自票 63 发现 1）

**来源：** 票 63 票面外发现 1（活体实证）
**区域：** Assessment 后端读路径
**Blocked by:** 63（已落地 3144454）
**Status:** closed — 提交 02510fb（协调者亲自接手实施：两任实现者均死于 daemon 重启）

## 问题

DELETE 会话是软删（`IsDeleted=true`），但 `GetSessionQueryHandler` 未过滤 `IsDeleted`——删除后 `GET /api/assessment/sessions/{id}` 仍 200 返回已删会话。删除在用户视角等于没删。

## What to build

- 盘点 Assessment 全部读路径（GetSession、ListSessions、stats、题目/轮次相关查询等），逐一核对 IsDeleted 过滤口径并在报告列表：哪些已过滤、哪些没有。
- 未过滤的统一补齐：已删会话按不存在处理（GET 404；列表/统计不计入）。写路径对已删会话的操作（继续答题/完成/再删）一并核查口径并在报告说明（发现不一致先报协调者，不许自创口径）。
- 若 Assessment 有全局查询过滤器机制则优先用它（与 Interviews/Jobs 的软删过滤口径对齐）；没有则在各 Handler 显式过滤并注明。

## Acceptance criteria

- [x] 活体/API：建会话 → DELETE → GET by id 404、列表不含、stats 不计（修复前复现 GET 200）
- [x] 未删会话全部读路径回归不变；Services.Assessment.Tests 全绿（现基线 31 例）且为软删过滤新增用例（含 boundary：删后写操作、重复删）
- [x] §8 两清单（影响面/不变项）随报告交付；§7 新改规则逻辑带单测

## 关闭记录（协调者验收，2026-10-22）

- **处置**：AssessmentDbContext 对 MockSession 补 `HasQueryFilter(x => !x.IsDeleted)`
  （与同文件 PracticeMaterial 同口径）。全局过滤器一处覆盖全部读写路径，
  无需逐 Handler 改。查询过滤器无 schema 变更 → **不带迁移**；动手前在未改
  tip 实测 `has-pending-model-changes` 已报漂移（先存问题）→ 另立票 67 盘点。
- **证据**（evidence-66/）：repro-before.txt（基线实例 :5246，10/10 复现：
  删后 GET 200/列表含/统计计/complete 200/abandon 204/重复删 204）；
  repro-after.txt（修复实例 :5247，13/13：GET 404/列表不含/统计=基线+1/
  删后四种写全 404/对照会话不受影响）；asm-tests-after.xml（39/39，
  新 8 例逐例耗时 0.012–7.5s 真实 DB 执行，非假绿）。
- **§7**：SoftDeleteFilterTests 8 例——已删 GET/列表/统计（含题目计数经
  Sessions 关联排除）/重复删/删后答题五核心 + 未删生命周期不变项 +
  boundary（不存在 id、空用户零计数、跨用户隔离）。
- **§8 影响面**：Assessment 全部会话读写（List/Get/Stats/AddQuestions/
  Answer/Score/Skip/Complete/Abandon/Delete 均先经会话查询，过滤器统一生效）；
  Suggest 端点回归 200 已验。**不变项**（逐条打勾）：☑ 软删行保留在库
  （IgnoreQueryFilters 计数=1 实测，不丢数据）；☑ 未删会话读写行为不变；
  ☑ 票 07 并发 409 映射未动；☑ 其他服务未动；☑ PracticeMaterial 行为未变
  （仅口径对齐注释）。
- **票面外发现**：AddQuestions 对全新会话首加题恒 409 并发冲突（活体两次
  独立触发，与本票无关）→ 立票 69。

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据存 evidence-66/
- 定版测试规程（console runner + 全套 extensions dll + XML 逐例耗时）；在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
