# §C 实施证据 — Craft 四项（C-1 / C-2 / C-3 / C-4）

- **票**：123（设计修复）
- **定稿**：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §C（第 276–323 行）
- **基线**：`develop-new-features` tip = `86a538f`（开工前拉取）
- **本节提交**：见台账「本票实施进度」表 §C 行
- **偏差清单**：**空**（见 §7）

---

## 1. 这一节要解决什么

定稿 §C 的开篇是 Phase 0 普查对「Craft」维度的四项判定，四个缺口互相独立、互相咬合：

| 缺口 | 普查证据（定稿原文） |
|---|---|
| 无动效令牌 | `--dur` / `--ease` / `--motion` 全仓 0 处；受审面 17 条 `transition:`，时长散落在 110/120/130/150/160/180/200/220/400ms |
| 不支持减少动效 | `prefers-reduced-motion` 全仓 0 处 |
| 焦点只做了一半 | 全局 `:focus-visible` 强制 `border-radius: 4px`（inset 变体 8px）；四个受审站点 `outline: none`；Sankey 节点组纯点击不可键盘 |
| 悬停无共享配方 | 105 处 `:hover` 无统一配方，且示例互相打架（black-alpha / hex / 白底 / midnight 白-alpha 各不相同）；Tracker `.list-row:hover` 还带扁平体系违规的 `box-shadow` |

对应 C-1（令牌 + reduced motion）、C-2（焦点补全）、C-3（悬停/按压统一）、C-4（状态编舞：一次编排时刻）。

---

## 2. C-1 — Motion tokens + reduced motion

### 2.1 令牌落点

`web/src/styles.scss` `:root`（第 115–118 行）：

```scss
--dur-fast: 120ms;
--dur: 160ms;
--dur-slow: 400ms;
--ease: cubic-bezier(0.2, 0, 0, 1);
```

对照定稿：**`--dur-fast: 120ms` · `--dur: 160ms` · `--dur-slow: 400ms` · `--ease: cubic-bezier(0.2, 0, 0, 1)`** — 四个值逐字一致，缓动曲线即 Material emphasized easing。

### 2.2 十七处转换逐条对照

定稿要求：「durations ≤ 130ms → `var(--dur-fast)`，150–220ms → `var(--dur)`，the single 400ms layout transition → `var(--dur-slow)`；bare `ease` → `var(--ease)`」。

