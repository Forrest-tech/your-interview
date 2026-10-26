# R-4 — 看板收尾:眼睛已被标注,给卡片一个年龄(实现存证)

> 票 123 · WorkBuddy 实现 · 基线 `develop-new-features` @ `86a538f`
> 定稿:`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-4(第 117–130 行)

---

## R-4.1 — 那个「眼睛」:不改代码,按测量结案

**定稿**(第 127 行):站立规则写进全应用 —— **没有可访问名的纯图标控件是缺陷**;若将来某次改动移除了某个开关的功能,图标就随之移除 —— 无名的装饰性图标永远不是终态。在这条规则下 `.col-hide` 按钮**已经合规**,本项的验证用 DOM 测量来断言这一点,让这个问题**按证据结案**,而不是按截图结案。R1 提出的「给该按钮新建 i18n 键与文案」**撤回** —— 文案已经存在。

**树里实际的样子**(已核):

```html
<!-- features/tracker/tracker.component.html:283-287 -->
<button mat-icon-button class="col-hide" type="button"
        (click)="hideColumn(col.status)"
        [matTooltip]="t('tracker.hideColumn')"
        [attr.aria-label]="t('tracker.hideColumn')">
  <mat-icon>visibility_off</mat-icon>
</button>
```

`tracker.hideColumn`(`i18n.service.ts:774`)= zh 隐藏此列 / en "Hide column" / fr "Masquer la colonne"。

**落点**:**零代码改动** —— 按定稿,这是「closure by measurement」。

---

## R-4.2 — 看板卡片补上列表行的等待行与 chip 行

**定稿**(第 128 行):

> Each kanban card gets a bottom row carrying the *same computed value the list row shows for that entry* — `daysWaiting(a)`, **days since the application date** (`tracker.component.ts:143-148`), rendered with the same key the list uses, `tracker.daysInLoop`, and shown under the same condition (non-null), exactly as the list row renders it (`tracker.component.html:400-402`). Plus a chip row mirroring list density: a verdict chip **only** when the entry is terminal, and an attention chip **only** when `needsFollowUp(a)` is true …

### 逐项对照

| 定稿要求 | 实现 | 落点 |
|---|---|---|
| 等待行 = 列表行**同一个计算值** | 直接调 `daysWaiting(a)`(同一个方法,不是复刻公式) | `tracker.component.html` `.card-wait` |
| 用列表行**同一个键** | `tracker.daysInLoop` | 同上 |
| **同样条件**(非 null 才显示) | `@if (daysWaiting(a); as dw)` —— null 时不渲染 | 同上 |
| 结果 chip **仅终态时** | `isTerminal(a)` | 同上 `.card-chips` |
| 注意力 chip **仅 `needsFollowUp(a)` 为真时** | `needsFollowUp(a)`(同一个方法) | 同上 |
| 不发明不存在的阈值 | 定稿明写「There is no flow-view attention signal in the tree … and none is invented」—— 本项**没有**引入任何新阈值 | 同上 |
| 卡片信息集 = 列表行信息 **− 步进器** | 公司、岗位、等待行、chip 行 —— 步进器本身不上卡片 | 同上 |

### 终态口径

定稿说「terminal」但没枚举状态。**树里已有的终态定义**是 `needsFollowUp()` 排除的那一组(`tracker.component.ts:181`):

```ts
if (['Offer', 'Accepted', 'Rejected', 'Withdrawn', 'Ghosted'].includes(app.status)) return false;
```

本项把这一组**提取成具名的 `terminalStatuses`**,并让 `needsFollowUp()` 复用它 —— 终态口径从此只有一处定义,两处判定不可能再分叉:

```ts
/** 终态:已有明确结果,不再需要跟进。票123 R-4:看板卡片的「结果 chip」只在此态出现。 */
readonly terminalStatuses: ApplicationStatus[] = ['Offer', 'Accepted', 'Rejected', 'Withdrawn', 'Ghosted'];

isTerminal(app: Application): boolean {
  return this.terminalStatuses.includes(app.status);
}

