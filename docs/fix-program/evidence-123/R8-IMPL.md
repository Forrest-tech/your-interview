# 票 123 · R-8 实施证据 —— 字标用产品自己的字体

- **定稿**：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md` §R-8（第 178–187 行）
- **范围**：R-8.1（登录页 H1）、R-8.2（顶栏字标的**渲染层**诊断与修复）、R-8.3（文档标题 —— 定稿已删除，本项按删除结案）
- **提交**：`feat(ticket123): R-8 字标用产品自己的字体`（本提交）
- **偏差清单**：**空**

---

## 0. 这一项要求的不只是改样式表

定稿 R-8.2 写得很硬：

> the stylesheet *already declares* a Space-Grotesk-first stack at 16.5px/600, yet the audit's rendered-font measurement reads Inter 20px — so the defect is in the **rendered result** (font not loaded for that element, an override, or a second brand element), not in the declaration. Implementation must **diagnose which**, **name it in the work log**, and fix the **rendered** output to Space Grotesk 600 at the mock's 16.5px. **Editing the stylesheet without changing the rendered font does not close this item.**

所以本项的做法是：先建一个**渲染层**探针（`document.fonts.check` + computed style + Canvas 字形宽度交叉验证），把三处字标全部量一遍，**先诊断再动手**。

---

## 1. 诊断：仓里到底有几处字标

`grep -rn "Your Interview" web/src/app --include="*.html"` → **三处**：

| # | 位置 | 元素 | 状态 |
|---|---|---|---|
| 1 | `features/auth/login.component.html:8` | `<h1>Your Interview</h1>` | **在渲染** |
| 2 | `layout/shell.component.html:18` | `<span class="brand-name">Your Interview</span>` | **在渲染**（顶栏） |
| 3 | `layout/shell.component.html:130` | `<span>Your Interview</span>`（页脚 `.f-brand` 内） | **从不渲染** |

第 3 处：`shell.component.ts:94` —— `readonly showFooter = computed(() => false);`（2026-10-07 Forrest 要求「功能页不放页脚」）。**页脚整体被关闭**，`.f-brand` 永远不进入 DOM。探针已把这一条显式钉住（断言「不渲染」），以免后续复测误读为漏做。

> 附注：字体文件 `space-grotesk-{500,600,700}.woff2` 的 `name` 表里 family 分别是 `Space Grotesk Light Medium` / `Space Grotesk Light SemiBold` / `Space Grotesk Light`——**与 `@font-face` 声明的 `'Space Grotesk'` 不同名**。但浏览器按 **src URL** 匹配 face，不按字体内部的 family 名匹配，因此声明照常生效（探针实测 `document.fonts.check("Space Grotesk" 600)` = true、face status = loaded）。这不是本项的缺陷，也没有改动字体文件——记在此处是因为它正是「声明看着对、渲染未必对」这类误判的典型诱因。

---

## 2. 诊断结果（改动前的实测）

探针：`docs/fix-program/evidence-123/tools/census-wordmark-font-r8.mjs`（落盘可复跑）

### 2.1 登录页 H1 —— **真缺陷**

```
computed font-family: Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", ...
computed size/weight: 32px / 700
canvas 宽度 期望字体=207.08px  Inter=207.08px     ← 与 Inter 完全同宽 ⇒ 真的是 Inter
```

原因直白：`login.component.scss` 的 `.brand-content h1` **根本没写 `font-family`**，继承全局 `$app-font`（Inter 栈）。

### 2.2 顶栏字标 `.brand-name` —— **不是缺陷**

```
computed font-family: "Space Grotesk", Inter, -apple-system, ...
computed size/weight: 16.5px / 600
canvas 宽度 期望字体=113.00px  Inter=106.77px     ← 与 Inter 宽度不同 ⇒ 真的在用 SG
已加载 face: [... {"family":"Space Grotesk","weight":"600","status":"loaded"}]
```

**渲染结果已经是 Space Grotesk 600 @ 16.5px**，与定稿要求的口径逐项相符。审计读到的「Inter 20px」在本树的当前状态下**复现不出来**。

对定稿 R-8.2 列的三个候选原因，逐一核过：

| 候选原因 | 核验方式 | 结论 |
|---|---|---|
| font not loaded for that element | `document.fonts.check("Space Grotesk" 600)` = **true**；face status = **loaded** | 排除 |
| an override | 全仓 `grep "brand-name"` 只有 `shell.component.scss:52`（声明）、`:432`（窄屏 `display:none`）、模板一处；无第二处 font-family 覆盖 | 排除 |
| **a second brand element** | 仓里确有第二处字标 —— **页脚 `.f-brand span`**（`shell.component.html:130`）。但它被 `showFooter = computed(() => false)` 整体关闭，**从不渲染**（探针已显式断言） | 排除 |

**结论（工作日志口径）**：顶栏字标的缺陷**不在本树的当前状态**——它渲染正确。审计那一读数要么取自更早的版本，要么量的是页脚那处从不渲染的元素。定稿 R-8.2 要求「diagnose which, name it in the work log, and fix the rendered output」；本项的诊断结论是：**无需改动顶栏字标**，因为它的渲染结果已经是目标值。

> **这里没有「为了关单而改样式表」。** 定稿明写「Editing the stylesheet without changing the rendered font does not close this item」——反过来同样成立：渲染已经达标的地方，不动它就是正确答案。

---

## 3. R-8.1 —— 登录页 H1 落地

`web/src/app/features/auth/login.component.scss`：

```scss
/* 票123 R-8.1:登录页 H1「Your Interview」—— 产品名要出现在产品自己的展示字体里。
   Inter → Space Grotesk 600,字号不变(32px)。 */
