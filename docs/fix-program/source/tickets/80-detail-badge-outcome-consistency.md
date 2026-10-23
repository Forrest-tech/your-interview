# 票 80 — 详情页头部徽章与列表分组口径打架（用户实测新缺陷，2026-10-22 登记；原编号 76 撞号改编）

## 症状（用户实测截图）
Playbook 详情页 Randstad 条目——头部徽章显示 "Completed"，左侧导航却在
"In Progress" 组（Completed 组 0），同屏口径打架。

## 根因（主代理定位，实施前须读码复核）
- 分组走 outcome 单一事实源（entryResultOutcome(e.outcome)，票 05 已定
  口径）：无终局结果 → In Progress，符合定稿"宁漏不错"，**无误**。
- 错在 web/src/app/features/playbook/playbook-detail.component.ts 的
  statusText（约 216–223 行）：e.result 为空且无 Pending 轮次时兜底
  return statusDone("Completed")，无 result 条目被默认贴 Completed。

## 任务
- statusText 与分组共用同一事实源：文案由 e.outcome 经
  entryResultOutcome 映射决定；Pending/无 outcome 显示进行中口径。
- 删除默认 Completed 兜底。
- e.status==='Analyzed' 分支与 outcome 冲突时以 outcome 为准
  （或在票面关闭记录中说明保留理由并经协调者批准）。

## 验收
- Randstad 同形态条目（无轮次、无 Result）重跑详情页：徽章与分组一致。
- 有终局结果条目：徽章与分组仍一致（回归）。
- 本票场景登记为真人化验收**永久回归任务**（验收阶段必含）。
- §7：若判读逻辑抽为纯函数/helper，带单测（含 boundary：outcome
  各枚举值、null/空、未知字符串）。
- §8 影响面：playbook-detail 头部徽章全部消费方；不变项：分组逻辑
  （entryResultOutcome 本体）与列表页行为不动。
- 区域串行：本票与票 21 同改 playbook-detail.component.ts，须在票 21
  提交推送后再开工（基于其后 tip 建 worktree）。
