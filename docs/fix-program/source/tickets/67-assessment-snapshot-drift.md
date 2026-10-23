# 票 67 — Assessment 模型快照漂移盘点（跟进票，源：票 66 动手前基线实测）

## 事实
票 66 动手前，在未改代码的 tip（6a37e73）上对 Services.Assessment 跑
`dotnet ef migrations has-pending-model-changes` 已报存在 pending model changes——
即 ModelSnapshot 与当前模型代码不一致（先存漂移，非票 66 引入；票 66 的查询
过滤器变更不产生 schema 变更，故未随票 66 处理）。

## 任务
1. 盘点漂移具体内容（哪个实体/属性/索引与快照不一致、何时引入）。
2. 若漂移只是快照滞后（模型代码为准、无真实 schema 变更需求）：补一笔
   「快照对齐」迁移（无实际 schema 变更或仅元数据），带 Designer.cs+ModelSnapshot，
   使 has-pending-model-changes 归零。
3. 若盘点发现真实未落库的 schema 意图：停工上报协调者，不得自行决定落库。

## 验收
- has-pending-model-changes 在本票提交后归零（命令输出存证据）。
- 迁移三件套齐全；Assessment.Tests 全绿；对既有数据库 database update 无破坏
  （存量数据不变）。

## 影响面 / 不变项
- 影响面：Assessment 服务全部 EF 读写（快照仅影响迁移生成，不影响运行时查询）。
- 不变项：不改任何实体行为与 API 契约；不改其他服务的快照（各服务分别盘点，
  本票只管 Assessment；若发现其他服务同病，另行立票）。
