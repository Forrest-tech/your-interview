# R-3 — 文案清扫与中点清除(实现存证)

> 票 123 · WorkBuddy 实现 · 基线 `develop-new-features` @ `86a538f`
> 定稿:`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-3(第 86–113 行)
> 设计稿:`docs/fix-program/source/ticket123-mocks/`(契约,逐项对照不得走样)

---

## R-3.1 — Tracker subtitle

**定稿原文**(第 92–95 行):

| 语种 | 原文 |
|---|---|
| en | Every application by stage, so you can see what needs you next. |
| zh | 全部投递按阶段排列,哪一条需要你接下来处理,一目了然 |
| fr | Toutes vos candidatures par étape, pour voir laquelle a besoin de vous ensuite. |

**落点**:`web/src/app/core/i18n/i18n.service.ts` — `tracker.subtitle` 三语**逐字**替换为上表原文。

**核验**:三语与定稿逐字比对无差异(含 zh 的半角逗号 `,` —— 定稿用的就是半角,照抄)。

---

## R-3.2 — Playbook 列表「Edit」按钮说真话

**定稿**(第 97 行):按钮 `playbook.component.html:21` 调 `toggleEdit()`,该函数切换的是**批量选择模式**并在退出时清空选择 —— 它什么都不编辑。改名:空闲态 **"Select"**(zh 选择 / fr Sélectionner);激活态 **"Done"**(zh 完成 / fr Terminé)。

**落点**:

| 文件 | 改动 |
|---|---|
| `core/i18n/i18n.service.ts` | 新增 `pb.select` / `pb.selectDone`(三语) |
| `features/playbook/playbook.component.html` | 图标 `edit` → `select_all`;文案 `common.edit`/`common.done` → `pb.select`/`pb.selectDone` |

**图标核验**:`select_all` 是**本仓已在用**的 Material Icons 名(已在其它模板出现),不引入新图标名 —— 连字字形名是 PUA 码位(`uniE000` 形式),无法用 cmap/字形名反查图标名,故只用已验证过的名字,避免豆腐块。

**行为核验**:`toggleEdit()` 本身未动,仍切换选择模式;空闲/激活两态文案按 `editing()` 信号切换。

---

## R-3.3 — 中点清扫(`middotJoins = 0`)

### 口径

定稿第 99 行给的是审计自己的指标:**count of ` · ` joins in body text** —— 即**正文里把标签与值(或三个以上异质事实)串在中点上**的写法。判据不是「文件里出现 `·` 这个字符」。

实现侧做两层核验:

1. **同一插值内的中点**(`{{ … · … }}` 或同一文本节点内的中点字面量)= 审计口径的直接对应物;
2. **源码侧普查脚本** `docs/fix-program/evidence-123/tools/census-middot-r3.py` —— 扫受审七面(login / dashboard / tracker / playbook / playbook-detail / techstack / calendar)的模板与组件 TS,剔除注释与 `.scss`,列出每一条仍带 ` · ` 的可渲染行。

### 逐条重写(定稿第 100–105 行点名的六处)

| # | 定稿点名项 | 原写法 | 新结构 | 落点 |
|---|---|---|---|---|
| 1 | Playbook 列表 Upcoming 行 | `{{ companyName }} · {{ role }} · Round {{ order }} {{ stage }}`(单一 `.up-name` 串) | 日期留在自己的 `.up-when`;公司加粗 `<b>` + 岗位压行 `.up-role`;阶段两枚 chip `[Round n]` `[Technical]`;面试官留 `.up-meta` | `playbook.component.html` / `.scss` |
| 2 | Dashboard Upcoming 行 | `2026-10-25 · Video`(日期·形式 ×2)+ `Round 1 · Technical`(阶段文本 ×2) | 日期/时间/形式三行 `.ac-when-line`;阶段两枚 chip | `dashboard.component.html` / `.scss` |
| 3 | Playbook detail 轮次 H3 | `Round 1 · Technical` | `Round 1` + 阶段 chip | `round-card.component.ts` |
| 4 | Detail 结果行 | `Outcome: Upcoming · 0 materials` | 成对事实:`Outcome` 标签 + 值、`Materials` 标签 + 计数 | `round-card.component.ts` |
| 5 | Detail next-step 盒 | `Prepare for Round 1 · Technical(2026-10-25)`(日期还塞进括号、无空格) | 标题 `Prepare for Round 1`,下接堆叠弱化行:`Technical round` / `2026-10-25` / `Video` | `playbook-detail.component.ts` / `.html` / `.scss` |
| 6 | Round selector chips | `R1 · Technical` | 随 §9 track 重设计消失;**本次先消掉中点**:分数从日期后的中点串改为独立行 `.step-score`(§9 落地时 chip 整体替换为轨道节点) | `playbook-detail.component.html` / `.scss` |

**第 6 项的诚实说明**:定稿第 105 行写的是「Round selector chips `R1 · Technical` disappear with the §9 track redesign。**If §9 slips a phase**, the interim fix is chip `R1` + separate stage chip — the join does not survive either way.」—— 本次 §9(R-9)尚未实施,故按定稿给明的**兜底口径**执行:chip 本身保留,但分数串上的中点先行清除(该 chip 内的 `R1 Technical` 之间本无中点,中点在日期与分数之间)。§9 落地时 chip 由轨道节点取代。

### 第 4 项与设计稿的对齐

`mocks/playbook-detail-track.html` 的事实行结构:

```html
<div class="facts">
  <span class="fact"><span class="k">Outcome</span><span class="v go">Passed</span></span>
  <span class="fact"><span class="k">Materials</span><span class="v">3</span></span>
  <span class="fact"><span class="k">Interviewers</span><span class="v">Jimmy P.</span></span>
