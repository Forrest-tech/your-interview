# 交接笔记：Muse → Claude（2026-11-11）

> 来源：Claude 在 Muse 的 Your-interview 线程里的直接对接（2026-11-11）。本文件是摘录，不是原件。
> 原件在 Muse 沙箱 `~/workspace/wt-handover-muse-local`（提交 `54a97330`，分支 `handover/muse-local-2026-11-11`，**推送因凭据过期失败，未入远端**）。
> 不含任何密钥、令牌或个人身份信息。

## 1. 治理状态

- 基底：`develop-new-features` = `9db23487`（票 313）；回滚锚点分支 `handover/baseline-2026-11-11`。
- Muse 的票号 296–313 与 `FIX-STATUS.md` 编号一一对应（296、300、309、312、313 等均有条目）；**311 无台账条目**（规格自述实装时才写）。新票自 314 起。
- 协议已改为 v1.1：执行者为 Claude。Muse 的两个定时任务（`workbuddy-fix-program-watch`、`sandbox-env-watchdog`）已按 Forrest 确认取消。
- Muse 自 2026-11-11 起不再向 `develop-new-features` 推送，无待推内容。

## 2. 只存在于 Muse 沙箱、未入库的东西

| 项目 | 说明 | 处理 |
|---|---|---|
| `sandbox-start.sh` 本地版 | 含 RabbitMQ 预建目录与 FRONTEND HOLD（option C，2026-10-28，脚本永不在 4200 起前端）。入库版仍会起 `ng serve` | 属于 Muse 沙箱的运维脚本，不影响应用；需要时再向 Muse 索取 |
| 票 311 规格 `SPEC.md` | 见第 4 节摘要 | 实装时索取原文 |
| `wb-verify/` 脚本与日志 | 2026-10-22/23 核查期工具 | 留在 Muse 沙箱 |
| `aurora-ui-preview` 分支（3bf44146） | 视觉预览，34 个 `.scss`，用户未点头 | 留在 Muse 沙箱（已做成 patch） |
| 旧 worktree 遗留 | wt-41、wt-82、wt-t124、wt-t127-review、wt-t128-review、wt-t141-recon、wt-t165-recon、wt-graphify-poc、wt-t234-impl | Muse 未逐一溯源，不确定其价值 |

## 3. 邮箱模块：已知事实

规则卡（Corpay 条目，来自用户截图，Muse 转写）：

- Company domains：`fleetcor.eu`
- Known contact emails：Khalil 的公司邮箱
- Company name keywords：`corpay`
- Review queue 当时共 10 条，均为招聘平台通知类弱信号（LinkedIn Job Alerts、Indeed 等，日期为 8 月），不是 Khalil 的邮件。

匹配语义（Muse 逐行读代码，Claude 核对了结果结构）：

- 强信号（发件或收件域命中、唯一条目；或已确认的往来人）→ 自动导入 Materials。
- 弱信号或多条目歧义 → 只进待确认队列，**不计入条目 Latest**。
- 无信号 → 静默跳过，**任何地方都看不到**。`MailboxRefreshJobSnapshot` 只有 Added、Pending、SkippedDuplicates、Failed，没有“无信号跳过”的计数（Claude 已在源码核实）。

推论与未知：

- Khalil 来自 `fleetcor.eu`，按规则应是强信号，不应卡在队列里；10-07、10-08 的邀请仍缺失，**原因未定**。
- 票 312（取信失败单封隔离）和 313（发件人邮箱行）已落地、CI 绿，但用户 2026-11-02 之后从未确认 Mac 侧结果。
- 2026-11-01 晚的“大错误”未定位；任务状态仅存于内存。
- 已排除：刷新窗口、HTML 渲染通病、显示过滤（306 已修）、303 误报、密钥丢失。

## 4. 票 311（跨轮次邮件时间线）规格摘要

- 需求：以时间线串起条目的全部邮件。**已定结构：一个节点 = 一个轮次，节点内放该轮邮件**；展开看详情，复用现有邮件卡片，不另写第二套渲染。
- 约束：仅邮件；不动导入、匹配、轮次指派、分类；不改库（归属为展示时计算）。
- 轮次归属优先级：1）主题里的轮次标记（如 “2nd round”）；2）已存储的 `roundId`；3）日期贴近度；4）未指派节点（仅在有无法归属的邮件时出现）。
- 所有轮次都有节点，包括零邮件轮次；节点内按日期正序，同日保持原序。
- 已知局限：邮件日期只有日粒度；跨轮长 thread 会被拆到不同节点。
- 另有 4 个开放决策，规格里各带推荐，Forrest 尚未点头。

## 5. Forrest 侧未验收事项（来自 Muse）

292 录前校准（真机人声仍小）、254 真机试听清单、278/279/271/282、Logto 的 restart 与密钥/密码轮换与 SMTP 验证、257/256/310 残留、`markHtmlMissing` 只加不清除的小缺陷、Save rules 的“已保存”指示、develop→main 合并（只由 Forrest 点头）。
