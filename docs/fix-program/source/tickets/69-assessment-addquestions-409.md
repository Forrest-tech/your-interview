# 票 69 — Assessment AddQuestions 对全新会话恒回 409 并发冲突（缺陷票，源：票 66 复现现场）

**Status:** closed — 提交 ab629ba（协调者验收通过：diff 读码 + 独立复跑 Assessment.Tests 55/55，新 16 例 DB 类耗时 0.028–8.65s 真实执行；活体全链 7/7）。
**根源存档**：MockQuestion/DimensionIssue 的 Guid 主键构造时预赋值，EF 在已跟踪父聚合上新增子实体时误判 Modified → UPDATE 命中 0 行 → DbUpdateConcurrencyException → 409；既有 MarkNewChildrenAsAdded 只翻 Detached 且跑在 DetectChanges 之前，误判漏网。修复：钩子先 DetectChanges + 对这两类 Modified 条目查库确认不存在才翻 Added（查库失败按已存在处理，真并发仍 409，票 07 口径钉有专测）。

## 复现（协调者于票 66 基线实例 :5246 亲测，两次独立触发）
1. 管理员登录 → POST /api/assessment/sessions 建全新会话（FullLoop）→ 201。
2. 紧接着 POST /api/assessment/sessions/{id}/questions 加 2 题 →
   **409 `Concurrency conflict`「This record was modified by another user…」**。
3. 删会话后同样调用仍 409（该项在票 66 修复后口径另计，本票只管全新会话首加题）。
票 66 第一任实现者的笔记同样记录 AddQuestions 失败（当时记为 400，实测为 409）。

## 影响
经 API 给会话加题是 Mock 主流程一步；前端若走此端点，用户将无法开始练习
（需先核查前端实际调用路径与受影响面——若前端另有路径，注明）。

## 任务
1. 定位根源：疑 xmin 并发令牌在「建会话→紧接着加题」链路上的加载/保存口径
   （如 Handler 两次加载、创建响应后 xmin 已变、或 Include 题目集合时的令牌校验）。
   按 diagnosing-bugs 流程先立可复现用例再修。
2. 修复后：建会话→加题→答题→评分全链经 API 活体通过；并发真冲突场景仍应 409
   （票 07 COD-05 口径不回退：两个并发写同一会话，后者得 409 而非 500/静默覆盖）。

## 验收
- 复现脚本修复前红、修复后绿（存证据）。
- §7 单测：AddQuestions Handler 级用例（全新会话首加题成功；真并发冲突仍 409），
  含 boundary（空题目列表、超 TargetQuestionCount）。
- 不变项：DbUpdateConcurrencyException→409 的全局映射（票 07）不动；
  PracticeMaterial 等其他实体的 xmin 行为不动。
