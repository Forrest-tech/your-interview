# Logto 集成部署手册（票241）

> 版本锁定：`ghcr.io/logto-io/logto:1.45.0`（compose 已锁定，绝不用 `:latest`）。
> 凭据只进本机 `.env`（与 `ADMIN_PASSWORD` 同 pattern），绝不入库、绝不进工单与聊天记录。
> 本手册的操作顺序就是割接顺序：先 M2M 与审批门，后迁移，最后开注册。

## 0. 部署前确认（Day-0 门，缺一不开工）

- [ ] SMTP 通道与发件域名已定（Logto 验证邮件就靠它；配错则新用户卡在验证）。
- [ ] 驳回通知细节已定（通知与否、文案、通道）；待审批列表入口 = Logto Console（见 §6）。
- [ ] `/admin` 页去留已定（割接期只读；割接后下线或二期改薄代理，不在一天计划内）。

## 1. 起服务与首管理员（防首占）

```bash
docker compose up -d postgres          # 已有 pgdata 的旧卷需先补一次库：
docker exec -it yourinterview-postgres psql -U postgres -c "CREATE DATABASE logto;"
docker compose up -d logto
```

- Console 只绑 `127.0.0.1:3002`（compose 已写死）；远程访问走 SSH 隧道或 VPN，绝不开公网端口。
- **首管理员必须在部署后立即在 localhost 上完成首次 onboarding 创建**，未完成前不开放任何外部路由。
  自托管 OSS 在**未装 license 时没有协作者邀请功能**（团队协作是 v1.45.0 Self-Hosted Pro
  的 license 解锁项）：管理面 = 这一个 onboarding 管理员账号，口令用密码管理器保管。
  若日后装了 Pro license 启用多人协作，必须重审本节（管理员清单复核流程），代码不因此改动。
- **租户管理员 ≠ 应用 Admin**：迁移导入 `admin@your-interview.local` 只给应用内 Admin 角色，
  不授予 Logto Console 管理权；Console 管理权只来自 onboarding 创建或（有 license 后）的邀请。
  双重确认验收：(a) 该账号能用 Logto 登录应用且为 Admin；(b) 操作员账号能登录 Console 行使租户管理。
- **回归核查（§5 回归行，实测勾选，不是文档）**：两项独立，缺一不可 ——
  (a) Console 管理员凭证当日复核清单（账号列表 + 轮换步骤，round-12）；
  (b) Console 管理员登录的 MFA 强制状态确认（round-10/13，与 (a) 是两回事）。

## 2. M2M 应用 + Management API 角色（前置凭证，第一步）

审批门、迁移、扫表的一切 Management API 调用都依赖它；没有它全部调用 401。

```bash
# 1) 先在 Console → Applications 建一个临时 M2M 应用，取其 token（唯一需要人工的一步）
export LOGTO_ENDPOINT=http://localhost:3001 LOGTO_ADMIN_TOKEN=<临时 M2M token>
python3 scripts/logto-migrate/setup-m2m.py
# 脚本建专用应用、指派管理角色、用新凭据调一次 GET /api/users 验收，打印 .env 两行。
```

脚本无法唯一确定角色时会自动中止并打印候选清单，改走 Console 手工指派（应用 → Roles →
选带 Logto 图标的管理角色），再重跑验收。临时 M2M 应用验收通过后即可删除。

## 3. Sign-in experience 与 API resource（Console 内配置）

- Sign-in experience：注册开、邮箱验证开、密码策略按现有口径；**“Automatic account linking”
  显式设为 OFF**（不是默认假设：部署清单里要亲眼确认它处于 OFF；见 §8 Google 一节）。
- 一次性邮箱/域名屏蔽（round-8 订正）：自托管 OSS 没有原生一次性邮箱开关（那是云端功能），
  机制是 Security 板块维护的 custom blocklist，把一次性域名作为条目加进去；验收时对着这张
  自定义清单核对，不要去找原生开关。
- API resource：新建一个代表本应用后端的 resource，identifier 填进各服务的
  `Logto:Audience`；在其上定义与现有 `Permissions.*` 一一同名的 scope。
