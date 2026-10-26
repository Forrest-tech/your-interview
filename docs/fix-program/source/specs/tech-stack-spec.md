# Tech Stack（技术栈题库）现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/tech-stack`：个人技术题库——条目 CRUD、分类管理、复习（SM-2 间隔重复）、掌握度、来源追溯（asked sources）、重复检测合并、Due 复习与复习热力图。
- 前端 `web/src/app/features/techstack/`（主组件 1386 行）；后端 Knowledge 服务，路由前缀 `api/knowledge`（`src/Services.Knowledge/Api/KnowledgeController.cs:18`，类级 `[Authorize]` + 逐端点权限策略）。

## 核心实体与数据真源
- **KnowledgeItem**（`src/Services.Knowledge/Domain/KnowledgeAggregate.cs`，469 行文件内聚合根）：字段含 UserId（票 03 起按用户隔离 + 全局过滤器）、Title、Topic（分类字符串）、SubTopic、Question、ConceptExplanation、MyAnswer、BetterAnswer、KeyPoints/CommonMistakes/FollowUps/References（`*Json` 字符串列）、Difficulty、Importance、TagsJson。
- **来源（asked sources）口径**：单来源兼容字段 Source（枚举 KnowledgeSource: Personal/FromInterview/AiGenerated/Imported，同文件 :428 附近）+ SourceInterviewEntryId/SourceCompanyName/SourceDate/SourceRoundNo/SourceRoundStage/SourceJobApplicationId；**多来源真源 = SourcesJson**（同文件 :97），一条目可挂多个「公司 · Round · 日期」来源——与用户 2026-10-17 定规（多 asked sources、条目级）一致。反查端点 `GET /api/knowledge/sources`、`GET /api/knowledge/{id}/trail`。
- **分类口径**：分类不是独立实体表挂在条目上，而是条目的 Topic 字符串 + 前端聚合；「空分类」（无条目也要存在的分类名）由票 16 新增后端表 `TechStackCategory`（`src/Services.Knowledge/Domain/TechStackLocalData.cs`，端点 `custom-categories` GET/PUT/DELETE/import，`KnowledgeController.cs:265-306`）。分类选择为组合框语义：可选已有 Topic 或输入新名（前端 `techstack.component.ts` + category-manager）。
- **掌握度**：MasteryLevel 五档 New=0/Learning=1/Familiar=2/Mastered=3/NeedsReview=4（`KnowledgeAggregate.cs:417`）。
- **复习调度**：SM-2 简化算法在聚合内（同文件 :232-318）：ReviewResult 四档 Again/Hard/Good/Easy；EasinessFactor 初值 2.5、下限 1.3、上限 3.0；NextReviewAt = 上次复习时间 + 间隔（非 now + 间隔，注释明示）。复习日志 KnowledgeReviewLog、条目关系 KnowledgeRelation（Prerequisite/Compare/Extends）。
- **复习热力图**：后端表 `ReviewActivityDay`（UserId/Date/Count，`TechStackLocalData.cs`），端点 `review-activity` GET/record/import（票 16 上后端、票 81 补 Log review 接线）。
- 前端 localStorage 只剩 UI 偏好：`ts.filtersCollapsed`、`ts.listWidth`、`ts.resoleMode`（`techstack.component.ts` grep 实查）——领域数据已无 localStorage 真源（票 16 修复 COD-10c 后的现状）。

## 主要流程
1. 列表/筛选/搜索：`GET /api/knowledge`（分页+筛选）、`/topics`、`/stats`、`/sources`。
2. 新建/编辑：POST/PUT `/api/knowledge`；局部更新有专用端点 `/answer`、`/better-answer`、`/question`、`/from-tech`（`KnowledgeController.cs:155-194`）。Question 为后端必填（Create/Update 校验器同口径，票 12/61 对齐 UI 必填标记后的现状）。
3. 复习：POST `/{id}/review`（记 ReviewResult → SM-2 算下次）与 `/{id}/mastery`（手动改档）；复习动作同时 record 复习活动（热力图）。
4. 与 Playbook 双向同步：Playbook 问答入库经 `POST /api/knowledge/import-candidates` 导入（带来源标签）；条目答案在 Playbook 备考报告内编辑走 `/from-tech` 等端点回写同一 KnowledgeItem——同一实体，无副本。
5. 重复治理：`GET /duplicates` + `POST /merge`；软删 + `POST /{id}/restore`（Trash 恢复）。

## 与既定规则对照结论
- 多 asked sources、分类组合框自动聚合：**符合**用户 2026-10-17 定规（SourcesJson 多来源 + Topic 聚合 + 空分类后端表）。
- 与 Playbook 同源：**符合**——Playbook 引用/回写的就是 KnowledgeItem 本体（ARC-07 的轮次双主问题不涉及本模块）。
- localStorage 孤岛（COD-10c）：**已修复**（票 16），仅剩 UI 偏好键。

## 现状与规则冲突
- **CONF-TS-1（→ AUDIT REQ-14，票 45 未实施）**：删除仍用原生 `confirm()` 两处（`techstack.component.ts:662`、`:1009` 实查），与 F-02 全站 ConfirmDialog 标准不一致。
- **CONF-TS-2（→ AUDIT FUN-07，票 35 未实施）**：详情「口语练习」按钮与复习热力图「少/多」等硬编码中文残留（AUDIT 证据 `techstack.component.html:105`），EN/FR 直出。
- **CONF-TS-3（观察，无 AUDIT 编号，不新造正单）**：分类存在双轨表示——条目 Topic 字符串与 `TechStackCategory` 空分类表靠前端 `techstack-local.service.ts` 合并展示；改名/删分类需同时处理条目 Topic 重命名端点（`topics/rename`）与空分类表，规格上以「Topic 为分类真源、分类表只存空分类占位」为现状口径，后续改动勿把分类表当全量分类真源。
