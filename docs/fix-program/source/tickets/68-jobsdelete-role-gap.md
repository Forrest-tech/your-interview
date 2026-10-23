# 票 68 — JobsDelete 权限与默认角色缺口核查（跟进票，源：票 14 票面外发现 1）

## 事实（票 14 实现者实查，待本票复核定性）
`Permissions.cs` 的 DefaultPermissions 中，默认 User 角色没有 JobsDelete；
而 Jobs 服务多个既有 DELETE 端点以 JobsDelete 授权（如 DeleteApplication、
DeleteAnswerTemplate、DeleteCommunication、DeleteResumeText 等）——普通注册
用户调用这些端点将得 403，删不掉自己的数据。票 14 的删问题端点已绕开
（用 JobsWrite，同 DeleteCoverLetter 先例），但既有端点未动。

## 任务
1. 复核：列全以 JobsDelete 授权的端点清单 + 各角色（User/PowerUser/Admin）
   实际权限矩阵，确认缺口真实存在且范围。
2. 定性二选一并说明理由：(a) 默认角色应含 JobsDelete（改 DefaultPermissions，
   注意存量用户权限是否随之生效/需迁移）；或 (b) 端点授权口径应改为 JobsWrite
   （与 DeleteCoverLetter 先例对齐）。涉及口径选择 → 先报协调者转用户拍板，
   不得自行决定。
3. 按拍板结果实现 + 回归（普通用户真实账号活体：各 DELETE 端点可删自己数据、
   不可删他人数据）。

## 验收
- 权限矩阵与端点清单落证据；拍板记录在案。
- §7：授权判定相关纯逻辑若有改动须带单测（含 boundary：无权限/跨用户）。
- 不变项：不放宽跨用户隔离（票 03 口径）；不动 JobsWrite 既有语义；
  PowerUser/Admin 行为不回退。