- 角色：建与现有角色同名的 Logto 角色（Admin / PowerUser / User / Viewer），每个角色授予
  对应 scope 集合。Service 角色不走 Logto，仍走现有服务间凭证。
- 品牌：logo 与主色按现有品牌对齐（基础对齐即可，精细化二期）。

## 4. SMTP 连接器与验证邮件实测

- 接入选定的 SMTP 通道（自有邮箱 SMTP / SendGrid / Mailgun / AWS SES 任一），发件域名与 Day-0 结论一致。
- 实测：注册一个测试邮箱，确认验证邮件到达、点链接后账号进入待审批（不是直接可用）。
  未验证邮箱不能登录是 Logto 自身 + 审批门双层保证（见 §6）。

## 5. Webhook 与 Custom JWT（审批门接线）

### Webhook（四个端点，都走 YARP 显式路由，不依赖泛路由）

| 端点 | 事件 | 说明 |
|---|---|---|
| `POST /api/webhooks/logto/user-created` | `User.Created` | 接收端 `Services.Identity`（`LogtoWebhookController`），签名验签（`logto-signature-sha-256`，HMAC-SHA256 hex），不走 JWT |
| `POST /api/webhooks/logto/suspension-status-updated` | `User.SuspensionStatus.Updated` | 同上；payload 的 `isSuspended` 只作 presence 校验，处理器一律以 fresh GET 为准 |
| `POST /api/webhooks/logto/user-data-updated` | `User.Data.Updated` | 验证完成翻转的事件路（无专用验证事件，见 §6.2） |
| `POST /api/webhooks/logto/user-deleted` | `User.Deleted` | 删号快传：镜像置 deleted + 旧侧停用，扫表存在检查仍是兜底 |

- YARP 路由：`identity-webhooks`（`Path: /api/webhooks/{**catch-all}` → identity，Order 0，
  确定性优先匹配；`appsettings.json` 已加）。
- 投递 URL（compose 内 Logto 容器视角）：`http://identity:8080/api/webhooks/logto/user-created`
  与 `http://identity:8080/api/webhooks/logto/suspension-status-updated`（docker DNS 直达；
  若 Logto 在 compose 外，则改走网关 `http://<gateway-host>:5200/api/webhooks/logto/...`）。
- 签名密钥：Console webhook 详情页的 Signing Key → `.env` 的 `LOGTO_WEBHOOK_SIGNING_KEY`。
- 投递语义：接收端只有置停用 + 打 `pending` 标记 + 写镜像行三步都成功才回 2xx；
  任一步失败回 5xx，投递方按其策略有限次重试（重试次数不写死——Logto 公开文档未给
  出固定次数，设计不依赖具体次数：快速 2xx + 幂等 + 扫表兜底）。
  慢处理器会烧掉重试预算，所以接收端只做最少调用（见 `LogtoApprovalGate`），
  Management API 客户端另设 10 秒超时，超时即 5xx 走重试，不 hanging。
  接收端是同步处理，不是“先回 2xx 再排队”——同步三步全成才是 2xx 的含义。
  队列路径 2xx 契约（round-15，若将来改排队才适用，本实现无队列）：队列 2xx 只表示
  “投递已持久化入队且三步有队列重试 + 扫表兜底保证”，入队后最终失败必须经告警
  surfacing，绝不能被当成已送达。
- 签名是接收端唯一鉴权（round-15）：坏签名/缺签名一律 401 且门根本不执行
  （回归测试 `LogtoWebhookControllerTests` 覆盖坏签名、缺签名与合法签名正向对照）。
- **暂停规则（导入默认流程）**：批量导入开始前先在 Console 暂停 User.Created webhook 订阅，
  导入完成并对账通过后再恢复；脚本非 dry-run 时强制要求 `--webhook-paused`，否则拒绝执行。
  暂停窗口内漏掉的真实新注册事件由 15 分钟扫表兜底（见 §6）。

### Custom JWT（access token 脚本，Console → Custom JWT）

把 `docs/logto/custom-jwt.js` 原样粘进 Console 的 Custom JWT 脚本框：

