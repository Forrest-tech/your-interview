# 63: Mock「完整模拟」提交值与后端枚举不一致（跟进票，源自票 13 发现 1，功能 bug）

**来源：** 票 13（FUN-06）票面外发现 1
**区域：** Mock 前端 `web/src/app/features/mock/mock.component.ts` + Assessment 后端 `AssessmentController` 模式解析
**Blocked by:** 13（已落地 026e949）
**Status:** ready-for-agent

## 问题

前端 modeOptions 提交值 `FullMock`，后端 `SessionMode` 第 4 值是 `FullLoop`（AssessmentAggregates.cs:30-38）；`AssessmentController.cs:51` 用 `Enum.TryParse` 解析，失败即**静默回退 TopicDrill**——用户选「完整模拟」建的会话实际按主题专项出题，且无任何提示。mock.component.ts:71-74 注释自称「以实际枚举为准」但列的仍是 FullMock，同误。

## What to build

- 前端：提交值 `FullMock` → `FullLoop`，订正误导注释；票 13 已加的标签别名（FullMock 同键显示）保留无妨，但新提交不得再产生 FullMock。
- 后端护栏：模式解析失败不再静默回退——回 400 校验错误（字段级，指明 mode 不合法）；先核查全部调用方（前端各模式值、既有测试、其它客户端）确认无依赖静默回退者；若有存量 FullMock 会话数据，其读回显示已由票 13 别名覆盖，数据订正不在本票（如发现存量行数，在报告注明）。

## Acceptance criteria

- [ ] 活体/API：以 FullLoop 建会话 → 会话 mode 落库为 FullLoop 且按完整模拟出题路径（与 TopicDrill 可区分的证据）；以非法 mode 值建会话 → 400 而非静默 TopicDrill（修复前复现静默回退）
- [ ] 四个模式逐一建会话，mode 落库与所选一致
- [ ] tsc 通过；Assessment 相关测试（如有）全绿

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据存 evidence-63/
- 不许为过测改断言口径；在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