| 文件 | 行 | 转换后 | 定稿映射 | 判定 |
|---|---|---|---|---|
| `features/tracker/tracker.component.scss` | 118 | `opacity var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `features/tracker/tracker.component.scss` | 188 | `box-shadow var(--dur) var(--ease)` | 同上 | ✅ |
| `features/tracker/tracker.component.scss` | 467 | `box-shadow var(--dur-fast) var(--ease), transform var(--dur-fast) var(--ease)` | ≤130ms → `--dur-fast` | ✅ |
| `features/playbook/playbook.component.scss` | 226 | `box-shadow var(--dur) var(--ease), transform var(--dur) var(--ease), border-color var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `features/playbook/playbook.component.scss` | 410 | `width var(--dur-slow) var(--ease)` | **the single 400ms layout transition** → `--dur-slow` | ✅ |
| `features/playbook/playbook.component.scss` | 462 | `background var(--dur) var(--ease), gap var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `features/playbook/playbook-calendar.component.scss` | 73 | `background var(--dur)` | 150–220ms → `--dur` | ✅（见 2.4 说明） |
| `features/techstack/techstack.component.scss` | 132 | `width var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `features/techstack/techstack.component.scss` | 418 | `background var(--dur)` | 150–220ms → `--dur` | ✅（见 2.4 说明） |
| `features/techstack/techstack.component.scss` | 452 | `background var(--dur-fast) var(--ease)` | ≤130ms → `--dur-fast` | ✅ |
| `features/auth/human-check.component.ts` | 55 | `border-color var(--dur) var(--ease), box-shadow var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `layout/shell.component.scss` | 87 | `background var(--dur-fast) var(--ease), opacity var(--dur-fast) var(--ease)` | ≤130ms → `--dur-fast` | ✅ |
| `layout/shell.component.scss` | 136 | `background var(--dur-fast) var(--ease)` | ≤130ms → `--dur-fast` | ✅ |
| `layout/shell.component.scss` | 213 | `flex-basis var(--dur) var(--ease), width var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `layout/shell.component.scss` | 249 | `background var(--dur-fast) var(--ease), color var(--dur-fast) var(--ease)` | ≤130ms → `--dur-fast` | ✅ |
| `layout/shell.component.scss` | 364 | `color var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |
| `layout/shell.component.scss` | 383 | `color var(--dur) var(--ease)` | 150–220ms → `--dur` | ✅ |

**计数**：受审面 17 条 —— tracker 3、playbook 3、calendar 1、techstack 3、auth 1、layout 6 = 17。与 Phase 0 普查计数一致（定稿列举为 tracker 3 / playbook 4 / techstack 3 / auth 1 / layout 6 = 17，其中 playbook 的 4 条里含 calendar 那条在定稿的分类里归到「playbook 4」；本实现按文件分别落点，总数仍为 17）。

> 说明：定稿列举分布为「tracker 3, playbook 4, techstack 3, auth 1, layout 6」，共 17。本实现按实际文件落点列出，playbook 系（`playbook.component.scss` 3 条 + `playbook-calendar.component.scss` 1 条）合计 4 条，与定稿的 playbook 4 吻合；techstack 3 条吻合。故计数无偏差。

### 2.3 全局 reduced-motion 块

`web/src/styles.scss` 文件末尾（定稿 §C C-1 变更 2 给出的原文块，**逐字照抄**）：

```css
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after {
    transition-duration: 0.01ms !important;
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    scroll-behavior: auto !important;
  }
}
```

定稿说明该块「App-wide by nature（它也一并压住 AI Practice 的录音脉冲与 Tracker 抽屉的 `drawerSlideIn` 入场动画）—— 两者都压得住，而且这里写明是**决定**而非意外」。实现即如此落地，未对 AI Practice 做例外。

### 2.4 两处「原本没有 easing 关键字」的声明

`playbook-calendar.component.scss:73` 与 `techstack.component.scss:418` 的原始声明是 `background 0.15s` —— **没有 `ease` 关键字**，走的是 CSS 默认 `ease`（`cubic-bezier(0.25, 0.1, 0.25, 1)`）。

定稿 C-1 变更 1 写的是「bare `ease` → `var(--ease)`」，指的是**显式写了 `ease` 关键字**的声明。这两条原本没有关键字，强行补上 `var(--ease)`（Material emphasized 曲线 `cubic-bezier(0.2, 0, 0, 1)`）会**改变既有的运动手感**——那是超出定稿授权的行为变更。

处理：**只 token 化时长**（`background var(--dur)`），保留 CSS 默认缓动语义。这是本节唯一一处需要判断的地方，记此备查。

---

## 3. C-2 — Focus completion

### 3.1 去掉强制圆角

定稿 C-2 变更 1：「Remove the forced `border-radius: 4px` from the global `:focus-visible` rule and the `8px` from its `.app-card` / `.entry` variant —— the outline follows each element's own radius in every browser the app targets; offsets stay exactly as they are.」

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 全局 `:focus-visible` 去掉 `border-radius: 4px` | `styles.scss` 全局 `:focus-visible` 规则（保留 `outline: 2px solid var(--brand)` 与 `outline-offset: 2px`） | ✅ |
| `.app-card` / `.entry` inset 变体去掉 `border-radius: 8px` | 同文件该 inset 变体规则（offset 不动） | ✅ |
| 「offsets stay exactly as they are」 | 两处 `outline-offset: 2px` 均未动 | ✅ |

### 3.2 四个 `outline: none` 站点逐个对照

定稿点名四处，要求「gain the shared ring on `:focus-visible`, keeping their existing border-color cue」。

| # | 定稿点名位置 | 实现落点 | 保留的 border 线索 | 补的共享焦点环 | 判定 |
|---|---|---|---|---|---|
| 1 | `application-detail-dialog.component.ts:990`（`.plain-area:focus`） | 同文件 `.plain-area` 样式块 | `:focus { border-color: var(--brand); }` | `:focus-visible { outline: 2px solid var(--brand); outline-offset: 2px; }` | ✅ |
| 2 | `intake-panel.component.ts:354`（textarea） | 同文件 `.paste` | **线索在父容器** `.paste-box:focus-within`（`.paste` 自身 `border: none`，未动） | `:focus-visible { outline: 2px solid var(--brand); outline-offset: 2px; }` | ✅ |
| 3 | `techstack.component.scss:615`（`.blk-editor:focus`） | 同文件 `.blk-editor` | `:focus { border-color: … }` 保留 | `:focus-visible { outline: … }` | ✅ |
| 4 | `app/shared/doc-viewer/doc-viewer.component.scss:111`（`.doc-editor:focus`） | 同文件 `.doc-editor` | `:focus { border-color: … }` 保留 | `:focus-visible { outline: … }` | ✅ |

第 4 项定稿特别强调：「converted here by name, **not** left to the phase-0 exemption list」—— 实现确实按名转换，未进豁免表。

### 3.3 Sankey 节点键盘化

定稿 C-2 变更 3 要求：`tabindex="0"`、`role="button"`、`aria-label` = 阶段标签 + 计数、Enter/Space 调同一个 `selectFlowStage()`，焦点以**既有的** `.selected` 描边表达。

`features/tracker/tracker.component.html`：

```html
<g [attr.transform]="'translate(' + n.x + ', 0)'" class="sankey-node-g"
   tabindex="0" role="button"
   [attr.aria-label]="n.label + ' ' + n.count"
   (click)="selectFlowStage(n.status)"
   (keydown.enter)="selectFlowStage(n.status)"
   (keydown.space)="selectFlowStage(n.status); $event.preventDefault()">