- 对 `approval === "pending"` 的用户直接 `api.denyAccess()`（Logto 侧快速拒发 token）。
- 其余用户在 access token 上带 `legacy_user_id`（customData）与 `roles`（展示用）声明。
- **分层说明**：脚本只是 Logto 侧的快速拒绝，应用侧的权威门是各服务共享的
  claims 转换层（`LogtoClaimsGate`，见 §7）——脚本漏掉或未部署时，claims 门仍按镜像
  零权限拒绝未批账号；脚本绝不能成为唯一防线。
- 上线前必须在 Console 的测试页跑通三组样本：pending → 拒发；approved → 放行带声明；
  migrated → 放行带声明。脚本上下文的字段名若与实测不符，以测试页为准修正脚本，
  不要猜（`roles`/`customData` 的读取已做多形态兼容，见脚本注释）。

## 6. 审批流（默认开启）与扫表

流程：注册 → 邮箱验证 → 待审批（停用态）→ 管理员批准（取消停用）→ 登录。

- 接收端守卫（强制）：`User.Created` 先查 `identity.logto_user_mapping`；
  行里 `approval=migrated` = 迁移用户，直接放行，绝不置停用、不打 `pending`。
  迁移导入时同步打 `approval=migrated`（脚本），批量导入期间再暂停 webhook 作第二层，
  但不以此代替守卫。
- 批准：管理员在 Console 取消停用 = 批准；或调同步端点
  `POST /api/admin/logto/users/{logtoUserId}/approve`（`admin.users.write`，两步都成功才回成功）。
  批准事件（`User.SuspensionStatus.Updated`）到达时，接收端重查 Logto 现态并按
  §6.1 的排序规则应用；未验证邮箱的账号即使被取消停用，镜像也保持未批准（见 §6.2）。
- 驳回 / 搁置：保持停用 = 搁置；删除账号 = 驳回（通知按 Day-0 结论）。
- 邀请制（并存路径）：Console 关掉公开注册（"Enable user registration" OFF），
  管理员发 magic link 邀请：one-time token，默认 600 秒（10 分钟），`expiresIn` 可延长
  （如 172800 秒 = 2 天，官方文档建议至少一天）；或经 Management API 建号后引导注册。
  被邀用户同样走验证 → 待审批 → 批准（邀请制本身不等同于批准）。

### 6.1 悬停事件排序（round-4 决议）

事件 payload 只当触发器，不可信其自带的状态与时间。处理器每次重查 Logto 现态，
只有 fresh `updatedAt` 大于镜像行的 `LastEventAt` 时才应用；延迟/重发的旧取消停用
事件（`updatedAt` 未变或更旧）一律忽略，绝不冲掉更新的停用。镜像行的 `LastEventAt`
每次应用后更新。
**同一用户事件按用户串行化**（round-13：门实例是 per-request 的，实例锁无用，
故用进程级静态 per-user `SemaphoreSlim`）：并发的停用/取消停用事件绝不能双双通过
水位守卫、再由 last-writer-wins 决定镜像态；回归测试
`SuspensionStatus_ConcurrentSuspendUnsuspend_MirrorMatchesLogto` 覆盖该剧本。

### 6.2 批准必须验证邮箱（round-3 决议）

取消停用一个未验证邮箱的账号时，镜像保持未批准（`pending`），应用侧 claims 门继续
零权限——“未验证邮箱不能登录”无条件成立。同步批准端点对未验证账号直接回 409，
不静默批准。邮箱验证状态取自 Logto 用户记录（`primaryEmailVerified` 等字段多形态解析，
见 `LogtoManagementClient`；字段名以 Console 实测为准，门是最终兜底）。
**验证状态来源口径（round-8）**：Management API `GET /api/users/{id}` 的用户对象是
第一来源；若该对象不带验证字段，则退到“构造验证”（hosted 注册流程本身强制验证）；
试点时必须确认实际适用的是哪一来源并记入部署清单。
**验证完成翻转的两条路条件不同，绝不能合并**：事件路（`User.Data.Updated`）在
pending + 现态已验证 + 现态未停用时翻转（事件本身就是“验证刚完成”的证据，且现态
未停用说明管理员批准已发生）；扫表路只在镜像已记录该次取消停用（镜像 `IsSuspended=false`）
的前提下翻转——镜像仍显示停用，说明取消停用事件根本没到，属于无归因，走 fail-closed
重停用，不翻转。Logto 没有专用验证事件，`User.Data.Updated` 检查与扫表就是全部触发器。

