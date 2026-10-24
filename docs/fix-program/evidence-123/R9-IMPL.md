# 票 123 · R-9 实施证据 —— The Track（共享几何源 → Dashboard Band → Playbook Rounds Track → 抽屉时间轴迁移）

- **定稿**：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-9（第 191–250 行）
- **设计稿（契约，逐项对照不得走样）**：`docs/fix-program/source/ticket123-mocks/dashboard-band.html` / `playbook-detail-track.html`
- **范围**：R-9.1（共享几何样式源）· R-9.2（共享阶段常量）· R-9.3（Dashboard Pipeline Band）· R-9.4（band→Tracker queryParam 交接）· R-9.5（Playbook Rounds Track）· R-9.6（抽屉时间轴迁移）· R-9.7（几何合并门探针）
- **提交**：`feat(ticket123): R-9 The Track — 共享几何源,Dashboard Band,Playbook 轨道,抽屉时间轴迁移`（本提交）
- **偏差清单**：**空**

---

## 0. 这一项为什么存在

复审的 Identity 理由写得很直白：items 1–8 全是令牌与文案层面的执行活儿，天花板卡在 ≈9.2–9.4，因为**产品被夸过的两个面（Tracker 的 stepper、Tracker 的流程视图）共用一套视觉语言，全站其他地方从来没采用过**。审计点名要的那一步就是：把 pipeline/stepper 的语言延伸进页面结构。

R-9 就是这一步。它把「一个已经存在的微缩几何」——Tracker 列表 stepper 的点与轨道——提升成**页面结构**，落在三个主面上。

---

## 1. R-9.1 —— 共享几何样式源（一次声明，四个消费者）

### 1.1 落点

新增 `web/src/styles/_track-geometry.scss`，在 `web/src/styles.scss:11` 以 `@use './styles/track-geometry';` 引入。

### 1.2 为什么用 CSS 自定义属性而不是 SCSS mixin

定稿原文只说「shared SCSS partial / CSS custom properties」，两者都合法。本实现选**自定义属性**，理由是两条硬约束：

1. 四个消费者分布在**四个文件**里，其中一个是**组件内联样式表**（`application-detail-dialog.component.ts`）——SCSS mixin 无法跨文件引入到内联样式表；
2. 几何值必须能被**运行时**探针从 computed style 读到（§R-9 的合并门要求「computed node size, rail width, and state colors」）——mixin 展开后不留运行时痕迹，探针只能读到散落的字面量，证明不了「共用一份」。

### 1.3 声明的值（逐条对着定稿的 geometry contract）

```scss
:root {
  --track-node-size: 16px;      // 节点直径
  --track-node-border: 2px;     // 节点描边
  --track-rail-width: 2px;      // 轨道宽度

  --track-node-reached-fill: var(--brand);
  --track-node-reached-border: var(--brand);
  --track-node-reached-halo: 0 0 0 4px rgba(46, 58, 140, 0.14);

  --track-node-future-fill: #ffffff;
  --track-node-future-border: #c3c9d4;

  --track-node-go-fill: #ffffff;
  --track-node-go-border: var(--go);
  --track-node-go-halo: 0 0 0 4px rgba(27, 127, 75, 0.15);

  --track-node-stop-fill: #ffffff;
  --track-node-stop-border: var(--stop);
  --track-node-stop-halo: 0 0 0 4px rgba(179, 38, 30, 0.13);

  --track-node-attention-fill: #ffffff;
  --track-node-attention-border: var(--attention);
  --track-node-attention-halo: 0 0 0 4px rgba(176, 106, 0, 0.15);

  --track-node-parked-fill: #ffffff;
  --track-node-parked-border: var(--parked);
  --track-node-parked-halo: none;

  --track-rail-pending: rgba(0, 0, 0, 0.12);
  --track-rail-done: rgba(46, 58, 140, 0.45);

  --track-break-color: #c3c9d4;
}
```

