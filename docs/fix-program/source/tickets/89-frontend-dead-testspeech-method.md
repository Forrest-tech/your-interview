# 票 89 — 前端 PracticeApi.testSpeech() 死代码（源：票 86 票面外发现 F-86-1）

## 事实（evidence-86 关闭报告）
- 前端 `PracticeApi.testSpeech()`（调 POST speech/test）全仓零组件调用方；设置页实际走 `testSpeechCredential()` → test-credential（MockManage）。
- 票 86 已将后端 speech/test 改挂 MockManage；该前端方法保留即无害但属死代码，易误导后续接线。

## 任务（二选一，实施时读码定，报告论证）
- 删该方法（若确认无任何动态/模板引用）；或
- 保留并在方法注释标注「未接线，接线时 403 呈现走 ai-setting 既有 flash 路径」。

## 验收
- 全仓 grep 零残留引用（删除路线）+ `npx tsc --noEmit` 过；设置页语音凭据测试活体不回退。
- 区域：Assessment 前端 practice api 文件；与票 48/36 无冲突面。