/** 终态对应的 chip 语义类(与全局 .chip 变体同名)。 */
terminalChipClass(app: Application): string {
  switch (app.status) {
    case 'Offer': case 'Accepted': return 'offer';
    case 'Rejected': return 'rejected';
    case 'Ghosted': return 'ghosted';
    case 'Withdrawn': return 'withdrawn';
    default: return '';
  }
}
```

**chip 用既有全局语义色,未新增任何颜色**:`.chip.offer` / `.chip.accepted`(go-tint/go)、`.chip.rejected`(stop-tint/stop)、`.chip.ghosted`(attention-tint/attention)、`.chip.withdrawn`(parked-fill/parked)都是 `styles.scss` 里已有的变体。

**注意力 chip** 用新增的 `.chip.attention`(`attention-tint`/`attention`)—— 与 `.chip.ghosted` 同一套既有令牌,没引入新色值。R-2 的白名单允许「上述任一色的 alpha 衍生物」与令牌本身;这里直接用的是令牌原值。

### 新增文案

`tracker.followup` = zh 待跟进 / en "Follow up" / fr "À relancer" —— 与列表行那个 `!` 徽标(`tracker.followupHint`)**同一语义**,但徽标是纯符号无文字,chip 需要一个可读的短标签。

### 样式

```scss
/* ---------- 票123 R-4:看板卡片底部行(等待天数 + chip 行) ---------- */
.card-wait { font-size: 12px; color: rgba(31, 36, 48, 0.55); margin-top: 4px; }
.card-chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; }
```

---

## R-4.3 — 回归守卫:纯图标按钮的可访问名 = 0

**定稿**(第 130 行):「app-wide probe on audited surfaces: icon-only buttons without accessible names = **0** (a regression guard from this item on — it passes on the current tree for this button and must keep passing)」。

### 普查工具

`docs/fix-program/evidence-123/tools/census-icon-buttons-r4.py` —— 解析受审七面的模板,按 `<button …>…</button>` 元素切块(支持跨行),判定「是不是纯图标按钮」(元素内只有 `mat-icon`、无可见文本),再判「有没有可访问名」。

**工具落盘的原因**:定稿的守卫要在**每一次**后续改动上保持成立,所以它必须可复跑,不能只做一次。

### 普查发现的真实缺陷

**第一轮:3 处硬缺陷** —— 三个「收藏」心形按钮(看板卡片 / 列表行 / 流程视图)**既没有 `aria-label`,也没有任何 tooltip**:

```html
<button mat-icon-button class="fav-btn" (click)="toggleFavorite(a.id); $event.stopPropagation()"
        [color]="isFavorite(a.id) ? 'warn' : undefined">
  <mat-icon>{{ isFavorite(a.id) ? 'favorite' : 'favorite_border' }}</mat-icon>
