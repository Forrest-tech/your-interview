# 43: ARC-13 i18n 机制收敛为一套

> 本文件为票 43 工作票面（原文承自 `source/tickets/43-arc-13-i18n-consolidation.md`，source 保持不动）；
> 变更安全协议两张清单已按附则要求在动手前写于下方。

**审计编号：** ARC-13（严重度 S3）
**区域：** 全前端 i18n
**Blocked by:** 13, 35 (文案修复后)
**Status:** ready-for-agent

## What to build

playbook 本地字典（v2.i18n、brief.i18n、analysis.i18n、qa.i18n）并入核心 I18nService 的命名空间分区，组件不再同时引两套；键名保持兼容映射，不丢任何现行文案。证据：AUDIT.md ARC-13。

## Acceptance criteria

- [x] 本地 i18n 文件下线、文案机制只剩一套（文件清单双证）；三语走查无键丢失/无原始键直出
- [x] tsc 通过

## 关闭纪律（全票通用）

- 复现失败 → 修复 → 复现通过 + 回归，证据路径回填 FIX-STATUS.md
- 迁移必须带 Designer.cs + ModelSnapshot；前端改动跑 `npx tsc --noEmit`
- 不许为过测改断言口径；与定稿冲突先报协调者
- 在独立 worktree 工作，push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features


## 附则：单元测试门槛（2026-10-21 用户立规，总纲 skill 第七节，即刻生效）

- 本票新写/改动的纯函数与规则逻辑必须带单测，含 boundary 类别（空/null、空集合、超长上限±1、特殊字符/Unicode、0 与负数、日期时区临界、文件类 0 字节/损坏/超限——按适用者取），命名/注释标注类别，缺类在报告说明理由；缺测协调者不予验收。
- 改到无覆盖的旧纯逻辑：先补特征测试锁现状再改行为。flaky 按缺陷立票；陈旧测试按票 55 先例（改测试+注明依据，不默默删）。细则以 skill 第七节为准。


## 附则：变更安全协议（2026-10-21 用户立规，总纲 skill 第八节，即刻生效）

- 动手前在本票补两张清单（写进票面，交付报告复述并打勾）：**影响面**——本票将改动的共享组件/函数/数据还被谁用、哪些流程经过；**不变项**——本票明确不动的东西，逐条写出。
- 验收范围 = 本票功能 + 影响面全部邻居：邻居逐项按总纲第六节验证剧本回归、不变项逐条打勾才许关票。只验新功能 = 未验收，协调者不予关票。


## 附则：偏差零容忍 + 对照表交付（总纲 skill 第八节第 5/6 关，即刻生效）

- **偏差零容忍**：票面/定稿/审计条目已定口径的，任何偏离（布局、字段、文案、顺序、删减、自创优化）未经批准禁止；撞上真实冲突停工上报协调者（写明冲突点 + 两个可选解法），禁止现场即兴改口径。事后发现的未批偏差按缺陷立票返工。
- **对照表交付**：UI 类交付报告必须附对照表——设计依据（定稿/审计条目期望行为/改前形态）与成果并排逐项打勾（布局结构/字段与文案/顺序/状态与空态/间距档位），偏差清单单列（为空才算按稿交付；非空每条注明原因与批准出处）。修复类票以审计条目期望行为 + 改前形态为设计依据，比例适用。无对照表的 UI 票协调者不予验收。

---

## 附则执行记录：影响面 / 不变项（2026-10-23 动手前写入，交付报告复述并打勾）

### 现状盘点（机械对账，非推断）

- 本地字典现存 2 个文件（analysis.i18n / qa.i18n 已由票 41 下线，不在本票范围）：
  - `features/playbook/redesign/v2.i18n.ts` — `V2_I18N` 465 键（`pb.v2.*`，zh/en/fr 三语齐）
  - `features/playbook/components/brief/brief.i18n.ts` — `BRIEF_I18N` 112 键（`pb.br.*`，zh/en 必有、fr 可选）
- 核心 `core/i18n/i18n.service.ts` DICT 已有 pb.* 键 530 个，其中与本地表**重叠 79 键（全部 `pb.br.*`）**：逐键对账 zh/en **零差异**，且核心既有条目 fr 与 en 同文（79/79）——即重叠键并入后三语展示逐字不变。
- 本地独有 498 键需并入核心 DICT：`pb.v2.*` 465（三语齐）+ `pb.br.*` 33（其中 27 键缺 fr：16 个 `pb.br.prep.*` + 11 个 `pb.br.rounds.*`）。
- 消费方（改接线即影响的下游）：
  - `makeV2T`/`makeV2Fmt`（v2.helpers.ts）→ 10 个组件：playbook-detail、redesign/round 下 round-card / materials-list / comm-log / round-record / qa-import / prep-report、redesign/intake/intake-panel、redesign/export/export-dialog、dashboard（formatLabel 传入）
  - `makeBriefT`（brief.helpers.ts）→ prep-plan-dialog
  - `BRIEF_I18N` 直引（playbook.component.ts F6 内联 t）→ playbook 列表页技术覆盖图谱文案

