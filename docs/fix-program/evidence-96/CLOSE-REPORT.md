# 票 96 CLOSE-REPORT（实现方证据，非台账）

## 结论
根治完成：分类名唯一约束改为大小写不敏感。应用层裁决环（票 92）零改动——
它本来就对 23505 做回查收敛，缺的只是库层能对变体并发撞出 23505 的约束。
迁移只写仓内代码，未向任何真实数据库 apply；共享库 schema 未动。

## 改动（worktree wt-t96，基线 tip 57e93501）
- `src/Services.Knowledge/Infrastructure/Persistence/KnowledgeDbContext.cs`：
  撤掉大小写敏感的 `(UserId, Name)` 唯一索引声明（`HasIndex(UserId)` 保留）；
  注释写明真正约束在迁移 SQL 里（EF fluent API 无法声明表达式索引）。
- `src/Services.Knowledge/Infrastructure/Persistence/Migrations/20261024153355_CaseInsensitiveCategoryNameUniqueIndex{.cs, .Designer.cs}`
  + `KnowledgeDbContextModelSnapshot.cs`（三件套齐）：
  Up = 删旧唯一索引 + `CREATE UNIQUE INDEX "IX_tech_stack_categories_UserId_LowerName"
  ON "knowledge"."tech_stack_categories" ("UserId", lower("Name"))`；
  Down = 删函数索引 + 重建旧唯一索引。
- `src/Services.Knowledge.Tests/TechStackLocalDataRaceTests.cs`：新增
  `Add_ConcurrentCaseVariant_Barrier_ExactlyOneRow`（4 Context 屏障，2×"RaceCase" +
  2×"RACECASE"，全成功且终态恰 1 行）。

## §7 验证（证据链，全部实测）
- 改前红测（重启前共享库）：新用例失败，终态 2 行，与票面复现一致。
- 改后绿测（scratch 库 yourinterview_t96，迁移链已 apply，函数索引实测在位）：
  Race 13/13（含新用例）、LocalData 24/24、Boundary 26/26，合计 63/63，
  0 失败 0 跳过；竞态用例耗时毫秒~秒级，DB 真跑非静默跳过。
- 顺序变体口径不变：`Add_SequentialCaseVariant_ExactlyOneRow` 在 scratch 上通过。
- 票 92 同名并发回归：既有屏障用例（2 Context、4 Context、软删后并发重加）
  在 scratch 上全绿；票面“72 发”属协调者级验收（8 线程×9 轮），仓内无同名
  72 发用例，机制由上述屏障用例覆盖。
- 漂移校验：手补 SQL 后再脚手架 Up 为空，DriftCheck 已移除；快照确认分类
  仅剩 `HasIndex("UserId")`。
- Down 验证（scratch）：回滚到 AddTechStackLocalData 后函数索引消失、旧唯一
  索引恢复；Up 重入已于首轮验证。
- 基线说明：票面“基线 97”已过时，现 tip 基线为 Knowledge.Tests 169/169
  （重启前共享库实测，0 失败 0 跳过）。重启后共享库丢失 knowledge schema
  （及 interviews/assessment/analytics/aigateway），DB 门控用例现全报 42P01——
  属 VM 更换后的环境问题，非本票回归；CI 流程是先 `dotnet ef database update`
  再跑测试，故新用例在 CI 上绿（迁移随链 apply）。

## §8 影响面与不变项
- 已核路径：分类名全部读写/查重/导入——写入点全仓仅两处：Add（245 行）、
  Import（307 行）。Import 批内 `NormalizeCategoryNames` 已是 OrdinalIgnoreCase
  去重（首拼写胜出）+ existingKeys OrdinalIgnoreCase，单批变体不可能双插，
  新函数索引不会冲导入；票 90 的导入并发吞错面只核口径、未改其错误呈现。
- 不变项：票 83 的 100 字符上限（校验器未动）、票 84 即时同步（删/改名路径
  未动）、票 92 裁决环语义（Handler 代码一行未改；变体并发后到者撞 23505 →
  回查 ToLower 命中赢家行 → Satisfied/204，与同名并发同口径）。
- 回查口径：`FindAsync` 的 `Name.ToLower()` 经 Npgsql 译为 SQL `lower()`，
  与函数索引同口径（ASCII 变体一致）。

## 存量变体重复行处置方案（报协调者裁决，本票未清理任何行）
- 现状：共享沙盒库现无 knowledge schema，无法盘点；真实主库（用户 Mac）
  助手无权直连、且按只读规矩只能 SELECT，盘点须由主库方执行。
- 盘点语句（只读）：`SELECT "UserId", lower("Name"), count(*),
  count(*) FILTER (WHERE NOT "IsDeleted") FROM knowledge.tech_stack_categories
  GROUP BY 1, 2 HAVING count(*) > 1;`
- 处置选项（待裁决，迁移 apply 前必须先做，否则 CREATE UNIQUE INDEX 报错，
  属预期、绝不在迁移里静默合并/删数据）：
  A. 每组保留最早创建行（CreatedAt 升序， tie-break 取 Id 最小），删其余行。
     分类行是空分类占位行，条目按 Topic 名字符串聚合、不以外键引用分类行，
     故删重复占位行不丢条目数据，只去重占位本身；保留首拼写原文。
  B. 组内含软删行 + 活跃行时优先保留活跃行（与恢复语义一致）。
  C. 不放心则先导出重复组清单，由用户逐组定。
- 另注：函数索引不过滤软删行，故活跃 + 软删变体对在库层亦互斥；但 Handler
  对软删行走 Restore 不插新行，正常流不会撞该约束，只有绕过 Handler 的
  直接写库会撞。
