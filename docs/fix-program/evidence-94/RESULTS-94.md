# 票 94 验收实测记录（RESULTS-94，草稿——验收进行中）

- 基线：远端 develop-new-features tip `a217552a`（新鲜 ls-remote 核实），工作区 `~/workspace/wt-t94`。
- 网关：worktree 源码 `dotnet run`，口 5100（不用 5200，5200 被票 123 遗留 mini-proxy 占用）。
- 业务服务：共享 5262/5125/5264/5265/5266 原样不动；analytics 5267、aigateway 5268 由本轮自起（worktree 源码）。
- OCR 5300：全程未起（部分栈常态；本票正是要证明 OCR 未起不拖垮网关判定）。

## 中途发现（验收受阻的真实原因，非本票配置问题）

- **F-A（沙盒拦截 .NET 出站 HTTP）**：nohup detached 的网关进程内，YARP 探活与 AddUrlGroup
  探活均被沙盒拦截，异常为 `Received an invalid status line: 'muse: Other TCP
  connections is turned off … Direct network protocols … other_tcp …'`。curl（exec 工具内）
  探同一地址 200，说明不是服务问题；unset 全部 proxy 环境变量后仍被拦截，说明不是
  env-proxy 问题。待验证的解法：网关改由 exec 会话后台运行（不 nohup 脱离），使其保留
  exec 工具的网络 profile。
