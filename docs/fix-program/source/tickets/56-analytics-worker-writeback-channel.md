# 56: Analytics 回写可信通道（COD-02 的配套收尾）

**来源：** 票 06（COD-02）实施时发现并上报、协调者裁决另票处理（2026-10-22）
**严重度：** S3（内部链路降级，非数据泄漏）
**区域：** Services.Analytics + Analysis.Worker
**Blocked by:** 06（已落地 8db7904）
**Status:** in-progress — 协调者裁决（2026-10-22）：**采解法 A**。实现者盘点证实：无服务账号角色/播种、票 03 先例实为实体归属校验不可搬运、Identity seeder 会同步抹掉手工加授。解法 B 以 analytics.write 为门会让 PowerUser（普通人类角色）也被 honoured，等于给一类真实用户重开票 06 刚堵的口子，不取。解法 A 实施口径：SharedContracts 新增 `analytics.writeback` 权限常量并注册进 Permissions.All；新增系统角色 `Service` 且纳入 DefaultPermissions 同步集（只给 Worker 回写最小权限集）；Identity 播种 Service 角色（顺带消除 .env.example 声称播种却无播种的不符，播种仅建角色、不建账号，账号仍由部署侧创建并授 Service 角色）；Analytics 以 perm claim `analytics.writeback` 判定 honoured、判定纯逻辑落 Analytics 层带 §7 单测；Worker 改传 `?ownerUserId=`。范围扩展（SharedContracts + Identity 播种/角色）经本裁决批准。证据：evidence-56/00-inventory-and-conflict-stop.md。

## 背景

票 06 把 `POST /api/analytics/ability` 的归属改为一律当前登录用户（Me），堵住可写型 IDOR。但 `Analysis.Worker/Consumers/InterviewAnalysisConsumer.cs` 的 `InterviewAnalysisCompletedConsumer`（约 :610）以**服务账号**身份调用该接口、在请求体传事件用户的 userId 做「分析完成 → 雷达图即时刷新」的回写——修复后这批快照改落服务账号名下，事件用户的雷达不再被这条同步通道刷新（事件消费者 EventConsumers.cs 的异步读模型更新不受影响，用户数据无泄漏、无错乱，仅同步刷新通道降级）。

## What to build

照票 03 裁决 B 的同一模式给这条回写开可信通道，不给普通用户留任何口子：

- Analytics 端：ability 端点接受显式 `ownerUserId`（query 或 body 均可，与 Worker 现有构造方式对齐选一），**仅当调用方是服务账号身份**（以角色/专用权限判定，如新增 `analytics.writeback` 权限只授予服务账号角色；在票内先查清服务账号的角色与权限现状再定）时才 honoured：以 ownerUserId 为归属写入；普通用户带 ownerUserId 一律忽略（仍落 Me 名下）。
- Worker 端：回写请求改走该可信参数（把现在 body 里的 userId 挪过去），其余构造不变。
- 禁止的形态：恢复 `body.UserId ?? Me`、按「请求体有 UserId 就信」的任何变体、给服务账号全量数据豁免。

## Acceptance criteria

- [ ] 服务账号带 ownerUserId=事件用户回写 → 快照落事件用户名下（DB/GET radar 双证），雷达同步刷新链路恢复
- [ ] 普通用户带 ownerUserId=他人 → 仍落自己名下（复测票 06 的攻击用例不回退）
- [ ] 服务账号不带 ownerUserId → 落服务账号自己名下（或明确拒绝，二选一在票内注明）
- [ ] Analysis.Worker 与 Analytics 构建 0 错误；票 06 的实测用例（自写/攻击）回归通过


## 附则：单元测试门槛（2026-10-21 用户立规，总纲 skill 第七节，即刻生效）

- 本票新写/改动的纯函数与规则逻辑必须带单测，含 boundary 类别（空/null、空集合、超长上限±1、特殊字符/Unicode、0 与负数、日期时区临界、文件类 0 字节/损坏/超限——按适用者取），命名/注释标注类别，缺类在报告说明理由；缺测协调者不予验收。
- 改到无覆盖的旧纯逻辑：先补特征测试锁现状再改行为。flaky 按缺陷立票；陈旧测试按票 55 先例（改测试+注明依据，不默默删）。细则以 skill 第七节为准。


## 附则：变更安全协议（2026-10-21 用户立规，总纲 skill 第八节，即刻生效）

- 动手前在本票补两张清单（写进票面，交付报告复述并打勾）：**影响面**——本票将改动的共享组件/函数/数据还被谁用、哪些流程经过；**不变项**——本票明确不动的东西，逐条写出。
- 验收范围 = 本票功能 + 影响面全部邻居：邻居逐项按总纲第六节验证剧本回归、不变项逐条打勾才许关票。只验新功能 = 未验收，协调者不予关票。


## 附则：偏差零容忍 + 对照表交付（总纲 skill 第八节第 5/6 关，即刻生效）

- **偏差零容忍**：票面/定稿/审计条目已定口径的，任何偏离（布局、字段、文案、顺序、删减、自创优化）未经批准禁止；撞上真实冲突停工上报协调者（写明冲突点 + 两个可选解法），禁止现场即兴改口径。事后发现的未批偏差按缺陷立票返工。
- **对照表交付**：UI 类交付报告必须附对照表——设计依据（定稿/审计条目期望行为/改前形态）与成果并排逐项打勾（布局结构/字段与文案/顺序/状态与空态/间距档位），偏差清单单列（为空才算按稿交付；非空每条注明原因与批准出处）。修复类票以审计条目期望行为 + 改前形态为设计依据，比例适用。无对照表的 UI 票协调者不予验收。