### 6.3 扫表（每 15 分钟，Identity 内后台任务，失败只记日志不终止）

| Pass | 范围 | 规则 |
|---|---|---|
| Pass 1 审批标记 | `ApprovalLaunchAt` 之后创建的用户 | 缺标记且不在映射表 → 置停用 + 打 `pending` + 镜像行 + 审计；`pending` 但未停用 → 先看镜像是否已记录该次取消停用：已记录 + 现态已验证 → 扫表翻转批准（见 §6.2 两条路），否则**无归因记录即视为无管理员操作，fail-closed 重新置停用 + 审计 + 告警日志**（见 §6.4）；`approved`/`migrated` 不动 |
| Pass 2 已映射但 pending 纠正 | 镜像行 `approval=pending` 且其 `LegacyUserId` 在旧 users 表里存在 | 标记纠为 `migrated`（customData + 镜像）；停用处理看来源标记：`SuspendedByGate=true`（门/扫表执行的误停）→ 解除停用 + 审计；否则视为管理员操作 → 只修标记，不碰停用态 |
| Pass 3/4 停用态与双写对账 | 全部镜像行（含上线前用户，不受 `ApprovalLaunchAt` 限制） | 逐行重查 Logto 现态：镜像 `IsSuspended` 与 Logto 不一致 → 同步镜像 + 审计（丢了的停用事件就此纠正）；Logto 侧用户已不存在 → 镜像置 `deleted` + 旧侧停用 + 审计（删号兜底；快传是 `User.Deleted` 事件）；旧 users 行存在时再比对旧 `IsActive`：旧启用 + Logto 停用 → 停用旧账号 + 审计（补齐 crashed disable）；旧停用 + Logto 未停用 → 停用 Logto 侧 + 审计（不自动启用旧账号，fail-closed；管理员重做一次启用即可）。**对账范围只覆盖有旧侧行的用户**（round-13）：Logto-only 用户的停用态只由 Logto + 镜像对账管，不碰旧侧（也没有旧行可写） |

- **扫表补行（round-12/13，不受上线时间限制）**：Logto 侧有标记但无镜像行的用户，
  按 Logto 侧标记与停用态补镜像行（approved/migrated 任何时候都补；pending 只在现态
  停用时补，`SuspendedByGate=true`）。pending + 未停用 + 无行**不补**——它走 fail-closed
  重停用路径（重停用自己写行）；把它补成“已记录未停用”等于把一次无归因的取消停用
  洗成有记录的，再翻转批准就成漏洞。
- **接收端幂等补行（round-12）**：已停用 + 已打 pending 的重发在回 2xx 之前必须先核实并
  补写镜像行，绝不能跳过缺失的镜像行（首投递在置停用 + 打标记成功后、写镜像前失败
  的 stuck 态就靠这一条 + 扫表补行修复；回归测试覆盖“镜像写失败 → 重发补行”）。

- 镜像列 `SuspendedByGate`：true = 本次停用由门/扫表执行，false = 在 Logto 侧观察到的
  管理员停用。Logto 不暴露停用时间戳，`updatedAt` 又会被每次 PATCH 改动，来源无法靠
  时间推断，必须显式记录。
- 瞬时重停用说明：管理员批准后、标记更新延迟的窗口内若扫表恰好运行，可能把刚批准的
  用户瞬时重停用一次——方向是 fail-safe，管理员再批准一次即恢复，不丢数据，审计可查。
  （事件驱动的批准路径已使该窗口极小；扫表 Pass 1 的 fail-closed 规则是其代价。）

### 6.4 健康监控与告警（审计日志 alone 不够）

- `GET /api/logto/health`（`admin.users.read`）：上次 webhook 到达时间、上次扫表时间、
  上次扫表动作数、连续扫表失败数；告警阈值：扫表心跳缺失超 30 分钟、连续扫表失败 ≥3 次、
  webhook 长时间静默（结合注册量判断）。扫表每次运行写心跳审计行。