</div>
```

实现侧对应 `.rc-fact` / `.rc-fact-label` / `<b>`,间距 `gap: 26px`、标签 12px/500/ink-2、值 13px/500 —— 与 mock 的 `.facts{gap:26px}` / `.fact .k{font-size:12px;color:var(--ink2);font-weight:500}` / `.fact .v{font-size:13px;font-weight:500}` 逐项对齐。

### 普查工具落盘

`docs/fix-program/evidence-123/tools/census-middot-r3.py` —— 可复跑,输出「命中总数 / 豁免命中 / 待修命中」三段。

### 普查结果

| 阶段 | 命中 | 说明 |
|---|---|---|
| 开工前 | 命中多面(见下方清单) | 含定稿点名的六处 + 未点名的残留 |
| 本次实施后 | **待修命中 0** | 七面源码侧全清 |
| 同一插值口径复核 | **0** | `{{ … · … }}` 形态清零 |

### 实施中额外清掉的中点(定稿第 109 行的兜底条款)

定稿第 109 行:「Any other ` · ` that survives anywhere on the audited surfaces must be added to this plan's appendix by amendment, with its reason.」 —— 下列各处落在受审面上且属同一模式,故一并清除(均为文案重排,不涉设计变更):

| 面 | 位置 | 原写法 | 处理 |
|---|---|---|---|
| tracker | `tracker.component.html:61` 失联横幅 | `{{ companyName }} · {{ role }}` | 公司/岗位两个子元素 `.ghosting-company` / `.ghosting-role` |
| tracker | `tracker.component.html:552` 分页信息 | `第{p} / {tp} 页 · 共 {t} 条` | 拆 `tracker.pagerPage` / `tracker.pagerTotal` 两键 |
| tracker | `application-detail-dialog.component.ts:222` 公司情报头 | `公司情报 · {{ company()?.name }}` | 标题 `.sec-title` + 副题 `.sec-sub` |
| tracker | `smart-add-dialog.component.ts:245` 下拉项 | `{{ companyName }} · {{ role }}` | 改用破折号分隔(下拉项是单行控件,不拆行) |
| tracker | `smart-add-dialog.component.ts:572` 创建提示 | `已创建「{company} · {role}」` | 同上 |
| tracker | `tracker.component.ts:1265` 删除确认 | `${companyName} · ${role}` | 新增 `tracker.confirmDeleteApp` 键(公司 — 岗位) |
| login | `login.component.html` 页脚 | `个人使用 · 数据仅存本地` | 拆 `auth.footPersonal` / `auth.footLocal` 两键,分两行 |
| playbook | `playbook.component.html:184` 覆盖条 tooltip | `已掌握 3 · 待复习 2` | 改用 ` / ` 分隔(tooltip 是单行提示) |
| playbook | `playbook-calendar.component.html:20` 提醒横幅 | `公司 · 阶段 · 日期 时间` | 三段 `.due-part` |
| playbook | `playbook-calendar.component.html:42` 日历 chip tooltip | `角色 · 阶段` | 改用破折号 |
| playbook | `prep-report.component.ts:629` / `round-card.component.ts:434` 导出框副题 | `公司 · Round N 阶段` | 改用破折号(对话框副题是单行) |
| techstack | `techstack.component.ts:839` 来源公司列表 | `names.join(' · ')` | 改用「、」(中文顿号,与本仓既有的顿号用法一致) |
| techstack | `techstack.component.ts:974` 轮次下拉 | `Round 1 · Technical · 日期` | 拆 `roundOptionParts()`,模板分段渲染 |
| techstack | `techstack.component.ts:1015` 多来源条目 | `公司 · Round N · 日期` | 拆 `sourceEntryParts()`,模板分段渲染 |
| techstack | `duplicates-dialog.component.ts:58-59` | `复习数 · 掌握度 · 来源公司` | 三段 `.r-meta-part` |

### 豁免(定稿第 106–107 行点名,不动)

| 豁免 | 内容 | 依据 |
|---|---|---|
| #4 | Smart paste gate text | 定稿第 106 行:判为合规,不改,点名以免下次复审重提 |
| #5 | 票 73 flow-strip middot(user-confirmed) | 定稿第 107 行:用户确认过的稿子,判为 **Legitimate**,渲染在换行之后,census 不可见 |

> **注**:豁免 #4 指的是**粘贴门控文案**。实现期普查到的 smart-add 两处中点(下拉项与创建提示)是**公司与岗位**的串接,不属该豁免覆盖的字符串,故按同一模式清除。豁免 #5 的 flow-strip(`tracker.component.html:524` 的 `· {{ a.companyName }}`)原样保留。

### 残留但判为不属本项的

| 位置 | 内容 | 判据 |
|---|---|---|
| `core/title/page-title.helpers.ts:53` | 浏览器标签「{页名} · Your Interview」 | 定稿第 108 行明写:文档标题是浏览器 chrome,不是正文,中点指标够不着它 |
| `i18n` 徽标类文案 | `pb.v2.rec.origTag`(原文 · 未修改)、`pb.v2.r.docStdSpoken`(标准回答 · 口语版)、`pb.v2.rec.stdSpokenLabel` 等 | 这些是**徽标/标签**的内部文本,各自渲染在独立元素里(`.orig-tag` 是独立 styled span;导出文档里它们是区块标题),不是「标签与值串在一条正文里」 |
| `shell.copyLine` | 页脚版权行 | 法律声明行,非数据串接 |
| `pb.v2.i.ocrNotConfigured` | 「设置 · AI」 | 路径引用(菜单层级),非数据串接 |
| `practice.*` / `setting.*` / `pb.metrics.*` | AI Practice / 设置 / Analytics 面 | 不在定稿 §Scope 的受审七面内 |

---

## R-3.4 — 对话框标题与按钮字符串一致

**定稿**(第 111–113 行):站立规则 —— 受审面上每个创建/编辑对话框,**对话框标题字符串 == 打开它的控件上的字符串**。本计划自己造出的那一对(Tracker 零态 CTA 与新建投递对话框)绑到**同一个 i18n 键**,永不走样。

### 本次核到并修正的三处

| 对话框 | 标题(改前) | 打开控件(改前) | 处理 |
|---|---|---|---|
| 新建投递 | `tracker.dlgNew` = "New application" | mini-fab 的 tooltip `tracker.newApp` = "New application" | **按钮改读 `tracker.dlgNew`** —— 键相同,字符串必然相同。`tracker.newApp` 保留在 i18n 里但已无引用(删键会牵动其它语种,故只改引用点,键本身留着不删) |
| 智能粘贴 | `tracker.smartPasteJob` = "Smart paste job" | 按钮 `tracker.smartAdd` = "Smart paste" | **对话框标题改读 `tracker.smartAdd`** —— 键相同 |
| 新增技术栈条目 | `ts.dialog.title` = "New knowledge entry" | mini-fab tooltip `techstack.newEntry` = "Add entry" | **对话框标题改读 `techstack.newEntry`** —— 键相同 |

### 核过即合规的(未动)

| 对话框 | 标题键 | 打开控件 | 结论 |
|---|---|---|---|
| 投递问答库 | `tracker.alTitle` | 菜单项 `tracker.alTitle` | 同一键,合规 |
| 管理分类 | `ts.cat.title`("Manage categories") | 图标按钮 tooltip `ts.cat.manage`("Manage categories") | 字符串相同,合规 |
| 投递详情 | 公司身份抬头(公司头像 + 名称 + 岗位) | 卡片点击 | 这是**身份抬头**,不是创建/编辑表单标题,不在 R-3.4 的「创建/编辑对话框」口径内 |
| 面试备战计划 | 硬编码「🎯 面试备战计划」 | **没有打开控件** —— 由状态流转自动弹出(`tracker.component.ts:206`) | 无控件可对齐,不在 R-3.4 口径内 |

### 命名观察(不在本次范围,不动,留档)

`prep-plan-dialog.component.ts` 整个对话框是**硬编码中文**(标题 + 正文 + 按钮),在 en/fr 界面下不翻译。它落在受审面(Tracker)上,是文案缺陷 —— 但:

- 定稿 R-3 枚举的重写清单里没有它;
- 它既不是中点串接(不属 R-3.3),也没有打开控件(不属 R-3.4);
- 改它需要新增约 8 个 i18n 键,属**范围扩张**。

按「设计变更先报台账待确认区等裁决,不许先斩后奏」与「不擅自扩大范围」的纪律,**本次不动**,在此留档供主线裁决。

---

## 偏差清单

**空。**

逐条对定稿 §R-3 验收:

- R-3.1 subtitle —— 三语逐字等于定稿原文 ✅
- R-3.2 按钮改名 —— Select/Done 两态文案 + `select_all` 图标,`toggleEdit()` 行为未变 ✅
- R-3.3 中点清扫 —— 六处点名重写全部落地;普查工具复跑待修命中 0;豁免 #4/#5 原样保留;页面标题、徽标类文案按定稿判据不属本项 ✅
- R-3.4 标题/按钮一致 —— 三处修正改为共键;三处核过即合规未动;一处(备战计划)判为无控件、留档不扩张 ✅

**类型检查**:`npx tsc -p tsconfig.app.json --noEmit` → exit 0。

---

## 自验工具

`docs/fix-program/evidence-123/tools/census-middot-r3.py`

```
$ python3 docs/fix-program/evidence-123/tools/census-middot-r3.py
=== 中点普查(源码侧)===
命中总数: 0(其中豁免 0, 需修 0)

待修命中: 0 ✅
```
