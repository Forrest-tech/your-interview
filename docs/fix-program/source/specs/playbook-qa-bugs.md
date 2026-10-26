# Playbook QA — Bug List (2026-10-15, 本地隧道实测)

## 测试结论总览
8 项需求逐项实测（只读）：R01/R02 核心链路存在；R03/R07 多轮 workspace 依赖"Create real round"；R04 导出按钮存在但需先生成指导材料；
R05 待验证导入对话框；R06 无分析数据（占位）；R08 全局日历缺失（已知）。

## 确认的 bug（修）
| ID | 问题 | 位置 | 状态 |
|---|---|---|---|
| B3 | Corpay 条目 Q&A 里混入 4 条 TEST-* 占位题（TEST-ALPHA binary search tree / TEST-BETA REST idempotency / TEST-GAMMA event delegation / TEST-DELTA database index） | 本地 DB 数据 | 清理中 |
| B6 | "TechStack"命名不一致 → 已统一为 "Tech Stack"（三语） | i18n.service.ts | ✅ 已修（c236f2b） |
| P1 | **粘贴文本 → 跑分析链路不通**（R01 核心流程一半是坏的）→ ✅ 已修（e7e8d0c）：consumer 无音频时走文本路径（事件自带文本优先，其次调新接口 GET assets/{id}/transcript）；文本切分+150wpm合成时间戳播种 segments；Measure 文本模式（跳过发音）；发音维度 N/A（Overall 改为非空维度平均——注意这是行为变更）；新增读转写稿端点。54/54 测试通过 | Consumer/Pipeline/Interviews API | ✅ 已修，待合 main |
| B7 | Q&A 删除按钮在自动化会话中点击无反应——代码用原生 confirm()，极可能是自动化环境自动 dismiss 了确认框（=点了取消）。浏览器无 JS 执行能力，无法验证。需用户在自己浏览器手动点一次确认 | playbook-detail.component.ts removeQuestion() | 待用户手动验证 |

## 经核实不是 bug
| ID | 结论 |
|---|---|
| B5 | Pending=0 正确：Pending 语义 = Transcribed/AssetsUploaded/Transcribing（待分析），Draft 另计。两条都是 Draft → Pending 为 0 符合定义 |
| R04导出 | 导出 PDF/Word 按钮存在，`@if (guidance())` 控制显示——无指导材料时不显示是合理 UX |
| R02文案 | "Prep"/"Mock Interview" 英文文案——当前 locale 就是英文，非 bug |
| B2 | Mohawk 条目无日期——用户数据缺失，非代码 bug |

## 待验证
| ID | 内容 |
|---|---|
| R05-dialog | ✅ 已验证通过：导入为 inline 选择模式（非弹窗），7 题复选框默认全选，每题可编辑问题/分类/答案，有全选/全不选，Cancel 未导入——符合 R05 需求，非 bug |

## 低优先级（记录，不修）
- B1: 登录页 "I'm not a robot" 复选框鼠标点不动，空格键可勾选——**两个独立会话复现**，疑似真 bug（widget 可能只响应键盘事件）。用户如也遇到，值得修
- B4: "Rounds (1)" 把 synthetic 占位轮也计数，可能误导
- R08: 全局日历视图缺失（需求明确要求，排期做）