```

`features/tracker/tracker.component.scss`：

```scss
.sankey-node-g:focus-visible { outline: none; }
.sankey-node-g:focus-visible .sankey-node { stroke: var(--brand); stroke-width: 3.5; }
```

`outline: none` 在此处是**刻意保留**且**由定稿授权**——定稿要求「Focus is shown by giving `.sankey-node-g:focus-visible .sankey-node` the **existing** `.selected` stroke treatment (`stroke: var(--brand)`, width 3.5)」。SVG `<g>` 上的 `outline` 也不会跟随节点形状绘制，故焦点改由 stroke 表达，与鼠标选中态读作同一状态。

Space 键额外 `preventDefault()`，避免空格滚动页面。

### 3.4 剩余 `outline: none` 普查（usage trace 分类）

定稿 C-2 验证条款：「the four converted sites no longer declare it on `:focus`; every remaining app-wide hit sits in AI Practice feature files or in `app/shared/material-tree` (renders only on AI Practice) — the named out-of-scope set, not an exemption.」

全仓 `outline: none` 剩余命中，按文件分类：

| 文件 | 行数 | 归属 | 判定 |
|---|---|---|---|
| `features/ai-practice/material-transfer-dialog.component.scss` | 185, 300 | AI Practice feature 文件 | ✅ 定稿点名的 out-of-scope 集 |
| `features/ai-practice/ai-practice.component.scss` | 373, 1951, 2718, 2766 | AI Practice feature 文件 | ✅ 同上 |
| `features/ai-practice/category-manager-dialog.component.ts` | 182, 212, 228 | AI Practice feature 文件 | ✅ 同上 |
| `shared/material-tree/material-tree.component.scss` | 100, 303 | `app/shared/material-tree`（仅在 AI Practice 实例化，Appendix V.7） | ✅ 同上 |

**合计 11 处，全部落在定稿点名的 out-of-scope 集内**，无一处进入豁免表。其余 grep 命中均为本次新增的**注释文本**（说明性文字），非声明。

另有 `tracker.component.scss` 的 `.sankey-node-g:focus-visible { outline: none; }` —— 这是 §3.3 按定稿刻意保留的焦点表达方式，**不属于「剩余 app-wide 命中」的违规项**：它不取消焦点可见性，只是把焦点指示从 outline 换成 SVG stroke。

---

## 4. C-3 — Hover / press unification

### 4.1 三个令牌

`web/src/styles.scss` `:root`（第 122–126 行）：

```scss
--hover-wash: rgba(46, 58, 140, 0.06);
--press-wash: rgba(46, 58, 140, 0.12);
--hover-wash-dark: rgba(255, 255, 255, 0.14);
```

对照定稿：**`--hover-wash: rgba(46, 58, 140, 0.06)`** · **`--press-wash: rgba(46, 58, 140, 0.12)`** · **`--hover-wash-dark: rgba(255, 255, 255, 0.14)`** —— 逐字一致。定稿注明前两者是「brand at alpha —— alpha derivatives under R-2's whitelist rule, not new colors」，后者是顶栏 `.nav-item:hover` 的**既有值**、tokenized unchanged。

### 4.2 两种合法悬停形态

定稿 C-3 变更 2：**(1) the wash** —— clickable rows and items 取 `background: var(--hover-wash)`；顶栏 nav（midnight 底）取 `var(--hover-wash-dark)`；**(2) the lift** —— Playbook `.entry` 卡片**按原样保留**（shadow + `translateY(-2px)`），其 transition 由 C-1 token 化。

| 定稿点名的行 | 原始值 | 改为 | 判定 |
|---|---|---|---|
| Tracker `.list-row` | `rgba(0,0,0,0.04)` 洗色 | `var(--hover-wash)` | ✅ |
| Tracker `.flow-strip-row` | black-alpha 洗色 | `var(--hover-wash)` | ✅ |
| Tracker `.metric.goal`（weekly-goal） | `var(--signal-tint)` | `var(--hover-wash)` | ✅ |
| Tracker `.board-nav` | 各自洗色 | `var(--hover-wash)` | ✅ |
| Playbook `.upcoming-row` | `#f5f8ff`（hex） | `var(--hover-wash)` | ✅ |
| Playbook `.view-link` | `rgba(46,58,140,0.08)` | `var(--hover-wash)` | ✅ |
| Playbook `.entry` | shadow + `translateY(-2px)` | **原样保留**，transition 改 `var(--dur) var(--ease)` | ✅ |
| Playbook detail `.nav-item` | `#e4e7f5` | `var(--hover-wash)`（保留 border-color 线索） | ✅ |
| Playbook detail `.cell` | `color-mix(...)` | `var(--hover-wash)` | ✅ |
| Tech Stack `.list-item` | 各自洗色 | `var(--hover-wash)` | ✅ |
| Tech Stack `.manage-btn` | 各自洗色 | `var(--hover-wash)` | ✅ |
| Category manager `.cat-row` / `.cat-name` | 各自洗色 | `var(--hover-wash)` | ✅ |
| Calendar `.cal-chip` | `var(--signal-soft)` | `var(--hover-wash)` | ✅ |
| 抽屉 `.hdr-btn` / `.hdr-close` | 各自洗色 | `var(--hover-wash)` | ✅ |
| 侧栏 `.side-item`（**白**底侧栏） | `rgba(0,0,0,0.05)` | `var(--hover-wash)` | ✅ 定稿：「a **white** sidebar, `.side-item`, whose items take the wash as-is」 |
| 顶栏 `.nav-item`（midnight 顶栏） | `rgba(255,255,255,0.14)` | `var(--hover-wash-dark)` | ✅ 定稿：「the topbar's nav items on the midnight bar take `var(--hover-wash-dark)` instead」 |