- **归因限制（必须知道）**：Logto 的审计日志只覆盖终端用户在 sign-in experience 里的活动，
  不含 Console 里的管理员操作——所以扫表无法通过 Logto 审计日志给一次 Console 取消停用
  归因。这正是 Pass 1 对无归因解除一律 fail-closed 重停用的原因；真正的批准归因通道是
  webhook 事件本身与同步批准端点（带操作员的审计行）。

## 7. 双轨校验（共享门，各服务同一实现）

- **机制（双 scheme，默认；选择器唯一，绝无顺序回退）**：`AddPolicyScheme("LogtoOrLegacy")` +
  `ForwardDefaultSelector` 按 issuer 分流。选择器先读**未验证的 JWT payload 里的 `iss`**
  （仅用于路由，不作信任依据——伪造 `iss` 只会把 token 误路由到另一个 scheme，
  那个 scheme 的完整签名/发行方/有效期校验随后仍会拒绝它），再由分流到的 scheme 做
  完整校验：Logto scheme 走 OIDC discovery（完整路径 `<issuer>/.well-known/openid-configuration`，
  JWKS 自动获取）；旧 scheme 走原对称密钥。
- **未配 `Logto:Issuer` 时行为与票241 之前一字不变**（单 scheme 旧口径，门不启用）。
- **共享门（唯一实现，禁 per-service 分支）**：`LogtoClaimsGate`（BuildingBlocks，
  `IClaimsTransformation`，`ServiceDefaults.AddJwtAuthentication` 统一注册，
  7 个调该方法的服务全部命中）。收敛核查必须逐服务确认门已接线（见 CONVERGENCE-CHECKLIST）。
- 门规则（灰度期双 scheme 同门）：
  - Logto token：按 Logto sub 查镜像行；无行 → 零权限；`approval` 非 `approved`/`migrated` → 零权限；
    `IsSuspended` → 零权限；通过后 `scope` claim（空格分隔）映射为 `perm` 声明（只认
    `Permissions.*` 已知值，未知 scope 忽略），业务 Guid 取镜像行的 `LegacyUserId`（表为准，
    不信 token 里的 `legacy_user_id` 声明）。
  - 旧 token：自带 `perm` 声明；按 NameIdentifier（业务 Guid）查镜像行，有行 →
    镜像门接管（`IsSuspended` 或 `approval` 非 approved/migrated，一律剥掉全部 `perm`
    声明，空标记也剥）；无行 → **旧侧 `IsActive` 回退**（round-6）：旧侧启用 → 原样
    放行（未迁移老用户的灰度期登录就靠这一条）；旧侧停用 → 剥夺（未迁移用户的管理员
    停用在旧侧生效）；**旧侧查无此人 → 剥夺**（round-15 fail-closed 默认：缺失 ≠ 启用）。
    管理员停用因此同时切断两条 token 路径；旧 scheme 绝不豁免于门。
  - **角色/权限残留窗口（round-10）**：角色或权限变更只在下一次 token 签发时生效；
    已降级用户的已发 token 在剩余有效期内仍带旧 scope，直到过期/刷新——这是机制本身，
    不是缺陷；门从不按请求查实时角色。
  - 失败默认：scope 缺失、scope 未映射、subject 无对应业务 Guid，一律零权限（不是默认 User）。
  - 镜像查询：直接跨 schema 只读查询 `identity.logto_user_mapping`（单条索引查询，不缓存；
    各服务共用同一个 Postgres 库，读第一个配置的连接串即可，查询本身 schema 限定）。
    Logto token 查不到行或查询异常 → 零权限（fail-closed）；旧 token 查询异常 → 原样放行
    （旧 token 的权威是旧 Identity，镜像库故障不应把已签发 token 全量打掉；停用传播
    靠双写 + 扫表补齐，见 §6.3 Pass 3/4）。
