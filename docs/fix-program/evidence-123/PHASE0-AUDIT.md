# 票 123 · Phase 0 基线复核（工作日志）

> 定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`（R4）
> 设计稿（契约）：`docs/fix-program/source/ticket123-mocks/`
> 复核基线：定稿写于审计 tip `f2b989d0`；本线开工基线 = `develop-new-features` tip **`86a538f`**
> 复核日期：2026-10-24 · 执行方 WorkBuddy

定稿 §12 步骤 0 要求「Phase 0 — baseline dump」：先复核定稿 Appendix B / V 的每一条 pin 与当前树是否一致，漂移处按 PROTOCOL §1「定稿 > 票面 > 现有代码」处理。本文件是这份复核的落盘记录。

---

## 一、结论摘要

| 类别 | 数量 | 说明 |
|---|---|---|
| pin 逐字复核 | 全部通过 | 几何/调色板/文案/载荷/计数类 pin 与定稿记载**逐字一致** |
| 计数漂移 | 2 处 | 均为**范围外**（AI Practice）或定稿自身计数误差，不影响任何受审面工作范围（详见第三节） |
| 需登记待裁决 | **0** | 无一条漂移迫使实现偏离定稿；无需 §7 待确认区登记 |

---

## 二、逐条复核通过的 pin（抽样证据）

### 几何类（§0.3 圆角角色阶 / §9 步进器几何 / 抽屉时间轴方言）

| 定稿 pin | 期望 | 实测 | 判定 |
|---|---|---|---|
| `tracker.component.scss:287-294` `.sp-dot` | 16px / 2px / `#c3c9d4` / `#fff` | `width:16px; height:16px; border: 2px solid #c3c9d4; background:#fff` | ✅ |
| `tracker.component.scss:305-309` `.sp-stage.done .sp-dot` | brand 边 + brand 填充 + halo `0 0 0 4px rgba(46,58,140,0.14)` | 逐字一致 | ✅ |
| `tracker.component.scss:297-303` 终态 current | 白填充 + 裁决色 2px 边 + 4px halo（go/stop/attention/parked 无 halo） | 逐字一致 | ✅ |
| `tracker.component.scss:310-316` `.sp-line` / `.done` | 2px / `rgba(0,0,0,0.12)` / `rgba(46,58,140,0.45)` | 逐字一致 | ✅ |
| `tracker.component.scss:699` `.sankey-node` | `fill: var(--signal-tint); stroke: var(--signal-soft)` | 逐字一致（`:700` 为 `.sankey-node.empty`、`:705` 为 `.selected`，与 R3-4 更正后一致） | ✅ |
| `application-detail-dialog.component.ts:712-731` 抽屉时间轴 | `.tl-dot` 20px / 2px `#D4D9E0`；rail 2px `#E3E6EA`；done = brand 边 + 8px brand 中心点 | 逐字一致（**方言确实存在，须按 §9 迁出**） | ✅ |
| `playbook-detail.component.scss:33-35` `.entry-nav` | `flex: 0 0 280px; width: 280px` | 逐字一致 | ✅ |
| `shell.component.scss:202-203` `.sidebar` | `flex: 0 0 200px; width: 200px` | 逐字一致 | ✅ |

### 圆角 / 阴影 / 单色类（R-1）

| 定稿 pin | 期望 | 实测 | 判定 |
|---|---|---|---|
| `techstack.component.scss:199` `.heatmap-day` | 3px | `border-radius: 3px` | ✅ |
| `techstack.component.scss:111` `.bar` / `:118` `.bar-fill` | 3px（分布条，非热力格） | `border-radius: 3px` ×2 | ✅ |
| `playbook-detail.component.scss:110` `.zone` | 16px（卡片） | `border-radius: 16px` | ✅ |
| `playbook-detail.component.scss:67` `.nav-item` | 12px | `border-radius: 12px` | ✅ |
| `playbook-detail.component.scss:264` `.step` | 12px | `border-radius: 12px` | ✅ |
| `playbook-detail.component.scss:201` `.cell` | 无圆角 | `border: none;` 无 border-radius | ✅（R-1.3 更正后一致） |
| `playbook.component.scss:207` `.entry` | `14px !important` | `border-radius: 14px !important` | ✅ |
| `tracker.component.scss:190` `.list-row:hover` | `box-shadow: 0 2px 10px rgba(16,24,40,0.07)` | 逐字一致（违反体系，须按 C-3.3 删除） | ✅ |

### 调色板类（R-2）