定稿特意纠正过的两处（R3 的更正）已守住：**侧栏不是深色壳**，深色面是顶栏；`.upcoming-row` 的原值是 hex 而非 black-alpha。

### 4.3 删除扁平体系违规的阴影

定稿 C-3 变更 3：「Tracker `.list-row:hover`'s `box-shadow: 0 2px 10px rgba(16, 24, 40, 0.07)` is deleted」。

实现：`tracker.component.scss` 的 `.list-row:hover` 删掉该 `box-shadow`，改 `background: var(--hover-wash)`。活体实测受审行 hover 时 `box-shadow = none`（§6.3）。

### 4.4 按压态

定稿 C-3 变更 4：「elements taking the wash take `background: var(--press-wash)` on `:active`」。

实现：所有取洗色的行/项补 `:active { background: var(--press-wash); }`。

活体实测：`.list-row` 真按压（`mouse.down()` 按住不放）读出 `rgba(46, 58, 140, 0.12)` ✅。

### 4.5 明确不动的东西

定稿把「Everything else maps to the wash」限定在受审面的 clickable rows and items 之内。以下**不属于**该集合，保持原样：

- 破坏性操作 `.del-btn`（红色语义）
- 链接 `.more-link`（下划线语义）
- 纯显隐 `.fold-head:hover { opacity: 1 }`
- 滚动条 thumb
- 拖拽 handle `.app-card:active { cursor: grabbing }`（定稿普查已点名它是受审面唯一的 `:active` 实例，且它是光标不是按压态）