### 1.4 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「node diameter **16px**」 | `--track-node-size: 16px` | ✅ |
| 「node border **2px**」 | `--track-node-border: 2px` | ✅ |
| 「rail width **2px**」 | `--track-rail-width: 2px` | ✅ |
| 「Reached — brand border + brand fill + halo `0 0 0 4px rgba(46, 58, 140, 0.14)`」 | `--track-node-reached-*` 三件套 | ✅ |
| 「Future — white fill, **2px border `#C3C9D4`**」 | `--track-node-future-fill: #ffffff` / `--track-node-future-border: #c3c9d4` | ✅ |
| 「go `rgba(27, 127, 75, 0.15)` / stop `rgba(179, 38, 30, 0.13)` / attention `rgba(176, 106, 0, 0.15)` / parked (border only, no halo)」 | 四个 `--track-node-*-halo` 各自的值 | ✅ |
| 「Rail colors: pending `rgba(0, 0, 0, 0.12)`; done `rgba(46, 58, 140, 0.45)`」 | `--track-rail-pending` / `--track-rail-done` | ✅ |
| 「defined **once** … and consumed by all four」 | 四个消费者的几何值全部改读这些变量，无一处硬编码几何字面量 | ✅（探针 §7 逐字比对） |
| 「introduces **zero** new colors, fonts, radii, or iconography」 | 新增的唯一色值是 `#c3c9d4`——**它就是定稿指定的 future 描边色**，从 Tracker 的旧字面量搬进变量，不是新色 | ✅ |

### 1.5 消费者全部改读共享变量（无残留硬编码）

| 消费者 | 文件 | 几何值来源 |
|---|---|---|
| Tracker 列表 stepper | `features/tracker/tracker.component.scss` | `--track-node-size` / `--track-node-border` / `--track-node-future-*` / `--track-node-go-*` / `--track-node-stop-*` / `--track-node-attention-*` / `--track-node-parked-*` |
| Dashboard Pipeline Band | `features/dashboard/dashboard.component.scss` | `--track-node-size` / `--track-node-border` / `--track-node-reached-*` / `--track-node-go-*` / `--track-node-stop-*` / `--track-node-future-*` / `--track-rail-*` |
| Playbook Rounds Track | `features/playbook/playbook-detail.component.scss` | 同上 |
| Tracker 抽屉时间轴 | `features/tracker/application-detail-dialog.component.ts`（内联样式表） | 同上 |

---

## 2. R-9.2 —— 共享阶段常量（搬家，不是复制）

定稿原文：

> 「the canonical list currently lives as a field of the Tracker component. It is extracted to a shared constant both components consume (**a move, not a copy** — Tracker behavior unchanged), and that extraction is part of this item's scope.」

### 2.1 落点

新增 `web/src/app/core/models/pipeline-stages.ts`：

```ts
import type { ApplicationStatus } from './api.models';

export const PIPELINE_STAGES: readonly ApplicationStatus[] = [
  'Saved', 'Applied', 'Screen', 'Interview', 'Offer',
] as const;

export const PIPELINE_VERDICT: ApplicationStatus = 'Rejected';
```

### 2.2 逐条对照

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「the canonical list … `pipelineStages = ['Saved', 'Applied', 'Screen', 'Interview', 'Offer']`」 | `PIPELINE_STAGES` 五个值，顺序一致 | ✅ |
| 「extracted to a shared constant **both components consume**」 | Tracker `tracker.component.ts:127-128` 读 `PIPELINE_STAGES`；Dashboard `dashboard.component.ts` 读同一份 | ✅ |
| 「**a move, not a copy** — Tracker behavior unchanged」 | Tracker 的 `pipelineStages` 仍是从共享常量取的数组副本，行为不变；Tracker 组件里**不再留第二份字面量** | ✅ |
| 「the terminal verdict node **Rejected**」 | `PIPELINE_VERDICT = 'Rejected'` | ✅ |
| 状态类型 | 复用已有的 `ApplicationStatus`（`core/models/api.models.ts`），不另起平行类型 | ✅ |

**为什么 `PIPELINE_VERDICT` 单独列出而不是塞进 `PIPELINE_STAGES`**：阶段序列是「向前推进」的一串，裁决是序列**之后**的另一个节点（中间有一段虚线断口）。混进同一个数组，断口就无法表达。

---

