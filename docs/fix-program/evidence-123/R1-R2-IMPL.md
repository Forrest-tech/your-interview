# 票 123 · R-1 / R-2 实施记录（工作日志）

> 定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`（R4）
> 实施基线：`develop-new-features` tip `86a538f`
> 实施日期：2026-10-24 · 执行方 WorkBuddy
> 前置：`PHASE0-AUDIT.md`（偏差清单：空）

---

## 一、R-1 —— 令牌残留：半径 / 阴影 / 单色 / H3

### R-1.4 圆角角色阶（§0.3）

**角色表**（定稿 §0.3）：6px 小件 · 8px 控件 · 10px 卡片/行/面板 · 14px 对话框/抽屉/主视觉 · 999px 药丸/徽标 · 50% 圆形。
**豁免**：Material `mat-form-field` 轮廓角段（复合值）、滚动条滑块、ripple/focus 叠层。

改法：**逐个 off-role 值定位到具体选择器后按角色归位**，不按口味拍。落地 29 条规则、13 个文件：

| 原值 | 角色 | 落点 |
|---|---|---|
| 3px | 小件 → 6px | `.heatmap-day`、`.bar` / `.bar-fill`（Tech Stack）、`.cov-legend .sw`、`.cov-bar`、round-record `.bar`/`.bar-fill` |
| 7px | 小字段 → 6px | `.fld.sm` ×4（comm-log / materials-list / qa-import / round-record）、`.toc-item`（prep-report） |
| 9px / 11px | 药丸/徽标 → 999px | techstack `:182`/`:464`/`:553`/`:709`、`.rs-progress`、tracker `.status-pill`/`.kw`、青色标签、duplicates-dialog chip |
| 12px | 卡片/行/面板 → 10px | `.nav-item`、`.zone`、`.step`、`.entry`（去 `!important`）、公司 logo、`.citem`、`.mat`、`.overview`/`.qcard`/`.player-bar`、tracker 六个卡片、login 品牌方块 |
| 16px | 面板 → 10px | `.zone`、login 品牌方块 → 14px（对话框角色） |
| 2px / 4px | 小件 → 6px | `.split-handle`、`.sent-on`、`.seg`、tracker `.select-box`、human-check 三处、login `.top-bar` |
| 12px（头像） | 圆形 → 50% | login 40px 头像 |
| 16px（登录品牌方块） | 对话框/主视觉 → 14px | login `.brand-content` |

**实施后普查**：`10px` 55 · `999px` 51 · `8px` 41 · `6px` 38 · `50%` 17 · `5px` 8（**全部为滚动条 track/thumb，§0.3 豁免**）· `0` 3（重置）· `14px` 2 ⇒ **受审面圆角全部落在角色集合内**。

### R-1.5 阴影

`playbook-detail.component.scss` 的 `box-shadow` 复核：仅剩 `:73` 的 `.nav-item.on` brand 状态环（`0 0 0 1px var(--yi-accent, #2e3a8c)`）—— 定稿 R-1.5 明文保留的「状态环」⇒ **elevation 阴影已为 0**，符合「卡片坐在发丝线上，唯一抬升是抽屉/对话框」。

### R-1.6 单色字体（monoCount 3 → 0）

定稿要求「phase-0 probe identifies the 3 monospace elements by element path」。**探针结论**（`tools/census-mono.mjs`，浏览器 computed style）：

- 受审面（playbook / tracker / techstack / dashboard）源码内**无任何** `monospace` 字面量 —— 命中全部在**范围外**文件（admin ×4、profile ×4、resume ×2）。
- Playbook 侧的 `<pre>` 元素共 6 个（`intake-panel` `.bcard-body`、materials-list `.body-text`、comm-log `.ci-full`、detail `.full-text` ×3），**全部已声明 `font-family: inherit`** ⇒ 不吃 UA 的 `pre { font-family: monospace }`，回落链与正文一致。
- **真正的 mono 元素在 Tracker's 抽屉**：`application-detail-dialog.component.ts:851` `.resume-text-view`（`<pre class="resume-text-view">`）**无 `font-family` 声明** ⇒ 命中 UA 默认等宽。这是 Tracker 抽屉（受审面）上的等宽数据读数。

**处置**：
- `.resume-text-view` 补 `font-family: inherit`（与 Playbook 侧 6 个 `<pre>` 同款口径）⇒ 渲染回落 Inter 栈，**不再读数等宽**；
- 其余 6 个 `<pre>` 已合规，不动；
- 范围外的 admin/profile/resume 显式等宽（`ui-monospace, SFMono-Regular, Menlo, monospace`）是**有意的代码/ID 展示**，不在受审面，**不动**。

> 附注：R-1.6 的判据「Playbook list 与 detail 的 monoCount = 0」在当前树**本就为 0**（源码无 mono、无 `<pre>` 漏声明）；实际漏声明处在 Tracker 抽屉。实现按**判据的意图**（受审面不出现等宽读数）一并补上，并在此记录实测事实。

### R-1.7 H3 字体归位（Space Grotesk 600）

定稿点名：Playbook list「In Progress」H3、Playbook detail 段 H3s、Tech Stack 段 H3s。
验收判据更宽：「every visible h3 computed fontFamily first entry = "Space Grotesk"」（覆盖审计七面）。

**受审七面的 h3 全量清单与改动**（逐面枚举，非抽样）：

| 面 | 元素 | 现状 | 改后 |
|---|---|---|---|
| Playbook list | `.section-title`（"In Progress"，16px/600） | 无 font-family | `Space Grotesk, Inter, sans-serif` |
| Playbook list | `.empty-card h3`（17px） | 无 | 同上 |
| Playbook detail | `.tail-card h3`（"Review & to-dos"，16px/600） | 无 | 同上 |
| Playbook detail | `.rc-head h3`（"Round 1 · Technical"，16px/600，`round-card.component.ts:47`） | 无 | 同上 |
| Playbook detail | `.panel-head h3`（intake 面板 20px/600） | 无 | 同上 |
| Tech Stack | `.dh-main h3`（19px/600） | 无 | 同上 |
| Tech Stack | `.rs-head h3`（20px/600） | 无 | 同上 |
| Tech Stack | `.rs-summary h3`（19px/600） | 无 | 同上 |
| Tracker 抽屉 | `.status-card h3`（16px/**700**） | 无 | 同上，weight 700 → **600** |
| Tracker 抽屉 | `.notes-card h3`（16px/**700**） | 无 | 同上，weight 700 → **600** |
| Dashboard | `.ac-title`（15px/600） | **已有** SG | 保留 |
| Tracker | `.chart-head h3`（15px/600） | **已有** SG | 保留 |
| Calendar / Login | 无 h3 | — | — |

**weight 归一说明**：定稿 R-1.7 原文「→ **Space Grotesk 600** at their current sizes」——「600」指字重，「their current sizes」指保留原字号。Tracker 抽屉两处原为 700，按此条归一到 600；其余各处原已 600，不动。

**范围外不动的 h3**：AI Practice（6）、AI Setting（2）、Mock（3）、Profile（1）—— 定稿 §Scope 明写「AI Practice / AI Mock / Analytics / Resume … no per-page work is specified for them here」。

---

## 二、R-2 —— 调色板全量清扫

### 三个具名实例（定稿逐条点名）

1. `playbook.component.scss:535` `.coverage { border-left: 4px solid #6a1b9a }` → **`#2e3a8c`**（brand）。定稿 R-2.1 的映射依据是 DESIGN-PLAN §3「Playbook 'Tech coverage map' edge/bars → signal indigo」。
2. `playbook.component.scss:224` `.entry.selected` 状态环 `box-shadow: 0 0 0 2px rgba(63, 81, 181, 0.18)` → **`rgba(46, 58, 140, 0.18)`**（brand 同 alpha）。`rgb(63,81,181)` = `#3F51B5`，118 前的 stock indigo。
3. `.field-error { color: #ba1a1a }` → **stop `#b3261e`**。定稿点名 `intake-panel.component.ts:399`；R-2 的 class rule 是 app-wide，故**全仓 14 处同语义**（admin ×2、mock、intake-panel、round-card、playbook ×2、profile、knowledge-dialog ×2、techstack、answer-library、detail-dialog ×2）**一并映射**。

### 类规则清扫（app-wide）

**白名单**（定稿 R-2.3 逐条照抄）：`:root` 令牌块（`styles.scss:78-103`）· `$brand-palette` 色阶 50–900 + A100–A700 · 上述任一色的 alpha 衍生物 · 等于令牌值的字面 hex · 头像豁免（`avatarColor()` 产物，按元素定义）。

**实施**：用 `tools/r2-palette-map.py` + `r2-palette-map2.py`（hex）与 `tools/r2-rgba-map.py`（rgba 基色）做**按语义族**的映射，脚本与映射表随票落盘（`evidence-123/tools/`），每条映射在表内标注了目标令牌。

| 语义族 | 目标 | 旧值举例 |
|---|---|---|
| 绿（通过/成功） | `--go` / `--go-tint` | `#2e7d32` `#22a35c` `#10b981` `#e8f5e9` `#d6f0e0` … |
| 红（失败/危险） | `--stop` / `--stop-tint` | `#c62828` `#dc2626` `#ef4444` `#ffebee` `#fecaca` … |
| 琥珀（需注意） | `--attention` / `--attention-tint` | `#ef6c00` `#f59e0b` `#b45309` `#fff3e0` `#fef3c7` … |
| 靛蓝/蓝/紫/青（强调） | `--brand` / `--signal-tint` / `--signal-soft` | `#3f51b5` `#2f6fed` `#2563eb` `#8b5cf6` `#eef4ff` `#c5cae9` … |
| 深灰（正文） | `--ink` | `#1f2937` `#111827` `#1e293b` `#24262b` … |
| 中灰（次级文字） | `--ink-2` / `--parked` | `#475569` `#64748b` `#9aa1ab` `#8a9099` … |
| 描边/分隔 | `--hairline` / `--track` | `#e5e7eb` `#cbd5e1` `#c5cdd9` `#e2e8f0` … |
| 底面 | `--paper` / `--surface` / `--parked-fill` | `#f9fafb` `#f1f5f9` `#f7f9fc` … |

**`--yi-*` 变量族**（**定稿未点名，本线新发现**）：`--yi-accent` / `--yi-border` / `--yi-surface` / `--yi-text` / `--yi-text-muted` 五个变量**全仓无任何定义点**（唯一例外是 `playbook-detail.component.scss:28` 的局部 `--yi-border: #98a2b3` 声明），因此各处 `var(--X, <回退值>)` 的**回退值就是真实渲染值**：
- `--yi-accent` 的回退是 `#3f51b5`（19 处）—— 正是 R-2 要杀的 118 前 stock indigo ⇒ 映射到 `--brand` `#2e3a8c`；
- `--yi-border`（52 处 `#ccc`/`#ddd`/`#e0e0e0`…）⇒ `--hairline` `#e1e4ec`；
- `--yi-surface` ⇒ `--surface` `#ffffff`；`--yi-text` ⇒ `--ink` `#1f2430`；`--yi-text-muted`（64 处）⇒ `--ink-2` `#5b6270`。

**Analytics 漏斗色阶**（`analytics.component.ts:22` `FUNNEL_COLORS = ['#9fa8da','#7986cb','#5c6bc0','#2e3a8c','#2e3a8c']`）：这是 R-2 修订中已被划掉的「七步漏斗色阶」的**残留实例**。定稿 R-2.4 规定「任何真正的新分类色，发现即映射到白名单或带回为方案修订」；漏斗需要 5 级可区分，而白名单的 `$brand-palette` 色阶有足够的台阶 ⇒ **映射到品牌色阶**：`#b6bee6`（200）· `#8e9ad6`（300）· `#6673c2`（400）· `#2e3a8c`（600）· `#27316f`（700）。**不新增任何颜色**，不构成方案修订。

**rgba 基色**：221 处 `rgba(...)` 的基色改为白名单色的 alpha 衍生物，**alpha 一律原样保留**（如 `rgba(63,81,181,0.12)` → `rgba(46,58,140,0.12)`；`rgba(0,0,0,0.12)` 阴影 → `rgba(31,36,48,0.12)` = ink 派生）。

### R-2 实施后普查结果

**源码侧（`tools/census-hex-r2.py` + 等价 rgba 普查）**：

| 指标 | 实施前 | 实施后 |
|---|---|---|
| 非白名单 hex（色） | 347 | **2**（均为豁免/钉位项，见下） |
| 非白名单 hex（处） | 1436 | **5** |
| 非白名单 rgba（值 / 处） | 49 / 68 | **0 / 0** |

**剩余 2 色 = 两条明文豁免，不是偏差**：
1. `#7d8797`（4 处）= **滚动条滑块色** —— §0.3 豁免项（「scrollbar thumbs」），且 §0.5「不引入新颜色」不禁止保留既有豁免色；
2. `#c3c9d4`（1 处，`tracker.component.scss:292`）= **定稿 Appendix V 的钉位**（「Stepper: `.sp-dot` 16px, 2px border `#c3c9d4`」），R-9 的 The Track 要**复用**该几何 ⇒ 改它会直接违反 R-9。

**受审六面（auth/dashboard/tracker/playbook/techstack/calendar）单独口径**：非白名单 hex **0 色 0 处**（上表两色分别落在 tracker 的钉位与 playbook 的滚动条豁免）。

**头像豁免**：四处 `avatarColor()` 副本（tracker `:546`、detail-dialog `:1488`、playbook `:500`、dashboard `:184`）返回 `hsl(...)` 产物，**不是 hex**，不进入本普查；定义仍按元素而非色表，**未改一行**。

---

## 三、偏差清单

**空。**

- R-1.4 圆角：全部 off-role 值按 §0.3 角色表归位，受审面零越界。
- R-1.5 阴影：elevation 为 0，仅剩定稿明文保留的状态环。
- R-1.6 单色：受审面等宽读数归零（补 `.resume-text-view` 的 `font-family: inherit`）。
- R-1.7 H3：受审七面 h3 全部 Space Grotesk 600。
- R-2 调色板：非白名单 hex 仅剩两条明文豁免；rgba 基色全部落在白名单色的 alpha 衍生物内。

**未触及定稿任何一句判据**；本线新发现的 `--yi-*` 无定义变量族与 Analytics 漏斗色阶，均按定稿 R-2.3/R-2.4 的既有条款处置（映射到白名单），**不构成方案修订，无需 §7 待裁决**。