- Service 角色（服务账号）不走 Logto，仍走现有服务间凭证；token 里即使出现 Service 也忽略。
  收敛后 Service 旧 token 仍路由到 Legacy scheme（选择器在 `RejectOldIssuer` 下对 Service
  旧 token 例外放行），且服务账号无镜像行、旧侧启用时门按回退放行——服务间调用不断
  （回归测试 `Old_ServiceToken_NoMirrorRow_ActiveOldUser_Passes`；收敛核查里“旧 token
  被拒绝”只针对用户 token，服务凭证另行验证，见 CONVERGENCE-CHECKLIST）。
- Token 有效期（实测值以部署清单为准）：Logto 刷新 token 默认 14 天（最长 180 天）、
  grant TTL 默认 180 天、会话默认 14 天；access token 有效期由配置决定，以 token 的 `exp`
  为准。停用会立即吊销刷新 token；access token 在 `exp` 前仍有效，但 claims 门按镜像
  在下一次请求即拒绝——停用生效窗口 = 镜像同步延迟（webhook 秒级，扫表 ≤15 分钟），
  不是 access token 剩余有效期。部署清单里必须记下首次观测到的 access token `expires_in`：
  ______ 秒（实施时填写）。

## 8. Google 登录用户

- Logto 侧配 Google 社交连接器；**“Automatic account linking”显式 OFF**（§3 已要求亲眼确认）。
  不依赖自动链接开关做任何匹配。
- 匹配机制（实施的口径）：导入脚本按邮箱匹配（Management API 建号时 `primaryEmail` 即旧邮箱）；
  Google 身份只接受已验证邮箱——Google 侧只返回已验证邮箱，Logto 也只把已验证的社交邮箱
  同步为 `primaryEmail`（官方文档口径）；旧 Google 回调在灰度期同样要求 `EmailVerified`
 （代码已实现，且灰度期起只放行已存在账号，见 §9）。
- 首次 Google 登录流程（自动链接 OFF 时）：用户走 Logto 的手动链接流程，
  用邮箱验证码证明邮箱所有权后链接到已导入账号；若用户误建了重复账号，管理员在 Console
  删除重复账号后让用户重走手动链接。**不建任何自动合并流程**：门分配的 Guid 全是新 Guid，
  绝不会与老用户 Guid 碰撞，所以重复账号不可能被扫表误合并。
- 未验证邮箱的社交身份绝不挂到已有本地账号（防接管形态），未验证者走新注册审批流。

## 9. 旧端点灰度开关（Auth 节）

| 开关 | 灰度期开始 | 收敛日 | 回滚 |
|---|---|---|---|
| `Auth:LegacyRegistrationEnabled` | `false`（旧 `POST /api/auth/register` 服务端 410，不只摘前端入口） | 保持 `false` | 不动（注册始终走 Logto） |
| `Auth:IssuanceEnabled` | `true` | `false`（旧登录/刷新 410，停发） | 翻回 `true` 并重启 Identity（配置开关，非重部署） |

- 旧 Google 回调灰度门：`LegacyRegistrationEnabled=false` 时，未知 Google 邮箱不再自动建号，
  直接拒绝并提示走 Logto 注册；已存在账号的 Google 登录不受影响。
- 刷新路径不跨发行方：旧 refresh token 只向旧 Identity 刷新出旧 token，Logto refresh token
  只向 Logto 刷新；收敛日旧 Identity 停发（含刷新）后，仍持旧 refresh token 的会话刷新失败，
  前端引导重登（Logto 登录）。前端 401 处理已是“刷新失败 → 回登录页”，收敛后该登录页即 Logto 入口。
  后端 410 已有回归测试（`LogtoAuthSwitchTests.Refresh_Convergence`）；收敛日实测“旧刷新 410 →
  前端引导重登”见 CONVERGENCE-CHECKLIST §5 回归行（round-16）。
- 旧 token 吊销传播（说清，不含糊）：旧 Identity 的 `SecurityStamp` 变更或管理员停用，
  不会自动传播到各服务对已签发旧 token 的接受（各服务只验签名与发行方）；补偿是灰度期
  管理员动作双写（旧 `/admin` 停用/启用同步调 Management API，见 §10）+ §6.3 Pass 3/4 对账。
  Logto 侧：停用立即吊销刷新 token，access token 到 `exp` 为止但 claims 门按镜像拒绝。

