# 收敛核查清单（票241，逐服务断言，漏一个服务即不算收敛完成）

> 收敛日操作：各服务 `Logto__RejectOldIssuer=true` + 重启；旧 Identity `Auth:IssuanceEnabled=false`
> + 重启 Identity（停发）。回滚时把两处翻回并重启（见 ROLLBACK.md）。
> 前置：`Logto:Audience` 必须已配成 Console 里建的 API resource identifier（未配时
> 双轨期 audience 不校验，上线清单要求配上后恢复校验）。

## 逐服务核查表（每行都要打勾并记实测结果）

| 服务 | 旧 token 401 | Logto token 200 | 旧刷新 410 | Logto 刷新可用 | 门已接线（共享 LogtoClaimsGate） |
|---|---|---|---|---|---|
| Identity（5262/8080） | ☐ | ☐ | ☐ | ☐ | ☐（`AddJwtAuthentication` 统一注册，代码即证据） |
| Jobs | ☐ | ☐ | ☐（经网关/直连均同） | ☐ | ☐ |
| Interviews | ☐ | ☐ | ☐ | ☐ | ☐ |
| Knowledge | ☐ | ☐ | ☐ | ☐ | ☐ |
| Assessment | ☐ | ☐ | ☐ | ☐ | ☐ |
| Analytics | ☐ | ☐ | ☐ | ☐ | ☐ |
| AiGateway | ☐ | ☐ | ☐ | ☐ | ☐ |

- 网关：不做 JWT 校验，无核查行；但要确认 `identity-webhooks` 路由（Order 0）仍在。
- 断言方法（每服务）：持一个收敛前签发的旧 token 调一个 `[Authorize]` 端点 → 401；
  持 Logto access token 调同一端点 → 200；持旧 refresh token 调 `/api/auth/refresh` → 410；
  持 Logto refresh token 调 Logto `/oidc/token` → 新 access token 可用。
- 门接线确认方法：`grep -rn AddJwtAuthentication src/*/Program.cs` 命中 7 个服务，
  且 `ServiceDefaults.AddJwtAuthentication` 内注册的是唯一的 `LogtoClaimsGate`
  （per-service 分支实现是被禁止的——查到分支即不算通过）。

## 部署时填写的实测值

- 首次观测到的 Logto access token `expires_in`：______ 秒（token 的 `exp` 为准；
  刷新 token 默认 14 天/最长 180 天、grant TTL 默认 180 天、会话默认 14 天均为
  Logto 官方默认值，见 SETUP.md §7）。
- “Automatic account linking” toggle 确认 OFF：☐（Console 亲眼确认，不是默认假设）。
- Custom JWT 测试页三组样本通过：☐（pending 拒发 / approved 放行 / migrated 放行）。
- 旧 `/api/auth/register` 匿名调用被拒（410）：☐。
- 旧 Google 回调未知邮箱被拒（提示走 Logto）：☐。
- Blocklist 清理：全部已迁移邮箱已从 Security blocklist 清除，且抽查一个曾被列入的
  已迁移用户能正常登录（blocklist 是否影响登录未能从官方文档确认，清除 + 实测两步缺一不可）：☐。
- 待审批账号手握刚签发 token 调业务接口 → 403（零权限）：☐。
- 扫表心跳审计行在 30 分钟内出现：☐；`GET /api/logto/health` 无告警：☐。
- 审批回归：批准 / 驳回 / 停用 / 解锁 / 重置密码（Console 与 Management API 两路）全通：☐。
- 回滚演练通过（见 ROLLBACK-DRILL.md，先种数据再回滚）：☐。

## 旧 token 吊销传播（核查时必须向值班说明的口径）

- 旧 Identity 的 `SecurityStamp` 变更或管理员停用，不会自动传播到各服务对已签发旧 token
  的接受；灰度期靠管理员动作双写 + 扫表 Pass 3/4 对账补偿。
- Logto 侧：停用立即吊销刷新 token；access token 到 `exp` 为止，但 claims 门按镜像
  在下一次请求即拒绝（停用生效窗口 = 镜像同步延迟，webhook 秒级、扫表 ≤15 分钟）。
