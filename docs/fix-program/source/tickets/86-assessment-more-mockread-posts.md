# 票 86 — Assessment 另两 POST 端点同挂只读权限（源：票 25 盘点票面外发现 #14/#34）

## 事实（evidence-25/00-inventory-and-plan.md §2 全端点盘点）
- #14 `POST pronunciation/assess`：挂 MockRead，消费 Azure 付费发音评估，
  Viewer（只读角色）可调用——与 COD-15 同根（付费/写操作挂只读权限），
  票 25 按不扩围纪律未动。
- #34 `POST speech/test`：挂 MockRead，真连 Azure 做连通测试（消耗极
  小、无落库），Viewer 可调用。

## 任务
- 两端点改挂口径：按票 25 已定先例（练习参与类写/消费 → MockAnswer；
  管理类 → MockManage）逐个定责——#14 属练习参与流 → MockAnswer；
  #34 为语音连通测试，实施时按其调用方（设置页/练习页）读码定
  MockAnswer 或 MockManage 并在报告论证。
- 前端调用方对 403 的呈现抽验（练习页发音评估、语音测试按钮），
  无提示则记票面外发现报协调者。

## 验收
- 活体：Viewer 两端点改前可达（非 403）→ 改后 403；User 不回退；
  读端点不变（方法同票 25 活体口径：以授权层状态码区分判定）。
- §7：端点权限反射断言单测（与票 25 同一测试类扩展或同形态新增）；
  Assessment.Tests 零回退。
- 区域串行：AssessmentController 与票 25 同文件，排票 25 推送后。