## 10. 管理员操作面（割接期）

- 日常管理用 Logto Admin Console（3002，localhost + 隧道）：列表、搜索、新建、停用/恢复、
  删除、改角色、查用户审计日志。
- 同步管理端点（`[Authorize]` + `admin.users.*` policy，与旧 `/admin` 同口径）：
  - `POST /api/admin/logto/users/{logtoUserId}/approve`：批准（未验证邮箱回 409；两步都成功才回成功）。
  - `POST /api/admin/logto/users/{logtoUserId}/reset-password`：重置密码（Management API
    `PATCH /users/:id/password`）。
  - `POST /api/admin/logto/users/{logtoUserId}/unlock`：解锁 = 取消停用且不改批准标记；
    `pending` 用户拒绝走 unlock（必须走 approve，解锁与批准职责分离）。
  - `GET /api/admin/logto/users/{logtoUserId}`：查 Logto 现态 + 镜像行（供排障与 drill 断言）。
- 旧 `/admin` 页割接期只读：前端 `logtoCutover=true` 时隐藏新建/编辑/删除/启用停用动作，
  显示 Logto Console 链接；写操作走 Console 或上述同步端点。
- **旧 `/admin` 停用/启用双写（单动作，round-4/5 决议）**：`AdminUpdateUserCommandHandler`
  在 IsActive 翻转时同步调 Management API（有映射行且 Management 已配置时）：
  先调 Logto（停用/取消停用），成功后再一次 `SaveChanges` 提交旧侧停用 + 镜像同步 + 审计；
  Logto 调用失败 → 整个操作失败并向管理员明示错误，旧侧绝不提交（不是“提交了再说”），
  且失败审计行单独保存（`admin.user.logto-sync-failed`；回归测试 round-15
  `Update_MappedUser_LogtoFailure_RollsBackOld_AndAudits` 做失败注入断言）；
  若旧侧提交恰好失败 → 尝试补偿性反向调用（取消刚做的 Logto 停用/恢复）并同样明示错误，
  残差由 §6.3 Pass 3/4 在 15 分钟内对账纠正，管理员重做一次即收敛。绝不静默留半停用用户。
  **注意（round-15 订正）**：round-9 的拒绝规则只覆盖 delete / reset-password / unlock，
  从未覆盖停用——停用走双写是 round-3/4/5 的既定决议；先前实现把停用也拒绝属于
  超范围延伸，本轮已纠正为双写，不要再把停用当成拒绝项。

## 11. 迁移执行（顺序即要求）

1. 暂停 User.Created webhook 订阅（Console，默认流程；恢复在第 5 步后）。
2. 先导第一批：`python3 scripts/logto-migrate/migrate.py --admin-only --webhook-paused`，
   验收：admin 可用 Logto 登录且为 Admin，**且**能登录 Console 行使租户管理；测试账号免重置登录成功；
   停用账号导入后仍停用；迁移用户未被接收端误停。
3. 全量：`python3 scripts/logto-migrate/migrate.py --webhook-paused`，行数核对（导出数 = 导入成功数 +
   失败清单数；失败只记邮箱 + 原因，绝不记哈希）。
   **Argon2id 试点确认（round-8）**：`$argon2id$` 前缀默认映射 `passwordAlgorithm=Argon2id`，
   但官方文档只明确列出原生 Argon2 与 MD5/SHA1/SHA256/Bcrypt/Legacy——试点 dry run 必须先证明
   Logto 接受的值；若不是 `Argon2id`，用 `--argon2id-algorithm <实测值>` 调整，无需改代码。
   若 Logto 拒收某类哈希，该账号走管理员重置密码通道（脚本只记邮箱 + 原因）。
4. 导出物生命周期：导出清单（不含哈希）默认导入成功后删除；`--encrypt` 时用 openssl
   AES-256-CBC（`-salt -pbkdf2`，口令只从 `LOGTO_EXPORT_PASSPHRASE` 环境变量读）加密后
   再删明文；保留上限 = 导入验收通过时，绝不留到割接日之后；SSD 上“删除”不保证不可恢复，
   敏感导出请走加密 + 验收后立即删除。
