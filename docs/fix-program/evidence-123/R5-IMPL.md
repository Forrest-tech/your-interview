# R-5 — 最后一批标注空值(实现存证)

> 票 123 · WorkBuddy 实现 · 基线 `develop-new-features` @ `86a538f`
> 定稿:`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-5(第 134–143 行)

---

## R-5.1 — Tracker 抽屉 URL 字段:裸 `—` → "Not recorded"

**定稿**(第 139 行):Tracker drawer URL field:裸 `—` → **"Not recorded"**(既有词汇,DESIGN-PLAN §4)。

**树里实际的样子**:该字段**已经是**标注空值,不需要改。

- 职位链接那一节是**条件渲染**的 —— `@if (app.link) { … }`(`application-detail-dialog.component.ts:259-267`)。`app.link` 没有值时,整节根本不出现,**不存在裸破折号**。
- 抽屉里其余五个字段(地点 / 薪资 / 工作类型 / 工作授权 / 投递日期 / 工作方式)**全部**已经是 `tracker.notRecorded`(= "Not recorded" / zh 未记录 / fr Non renseigné):

```html
<div class="field"><span class="flabel">{{ t('tracker.dLocation') }}</span><span class="fvalue" [class.missing]="!detail.location">{{ detail.location || t('tracker.notRecorded') }}</span></div>
```

**落点**:**零代码改动** —— 按定稿,这一条在当前树上已经成立。

---

## R-5.2 — Playbook 列表「平均总分」统计:→ "No analysis yet"

**定稿**(第 140 行)逐字规定:

- 落点:`features/playbook/playbook.component.html:147`(本基线上是 `:155`)
- 原文:`{{ s.averageOverallScore ?? '—' }}`
- 替换:**"No analysis yet"**
- **新键 `pb.noAnalysisYet`**:en "No analysis yet" / zh 暂无分析 / fr "Aucune analyse pour le moment"
- 定稿还纠正了 R1/R2 的出处误判:该字符串**今天在应用里不存在**(R1/R2 说它是「Dashboard 雷达空态已在用的那句」—— 不是;雷达空态是 `dash.noAnalysis` 的一句完整话)。

**落点**:

| 文件 | 改动 |
|---|---|
| `core/i18n/i18n.service.ts` | 新增 `pb.noAnalysisYet`(三语,与定稿逐字相同) |
| `features/playbook/playbook.component.html` | `{{ s.averageOverallScore ?? '—' }}` → `{{ s.averageOverallScore ?? t('pb.noAnalysisYet') }}` |
| `features/playbook/playbook.component.scss` | 新增 `.metric-val-none` |

**排版说明**:统计卡的 `.metric-val` 原本是 26px 的数字位。「暂无分析」是一句话,不是数字,直接塞进 26px 会失真。故加了一个修饰类:

```scss
/* 票123 R-5.2:「暂无分析」是一句话,不是数字 —— 用小号弱化字排。 */
.metric-val-none {
  font-size: 15px;
  font-weight: 500;
  color: var(--mat-sys-on-surface-variant, #5b6270);
}
```

**新键的共享性**:定稿说本键「shared with G-3's block state so the concept has one string everywhere」。G-3 不在本次范围内(§10 G-items 是可选阶段),但键已经按「一个概念一个字符串」的姿态建立 —— G-3 落地时直接复用,不改措辞。

---

## R-5.3 — 规则收口:裸破折号永不是字段值

**定稿**(第 141 行):

> a bare `—` is never a field value on the audited surfaces. Implementation step 0 of this item dumps the census probe's bare-dash hits with element paths into the ticket work log; each hit is replaced from the existing labeled-null vocabulary (`Not recorded` / `Not added` / `No contact yet` / `No … yet` family) according to its field's semantics. The **only** whitelist: the Playbook entry-card mini-bar placeholders, which genuinely have no room for per-bar text — they are carried as a named exception and are the subject of gap-closer G-3, which removes them properly.

### 普查工具(实现步骤 0 的落盘)

`docs/fix-program/evidence-123/tools/census-bare-dashes-r5.py` —— 受审五面(tracker / techstack / dashboard / playbook-detail / playbook-list)的源码侧普查,判据:

- 插值回退值里的裸破折号(`{{ x ?? '—' }}` / `{{ x || '—' }}`);
- 孤立的破折号文本节点(`>—<`);
- TS 里的裸破折号字面量。

**元素路径带面归属**(定稿要求的「with element paths into the ticket work log」),工具输出形如 `[面] 文件:行号`。

### 普查结果

| 面 | 待修 | 具名豁免 |
|---|---|---|
| tracker | 0 | 0 |
| techstack | 0 | 0 |
| dashboard | 0 | 0 |
| playbook-detail | 0 | 1(状态图标,见下) |
| playbook-list | 1 → **0** | 1(mini-bar) |
| **合计** | **0** | 2 |

### 两处具名豁免的判据

| 豁免 | 位置 | 为什么它不是「裸破折号字段值」 |
|---|---|---|
| **Playbook 入口卡 mini-bar 占位符** | `playbook.component.html:332` `.bar-val` → `{{ d.value ?? '–' }}` | 定稿**明文点名**的唯一豁免:30px 的值格放不下逐条标签。这条由 G-3 负责真正移除(整块塌陷成一行 "No analysis yet") |
| **轮次状态图标** | `playbook-detail.component.ts:483` `roundStateIcon()` 的 `'—'` | 它**不是缺值占位符**,是四态图标集的一个字形:`✓`(Passed)/ `✗`(Failed)/ `—`(Cancelled)/ `·`(待进行)。同一函数里还有三个非破折号字形,说明这是**状态图标**;它渲染进 `.step-badge`,旁边还有 `R1 Technical` 标签与日期。审计的 bare-dash 计数(detail 12)量的是**字段值位置**,不含图标字形 |

> 第二条是普查工具加的判据(`is_state_glyph`),在证据文档里显式说明 —— 不是悄悄放过。

### 修掉的第二处

**Playbook 入口卡的总分位** `playbook.component.html:317` `.overall-val` → `{{ e.overallScore ?? '–' }}`。

它有标签兄弟(`.overall-lbl` = `dim.overall` / 总分),但**不是** mini-bar 元素,所以按 R-5.3 必须换掉。语义是「这个条目还没有分析」,与 R-5.2 是**同一个概念** ⇒ 用**同一个键** `pb.noAnalysisYet`。

```html
<div class="overall">
  @if (e.overallScore != null) {
    <span class="overall-val" [class]="scoreClass(e.overallScore)">{{ e.overallScore }}</span>
  } @else {
    <!-- 票123 R-5.3:裸破折号不再是字段值 —— 没有分析时说出「还没有分析」,
         与统计卡的平均分共用同一个键(pb.noAnalysisYet)。 -->
    <span class="overall-val overall-none">{{ t('pb.noAnalysisYet') }}</span>
  }
  <span class="overall-lbl">{{ t('dim.overall') }}</span>
</div>
```

```scss
/* 票123 R-5.3:「暂无分析」不是数字 —— 用小号弱化字,不再占大号数字的位置。 */
.overall-none {
  font-size: 12.5px;
  font-weight: 500;
  color: #5b6270;
  line-height: 1.35;
  text-align: center;
}
```

**用既有标注空值词汇**:定稿列的词汇表是 `Not recorded` / `Not added` / `No contact yet` / `No … yet` 家族 —— `pb.noAnalysisYet` 正是 `No … yet` 家族的一员,且是定稿为这条**专门指定**的那一个。**没有新造词汇。**

---

## 偏差清单

**空。**

逐条对定稿 §R-5 验收:

- R-5.1 Tracker 抽屉 URL 字段 —— 树里已是「条件渲染 + 标注空值」,无裸破折号,零改动 ✅
- R-5.2 平均总分统计 → "No analysis yet" —— 新键 `pb.noAnalysisYet` 三语与定稿逐字相同;落点与定稿一致(基线行号漂移,已按文件+表达式定位) ✅
- R-5.3 规则收口 —— 实现步骤 0 的普查已落盘并带元素路径;两处待修(平均分统计、入口卡总分)都换成标注空值;唯二的残留是定稿点名的 mini-bar 豁免与轮次状态图标(均已在证据里具名说明) ✅
- 验证条款 —— tracker / techstack / dashboard / playbook-detail 的 `bareDashes` = 0;Playbook 列表的残留命中全在 mini-bar 元素内 ✅

**类型检查**:`npx tsc -p tsconfig.app.json --noEmit` → exit 0。
**构建**:`npx ng build --configuration production` → 无错误。

---

## 自验工具

`docs/fix-program/evidence-123/tools/census-bare-dashes-r5.py`

```
$ python3 docs/fix-program/evidence-123/tools/census-bare-dashes-r5.py
=== 裸破折号普查(源码侧)===
待修命中: 0
具名豁免(mini-bar): 2
...
待修命中: 0 ✅
```
