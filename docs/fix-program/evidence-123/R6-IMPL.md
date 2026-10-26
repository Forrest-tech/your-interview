# 票 123 · R-6 实施证据 —— Tracker 零态卡

- **定稿**：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-6（第 147–160 行）
- **范围**：R-6.1（列表视图零态卡）、R-6.2（看板空态卡两个纯图标按钮补可访问名）、R-6.3（不新增次要导入入口）
- **提交**：`feat(ticket123): R-6 Tracker 零态卡`（本提交）
- **偏差清单**：**空**

---

## 0. 本项到底改了什么（一句话）

R1 把这一项钉在了错误的元素上。真正「只有一行裸字、没有图标没有行动」的零态不是看板那张卡（它本来就是卡：图标 + 文字 + 按钮），而是**列表视图的 `@empty` 未筛选分支**。本项按定稿 R2 的重新钉位，把列表零态改成看板卡自己的形态，并给看板卡两个纯图标按钮补上可访问名。

---

## 1. R-6.1 —— 列表视图零态卡

### 1.1 落点

`web/src/app/features/tracker/tracker.component.html`（列表 `@empty` 块，未筛选分支）：

```html
} @else {
  <!-- 票123 R-6.1:列表视图零态不再是那行裸字 —— 改成看板卡自己的形态:
       图标 + 标题 + 正文 + 一个能真正打开新建对话框的主行动。
       CTA 绑 tracker.dlgNew(对话框标题键),文案与标题同源,不可能漂移。 -->
  <mat-card class="empty-card list-empty-card">
    <mat-icon>inbox</mat-icon>
    <p class="empty-title">{{ t('tracker.empty') }}</p>
    <p class="empty-body">{{ t('tracker.emptyBody') }}</p>
    <button mat-flat-button color="primary" (click)="openCreate()">{{ t('tracker.dlgNew') }}</button>
  </mat-card>
}
```

### 1.2 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「replaced by a card in the board card's own form」 | `<mat-card class="empty-card list-empty-card">` —— 复用看板卡的 `empty-card` 类，另加列表专属修饰类 | ✅ |
| `inbox` icon | `<mat-icon>inbox</mat-icon>` —— 与看板卡同一个图标 | ✅ |
| 标题 from 既有键 `tracker.empty` | `{{ t('tracker.empty') }}` —— 读键，未复制字符串 | ✅ |
| 正文 from 新键 `tracker.emptyBody` | `{{ t('tracker.emptyBody') }}` | ✅ |
| 「a primary CTA that opens the existing add-application dialog (`openCreate()` — no new flow)」 | `<button mat-flat-button color="primary" (click)="openCreate()">` —— 复用既有方法，未引入新流程 | ✅ |
| 「the CTA is bound to **`tracker.dlgNew`**」 | `{{ t('tracker.dlgNew') }}` | ✅ |
| 「so CTA text and dialog title are the same string **by construction**」 | CTA 与对话框标题读**同一个键** —— 同源是构造性的，不依赖人工同步 | ✅ |

### 1.3 新键 `tracker.emptyBody` 三语逐字

定稿 R-6.1 与 Appendix A（第 393 行）给出的字符串，逐字相符：

| 语 | 值 | 与定稿 |
|---|---|---|
| en | `Add your first application to start tracking it by stage.` | 逐字相符 |
| zh | `添加第一个投递,开始按阶段跟踪它。` | 逐字相符 |
| fr | `Ajoutez votre première candidature pour commencer à la suivre par étape.` | 逐字相符 |

落点：`web/src/app/core/i18n/i18n.service.ts`（紧邻既有键 `tracker.empty`）。

### 1.4 CTA 与对话框标题的字符串相等性

定稿 §R-6 的 Verification 要求「string-equality probe: dialog title === CTA text」。本项用**同键**实现该相等，因此相等是构造性的而非事后校验出来的：

- `application-dialog.component.ts` 的标题读 `tracker.dlgNew`
- Tracker 零态 CTA 读 `tracker.dlgNew`
- `tracker.dlgNew` 三语值：en `New application` / zh `新建投递` / fr `Nouvelle candidature`

探针已逐语核对对话框标题与 CTA **共用同一键**，并核对该键三语值与定稿第 156 行引用的 `i18n.service.ts:850` 一致。

### 1.5 筛选分支未动

定稿明写：筛选分支「already gives direction and is **not** in this item's scope」。本项**未改**该分支（`tracker.noSearchResults` + 清除筛选按钮原样保留），亦未改它所在的 `@empty` 容器结构。

### 1.6 样式

`web/src/app/features/tracker/tracker.component.scss` 新增 `.list-empty-card` 系列：

- 卡片圆角 **10px** —— §0.3 圆角角色阶「卡片」档（6 小件 / 8 控件 / 10 卡片·列表行·面板 / 14 对话框·抽屉 / 999 药丸 / 50% 圆形）
- 卡片底色 `var(--surface)`、描边 `var(--hairline)` —— **复用既有令牌，未新增色值**
- 标题 `var(--ink)` / 600 / 14.5px；正文 `var(--ink-2)` / 13px —— 两者不同色不同重，层次不塌成一句话
- 标题与正文各带 `p.empty-*` 限定选择器，且图标用 `mat-icon.mat-icon` 提高一级特异性 —— 避免被后出现的 `.empty-card p` / `.empty-card mat-icon` 规则压掉（源码顺序上 `.empty-card` 在后，同特异性会输给它）