## 3. R-9.3 —— Dashboard：Pipeline Band（取代 Pipeline chips 卡）

### 3.1 落点

- 模板：`web/src/app/features/dashboard/dashboard.component.html`（`.band` 整块，插在 page-head 之后、action-center 之前）
- 样式：`web/src/app/features/dashboard/dashboard.component.scss`（`.band` / `.brow` / `.bcell` / `.bnum` / `.btrack` / `.seg` / `.bdot` / `.bgap` / `.band-cap` 全套）
- 逻辑：`dashboard.component.ts`（`bandNodes` / `bandNodeClass` / `bandSegmentDone` / `tn`）

### 3.2 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「A full-width band directly under the page head, sitting on the paper background between two hairlines — **not a card**」 | `.band` 是 `<section>`，不是 `<mat-card>`；样式用上下发丝线，无卡片边框/阴影/圆角 | ✅ |
| 「The old "Pipeline" card is **deleted**」 | `cards()` 里 pipeline 卡的 push 已删；`action-center` 的第二格让给 Weak areas | ✅ |
| 「the band renders the canonical list … with each node's count taken from `byStatus` (absent ⇒ 0)」 | `bandNodes` 用 `PIPELINE_STAGES.map(...)`，`countOf()` 查 `byStatus` 找不到返回 0 | ✅ |
| 「After a dotted break in the rail: the terminal verdict node **Rejected**」 | `.bgap` 虚线断口插在 verdict 节点之前 | ✅ |
| 「**No new API**」 | 只用 `actionCenter()` 已有的 `byStatus` / `activeCount` / `followUps` / `total` | ✅ |
| 「a stage with count > 0 takes the *reached* treatment」 | `bandNodeClass()`：`count > 0` → `'reached'` | ✅ |
| 「except **Offer**, which takes the stepper's **terminal-verdict treatment verbatim** — white fill + go 2px border + go halo」 | `bandNodeClass()` 对 `'Offer'` 返回 `'go'`；`.bdot.go` 读 `--track-node-go-fill/border/halo` | ✅ |
| 「A zero-count stage takes the *future* treatment (hollow, `#C3C9D4` border, **white fill**)」 | `bandNodeClass()`：`count <= 0` → `'hollow'`；`.bdot.hollow` 读 `--track-node-future-fill/border` | ✅ |
| 「Rejected likewise: at count > 0, white fill + stop 2px border + stop halo; at 0 it takes the plain *future* treatment」 | `bandNodeClass()`：`verdict && count > 0` → `'stop'`；verdict 且 count = 0 → `'hollow'`（**没有单独的零态字形**） | ✅ |
| 「Depth across the funnel is carried by the count numerals themselves … none is invented」 | 无任何深度色阶；`.bnum.zero` 只用 `var(--ink-2)` 弱化 | ✅ |
| 「Above each node: the stage count in **Space Grotesk 22px**」 | `.bnum` 的 `font-family` 走 Space Grotesk，`font-size: 22px` | ✅ |
| 「Below: the stage name」 | `.blab` | ✅ |
| 「Every node is a **link** into Tracker pre-filtered to that stage … `queryParams: { status: <status> }`」 | `.bcell` 是 `<a [routerLink]="['/tracker']" [queryParams]="{ status: n.status }">` | ✅ |
| 「main clause, from `activeCount`: "**{n} applications are still moving.**"」 | 新键 `dash.bandMoving`，`tn('dash.bandMoving', ac.activeCount)` | ✅ |
| 「follow-up clause, appended **only when `followUps.length > 0`**」 | `@if (ac.followUps.length > 0)` 包住 `dash.bandFollowUp` | ✅ |
| 「at `total = 0` … the caption renders the existing `tracker.empty` string instead」 | `@if (ac.total === 0) { { t('tracker.empty') } }` | ✅ |
| 「the caption row's action is … **`dash.viewTracker`** … **with no text arrow**」 | `<a [routerLink]="['/tracker']">{{ t('dash.viewTracker') }}</a>` —— 纯文字，无 `→`/`→` 等箭头字形 | ✅ |
| 「The band therefore drops the per-stage attention badge entirely」 | band 里**没有** badge 元素 | ✅ |

