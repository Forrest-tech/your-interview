# 回滚手册（票241）

> 原则：回滚不是“删 Logto 重来”，而是把登录入口与签发权指回旧 Identity，
> Logto 数据原地不动（下次再切时仍在）。回滚演练见 ROLLBACK-DRILL.md，
> 演练必须先种灰度期业务数据，否则断言空转。

## 收敛前回滚（灰度期，Logto 还不是唯一发行方）

1. 前端登录入口指回旧 Identity（环境开关切回旧登录页；Logto 按钮隐藏）。
2. 各服务 `Logto:Issuer` 保持不动亦可（双 scheme 下旧 token 仍被接受），
   或直接清空 `Logto:Issuer` 回到单轨旧口径（重启各服务）。
3. 旧注册端点保持 `LegacyRegistrationEnabled=false`（注册仍走 Logto，直至另行决策），
   或经决策翻回 `true`（不推荐：审批门随即被旁路）。
4. 验收：旧登录可用；Logto 侧数据不动；下次再切只需反向操作。

## 收敛后回滚（旧 scheme 已下线、旧 Identity 已停发，四步缺一不可）

1. **恢复旧 scheme**：各服务 `Logto__RejectOldIssuer` 翻回 `false` 并重启
   （双 scheme 下重挂旧 issuer；旧 token 重新被接受）。
2. **前端登录入口指回旧 Identity**（同收敛前第 1 步）。
3. **恢复旧签发**：`.env` 里 `AUTH_ISSUANCE_ENABLED` 翻回 `true` 并重启 Identity
   （配置开关，非重部署；旧登录/刷新随即恢复签发）。
4. **灰度期 Logto-only 用户回迁**（关键，见下）。

## 灰度期 Logto-only 用户回迁（业务 Guid 不断链）

Logto-only 用户 = 灰度期在 Logto 新注册、在旧 Identity 没有账号的用户。
他们的业务数据在灰度期以镜像行 `LegacyUserId`（审批门分配的 Guid）为外键产生，
回迁必须按**这个 Guid**重建旧账号，否则业务数据断链。

```bash
# 1) 导出候选（含镜像 legacyUserId；不含任何密码哈希，Logto 本来也不导出哈希）
export LOGTO_ENDPOINT=http://localhost:3001
export LOGTO_M2M_CLIENT_ID=... LOGTO_M2M_CLIENT_SECRET=...
python3 scripts/logto-migrate/export-logto-users.py \
  --since "$LOGTO_GATE_LAUNCHED_AT" --out ./logto-only-users.json

# 2) 逐条查旧库 users 表：无对应 Guid 行的才是真 Logto-only（镜像行本身不能判定，
#    门也给新注册写行）。有对应行的说明已是迁移用户，跳过。
# 3) 对每条真 Logto-only，调回迁端点（admin.users.write）按 legacyUserId 重建：
#    POST /api/admin/logto/users/import
#      {"legacyUserId": "<镜像 Guid>", "logtoUserId": "<Logto 用户 id,必填且必须是导出文件里的真值>",
#       "email": "<邮箱>", "displayName": "<显示名>"}
#    端点建账号时 MustChangePassword=true、密码置为不可登录的随机哈希。
#    logtoUserId 绝不能留空占位：空主键行会被扫表存在检查误判为“Logto 侧已删号”。
#    批准映射（round-13）：端点先查 Logto 现态再定旧侧启用态 —— pending 或 suspended
#    的重导为 IsActive=false，只有 approved 且未停用才导为启用；重导集是上线后在
#    Logto 创建的全部用户（灰度期 + 收敛后注册者），不只收敛前。
# 4) 重置密码邮件机制（决议：现有端点 + 运营通知，不新增 forgot-password 端点）：
#    管理员调现有 POST /api/admin/users/{id}/reset-password 设临时密码，
#    再由运营经已有邮箱渠道把临时密码通知用户（应用内无 SMTP 发信器，
#    不要假装存在自动邮件）。用户首次登录后必须改密（MustChangePassword）。
```

回迁验收（逐条断言，见 ROLLBACK-DRILL.md）：

- [ ] 回迁账号的旧 `Id` == 镜像 `LegacyUserId`（Guid 一字不差）。
- [ ] 灰度期该用户名下产生的业务行仍按该 Guid 归属（抽查一行业务记录的归属字段）。
- [ ] 回迁账号经“临时密码登录 → 强制改密 → 正常登录”全通。
- [ ] 行数核对：导出候选数 = 已有旧行跳过数 + 回迁成功数 + 失败清单数。
- [ ] 批准映射断言：收敛时仍 pending 或 suspended 的用户重导后旧侧 `IsActive=false`；
  approved 且未停用的用户重导后 `IsActive=true`（单测 `Import_ReimportSet_PendingAndSuspended_AllInactive`
  已覆盖映射函数，演练里抽查两类各一例）。
- [ ] 演练范围切分（round-15）：收敛前回滚演练只含“前端登录入口指回旧 Identity”
  且 Logto 数据不动（不重导、不翻开关）；重导 + 开关翻回只属于收敛后演练。

## 沙盒 drill 能证明什么、不能证明什么

- 能证明：开关翻转行为（410 ↔ 正常）、回迁端点的 Guid 保留、镜像导出格式、
  双 scheme 选择器分流、claims 门的新旧判定——全部是代码与配置行为。
- 不能证明：真实 Logto Console 的行为（首次 onboarding、M2M 指派、webhook 投递时序）、
  真实 SMTP 投递、Mac 上 Docker 的网络与数据卷行为、真实 Google 连接器的链接表现。
  因此沙盒 drill 通过 ≠ 生产回滚一定成功；生产回滚前仍需在 Mac 上按
  ROLLBACK-DRILL.md 跑一次真实 drill（needs-user 项，见最终报告）。