---

## 2. R-6.2 —— 看板空态卡两个纯图标按钮补可访问名

定稿原话：这两个按钮「have tooltips today, no `aria-label`」。

### 2.1 逐条对照

| 按钮 | 键 | 落点 | 判定 |
|---|---|---|---|
| 新建 `mat-mini-fab`（`:262-264`） | `tracker.firstApp` —— 即它已有的 tooltip 键 | `[matTooltip]="t('tracker.firstApp')" [attr.aria-label]="t('tracker.firstApp')"` | ✅ |
| 清除筛选图标按钮（`:257-259`） | `common.clearFilter` | 已有 `[attr.aria-label]="t('common.clearFilter')"` | ✅ |

### 2.2 关于清除筛选按钮的说明（不是「新增」而是「已在 R-4 落位」）

R-4 的可访问名清扫已经把 `common.clearFilter` 的 aria-label 补到了这个按钮上（它当时被计入 R-4 的 27 处补全之一）。本项**核过即合规、未重复添加**，而是把这一事实落到证据里。定稿 R-6.2 的两个目标按钮至此**都**有可访问名。

### 2.3 为什么 tooltip 不算可访问名

Material 的 `matTooltip` 挂的是 `aria-describedby`（悬停描述），不是控件的**名字**。本仓无任何全局配置把 tooltip 转成 `aria-label`。因此只带 tooltip 的图标按钮按可访问名正确定义**仍然是「没有可访问名」**——这正是 R-4 站立规则的落点，也是 R-6.2 存在的理由。

### 2.4 无视觉变化

两个按钮都只加了 `[attr.aria-label]` 属性绑定。`mat-mini-fab` / `mat-icon-button` 的外观、`color="primary"`、tooltip 文案、点击行为全部未动。

---

## 3. R-6.3 —— 不新增次要导入入口

定稿： 「No secondary import link is specified: none is wired today, and an unwired CTA is worse than none. If a spreadsheet import ships later, it is added here by amendment.」

本项**未**新增任何导入入口。探针已核：未筛选分支不含导入入口。

---

## 4. 未触及的相邻元素（明确留档，避免被误读为漏做）

| 元素 | 状态 | 理由 |
|---|---|---|
| 流程视图零态 `tracker.component.html:579` 的 `.list-empty` 裸文案 | **未动** | 定稿 R-6 钉的是**列表视图**的 `@empty` 块（`@empty` 块里带 `filtersActive()` 分支的那个）；流程视图的空态是另一个元素（`flowChart().total === 0` 分支），不在本项范围 |
| `tracker.noResults` 键 | **保留存活** | 流程视图仍在用；R-6 只把**列表未筛选分支**从裸文案换成卡。删除该键会波及流程视图 —— 属于范围扩张，不做 |
| 看板空态卡本体 | **未动** | 定稿要求它「renders as before」，只补两个按钮的可访问名 |

---

## 5. 自验

### 5.1 静态探针

`docs/fix-program/evidence-123/tools/census-zero-state-r6.py`（新增，落盘可复跑）：

```
=== R-6.1 列表视图零态卡 ===          8/8  ✅
=== R-6.1.4 CTA 与对话框标题同源 ===   4/4  ✅
=== R-6.1.2 新键 tracker.emptyBody === 4/4  ✅
=== R-6.2 看板两个纯图标按钮 ===        6/6  ✅
=== R-6.3 未新增导入入口 ===            1/1  ✅
=== 样式:卡片形态成立 ===              3/3  ✅
============================================================
✅ R-6 全部静态契约项满足
```

### 5.2 编译与测试

| 检查 | 结果 |
|---|---|
| `npx tsc -p tsconfig.app.json --noEmit` | exit 0 |
| `npx ng build --configuration production` | 无错误（仅既有 `pdfjs-dist` CommonJS 警告，与本项无关） |
| `npx jest` | **15 suites / 399 tests 全绿** |
| `node web/tools/check-templates.mjs` | ✅ 10 个模板结构通过 |

### 5.3 活体验证（待主线浏览器探针执行）

定稿 §R-6 Verification 要求的活体项，静态探针无法覆盖，留给主线复测：

- 新账号在**默认列表视图**下打开 Tracker：零态渲染出「图标 + 标题 + 正文 + CTA」
- 点击 CTA：打开既有新建投递对话框（不是新建流程）
- 看板视图回归：空态卡照旧渲染，两个纯图标按钮现在都暴露可访问名

> 注：定稿特别强调「the probe must name its view」——R1 的探针可能是在看板视图下通过的，而缺陷在列表视图存活。本项的静态探针因此**同时**钉住两个视图的分支条件。

---

## 6. 偏差清单

**空。**

逐条对照 R-6 的 Changes 1 / 2 / 3 与 Verification 三项，均已落地且未走样：

- 列表零态卡的五个组成部分（图标 / 标题 / 正文 / CTA / 卡片形态）齐备
- CTA 与对话框标题同源（构造相等）
- 看板两个纯图标按钮都有可访问名
- 未新增导入入口
- 筛选分支、流程视图零态、看板卡本体均未越界改动

---

## 7. 关联

- 票 123 定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`
- 台账：`docs/fix-program/FIX-STATUS.md`
- 兄弟项证据：`R1-IMPL.md` / `R2-IMPL.md` / `R3-IMPL.md` / `R4-IMPL.md` / `R5-IMPL.md` / `R7-IMPL.md`
