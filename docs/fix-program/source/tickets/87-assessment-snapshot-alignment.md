# 票 87 — Assessment 模型快照对齐（清 D1/D2 漂移）（源：票 67 盘点）

## 事实（evidence-67/drift-inventory.md，基线 1502777 实测）
当前 tip 上 `dotnet ef migrations has-pending-model-changes` 报漂移 2 条：
- **D1（高危处置面）**：快照仍含 UserResume 实体，模型已删（票 17 决策
  「删代码、留表留数据」）。任何自动 scaffold 的迁移都会捎带
  `DropTable(assessment.resumes)`——apply 即销毁票 17 明确保留的表与数据。
- **D2（低）**：PracticeCategory.LinkedSource 快照/存量库为 varchar(20)，
  模型无长度配置（约定 text）。取值域封闭（null 或 "techstack"，领域层
  硬校验），永不触顶。

## 任务（口径已由票 67 盘点推荐、本票执行）
1. **D2 走 (a)**：AssessmentDbContext 给 LinkedSource 补
   `HasMaxLength(20)` —— 模型=快照=存量库，零 DB 变更。
2. **D1 走手工对齐迁移**：scaffold 后**手工剔除全部操作**（Up/Down 留空），
   仅保留 Designer + ModelSnapshot 更新（快照剔除 UserResume 实体）；
   三件套齐全。迁移命名按仓内惯例。
3. 落库验证：空库全链迁移跑通；存量库（沙盒库）apply 后
   assessment.resumes 表与行数不变（前后实查留证）。

## 硬门槛
- 迁移 Up() 必须**零 DROP、零 AlterColumn**——协调者复核迁移文件后
  才许 apply 验证（先把迁移文件与 diff 落盘 evidence-87/ 报告协调者，
  得复核回执再跑 apply）。
- 在本票落地前，其余 Assessment 迁移票一律不得 apply 自动 scaffold
  的迁移（见 FIX-STATUS 传播警告）。

## 验收
- has-pending-model-changes 在落地后 tip 上 exit=0（归零留证）。
- Assessment.Tests 零回退；运行时行为不变（对齐属元数据层）。
- §7 不适用新逻辑（纯配置+空迁移）；§8 不变项：resumes 表/数据、
  LinkedSource 运行时取值域、全部业务代码不动。
