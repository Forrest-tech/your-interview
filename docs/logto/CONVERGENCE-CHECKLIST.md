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

## §5 回归行（round-11 至 round-16 追加的验收项，每项都要有实测或单测证据）

- [ ] 灰度期旧侧建号被拒（round-11，与公开注册 410 是两回事；单测
  `CreateUser_GreyPeriod_Rejected_BeforeAnyDbCall`）：☐。
- [ ] 角色/权限残留窗口（round-11）：降级后旧 token 在有效期内仍带旧 scope，
  新发 token 才带新 scope（单测 `ResidualWindow_OldTokenKeepsOldScope_NewTokenReflectsDowngrade`）：☐。
- [ ] 删号时间界（round-11）：镜像置 deleted + 旧侧停用 + 门拒绝 deleted 行，三项在删号后
  数分钟内（下个扫表周期之前）全部完成，证明 `User.Deleted` 快传已接线、扫表只是兜底
  （单测 `HandleUserDeletedAsync` 不经过 `ReconcileAsync` 即完成三项）：☐。
- [ ] Console 管理员凭证当日复核清单（round-12：账号列表 + 轮换步骤，实测勾选）：☐。
- [ ] Console 管理员登录 MFA 强制状态确认（round-13，与上一项独立）：☐。
- [ ] 镜像写失败重发补行（round-13：首投递停用 + 标记成功、镜像写失败 → 重发补行后 2xx）：☐。
- [ ] 扫表无行补行（round-13：Logto 侧有标记无镜像行 → 按 Logto 侧标记与停用态补行，
  含上线前迁移用户；pending + 未停用 + 无行不补，走 fail-closed 重停用）：☐。
- [ ] 并发停用/取消停用事件（round-14）：镜像终态与 Logto 现态一致，无 last-writer-wins
  覆盖（单测 `SuspensionStatus_ConcurrentSuspendUnsuspend_MirrorMatchesLogto`）：☐。
- [ ] 对账范围（round-14）：Logto-only 用户未停用时，对账不因“旧侧查无此人”而停 Logto
  （单测 `Sweep_LogtoOnlyUser_Unsuspended_NotTouchedByOldSide`）：☐。
- [ ] 回滚重导批准映射（round-14）：重导集含 pending 与 suspended 用户，导后旧侧行一律
  `IsActive=false`（单测 `Import_ReimportSet_PendingAndSuspended_AllInactive`）：☐。
- [ ] 坏签名 webhook 被拒（round-15：坏签名/缺签名一律 401 且门不执行；
  单测 `LogtoWebhookControllerTests`，含合法签名正向对照）：☐。
- [ ] 邀请制端到端（round-15，实测）：发邀请 → 验证 → 待审批 → 批准 → 登录，全程通
  （邀请制本身不等同于批准；需真实 SMTP + Console 操作，沙盒无法代测）：☐。
- [ ] 停用双写失败注入（round-15：Logto 侧停用调用失败 → 旧侧不提交 + 明示错误 +
  失败审计行；单测 `Update_MappedUser_LogtoFailure_RollsBackOld_AndAudits`）：☐。
- [ ] 队列路径 2xx 契约（round-15，文档项）：本实现无队列（同步三步全成才是 2xx）；
  若将来改排队，队列 2xx 只表示持久化入队，最终失败必须经告警 surfacing、不得当成已送达：☐。
- [ ] 回滚演练范围切分（round-15）：收敛前演练只指回登录入口、Logto 数据不动；
  重导 + 开关翻回只属于收敛后演练（见 ROLLBACK-DRILL.md 两段）：☐。
- [ ] 门回退 fail-closed（round-15）：旧 token 无镜像行且旧侧查无此人 → 剥夺
  （单测 `Old_NoRow_Fallback_ThreeCases` 的“用户不存在”分支）：☐。
- [ ] 终局失败告警（round-16）：扫表连续失败 ≥ 3 次 → 健康端点 `sweepFailing=true`
  （单测 `Health_ConsecutiveSweepFailures_AlertFlag`；本实现无队列，所谓终局失败即
  投递持续 5xx + 扫表兜底失败，由健康端点 + ERROR 日志 surfacing）：☐。
- [ ] 收敛后服务凭证（round-16）：Service 角色旧 token 在收敛后仍走 Legacy 且门放行
  （单测 `Old_ServiceToken_NoMirrorRow_ActiveOldUser_Passes` + 选择器 Service 例外测试；
  实测另行确认服务间调用成功）：☐。
- [ ] 收敛后旧刷新 410 + 前端引导重登（round-16）：后端 410 单测
  `Refresh_Convergence_Returns410_AndNeverSends` 已有；实测旧刷新失败后前端回到
  Logto 登录页（前端 commit 5 落地后核查）：☐。
- [ ] 批准标记服务端独占（round-16）：`LogtoAdminController` 写入口全部带
  `admin.users.*` policy（单测 `ApprovalMarkWriters_AreAdminGated`）；公开接口无
  接受 customData/approval 的端点（grep 结论见 SETUP.md §12）：☐。
- [ ] 灰度期开关引入动作（round-7）：`Auth:IssuanceEnabled` 在灰度期开始时引入、
  默认 `true`（compose `.env.example` 已含该项，灰度期开始即确认其值为 `true`）：☐。
- [ ] 旧侧映射块（round-9/10）：已映射用户的旧 `/api/admin` 删除/重置密码/解锁被拒，
  未映射账号仍可用旧端点；停用/启用走双写（不是拒绝，见 SETUP.md §10 订正）：☐。
- [ ] 一次性域名屏蔽清单（round-8）：对着 Security 板块的 custom blocklist 核对一次性域名
  条目（不是原生开关，云端功能在自托管不存在）：☐。

## 旧 token 吊销传播（核查时必须向值班说明的口径）

- 旧 Identity 的 `SecurityStamp` 变更或管理员停用，不会自动传播到各服务对已签发旧 token
  的接受；灰度期靠管理员动作双写 + 扫表 Pass 3/4 对账补偿。
- Logto 侧：停用立即吊销刷新 token；access token 到 `exp` 为止，但 claims 门按镜像
  在下一次请求即拒绝（停用生效窗口 = 镜像同步延迟，webhook 秒级、扫表 ≤15 分钟）。
