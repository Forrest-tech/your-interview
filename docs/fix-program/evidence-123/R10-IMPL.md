# 票 123 · R-10 实施证据 —— 热力图邀请行

- **定稿**：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-10（第 253–261 行）
- **范围**：R-10.1（零活动时的邀请行）、R-10.2（新键三语）、R-10.3（接既有流程）、R-10.4（不新增颜色与版式系统）
- **提交**：`feat(ticket123): R-10 热力图邀请行`（本提交）
- **偏差清单**：**空**

---

## 0. 这一项为什么存在

复审的 Information-design 理由点名了三个 holder：看板阶段停留时长（→ R-4）、Tracker 零态行动（→ R-6），以及「**heatmap remains a sparse grey field with no invitation**」。R1 的清单里没有热力图这一项（只在 R-1.1 碰了格子圆角），却宣称「除两条残留外已闭合全部发现」——这一条是第三条残留。

缺陷形态与 F-2 的裸零态同类，只是换了个面板：**没有任何复习记录的用户，看到的是一个标题、84 个灰格、和一个图例——没有一句话，没有一个行动。**

---

## 1. R-10.1 —— 触发条件：窗口内复习总数为 0

### 1.1 落点

`web/src/app/features/techstack/techstack.component.ts`（紧随 `heatmapWeeks()` 之后）：

```ts
/**
 * ★ 票123 R-10:热力图窗口内的复习总数 —— 窗口内过去的日子 count 之和。
 *
 * 未来日子 count 为 -1(不是 0),不参与求和。
 * 为 0 时热力图是一片灰格,没有一句话也没有一个行动 —— 面板要改成一次邀请。
 */
readonly heatmapTotalCount = computed(() => {
  let total = 0;
  for (const week of this.heatmapWeeks()) {
    for (const day of week) {
      if (day.count > 0) total += day.count;
    }
  }
  return total;
});
```

### 1.2 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「the sum of `count` over the past days of `heatmapWeeks()`」 | 数据源是 `this.heatmapWeeks()`；只累加 `day.count > 0` | ✅ |
| 「the **visible window's** total review count is **0**」 | 触发条件 `heatmapTotalCount() === 0` | ✅ |

### 1.3 关于「未来日子 count = -1」

`heatmapWeeks()` 给窗口内尚未到来的日子赋 `count = -1`（表示「还没到」，不是「零次复习」）。求和时用 `day.count > 0` 而不是 `day.count >= 0`，因此未来日子**不参与**求和——这一点在实现里明确写进了注释，避免后来者把判据改成 `>= 0` 而让未来日子把总数算成负偏移。

### 1.4 「有复习时一切不变」

邀请行挂在 `@if (heatmapTotalCount() === 0)` 内。窗口内只要有一次复习，总数 > 0，邀请行不渲染，**热力图网格、图例、连击徽标的渲染逻辑一行未动**。

---

## 2. R-10.2 —— 模板：图例下方的邀请行

### 2.1 落点

`web/src/app/features/techstack/techstack.component.html`（`.heatmap-panel` 内、`.heatmap-legend` 之后）：

```html
<!-- 票123 R-10:窗口内一次复习都没有时,图例下面给出一次邀请 ——
     一句话说明这张图会怎么填起来,加一个进入既有复习流程的行动。
     startReview() 是复习流程的真实入口;没有到期项时它自己报
     techstack.reviewNoDue(既有行为,不是这里发明的)。 -->
@if (heatmapTotalCount() === 0) {
  <div class="heatmap-invite">
    <p>{{ t('ts.heatmap.empty') }}</p>
    <button mat-button color="primary" (click)="startReview()">{{ t('ts.heatmap.startReview') }}</button>
  </div>
}
```

### 2.2 逐条对照定稿

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「the panel renders an invitation row **under the legend**」 | `.heatmap-legend` 之后、同一 `.heatmap-panel` 内 | ✅ |
| 「a body line (new key `ts.heatmap.empty`)」 | `{{ t('ts.heatmap.empty') }}` | ✅ |
| 「a text-button CTA (new key `ts.heatmap.startReview`)」 | `<button mat-button color="primary">` + `{{ t('ts.heatmap.startReview') }}` | ✅ |
| 「wired to the **existing** `startReview()`」 | `(click)="startReview()"` | ✅ |
| 「the review flow's real entry point」 | `startReview()` 是既有公开方法（`techstack.component.ts:1293`），页面顶部「今日复习」按钮接的就是它 | ✅ |
| 「if nothing is due it reports through the existing `techstack.reviewNoDue` notice」 | `startReview()` 内部 `this.notify(this.t('techstack.reviewNoDue'))` —— 未改动 | ✅ |
| 「behavior inherited rather than invented」 | 本项**没有**新增任何提示分支，空队列提示完全走既有代码路径 | ✅ |
| 「No new colors, no new layout system」 | 见 §4 | ✅ |