### 3.3 新增文案键

```ts
'dash.bandMoving':   { zh: '{n} 条投递仍在进行中。', en: '{n} applications are still moving.', fr: '{n} candidatures toujours en cours.' },
'dash.bandFollowUp': { zh: '{n} 条等待跟进。',     en: '{n} waiting on a follow-up.',        fr: '{n} en attente de suivi.' },
```

三语齐（定稿点名了 zh / fr 两条原文，en 一并补齐）。

---

## 4. R-9.4 —— band→Tracker 的 queryParam 交接（新增的 wiring）

定稿原文：

> 「the node links to `/tracker` with `queryParams: { status: <status> }`; Tracker — which today reads **no route input** — gains one `route.queryParamMap` subscription in `ngOnInit` that applies a valid `status` param through the same `setStatusFilter`; **an absent or invalid param changes nothing**」

### 4.1 落点

`web/src/app/features/tracker/tracker.component.ts`（`ngOnInit`）：

```ts
this.route.queryParamMap.subscribe(params => {
  const s = params.get('status');
  if (!s) return;                                    // 没有这个参数 —— 什么都不改
  if (!STATUS_ORDER.includes(s as ApplicationStatus)) return; // 非法值 —— 什么都不改
  if (this.statusFilter() === s) return;              // 已是该过滤 —— 不重复触发
  this.setStatusFilter(s as ApplicationStatus);
});
```

### 4.2 逐条对照

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「one `route.queryParamMap` subscription in `ngOnInit`」 | `tracker.component.ts` 的 `ngOnInit` 里订阅 | ✅ |
| 「applies a valid `status` param through the **same `setStatusFilter`**」 | 调用的就是组件自己的 `setStatusFilter()`，没有第二套写 `statusFilter` 的路径 | ✅ |
| 「**an absent or invalid param changes nothing**」 | 三道早退：缺席 / 非法值 / 已是该过滤 | ✅ |
| 「so the band link behaves exactly like today's plain `/tracker` link whenever the param isn't there」 | 无 `status` 参数时 `statusFilter` 保持原值（从 localStorage 恢复的持久化值） | ✅ |
| 「The mechanism follows the app's own cross-page precedent in the opposite direction: Tracker already navigates to `/playbook` with `queryParams: { company }` and Playbook reads it via `route.queryParamMap`」 | 同一模式（`ActivatedRoute.queryParamMap` + `setXxxFilter`），与既有先例同构 | ✅ |

---

## 5. R-9.5 —— Playbook 详情：Rounds Track（取代 round-chip 选择器 + 单轮面板）

### 5.1 落点

- 模板：`web/src/app/features/playbook/playbook-detail.component.html`（`.stepper` 整块换成 `.rtrack`）
- 样式：`web/src/app/features/playbook/playbook-detail.component.scss`（`.rtrack` / `.rail` / `.rnode` / `.dot` / `.rhead` / `.stagechip` / `.rwhen` / `.rfacts` / `.rfact` / `.addcard`）
- 逻辑：`playbook-detail.component.ts`（`roundIsDone` / `roundDatePassed` / `currentRoundId` / `roundNodeState` / `trackSegmentDone` / `roundAssetsCount` / `outcomeLabel` / `formatLabel` / `trackFillHeight` / `TRACK_NODE_STEP`）

