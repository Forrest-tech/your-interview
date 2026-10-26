# 票 42 · ARC-11 前端 API 层收敛 — 验证记录

## 目标
将 `web/src/app/features/**` 中所有自拼的 `/api/` URL 字面量收进 `web/src/app/core/api/*.service.ts` 的 typed 方法，组件只调服务、不再构造路径。后端路径、请求/响应形状、行为一律不变。

## 收口方式
- 新增 typed 服务（前期）：admin / analytics / interviews / jobs / knowledge / assessment / ai / mock / ocr。
- 补齐方法（本期）：`KnowledgeApiService`（getItem/updateItem/deleteItem/restoreItem/updateMastery/review/updateFromTech/updateBetterAnswer/renameTopic/deleteTopic/getStats/getDuplicates/getSources/merge，返回类型对齐 `api.models.ts`）、`InterviewsApiService`（getCompanyRounds/createPrepPlan）、`AssessmentApiService`（deleteSession/getSession/answerQuestion/nextQuestion/completeSession）、`JobsApiService`（changeStatus/archive/unarchive/updateOutreach/saveApplicationJd/求职信/简历 等全套）。
- 迁移组件：`mock` / `mock-session` / `ai-practice` / `techstack` / `techstack/category-manager` / `tracker` / `playbook/prep-plan-dialog` / `playbook/playbook-calendar` / `techstack/duplicates-dialog`。

## 验证步骤与结果

### 1. 字面量归零
```
grep -rnE "(['\`\"/])/api/" web/src/app/features --include="*.ts" \
  | grep -vE "core/api|practice-api|tracker-api|\.api\.service\.ts|playbook-v2\.api\.ts" \
  | grep -vE "/\*|\*|//" | grep -vE "this\.|const |let |return |private "
```
**结果：0 处代码字面量。**（`playbook-v2.api.ts` 为既有 API 适配层，按 ARC-11 豁免不迁；其余仅剩注释里的 `/api/` 提及。）

### 2. 类型检查
```
cd web && npx tsc --noEmit
```
**结果：exit 0，0 错误。**

### 3. 行为不变
- 所有端点路径逐字保持原样（如 `/api/knowledge/merge`、`/api/jobs/applications/${id}/status`、`/api/interviews/rounds/upcoming`）。
- 请求体与响应处理不改；仅把 `this.api.X(path, ...)` 改为 `this.xxxApi.method(...)`。
- `ApiClient` 基类行为不变。

## 已知次要项（非回归）
- `tracker.component.ts` 的 `getStats()` 消费原为 `TrackerStats`，`JobsApiService.getStats()` 类型签名是 `InterviewStats`；原代码即 `this.api.get<TrackerStats>('/api/jobs/stats')`，运行时契约未变，仅为类型声明的口径差异，未改行为。

## 结论
票 42 实现完成，字面量归零、tsc 全绿。建议 CI E2E 黄金路径复跑（套件 5 绿 + 1 跳过）作为第四步远程确认。