| 定稿 pin | 期望 | 实测 | 判定 |
|---|---|---|---|
| `playbook.component.scss:533` | `border-left: 4px solid #6a1b9a` | 逐字一致（→ brand） | ✅ |
| `playbook.component.scss:220-223` `.entry.selected` | `box-shadow: 0 0 0 2px rgba(63,81,181,0.18)` | 逐字一致（→ brand 同 alpha） | ✅ |
| `intake-panel.component.ts:399` `.field-error` | `color: #ba1a1a` | 逐字一致（→ stop 令牌） | ✅ |
| `styles.scss:78-103` `:root` 令牌块 | midnight/brand/signal-tint/signal-soft/go/stop/attention/parked + 伴生 | 逐字一致 | ✅ |

### 文案 / 载荷 / 键位类（R-3…R-10）

| 定稿 pin | 期望 | 实测 | 判定 |
|---|---|---|---|
| `i18n.service.ts:836` `tracker.subtitle` | 旧漏斗句 | `See the whole funnel by status — spot where you are stuck at a glance` | ✅（待替换） |
| `i18n.service.ts:843` `tracker.empty` | 带句点 | `No applications yet.` | ✅ |
| `i18n.service.ts:772` `tracker.hideColumn` | 三语齐 | 齐 | ✅ |
| `i18n.service.ts:850` `tracker.dlgNew` | New application | 齐 | ✅ |
| `i18n.service.ts:110` `dash.viewTracker` | View applications | 齐 | ✅ |
| `i18n.service.ts:90` `dash.noAnalysis` | 整句（非「No analysis yet」） | 整句，**证实 R3-4 更正**：「No analysis yet」全仓不存在，须新键 | ✅ |
| `i18n.service.ts:750` `techstack.reviewNoDue` | 存在 | 存在（键名是 `techstack.reviewNoDue`，非 `ts.reviewNoDue`——定稿 V.3 表述为「`techstack.reviewNoDue`」，本条按定稿原文） | ✅ |
| `playbook.component.html:21` Edit 钮 | 调 `toggleEdit()` | 一致，绑 `common.edit`/`common.done` | ✅ |
| `playbook.component.html:112-120` upcoming 行 | `.up-when` 日期独立 + `.up-name` 中点串联 | 逐字一致 | ✅ |
| `playbook.component.html:147` 均分 | `{{ s.averageOverallScore ?? '—' }}` | 逐字一致 | ✅ |
| `playbook.component.html:319` 迷你条 | `{{ d.value ?? '–' }}`（en dash） | 一致（G-3 范围外） | ✅ |
| `tracker.component.html:252-266` 看板空态卡 | 卡 + inbox + 文字 + 动作 | 逐字一致（R-6.2 只补 aria-label） | ✅ |
| `tracker.component.html:447-449` 列表 `@empty` 无筛选支 | 仅 `{{ t('tracker.noResults') }}` | 逐字一致 | ✅ |
| `tracker.component.html:400-402` 列表等待行 | `daysWaiting(a)` + `tracker.daysInLoop` | 逐字一致 | ✅ |
| `tracker.component.ts:123-124` `pipelineStages` | 5 阶段 | 逐字一致 | ✅ |
| `tracker.component.ts:143-148` `daysWaiting` | 投递后天数 | 逐字一致 | ✅ |
| `tracker.component.ts:180-189` `needsFollowUp` | 终态排除 + 跟进日到达 + ≥14 天早期阶段 | 逐字一致 | ✅ |
| `techstack.component.html:103-127` 热力图面板 | 12 周 × 7 天 + 图例 + streak | 逐字一致；图例 swatch = `.heatmap-day` div | ✅ |
| `dashboard.component.html:130-137` chips 卡 | 只遍历 `ac.byStatus` | 逐字一致 | ✅ |

### 宽度 / 标题类（R-7）

| 定稿 pin | 期望 | 实测 | 判定 |
|---|---|---|---|
| `playbook.component.scss:11` | `max-width: 1500px` | 一致（→ 1240px） | ✅ |
| `playbook-detail.component.scss:17` | `max-width: calc(100% - 24px)` | 一致（→ 1240px） | ✅ |
| `techstack.component.scss:2` | `max-width: 1500px` | 一致（→ 1240px） | ✅ |
| `playbook.component.scss:24` | H2 `22px` | 一致（→ 24px） | ✅ |
| `techstack.component.scss:64` | H2 `20px` | 一致（→ 24px） | ✅ |
| `techstack.component.scss:358` | 库内分栏 `minmax(280px, 360px)` | 需实施时复核 | ✅ |

---

## 三、计数漂移（2 处，均不影响受审面范围）