---

## 5. C-4 — State choreography: one orchestrated moment

### 5.1 唯一 keyframe + 工具类

定稿 C-4 变更 1 给出的原文：

```scss
@keyframes contentIn { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; transform: none; } }
.content-in { animation: contentIn var(--dur) var(--ease); }
```

`web/src/styles.scss` 第 561–567 行逐字落地。

### 5.2 六个面各挂一次

定稿要求：「applied **once** to the primary content container of Dashboard / Tracker / Playbook list / Playbook detail / Tech Stack / Calendar」。

| 面 | 模板根元素 | 判定 |
|---|---|---|
| Dashboard | `dashboard.component.html:1` → `<div class="page content-in">` | ✅ |
| Tracker | `tracker.component.html:1` → `<div class="page content-in">` | ✅ |
| Playbook list | `playbook.component.html:1` → `<div class="page content-in">` | ✅ |
| Playbook detail | `playbook-detail.component.html:1` → `<div class="page content-in">` | ✅ |
| Tech Stack | `techstack.component.html:1` → `<div class="page content-in">` | ✅ |
| Calendar | `playbook-calendar.component.html:1` → `<div class="cal-page content-in">` | ✅ |

各面**只有一处**挂载（不是每卡一次、不是每区块一次）。

### 5.3 装载 → 内容的单一时刻

定稿变更 2：「the existing progress bars remain the loading state（no skeleton system is introduced）」，且「The empty states this plan builds (R-6's card, R-10's invitation) render inside the content-in container, so loading → empty and loading → content are the same single moment, not two pops.」

实现：R-6 的零态卡与 R-10 的邀请行本来就在各面根容器内，`.content-in` 挂根容器即自动覆盖空态与内容态两条路径；未引入任何 skeleton。

### 5.4 R-9 轨道动效

定稿变更 3：「R-9's rounds-track expansion animates with `var(--dur)` / `var(--ease)`（motion answering a user's action）；the Dashboard band takes the C-3 wash on its node links and no other motion.」

