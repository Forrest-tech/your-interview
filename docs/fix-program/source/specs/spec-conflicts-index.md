# 现状规格 · 冲突汇总索引（票 33）

> 现状规格（2026-10-22 按代码实查落档）配套汇总。基线 `origin/develop-new-features` tip `f99cfdd`。
> 纪律：本索引**不新造 AUDIT 编号**——每条冲突只索引回 AUDIT 既有编号（REQ/COD/FUN/ARC/OBS）或既有票号；CONF-* 前缀仅是本索引内的定位标签，不是审计编号。已修复项单列于表后，不计入冲突条数。

## 冲突清单（现行未对齐：21 个标签，按同根归并 14 条）

| 本索引标签 | 模块/规格文件 | 一句话 | 索引回 |
|---|---|---|---|
| CONF-DASH-1 | Dashboard · dashboard-spec.md | interview 计数（Jobs 状态）与 Upcoming（Interviews 排期）同屏双口径，排期未出结果不推进状态 | REQ-02（票 04 部分修复后残留） |
| CONF-DASH-2 | Dashboard · dashboard-spec.md | 弱项区硬编码中文残留 | FUN-07（票 35） |
| CONF-TS-1 | Tech Stack · tech-stack-spec.md | 删除两处原生 confirm() | REQ-14（票 45） |
| CONF-TS-2 | Tech Stack · tech-stack-spec.md | 口语练习按钮/热力图硬编码中文 | FUN-07（票 35） |
| CONF-AP-1 | AI Practice · ai-practice-spec.md | Tech Stack 题目转入 Practice 成副本、不回同步，与同源规则有张力 | REQ-16（ARC-07 同型，无独立编号） |
| CONF-AP-2 | AI Practice · ai-practice-spec.md | 3645 行单类巨型组件 | ARC-02（票 53） |
| CONF-MOCK-1 | AI Mock · ai-mock-spec.md | next-question 投机性调用，后端无此端点 | COD-21（票 48/72） |
| CONF-AN-1 | Analytics · analytics-spec.md | 前端恒调 ability/trend，后端无此路由，趋势区块恒降级 | FUN-08 + COD-21（票 36） |
| CONF-AN-2 | Analytics · analytics-spec.md | 快照投影与真源可分叉，无对账/重建入口 | REQ-18 + OBS-4（票 34） |
| CONF-AN-3 | Analytics · analytics-spec.md | MasteryBreakdown 当日更新以原值回写、计数不迁移（CONF-AN-2 具体成因） | REQ-18（子项，不新造编号） |
| CONF-RS-1 | Resume · resume-spec.md | 图片型/扫描简历无 OCR 入口，与 platform-ocr-spec 消费方预期不一致 | platform-ocr-spec / 票 59 范围注记 |
| CONF-RS-2 | Resume · resume-spec.md | 已存文本简历不可见/不可编辑、无粘贴入口、空存 400 话术 | FUN-10（票 37） |
| CONF-RS-3 | Resume · resume-spec.md | 前端 localStorage 优先读，旧草稿可盖后端较新正文 | ARC-04（残留面） |
| CONF-ADM-1 | Admin · admin-ai-settings-spec.md | Admin 页三处原生 confirm()（REQ-14 同型扩散面） | REQ-14（票 45 范围外） |
| CONF-TR-1 | Tracker · tracker-spec.md | 轮次双主（Jobs 投影 vs Interviews 主数据）+ 排期不回写状态 | ARC-07 + REQ-02 残留 |
| CONF-TR-2 | Tracker · tracker-spec.md | 抽屉 Overview 无独立 Work mode 展示字段 | REQ-15（票 32） |
| CONF-TR-3 | Tracker · tracker-spec.md | 原生 confirm 四处 + prompt 一处残留 | REQ-14（票 45） |
| CONF-CAL-1 | 日历 · calendar-spec.md | 60 天数据窗口与任意翻月不对齐、窗口外恒空无提示；upcoming 窗口三处不一致 | REQ-17（规格缺失衍生） |
| CONF-CAL-2 | 日历 · calendar-spec.md | 定稿提醒（提前 1 天/1 小时）无后端实现 | Playbook 定稿 §二 / 定稿审计 U3 |
| CONF-CAL-3 | 日历 · calendar-spec.md | 后端 .ics 导出端点仍在，与定稿取消裁决不一致 | Playbook 定稿 2026-10-21 裁决 |
| CONF-CAL-4 | 日历 · calendar-spec.md | 星期头硬编码中文 | FUN-07（票 35） |

注：上表 21 个标签按同根归并为 **14 条**：①REQ-02/ARC-07 轮次同源残留（DASH-1+TR-1）②FUN-07 硬编码中文（DASH-2+TS-2+CAL-4）③REQ-14 原生对话框（TS-1+ADM-1+TR-3）④ARC-02 巨型组件（AP-2）⑤同源张力·转入副本（AP-1）⑥COD-21/FUN-08 缺失端点与投机调用（MOCK-1+AN-1）⑦REQ-18 投影分叉（AN-2+AN-3）⑧RS-1 OCR 未接 ⑨FUN-10 文本维护缺失（RS-2）⑩ARC-04 残留读侧（RS-3）⑪REQ-15 Work mode（TR-2）⑫CAL-1 窗口口径 ⑬CAL-2 提醒未落地 ⑭CAL-3 .ics 残留。另 TR-4（REQ-11 bulk-bar）为待 UI 核销的观察项，不计入 14 条。

## 观察项（未判定为冲突，规格内留痕）
- CONF-TS-3：分类双轨表示（Topic 字符串为真源、TechStackCategory 只存空分类占位）——口径已在规格内定义，防误用。
- CONF-ADM-2：AI 设置页跨三服务拼装、无整页级错误汇总——排障口径留痕。
- CONF-MOCK-2 / AUDIT OBS-1：Mock 出题依赖 LLM/worker，无模型时空题面——环境项，未判定产品缺陷。
- CONF-TR-4 / REQ-11：bulk-bar 纵向占用需 UI 实测，归票 31。

## 已对齐（审计条目 → 已修复，不计冲突，规格内留痕备查）
- REQ-01 迁移守卫 → 票 02；COD-01 用户隔离 → 票 03；REQ-02/ARC-07 结果回写结构化 → 票 04/05/58（残留见 CONF-DASH-1/TR-1）；COD-10 localStorage 孤岛 → 票 14/15/16；ARC-04 简历副本删除 → 票 17；REQ-05 文本简历回落 → 票 26；REQ-08 弱项守卫 → 票 27；COD-15 写端点权限 → 票 25/86；FUN-05/FUN-06/REQ-09/REQ-10 口径 → 票 12/13/29/30/61/63。
