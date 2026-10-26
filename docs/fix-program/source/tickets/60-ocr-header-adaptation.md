# 60: 本地 OCR 文本头部适配（跟进票，源自票 59 发现 1 + 定稿预留规则）

**来源：** 票 59 票面外发现 1；定稿 `replica/platform-ocr-spec.md` 与 AB-RESULT.md 已预留「Paddle 路线需要自备一层轻量规则解析（主题行=首行、发件人行=含日期时间的行等）」
**区域：** Playbook 入口前端 `web/src/app/features/playbook/redesign/intake/intake-panel.component.ts`（OCR 文本进 `cardFromText`/`parseEmail` 前的适配层）
**Blocked by:** 59（已落地 09997d8）
**Status:** ready-for-agent

## 问题

本地 OCR（Paddle）输出是带阅读序的散行，**没有 Key:value 头**：窗口标题行、账号行、发件人行（姓名与日期时间同行、可能粘连无空格，如 `Khalil Musazade2026-10-0809:18`）、收件人行、会议卡、正文依次排列。现行 `parsePastedEmail` 只认 Key:value 头 → 头部三件套全空 → `cardFromText` 判 failed → 按票 59 兜底设计回落视觉路径。结果：邮件截图这一主场景本地 OCR 实际不产卡，与定稿「本地为主」不符。

## What to build

在 intake 的 OCR 文本入口加一层**头部适配**：仅当文本来自本地 OCR 且 `parsePastedEmail` 扫不到任何头时，对原文做轻量规则定位并合成为标准头块，再走既有同一条管线（`parsePastedEmail` → `cardFromText`/`parseEmail`，抽取器与抽取域口径一律不变）：

- **主题行**：首个非空行作主题候选；含窗口标题前缀形态（如 `某人名 : 主题本体`，OCR 复核已知的客户端标题格式）时按规则剥离前缀（与 ocr-test RESULT.md 的已知残余口径一致：只剥「人名 + 空格冒号」型前缀，剥不动就原样保留）。
- **发件人行**：其后数行内找「含日期时间形态」的行（日期 `20\d{2}[-/年]\d{1,2}[-/月]\d{1,2}` 与时间 `\d{1,2}:\d{2}` 可粘连）——该行日期时间之前的部分作 From、日期时间作 Sent/Date；找不到则不合成（保持 failed→兜底的既有行为）。
- **合成方式**：把定位到的主题/发件人/日期重组为 `Subject: … / From: … / Sent: …` 头块 + 空行 + **发件人行之后的原文余部**作 body（会议卡必须落在 body 内——这是 extractInterviewWhen 的抽取域，防误抽发信时间）；原文本身（卡片 body 字段、来源标注）保持票 59 口径不变。
- 视觉路径与粘贴路径**不经**此适配（它们自带 Key:value 头，parsePastedEmail 命中即不触发）；适配只作用于本地 OCR 文本且无头的情况。

## Acceptance criteria

- [ ] 以票 59 服务对真实图的实际输出（evidence-59 的 sync-result 文本形状，含粘连发件人行）为样本：适配后 cardFromText 不再 failed，subject/from/date 落位、`when = 2026-10-13 10:00`（机械抽源码 harness 验证，参照 evidence-57/59 的做法）
- [ ] 对照组：视觉形态文本（带 Key:value 头）经同一入口结果与适配前逐字段一致（适配不触发或无副作用）；纯散行无日期时间行的文本仍 failed→兜底，不硬造头
- [ ] 来源标注仍为本地 OCR（适配不改变来源口径）；`npx tsc --noEmit` 通过

## 关闭纪律（全票通用）

- 现状复现 → 修复 → 复现通过 + 回归，证据存 evidence-60/
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features
