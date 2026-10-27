# 回滚演练手册（票241，沙盒 compose 环境）

> 顺序铁律：**先种灰度期业务数据，再回滚**。不先种数据，回迁与连续性断言全部空转，
> 演练通过等于没练。本手册在沙盒 compose 环境跑（能起 docker 的环境）；
> 沙盒能证明什么、不能证明什么见 ROLLBACK.md 末节。

## 前置

- compose 已起：postgres、identity、网关、logto（`1.45.0` 锁定版）。
- `.env`：`LOGTO_ISSUER=http://localhost:3001/oidc`（双轨已开），
  `LOGTO_M2M_CLIENT_ID/SECRET`、`LOGTO_WEBHOOK_SIGNING_KEY` 已填，
  `LOGTO_GATE_LAUNCHED_AT` 已填（演练开始时间）。
- 管理员 token：用 Console 建的 M2M 凭据（或 `setup-m2m.py` 产物）。

## Step 0 — 种灰度期业务数据（必须先做）

1. 在 Logto Console 建一个测试用户 `drill-only@example.com`（或走公开注册），
   完成邮箱验证 → 接收端置停用 + `pending` → 管理员批准（Console 取消停用，
   或 `POST /api/admin/logto/users/{logtoUserId}/approve`）。
2. 用该用户经 Logto 登录拿到 access token，调一次业务写接口
   （例如新建一条 Tracker 条目或材料），记下返回的业务记录 id。
3. 查镜像行，记下该用户的 `LegacyUserId`（`identity.logto_user_mapping`）：
   ```sql
   SELECT "LogtoUserId", "LegacyUserId", "Approval", "IsSuspended"
     FROM identity.logto_user_mapping WHERE "Email" = 'drill-only@example.com';
   ```
   断言：`Approval='approved'`，`LegacyUserId` 非空。
4. 查业务记录归属字段 == 上一步的 `LegacyUserId`（这是回迁后要复核的同一条链）。

## Step 1 — 收敛（模拟收敛日）

1. 各服务 `LOGTO_REJECT_OLD_ISSUER=true` + 重启；Identity `AUTH_ISSUANCE_ENABLED=false` + 重启。
2. 断言：旧 token 调业务接口 → 401；Logto token → 200；旧 `/api/auth/refresh` → 410。

## Step 2 — 回滚（ROLLBACK.md 收敛后四步）

1. 各服务 `LOGTO_REJECT_OLD_ISSUER=false` + 重启。
2. 前端登录入口指回旧 Identity（演练环境用直连旧 `/api/auth/login` 代替前端点击）。
3. Identity `AUTH_ISSUANCE_ENABLED=true` + 重启。
4. 回迁 Step 0 的用户：
   ```bash
   python3 scripts/logto-migrate/export-logto-users.py \
     --since "$LOGTO_GATE_LAUNCHED_AT" --out ./drill-logto-only.json
   # 确认 drill-only@example.com 在导出里，且旧库 users 表无对应 Guid 行
   curl -s -X POST http://localhost:5200/api/admin/users/import-logto \
     -H "Authorization: Bearer <管理员旧 token>" -H 'Content-Type: application/json' \
     -d '{"legacyUserId": "<Step 0 的 LegacyUserId>", "email": "drill-only@example.com", "displayName": "Drill User"}'
   # → 201，id == Step 0 的 LegacyUserId（一字不差，否则演练失败）
   curl -s -X POST http://localhost:5200/api/admin/users/<id>/reset-password \
     -H "Authorization: Bearer <管理员旧 token>" -H 'Content-Type: application/json' \
     -d '{"newPassword": "<临时密码>"}'
   ```

## Step 3 — 回滚验收断言（逐条打勾）

- [ ] 旧登录恢复：种子管理员旧账号登录 → 200，业务接口 200。
- [ ] 回迁账号 Guid == Step 0 的 `LegacyUserId`（一字不差）。
- [ ] Step 0 的业务记录归属字段仍 == 该 Guid（业务数据不断链）。
- [ ] 回迁账号“临时密码登录 → 强制改密 → 正常登录”全通。
- [ ] 行数核对：导出候选数 = 已有旧行跳过数 + 回迁成功数 + 失败清单数。
- [ ] 旧 token 在回滚后重新被接受（双 scheme 已重挂旧 issuer）。

任一断言失败即演练失败：先修代码/配置再重练，不要把“写清了”当“练过了”。