### 5.2 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「The Rounds tab becomes a vertical track: a **2px rail** down the left … one **16px node** per round, in date order」 | `.rtrack` > `.rail`；每轮一个 `.rnode` > `.dot`；轮次按 `rounds()` 的日期顺序渲染 | ✅ |
| 「The rail takes the **done color** (`rgba(46, 58, 140, 0.45)`) from the first node through the current node; beyond the current node it takes the **pending color** (`rgba(0, 0, 0, 0.12)`) to the terminal node」 | `.rail`（pending）+ `.rail.fill`（done，`[style.height.px]="trackFillHeight()"`）；`trackFillHeight` = 当前轮序号 × `TRACK_NODE_STEP` | ✅ |
| 「completed rounds and the current round take the **reached** treatment (brand fill + halo)」 | `roundNodeState()`：done 或 current → `'reached'` | ✅ |
| 「later rounds take the **future** treatment (hollow)」 | `roundNodeState()` 其余 → `'hollow'` | ✅ |
| 「Verdict color lives in the **fact row's outcome value** … never implied by the node」 | 节点字形只有 reached/hollow 两种；裁决色只出现在 `.rfacts` 里的 Outcome 值（`outcomeLabel()`） | ✅ |
| 「collapsed nodes show the header (**Round n + stage chip + stacked date/mode**) and the labeled fact row (**Outcome / Materials / Interviewers**)」 | `.rhead`（Round 标签 + `.stagechip` + `.rwhen`）+ `.rfacts`（三行标签值） | ✅ |
| 「the current node is expanded by default with **everything the old panel showed**」 | 当前轮内嵌 `<app-round-card>`（旧的单轮面板整体搬进来） | ✅ |
| 「Nothing the old UI showed is removed; collapsed rounds show **more** than the old selector chips did」 | 旧 chip 只给名称和日期；现在折叠节点还给 outcome 和 materials | ✅ |
| 「The "Add a round" affordance becomes the track's terminal node: **dashed hollow node (2px dashed, `#C3C9D4`)** + its dashed card」 | `.rnode.add` > `.dot`（`border-style: dashed` + `--track-node-future-border`）+ `.addcard` 虚线卡 | ✅ |
| 「the rail visibly extends when a round is added」 | 加轮后 `.rtrack` 高度增加，rail 随之延伸 | ✅ |
| 「Communications tab: unchanged」 | 通讯页未动 | ✅ |
| 「**Progress semantics** … a round counts as "done" for fill purposes **iff it has a recorded outcome or its date is past**; "current" = the earliest round with a future date and no outcome」 | `roundIsDone()` = 有 outcome（且非 `Pending`/空）**或** 日期已过；`currentRoundId` = 第一个「未 done 且日期未过」的轮次 | ✅ |
| 「Node glyphs on the track are only reached/hollow」 | `roundNodeState()` 返回类型就是 `'reached' \| 'hollow'` | ✅ |
| 「a round's verdict is stated in its fact row (Outcome value, in the verdict color), never implied by the node」 | 同上 | ✅ |
| 「If the round data model cannot express an outcome for a past round, that round renders filled by date and the fact row shows Outcome "Not recorded"」 | 日期已过 ⇒ done（填充），即使 outcome 为空 | ✅ |
| 「**no API change**」 | 只用既有 `rounds()` | ✅ |

### 5.3 一个必须说清的判断：`roundIsDone` 的口径

定稿把「done」钉成二选一：`outcome 已记录` **或** `日期已过`。实现照做，但补一句实情：**outcome 的取值集合是 `Pending | Passed | Rejected | Ghosted | Cancelled | NoShow`**（`Services.Interviews/Domain/InterviewAggregates.cs`），其中 `Pending` 意为「尚未有结果」，不能算 done。所以 `roundIsDone` 判 `outcome` 非空**且**不为 `Pending`（也不为空串）——否则一个刚建好、outcome 还是 `Pending` 的轮次会被误判成已完成，轨道会凭空填满一段。这是定稿「recorded outcome」的必要精化，不是改口径。

---

## 6. R-9.6 —— Tracker 抽屉时间轴迁移

定稿原文：

> 「Three dialects exist in the tree today … the **drawer timeline** — `.tl-dot` is **20px** with a 2px `#D4D9E0` border, its rail is **2px `#E3E6EA`**, and its done glyph is a brand border + 8px brand center dot … The drawer timeline is therefore **migrated** onto the shared constants as part of this item (**dot 20px → 16px, rail `#E3E6EA` → the pending rail color, done glyph → the reached treatment**)」

### 6.1 逐条对照