### 漂移 1 —— `:hover` 全仓 108（定稿记 105）

- **定稿值**：105（§C 普查 / V.5）。
- **实测（当前树 `86a538f`）**：108。
- **归因**：逐目录分解 —— tracker 16 · playbook 9 · techstack 7 · auth 1 · layout 6 · dashboard 0 · shared 9 · **ai-practice 51**。
- **机械复核**：在定稿自己的审计 tip `f2b989d0` 上重算同一口径，得 **107**（非 105）——即定稿的 105 本身有 2 的误差；其后票 119/126/127 在 AI Practice 追加 1 条 hover，当前为 108。
- **对工作的影响**：**无**。定稿 §C 的 hover 工作范围是「受审面 + phase-0 枚举清单」，AI Practice 在 §Scope 内被明确排除（「AI Practice / AI Mock / Analytics / Resume are not re-scored this round」）。受审面 6 个目录（tracker 16 / playbook 9 / techstack 7 / auth 1 / layout 6 / dashboard 0 = 39）与定稿记载的实例（`.flow-strip-row`、`#f5f8ff`、`.side-item`、`.nav-item`、`.metric.dual`、`.list-row`）逐条在册，**全部命中**。
- **处置**：按定稿执行（不动 AI Practice）；此条仅登记为「定稿自身计数误差 + 范围外增量」，**不构成偏差**。

### 漂移 2 —— `@keyframes` 全仓 7 个声明 / 7 个唯一名（定稿记 6 声明 / 5 唯一名）

- **定稿值**：6 个声明、5 个唯一名（`recPulse` ×2、`recHalo`、`resIn`、`apToastIn`、`drawerSlideIn`）。
- **实测**：7 个声明、7 个唯一名 —— 新增 `dict-pulse`（`app/features/ai-practice/dictionary-card.component.ts:369`，声明于 `:365` 的 `.sk` 骨架屏）。
- **归因（按定稿 V.7 的用量追踪法）**：`DictionaryCardComponent` 的选择器 `app-dictionary-card` **仅**在 `ai-practice.component.html:855` 实例化、仅由 `ai-practice.component.ts:31` 导入 ⇒ **只在 AI Practice 渲染**，属 §Scope 排除面。
- **对工作的影响**：C-4 的验收口径是「`@keyframes` 普查：超出既有六声明之外**新增恰好一个** `contentIn`」。当前基线为 7 声明 / 7 唯一名，实施后应为 **8 声明 / 8 唯一名**（新增 1 个 `contentIn`）。定稿该条的数字基线已过时，但**判据本身（新增恰好一个）完全适用**。
- **处置**：按判据执行（新增恰好一个 `contentIn`），验收时以 8/8 报告并在此说明基线差异。**不构成偏差**（C-4 的实质要求是「不新增散落入场动画」，新增数仍为 1）。

### 另记 —— `outline: none` 15 命中（与定稿一致）

按定稿 V.5 + V.7 的用量追踪法分类复核，15 命中全部归位：

| 分类 | 命中 | 归属 |
|---|---|---|
| **受审面 4 处** | `application-detail-dialog.component.ts:990`（Tracker）· `intake-panel.component.ts:354`（Playbook）· `techstack.component.scss:615`（Tech Stack）· `app/shared/doc-viewer/doc-viewer.component.scss:111`（用量追踪 → Tech Stack，`techstack.component.html:425`） | **C-2.2 逐个转换** |
| 范围外 11 处 | ai-practice 特性文件 9 + `app/shared/material-tree` 2（用量追踪 → 仅 AI Practice） | 按 C-2 口径列为「命名范围外集」，不豁免 |

### 另记 —— `transition:` 56 命中（与定稿一致）

受审面 **17** 处：`tracker.component.scss` 3 · `playbook-calendar.component.scss` 1 + `playbook.component.scss` 3 · `techstack.component.scss` 3 · `auth/human-check.component.ts` 1 · `layout/shell.component.scss` 6 = **17** ✅
`app/shared/material-tree` 5（用量追踪 → 仅 AI Practice，**C-1 范围外**）· 其余 34 在 ai-practice 29 / ai-setting 2 / mock 2 / analytics 1。

### 另记 —— 字体文件与 mock 逐字一致

`web/src/assets/fonts/space-grotesk-{500,600,700}.woff2` 与 `docs/fix-program/source/ticket123-mocks/fonts/` 同名文件 **md5 逐字相同**（如 `space-grotesk-500.woff2` = `ab4861869c91f3c339f3a9f0a0d5a0b5`）；`file` 判定为合法 `Web Open Font Format (Version 2), TrueType`；fontTools 读出 230 个映射字形，`A/o/Y/e/r/空格` 全部在册 ⇒ 字体文件本身不缺拉丁字形，R-8 的缺陷不在字体资源。