</button>
```

这正是定稿守卫要拦的那一类:纯图标、无可访问名。已修 —— 绑定既有键 `tracker.favorite` / `tracker.unfavorite`(该键本来就在 i18n 里,只是没人用),tooltip 与 `aria-label` 同值同键。

**第二轮:27 处软缺陷** —— 只带 `matTooltip`、没有 `aria-label` 的纯图标按钮。

> **为什么 tooltip 不算可访问名**:Material 的 `matTooltip` 挂的是 `aria-describedby`(悬停时朗读的**描述**),不是控件的**名字**。读屏走到这些按钮上时,仍会念出一个无名控件。本仓也没有任何全局配置把 tooltip 转成 `aria-label`(已核 `app.config.ts` / `app.component.ts` / `app/shared/*`)。
>
> 所以定稿说的「without accessible names」按可访问名的正确定义算,这些**都算没有**。修法是**纯加法**:把同一份 tooltip 表达式再绑一次 `aria-label` —— 不改视觉、不改行为、不加新文案。

补全用脚本做(`docs/fix-program/evidence-123/tools/add-aria-labels-r4.py`),规则:

- 已是纯图标按钮、已带 `aria-label` → 跳过;
- 是纯图标按钮、带 `matTooltip="X"` / `[matTooltip]="X"` → 补 `[attr.aria-label]="X"`(插值式 tooltip 同时改成属性绑定式,见下);
- 没有任何 tooltip → 跳过(补它需要新文案,那属设计决策,不在本项范围)。

**一处需要特别说明的**:两个归档按钮原来写的是 `matTooltip="{{ t('tracker.archive') }}"`(插值式属性)。脚本按同形复制会产出 `[attr.aria-label]="{{ … }}"`,这是**语法错误**(属性绑定里不能再放插值)。已手工改成 `[matTooltip]="t('tracker.archive')"` + `[attr.aria-label]="t('tracker.archive')"` —— Angular 的 `matTooltip` 本来就该用属性绑定式。构建期报 NG5002 时才暴露出来,已被 `ng build` 抓到并修掉。

### 最终普查结果

```
=== 纯图标按钮可访问名普查(受审七面) ===
硬缺陷(无 aria-label 且无 tooltip/title): 0

软提示(有 tooltip/title,无 aria-label): 0
```

**受审七面(login / dashboard / tracker / playbook / playbook-detail / techstack / calendar)上的纯图标按钮,现在每一个都有可访问名。**

### 补全落点明细(27 处)

| 文件 | 处数 | 控件 |
|---|---|---|
| `features/dashboard/dashboard.component.html` | 1 | 刷新 |
| `features/tracker/answer-library-dialog.component.ts` | 2 | 编辑 / 删除条目 |
| `features/tracker/application-detail-dialog.component.ts` | 7 | 编辑投递 / 重新解析 JD / 编辑 JD 摘要 / 编辑备注 / 删问答 / 编辑沟通 / 删沟通 |
| `features/tracker/tracker.component.html` | 7 | 更多操作 / 只看收藏 / 清筛选(×2)/ 归档 / 取消归档 / 上一页 |
| `features/playbook/playbook.component.html` | 1 | 刷新 |
| `features/techstack/category-manager/category-manager.component.html` | 3 | 确认重命名 / 取消重命名 / 删除分类 |
| `features/techstack/techstack.component.html` | 6 | 管理分类 / 回收站 / 刷新 / 清筛选 / 上一页 / 下一页 / 做成闪卡 |

(实际 27 处,上表「7」项含看板卡片与列表行的收藏按钮 —— 那两个已在上文单独修过,脚本不再重复处理。)

---

## 定稿其余验证条款的状态

| 条款 | 状态 |
|---|---|
| DOM 探针:`.col-hide` 的可访问名与 tooltip 等于 `tracker.hideColumn` | ✅ 代码层已核(`tracker.component.html:283-287`) |
| 受审面纯图标按钮无可访问名 = 0 | ✅ 普查工具复跑 = 0 / 0 |
| 截图对比(同一条目,列表 vs 看板)显示同一个等待值 | ⚠️ **待活体复核** —— 两侧调的是同一个 `daysWaiting(a)` 方法、同一个 `tracker.daysInLoop` 键,值必然一致;但「截图对比」本身是浏览器行为,需在活体跑一次截图存档 |
| 几何探针:看板在 1440px / 1280px 视口下无横向滚动条 | ⚠️ **待活体复核** —— 本项只加了一行文字与两枚 chip,未改看板的列宽/横向滚动结构;但视口行为需活体确认 |

**关于两条「待活体复核」的诚实说明**:本沙箱的后端服务此前未启动,浏览器活体探针无法跑通(见 `PHASE0-AUDIT.md`)。这两条**不在源码层可证伪**的范围 —— 它们量的是渲染后的布局与像素。等后端可起时,需要补:①列表/看板同条目截图对;②1240/1280 视口下的看板横向滚动条测量。

---

## 偏差清单

**空。**

逐条对定稿 §R-4 验收:

- R-4.1 那个「眼睛」 —— 零代码改动,按定稿「closure by measurement」结案;.col-hide 既有 tooltip 又有 aria-label,键为 `tracker.hideColumn` ✅
- R-4.2 看板卡片补等待行 + chip 行 —— 与列表行**同一个** `daysWaiting()` / **同一个** `tracker.daysInLoop` / **同样**的非空条件;结果 chip 仅终态、注意力 chip 仅 `needsFollowUp()` 为真;未发明任何新阈值;终态口径提取为单一具名常量,两处判定不分叉;chip 复用既有全局语义色,未新增颜色 ✅
- R-4.3 守卫 —— 普查工具落盘可复跑;查出并修掉 3 处硬缺陷 + 27 处软缺陷;最终受审七面 0 / 0 ✅

**类型检查**:`npx tsc -p tsconfig.app.json --noEmit` → exit 0。
**构建**:`npx ng build --configuration production` → 无错误。
**测试**:Jest 15 suites / **399 passed, 399 total**。
**模板结构检查**:`node tools/check-templates.mjs` → ✅ 23 个模板结构通过。

---

## 自验工具

| 工具 | 用途 | 结果 |
|---|---|---|
| `tools/census-icon-buttons-r4.py` | 纯图标按钮可访问名普查(受审七面) | 硬缺陷 0 / 软提示 0 |
| `tools/add-aria-labels-r4.py` | 按既有 tooltip 批量补 `aria-label`(幂等,`--dry` 可预览) | 已补 27 处 |
| `tools/census-middot-r3.py` | 中点普查(R-3 回归护栏) | 待修命中 0 |