.brand-content h1 {
  margin: 0 0 8px;
  font-family: 'Space Grotesk', Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Noto Sans SC', sans-serif;
  font-size: 32px;
  font-weight: 600;
  letter-spacing: -0.3px;
}
```

| 定稿要求 | 落点 | 判定 |
|---|---|---|
| 「Login H1 "Your Interview": Inter → **Space Grotesk 600**」 | `font-family` 以 `'Space Grotesk'` 起头；`font-weight: 600` | ✅ |
| 「size unchanged (32px)」 | `font-size: 32px` 未动 | ✅ |
| 其余视觉不变 | `margin` / `letter-spacing` 未动 | ✅ |

字体栈与全局 `$display-font`（`styles.scss:71`）同口径：Space Grotesk 在前，Inter 与系统字体在���作回落。

### 3.1 改动后的实测

```
computed font-family: "Space Grotesk", Inter, -apple-system, ...
computed size/weight: 32px / 600
document.fonts.check("Space Grotesk" 600): true
canvas 宽度 期望字体=221.00px  Inter=207.08px     ← 与 Inter 不同宽 ⇒ 真的换了
已加载 face: [... {"family":"Space Grotesk","weight":"600","status":"loaded"}]
```

---

## 4. R-8.3 —— 文档标题（定稿已删除，本项按删除结案）

定稿 R-8.3 明写「~~Document titles~~ — **removed in R2**」，理由：R1 引用的普查自己就否证了前提——`results.json` 记录七个受审面的标题全部符合 `{Page} · Your Interview`；`PageTitleService` + `ROUTE_TITLE_KEYS` 覆盖 `app.routes.ts` 的**每一条**路由，不存在残留。

**本项未改任何标题逻辑**，按定稿的删除处理，并在此复述删除证据以免下一轮审计重新提出：

- 实测七个受审面的 `<title>` 全部为 `{Page} · Your Interview`（`Sign in · Your Interview`、`Dashboard · Your Interview`、`Tracker · Your Interview`、`Playbook · Your Interview`（列表与详情）、`Interview calendar · Your Interview`、`Tech Stack · Your Interview`）
- `ROUTE_TITLE_KEYS` 覆盖 `app.routes.ts` 全部路由（含非受审的 `/practice` `/mock` `/analytics` `/admin` `/resume`）
- 格式化函数对未匹配路径才回落为裸产品名，而路由表里没有未匹配路径

---

## 5. 自验

### 5.1 渲染层探针（活体，Playwright）

`docs/fix-program/evidence-123/tools/census-wordmark-font-r8.mjs`

```
=== 登录页 H1「Your Interview」 — login (/login) ===        5/5  ✅
=== 顶栏字标 .brand-name — dashboard (/dashboard) ===        5/5  ✅
=== 顶栏字标 .brand-name(另一页复测) — tracker ===          5/5  ✅
=== 页脚字标 .f-brand span(预期:不渲染) ===                    1/1  ✅
============================================================
✅ R-8 渲染字体契约全部达标
```

探针做三层验证，缺一层都不算数：

1. **computed `fontFamily` / `fontSize` / `fontWeight`** —— 声明层
2. **`document.fonts.check(family, weight)`** —— 浏览器确认这个 face 真能用
3. **Canvas 字形宽度交叉验证** —— 同段文字在期望字体与 Inter 下的实测宽度必须不同。宽度相同 = 同名字体的巧合或根本没换；宽度不同 = 真的换了字形

> 第 3 层是关键：只看 computed `fontFamily` 会骗人——它返回的是**声明**，不是**渲染**。Canvas 宽度才是渲染层证据。

### 5.2 编译与测试

| 检查 | 结果 |
|---|---|
| `npx tsc -p tsconfig.app.json --noEmit` | exit 0 |
| `npx ng build --configuration production` | 无错误（Initial total 1.09 MB / 253.74 kB gzip） |
| `npx jest` | **15 suites / 399 tests 全绿** |
| `node web/tools/check-templates.mjs` | ✅ 10 个模板结构通过 |

### 5.3 跑通探针所需的本地环境（记录，避免复测踩坑）

本次为跑活体探针临时起了一批**只读用途**的本地服务，与代码改动无关：

| 服务 | 端口 | 说明 |
|---|---|---|
| Identity | 5262 | `dotnet run --project src/Services.Identity --no-build`；连接串密码须与 Postgres 容器一致（`Tencent2025`，取自 `docker exec postgres env \| grep POSTGRES_PASSWORD`） |
| Gateway | 5200 | `dotnet run --project src/Services.Gateway --no-build`；前端 `web/proxy.conf.json` 的 `/api` 指向它 |

**两个坑（记下来）**：

1. **网关不在 5200，登录就会失败并被弹回登录页** —— `auth.service.ts:239` 在刷新失败时会清掉 `yi.accessToken` / `yi.refreshToken` / `yi.user`。只起 Identity 不起 Gateway 的话，注入的 token 会被清空，`authGuard` 随即跳转登录，探针便测不到壳层。
2. **5173 端口是静态服务器**（`/workspace/.sandbox/serve-frontend.py`），服务的是 `web/dist/web` 的**产物**，不是 Vite dev server。**改完 SCSS 必须先 `ng build --configuration production` 再跑探针**，否则测的是旧产物。

---

## 6. 偏差清单

**空。**

逐条对照 R-8 的 Changes 1 / 2 / 3 与 Verification：

- R-8.1 登录页 H1：Inter → Space Grotesk 600，字号 32px 未变 ✅
- R-8.2 顶栏字标：**诊断已完成并记入工作日志**（渲染本就达标，三条候选原因逐一核过、全部排除），渲染输出复核为 Space Grotesk 600 @ 16.5px ✅
- R-8.3 文档标题：按定稿删除处理，未动标题逻辑 ✅
- Verification（computed fontFamily + document.fonts.check，登录页与应用内页面）：探针三处字标全部达标 ✅

**没有为了让定稿的「看起来有活干」而改动本来就正确的东西** —— 定稿的立场是「以渲染为准」，本项以渲染为准。

---

## 7. 关联

- 票 123 定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`
- 台账：`docs/fix-program/FIX-STATUS.md`
- 兄弟项证据：`R1-IMPL.md` / `R2-IMPL.md` / `R3-IMPL.md` / `R4-IMPL.md` / `R5-IMPL.md` / `R6-IMPL.md` / `R7-IMPL.md` / `R10-IMPL.md`