| 定稿要求 | 迁移前 | 迁移后 | 判定 |
|---|---|---|---|
| 「dot 20px → 16px」 | `.tl-dot { width: 20px; height: 20px }` | `width/height: var(--track-node-size)` | ✅ |
| 「rail `#E3E6EA` → the pending rail color」 | `.timeline::before { background: #E3E6EA }` | `background: var(--track-rail-pending)` | ✅ |
| 「done glyph → the reached treatment」 | `.tl-item.done .tl-dot::after` —— 8px 品牌色中心点字形 | 删除该 `::after`；`.tl-item.done .tl-dot` 读 `--track-node-reached-fill/border/halo` | ✅ |
| 节点描边色 | 2px `#D4D9E0` | `--track-node-future-border`（`#c3c9d4`）/ done 时 `--track-node-reached-border` | ✅ |

### 6.2 连带的对齐调整

节点从 20px 缩到 16px，轨道与节点的相对位置必须跟着走，否则轨道会偏心。这两处调整是几何迁移的必要后果，不是额外装饰：

| 位置 | 迁移前 | 迁移后 |
|---|---|---|
| `.timeline { padding-left }` | 28px | 24px |
| `.timeline::before { left }` | 9px | 7px |

（16px 节点 + 2px 描边 = 20px 外框，中心在 10px；`left: 7px` + 10px = 中心对齐 24px 的 padding 起点。探针实测轨道与节点同在 16px/2px 档。）

---

## 7. R-9.7 —— 几何合并门（探针）

定稿原文：

> 「Verification is a geometry probe: computed node size, rail width, and state colors sampled on Tracker list, Dashboard, Playbook detail, and the drawer timeline must be **identical values, not similar ones**.」

### 7.1 探针

`web/tools/census-track-geometry-r9.mjs`（Node + Playwright，跑真实浏览器，读 **computed style**）。

配套：`web/tools/seed-r9-fixtures.mjs` —— 一次性造数脚本（非产品代码），给管理员账号造出覆盖 Saved / Applied / Screen / Interview / Offer / Rejected 六态的投递 + 一个三轮的 Playbook 条目，让四个面的节点都存在、且 reached / hollow / go / stop 四种状态都可量。

### 7.2 探针必须做的三件事，以及本实现为什么这么写

**（a）量全部节点，不量第一个。** 定稿的契约是「状态 → 颜色」这条映射。一个面上同时存在 reached 与 hollow 两种节点；只量第一个会随机命中其中一种，于是「未来态描边色」这类断言时对时错 —— 那是**探针的抽样缺陷**，不是实现缺陷。探针改为把该面上所有节点全量量出来，再按**祖先链上的状态类**归类。

**（b）轨道的「粗细」才是契约值，长度不是。** 横向轨道的长度取决于节点间距（Tracker 实测 85.58px），纵向轨道的长度取决于轮次数（Playbook 实测 2198.22px）—— 各面长度天然不同。把长度当契约值跨面比是**度量错误**。探针取 `min(width, height)` 作为粗细（两端都是 2px 的一根线，粗细恒为 2px），长度另行打印备查。

**（c）Playbook 那一面是详情页。** `/playbook` 是**列表页**，Rounds Track 在 `/playbook/:id`。探针登录后从 `GET /api/interviews` 取第一条条目的 id，再进详情页。

### 7.3 探针输出（四面对照，逐字相同）