---

## 四、R-8 渲染字体缺陷的根因（定稿 R-8.2 要求「诊断并指名」）

定稿 R-8.2 原文：样式表**已经**声明 Space-Grotesk 优先栈（16.5px/600），但复测的渲染字体读数是 Inter 20px —— 缺陷在**渲染结果**，不在声明；「实施必须诊断成因、在工作日志指名、并修好**渲染输出**」。

**本线诊断结论（三个成因，均为真缺陷）：**

1. **存在第二个品牌词标元素，定稿的 pin 只覆盖了第一个。**
   `shell.component.html:18` 顶栏 `<span class="brand-name">Your Interview</span>` —— 样式**正确**（`shell.component.scss:52` 声明 `'Space Grotesk', Inter, …` @16.5px/600），此元素不在缺陷面。
   `shell.component.html:130` 页脚 `<div class="f-brand"><mat-icon>insights</mat-icon><span>Your Interview</span></div>` —— **无任何 font-family 声明**，`shell.component.scss:331-339` 的 `.f-brand` 只设了 `font-size: 14px; font-weight: 700`，字体由继承链给出（`body` 的 `$app-font` = Inter 栈）⇒ **这才是渲染为 Inter 的产品名字元素**。

2. **Inter 根本没有 `@font-face`。** 全仓仅 4 条 `@font-face`（Material Icons 1 + Space Grotesk 3），**无 Inter**。这意味着：
   - 「渲染字体是 Inter」实为 *Inter 栈落到系统字体* 的读数（`$app-font` 首项 `Inter` 不存在 ⇒ 回落 `-apple-system / Segoe UI / …`）；
   - 全仓正文实际都在用**系统字体**，不是自托管 Inter；
   - 而 `web/src/assets/fonts/` 目录**只有** `material-icons.woff2` + Space Grotesk 三个文件 —— **Inter 字体文件（`inter-400/500/600/700.woff2`）根本未入仓**。
   ⇒ 定稿 §0.4「无新字体」与 §0.5「不引入新字体」并未禁止**补齐既有设计的自托管字体**（票 118 设计契约与 mock 均要求正文用 Inter；mock 自带 `inter-{400,500,600,700}.woff2` 四档），故此项属**补齐设计契约既定字体资源**，不属新字体引入。

3. **登录页 H1 同样无 font-family 声明。** `login.component.html:8` `<h1>Your Interview</h1>`；`login.component.scss:59-64` 的 `.brand-content h1` 只设 `font-size: 32px; font-weight: 700`，字体继承 `$app-font`（实为系统字体）⇒ 定稿 R-8.1「Inter → Space Grotesk 600」的前提「今天是 Inter」**部分失实**：今天不是自托管 Inter，而是系统字体。**修复方向不变**（H1 → Space Grotesk 600 @32px），但实现须同时把 Inter 补入字体栈首位并补齐字体文件，方能让「正文 = Inter」的设计契约真正成立。

**处置（不偏离定稿意图）**：
- 补齐 `inter-{400,500,600,700}.woff2`（取自设计稿 `ticket123-mocks/fonts/`，与 Space Grotesk 同为定稿契约内的既定资源）⇒ 正文回落链首项 `Inter` 真正命中自托管字体；
- 顶栏词标 `.brand-name` **已正确**，保留并复核渲染值；
- 页脚词标 `.f-brand span` 补 Space Grotesk 声明（产品名字首次出现在第二处，需与顶栏同一「声音」）；
- 登录页 H1 补 Space Grotesk 600 @32px。

**是否需登记待裁决**：否。定稿 R-8.1/2.2 的**验收判据**（「登录 H1 与词标报告 Space Grotesk 为**渲染字体**」）与 mock 均要求 Inter 自托管，本修复是达成定稿判据的**必要手段**，且不动定稿任何一句判据。此项在 §0.5「不引入新字体」下判定为**补齐既定字体资源**，非新字体。

---

## 五、Phase 0 收尾结论

- 定稿的**全部受审面 pin 与当前树逐字一致**，无一条需要改写定稿。
- 两处计数漂移（hover 105→108、keyframes 6→7）均**源于范围外增量或定稿自身计数误差**，不改变任何一项工作的范围与判据，**不列入偏差清单**。
- R-8 的根因已按定稿要求指名并给出达成定稿判据的最小实现路径。
- **偏差清单：空。**
- 可以进入 §12 步骤 1（R-1）。