实现：
- `playbook-detail.component.scss` 的 `.rtrack .rail.fill` 加 `transition: height var(--dur) var(--ease);`（轨道填充随当前轮推进）
- 当前轮的 `<app-round-card>` 包一层 `.rexpand`，样式 `animation: contentIn var(--dur) var(--ease);`
- Dashboard band 的节点链接只取 C-3 洗色，**无其他动效**（R-9 的实现里 band 节点本就是普通 `<a>`，只加 hover/active 洗色）

### 5.5 明确拒绝的项

定稿写明「**Explicitly refused:** per-section staggers, fade-up-on-scroll, entrance motion on dialogs beyond what Material ships」。实现**一条都没做**：无逐区块错峰、无滚动淡入、无对话框额外出场动画。

### 5.6 keyframes 普查

定稿验证条款：「exactly **one** new keyframe (`contentIn`) beyond the existing six declarations（five unique names — `recPulse` ×2, `recHalo`, `resIn`, `apToastIn`, `drawerSlideIn`）」。

全仓 `@keyframes` 实测：

| 文件 | 行 | 名称 | 归属 |
|---|---|---|---|
| `features/ai-practice/ai-practice.component.scss` | 874 | `recPulse` | 既有 |
| `features/ai-practice/ai-practice.component.scss` | 885 | `recHalo` | 既有 |
| `features/ai-practice/ai-practice.component.scss` | 1804 | `resIn` | 既有 |
| `features/ai-practice/ai-practice.component.scss` | 2258 | `apToastIn` | 既有 |
| `features/ai-practice/ai-practice.component.scss` | 3024 | `recPulse`（第二处声明） | 既有 |
| `features/ai-practice/dictionary-card.component.ts` | 369 | `dict-pulse` | 既有（内联样式表） |
| `styles.scss` | 538 | `drawerSlideIn` | 既有 |
| `styles.scss` | 561 | **`contentIn`** | **本次唯一新增** ✅ |

声明数 8（含 `dict-pulse` 这条定稿未列举的内联 keyframe），本次新增恰好一个 `contentIn`，无第二个新入场动画。

---

## 6. 活体验证（Playwright 探针 `web/tools/census-craft-r9c.mjs`）

### 6.1 C-1 静态普查

```
=== C-1 静态普查:受审面 transition 的字面量时长应为 0 ===
  ✅ 受审面 transition 不带字面量时长 —— 扫了 53 个文件
```

### 6.2 C-1 活体：reduced vs no-preference

```
    · .list-row  duration=0.16s  timing=cubic-bezier(0.2, 0, 0, 1)
    · .nav-item  duration=0.12s, 0.12s  timing=cubic-bezier(0.2, 0, 0, 1), ...
  ✅ no-preference 下受审元素的 transition-duration 全部是令牌值 —— 实读 .list-row:0.16s | .nav-item:0.12s, 0.12s
  ✅ reduce 下受审元素的 transition-duration 被压到 ≤ 0.01ms
  ✅ reduce 下 .content-in 的 animation-duration 被压到 ≤ 0.01ms —— 实读 contentIn/1e-05s
```

### 6.3 C-2 / C-3 活体

```
=== C-2 静态普查:四个站点的 outline:none 应已消失 ===
  ✅ ... .plain-area 保留 border 线索(self)且补上共享焦点环
  ✅ ... .paste 保留 border 线索(parent)且补上共享焦点环
  ✅ ... .blk-editor ...
  ✅ ... .doc-editor ...

=== C-4 活体:content-in 的 animation-name ===
  ✅ /dashboard ... /tracker ... /playbook ... /techstack 全部 animation-name = contentIn —— 实读 contentIn/0.16s/ease

=== C-2 活体:Sankey 节点键盘可达 + Enter 切换阶段 ===
  ✅ 流程视图里找得到 Sankey 节点组
  ✅ Sankey 节点组有 tabindex="0" / role="button" / aria-label("Saved 3")
  ✅ Sankey 节点组可以被聚焦
  ✅ Sankey 焦点用既有的 .selected 描边表达 —— 实读 rgb(46, 58, 140) / 3.5px
  ✅ Enter 键切换了 Sankey 阶段 —— before="Favorited applications No favorited appl" after.selected=1

=== C-3 活体:hover 洗色 / 行无阴影 / active 按压洗色 ===
  ✅ .list-row:hover 的 background = rgba(46, 58, 140, 0.06)
  ✅ .nav-item:hover 的 background = rgba(255, 255, 255, 0.14)
  ✅ 受审行 hover 时 box-shadow = none
  :active 真按压实测 = rgba(46, 58, 140, 0.12)
  ✅ .list-row:active 的 background = rgba(46, 58, 140, 0.12)
```