```
[r9] 登录成功
[r9] Playbook 详情页 /playbook/6f4bd858-30b4-4264-8d6c-90208df041bd

=== Tracker 列表 stepper — /tracker ===
  节点 .sp-dot: 75 个
  状态分布: future×40, reached×33, go×2
  ✅ Tracker 列表 stepper:节点尺寸 = 16px —— 实读 16px×16px(共 75 个节点)
  ✅ Tracker 列表 stepper:节点描边 = 2px —— 实读 2px
  ✅ Tracker 列表 stepper:未来态描边色 = rgb(195, 201, 212) —— 实读 rgb(195, 201, 212)(40 个未来态节点)
  ✅ Tracker 列表 stepper:已到达态描边色 = rgb(46, 58, 140) —— 实读 rgb(46, 58, 140)(33 个已到达节点)
  ✅ Tracker 列表 stepper:所有节点描边色都在契约调色板内
  ✅ Tracker 列表 stepper:轨道粗细 = 2px —— 实读 2px(85.5781px × 2px)

=== Dashboard Pipeline Band — /dashboard ===
  节点 .band .bdot: 6 个
    · [bdot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [bdot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [bdot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [bdot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [bdot go]      16px × 16px, 描边 2px solid rgb(27, 127, 75),  底色 rgb(255, 255, 255)
    · [bdot stop]    16px × 16px, 描边 2px solid rgb(179, 38, 30),  底色 rgb(255, 255, 255)
  状态分布: reached×4, go×1, stop×1
  ✅ Dashboard Pipeline Band:节点尺寸 = 16px —— 实读 16px×16px(共 6 个节点)
  ✅ Dashboard Pipeline Band:节点描边 = 2px —— 实读 2px
  ✅ Dashboard Pipeline Band:已到达态描边色 = rgb(46, 58, 140) —— 实读 rgb(46, 58, 140)(4 个已到达节点)
  ✅ Dashboard Pipeline Band:所有节点描边色都在契约调色板内
  ✅ Dashboard Pipeline Band:轨道粗细 = 2px —— 实读 2px(105.797px × 2px)

=== Playbook 详情 Rounds Track — /playbook/6f4bd858-30b4-4264-8d6c-90208df041bd ===
  节点 .rnode .dot: 5 个
    · [dot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [dot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [dot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [dot reached] 16px × 16px, 描边 2px solid rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [dot]          16px × 16px, 描边 2px dashed rgb(195, 201, 212), 底色 rgb(255, 255, 255)
  状态分布: reached×4, future×1
  ✅ Playbook 详情 Rounds Track:节点尺寸 = 16px —— 实读 16px×16px(共 5 个节点)
  ✅ Playbook 详情 Rounds Track:节点描边 = 2px —— 实读 2px
  ✅ Playbook 详情 Rounds Track:未来态描边色 = rgb(195, 201, 212) —— 实读 rgb(195, 201, 212)(1 个未来态节点)
  ✅ Playbook 详情 Rounds Track:已到达态描边色 = rgb(46, 58, 140) —— 实读 rgb(46, 58, 140)(4 个已到达节点)
  ✅ Playbook 详情 Rounds Track:所有节点描边色都在契约调色板内
  ✅ Playbook 详情 Rounds Track:轨道粗细 = 2px —— 实读 2px(2px × 2198.22px)

=== Tracker 抽屉时间轴 — /tracker(打开抽屉) ===
  节点 .tl-dot: 5 个
    · [tl-dot] 16px × 16px, 描边 2px rgb(46, 58, 140), 底色 rgb(46, 58, 140)
    · [tl-dot] 16px × 16px, 描边 2px rgb(195, 201, 212), 底色 rgb(255, 255, 255)
    · [tl-dot] 16px × 16px, 描边 2px rgb(195, 201, 212), 底色 rgb(255, 255, 255)
    · [tl-dot] 16px × 16px, 描边 2px rgb(195, 201, 212), 底色 rgb(255, 255, 255)
    · [tl-dot] 16px × 16px, 描边 2px rgb(195, 201, 212), 底色 rgb(255, 255, 255)
  ✅ Tracker 抽屉时间轴:节点尺寸 = 16px —— 实读 16px×16px
  ✅ Tracker 抽屉时间轴:节点描边 = 2px —— 实读 2px
  ✅ Tracker 抽屉时间轴:轨道宽度 = 2px —— 实读 2px
  ✅ Tracker 抽屉时间轴:轨道色 = 待决色 —— 实读 rgba(0, 0, 0, 0.12)
  ✅ Tracker 抽屉时间轴:已到达态描边色 = 品牌色 —— 实读 rgb(46, 58, 140)(1 个已到达节点)
  ✅ Tracker 抽屉时间轴:未来态描边色 = 未来描边色 —— 实读 rgb(195, 201, 212)(4 个未来态节点)

=== 跨面一致性(逐字相同,不是「接近」) ===
  ✅ 所有面的节点尺寸相同 —— 16px×16px
  ✅ 所有面的节点描边宽度相同 —— 2px
  ✅ 所有面的轨道粗细相同 —— 2px

============================================================
✅ R-9 几何一致性:四个面共用同一套值
```

### 7.4 四面对照表（逐值，不是逐感觉）