---

## 3. R-10.3 —— 新键三语逐字

定稿 §R-10 正文与 Appendix A（第 399 行）给出的字符串，逐字相符：

| 键 | 语 | 值 |
|---|---|---|
| `ts.heatmap.empty` | en | `No reviews yet. Finish a first review and this map starts filling in.` |
| | zh | `还没有复习记录。完成第一次复习,这张图就会开始填起来。` |
| | fr | `Aucune révision pour le moment. Terminez une première révision et cette carte commencera à se remplir.` |
| `ts.heatmap.startReview` | en | `Start a review` |
| | zh | `开始复习` |
| | fr | `Commencer une révision` |

落点：`web/src/app/core/i18n/i18n.service.ts`（紧随既有的 `ts.heatmap.less` / `ts.heatmap.more`）。

### 3.1 与既有 `techstack.startReview` 键的关系（不是重复）

仓里已有一个 `techstack.startReview`（`i18n.service.ts:733`），值是「今日复习 / Review due / Réviser」，页面顶部那个按钮接的是它。

定稿明确把新 CTA 命名为 **`ts.heatmap.startReview`**（新键），值是「开始复习 / Start a review / Commencer une révision」，与前者的**措辞不同、语境不同**（一个是面板里的邀请行动，一个是页面顶部的常驻入口）。因此两键并存是定稿的要求，**不是重复造键**。探针已逐语核对新键三语值与 Appendix A 第 399 行一致。

---

## 4. R-10.4 —— 不新增颜色与版式系统

`web/src/app/features/techstack/techstack.component.scss` 新增三条规则：

```scss
.heatmap-invite {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px solid var(--hairline);
}
.heatmap-invite p {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--ink-2);
}
.heatmap-invite button {
  flex: 0 0 auto;
  margin-left: auto;
}
```

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「No new colors」 | 分隔线用既有 `--hairline`，正文用既有 `--ink-2`；**邀请行自己的样式里没有新增任何十六进制色值** | ✅ |
| 「no new layout system」 | 只是面板内一行 flex（正文 + 按钮），未引入新的栅格/卡片/版式概念 | ✅ |
| 「one labeled line and one existing action」 | 一行文字 + 一个按钮 | ✅ |

圆角与按钮形状走既有令牌（`--mdc-text-button-container-shape: 8px`），未新造。

---

## 5. 自验

### 5.1 静态探针

`docs/fix-program/evidence-123/tools/census-heatmap-invite-r10.py`（新增，落盘可复跑）：

```
=== R-10.1 触发条件 ===        4/4  ✅
=== R-10.2 模板邀请行 ===       6/6  ✅
=== R-10.3 新键三语逐字 ===     8/8  ✅
=== R-10.4 既有流程入口 ===      4/4  ✅
=== R-10.5 不新增颜色/版式 ===   4/4  ✅
============================================================
✅ R-10 全部静态契约项满足
```

> 探针首次运行时报了一处「新增十六进制色值」——经查是探针自身用字符串切片把邀请行**之后**的 `.resole-toggle` 等规则的色值算了进来（`#5b6270` / `#68707e`），不是本次改动引入的。已把提取方式改为正则只取 `.heatmap-invite` 开头的规则块本身，复跑通过。**这是探针的缺陷，不是实现的缺陷**——记在此处以免复测时误读。

### 5.2 编译与测试

| 检查 | 结果 |
|---|---|
| `npx tsc -p tsconfig.app.json --noEmit` | exit 0 |
| `npx ng build --configuration production` | 无错误 |
| `npx jest` | **15 suites / 399 tests 全绿** |
| `node web/tools/check-templates.mjs` | ✅ 10 个模板结构通过 |

### 5.3 活体验证（待主线浏览器探针执行）

定稿 §R-10 Verification 要求的三项，静态探针无法覆盖，留给主线复测：

- 零活动账号打开 Tech Stack：邀请行在图例**下方**可见，正文 + CTA 都在
- 点击 CTA：进入复习流程；若没有到期项，弹出既有的「暂时没有到期的知识点」提示
- 通过应用自身的路径记录一次复习后：邀请行消失，且至少一格渲染 `lv-1` 或更高

---

## 6. 偏差清单

**空。**

逐条对照 R-10 的 Changes 与 Verification，落地且未走样：触发条件是窗口内 count 之和为 0、邀请行在图例下方、正文与 CTA 各自绑新键、CTA 接既有 `startReview()`、空队列走既有 `techstack.reviewNoDue`、有复习时一切不变、未新增颜色与版式系统。

---

## 7. 关联

- 票 123 定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`
- 台账：`docs/fix-program/FIX-STATUS.md`
- 兄弟项证据：`R1-IMPL.md` / `R2-IMPL.md` / `R3-IMPL.md` / `R4-IMPL.md` / `R5-IMPL.md` / `R6-IMPL.md` / `R7-IMPL.md`