### 6.4 与 R-8 / R-9 探针共存

§C 改动后重跑 R-8 与 R-9 活体探针，**仍全过**：

- `✅ R-9 几何一致性:四个面共用同一套值`
- `✅ R-8 渲染字体契约全部达标`

即 §C 的令牌化与动效没有破坏 R-9 的共享几何源，也没有破坏 R-8 的字标字体契约。

---

## 7. 偏差清单

**空。**

逐项核对：C-1 令牌四值一致、17 条转换映射正确、reduced-motion 块逐字照抄；C-2 去掉两处强制圆角、四个站点按名转换、Sankey 键盘化完整；C-3 三令牌逐字一致、两种悬停形态守住、违规阴影已删、按压态到位；C-4 唯一 keyframe、六个面各挂一次、轨道动效到位、无任何明令拒绝项。

**两处需要判断的地方（均已按「不改行为」处理并记此备查，非偏差）**：

1. `playbook-calendar.component.scss:73` 与 `techstack.component.scss:418` 原本写 `background 0.15s`（无 easing 关键字）—— 只 token 化时长，保留 CSS 默认 `ease` 语义，不强加 `var(--ease)`。
2. `.sankey-node-g:focus-visible { outline: none; }` 是定稿 C-2 变更 3 明写的焦点表达方式（焦点由 SVG stroke 承担），不属于违规的「剩余 outline:none」。

---

## 8. 探针自身的四处修正记录

| # | 症状 | 根因 | 修正 |
|---|---|---|---|
| 1 | 首跑报 `ENOENT /workspace/your-interview/src/app/...` | 探针 `ROOT` 少拼了 `web/` | `ROOT` 改为仓库根 + `/web/` |
| 2 | 首跑报三处「缺 `:focus-visible` 焦点环」 | 探针正则 `(:focus\|:focus-visible)?` 的 `:focus` 分支把紧邻的 `:focus-visible` 块整块吃掉 | 改为分别抽出 `:focus` 块与 `:focus-visible` 块 |
| 3 | 首跑报 `transition-duration` 「不是令牌值」 | `transition-duration` 可为逗号分隔多值（`.nav-item` = `0.12s, 0.12s`），整串比对误判 | 改为逐值比对 |
| 4 | 首跑报「流程视图里找得到 Sankey 节点组」未命中 | 视图切换按钮**只有图标无文字**（`mat-icon account_tree`），按 textContent 找不到 | 改为按 `.view-btn` 的图标内容定位 |
| 5 | 次跑报「`.paste` 丢了 border-color 线索」 | `.paste` 自身 `border: none`，线索在**父容器** `.paste-box:focus-within`（未动它） | 探针加 `cue: 'parent'` 分类，接受父容器线索 |
| 6 | `:active` 用 class 模拟 | 模拟测不到 `:active` 规则本身 | 改为 `mouse.down()` 按住不放再读 computed，实测 `rgba(46,58,140,0.12)` |

**六次红全部是探针自身缺陷**，无一次是实现的偏差；每一次的归属都已查清并记此，未出现「改实现去迁就探针」的情况。

---

## 9. 自验结果

见台账「本票实施进度」表 §C 行的测试计数（tsc / Jest / ng build / check-templates / 三个活体探针）。