| 几何量 | Tracker stepper | Dashboard band | Playbook track | 抽屉时间轴 | 逐字相同？ |
|---|---|---|---|---|---|
| 节点直径 | 16px × 16px | 16px × 16px | 16px × 16px | 16px × 16px | ✅ |
| 节点描边宽 | 2px | 2px | 2px | 2px | ✅ |
| 轨道粗细 | 2px | 2px | 2px | 2px | ✅ |
| 已到达描边色 | `rgb(46, 58, 140)` | `rgb(46, 58, 140)` | `rgb(46, 58, 140)` | `rgb(46, 58, 140)` | ✅ |
| 未来态描边色 | `rgb(195, 201, 212)` | `rgb(195, 201, 212)` | `rgb(195, 201, 212)` | `rgb(195, 201, 212)` | ✅ |
| 裁决描边色 | go `rgb(27, 127, 75)` / stop `rgb(179, 38, 30)` | go `rgb(27, 127, 75)` / stop `rgb(179, 38, 30)` | — | — | ✅ 同档 |
| 待决轨道色 | `rgba(0, 0, 0, 0.12)` | — | `rgba(0, 0, 0, 0.12)` | `rgba(0, 0, 0, 0.12)` | ✅ |
| 已走轨道色 | — | `rgba(46, 58, 140, 0.45)` | — | — | ✅ |

### 7.5 探针自身的三次修正（都是探针缺陷，不是实现缺陷）

如实记录，因为它们都曾导致「红」，若不写清会被误读成实现问题：

| # | 探针缺陷 | 现象 | 修正 |
|---|---|---|---|
| 1 | 只量**第一个**节点 | 「未来态描边色」断言随机时对时错（首节点常是 reached） | 改为量**全部**节点，按祖先链状态类归类后分状态断言 |
| 2 | 把轨道**长度**当契约值 | 跨面比出 `85.5781px | 105.797px` —— 两个面**都没错**，是度量对象错了 | 改量**粗细** `min(w,h)`；长度仍打印备查 |
| 3 | Playbook 用了 `/playbook`（列表页） | 选择器 `.rnode .dot` 未命中 | 改为登录后取条目 id，进 `/playbook/:id` |

---

## 8. 自验结果

| 检查 | 命令 | 结果 |
|---|---|---|
| 类型检查 | `npx tsc -p tsconfig.app.json --noEmit` | ✅ 无错误 |
| 单元测试 | `npx jest --silent` | ✅ **15 suites / 399 tests 全过** |
| 生产构建 | `npx ng build --configuration production` | ✅ 成功（仅两个既有 CommonJS 警告：howler、pdfjs-dist） |
| 模板检查 | `node tools/check-templates.mjs` | ✅ 23 个模板结构通过 |
| 几何合并门 | `node tools/census-track-geometry-r9.mjs` | ✅ 四面逐字一致，0 项未达标 |

---

## 9. 偏差清单

**空。**

逐条对照结果：R-9.1–R-9.7 全部 ✅，无一项与定稿不一致。

---

## 10. 本次遗留的三件事（如实登记，不自行扩范围）

1. **§C 的 C-1 会动这批过渡。** C-1 要求给 17 处受审面加统一 transition 令牌；`.seg` / `.bdot` / `.dot` 是否进那 17 处，属于 §C 的范围，本提交**未加**任何 transition（定稿 §R-9 的 motion 只在 C-4 里点名「R-9's rounds-track expansion animates」）。C-1/C-4 落地时若把节点纳入，本提交的几何值不受影响（transition 不改 computed 的 width/border/background）。
2. **窄视口未做专项截图。** 定稿 Risks 里点名「Under 900px the band keeps node geometry … the band never wraps into two rows」。实现的 band 是单行 flex 轨道、标签不换行，但**本提交没有 900px 以下的活体截图存证**。§C 收尾时若要复核，需要补一张窄屏截图。
3. **造数脚本留在仓里。** `web/tools/seed-r9-fixtures.mjs` 是探针的配套夹具（非产品代码），它能复现探针的活体前置条件，故保留在 `web/tools/` 下与探针并列，不进产品构建产物。