- **F-B（jobs 目的端口漂移，待主线另行定夺）**：网关 appsettings 里 jobs 目的地址仍是
  旧 526x 方案的 `127.0.0.1:5263`，而沙盒 jobs 实际跑在 5125（Jobs launchSettings http
  profile 与 sandbox-start.sh 一致），5263 上无任何监听。本轮验收中我的网关实例用环境
  变量 `ReverseProxy__Clusters__jobs__Destinations__d1__Address=http://127.0.0.1:5125/`
  临时对齐（不改仓内 5263），该漂移本身不在本票三处改动内，需主线另立项定夺（它同时
  影响 dotnet-run 模式下 /api/jobs/* 的转发，不只健康探活）。
- **F-C（启动慢）**：网关 `dotnet run` 从拉起到监听约需 1–3 分钟，验收探针必须等监听出现后再判，
  不可按秒级超时判失败。
- **环境事件**：验收中途 VM 换代，全部进程被杀（自起与共享服务皆 down）；worktree 改动与证据
  文件完好，待 watchdog 恢复共享服务后重起自有三件套继续。
- **F-D（RabbitMQ 缺失是 S1 的硬前置）**：VM 换代后 rabbitmq-server 未安装（apt 锁竞争激烈，
  平台 apt 与 watchdog 安装多次互撞），5672 长期关闭。后果链已实证：Jobs 5125 因总线连不上
  而 /health/live 恒 503；自起 analytics 5267 的健康检查含 masstransit-bus 项（MassTransit
  自动注册），总线 Not ready → 503。没有 RabbitMQ，7 集群探活不可能全绿——S1 的前置就是
  RabbitMQ 本身。2026-10-24 16:0x 平台 apt 装好 rabbitmq-server 3.12.1 后，本轮按既定做法
  chown + `rabbitmq-server -detached` 起服，日志确认 `started TCP listener on [::]:5672`、
  `Server startup complete`。注意：aigateway 5268 在总线 down 时仍报 200（其健康项未含总线），
  与 analytics 行为不同，属各服务健康注册差异，非异常。
- **F-E（VM 过载致 loopback 响应极慢）**：16:07 实测 load average 49.98（2 核 VM），全部
  loopback 健康探针 30s 超时（此前同探针 0.7–26s 不等）。验收探针必须放宽超时并在负载回落
  窗口判读，不可把过载超时误判为服务不健康。

## S1 全起（7 业务服务 + 网关，不起 OCR）—— 前置条件达成，聚合活体被沙盒策略阻断

- 7 个服务个体健康（curl 实测，负载回落窗口，全部 200）：
  identity 5262（共享）200、jobs 5125（共享）200、interviews 26164（自起）200、
  knowledge 26165（自起）200、assessment 26166（自起）200、analytics 5267（自起）200、
  aigateway 5268（自起）200；OCR 5300 连接失败（未起，符合本票设定）。
- 探活清单复现验证（`evidence-94/verify-enabledclusters.py`，模拟 Program.cs:43-83
  解析逻辑 + 环境变量覆盖层级，输出见下）：**恰好 7 个业务集群，ocr 被过滤排除，
  无 5300**。断言全部通过。
- 网关 /health/live 实测：**Unhealthy / 503**——但这不是修复失败，而是沙盒策略阻断：
  网关进程内一切 .NET 出站 HTTP 均被沙盒拦截，异常为 `Received an invalid status
  line: 'muse: Other TCP connections is turned off … switch other_tcp from Deny to
  Ask.'`。已排除的假说：nohup 脱离（exec 会话同样被拦）、dotnet run 包装（dll 直跑
  同样被拦）、env-proxy（全部 proxy 变量 unset 后同样被拦）；而 exec 内 curl 直连同
  地址 200（0.013s），证明服务侧无问题。结论：**本沙盒会话内无法完成网关聚合活体**，
  与修复本身无关。解堵三选一（需主线/用户定）：①用户把 Muse 设置 → Permissions →
  Direct network protocols 的 other_tcp 从 Deny 改为 Ask 后重跑聚合；②主线会话下
  试跑一次（本结论来自子 agent 会话，未证实主线会话是否同策）；③Mac/CI 环境验证聚合。
- 注：共享 5264/5265/5266 在验收窗口内始终未被 watchdog 恢复，故 interviews/
  knowledge/assessment 用自起实例（26164/26165/26166）+ 网关环境变量覆盖目的地址；
  该覆盖仅作用于本轮验收实例，不改仓内配置。

## S2 反向（停一个业务服务 → 必须 503）—— 未验证（非失败）

- 端点在全部服务健康时已因拦截而 503，此时停服务同样 503，无法区分“探活有效”
  与“探活被拦”，故 S2 **不宣称通过**；待聚合解堵后，停自起 analytics 一项重跑即
  可补验（反向机制与 S1 同源：AddUrlGroup 探活失败即 Unhealthy）。

## S3 OCR 未起独立性 —— 机制证据成立，活体同 S1 被阻断

- 机制证据：探活清单复现输出中 ocr 被过滤排除、无 5300——OCR 未起不可能拖垮网关
  判定（判定集合里根本没有它）。活体 200 同 S1 一并被拦截阻断，待解堵后与 S1 同跑。

## E2E 回切核查

- `web/e2e/health.setup.ts`：单文件 tsc（typescript 5.5.4 + process 存根）**CLEAN**；
  改动为 globalSetup 首步先等 `${API}/health/live` 200，再探登录、再探前端（登录
  检查保留作第二道 guard）。完整 globalSetup 实跑需前端 4200（FRONTEND HOLD 禁启）
  与通过聚合的网关，故留待 CI/Mac；本轮不宣称 E2E 全套通过。
- 另：`web/e2e/t81-ocr-entry-to-card.spec.ts:10` 注释仍有“OCR 服务在本环境根本没有
  落地”的旧前提，与本票更正相悖；本轮按任务范围（仅一处 E2E 回切）未动它，提请
  主线顺手改一句或另立小票。

## 清理

- 自起网关（5100 exec 会话）、自起 interviews/knowledge/assessment（26164/26165/
  26166）、自起 analytics/aigateway（5267/5268）验收后全部停止，端口归还；
  共享服务（5262/5125）与 RabbitMQ（5672，本轮恢复）保持运行，不动。
- 共享库影响：analytics/aigateway 自起时按服务自身设计跑了启动迁移（仅各自 schema
  建表：analytics.__ef_migrations_history 两行、aigateway 同理，无种子数据写入），
  属服务正常首跑行为；如主线希望清零，DROP SCHEMA analytics/aigateway 即可。