5. 恢复 webhook 订阅；跑一次扫表（重启 Identity 或等 15 分钟），核对无新增 pending 误停。
6. **防重邮箱碰撞（round-5 决议，执行机制：Logto Security 板块的 blocklist）**：
   导入完成、行数对账通过之前，Console 里公开注册保持关闭（第一层）。
   导入完成后若仍有失败/未迁移的邮箱（stragglers），把这些邮箱逐条加进
   Console Security 板块的 blocklist（customBlocklist，支持精确邮箱、域名与通配模式，
   这里只用精确邮箱）再开注册——这是“灰度期阻止未迁移老邮箱在 Logto 注册”这条规则
   在 Logto 功能上的落点，规则因此可追溯到具体功能，而不是一句口头约定。
   该邮箱的用户看到的是 Logto 自身的注册受阻表现，并被引导等迁移完成或联系管理员。
   **sign-in 影响核查（必须做）**：公开文档把 blocklist 描述为注册期控制
   （"during sign-up"），是否同时影响登录未能从官方文档确认——所以不赌：
   每个 straggler 导入成功后，立即从 blocklist 里清除该邮箱条目，再实测该用户
   能正常登录。清除动作本身在两种可能下都安全：若 blocklist 只影响注册，清除只是
   卫生清理；若它也影响登录，清除就是恢复登录的必需步骤。**不建合并流程**：
   提前注册的账号因待审批零权限不可能已产生业务数据，恢复办法是管理员在 Console
   删除该提前注册的 Logto 账号后重跑该邮箱的导入（删除 + 重导，不是合并）。
   接收端另有碰撞审计兜底：`User.Created` 命中已存在老邮箱（未迁移）时记
   `logto.approval.collision` 一笔并保持其待审批。

## 12. 密钥存放与轮换

- 只放 compose `.env`（gitignored），绝不硬编码、绝不入库。
- 轮换：M2M secret 在 Console 重新生成 → 更新 `.env` → 重启 Identity；
  webhook 签名密钥在 Console webhook 设置里重新生成 → 更新 `.env` → 重启 Identity；
  轮换窗口由 Logto 投递有限次重试 + 接收端 503 语义 + 扫表兜底覆盖。
- 信任边界：`customData.approval` 只允许服务端写——Management API key 持有者（Identity 后端）
  与 Console 管理员；最终用户经任何公开接口（sign-in experience、用户自助 API）都写不到
  `approval`。验收（round-16，两项测试，不是口头保证）：
  (a) `LogtoAdminController` 的 approve/reset/unlock/import 全部带 `perm:admin.users.write`、
  get 带 `perm:admin.users.read`，类级带 `[Authorize]`；
  (b) webhook 接收端坏签名/缺签名一律 401 且门不执行（`LogtoWebhookControllerTests`）——
  唯一无鉴权的入口不接受调用方传来的 approval，只接受签名过的 Logto 事件。
  全仓 grep 结论：Api 目录下 `customData` 只出现在 `LogtoAdminController` 内。

## 13. 回滚与收敛

- 收敛与回滚见 `CONVERGENCE-CHECKLIST.md`、`ROLLBACK.md`；回滚演练见 `ROLLBACK-DRILL.md`
  （先种灰度期业务数据再回滚，否则断言是空转）。
- 回滚重导口径（round-8/13）：重导集 = 上线后在 Logto 创建的**全部**用户（灰度期 + 收敛后
  注册者，不只收敛前）；重导时按映射表 Guid 重建（绝不新 mint Guid，否则灰度期业务数据
  孤儿）；批准映射：Logto 侧 pending/suspended 的重导为旧侧 `IsActive=false`，只有
  approved 且未停用才导为启用（`AdminImportLogtoUserCommand.MapIsActive`，回归测试覆盖
  pending/suspended 全灭 + approved 对照）。
- 演练范围切分（round-15）：收敛前回滚演练 = 只把前端登录入口指回旧 Identity，Logto 数据
  不动（不重导）；“Logto-only 用户重导 + 旧签发开关翻回”只属于收敛后演练。
- 收敛核查清单逐服务断言旧 token 被拒绝，漏一个服务即不算收敛完成。