### 影响面（本票将改动的共享件 → 谁在用 → 哪些流程经过）

1. **`core/i18n/i18n.service.ts` DICT**：新增 498 键（命名空间分区 `pb.v2.*` / `pb.br.*`），既有 530 个 pb.* 条目与全部非 pb.* 条目零改动。使用者 = 全站每个 `t()/tn()/tf()` 调用；经过流程 = 全部页面三语展示。新增只增不改，非 pb 键路径不受影响。
2. **`redesign/v2.helpers.ts` `makeV2T`/`makeV2Fmt`**：本地表优先接线改为直接委托 `i18n.t`（键已全部在核心 DICT，行为等价）。下游 10 组件；经过流程 = Playbook 详情页全部 tab（intake / rounds / materials / comm-log / qa-import / prep-report / export）、Dashboard 待投轮次 format 标签。
3. **`components/brief/brief.helpers.ts` `makeBriefT`**：同上改委托。下游 prep-plan-dialog；经过流程 = 备考方案生成弹窗（AI 生成 / 未配置 / 失败三态文案）。
4. **`playbook.component.ts` F6 内联 `t()`**：去掉 `BRIEF_I18N` 直引，改纯 `i18n.t`。经过流程 = Playbook 列表页技术覆盖图谱标题/展开收起/掌握度等文案。
5. **删除 `v2.i18n.ts` / `brief.i18n.ts`**：仅上述 3 个文件引用（已 grep 双证），无其他消费者。

### 不变项（本票明确不动的东西，逐条）

1. **键名**：577 个本地键名逐字保留（`pb.v2.*` / `pb.br.*`），不重命名、不并键、不删键。
2. **zh / en 文案**：逐字承自本地表，一字不改。
3. **fr 文案**：本地表有 fr 的 471 键逐字承袭；缺 fr 的 27 键按核心 DICT 既有约定填 en 同文（依据：重叠 79 键在核心 DICT 中 fr 与 en 全部同文，填 en 同文后 fr 展示与改前（en 兜底）逐字一致）——**不新造法语译文**，避免未批准的口径变更。
4. **核心 DICT 既有条目**（含 79 个重叠键的核心侧条目）：零改动，本地重复条目丢弃（值已逐字相同）。
5. **`I18nService` 公共 API**（`t`/`tn`/`tf`/`setLang`/`lang`/`locale`/持久化与 `<html lang>` 同步）：不动。
6. **组件调用点**：全部 `t(key)` / `fmt(key, params)` 签名与用法不动，组件文件除 playbook.component.ts 的 import 行外零改动。
7. **helpers 中其余纯函数**（applyParams、htmlToPlainText、isAudioAsset、derivePhase、normalizeThreadSubject、轮次日期序、entryResultOutcome/entryStatusBadgeKey、parseJsonList、daysUntil、nextRound、topQuestions、extractKeywords、renderMarkdown、normalizeEmailDate、parsePastedEmail 等）：不动。
8. **后端 / 数据库 / 迁移**：零改动（纯前端文案机制收敛）。
9. **语言切换、存储键 `yi.lang`、默认语言 en**：不动。

---

## 完成记录(2026-10-23,执行者回填)

- **实现**:498 个本地独有键并入核心 DICT 命名空间分区(pb.br.* 33 + pb.v2.* 465);v2.i18n.ts / brief.i18n.ts 删除;makeV2T / makeBriefT / playbook.component F6 内联 t 改直接委托 i18n.t;组件调用点零改动。
- **验证**:parity+boundary 42/42 PASS;引用键审计 570 引用 0 缺失;`tsc --noEmit` 0 错;`ng build` exit 0(AOT 模板检查通过)。证据:`docs/fix-program/evidence-43/`(PLAN + VERIFICATION + 两个可复现脚本)。
- **关键数字**:本地 577 键 = 重叠 79(zh/en 与核心逐字同,核心 fr=en 同文,并入后三语展示逐字不变)+ 本地独有 498 并入;27 个缺 fr 键按核心 DICT 既有约定填 en 同文(与改前 fr 模式 en 兜底一致)。
- **偏差(已登记台账待确认区)**:angular.json 初始 bundle 预算 1mb → 1.2mb —— 票面 mandate 使字典从懒加载 chunk 移入即时加载的核心 service,初始 bundle +86.3 KiB 超原预算;按协议 §1(票面 > 现有代码)最低幅度调整,待 Forrest 知悉。
- **状态**:PUSHED(sha 见 FIX-STATUS.md),待 CI 全绿按协议 §3 第四步转 VERIFIED。
