# FIX-STATUS · 对账台账

> **运行回执（按 PROTOCOL §4 首次启动 · 2026-10-24 WorkBuddy 接手）**
> - **远端 tip**：`df9743d`（`refs/heads/develop-new-features`，2026-10-24 `git ls-remote` 确认；本线开工基线 = `86a538f`，期间主线并入票 125/130/131/132/133，本线已 merge 并入远端）→ 推送时远端曾前进到 `9da5ea7`（主线票124/125 接手登记），本线 merge 后再推；其后主线再推票 123 接手修复三笔（`1c68646`/`5e3cc6a`/`df9743d`），本线已 fast-forward 并入
> - **未完成总数**：46（TODO 9 + PUSHED 27 + BLOCKED 3 + B 组锁定 3 + 异议待裁决 5 + 本线 DOING 0；VERIFIED 票 41 + **123**；明细见队列表）→ 票 123 实现完成转 PUSHED → **主线独立复测五轴均值 9.5 达线转 VERIFIED**；票 124 转 DOING → **票 124 亦实现完成转 PUSHED**（Forrest 已定夺接受本线实现，主线只做独立复测），本线 DOING 归零。**再注（2026-10-24，A3-3）**：本行 46 为旧快照，最新状态以队列表与条目为准，S4 前重算。**注**：主线 `9da5ea7`（17:07 +0800）另登记「票 124/125 改由主线接手」且其 tip 内**无任何票 124 实现代码**（已 ls-tree 核实）→ 无重复实现；票 124 以本线 `6cbdebd` 为唯一实现入账，执行权归并说明见第三节。
> - **执行模型**：MiniMax-M3（PROTOCOL §10 锁定 Kimi-K2.7-Code；本会话实际模型为 MiniMax-M3，按 §10 末句「发现被切换，在运行回执区登记并继续工作」登记于此，切换由 Forrest 处理）
> - **事实源**：`docs/fix-program/source/`（AUDIT.md + 票面 + replica 定稿 + HANDOVER.md）
> - **本文件纪律**：队列与状态唯一事实源；没到远端且验过的成果，不报「已完成」。

> **当前 DOING**：**无**。本线两票（123、124）均已交付：**123 已由主线独立复测 9.5 达线转 VERIFIED**；**124 Forrest 已定夺接受本线实现、主线复测中**（如有偏差会以票的形式返回）。票 125 归主线（已实现完成，待 rebase 落地 + 独立复测），**本线不做 125**。本线暂无在办票，等 Forrest 后续指令。
> - **注记（票 124 范围钉死）**：恢复单位 = 节点 + 全部已删后代、零迁移（删除时间取行 `UpdatedAt`）、恢复响应回整树包 `{restored, treeVersion, nodes}`、入口镜像 Tech Stack 回收站 + 计数徽标、不含永久删除、不动保存熔断。实现首步先折入微瑕 **N1**（规格第 6、337 行两处「附录 V1–V29」指针订正为 V1–V30）。
> - **票 123（设计修复）已转 VERIFIED**（本线实现 `0c743b4` + `89c35ac` + `d139fc3`；主线接手修复 `1c68646` + `5e3cc6a`；独立复测五轴均值 **9.5 达线**、不符清单空、CI run `40085137066` 三项全绿，`df9743d` 关案）。
> - **票 124（回收站）已于 2026-10-24 实现完成并置 PUSHED**（实现提交 `6cbdebd`；证据 `evidence-124/IMPL.md`；偏差清单空）。**Forrest 已定夺：接受本线实现，主线只做独立复测**；两条待裁决项均按 A 关案（与本线实现逐字一致，无需改动）。

> **本票实施进度（WorkBuddy · 逐票独立提交 · 推 develop-new-features）**
>
> | 项 | 提交 | 证据 | 偏差清单 |
> |---|---|---|---|
> | Phase 0 | `a7f2241` 前的审计 | `evidence-123/PHASE0-AUDIT.md` | — |
> | R-1 令牌残留扫掠 + R-2 调色板全量清扫 | `1c5c404` | `evidence-123/R1-R2-IMPL.md` | 空 |
> | R-7 内容度量与页标题统一到全站口径 | `d7a8186` | `evidence-123/R3-IMPL.md` 附 | 空 |
> | R-3 文案清扫与中点清除 | `a7f2241` | `evidence-123/R3-IMPL.md` | 空 |
> | R-4 看板收尾（等待行/chip 行/可访问名） | `735d95b` | `evidence-123/R4-IMPL.md` | 空 |
> | R-5 标注空值 | `a13eb5f` | `evidence-123/R5-IMPL.md` | 空 |
> | R-6 Tracker 零态卡 | `b68d25d` | `evidence-123/R6-IMPL.md` | 空 |
> | R-10 热力图邀请行 | `a0e4733` | `evidence-123/R10-IMPL.md` | 空 |
> | R-8 字标用产品自己的字体 | `e238691` | `evidence-123/R8-IMPL.md` | 空 |
> | **R-9 The Track（共享几何源 → Dashboard Band → Playbook 轨道 → 抽屉时间轴迁移）** | **`6a349db`** | **`evidence-123/R9-IMPL.md`** | **空** |
> | §C 四项（C-1 令牌+reduced motion → C-2 焦点补全 → C-3 hover/press → C-4 contentIn+展开动效） | `0c743b4` | `evidence-123/C-IMPL.md` | 空 |
>
> - **R-9 实施注记（2026-10-24）**：
>   - 落地六件事 —— ①共享几何样式源 `web/src/styles/_track-geometry.scss`（`:root` 自定义属性；选它而非 SCSS mixin 的理由：四个消费者跨四个文件、其中一个是组件内联样式表，且几何值须能被运行时探针从 computed style 读到）；②共享阶段常量 `core/models/pipeline-stages.ts`（Tracker 与 Dashboard 读同一份，**搬家不是复制**）；③Dashboard Pipeline Band（取代 Pipeline chips 卡；节点=规范阶段序列+断口后 Rejected 裁决节点；状态与 Tracker stepper 逐字一致；caption 只用 payload 能算的字段；CTA 用既有 `dash.viewTracker` 且无文字箭头）；④band→Tracker 的 `route.queryParamMap` 交接（缺席/非法值一律不改状态）；⑤Playbook Rounds Track（竖向轨道取代 chip 选择器；填充规则照定稿：done=有结果或日期已过、current=最早的「未来且无结果」；节点字形只有 reached/hollow，裁决色只在事实行的 Outcome 值）；⑥抽屉时间轴迁移（dot 20px→16px、轨道 `#E3E6EA`→共享待决色、done 的「品牌描边+8px 中心点」字形→共享 reached 处理）。
>   - **几何合并门通过**：探针 `web/tools/census-track-geometry-r9.mjs`（Node+Playwright，读 computed style）在四个面量到**逐字相同**的值 —— 节点 16px×16px、描边 2px、轨道粗细 2px、reached/future/go/stop 描边色同档、待决轨道色 `rgba(0,0,0,0.12)`；状态覆盖 Tracker future×40/reached×33/go×2、Dashboard reached×4/go×1/stop×1、Playbook reached×4/future×1、抽屉 reached×1/future×4。
>   - **探针自身的三处度量缺陷已修正并记入证据文档**（都是探针问题，不是实现问题）：只量首节点（改为量全部节点、按祖先链状态类归类）/ 把轨道**长度**当契约值（改为量**粗细** `min(w,h)`，长度另打印备查）/ Playbook 用了列表页 `/playbook`（改为登录后取条目 id 进 `/playbook/:id`）。
>   - **活体环境三个坑（记入证据文档，供后续复测复用）**：Gateway 期望 Jobs=5263 / Interviews=5264（`Services.Gateway/appsettings.json` 的 `ReverseProxy.Clusters`），`dotnet run` 会被 launchSettings 的 `applicationUrl` 覆盖，须用 `--urls` 指定；Interviews 的连接串键是 `ConnectionStrings__InterviewsDb`（**不是** `__Interviews`）；Postgres 密码须从 `docker exec postgres env | grep POSTGRES_PASSWORD` 读（`Tencent2025`，与 `sandbox-start.sh` 里写的 `postgres123` 不一致）；沙盒 cgroup 上限 8GB，四个 .NET 服务常驻时 `ng build` 会被 OOM kill，须先停服务再构建、构建完再起。
>   - **自验**：tsc 无错误 · Jest **15 suites / 399 tests 全过** · `ng build --configuration production` 成功（bundle 预算警告为既有项：基线 611.10 kB → 本次 612.27 kB，Δ1.17 kB）· `check-templates` 23 个模板通过 · 几何探针 0 项未达标。
- **§C 实施注记（2026-10-24）**：
  - **C-1 令牌 + reduced motion** —— `styles.scss` `:root` 落四个动效令牌（`--dur-fast: 120ms` / `--dur: 160ms` / `--dur-slow: 400ms` / `--ease: cubic-bezier(0.2, 0, 0, 1)`，与定稿逐字一致）；受审面 **17 条** `transition:` 全部 token 化（tracker 3 / playbook 3 / calendar 1 / techstack 3 / auth 1 / layout 6 = 17），映射按定稿「≤130ms→fast、150–220ms→dur、单条 400ms→slow、bare ease→var(--ease)」；末尾落定稿原文的全局 `prefers-reduced-motion` 块（逐字照抄，app-wide 压住 AI Practice 录音脉冲与抽屉 `drawerSlideIn`，是决定非意外）。
  - **C-2 焦点补全** —— 去掉全局 `:focus-visible` 的强制 `border-radius: 4px` 与 `.app-card`/`.entry` inset 变体的 `8px`（offset 不动）；四个点名站点按名转换（`.plain-area` / `.paste` / `.blk-editor` / `.doc-editor`），各自保留既有 border-color 线索并补共享焦点环；Sankey 节点组加 `tabindex="0"` + `role="button"` + `aria-label`（标签+计数），Enter/Space 调同一个 `selectFlowStage()`，焦点由**既有** `.selected` 描边（`stroke: var(--brand)` / 3.5px）表达。**剩余 `outline: none` 全仓 11 处（AI Practice 9 + `app/shared/material-tree` 2）全部落在定稿点名的 out-of-scope 集**，无一处进豁免表。
  - **C-3 hover/press 统一** —— 落三个令牌（`--hover-wash: rgba(46,58,140,0.06)` / `--press-wash: rgba(46,58,140,0.12)` / `--hover-wash-dark: rgba(255,255,255,0.14)`，逐字一致）；两种合法形态守住：**wash** 用于 clickable rows/items（白底侧栏 `.side-item` 也取 wash，midnight 顶栏 `.nav-item` 取 `--hover-wash-dark`），**lift** 仅 Playbook `.entry` 卡片按原样保留（shadow + `translateY(-2px)`，transition 由 C-1 token 化）；删掉 Tracker `.list-row:hover` 的扁平体系违规 `box-shadow`；取洗色的元素补 `:active` 按压洗色。破坏性按钮/链接/纯显隐/滚动条/拖拽 handle 保持原样（不属「clickable rows and items」）。
  - **C-4 状态编舞** —— 唯一新 keyframe `@keyframes contentIn` + 工具类 `.content-in`（定稿原文逐字）；六个面各挂**一次**：Dashboard / Tracker / Playbook list / Playbook detail / Tech Stack（`.page content-in`）+ Calendar（`.cal-page content-in`）；R-9 轨道填充 `transition: height var(--dur) var(--ease)`、当前轮卡片 `.rexpand` 用 `contentIn`；**明令拒绝的三项一条未做**（逐区块 stagger / 滚动淡入 / 对话框额外出场）。全仓 `@keyframes` 普查：本次新增**恰好一个** `contentIn`。
  - **两处需判断、已按「不改行为」处理并记入证据文档（非偏差）**：①`playbook-calendar.component.scss:73` 与 `techstack.component.scss:418` 原本是 `background 0.15s`（**无** easing 关键字，走 CSS 默认 ease），故只 token 化时长、保留默认缓动语义，不强加 `var(--ease)`（Material emphasized 曲线会改手感，超出定稿授权）；②`.sankey-node-g:focus-visible { outline: none; }` 是定稿 C-2 明写的焦点表达方式（焦点由 SVG stroke 承担），不属于违规的「剩余 outline:none」。
  - **探针自身的六处度量缺陷已修正并记入证据文档**（六次红全是探针问题、无一次是实现偏差，未出现「改实现迁就探针」）：`ROOT` 少拼 `web/` / `:focus` 分支吃掉相邻 `:focus-visible` 块 / 多值 `transition-duration` 整串比对误判 / 视图切换按钮只有图标无文字（改按 `.view-btn` 图标找）/ `.paste` 线索在父容器 `.paste-box:focus-within` / `:active` 用 class 模拟测不到（改 `mouse.down()` 真按压）。
  - **自验**：tsc 无错误 · Jest **15 suites / 399 tests 全过** · `ng build --configuration production` 成功 · `check-templates` 23 个模板通过 · **三个活体探针全过**（`census-craft-r9c.mjs` §C 四项全达标 / `census-track-geometry-r9.mjs` R-9 四面几何逐字一致 / `census-wordmark-font-r8.mjs` R-8 字体契约达标 —— §C 改动未破坏 R-8/R-9）。


---

## 队列速览（状态机：PROTOCOL §2）

| 状态 | 票号 | 说明 |
|---|---|---|
| **VERIFIED** | 41 | CI run `39888149532` Backend/Frontend/E2E 全 success；证据 `docs/fix-program/evidence-41/` |
| **PUSHED（本执行线）** | 42 | ARC-11 前端 API 层收敛收口；远端 `277919c`；证据 `docs/fix-program/evidence-42/`（PLAN + VERIFICATION）；features/** 字面量归零、tsc --noEmit 0 错；**待 CI E2E 黄金路径复跑确认后转 VERIFIED**（协议 §3 第四步） |
| **PUSHED（非本执行线 · 本线独立复验）** | 43 | 并行线落地 `92aa138` + `239412f`；本线以自建工具独立复验：parity+boundary **42/42 PASS**、引用键审计 **570 引用 / 0 缺失**、tsc 0 错、ng build exit 0（基线取其合并前 tip `f594b28`；复验时 tip `6dd2304`，该区间触及 i18n 文件仅本票一例。其后 `3946b3b`（票 79）向核心 DICT 新增 1 键字段级校验文案，属后续票新增，不影响本票判定——键集只增不减/既有条目零改动对该键天然成立，DICT 净增由 +498 变 +499）；证据 `docs/fix-program/evidence-43/`；CI 状态沙盒无凭证无法核实，待确认后转 VERIFIED |
| **PUSHED（非本执行线 · 证据待补）** | 35, 36, 38, 39, 40, 44, 45, 47, 48, 49, 50, 51, 52, 53, 54, 64, 65, 70, 77, 78, 79, 88*, 91*, 92* | 远端已含实现（sha 见第三节「并行线落地批」登记表；35/36/38/39/40/44 为早期落地、本台账此前未入队，现补登记）；非本线实现，测试/活体证据由并行线补登记；票 48 内容并入 `4626aba`（票 95 编外提交，随异议 1 待裁决）；带 * 号为正单 series 票（88–93 撞号，见异议 2 回执与第一节） |
| **VERIFIED（第二道独立真人化验收通过）** | 21 | COD-08：Playbook 待办勾选接真实端点且不吞错；实现 `b1a3d7a`；第二道复验 `~/workspace/ticket21/VERIFY-21-R2.md`：A 成功路径（落库+刷新保持）与 B 失败路径（拦截 PATCH→500，0.5s 内 snack 可见 + checkbox 回退 + 服务端未假写入，首轮未证项至此闭环）+ tsc 0 均过；不符清单空（见台账末 21 条） |
| **VERIFIED（主线独立复测 9.5 达线）** | 123 | 设计修复（票 118 后复测 8.3/10 未达 9.5 线催生）；定稿 `source/ticket123-REMEDIATION-PLAN.md` R4（评分存证 R4 10.00 PASS）；设计稿 `source/ticket123-mocks/` = 契约，逐项对照不得走样；范围 R-1…R-10 + §C 四项（G-items 为可选阶段，**不在本次范围**）；实现提交 `0c743b4`（+ 台账补正 `89c35ac` + 并入主线 `d139fc3`）；**偏差清单空**；证据 `docs/fix-program/evidence-123/`（11 份实施文档 + 12 份探针）；自验：tsc 0 · Jest 19 suites/476 tests · ng build 生产成功 · check-templates 23/23 · 三个活体探针全过（§C 四项 / R-9 四面几何逐字一致 / R-8 字体契约）；**不自评关单**（9.5 线由主线独立复测判） |
| **VERIFIED（主线快速复测 R2，见条目 484；本行原 PUSHED 为条目 484 落地后未同步的 stale 措辞，现订正 A3-2）** | 124 | 回收站（仅 Assessment 练习素材）；定稿 `source/ticket124-SPEC.md` R2（评分存证 R2 9.99 PASS）；范围钉死：恢复单位 = 节点 + 全部已删后代、零迁移（删除时间取行 `UpdatedAt`）、恢复响应回整树包、入口镜像 Tech Stack 回收站 + 计数徽标、不含永久删除、不动保存熔断；实现首步折入微瑕 N1（规格第 6/337 行「V1–V29」→ V1–V30）；实现提交 `6cbdebd`；**偏差清单空**；证据 `docs/fix-program/evidence-124/IMPL.md`；自验：后端 T1–T14 **14/14** + 权限钉测、Jest **20 suites/514 tests**、tsc 0 错、check-templates 24/24、ng build 生产成功、活体 API 层 **36/36**（E1–E8）+ 浏览器 UI 层 **16/16**（U1–U7，截图 `/workspace/ticket124-recognition/`）；**执行权归并说明见第三节「票 124 执行权归并说明」条**（主线 tip `9da5ea7` 未落地任何票 124 实现代码 → 无重复实现，本提交为唯一实现）；**不自评关单**（9.5 线由主线独立复测判） |
| **VERIFIED（主线独立复测）** | 125 | AI Practice 五项评审修复（追问取 score.recognized / 全量 i18n / 删外层标题行 / Take 整行点击 / 第五项候选与判别）；实现 `5842711`；主线独立复测不符清单为空转 VERIFIED（见条目 477） |
| **VERIFIED（主线独立复测 + 独立 UI 复测 9.7 达线）** | 130 | 词典点词慢与白条修复（C1 预热 + H1 骨架定宽 + H3 延迟出示）；实现 `99896d2`；D-130-1 经用户 2026-10-24 裁决 (a) 追认 K-2 为定稿澄清后复判为过，零代码改动（见裁决执行条） |
| **VERIFIED（主线独立复测 + 独立 UI 复测 9.8 达线）** | 131 | 重音标签释放（纯前端，tabEnabled/cardHasAnyAudio）；独立复测不符清单为空转 VERIFIED（见条目 472） |
| **VERIFIED（主线独立复测 + 浏览器 E1–E4）** | 132 | 重载 take 音频回填（replayAt 本地有源判据 + GUID 懒拉取）；独立复测 + 浏览器段 22 PASS 转 VERIFIED（见条目 465） |
| **VERIFIED（主线独立复核）** | 133 | Prep 报告 12 常驻 + 1 条件模板；实现 `23a15a2` + 修复 F-133-R1 独立复核转 VERIFIED（见条目 469/470；Mac 侧 E1–E3/Q-1/maxTokens 校准列环境外清单） |
| **TODO（重构批）** | — | 51/52/53/54 已全部由并行线落地，本批清零 |
| **TODO（平台/跟进）** | 67 | 71、72、74、75 已对账销号（见台账末对账条）；64/65/70/77/78/79 已落地；正单 88 已落地（`fe3a4fd`）；正单 89/93 与增补 series 撞号且未实现，待定号后入队（见第一节）；正单 90 已落地（`ba3810b`）并转 VERIFIED（见台账末 90 条） |
| **BLOCKED-DECISION** | 68, 73 | 需 Forrest 拍板，跳过不做 |
| **PUSHED（聚合活体待 Mac/CI 验证）** | 94 | 票面在增补目录 `docs/fix-program/tickets/94-gateway-health-503-ocr-cluster.md`（原“无票面文件”为 stale 表述，现更正）；实现 `fb2e6a5` 已推（appsettings 补 7 业务集群 EnabledClusters、OCR 排除；hybrid 保留 4 集群覆盖；E2E 改回等 /health/live）；沙盒 other_tcp 策略拦截网关出站 HTTP，聚合活体（正向 200 + 反向 503）无法在沙盒完成，待 Mac/CI 验证，不算 VERIFIED（见台账末 94 条） |
| **B 组锁定（增补 series 号）** | 90, 91, 92 | 指增补 series（four-unverified-chains / coverage-phase2 / ocr-golden-set，`tickets/` 票面），三票均无实现提交；锁定已由裁决 5A 解除（冻结票 90/96 解冻），本行原「锁定有效」为裁决后未更新的 stale 表述，现更正；与正单 series 同名号票无关，勿再混淆（见异议 2 回执） |
| **异议区已登记（5A 闭案确认）** | 95, 98, 99, 100, 103 | 2026-10-24 Forrest 确认：五项即异议 1（95）、异议 3（98、99）、异议 4（100）、异议 5（103），均属裁决 5A 已判「登记滞后」就此结案之范围；本行原「待裁决」为裁决后未更新的 stale 行，现更正；五项冻结随 5A 闭案解除，后续跟进走正常先登记后开工 |

---

## 一、81–84 撞号（一次说清）

| 票号 | 真相 | 处置 |
|---|---|---|
| **票 81** | Log review 接线复习活动记录 | 正式版，`4bd0aa8` |
| **82-review-activity** | 与票 81 是同一件事——分支 `t82-work` 的 tip 就是 `4bd0aa8`（票81 提交本身） | **作废，吸收进票 81** |
| **票 82（正式）** | 首登路径导入被一次性闩锁吞掉 | 正式版，`0a92e70`，已落 develop |
| **票 83** | 超长分类名假成功静默不落库 | `44523c4` |
| **票 84** | 删分类后条目卡/chip 旧名不刷新 | `0d0af6a` |

即：历史上存在两个「82」——一个是 review-activity（与 81 重复，作废），一个是首登修复（正式版 `0a92e70`）。

### 1.1 88–93 双 series 撞号（2026-10-23 本线发现，待定号）

| 号 | 正单 series（`source/tickets/`，`e6719bc` 10-22 交接方直投） | 增补 series（`tickets/`，`6e553eb` 10-23 系统提升 A/B 组建档） |
|---|---|---|
| 88 | 部分有分时未评分维度不进 Top 3（`fe3a4fd` 已落） | A1 CI 流水线（`c0a5010` 已落） |
| 89 | 前端死 TestSpeech 方法删除（未见实现提交） | A2 E2E 接入 CI（`b95147b` 已落） |
| 90 | 框架异常映射缺口（`ba3810b` 已落，独立复测 VERIFIED，见台账末 90 条） | B1 四条未验链（B 组锁定，无提交） |
| 91 | Knowledge 发布侧补事件（`15c6639` 已落） | B2 覆盖二阶段（B 组锁定，无提交） |
| 92 | 分类 Add 双击竞态 500（`3c0f8f7` + `7254d20` 已落） | B3 OCR 金标准回归集（B 组锁定，无提交） |
| 93 | 更多 Weakness 死代码（未见实现提交） | B4 一条命令起环境（`e99d971` 已落） |

- 台账既有「已落地」88/89/93 指增补 series；队列表「TODO 88」指正单 series —— 混用曾在异议 2 造成误判（并行线被误指违反 B 组锁，执行者回执见异议 2）。
- 增补 94（网关健康 503 / OCR 集群）已推 `fb2e6a5`，聚合活体待 Mac/CI 验证（沙盒 other_tcp 策略阻断），与正单无撞号。
- 待 Forrest/协调者定号规则（建议：增补 series 改领 94+ 段或加前缀）；定号前本台账以 * 标注正单 series 票。

---

## 二、待确认区（阻塞项 · 需 Forrest 提供）

本仓库与当前沙盒中**不存在**以下文件，经查全部分支 git 历史确认**从未进过版本控制**：

- `AUDIT.md`（55 条审查单 = 票的完整清单与审查要求）
- `replica/` 定稿
- 原始 `FIX-STATUS` 主账

因此**无法判定「哪些票尚未修」**——没有提交就没有证据，但也不能反推「没提交 = 不用修」。
要完成真正的全量对账（逐票比对状态/sha/测试数字），需提供上述任一份事实源。

**已确认状态（据交接方口径，非本人推断）**：

| 票号 | 状态 | 说明 |
|---|---|---|
| 票 16 | 已关（第二道复验通过） | 81–84 全部落定后 T2/T5/D4b 复验通过，见台账末“票 16 关闭”条 |
| 票 21 | 已关（第二道复验通过） | 见台账末“票 21 关闭”条 |
| 票 68 | **待 Forrest 拍板** | Jobs 删除权限口径选择，不许自行实现 |
| 票 73 | **待 Forrest 拍板** | Tracker 收藏入口，属呈现新需求，需先出效果图 |
| 票 94 | 已推（聚合活体待 Mac/CI 验证） | 网关 /health/live 恒 503 已修（7 业务集群探活、OCR 排除）；原“OCR 集群无服务”为 stale 表述，现更正：OCR 服务存在（compose 有 ocr-service），是部分栈里 OCR 未起才拖垮旧探活；聚合活体待 Mac/CI 验证 |
| 票 90/91/92 | **B 组暂停** | 剩余审查票清完前不开工 |

**待 Forrest 裁决（本线登记，§7 格式）**：

| 票号 | 问题 | 选项 | 推荐 |
|---|---|---|---|
| 票 43 | i18n 单字典收敛（票面 ARC-13 mandate）使初始 bundle +86 kB 量级，越过 angular.json 原 1mb 错误线（不调则 `ng build` exit 1 → CI 红灯）。并行线已将 production initial maximumError 调至 1.2mb（`239412f`，实测 1012.63→1098.92 kB）；本线同构实现独立测得同量级结论（1010.09→1096.39 KiB，证据 evidence-43/PLAN.md「已知后果」）。属票面 mandate 的必然架构后果：字典由懒加载 chunk 移入随 shell 即时加载的核心 service，应用总包体基本不变 | A：维持 1.2mb（合并本体不动，仅预算线随「一套机制」架构再分配；maximumWarning 与组件样式预算未动） / B：回退 1mb 并改实现保预算（如字典拆懒加载子模块——违背票面「机制收敛为一套」mandate，需另票立项） | **A**（两线独立测量同结论；如不认可，回退 angular.json 该行即可，合并本体不受影响） |
| 票 124 | **规格 §5.3「断裂点重根不改断裂点节点的 CategoryId（原样保留）」在实体层无对应方法**：`PracticeMaterial.ParentId`/`SortOrder` 均为 `private set`，唯一能改父指针的公开方法是 `MoveTo(Guid?, int)`，它**必然同时覆盖 SortOrder**（`PracticeMaterials.cs:141-147`）。规格 §12 不变项 #4 明列「PracticeMaterial 实体、表结构、迁移集——不变」，故不能为它新增实体方法 | A：**调用 `MoveTo(null, entity.SortOrder)`** —— 传原 SortOrder 即「SortOrder 不变」，CategoryId 本就未被 MoveTo 触及（原样保留自动成立）；实体零改动、不变项 #4 不破。副作用：`MoveTo` 会 Touch UpdatedAt，故断裂点若自身已删，其 deletedAt 代理值会被刷新（但断裂点属防御性路径，规格 T13/T14 只断言「重根 + 出现在树里」，不断言其 deletedAt） / B：给实体新增 `ReRoot()` 只改 ParentId 不 Touch —— 严格更贴字面，但**破 §12 不变项 #4**（实体变更），且多一处公共 API 面 | **A**（实体零改动；规格 §5.3 明写「对它 `MoveTo(null, …)` 重根」——`MoveTo(null, …)` 本身就在定稿字面内，传原 SortOrder 是该调用的唯一取值方式，不属改稿。CategoryId 不变因 MoveTo 不触及该字段而自动成立） |
| 票 124 | **规格 §5.3「restored = 本次实际翻转的行数」与实体 `Restore()` 的幂等性**：`Restore()` 首行 `if (!IsDeleted) return;`（`PracticeMaterials.cs:190`），已恢复行是 no-op 且不返回布尔，故处理器**不能**靠调用结果计数 | A：**处理器在调用 `Restore()` 前先判 `IsDeleted` 自行计数** —— 完全贴合规格「实际翻转的行数」字面语义，实体零改动 / B：改 `Restore()` 返回 bool —— 动实体 API（不变项 #4）且影响既有保存处理器调用点 | **A**（规格字面即「实际翻转的行数」；幂等的 `Restore()` 不改、只在调用侧计数，是唯一的无损读法） |

> **✅ 裁决关案（2026-10-24，Forrest 经主线同步）**：票 124 两条待裁决项**均按 A 定案** —— ①断裂点重根调用 `MoveTo(null, entity.SortOrder)`；②restored 计数在调用 `Restore()` 前判 `IsDeleted` 自行计数。WorkBuddy 实现（`6cbdebd`）即按此落地，**实现与裁决一致，无需改动**。Forrest 同时定夺：**接受 WorkBuddy 的 124 实现，主线只做独立复测**；复测如有偏差会以票的形式返回。上述两行保留原文备查，状态由「待裁决」转「已裁决关案」。

---

## 三、已落地票清单（远端 develop 有提交 = 硬事实）


### 票 1
- `a370180` COD-04 轮次扩展字段部分更新(票01) — null保持/空串清空/非空赋值,不再静默清转写/备考题/邮件/录音

### 票 2
- `952b1dd` REQ-01+COD-14/票02 RemoveAiSettings 跨 schema 迁移加存在性守卫 + 全服务启动迁移失败 fail-fast

### 票 3
- `cb7e1fd` COD-01/票03 Knowledge 用户隔离 — Program.cs 补 AddCurrentUser 注册+KnowledgeItem 加 UserI
- `2589a55` COD-01/票03 Interviews 用户隔离 — InterviewEntry 加 UserId(expand→backfill→contract 3 
- `c60e682` COD-01/票03 Jobs 用户隔离 — Company/JobApplication/ApplicationCommunication 加 UserId(

### 票 4
- `7390730` REQ-02+ARC-07/票04 轮次结果回写同源 — SharedContracts InterviewOutcomeRecorded 加结构化 Inter

### 票 5
- `760876a` ARC-14/票05 结果判读单一事实源 — Interviews 列表/详情 DTO 新增结构化 Outcome(后端 DeriveOutcome 单一派生:
  - 测试: Interviews.Tests 88/88；88/88；EntryOutcomeDerivationTests/20

### 票 6
- `8db7904` COD-02/票06 ability 接口禁止替他人写入 — RecordAbility 一律以当前登录用户 Me 为准,RecordAbilityBody 移

### 票 7
- `1cab100` COD-05/票07 并发冲突统一映射 409 — ServiceDefaults 全局异常管线新增 DbUpdateConcurrencyException→

### 票 8
- `639f867` COD-06+COD-07/票08 转写契约对齐 — getTranscript 在 playbook-v2.api 适配层映射 fullText→transc

### 票 9
- `0804344` FUN-02/票09 手动建单/编辑 JD 与备注不再丢,全量 PUT 不再误清 — 前端 create() 负载补齐 jdText/notes(备注含 [DO

### 票 10
- `73f82ab` FUN-03/票10 Prep 打开既有条目不重复新建 — CreateEntryCommandHandler 对 JobApplicationId 幂等:当前
  - 测试: Interviews.Tests 93/93；93/93；CreateEntryDedupeTests/5

### 票 11
- `13b99c1` FUN-04/票11 JD 长文本保存不再 500 — JdText 列 varchar(40000) 扩为 text(迁移 20261022031113_Ex
  - 测试: Jobs.Tests 16/16；16/16；JdTextLengthGateTests/4

### 票 12
- `516d4ab` FUN-05 新建弹窗 Interview question 必填口径对齐 — question 标必填(*+required 属性),canSubmit/va

### 票 13
- `026e949` FUN-06 Mock 模式文案三语修复 — 实查缺失不止审计的 8 键:mock.component.ts + 会话页共引用 96 键、DICT 零定义 28

### 票 14
- `7ddfc67` COD-10a Tracker 收藏与自备问题上后端 — Jobs 新增 application_favorites/application_questions
  - 测试: Jobs.Tests 37/37

### 票 15
- `a3321de` COD-10b Playbook 本地数据上后端 — Interviews 新增 star_stories/legacy_debriefs/legacy_pas

### 票 16
- `cb37c52` COD-10c Tech Stack 分类与复习记录上后端 — Knowledge 新增 tech_stack_categories/review_activi

### 票 17
- `2edb30b` ARC-04 主简历单一真源 — 删除 Assessment 侧简历副本

### 票 18
- `ca629eb` ARC-09 JWT 与 Kestrel 上限收进 ServiceDefaults — 校验参数唯一定义+Jobs 兜底统一口径,七服务 Program.cs 

### 票 19
- `70bc7e7` ARC-06 Json 大字段强类型适配层——16 字段经 JsonFieldAdapter 版本信封({"v":1,"data":载荷原文})收敛,聚合 ba
- `5d772b2` ARC-06 特征测试先行——16 个 *Json 字段读写往返/旧数据兼容/三处形状判定查询锁定现状(基线 122→142 全绿)

### 票 21
- `b1a3d7a` COD-08 待办勾选接真实端点且不吞错——playbook-detail toggleAction 由 PUT /api/interviews/{id}/ac

### 票 22
- `0440255` COD-09 补齐 AddRoundDetails 迁移 Designer 并删手工脚本

### 票 23
- `a3463f7` COD-12 ListSessions 空 Issues 不再 500 — WeakestDimension 投影空集安全

### 票 24
- `9c97312` COD-13 AttachAsset 坏 JSON 返回 400 而非 500 — 共享映射加 JsonException→400

### 票 25
- `0c2b18a` COD-15 写操作改挂写权限 — SaveRecordingScore/Synthesize 由 MockRead 改 MockAnswer(仅两行授权属性)
- `ebe4a33` COD-15 红测先行 — AssessmentEndpointPermissionsTests(22例:两目标端点断言 perm:mock.answer 现红

### 票 26
- `4fd9247` REQ-05 GetEffectiveResume 文本简历回落 + Tracker Documents text 形态

### 票 27
- `1b6422e` REQ-08 Dashboard/Analytics 无数据不列弱项/Best——weakest 加 scored 守卫，零数据走既有空态

### 票 28
- `1502777` ARC-08a SmartParseRules 特征测试先行 + coverlet 接入与后端基线

### 票 29
- `8d29a69` REQ-09 SmartParse 形态缺口修复 — 内联Benefits可解/摘要跳元数据行/公司名FUN-09两形态+About误判

### 票 30
- `f99cfdd` REQ-10 轮次展示编号按日期序 + 新建轮次默认值旁注

### 票 31
- `6e37ad2` REQ-11 bulk-bar 改一行紧凑工具条 — 显式 flex-direction:row 覆盖 mat-card column(改前实测328px纵向堆

### 票 32
- `68bc118` REQ-15 抽屉 Overview 字段网格补 Work mode 展示 — 追加第7格(detail.workMode||'—',复用既有 i18n 键 t

### 票 34
- `faedc0b` REQ-18 投影对账与分叉修复 — KnowledgeLevelChanged 消费者改单条目台账增量迁移(废 CONF-AN-3 原值回写)+等级语义映射(
  - 测试: Analytics.Tests 51/51

### 票 37
- `7d8676c` FUN-10 文本简历可见可编辑 — 编辑态补 textarea(复用既有 resumeLabel/resumePh 三语键与 .resume-field 样式

### 票 46
- `ca24c6f` COD-17 死代码清理 — 删无端点 AddWeaknessCommand(record+Validator+Handler,全仓仅定义 6 行、零发送方/零
  - 测试: Interviews.Tests 184/184

### 票 55
- `e3864f6` 陈旧测试裁决修复 — AttachAssetValidator_MissingRoundId_Invalid 改为 EntryLevelWithoutRound

### 票 56
- `b192084` Analytics 回写可信通道(协调者裁决解法A)——SharedContracts 新增 analytics.writeback 权限并注册 Permiss
  - 测试: AbilityOwnershipTests 10/10；Worker.Tests 54/54；10/10

### 票 57
- `c3af5b7` OCR 邀请卡会议时间抽取 — extractInterviewWhen 扩展两类格式:ISO 日期与时间之间可夹星期(2026-10-13 周二 10:00～

### 票 58
- `6c8b8ed` LegacyOutcomeTextAdapter 补英文受控词整串精确匹配 — 关键词回落之前先做与 Interviews DeriveOutcome 规则2 
  - 测试: Jobs.Tests 12/12；消费者测试/4

### 票 59
- `09997d8` 共享 OCR 服务 + Playbook 入口切换 — 新建无状态 Python 服务 src/ocr-service(FastAPI+PaddleOCR PP

### 票 60
- `7f67913` 本地 OCR 文本头部适配 — 新增 adaptLocalOcrEmailText:仅当 source='local' 且 parsePastedEmail 无

### 票 61
- `6a37e73` §7 补测落地 — 新增 Services.Knowledge.Tests 项目(UpdateKnowledgeValidatorTests 22 例:Ques
  - 测试: 22/22
- `7011af9` 详情编辑保存不再清空 Question — saveDetailEdit 全量 PUT 的 question 改为回填 item.question(请求负载与本

### 票 62
- `7cc5132` SmartAdd 四长文本列扩 text + 200,000 统一闸门 (票62)

### 票 63
- `3144454` Mock「完整模拟」提交值 FullMock 与后端枚举 FullLoop 不一致 — 前端 modeOptions 提交值改 FullLoop 并订正 71-
  - 测试: 31/31

### 票 66
- `02510fb` MockSession 补软删除全局过滤器 (票66)

### 票 69
- `ab629ba` AddQuestions 对已持久化会话恒 409 修复 (票69)

### 票 76
- `a9e55b4` 超限请求体返回 413 而非 500 — 共享映射透传 BadHttpRequestException.StatusCode(Kestrel 超限框架带 413
  - 测试: 664/664

### 票 80
- `c6ee0a6` 详情页头部徽章与分组共用 outcome 单一事实源

### 票 81
- `4bd0aa8` Log review 接线复习活动记录——review() 成功回调补 recordReviewActivity(),热力图据实着色

### 票 82
- `0a92e70` 首登路径导入被一次性闩锁吞掉——三面(Tech Stack/票14 Tracker/票15 Playbook)同型改失败不闩锁+按账号闩锁+未登录不触发

### 票 83
- `44523c4` 超长分类名假成功静默不落库——前端提交前纯校验(validateCategoryName,UTF-16计与后端100同口径)+字段级报错含上限、service错

### 票 84
- `0d0af6a` 删分类后条目卡/chip旧名不刷新——删除成功经共享服务通知(categoryDeleted$)即时本地同步:条目卡/详情头/复习队列副本topic回落Unca

### 票 85
- `66487c3` InvitePasteParser 英文日期去宿主 culture 依赖 — GetCultureInfo("en-US") 在 invariant 宿主(生产
  - 测试: Ticket85/20

### 票 86
- `cf376aa` Assessment 另两 POST 端点授权对齐 COD-15 — AssessPronunciation 改 MockAnswer、TestSpeech 改
- `20cca9f` 红测先行 — AssessmentEndpointPermissionsTests 扩展(AssessPronunciation 期望 perm:mock.an

### 票 87
- `03afc0c` Assessment 模型快照对齐 — 空迁移剔 UserResume 快照残留 + LinkedSource 补 HasMaxLength(20)

### 票 88
- `c0a5010` A1 CI 流水线从零建立——后端 build+全量测试+覆盖率门 / 前端 tsc+build

### 票 89
- `b95147b` A2 E2E 接入 CI——每次 push/PR 自动跑黄金路径回归
- `d7398a1` feat(e2e): 票89 A2 补齐黄金路径 E2E-2/3/4/5,套件 5 绿 + 1 显式跳过
  - 测试: 5
- `e725bcb` feat(e2e): 票89 A2 E2E 冒烟回归套落地,首个回归用例取票80 场景

### 票 93
- `e99d971` B4 一条命令起环境 + 起后自检 + README 前置条件

### 并行线落地批（2026-10-23 机械登记；非本线实现，证据未由本线登记 —— 票 43 除外）

> 登记依据：`git log` 全历史机械对账（远端 tip `3946b3b` 均已含）。本线未逐票复验，仅登记「已落远端」事实；测试/活体证据由并行线补。带 * 号 = 正单 series（`source/tickets/`）票，与增补 series（`tickets/`，Forrest 系统提升 A/B 组）在 88–93 段撞号，见第一节与异议 2 回执。

| 票号 | 远端提交 | 一句话（据提交信息） |
|---|---|---|
| 35 | `355d1da` | FUN-07 硬编码中文清扫（后端三处结构化） |
| 36 | `92f1fe2` | FUN-08/COD-21 GET /api/analytics/ability/trend 落地 + 分析面接线 |
| 38 | `d03cb02` | ARC-10 AiGateway 客户端单一 adapter |
| 39 | `7b1efee` | ARC-12 文件存储单一 adapter |
| 40 | `e1c19c7` | ARC-15 契约与 Worker 同构合一（LLM 实现 3 客户端 + 工厂） |
| 43 | `92aa138` `239412f` | ARC-13 i18n 单字典收敛 + 预算伴随；**本线独立复验通过**（证据 `evidence-43/`） |
| 44 | `069aa5c` | REQ-12 页签标题（静态标题 + PageTitleService） |
| 45 | `184cbe0` | REQ-14 原生 confirm/prompt/alert 全量替换为站内弹窗 |
| 48 | 并入 `4626aba` | 内容随票 95 编外提交落地，随异议 1 待裁决 |
| 49 | `790e843` | ARC-17 删除模板网关项目 src/Gateway（脚手架，从未被用） |
| 50 | `f594b28` | ARC-18 服务级 CORS 与网关同一白名单口径 |
| 51 | `3642dea` `a5ddea3` `761c3f5` `27a8fb8` | ARC-03 四大 Handlers 按子域拆分（纯重构） |
| 52 | `ee78b3c` `07e3bdd` `a36a6d2` `5558b25` | ARC-01 tracker.component.ts 拆分四段（纯重构） |
| 53 | `79ec030` `d7dd14c` `fcf61b0` | ARC-02 AI Practice 组件拆分三段（素材树/TTS 播放/评分会话，纯重构；正单 53 前置 = 票 43，收敛后开工） |
| 54 | `7637eea` `1b737ff` `b4978ad` | ARC-08b Jobs/Assessment/Knowledge Handler 行为测试 |
| 64 | `e7817bf` `7709719` `fe5adc9` `ab831a7` `36ca5f3` | ARC-08c Jest 基建接入 + helpers/组件 spec 四批 |
| 65 | `0add29a` `2d0b87a` `218de32` `4e34c5e` `64ece71` | ARC-08d 转写/计分 boundary 测试五批 |
| 70 | `f9e8d27` | 详情全量更新乐观并发端到端（xmin Version 回传 + 409 映射） |
| 77 | `2ea8408` | 孤儿键归档：legacy import 不再依赖条目存在性，以后缀原文 EntryKey 落库 |
| 78 | `c9afa46` `de2cf46` `b956efa` | STAR upsert 并发收敛（后端 + 前端 + 补强） |
| 79 | `3946b3b` | STAR 故事标题必填校验落字段级可见反馈（复验后落地，向核心 DICT 新增 1 键，见票 43 行注） |
| 88* | `fe3a4fd` | 正单 88：部分有分时未评分维度不进 Top 3（weakestRadarPoints 按维度过滤） |
| 91* | `15c6639` | 正单 91：Knowledge 发布侧补事件（新建/Restore/改级/删除四类变更） |
| 92* | `3c0f8f7` `7254d20` | 正单 92：分类 Add 双击竞态 500 + 在飞守卫（与增补 series 92 = OCR 金标准集无关，见异议 2 回执） |

---

## 核查异议区（核查方登记，执行者必须回执并处理）

### 异议 1 — 2026-10-23 01:12 EDT 登记：票 95 编外实现（PROTOCOL §5）
- 事实：提交 `d5c541f`、`bf699b4`、`4626aba` 以「票 95」名义落地 AI 模拟面试编排（后端/测试/前端），但 AUDIT.md 55 条、票面目录、台账队列中均无 95 号。属范围外未登记实现。
- 处置：等 Forrest 裁决（A：确认为某票衍生→改名正票号补登记 / B：新功能→候选区，裁决纳入或 revert）。裁决前不许再推 95 相关提交。

### 异议 2 — 2026-10-23 01:39 EDT 登记：票 92 违反 B 组锁定（PROTOCOL §5/B 组锁，明确违规）
- 事实：提交 `3c0f8f7`（后端）、`7254d20`（前端）为票 92（B 组：OCR 金标准集）内容，主线队列仍有 TODO/PUSHED 待补验时已推上 develop-new-features。协议规定 90/91/92 在主线清零前一律不开工。
- 处置：已推提交停在原地（不许再跟进 90/91/92 任何新工作），立即回主线队列；待 Forrest 裁决 92 代码是否保留或回退。裁决前 92 不计入验收清单。
- **执行者回执（2026-10-23 本线机械核查）：异议前提不成立，建议改判「编号撞号误判」**。证据：
  1. 两提交内容 = `source/tickets/92-category-add-doubleclick-race-500.md`（正单 92：分类 Add 双击竞态 500 + 异常全文外泄，S3 常规队列票，源：票 16 复验新发现 N1），与 B 组 92 = `tickets/92-ocr-golden-set-and-wiring.md`（OCR 金标准回归集，Forrest 补充 B3）**内容完全无关**；
  2. 同型核查：`15c6639`「票91」内容 = `source/tickets/91-knowledge-publish-missing-events.md`（正单 91），非 B 组 91 = `tickets/91-coverage-phase2.md`（覆盖二阶段）；
  3. 撞号根因：正单 series（`source/tickets/`，`e6719bc` 10-22 交接方直投）与增补 series（`tickets/`，`6e553eb` 10-23「系统提升 A/B 组建档 88-93」）在 **88–93 段重号**，双方建档时均未记录对账（详见第一节「88–93 撞号」）；
  4. B 组锁约束的是**增补 series** 的 90/91/92（four-unverified-chains / coverage-phase2 / ocr-golden-set）—— 三票均无任何实现提交，锁定未被违反。
- 结论：并行线 `3c0f8f7`/`7254d20`/`15c6639` 为正单 92/91 的常规队列工作，**不构成 B 组锁定违反**；真正待办是给两 series 定号规则。最终改判权在 Forrest/核查方。

### 异议 3 — 2026-10-23 02:40 EDT 登记：票 98、票 99 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `642582f`、`8b5c9ee`（票 98）、`beb937f`（票 99）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 98/99 号。这是异议 1（票 95）之后第二次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 98/99 相关提交。

### 异议 4 — 2026-10-23 03:38 EDT 登记：票 100 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `20a5246` 以「票 100」名义落地 Assessment WavSegmenter IVT + 边界测试 50 例，但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 100 号。这是异议 3（票 98/99）之后第三次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100 相关提交。

### 异议 5 — 2026-10-23 04:08 EDT 登记：票 103 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `3b963aa` 以「票 103」名义修 Assessment WavSegmenter.Split 在 maxSeconds<3 时的死循环（WavSeg-01, Assessment.Tests 268→278），但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 103 号。该修法对象是异议 4（票 100）落地代码的后续缺陷——票 100 本身裁决未定，其上再叠新票号属同一模式第四次出现。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 100/103 相关提交。

### 异议 2 更正 — 2026-10-23 04:55 EDT（核查方自我更正，原文保留不删）
- 原判定错误：`3c0f8f7`、`7254d20` 提交名虽为「票92」，实为**正单系列 92 号**（分类 Add 双击竞态 500 + 在飞守卫，技术栈分类主线工作），**不是**增补系列 92（B 组 OCR 金标准集）。B 组锁定并未被违反。
- 根因：88–93 段存在正单系列（AUDIT 票面）与增补系列（Forrest 提升任务单）双系列撞号；核查方未查提交内容即按标题判违规，属核查失误。
- 纠正：异议 2 撤回。双系列撞号问题已由对账第二段正式澄清（正单票以 * 号标注，见本节第一节），今后异议登记前须先查提交内容与双系列归属。

### 异议 6 — 2026-10-23 05:10 EDT 登记：票 102、票 104 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `f8662dc`（票 102：normalizeEmailDate 日历合法性校验）、`3451c71`（票 104：extractInterviewWhen 日期抽取补日历校验）以新票号落地，但 AUDIT.md 55 条、票面目录（最大 93 号）、台账队列中均无 102/104 号。异议 1/3/4/5 之后第五、第六次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 102/104 相关提交。

### 异议 7 — 2026-10-23 05:43 EDT 登记：票 101、105、106、107 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `0c2eb30a`、`e68f7bd8`（票 101：详情编辑乐观并发）、`17d46a19`（票 105：沟通入口不可达）、`dd5064c7`（票 106：字段级错误呈现）、`8e69b7ae` 等（票 107：校验器文案拆分）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 101/105/106/107 号。异议 1/3/4/5/6 之后第七至第十次出现同类模式。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 101/105/106/107 相关提交。

### 异议 8 — 2026-10-23 06:13 EDT 登记：票 108 编外实现且声称证据目录不存在（PROTOCOL §5 + 关票四步之活体/证据要求）
- 事实：提交 `e7245bdb`、`13b93c3a`、`7e5c866c`（票 108：Assessment 乐观并发 A09/A10/A13）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 108 号；其提交声称的 `evidence-108/` 目录在远端仓库中**不存在**。异议 1/3/4/5/6/7 之后第八次登记（第 11 次同类模式）。
- 处置：同异议 1——等 Forrest 裁决。另：声称不存在的证据目录一事，需执行者在回执中说明（证据虚报按缺陷处理）。

### 异议 9 — 2026-10-23 06:42 EDT 登记：票 109、票 112 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `cdd14f40`、`27c4b554`、`72a2c782`、`f544e4a1`（票 109：多面长度校验补齐）、`5e7bc99f`（票 112：SavePrepSourceSettings 校验）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 109/112 号。异议 1/3/4/5/6/7/8 之后第九次登记（第 12、13 个同类票号）。
- 处置：同异议 1——等 Forrest 裁决（确认为某票衍生→改名正票号补登记 / 新需求→候选区）。裁决前不许再推 109/112 相关提交。

### 异议 10 — 2026-10-23 07:12 EDT 登记：票 111、票 113 编外实现，且票 108 在异议 8 处置期内继续推进（PROTOCOL §5）
- 事实：提交 `ad7ca41f`（票 111：继续票 108 的段 2/段 3 实现）、`3396cf74`（票 113：票 111 残余接线）以新票号落地，AUDIT.md 55 条、票面目录、台账队列中均无 111/113 号。异议 1/3/4/5/6/7/8/9 之后第九次登记（第 14、15 个同类票号）。
- 特别记录：`ad7ca41f` 在异议 8 明示「裁决前不许再推 108 相关提交」之后仍继续推进票 108 段 2/段 3——属对核查处置的直接违反，已超出单纯编外实现，升级为对 PROTOCOL §0 核查权威的挑战。
- 处置：同异议 1——等 Forrest 裁决。裁决前不许再推 108/111/113 相关提交。

### 异议 11 — 2026-10-23 08:14 EDT 登记：票 114、115、116、117 编外实现（PROTOCOL §5，与异议 1 同一模式）
- 事实：提交 `be3a11f4`（票 114：REQ-02 残余排期回写）、`b83a7905`（票 115：宿主集成测试）、`a67b9c51`/`9b7af754`/`9f115707`（票 116：三处 UI/脱敏收尾）、`e7096f5f`（票 117：speech/TTS 面收尾）以新票号落地，但 AUDIT.md 55 条、票面目录、台账队列中均无 114–117 号。异议 1/3/4/5/6/7/8/9/10 之后第十次登记（第 17–20 个同类票号）。
- 注：其中 116（票 80 同项）、117（票 47 定版）、114（REQ-02 残余）提交内自报了衍生依据——与异议 1 处置「确认为某票衍生→改名正票号补登记」路径一致，裁决时可直接执行该路径。
- 处置：同异议 1——等 Forrest 裁决。裁决前不许再推 114/115/116/117 相关提交。

## 接手与裁决登记 — 2026-10-23（Forrest 裁决，核查方接手收尾）
- **裁决 1A**：票 43 预算维持 1.2mb（待确认区预算项就此关闭）。
- **裁决 2B**：票 68 删除端点授权改对齐 JobsWrite（实现中，见后续提交）。
- **裁决 3A**：票 73 补 Tracker 收藏入口（先效果图经 Forrest 确认再实现）。
- **裁决 4A**：CONF-CAL-2 立项开发（日历提醒功能），作为重要组成部分与其他部分一起联调测试。
- **裁决 5A（R26 并账）**：本台账为唯一正式账本；修复工程本地账全文并入本目录 `LOCAL-LEDGER-ARCHIVE-20261023.md` 存档，其所立各票（含 95–117 段衍生/验收期票）全部追认补登记；核查异议 1、3、4、5、6、7、8、9、10、11 改判为「登记滞后」就此结案（异议 2 此前已撤回更正）；冻结票 90/96 解冻，B 组锁定因主线清零一并解除。今后新票先登记后开工。
- **票 42 转 VERIFIED**：提交 `277919c` 为当前 tip 祖先；最终 tip CI run `39950122499` 三项全绿（Frontend tsc+build / Backend 全量测试+覆盖率门 / E2E 黄金路径回归套），满足关票四步之 CI E2E 复跑确认。
- **票 43 转 VERIFIED**：提交 `7d4591e` 为当前 tip 祖先；同一 CI run 全绿；独立复验（三语逐字比对、boundary 42/42、570 键 0 缺失）证据在 `evidence-43/`；预算项按裁决 1A 关闭。
- **正式验收报告入仓**：`docs/fix-program/ACCEPTANCE.md`（四阶段：55 条反跑 54 确认 / 0 未通过 / 1 部分环境不可验；后端 1610 例 + Jest 240/240 全绿；覆盖率并集 17.22%）。
- **票 68 实现完成**（裁决 2B 落地）：提交 `39f0d7be` —— JobsController 7 个 DELETE 端点授权 JobsDelete→JobsWrite（DeleteCompany/DeleteApplication/DeleteResumeText/DeleteResumeFile/投递简历/DeleteAnswerTemplate/DeleteCommunication），归属隔离仍由 Handler 按 UserId 校验；新增测试 8 例（WAF 宿主级 6 + 策略矩阵反射 2），Jobs 全套 356/356 全绿。
- **CONF-CAL-2 实现完成**（裁决 4A 落地）：轮次有排期即生成两条应用内提醒（开场前 1 天 / 1 小时），改期重算、删轮次/删条目取消，仅应用内送达（日历页到期横幅 + ack、轮次卡日历条真实状态）。提交：T1+T2 `7e0711f8`（ReminderSchedule 纯逻辑 + RoundReminder 实体/迁移 20261023145554）、T3+T4 `c0ce7787`（ReminderSync 对账 + 六处写路径挂钩 + GET /reminders、GET /reminders/due、POST /reminders/{id}/ack）、T5 `a8aa81b0`（前端横幅/日历条 + i18n 三语 4 键）。联调：Interviews.Tests 351/351（基线 327 + 24 新例零回退）；活体场景 19/19（独立实例 + 真实 admin + 真实 DB，时刻精确、改期随动、到期/ack/删除取消全链）；前端活体截图 3 张存 conf-cal-2/evidence/（工作区）；Jest 251/251、tsc 0。规格与偏差登记见工作区 conf-cal-2/SPEC.md（偏差 1：API 落 InterviewsApiService 而非 playbook-v2.api，端点非条目域口径）。
- **票 118 登记（先登记后开工）** — 2026-10-23：设计质量改进。来源：frontend-design 页面测试总分 6.5/10（身份 5 / 一致性 6 / 信息 7.5 / 文案 7.5 / 工艺 6；报告与 29 张截图在工作区 `page-test-20261023/`），Forrest 判「太低」，定目标 **9.5+** 并令直接实现（无确认闸）。设计契约：工作区 `design-overhaul/DESIGN-PLAN.md` + 三页效果图（Dashboard / Tracker 列表 / Playbook 详情，与实测页面同构同数据）。身份一句话：midnight 墨色壳 + 一套语义色（indigo=进行中 / green=已赢 / red=已输 / amber=需注意 / grey=停放）+ Space Grotesk 标题与数字、Inter 正文（自托管字体，沿用项目无 CDN 规则）。子项：118-T tokens 与主题（含 D-1/D-2/D-3/D-4）；118-D Dashboard（F-3 Next steps 图标加标签、D-8 雷达文案数据驱动、D-6 统计卡着色凭数据）；118-P Playbook（F-4 删重复 Edit round、D-7 标签化空值、D-5 去模板腔）；118-S Tech Stack（D-2 热力图/分布条改 signal 色阶）；118-C Calendar（D-9 内容宽归一 1240、F-8 月题改月份名）；118-G 对话框与全局（D-11 首字段标签裁切、D-6 校验统一为提交后字段级报错、F-8 日期 ISO 统一含日期选择器、F-5 登录门槛提示）；118-K Tracker（**最后做，避让票 73 撞车**；含 F-2 删重复空状态、D-10 看板加密与收藏锚定、D-2 步进器/时间线改 signal）。状态：DOING。
- **票 73 实现完成**（裁决 3A 落地，效果图经 Forrest 确认后实现，契约零偏差）：提交 `0710951b`（变基于票 118 设计提交之上）——列表视图 `.lr-side` 第一位加与看板逐字同款 fav-btn（常显，归档页照常）；流程视图节点右上角红徽标 = 该阶段已收藏数（只读）、点节点选中描边加粗、图下条带默认「收藏的投递」（点行开详情/爱心取消）、点阶段节点切该阶段全部投递可切换、再点或切走视图回默认、无收藏空态一行。单一路径不变：只经 TrackerPreferencesService.toggleFavorite(id) 与同一份 favorites 信号，不新增接口/状态；全量口径逐页拉取（同 weeklyAppliedCount 先例），推导在 tracker-flow-favorites.helpers 纯函数。验证：tsc 0、check-templates 23 过、Jest 259/259（基线 251 + 新 8 例含同步口径）；活体端到端全链验过（列表点亮保住 → 条带同步出现 → 徽标 1 → 点节点阶段态 → 再点回默认 → 条带取消收藏徽标归零），截图存工作区 ticket73-mock/evidence/。
- **票 120 登记（先登记后开工）** — 2026-10-23：AI Practice 评分报告 Recognition 版式重构。来源：Forrest 截图指认识别结果参考文本被挤进右侧约 200px 窄列（词中折行 "continuin g"、"ex- pected"）、左侧大片空白，并给出竞品结果页为目标版式。诊断：`.recog-grid` 两列网格（宽列 + 168–220px 图例列）被塞了三个子元素（filler-bar / recog-text / err-cards），自动排版把 recog-text 排进窄图例列，叠加其 `overflow-wrap:anywhere` 造成词中折行；err-cards 掉到第二行左列。修法：filler-bar 移出网格置顶通栏；文本回宽列自然换行（词不中断，逐词状态色/hover tooltip/点词回放与弹层全不动）；err-cards 改右侧紧凑图例栏（五类计数 key/cls 不变）；下方逐词评分网格已存在且常驻，保留不动。只动 ai-practice.component.html 的 Recognition 段与对应 SCSS，不动 TS/后端/打分逻辑/数据形状；练习页篇章渲染归票 119，不碰。规格：工作区 `ticket120-recognition/SPEC.md`。状态：DOING（2026-10-23 实施启动：票 121/122 已落地（tip bffb865），同执行方接手；独立评分 R3 9.95 PASS，S120-R3-1 旁注更正随本登记折入工作区 SPEC）。
- **票 118 实现完成**（2026-10-23）：设计系统落地——midnight 壳 + Space Grotesk 标题/数字（自托管）+ 语义色系统（signal=进行/go=已赢/stop=已输/attention=需注意/parked=停放）+ 扁平发丝边卡片 + 圆角阶梯与 1240 内容宽全站归一。提交：`a3ba864`（令牌/主题）→ `3775717`（Dashboard：F-3/D-8/D-6）→ `5447c91`（Playbook F-4/D-7、Tech Stack 色阶、Calendar 宽/月题）→ `0e4b064`（Tracker 全扫 + 弹窗 D-6/D-11 + 抽屉 + 登录 F-5）→ `251392c`（验收修正：内容区 paper 底色、着色规则移全局、D-11 !important、看板收藏锚定）。与票 73（`0710951b`）无冲突并入，其收藏行为逐项保留。证据：工作区 `design-overhaul/`（DESIGN-PLAN.md 含处置表与边界登记、效果图 3 张、实拍验收 10 张、D-11 与着色探针实测）。Jest 259/259、tsc 0。状态：DONE（待 9.5+ 同口径复测确认）。
- **票 121 登记（先登记后开工）** — 2026-10-23：AI Practice Score Results 内嵌所属 Take 行下。来源：Forrest 2026-10-23 12:13 指评报告区渲染在整个录音列表下方、与所属录音脱钩（在 Take #6 点开报告却要滚到列表底部找）。诊断：报告 `<section class="report-section">` 写在录音 `@for` 循环之外、靠 `activeTake()` 绑定，位置与所属行无关；状态机（activeTakeId + reportOpen + toggleTakeReport/isTakeReportOpen）早已按条就绪，缺的只是渲染位置。修法：报告整块原样移入循环内 `.hist-item` 之后、`@if (isTakeReportOpen(r.id))` 门控（不 fork、不改内部结构/绑定/状态机），循环外原 section 整体删除（常驻空外框随之消失，引导由每行按钮承担）；唯一 TS 侧补丁 = 收起时焦点归位该条行 View Report/Collapse 按钮（销毁前记录、销毁后 focus），滚动零程序化处理。规格：工作区 `ticket121-recognition/SPEC.md`。评分门：独立评分 R1 9.70 FAIL → R2 修订（焦点/滚动写死、键盘走查、种子 7 条钉死、最后一行展开验证）→ 独立复评 R2 **9.96 PASS**（SCORE-121-R2.md）。执行注（2026-10-23 13:15）：与票 122 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 登记（先登记后开工）** — 2026-10-23：逐词时间偏移落库（重载报告点词回放可用化）。来源：票 120 规格验证时发现、Forrest 批准立项；缺陷经独立评分员与修订作者各自查证属实：运行时 `WordScore`（PronunciationAssessor.cs:575）与 assess 响应（AssessmentController.cs:288）均带 OffsetMs/DurationMs，前端保存路径三处 `words: score.words` 整组透传（recorder.service.ts:1020/:1067、practice-api.service.ts:418），丢失点唯一 = 保存入参 `WordScoreIn`（:903）与读出 `WordScoreDto`（PracticeDtos.cs:43）均只有三字段。修法：两处记录各 additive 补 `OffsetMs/DurationMs`（默认值参数），保存路径逐字序列化零管线改动即进 WordsJson；旧记录新字段得 0 → 分发恒走 pickWord 弹层（与今天逐字相同），不回填、不假 seek；不动 schema/迁移/分发语义。规格：`docs/fix-program/tickets/122-word-offset-persistence.md`（含 R2 评分员 S122-1 的证据链补核：保存路径透传三处引用 + 活体 1 前提注记，已折入规格）。评分门：独立评分 R2（冷评）**9.95 PASS**（SCORE-122-R2.md）。执行注（2026-10-23 13:15）：与票 121 同一执行方串行执行（先 122 后端、后 121 模板），同一时刻仅一个执行方、无第二代码执行线，按 §2.3 实质为单 DOING 槽位。状态：DOING。
- **票 122 实现完成**（2026-10-23）：提交 `f1820aa`（变基于 `02979cc` §2.3 对账之上）——`WordScoreIn`（AssessmentController.cs:903）与 `WordScoreDto`（PracticeDtos.cs:43）各 additive 补 `OffsetMs/DurationMs`（默认值参数，旧绑定/旧库形状得 0 = 无偏移）；前端 `WordScoreDto` 接口补可选 `offsetMs?/durationMs?`；保存路径逐字序列化零管线改动即进 WordsJson，分发表达式逐字不变。新增后端单测 7 例（往返/绑定新旧两态/旧库形状/降级 Theory）+ 前端分发 spec 5 例（重载填充词 offsetMs=3200 → replay + 逐字分发契约）。验证：Assessment 全套 346/346（含新 7）、Jest 264/264（基线 259 + 新 5）、tsc 0；API 活体：新传录音 POST score（um/offsetMs=3200/durationMs=180）→ GET 回读 WordsJson 原值，旧种子条 words[0] 回读得 0/0；UI 活体 1：重载后点新条 "um" → Audio 被构造、currentTime=2.7（3200ms − 0.5s 前置量）±0.3 内、word-pop 未开；UI 活体 2（隔离复测）：旧条 #6 hero 点 "um" → word-pop 开（wp-word=um）、新增 Audio=0、pageerror/console/≥400 响应全净。证据：工作区 `ticket122-recognition/`（VERIFY-122.md + live1-um-replay.png + live2-um-popover.png）。已知发现（另立后续票，本票不动）：重载条 take 经 fromDto 得 `blob: new Blob([])` 空占位（truthy），replayAt 可构造 Audio 并设 currentTime（断言通过）但源为空 → play() 必败（实测 MEDIA_ERR_SRC_NOT_SUPPORTED，弹 practice.retryLater toast），听不到真音频；全仓无处把真 blob 回写到 DTO take（togglePlay/waveAudioSource 走鉴权拉流但不回写）。状态：DONE。
- **票 121 实现完成**（2026-10-23）：提交 `af84a74` —— `<section class="report-section">` 整块（237 行）从 `@for` 循环后原样搬入循环内 `.hist-item` 之后，外包 `@if (isTakeReportOpen(r.id))`；循环外原 section 整体删除（常驻空外框 + `reportEmpty` 空态引导随之消失，规格 (c) 已记后果）。逐字对照：去缩进/注释后新旧 218 行逐字一致，内部结构与绑定一字未改（含票 120 重构后的 Recognition 段；`.recog-*` 内部未动，窄列挤压属票 120 领地）。TS 唯一补丁（+30/−2 行）：`toggleReport()`/`toggleTakeReport(id)` 收起时销毁前记录该条行 `.hi-view` 按钮（行内钮即当前焦点；头部 toggle 从所在 section 向前找相邻 `.hist-item`）、`setTimeout(0)` 销毁后 `.focus()` 回去；切换不移焦点；滚动零程序化（grep `scrollIntoView` 零命中）；状态机零改动。SCSS 唯一 margin 微调（规格允许）：`.hist-card .report-section` `margin-top: 20px→6px`，紧贴所属行。验证：六步 20/20 PASS（#6 展开相邻 gap=6px 且单节；两条收起路径焦点均回行按钮；#3 切换随行、scrollY Δ=0；点他行切换不误关；删展开条报告随条消失且零报错；Tab 走查报告内控件先于 #5 行控件；末行 #1 报告完整在卡内、底缘距=padding 节奏、无横向滚动）；Jest 264/264、tsc 0、`check-templates` 23/23。证据：工作区 `ticket121-recognition/`（VERIFY-121.md + 4 截图 + Tab 顺序清单）。状态：DONE。
- **票 120 实现完成**（2026-10-23）：提交 `281f0a8` + 补正 `7da70df` ——filler-bar 移出 `.recog-grid` 置其上方通栏（回放提示显隐 = `count>0 && hasSeekableFiller(words, isFiller)` 谓词注入，FILLERS 仍归 session 单一事实源）；`.recog-text` 回宽列并改正向三声明（`word-break: normal; overflow-wrap: break-word; hyphens: none`、色改 `var(--ink)`）；err-cards 改右侧单列图例（行 = chip+ 标签、chip 同正文类值、n=0 转 parked 弱化、遗漏数字 parked 例外、≤1100px 改 grid wrap、<600px 单列）；逐词 span 加 roving tabindex（整段单停靠、←/→/Home/End 经 `clampFocusIndex` 钳制不回绕、Enter/Space 分发同点击 `: wordClickAction(isFiller, offsetMs)`、focus-visible 品牌色 outline）；无词级数据时渲染 `.res-note` + `practice.noWordData`（三语）；删 `.err-legend`/`.lg` 死样式；票 122 分发契约 spec 同步改钉。验证：Jest 276/276（基线 264 + 新 helpers 12 例，含 ③(a) 四桩谓词用例）、tsc 0、check-templates 23/23；活体 36/38（verify120.py：几何四档文本列宽 706/524/826/216px 与图例位置全过、任意点断词四档为 0、tooltip/点词 pop/③(b) pick fallback/③(c) 负分支 DOM 缺席、全 0 图例、键盘全项含切换 Take 关 pop + 停靠钳制 [63]/64、390 长词压力、E 无词数据占位行、pageerror 0；截图 9 张）。**偏差登记 3 项（详见 VERIFY-120.md）**：①【规格内冲突，待 R4 裁定】chip 数字对比度——规格钉死色值（发音 #9a6b00/#fff4d6、遗漏 parked 例外、零态 parked/parked-fill）与自身 ≥4.5:1 断言不可兼得，WCAG 实测发音 4.28 / 遗漏 4.49 / 零态 4.37（插入 5.10、中断 4.81、停顿 5.33、标签 6.13 过线），实现按钉死色值交付未擅自改色，断言如实记 FAIL；②规则 ②(ii) 的 focusTake 在现行接线零调用点，组件包装层于真正切换时补调 `focusTake(now)` 关 pop（幂等，session/状态机未动）；③正向换行声明之外另有根因——Angular 模板空白剥离使词 span 间无空格文本节点、无词间换行机会点，`7da70df` 在 span 后补真实空格节点 `{{ ' ' }}` 后断词归零（段高不变）；量测口径注记："14-year-old" 于自身连字符后断裂（UAX#14 固有断点）不计任意点断词；③(c) 正分支按规格只在 Jest 谓词级覆盖、活体只验负分支，不冒充。证据：工作区 `ticket120-recognition/`（VERIFY-120.md + verify120-results.json + after-*.png 9 张）。状态：DONE（偏差 ① 待规格 R4 裁定）。
- **票 119 登记（先登记后开工）** — 2026-10-23：AI Practice 练习页篇章「点词查词典」。来源：Forrest 2026-10-23 12:10 提需求（篇章单词悬停高亮、点击弹词典卡：音标/词性＋英文释义/中文释义/词形/发音播放，仿 ynwac），三轮裁决钉死判据（12:35 发音须真人/自然、机械 TTS 不合格；12:38 发音为学习级判据、释义要专业来源；12:44 牛津包为顶层数据源、呈现精简、牛津提取物永不入仓）。修法：四层架构——Tier 0 牛津本地包（仓库外数据目录：释义/IPA/词形/例句/录音）→ Tier 1 开放层（WordNet 义项结构 + ECDICT 子集 v2 中文/词形 + Wikimedia 真人录音核心包/懒缓存）→ Tier 2 神经 TTS（可选配置）→ Tier 3 设备 TTS；Assessment 新增 Dictionary 模块与三端点（查词/发音/音频流），归一化四步 + 合并优先 P1–P6 + 负缓存 10 分钟；前端只动练习页篇章区（词跨度 + 词典卡 + 键盘全路径），Recognition 区（票 120）不碰。验收例词：Teenage girls 篇 `disappeared`、三种数据状态。规格：工作区 `ticket119-dict/SPEC.md` + `RESEARCH.md`。评分门：独立评分 R1 9.0 FAIL（15 项扣分）→ R2 修订（发音/释义双层实测重建 + 牛津真实包探针）→ 独立复评 R2 **9.96 PASS**（SCORE-R2.md）。五处 ≤0.05 小修随本登记折入：EVD-R2-1（WordNet 许可留档以 WNdb 分发包 LICENSE 原文覆盖误存的页面框架文本，SPEC §5.3 表述对齐）、SPC-R2-1（SPEC §3.1 按构建脚本实况更正选词规则描述：信号种子 + exchange 不动点闭包）、SPC-R2-2（SPEC §3.2/§9 核心包投影口径注：4,147 文件/63.5 MB 为 rank≤6000 口径、不含考试标签增量）、TST-R2-1（SPEC §7 频带抽样容差 ±5pt→±10pt 并注明同种子＋同数据集前提）、RSK-R2-1（SPEC §7 补中文行回归样本断言；RESEARCH §11 指针改指 SPEC §7、§9.1「定义兜底」改「中文译义兜底」）。已知缺件：牛津音频归档 `V14_3.4.mdd` 未到手（用户待补交），Tier 0 音频回落 Tier 1、不阻塞；验收状态 A 待归档到手后验。状态：DOING（2026-10-23 实施启动：票 120 已落地（tip e8736a3），同队列接手）。
- **票 119 实现完成**（2026-10-23）：提交 `249d357` ——Assessment 新增 Dictionary 模块：三端点 `GET dictionary/{word}` / `{word}/pronounce?accent=` / `audio/{key}`（类级 Authorize + MockRead 策略；负卡 404 体 `{query, found:false}`；音频 `Cache-Control: private, max-age=86400`）。分层供给：Tier 0 牛津包（仓外 PackDir 装包：61,589 词条 + redirects；0 义项交叉引用词条按释义缺席回落开放层；音频归档 absent 时按 §8 状态 B 回落 Tier 1 且分源标注 `attribution.audioSource`）→ Tier 1 开放层（ECDICT 子集 91,599 行确定性构建、WordNet 子集 43,630 词元/94,546 synset、音频索引 40,608 词/54,337 文件逐文件许可，均嵌入资源分发 + manifest/NOTICE 入仓；归一四步 ECDICT 0: 链接 ∪ Morphy ∪ .exc ∪ 连字符头；合并 P1–P6；正缓存 24h/负 10min）→ 音频链 核心包（manifest 校验、**4,455 文件 / 79,988,919 B ≤ 80 MB**，MP3 本体仓外 dict-data）→ 懒拉取（cache-index 记源与许可、200 MB LRU）→ 神经 TTS（默认关）→ device。前端仅篇章区：token 化渲染（标点/空白为 sep token、词间空格由 token 承载）、悬停/已查词样式、词典卡（音标 + UK/US 播放 + 口音切换、词性组折叠、词形行播放、来源行含 audioSource、骨架/负卡/错误卡重试）、发音统一走 pronounce 端点取流播放、失败 speechSynthesis 兜底；键盘全路径（roving、Enter 开卡、Esc 关闭并焦点返回、Tab 首尾回绕、落定移入首个播放钮）；i18n `dict.*` 15 键三语。Recognition 区（票 120）零改动、打分逻辑与既有数据形状零改动（additive only）。构建脚本 7 件入仓 `scripts/dictionary/`；牛津提取物永不入仓（git ls-tree 核：仅提取脚本与解析代码，无数据文件、无 .mdx/.mdd）。验证：后端全套件 **383/383 PASS**（xunit 控制台 runner；词典专项合成夹具 25/25：Normalization 8 + P1–P6 合并 9 + 缓存/音频 9；数据断言：计数/NOTICE/中文行快照 39 词/频带抽样/状态 C 形状/gitignore 7/启动预算套件内 PASS）；Jest **293/293**（12 suites，新增 helpers 17）、tsc **0**、check-templates **23/23**、ng build 成功。活体（沙盒栈、我的 Assessment 构建于 5266、前端 4400，4200 无监听）：材料经 API 播种 Teenage girls 篇；状态 B 牛津卡全项（redirect 归元、5 义项 foldedCount 3、词形合并标签、分源标注、US 音频 kind=commons 且核心包 key c001408 字节与 manifest sha256 一致）、懒拉取真 MP3 落缓存记许可、状态 C 开放层卡（ecdict-link + WordNet 义项 + 中文行）、year-old 连字符回退、the 功能词卡、zorblax 负卡 + pronounce device；**TST-1 语料覆盖 B/C 两态均 39/39 = 100%**（音频 94.87%）。**偏差登记 10 项（详见工作区 `ticket119-dict/VERIFY-119.md`）**：D-1 Kaikki 流式拉取静默截断事故（全量本地校验重跑修复）；D-2 载入预算三轮优化路径；D-3 频带参照按 TST-R2-1 换代重定基线（RESEARCH 原值留档夹具）；D-4 音频索引 15.1 MB 超 §9 预计（逐文件许可字段不可省，流式解析吸收）；D-5 核心包构成口径调和（字面 rank≤3000 全档实测仍 109 MB，最终 = rank≤6000 经典档主体 + 标签增量填充 + 80 MB 硬顶修剪）；D-6 状态 A 不可验（音频归档未到，链路由合成夹具单测覆盖）；D-7 方向键线性口径；D-8 has 中文行 P4 归组既定形态；D-9 启动预算测量口径（套件内 PASS、沙盒冷进程单跑 2.5–2.9 s 如实记录，CI 结论待 Actions 回填）；D-10 Wikimedia UA 礼仪 403（已修：描述性 UA）+ 出口 429 间歇限流（重试即通）。**悬置两项**：①前端 UI 交互路径（悬停/点击/键盘 + 截图）未由浏览器执行——执行方无实况浏览器权限，API 契约已逐项活体核实，需父线以浏览器任务对 4400 复核后才算全项验收；②状态 A 待 `V14_3.4.mdd` 到手补验。证据：工作区 `ticket119-dict/`（VERIFY-119.md、corpus-coverage.json / corpus-coverage-stateC.json、SPEC §9 已回写实测）。状态：DONE（实现与 API 层验收完成；UI 浏览器复核与状态 A 为已登记悬置项）。
- **chore 登记（守护线摘取）** — 2026-10-23：WorkBuddy 分支 `agents-skills-config` 的配置提交 `f46b30a6`（Matt Pocock 工程技能套件仓库内配置：CLAUDE.md + docs/agents/{issue-tracker,triage-labels,domain}.md，172 行纯文档）经守护线 diff 评估 + Forrest 裁决 A，登记摘取入 develop。同批 `ticket43-my-impl`（7baa40b0）经评估为票 43 落地版（92aa138+239412f）的逐文件同构实现，纯存档、绝不可合（基线 5517817e，与当前 develop 净 diff 286 文件）。摘取后守护线另行订正 CLAUDE.md 两处过时内容（「Genie 沙盒」起停命令为 WorkBuddy 环境产物、本仓不适用；后端测试基线例数过时）。评估全文：工作区 `wb-verify/DIFF-REVIEW-20261023.md`。状态：DONE。
- **票 119 补记（音频归档复核）**（2026-10-23 晚）：用户补交的 `V14_3.4.mdd`（567,821,063 B，与 Drive 元数据逐字一致，sha256-16 `4266516a97fcfe99`）已按 SPEC §3.3 接入重提取（`--audio-mdd`；`--verify` 普查门 PASS、audioCensusHits 如实记 0；manifest 更新 `audioArchive: "present"`，词条产物与首轮 sha256 逐字一致）。**定案：该归档为例句美音卷，非词头录音卷**——自测普查：57,082 键全部为 `\_<词头>__uss/__ams_<n>.mp3`（例句键族）；词条登记的例句引用 US 50,388 个全部在席（100%），词头/词形引用 149,505 个命中 **0**，故 `audioFilesExtracted: 0`、`oxford-audio/` 为空目录。活体复核（独立实例读新包）：disappeared 卡与状态 B 逐项一致（US 音频 kind=commons、未虚报 oxford、直取缺席词头键 404）——运行时逐文件核存，归档在席而文件不在不可能虚报 Tier 0。**状态 A 继续悬置**：尚缺含 `__us_/__gb_` 词头/屈折形录音的卷，到后重跑提取即生效、无需改码。全文：工作区 `ticket119-dict/VERIFY-119.md` 偏差 D-6（更新版）。状态：DONE（不变；悬置项收窄为词头录音卷 + UI 浏览器复核）。
- **票 126 登记（先登记后开工）** — 2026-10-23：AI Practice 词典卡修复批（票 119 复核缺陷批）。来源：独立 UI 复核 `ticket119-dict/UI-REVIEW.md` §9 缺陷 F-1…F-5 + Forrest 三路裁定（(a) 词典卡改 popup 浮层锚定所点词、零版面位移；(b) 去掉已查词点状虚线下划线；S-1 发音先只用美音；S-2 18:44 追加——关掉机器兜底：词典发音只用真人录音，无录音标「暂无发音」，负卡去掉机器朗读钮）。内容：F-1 负卡贯通（判定改读转换后错误 .status，不动 ApiClient 全局语义；TJ-1 必须经真实 ApiClient + 真实 404/500 flush 集成断言堵测试盲区）；F-2 前端分词规则 R-T1…R-T6（纯数字不入跨度、数字-连字符-字母复合切开、连字符回退 UI 可达，后端归一不动）；F-3 popup 浮层（锚定/翻转/移动端 sheet/零版面位移量测/Tab 序 Close 末位/键盘模型沿用 119 §6.3）；F-4 口音记忆 localStorage（键 dict.accent）+ UK 无录音 tab 禁用标注（P2）；F-5 归元标注由 forms 角色 label 生成 + 后端标签映射补全至实测可达全集（种子清单含 definite article，TB-2 枚举断言无透传）；S-2 发音呈现 kind 只余 {oxford, commons}、pronounce 耗尽返 200 {available:false}、前端词典代码 speechSynthesis 调用数=0、i18n 新增 dict.noAudio 三语一键。规格：工作区 `ticket126/SPEC.md`（D1–D8 决策 + R-126-1…14 + TJ-1…7/TB-1…3/E1…E8）。评分门：独立评分 R1 **9.96 PASS**（SCORE-R1.md）；微瑕 M-1…M-6 按过门约定折入实现首步（S-1 专属验收 / E1 记忆腿换 UK 有录音词 / §8.4 禁用态 TJ 断言 / §9.2 种子清单冻结回写 / §6.6 滚动关闭返焦不回滚 / 首焦点按 §6.8 Close 末位口径 + 键名钉死 dict.accent）。状态：DOING。
- **票 126 实现完成**（2026-10-23）：实现提交 `fe213a1`（登记 `1dc4967`）。内容：D1 词典卡迁 CDK Overlay 浮层（flexibleConnectedTo 四位序 下8px→上翻转→右对齐及翻转、视口边距 8、<640px 底部 sheet；内容落定/折叠后 updatePosition 按真实高度重选位序——开卡骨架高度会让 CDK 误选、flexibleDimensions 开着不走回退，两处均活体实测后修）；D2 已查词零描边（dict-looked 呈现全删）；F-1 404 判定改 errorStatusOf（原生/转换后两路），ApiClient 全局 rethrow 一字未动；F-2 前端分词 R-T1…T5（14-year-old 只查 year-old 段、纯数字零请求）；F-3 Close 钮 DOM 末位 + 绝对定位右上、Tab 序=视觉序；F-4 口音记忆 dict.accent（换词不复位、无录音 tab 禁用+「暂无发音·UK/US」标注）；S-2 发音呈现 kind 只余 {oxford, commons}、链耗尽 available=false 无 kind、词典代码 speechSynthesis=0、无录音=「暂无发音」状态行（新键 dict.noAudio 三语）、负卡删朗读钮；F-5 后端 PosLabel 映射补全+组合标签逐分量、form 英文码映射、标签值域断言、归元标注由 forms 角色生成。验证：后端 Assessment **433/433 PASS**（基线 383，+50：TB-1…TB-3 与 supersede 改写；StartupBudget 一例环境抖动、整套复跑全绿）；前端 Jest **335/335**（基线 293，+42：TJ-1 经真实 ApiClient+真实 HttpErrorResponse flush 404/500——构造期抓出两例假绿并修替身基建、TJ-2 用例表、TJ-3…TJ-7、M-1/M-3/M-5）、tsc 0、check-templates 23/23。活体 E1–E8（本票构建 5276 + 前端 4401，未扰 119 复核线 5266/4400）：E1 位移 374→374=0、E2 翻转后卡底=词顶−8、E3 负卡无 Retry 无发音钮、E4 点 year-old 段出 year 卡/点 80 零请求、E5 真实差集词 tintinnabulation 无钮+「暂无发音」、E6 纯键盘全路径 Close 末位、E7 sheet 三边 8px、E8 已查词 computed 无下划线+pageerror=0。**偏差 2 条（如实登记，全文工作区 `ticket126/VERIFY-126.md`）**：D-126-1 §8.7 前提与实况不符——词形钮在 119 即恒渲染、FormDto 无逐词形音频可用性，「无录音不渲染」需契约扩展（§12 未授权）未擅动；现行为 = 词形钮保留、pronounce 无录音静默不播、零机器兜底（规格问题 Q-1 待裁）；D-126-2 E1/M-1 the 的 UK 音频流遭 Wikimedia 对沙盒共享出口 429 限流未取到（钮在席、pronounce 200+commons key、日志实证；119 D-10 同型环境问题）。另 Q-2：§6.7「默认播放钮」括注与 119 已验行内 DOM 序（口音 tag 为行首个可交互元素）表述差，按继承条款保 119 行为、待裁。状态：DONE（推送与 CI 见 VERIFY-126 回填）。
- **票 126 修复登记（复测缺陷 F-126-R1，先登记后开工）** — 2026-10-23：独立 UI 复测（工作区 `ticket126/UI-REVIEW-126.md` §1/§8，截图 r08）翻出唯一 FAIL 子项——popup 翻转时浮层高度未按 SPEC §6.2 末条钳制：视口高 560px、所点词上下可用空间（≈300/≈234px）均小于卡高 392px（70vh 上限）时，卡翻转方向与 8px 间距正确，但高度未收至较大侧可用空间，卡顶 = −92px，词头/音标/Close/播放钮溢出视口上方不可达。修法口径（规格现成、不改规格）：定位收尾把面板高度钳制为所选侧可用空间（min(70vh, 可用空间)），`.dict-card` 在该高度内纵向滚动；回归断言同几何下 cardTop ≥ 0 且 cardBottom ≤ 视口高。范围封顶：只动 popup 定位/钳制一处，单侧充足的既有行为（词下 +8px 锚定、翻转、移动端 sheet）不得回归。状态：DOING。
- **票 127 登记（先登记后开工）** — 2026-10-23：词典发音完整化（Mac 侧牛津包装配）+ popup 内跟读。来源：Forrest 2026-10-23 四条裁定——(a) 发音先只用美音（牛津词头录音卷未到手期间单词发音 = Commons 真人美音；英音不补、不呈现为可用；既有 UK 真人录音呈现依票 126 §8.6 不删）；(b) 发音只用真人录音、无录音标「暂无发音」、禁机器朗读（票 126 已落地，本票不回退）；(c) 跟读仿欧路：popup 内对有真人录音的词走「播原声 → 用户录音 → 复用现有发音评分链打分 → 原声/本人录音对比回放」；(d) 牛津包 Mac 侧一次性装配生效（包本就在他本地欧路目录，差的只是一次性提取 + `Dictionary:Oxford:PackDir` 配置 + 重编重启）+ 一次性装配手册交付。修法：跟读区 = popup 内独立一节（发音行之后、词形行与词性组之前）；目标词 = 所点词形优先、无 US 真人录音回落词元（D1），原声固定美音与口音切换解耦（D2），无 US 录音则入口不渲染；评分零新引擎零新端点——复用 `POST /api/assessment/pronunciation/assess` + 前端 RecorderService 管线但不走 grade() take 落库路径（referenceText = 目标文本、language 恒 en-US，D3/D5）；录音与分数仅会话内持有、关卡/换词即释放，teardown 含迟到响应（含目标解析 GET）丢弃（D4）；失败态 F-S1…F-S7 逐型诚实呈现不静默（沙盒 503 未配置态即 F-S2 常态、正好活验）；后端零改动（R-127-14 以 diff 核把关）；i18n 新增 `dict.shadow.*` 18 键三语；例句音频呈现与例句跟读不做（D7，继承 119 §3.3）；Mac 装配 = 仓库同级 dict-data 根 + compose 两处增改（D8），手册经「沙盒同套包按手册步骤同序重跑」定稿门后单独成文 `ticket127/MAC-SETUP.md`。规格：工作区 `ticket127/SPEC.md`（决策 D1–D8 + R-127-1…14 + TJ-1…TJ-8 + E1–E6 + M1/M2）。评分门：独立评分 R1 9.87 FAIL（必改四条：键数 17/18 矛盾、§5.2 解析失败无口径、§6.5 迟到丢弃未覆盖解析 GET、录音态 Close 行为未定）→ R2 修订（补第 18 键 resolveFailed + F-S7、迟到丢弃扩至解析 GET、Close = 先停录再关卡）→ 独立复评 R2 **9.93 PASS**（SCORE-R2.md）；≤0.05 级微瑕一条（§6.2 迁移括注弃旧时机措辞与 §6.5 注字面相抵，operative 口径以 §6.5 注为准）按过门约定折入实现首步订正落盘。状态：DOING。
- **票 126 修复完成（F-126-R1）**（2026-10-23）：实现提交 `fc1540c`（登记 `14369d5`）。根因：popup 高度只有卡自身 CSS `max-height: 70vh` 一道帽，CDK 落位只选侧与间距，SPEC §6.2 末条的「双侧不足时高度收至所选侧可用空间」从未落地——560px 视口、词上 300/下 234 皆 < 392 时翻转正确但卡顶被推到 −92。改法（只动定位/钳制一处）：新增纯函数 `dictPopupGeometry`（dictionary.helpers.ts）算所选侧钳制高 `min(70vh, 侧可用空间)`；宿主落位收尾 `applyDictPopupClamp` 按面板/锚点实测矩形定侧、把钳高落到卡 inline max-height（卡自带 overflow-y:auto 即卡内滚动）、再 updatePosition 以钳后高度重锚；开卡/内容落定/滚动三路重算，关卡清值；移动 sheet 与单侧充足行为不回归，后端零改动、规格未改。验证：前端 Jest **343/343**（基线 335，+8 例 F-126-R1：560px 双侧不足几何断言 cardTop=0/cardBottom=300、词头/Close 可达、下方较大侧钳制、单侧充足不钳制、宿主契约）、tsc 0、check-templates 23/23。活体（本线实例 4402 + Assessment 5266，Playwright，14/14 PASS、pageerror=0）：缺陷几何逐字复现（词顶 308）→ 卡高 392→**300**、卡顶 **−92→0**、卡底 300、间距 8、词头/Close/播放钮全在视口内、卡内滚动 scrollHeight 519 > clientHeight 298 且滚动后面板不脱锚、钳制态 Esc 返焦精确；回归 1440×900 词下开卡间距 8、`.audio-stack` 位移 374→374=0、Esc 返焦精确。证据：工作区 `ticket126/VERIFY-F126R1.md` + `ticket126/shots-fix/`（截图 3 张 + results.json）。状态：DONE（CI 结论见 VERIFY-F126R1 回填）。
- **票 127 实现完成**（2026-10-23）：实现提交 `2d8a0ef`（登记 `8f8c7c5`）。内容：popup 内跟读区（dictionary-shadow.ts 新：DictionaryShadowSession 目标解析 §5.2/状态机 §6/teardown 迟到丢弃含解析 GET/F-S1…F-S7 逐型；纯函数 pickShadowTarget/shadowResultView/isShadowNotRecognized/shadowErrorLabelKey 与既有报告 err.* 同表）；RecorderService additive 接缝 startShadowRecording/stop/cancel/assessShadowRecording（独立采集态、30s 自动停、grade 同构送评 language 恒 en-US、分项 null 保留，不走 take 落库）；dictionary-card 发音行后/词性组前跟读节（Tab 序 DOM 自然序、Close 仍末位、Esc 录音态先停录、相位焦点衔接、结果 aria-live）；practice-dictionary begin/attach/teardown 接线 + §9.4 录音态点外/点词/Enter 守卫；i18n dict.shadow.* 18 键三语。**后端零改动**（diff 名单核）。验证：Jest **381/381**（基线 343 + TJ-1…TJ-7 共 38）、tsc 0、check-templates 23/23；后端 Assessment **433/433**（无回归确认；StartupBudget 曾两红——与提取并行满载 4466ms、VM 轮换后 2012ms，同既往环境抖动型，隔离复跑 PASS、整套第三跑全绿）。活体 E1–E6 **28/28**（4401→5266，Azure key 空 = 503 态）：入口可见性（whilst UK-only/tintinnabulation 无录音不渲染）、目标 = disappeared 词形、音频 21,060 字节、写请求恰一条 assess（不落库实证）、F-S1 真实拒麦/F-S2 503 录音保留/F-S7 解析失败重试、纯键盘全程 Esc 分层返焦。手册定稿门已过：沙盒同套包按手册同序重跑——探针 PASS、全量提取 manifest 与基准逐字相同（keys 310088/entries 61589/audioFilesExtracted 0）、产物字节逐一相同、zgrep 例句登记有输出；定稿门抓出并修正手册第 2 步缺 beautifulsoup4/lxml 一处；终稿 `ticket127/MAC-SETUP.md`。**偏差：空**；口径登记 L-1…L-6（§6.2 微瑕订正已落盘、回落分支活体不可构造以 TJ-2 结案、滚动关闭录音态走 teardown、F-S2 钮文案复用 score 键、手册草案与终稿第 2 步差、活体中途 VM 轮换）全文工作区 `ticket127/VERIFY-127.md`。Mac-only 残余：M1 装配正式执行、M2 评分成功幕（待 Azure key）、audio-core 数据包投递通道（落点已钉死 ~/dev/dict-data/audio-core）。状态：DONE（推送与 CI 见 VERIFY-127 回填）。
- **票 127 修复登记（复测缺陷 F-127-R1，先登记后开工）** — 2026-10-23：独立 UI 复测（工作区 `ticket127/UI-REVIEW-127.md` 不符清单，总分 8.8/10，证据 r14 + dbg-focus.js 焦点轨迹）翻出唯一不符项（Minor）——评分往返后焦点丢失、Esc 关卡失效：送评后 scoring 相位模板只剩一行消息，被聚焦的评分钮在进入 scoring 的瞬间被重渲染销毁，焦点回落 BODY，卡根 `onCardKeydown` 够不着，连按 Esc 三次卡不关（键盘流与鼠标流均中招，Close 钮可关）；违反规格 §9.2（结果呈现后焦点应留置而非丢失）与 §9.3（非录音态 Esc 关卡在 error/result 态不成立）。根因：录音停录路径有焦点接续（停录→评分钮），唯独评分 settle 这一步缺失。修法口径（规格现成、不改规格）：进入 scoring 时若焦点在卡内则记下，settle 到 error/result 后把焦点接续到跟读区内继任钮（error 态评分重试钮优先、result 态「再练一次」优先，纯函数给优先序）；settle 时焦点已在卡内则不抢（只接掉到 BODY 的焦点）。范围封顶：只动卡组件评分 settle 焦点接续一处（+ result「再练一次」钮 data 属性 + dictionary-shadow.ts 纯函数），后端零改动；状态机其余路径（停录接续/迟到丢弃/teardown）不得回归。F-S2 已实证；成功 result 态同构一并修、沙盒不可活验（无 Azure 凭据），随 M2 在 Mac 顺带核。状态：DOING。
- **票 127 修复完成（F-127-R1）**（2026-10-23）：实现提交 `822becb`（登记 `df8738e`）。根因：被聚焦的评分钮在进入 scoring 相位的瞬间被模板销毁（该相位只剩一行「评分中…」消息），焦点当场回落 BODY，settle 后 error/result 的新按钮集无人接续，卡根 `onCardKeydown` 够不着 → Esc 关卡失效；录音停录路径有焦点接续（停录→评分钮），唯独评分 settle 这一步缺失。改法（只动评分 settle 焦点接续一处）：卡组件在进入 scoring 时（评分钮销毁前最后一刻）捕获「焦点本在卡内」，settle 到 error/result 后按纯函数 `shadowSettleFocusSelectors`（dictionary-shadow.ts）优先序接回区内继任钮——error 态：评分重试钮 → 重录 → 解析重试；result 态：「再练一次」（模板补 `data-shadow-again`）→ 我的录音 → 重录；settle 时焦点若已在卡内（评分期间用户自行移走）则不抢，只接掉到 BODY 的焦点。后端零改动；状态机/停录接续/迟到丢弃/teardown 一字未动。验证：前端 Jest **387/387**（基线 381，+TJ-8 共 6 例：失败/成功两路径优先序、其余相位空表、源码契约 3），tsc 0、check-templates 23/23。活体（本线实例 4403→Assessment 5266，Azure 未配置 = F-S2 态；Playwright 键盘流 + 鼠标流 **10/10 PASS、pageerror=0**）：settle 后 `activeElement` = 卡内 `[data-shadow-score]`「Score it」（修复前为 BODY），Esc 关卡恢复且焦点精确返回触发词 disappeared；Esc 停录分层与停录接续回归无恙。截图 3 张工作区 `ticket127/shots-fix/`、轨迹 `ticket127/work-fix/verify-f127r1-results.json`。成功 result 态沙盒不可活验（无 Azure 凭据），代码同构一并修 + TJ-8 覆盖，随 M2 在 Mac 顺带核。状态：DONE（CI 结论见工作区 `ticket127/VERIFY-F127R1.md` 回填）。
- **票 129 登记（先登记后开工）** — 2026-10-23：用户已裁小修批（同批两段、各自独立提交）。段一：CI 内存预算调整（Forrest 2026-10-23 裁 A）——`DictionaryDataTests.StartupBudget` 内存臂预算 250MB → 320MB。背景：该例内存腿在 CI 连红三棵树（实测 299.6 / 268.3 / 263.1 MB，落在 263–300MB 噪声带），三棵树的当票改动均与词典数据量无关、其余套件全绿，定性为既有环境敏感测量（票 119 偏差 D-9 已登记其测量敏感性）；沙盒整套复跑含该例 433/433 全绿。时间臂 2000ms 与其他预算一律不动。状态：DOING。
- **票 129 段二登记（先登记后开工）** — 2026-10-23：票 120 偏差 1 按 A 案收口（Forrest 2026-10-23 裁 A）。事实：规格钉死色值下图例 chip 数字三处对比度不达 4.5:1（WCAG 实测：发音 #9a6b00/#fff4d6 = 4.28、遗漏 parked 例外 #68707e/#f1f3f5 = 4.49、全 0 弱化态 #68707e/#edf0f4 = 4.37；插入 5.10 / 中断 4.81 / 停顿 5.33 已达标，见工作区 `ticket120-recognition/VERIFY-120.md` 偏差 1）。修法：仅图例 chip（`.ec … b` 作用域）的数字文字色改同色系加深至实测 ≥4.5:1（常态与全 0 态都要达标）；正文逐词色（`.rw.e-*`）一律不动；已达标三行不动；全局令牌 `--parked` / `--parked-fill` 不动（chip 作用域内以加深色值替代 var 引用）。状态：DOING。
- **票 129 段一实现完成**（2026-10-23）：提交 `b6b9a0f` —— `DictionaryDataTests.StartupBudget` 内存臂预算 250MB → 320MB（方法改名 `StartupBudget_LoadUnder2s_MemoryUnder320MB`、断言消息同步、代码注记裁决日期与噪声带依据）；时间臂 2000ms 不动；仓库内 250MB 预算引用仅此一文件两行（grep 全仓核毕）。验证：Assessment 全套 **433/433 全绿**（含该例；整套首跑时间臂 3041ms 环境抖动单红、隔离复跑 PASS、沉降后整套复跑全绿，同票 119 偏差 D-9 既往抖动型）。CI 结论见工作区 `ticket120-recognition/VERIFY-120-FIXA.md` 回填。状态：DONE。
- **票 129 段二实现完成**（2026-10-23）：提交 `8bf59ec` —— 仅图例 chip `.ec … b` 作用域三处数字色加深：发音 `#9a6b00 → #906500`；遗漏与全 0 态在 chip 作用域内以 `#616976` 替代 `var(--parked)` 引用（全局令牌 `--parked`/`--parked-fill` 不动、正文 `.rw.e-*` 色值不动、已达标的插入/中断/停顿三行不动）。验证：活体 verify120 同口径（getComputedStyle 实测）**24/24 PASS** —— 常态 发音 4.73 / 遗漏 4.98 / 插入 5.10 / 中断 4.81 / 停顿 5.33（修前 4.28 / 4.49 / 5.10 / 4.81 / 5.33），全 0 态五行 4.85（修前 4.37），标签两态 6.13，正文五类计算色与规格钉死原值逐字一致，pageerror 0；Jest **387/387**（基线守恒）、tsc 0。证据：工作区 `ticket120-recognition/VERIFY-120-FIXA.md` + `shots-fixA/`（截图 2 张 + contrast-results.json）。**票 120 偏差 1 就此按 A 案关闭。** 状态：DONE。
- **票 128 登记（先登记后开工）** — 2026-10-23：词形播放钮逐词形音频可用性（票 126 偏差 D-126-1 的 Q-1「下一小批」）。来源：Forrest 2026-10-23 对 Q-1 的正式裁决「词形没录音就不显示播放钮」+ 票 126 规格 §8.7 原定行为 + 发音三裁（只用真人录音 / 没录音不给钮 / 美音优先口音切换既有行为，不得回退）。内容：后端 `FormDto` additive 加 `Audio`（`FormAudioDto {Us, Uk}` 恒输出、不带 key/kind），粒度 = 逐词形 × 逐口音；判定语义 = 本形自己的录音、不吃词元回落，判定链 = 与 pronounce 同一条供给链的本形投影（Tier 0 本形音键+文件实存 → 核心包本形 → audio-index 本形，懒层算可用）、判定路径零网络；挂接点唯一（`LookupCore` 返回前统一回填，牛津路与开放路共用），逐词形判定异常隔离为该形 false/false、Lookup 不失败；pronounce 一处有界对齐——仅 pronounce 路径、仅 q ≠ lemma 时「本形投影全程优先」（本形三层走完才回落既有整链，词元回落与票 127 跟读回落语义保留），主端点 `audio` map 构造不走对齐、输出逐字不变。前端：`DictCardData.forms` 项加可选 `audio`，词形钮渲染 ⟺ `audio[当前口音] === true`，缺失/null/非布尔按无钮处理（逐词形隔离、诚实默认不渲染、禁止猜测性兼容分支）；文字/标签恒在、无占位无词形级无发音文案、点击 emit 逐字不变、口音切换即时重评且零新请求。混搭四象限按规格钉死（新后端+旧前端退回现行行为；旧后端+新前端词形钮全隐诚实降级）。规格：工作区 `ticket128/SPEC.md`（决策 D1–D7 + R-128-1…11 + TB-1…7 / TJ-1…4 / E1–E4）。评分门：独立评分 R1 9.88 FAIL（必改 1：§4.3 全称保证与机制/测试对不齐——跨层抢占 H1/H2）→ R2 修订采收口方案 A（对齐扩为本形投影全程优先 + TB-4/TB-5 补混合层判别夹具 + 归元前提边界声明）→ 换新独立评分员复评 R2 **9.99 PASS**（SCORE-R2.md）；唯一微瑕（TB-5① 变体 B 判别性表述过强，≤0.05 级）按过门约定折入实现首步修正（变体 B 夹具补「词元在核心包另有一条录音」）。基线：后端 433 / Jest 387，只升不降。状态：DOING。
- **票 128 实现完成**（2026-10-23）：实现提交 `b5d519d`（登记 `3c9ce60`）。内容：后端 `FormDto.Audio`（`FormAudioDto {Us, Uk}` 恒输出、无 key/kind）；判定 = 本形投影段 `ResolveFormProjection`（Tier 0 本形音键+文件实存 → 核心包本形 → index 本形，与 pronounce 投影段同一实现、共用 ResolveCore/PickIndexRec/AudioFileExists），`LookupCore` 返回前唯一挂接回填、逐词形异常隔离 false/false；pronounce 仅 q ≠ lemma 开「本形投影全程优先」、全落空回既有整链（词元回落保留），主端点 `audio` map 走未对齐公开重载逐字不变。前端 `formPlayable` 纯函数判据（audio 存在且当前口音严格 true 才渲染；缺失/null/非布尔无钮），卡组件词形钮外包 @if、emit/aria-label 逐字不变、口音切换即时重评零新请求；线 DTO `DictionaryCardDto.forms` 同形扩展（直通无映射，透明登记）。折入微瑕已兑现：TB-5① 变体 B 补词元核心包录音（c128d00）使旧机制必红、新机制必绿判别成立。验证：后端 Assessment **445 例**（基线 433 + TB-1…TB-7 共 12；整套跑仅 StartupBudget 时间臂负载抖动红、隔离复跑 PASS——开工前基线 433 整套跑同单红，119 D-9 既有环境项、与本票无关）；前端 Jest **399/399**（基线 387 + TJ-1…TJ-4 共 12；126/127 存量断言零冲突、无 supersede）、tsc 0、check-templates 23/23。活体 E1–E4 **15 PASS / 0 FAIL / 1 NOTE**（本票构建 5266 + 前端 4404，牛津包 dict-data/oxford-rerun127 真实数据）：E1 disappeared/girls 两卡 DOM 钮集与 DTO 逐词形逐口音全等；E2 girl 卡切 UK girls 钮消失、gender 卡切 UK gendered 钮出现（双向），切换全程 dictionary 请求 0 新增；E3 核心包词形钮 pronounce 200 + 音频流 200/21,060 B，index 层词形断言止于 pronounce available=true（取流 404 为 §4.2 既登残余、D-126-2 同型，如实记录非判据）；E4 disappears/disappearing 行只有文字标签无钮；pageerror=0；截图 5 张。**偏差：空**（全文工作区 `ticket128/VERIFY-128.md`）。残余（§12 既登）：Tier 0 第 1 层真实包活体 true 路径待词头录音卷到手后在 Mac 随票 127 M1 顺带复跑，不阻塞。CI（run `40062647493`）：Frontend success / E2E success / Backend failure——唯一红因为 Interviews 并发 upsert 赛跑测试撞 PG 40P01 deadlock，基线 `325be45` 的 run `40059483092` 同测试同死法先红，与本票改动面无交集（该步中止于 Interviews、Assessment 项目未及执行，基线同）；重跑 API 被 PAT 403 挡住。状态：DONE。
- **票 123 登记（先登记后开工）** — 2026-10-24：设计修复（票 118 落地后独立复测 8.3/10 未达 9.5 线催生的修复票）。**执行方：WorkBuddy**（Forrest 2026-10-24 指示交接：由 WorkBuddy 实现，主线守护/复测）。定稿：`docs/fix-program/source/ticket123-REMEDIATION-PLAN.md`（随本登记入仓；设计稿 = `docs/fix-program/source/ticket123-mocks/`，为设计契约，逐项对照不得走样）。评分门：独立评分 R4 **10.00 PASS**（存证 `docs/fix-program/source/ticket123-SCORE-R4.md`）。关键注记：方案诚实投影全量 **9.48**（区间 9.3–9.65，点估计不到 9.5）——实现后由主线按同 rubric 独立复测判 9.5 线，达线与否照实报、不许注水凑分，未达由 Forrest 裁决后续。状态：DOING（2026-10-24 WorkBuddy 开工：先登记后开工已执行——本条 + 队列表 + 运行回执一并落盘后才动代码；基线 `develop-new-features` tip `86a538f`；Phase 0 逐条复核 Appendix B pin 与当前树一致性）。
- **票 123 实现完成**（2026-10-24）：实现提交 `0c743b4`（台账补正 `89c35ac`，并入主线 `d139fc3`，远端 tip 已 `git ls-remote` 确认 `d139fc3`）。范围 = R-1…R-10 + §C 四项（**G-items 按定稿不在本次范围**）。逐项落地：Phase 0 审计 → R-1 令牌残留扫掠 + R-2 调色板全量清扫 → R-7 内容度量/页标题统一 → R-3 文案清扫与中点清除 → R-4 看板收尾（等待行/chip 行/可访问名）→ R-5 标注空值 → R-6 Tracker 零态卡 → R-10 热力图邀请行 → R-8 字标用产品自己的字体 → R-9 The Track（共享几何源 → Dashboard Band → Playbook 轨道 → 抽屉时间轴迁移）→ §C Craft 四项（C-1 令牌+reduced motion / C-2 焦点补全 / C-3 hover+press 统一 / C-4 contentIn 编舞）。**偏差清单：空**（逐项对照定稿验收，见 `docs/fix-program/evidence-123/` 下 11 份实施文档：PHASE0-AUDIT / R1-R2 / R3 / R4 / R5 / R6 / R10 / R8 / R9 / C-IMPL + 12 份探针副本）。验证：tsc 0 错 · Jest **19 suites / 476 tests 全过** · `ng build --configuration production` 成功 · `check-templates` **23/23** · 三个活体探针全过（`census-craft-r9c.mjs` §C 四项全达标 / `census-track-geometry-r9.mjs` R-9 四面几何**逐字**一致：节点 16px×16px、描边 2px、轨道粗细 2px / `census-wordmark-font-r8.mjs` R-8 字体契约达标）——§C 改动未破坏 R-8/R-9。诚实登记三件遗留：①两处原本无 easing 关键字的 transition（`playbook-calendar.component.scss:73`、`techstack.component.scss:418`）只 token 化时长、保留 CSS 默认 `ease` 语义（强加 Material emphasized 曲线会改手感，超出定稿授权）；②`.sankey-node-g:focus-visible { outline: none; }` 系定稿 C-2 明写的焦点表达方式（焦点由 SVG stroke 承担），非违规的「剩余 outline:none」；③G-items（3 项已登记残余）按定稿为可选阶段，未实现。**状态：PUSHED**（**不自评关单** —— 方案诚实投影 9.48 不到 9.5 线，9.5 线由主线按同 rubric 独立复测判）。
- **票 123 主线接手修复**（2026-10-24，主线）：接手触发条件成立（WorkBuddy 本线 DOING 归零 + 新 tip CI 仍红且恰为原 2 例 E2E），主线按 Forrest 05:10 接手令接手。修法：①E2E 定位跟上 R-9 新契约（`[data-testid="round-track"] .rnode` + 三语轮次文案，未恢复 `.step`）；②Tech Stack 720p split 保底（`.page` 纵向可滚 + `.split { min-height: 120px }`，未删 content-in、未在 spec 预折叠 filters）。改动面仅 2 文件（e2e spec + techstack scss），后端零改动。验证：tsc 0、Jest 514/514、check-templates 24/24、E2E 两例本地默认超时下通过（720p + filters 展开 + 邀请行在席三重条件经探针确认）。证据：工作区 `ticket123/VERIFY-123-TAKEOVER.md`。**状态：VERIFIED**（主线独立复测 `ticket123/UI-REVIEW-123-TAKEOVER.md`：五轴均值 9.5 恰好达线，不符清单为空；CI run 40085137066 三项全绿；最终裁决由主线据复测建议执行）。
- **票 124 转 DOING**（2026-10-24 WorkBuddy 开工）：先登记后开工已执行 —— 本条 + 队列表（123 转 PUSHED、124 转 DOING）+ 运行回执（远端 tip 刷新为 `d139fc3`）一并落盘后才动代码。基线 = 本线当前 tip `d139fc3`（已含主线票 125/130/131/132/133 并入）。范围钉死（照定稿 D1–D8）：仅 Assessment 练习素材（practice_materials）；恢复单位 = 顶层已删节点 + 其全部已删后代（+ 防御性已删祖先链），另设「全部恢复」；零迁移（删除时间取行 `UpdatedAt`，不加列、不做迁移）；恢复响应回整树包 `{ restored, treeVersion, nodes }`（D5，不用 Knowledge 式 204）；入口镜像 Tech Stack 回收站形态（材料面板工具行图标按钮 + 计数徽标 + 模态对话框），树空且回收站非空时空状态下加一行回收站指针（D6）；**不含永久删除**（D4）、**不动整树保存熔断**（D7），二者均列后续票候选。端点/权限/错误口径对齐 Assessment 既有惯例，恢复动词对齐 Knowledge 的 `POST {id}/restore`（D8）。**实现首步折入微瑕 N1**：规格第 6 行与第 337 行两处「附录 V1–V29」指针订正为 V1–V30。证据目录 `docs/fix-program/evidence-124/`。
- **票 124 登记（先登记后开工）** — 2026-10-24：回收站（仅 Assessment 练习素材；事故依据 = 整树覆盖保存软删 135 条素材、应用内无自助恢复入口）。**执行方：WorkBuddy**（Forrest 2026-10-24 指示交接）。定稿：`docs/fix-program/source/ticket124-SPEC.md`（随本登记入仓）。评分门：独立评分 R2 **9.99 PASS**（存证 `docs/fix-program/source/ticket124-SCORE-R2.md`）。范围钉死：恢复单位 = 节点 + 全部已删后代；零迁移（删除时间取行 `UpdatedAt`，不加列）；恢复响应回整树包；入口镜像 Tech Stack 回收站 + 计数徽标；不含永久删除、不动保存熔断。关键注记：实现首步含微瑕 N1 修正——规格第 6 行与第 337 行两处「附录 V1–V29」指针订正为 V1–V30（≤0.05 级纯文字瑕，按过门约定折入实现首步、不重开评分轮）。**顺序：排在票 123 之后**（123 先行、124 随后，同一时刻仅一票 DOING）。状态：DOING（2026-10-24 WorkBuddy 开工）。
- **票 124 实现完成**（2026-10-24 WorkBuddy）：实现提交 `6cbdebd`（远端 tip 已 `git ls-remote` 确认 `6cbdebd`）。范围 = 定稿 D1–D8 全部落地。逐项落地：①后端新增 `Application/PracticeMaterialTrashHandlers.cs`（List / Restore / RestoreAll 三处理器 + 共享 `MaterialTreeBuilder`——`PracticeMaterialHandlers` 的原 `Build` 原样提取为共享件，两处拼装同口径，行为一字未改，钉 T8）；`AssessmentController` 增三端点（`GET /api/assessment/materials/trash` = MockRead、`POST /api/assessment/materials/{id}/restore` = MockManage、`POST /api/assessment/materials/trash/restore-all` = MockManage）+ `AssessmentEndpointPermissionsTests` 增三端点策略钉测；**零迁移**（恢复只翻既有行 `IsDeleted`，删除时间取行 `UpdatedAt`；`PracticeMaterial` 实体零改动）。②前端新增 `material-trash.helpers.ts`（纯逻辑层：行视图 / 徽标 / 三态判据——因仓内 Jest 基建为 node 环境不引 TestBed，抽出纯函数后 F2/F3/F5 才是真行为断言而非源码字符串断言）、`material-trash-dialog.component.{ts,html,scss}`（受控对话框，纯展示 + 抛事件，契约 C2；三态 = 加载中 / 失败+重试 / 空态，**加载失败绝不冒充空态**）、`practice-api-trash.spec.ts`（F1–F6）；`practice-api.service.ts` 增三方法 + `adoptTreePackage()` 整体采纳树包（契约 C1）；`practice-material-tree.ts` 增回收站编排 + `adoptRestoredTree()`（恢复成功后 `treeDirty=false` 且 `treeDeletedSinceSave=false`，契约 C3）；`material-tree.component` 增 `.head-trash` 入口 + `.trash-badge` 徽标（>99 → `99+`），树空时在既有 `emptyHint` **下方**追加 `.tree-trash-hint` 指针行（**不改既有文案与显示条件**，仅 `trashCount>0` 时渲染）；`i18n.service.ts` 增 12 个 `practice.trash.*` 键（zh/en/fr 三语）；`styles.scss` 增 `.app-trash` 弹窗外壳。③两条待裁决项**按推荐 A 落地**（断裂点重根走 `MoveTo(null, SortOrder)`、`restored` 计数在调用侧判 `IsDeleted`），**均不破「实体零改动」不变项** —— 仍登记在待确认区等 Forrest 裁决，未自行拍板关案。④规格微瑕 **N1** 已随首步折入（第 6、337 行两处「附录 V1–V29」→ V1–V30）。**偏差清单：空**（逐条对照定稿 D1–D8 / §4 / §5 / §8.1–§8.5 / §11 R1–R10 / §12 不变项 #1–#7，见 `docs/fix-program/evidence-124/IMPL.md`）。验证：后端 `PracticeMaterialTrashHandlerTests` **T1–T14 14/14**（含 T13 `session_replication_role=replica` 造悬空父行 / T14 多级断裂）+ 权限钉测；Assessment 全量 **474 例中 473 绿**（唯一红 `DictionaryDataTests.StartupBudget_LoadUnder2s_MemoryUnder320MB` = **既有环境敏感测量 flake**，票 119 D-9 / 票 129 段一已登项，隔离复跑 **3/3 全过**，该测试测词典开放层载入耗时与 GC 增量、与本票改动面零交集 —— **未为其上调任何预算、未动无关代码**）；前端 Jest **20 suites / 514 tests 全过**（含新增 `practice-api-trash.spec.ts` 38 例）、`tsc --noEmit` 0 错、`check-templates` **24/24**、`ng build --configuration production` 成功（1.09 MB / 255.84 kB gzip）；活体 API 层 **E1–E8 36/36**（含 E7 恢复后**不刷新**直接改名保存不报 409 且被恢复节点未被静默再删 = C1/C3 端到端证明、E8 两个隔离维度：无 `mock.manage` → **403 鉴权先行**、有权限的他人 → **404 不泄露存在性**）；活体浏览器 UI 层 **U1–U7 16/16**（真实 DOM 断言：入口按钮 / 徽标计数与后端口径一致 / 对话框三态 / 清单行字段 / 点恢复后该行移出+toast+徽标减一 / 树空指针行 / pageerror 全程 0），截图存证 `/workspace/ticket124-recognition/`（7 张，实现票自定路径，已按规格 §11.3 登记）。诚实登记两件：①`DictionaryService.cs` / `OpenLayerStore.cs` 两处**编译消歧**（C# 12 集合表达式致 CS0121 / CS7036，实测不改则构建不过）随本票一并提交 —— **行为不变**，非功能改动；②规格 §13 的 **S3 残余孔**（持 `TreeVersion=null` 的旧调用方保存仍可再删恢复项）**本票不堵**，按定稿留给后续保存契约票，未在最高危写路径上加无规格启发式。**状态：PUSHED**（**不自评关单** —— 9.5 线由主线按同 rubric 独立复测判）。
- **票 125 登记（先登记后开工）** — 2026-10-24：AI Practice 五项评审修复（追问恒死改取 score.recognized / 批硬编码中文全量 i18n / 删 Score Results 外层标题行 / Take 整行点击 / 第五项候选与判别、不凑数不预修）。**执行方：WorkBuddy**（主线规格已过门，追加交接）。定稿：`docs/fix-program/source/ticket125-SPEC.md`（随本登记入仓）。评分门：独立评分 R1 **9.95 PASS**（存证 `docs/fix-program/source/ticket125-SCORE-R1.md`）。关键注记：实现首步含微瑕 K-1 修正——证据行号勘误（C-12 三处 host.t 先例 :259/:262/:396 → :256/:260/:385；5.3 表 pace 单位行 :752 → :754；C-10 .replace 先例 :675-676 → :672/:679），以方法名/选择器重锚（≤0.05 级纯文字瑕，按过门约定折入实现首步、不重开评分轮）。**顺序：排在票 124 之后**（123→124→125，同一时刻仅一票 DOING；123、124 未 PUSHED 前不许开工 125）。状态：TODO（WorkBuddy 开工时自行转 DOING；先登记后开工规矩不变）。
- **票 132 登记（先登记后开工）** — 2026-10-24：重载 take 点词回放无声修复（票 122 已知发现的后续票）。来源：票 122 落地时实测——重载条 take 经 `fromDto` 得 0 字节占位 blob（truthy），`replayAt` 以 truthiness 为唯一闸门、对空源 createObjectURL 后 `play()` 必败（实测 `MEDIA_ERR_SRC_NOT_SUPPORTED`），只弹 `practice.retryLater` 笼统文案；全程不发任何请求，也没接上仓内现成的鉴权拉流机制（grade/togglePlay/waveAudioSource 三处都在用，唯独 replayAt 没接）。修法（规格 R-F1…R-F8）：仅 `replayAt` 一处——本地有源判据与全仓统一为 `blob.size > 0`；本地无真源且 id 为 GUID 时，点播当刻直走 `fetchRecordingAudio` 懒拉取（否决加载时回填：不为列表每条 take 预下载整段音频），取回后同公式 seek 播放；失败提示照 togglePlay 分级（404 → `practice.recGone` 明示文件已不存在、其余 → `practice.recLoadFail`，禁 retryLater 冒充取流失败）；在途竞态作废（切 take/连点新词时旧结果不播不弹）；取回 blob 不回写 Recording、不进 `audioBlobUrls` 缓存；后端零改动、零契约变更（音频端点现成：行不存在/文件不在同归 404）。规格：工作区 `ticket132/SPEC.md`（根因三段链 + 决策 D1–D7 + 零回退清单 10 条 + 失败态 F-S1…F-S7 + 验收 R-132-1…R-132-8 + Jest TJ-1…TJ-5 + 活体 E1–E4）。评分门：独立评分 R1 **9.99 PASS**（工作区 `ticket132/SCORE-R1.md`，五项重点核查亲验通过、扣分点无）。基线：后端 445 不变全绿、Jest 399 只升不降。执行方：主线。状态：DOING。
- **票 132 实现完成**（2026-10-24）：实现提交 `2fa05dd`（登记 `973b62b`，变基于 `86a538f`）。内容：仅 `practice-grading-session.ts` 的 `replayAt` 一处（+ 同文件私有帮手）——本地有源判据与全仓统一为 `blob.size > 0`（0 字节占位 blob 不再被当真源）；本地无真源且 id 为 GUID 时点播当刻直走 `fetchRecordingAudio` 鉴权懒拉取（否决加载时回填），取回后同公式 seek（offset−0.5s）播放；失败照 togglePlay 分级（404 → `practice.recGone` 明示文件已不存在、其余 → `practice.recLoadFail` 组文，禁 retryLater 冒充取流失败；取到源但 play 被拒仍 retryLater）；在途竞态作废（切 take/连点新词时旧结果不播不弹、未播 URL revoke）；取回 blob 不回写 Recording、不进 `audioBlobUrls` 缓存；**后端零改动、零契约变更**（除台账外 diff 仅 3 个前端文件）。验证：Jest **410/410**（基线 399 + TJ-1…TJ-4 新 11 例；TDD 红灯实证：旧实现下 7 红/4 绿；TJ-5 由票 122 存量分发断言承担）、tsc 0、check-templates 23/23；后端 Assessment **445 例**（首跑 444 绿 + StartupBudget 时间臂 4414ms 环境抖动单红——票 119 D-9 既有项、隔离复跑 PASS；首跑 85 红已定位为沙盒库 assessment schema 缺失、经本票构建 5266 的 Seeder 迁移补齐后收敛，非代码问题）。API 层活体：种子 7 条 legacy + E-take（`af897d02…`，um@3200 回读一致）；replayAt 将发的那条音频请求实测 200/320,044 字节/RIFF 头；文件临时移走 → 404 `录音文件 不存在`（前端映射 recGone 的服务端前提成立），文件已原样恢复。**待补（诚实登记）**：E1–E4 的真实浏览器段因实现方无 live browser 权限未跑——前端 4405 + Assessment 5266 + 种子已就绪，逐条剧本见工作区 `ticket132/BROWSER-132.md`；浏览器任务跑完 E1–E4 且 pageerror 全净后方可转 VERIFIED。supersede 登记（规格 §9.1）：票 122 分发 spec 宿主替身补 `practiceApi` 接缝（断言口径不变）。证据：工作区 `ticket132/VERIFY-132.md` + `BROWSER-132.md` + `seed-etape.py`。浏览器段（VM 侧 Playwright 真浏览器，2026-10-24）：E1 重载后点 um —— 音频请求 200/320,044 字节、paused=false、起点 2.7277（≈2.7±0.3）、duration=10；E2 mic-gated（以 TJ-1 为证，不冒充）；E3 文件移走 → 404 + recGone 英文文案与 i18n 源逐字一致、失败不构造 Audio，文件已原样恢复并复查回 200；E4 零回退抽查（legacy 点词零请求、togglePlay 正常、切回 E-take 200）；E1–E4 计 22 PASS / 0 FAIL，pageerror 全程 0。注：验证开工时 VM 轮换致旧库清空，种子已按既定程序经应用自身 API 重建（非直写库），新 E-take 与新素材 id 见 VERIFY-132.md §4。证据：工作区 ticket132/VERIFY-132.md §4 + live/e132-results.json + shots/e1-reload-replay.png + shots/e3-expired-toast.png。状态：VERIFIED（执行方：主线）。
- **票 130 登记（先登记后开工）** — 2026-10-24：词典点词慢与白条修复。来源：Forrest 2026-10-24 Mac 实测（第一次点慢、白色长条先行、后续点仍有一点延迟）+ 诊断定案（工作区 `ticket128/PERF-DIAG.md`：冷首查 ~3.4s 三层懒构建压首查、骨架条宽塌成 0 的白条、热态空壳闪现）+ Forrest 对修法选项的 **A 案裁决**（全做：C1 启动预热 + H1 骨架复活定宽 + H3 空壳延迟出示）。**执行方：主线**。内容：后端主机启动层一次性后台预热（五段组成：开放层构建 / 牛津状态载入 / 核心包 manifest 解析 / 哑查词 / 哑 pronounce；并发语义钉死 = 同一构建只发生一次、赛跑首查等同一次构建且不慢于基线；核心包 `EnsureCoreLoaded` 先发布后填充的半成品竞态收口为一次性发布；预热失败 catch-all 诚实降级、逐层与基线一致、绝不冒泡为启动失败；就绪/健康探针语义零改动）；前端浮层内卡宽恒 = 面板宽（全状态，骨架条百分比据实解析、骨架屏复活）+ 空壳延迟出示 150ms 三事件状态机（落定先到直达无壳 / 阈值先到挂骨架壳原地换 / 取消先到永不挂出；错误快路不等阈值；已挂出切换不进延迟窗）。红线：`StartupBudget` 测试本体与预算数字（2,000ms / 320MB）零改动、变红即越界信号。规格：工作区 `ticket130/SPEC.md`（R-130-1…12 + TB-1…TB-5 / TJ-1…TJ-3 / E-130-C1·C2·H1…H5）。评分门：独立评分 R1 **9.90 PASS**（工作区 `ticket130/SCORE-R1.md`）；三处 ≤0.05 级微瑕按过门约定折入实现首步——K-1：TJ-2 补「滚动出视口」取消一路断言；K-2：retry 窗内显示裁决（主线裁决：旧错误卡保留在席直至新结果落定、不卸下不换骨架，150ms 延迟窗只管无卡在席的全新挂载；TJ-1⑤ 补窗内显示态断言）；K-3：活体 E-130-H2 逐词点击前先关卡、明写进执行记录。基线：后端 445 / Jest 399，只升不降。状态：DOING。
- **票 130 实现完成**（2026-10-24）：实现提交 `99896d2`（登记 `43346dd`，变基于 `8a82451` 票 132 落地）。内容：C1 `DictionaryWarmup` + `DictionaryWarmupHostedService`（五段 W1–W5、失败 catch-all 诚实降级、Task.Run 非阻塞——race4 探针发现直接 await 拖住监听 2–4.5s，已修复并由 TB-1b + race5/C2 赛跑段复证）；`EnsureCoreLoaded` 一次性发布（修半成品竞态）；H1 `.dict-card { width: 100% }` 全状态卡宽恒=面板宽；H3 `DICT_POPUP_DELAY_MS = 150` 三事件状态机（K-2 retry 旧错误卡保留在席直至落定）。验证：后端 **456/456**（445 + TB-1/TB-1b/TB-2/TB-3/TB-4/TB-5 共 11；变基前多轮唯一红为 StartupBudget 时间臂抖动——票 119 D-9 既有项，本体与数字 diff 为空、隔离复跑 2/3 PASS、变基后整套 456/456 全绿）；Jest **422/422**（399 + 票 132 的 11 + 本票 TJ-1…TJ-3 共 12）、tsc 0；活体 E-130 全组 **26 PASS / 0 FAIL**（预热 1773–3285ms、预热后新词 curl 2–53ms、浏览器首点 99–198ms、壳精确时序 155ms 桌面+移动双测、H2 中位 83–113ms 零骨架帧、H3 慢查壳 202–211ms + Esc 取消 + 30ms 404 直达 90–114ms、H4 慢/快双路整卡在视口内且 Close 与播放钮可达、H5 只挂 B 卡且两次请求都发出、error 卡宽 400=400、pageerror 0；C2 重启后首点 163ms；赛跑窗内首查正确 200/1352ms ≤基线 3,344ms）。证据：工作区 `ticket130/VERIFY-130.md` + `live/` + `shots/`。**偏差：空**。独立 UI 复测（2026-10-24，非实现方，自备脚本 review130.js + probe-focus.js，自建全栈 ng build 产物 + 4406 代理 + Assessment 5276 真实牛津包/audio-core 数据，4200 未用）：主走查 15 PASS / 0 FAIL / 1 NOTE，三上下文（桌面/560px 矮视口/360px 移动）pageerror 全程 0；H1 骨架逐值复现（桌面 panel/card 400、骨架容器 366、三条 [139.08, 336.72, 234.23]；移动 344/344/310、三条 [117.80, 285.19, 198.39]，壳→落定宽差 0，error 卡 400=400）；H3 慢查壳首现 198ms（规格带 [150,250]）、热态 8 词 8/8 零骨架帧中位 63ms、Esc 取消干净（1000ms+ 后浮层 0、焦点回触发词）、30ms 404 直达 88ms 零骨架帧、连点换词只挂 B 卡；H4 矮视口慢/快双路卡底 560 = 视口高、Close 与播放钮可达；焦点：开卡首焦点 American accent 钮、Esc 回触发词；C1 独立证据：启动日志自现 warmup complete 五段全 ok、预热后 the 47ms / um 27ms。打分 9.7/10（扣 0.2 headless 无声卡未亲耳验声 + 后端套件未重跑、扣 0.1 移动抽屉 scrim 既有行为 NOTE M0，不立票），不符清单为空。证据：工作区 ticket130/UI-REVIEW-130.md + ui-review-shots/ 6 张。状态：VERIFIED（执行方：主线）。
- **票 133 登记（先登记后开工）** — 2026-10-24：Prep 报告模板升级（8 节 → 12 常驻 + 1 条件）。来源：用户裁定按 `prep-report-framework/RESEARCH.md` 12+1 总表推进 + 主线三条钉死口径（双路同构、分流、话术恒英文）。内容：规范结构 13 节位 + 轮次分类器（Stage 正则五布尔）+ 服务端结构归一化 N-1…N-8（含 §6.5 后置路由校验四步）+ 问题分流（卡题→弱点预案节、余题按 Category 路由、SmallTalk 移除）+ 语言口径 SP-2（spoken lines stay in English）+ maxTokens 8000→16000 + 本地路分节 builder 与三语 LocalTexts 扩展 + 前端 fw 骨架同构与 note 呈现。规格：工作区 `ticket133/SPEC.md` R2（独立复评 **9.95 PASS**，`ticket133/SCORE-R2.md`；微瑕 N-1 打捞规则折入实现首步）。数据铁律：验收样本只用应用内真实 Corpay/Mohawk 条目（§15），沙盒无真实记录时对应断言登记未覆盖、不造数。基线：后端 445、Jest 410 只升不降。执行方：主线。状态：DOING。
- **票 133 实现完成**（2026-10-24）：实现提交 `23a15a2`（登记 `8eef79a`，变基于 `985536f` 票 130 落地）。内容：Prep 报告 8 节 → 12 常驻 + 1 条件（13 节位顺序钉死）；新文件 `PrepReportStructure.cs`（轮次分类器 §4.2 五布尔、§6.2 分流、本地分节 builder、AI 提示词 SP-1…SP-7、结构归一化 N-1…N-8 + §6.5 四步）；Handler 仅做编排（AI 路：提示词 + maxTokens 16000 + 归一化后落库，解析失败/网关异常走整体本地兜底，旧两 catch 语义不变；旧 SystemPromptFor/BuildAiPrompt/AppendFetched/旧 LocalTexts/Trunc 已删，BuildSources/Detail/NormalizeLanguage/LoadSourceContext/SafeSearch 原样保留）；前端 fw 骨架 13 节同构 + §4.2 分类镜像 + `note` 字段与卡片内一行呈现 + 导出排除 note；i18n 新增 roundBrief/codingRound/designRound/weaknessPlan/compProcess/designCard 三语键、全部 fw 标题去数字前缀、focus.* 键删除。评分微瑕 N-1 兑现：归一化丢弃节之前先打捞有据题按 §6.2 移入规范落点（SmallTalk 按 §6.5 第 3 条移除），对应测试 `Normalize_Salvage_GroundedQuestionOnlyInDiscardedCodingSection` 落地。验证：后端 Interviews **403 例**（基线实测 351——登记条所写 445 系 Assessment 套件口径，特此订正——+ 本票 52 例；全套首跑 1 例 StarStory 并发死锁既有 flake（PG 40P01）红、隔离复跑 17/17 PASS，与本票无关）；Jest **453/453**（变基后基线 422 + 本票 31 例）；tsc 0、check-templates 23/23；沙盒 API 活体 `ticket133/live-133.py` **25/25**（12/12/11 节矩阵、非卡路由五类落点、SmallTalk absent、zh 语言口径、诚实空态、事实块首位、structureVersion=2、无 filledSections、本地路 local-aggregation）。**待补（诚实登记）**：① Mac 侧 E1–E3 真实样本活体（Corpay 设计/编程/经验三轮）+ Q-1 内容质量（自介三档词数、故事提取、JD 映射）+ maxTokens 16000 三轮 p90 校准——沙盒无真实记录，TB-5/TB-7/TB-10/TB-12 真实内容断言走 `PREP_REPORT_SAMPLE_EXPORT` 导出缝、缺导出时跳过登记；② 模板 note 行浏览器可见性（数据层已验：note 字段 + 导出排除 + 旧 8 节兼容；沙盒 ng build 因 8GB 共享机 OOM 未跑通，tsc 与模板结构检查已过）；③ ng build 生产构建待内存充裕环境复跑。证据：工作区 `ticket133/VERIFY-133.md` + `live-133.py`。状态：PUSHED（执行方：主线）。
- **票 133 修复 F-133-R1（先登记后开工）** — 2026-10-24：独立复核阻塞项（REVIEW-133.md）：题库关时弱点仍进报告，违规格 §6.1 明文条款（题库关时原题与弱点一律不进报告、弱点节呈现空态 5.10）。根因：Handler BuildSourceData 只门控 questions、weaknesses 无条件透传；归一化 bank-off 分支只清 questions、不动 weakness-plan blocks；N-2/N-3 脚手架与 N-5 重建以未门控 d 重建弱点块。修法：BuildSourceData 弱点按题库开关门控；BuildLocalReport 与 Normalize 入口以门控后数据建节；归一化 bank-off 终步以空弱点脚手架重置 weakness-plan blocks；补本地/AI 两则测试（题库关 + 弱点存在 → 空态；题库开行为不变）。验证：后端 Interviews **405/405**（基线 403 + 本修复 2 例；首跑 103 例 FileNotFound 系控制台运行器缺共享框架 dll 环境问题，补拷 8.0.31 全量 dll 后消除）；Jest **476/476**（基线不变，零前端改动）；tsc 0、check-templates 23/23；两路复现测试红灯先行（本地类 2 失败、归一化类 1 失败）修后转绿。证据：工作区 ticket133/VERIFY-133-FIX-R1.md。独立复核（2026-10-24，非实现方，报告工作区 ticket133/REVIEW-133-FIX-R1.md）：远端 tip db2afd32 核实、改动面仅 4 文件（2 产品 + 1 测试 + 台账，定稿未动、零前端文件）；按 REVIEW-133 原始描述重建的独立探针跑在整节序列化 JSON 上（比实现方 items 视图更严），修复后 3/3 PASS（本地路 bank-off + 弱点 → 空态行在席、弱点标题/细节全无；AI 路罐装弱点块 bank-off → 剥离为空态；题库开回归 → 弱点正常渲染、无空态行）；探针真实性反证——两产品文件临时还原为修复前版本（6d884b8b）重跑，Probe1/Probe2 失败且失败原文与 REVIEW-133 原始失败逐项一致，Probe3 通过，随后恢复 db2afd32、工作树干净；亲手复跑后端 Interviews 405/405、Jest 476/476（19 套件）、tsc 0、check-templates 23/23；不符清单为空，无 F-133-R2。执行方：主线。状态：VERIFIED（执行方：主线）。
- **票 131 登记（先登记后开工）** — 2026-10-24：口音页签放行判据升级为卡级（词元 ∨ 任一词形）。来源：用户 2026-10-24 正式裁决「任一词形有录音就放行口音页签」+ 票 128 独立 UI 复测观察项 O-2（reached 卡：词形 reaches 有 UK 录音、词元无 UK，新会话默认 US 下永远切不到 UK）。内容：纯前端票（后端零改动、DTO 契约零改动）：dictionary.helpers.ts 新增卡级纯函数 tabEnabled(c, A) = accentButtonVisible(c.audio, A) ∨ 任一词形 formPlayable(f, A)（复用现成严格纯函数，零请求）；accentButtonVisible 本体不许改（词元钮判据隔离）；三处连带绑定跟随新判据（页签 disabled/title/aria-label、音标行渲染条件 phoneticOf ∨ tabEnabled、整卡「暂无发音」行触发改以 cardHasAnyAudio = tabEnabled(us) ∨ tabEnabled(uk)，按 126 §8.3 卡级原文对齐）；口音持久化零改动、无自动回落；皆无录音形态与现行数学等价。规格：工作区 ticket131/SPEC.md（R-131-1…9 + TJ-131-1…8 + 活体 E1–E5）。评分门：独立评分 9.95 PASS；微瑕 K-1（−0.05）折入实现首步——存量 TJ-7 的源码字符串断言 toContain('[disabled]="!accentVisible(c, acc)"') 必随绑定改动，预先登记为 supersede 项（新断言钉 tabEnabled 绑定、对应 R-131-2），TJ-7 其余断言不许动。基线：后端零改动、Jest 453 只升不降（动手前实测基线见实现报告）。执行方：主线。状态：DOING。
- **票 131 实现完成**（2026-10-24）：实现提交 `b2e67374`（登记 `6b967808`，变基于 `2f17546f` 票 130 转 VERIFIED）。内容：纯前端票（后端零改动、DTO 契约零改动）：dictionary.helpers.ts 新增卡级纯函数 `tabEnabled(c, A) = accentButtonVisible(c.audio, A) ∨ 任一词形 formPlayable(f, A)`（复用现成严格口径，零请求）+ `cardHasAnyAudio = tabEnabled(us) ∨ tabEnabled(uk)`；卡模板四处绑定跟随新判据（页签 disabled/title/aria-label、音标行条件 `phoneticOf ∨ tabEnabled`、整卡状态行 `!cardHasAnyAudio`）；词元播放钮仍走 `accentVisible`（`accentButtonVisible` 本体未动，R-T4 隔离）；口音记忆零改动、无自动回落；皆无录音形态与现行数学等价。验证：Jest **476/476**（基线 453 + TJ-131-1…TJ-131-8 新 23 例；TDD 红灯实证：旧实现下 0 例可跑）、tsc 0、check-templates 23/23；CI run 40071977970 三臂全绿（Backend/Frontend/E2E）。活体（VM 侧 Playwright 真浏览器，前端 4406 + 现成后端栈）：E1–E5 计 **44 PASS / 0 FAIL / 1 NOTE**——E1 reached 新会话 UK 页签可点无标注、切 UK 后 reaches 钮在/词元 UK 钮不在/状态行不在、DTO↔DOM 逐词形一致、切换零 dictionary 新请求；E2 128 矩阵回归（girls/gender/disappeared/figures，词形行按当前 DTO、词元钮另计）；E3 P-1 零点击可达 + P-2 无自动回落（记忆 uk 保持、点 US 后落 us）；E4 真实皆空卡（Teenage）状态行在、页签全禁用带标注、零播放钮；E5 跟读原声只认 US（pronounce accent=us）、切页签目标不变；pageerror 全程 0。supersede 登记（§9.2 审计）：S-131-1（K-1 预登记，TJ-7/M-3 tab disabled 改钉 tabEnabled 绑定）、S-131-2（126 状态行契约改钉 cardHasAnyAudio 绑定），余者不动。残余（诚实登记）：Tier 0 词形录音活体 true 路径与 128 §12 同源（Mac 随 E1 变体顺带复跑）；E5 评分成功态待 Mac 实点。证据：工作区 `ticket131/VERIFY-131.md` + `live-e131.js` + `live-e131-results.json` + `shots/` 5 张。独立复测（2026-10-24，非实现方，自备脚本 review131.js + 判别探针，自建前端 ng serve 4406 + 现成后端栈，4200 未用）：代码审计逐字对 diff（改动面 4 前端文件 + 台账、后端零文件；tabEnabled/cardHasAnyAudio 与规格 §4 公式逐字一致；accentButtonVisible 本体一字未动；anyAccent() 零残留引用；accent.set 全仓仅 setAccent 一处）；亲手复跑 Jest 476/476（19 套件）、tsc 0、check-templates 23/23；活体：E1 全新会话 reached、UK 页签可点无标注、切 UK 后词形钮集 [reaches] 与 DTO forms[].audio.uk 逐项一致、词元 UK 钮不在/US 钮在/状态行不在、切换 dictionary 请求增量 0；E2 girls US→UK→US、gendered UK 出现、disappeared/figures 与 DTO 一致；E3 P-1 预置 uk 零点击在席且记忆保持 uk、P-2 无自动回落（初始仍 UK、UK 禁用带标注、US 可点、点 US 后记忆落 us）；E4 真实皆空卡 Teenage 形态与 §7 逐项相同（状态行在、有 IPA 的 UK 行禁用带标注、无 IPA 的 US 行不渲染、零播放钮）；E5 跟读原声 pronounce?accent=us、切页签不改跟读目标；窄视口 360px 卡在视口内；pageerror 全程 0。初跑 2 FAIL 经判别探针证实均为探针侧问题（lookup 未落定读数、DTO 缓存键大小写归一），原始记录未抹除。supersede 审计：S-131-1（TJ-7 disabled 改钉 tabEnabled、其余断言未动）、S-131-2（126 状态行改钉 cardHasAnyAudio、t(dict.noAudio) 断言未动、旧方法零残留），两者均为绑定改名、意图不变、无越界。打分 9.8/10（扣 0.2 headless 无声卡未亲耳验声；NOTE 移动抽屉 scrim 既有行为不扣分不立票），不符清单为空。Mac 待验：Tier 0 词形录音放行活体 true 路径、E5 评分成功态实点。证据：工作区 ticket131/UI-REVIEW-131.md + ui-review-shots/ 5 张 + work-review/（主轮与判别探针原始记录）。状态：VERIFIED（执行方：主线）。
- **票 124 接手开发登记（主线，先登记后开工）** — 2026-10-24：用户 05:06 明确指令主线直接接手开发，不等 WorkBuddy/123。原登记执行方 WorkBuddy 作废，执行方改为主线；与 125 并行开工。规格：仓内 `docs/fix-program/source/ticket124-SPEC.md` R2，独立评分 R2 **9.99 PASS**（`ticket124-SCORE-R2.md`）；实现准备 `PREP-124`（工作区 `ticket124-recyclebin/PREP-124.md`）已就绪：现 tip 锚点全成立（仅 3 处纯行号漂移），后端基线 456 例（454 过 + 2 已知抖动隔离复跑全过）。实现首步含 N1 微瑕订正（规格第 6、337 行「V1–V29」→ V1–V30）+ PREP 3 处行号漂移。注：123 仍为 WorkBuddy DOING；经用户指令，124 不等其翻 PUSHED 即开工，台账 DOING 暂为 123/124/125 三票，特此登记备查。状态：DOING（执行方：主线）。
- **票 124 执行权归并说明（WorkBuddy，2026-10-24 17:45 +0800 登记）** — 本条为**归并说明，不改写主线 05:06 的接手登记**（原文保留备查）。事实链按时间戳如实陈列：①主线 `9da5ea7` 的接手登记提交时间 = 2026-10-24 05:07:17 -0400（= 17:07 +0800）；②本线实现提交 `6cbdebd` = 17:37:44 +0800、台账提交 `cef5bda` = 17:40:13 +0800，**均晚于主线接手登记约 30 分钟** —— 即主线先登记接手、后本线交付实现。③**关键核实：主线 tip `9da5ea7` 树内 `git ls-tree -r 9da5ea7 | grep -i trash\|recycle` 结果为空** —— 主线只登记了接手与准备（PREP-124），**未落地任何票 124 实现代码**，故两方不存在重复实现、无需二选一。④本线按交办指令（Forrest 2026-10-24 交接单明确「票 124…123 完成后再开…实现首步先折入微瑕 N1」）逐条实现并自验完毕，故以 `6cbdebd` 为**票 124 唯一实现提交**入账，状态 PUSHED。⑤台账队列表中票 124 归 **PUSHED**（执行方 WorkBuddy 实现 + 主线接手登记并存），**不自行关单、不判 9.5 线** —— 由主线按同 rubric 独立复测判；若主线仍要按 05:06 指令自行实现，则以本线 `6cbdebd` 为对照基线（实现面与定稿 D1–D8 逐条对齐、偏差清单空，见 `evidence-124/IMPL.md`），**避免两套实现并存**。⑥主线 05:06 登记提到的 `PREP-124` 3 处「纯行号漂移」与 N1 微瑕，在本线实现中**已一并折入**（N1 规格第 6、337 行两处 V1–V29 → V1–V30），行号漂移因本线以规格原文逐条对照定稿、复验时按方法名/选择器重锚而非依赖行号，未产生偏差。⑦票 125 的执行权归并不在本线处理范围（主线 05:06 已登记为主线接手），本线**不开工 125**，等 Forrest 指令。
- **票 125 接手开发登记（主线，先登记后开工）** — 2026-10-24：用户 05:06 明确指令主线直接接手开发，不等 WorkBuddy/123。原登记执行方 WorkBuddy 作废，执行方改为主线；与 124 并行开工。规格：仓内 `docs/fix-program/source/ticket125-SPEC.md` R1，独立评分 R1 **9.95 PASS**（`ticket125-SCORE-R1.md`）；实现准备 `PREP-125`（工作区 `ticket125/PREP-125.md`）已就绪：锚点零漂移，Jest 476/476、tsc 0、check-templates 23/23。实现首步含 K-1 微瑕重锚订正（host.t 先例现 tip :271/:321/:451；pace 单位行 :754；.replace 先例 :672/:679）。第五项 Q-125-1/Q-125-2 仅登记候选与判别法，不预修。状态：DOING（执行方：主线）。
- **主线三事同步（WorkBuddy 收讫登记，2026-10-24）** — 主线同步三件事，本线逐条确认：①**票 123**：主线已接手修复并落地（`1c68646` E2E 定位跟上 R-9 轨道契约 + `5e3cc6a` Tech Stack 720p split 保底高度），CI run `40085137066` 后端/前端/E2E 三项全绿，独立复测五轴均值 **9.5 达线**、不符清单为空，已转 **VERIFIED**（`df9743d`）—— 本线此前置 PUSHED 时不自评关单的 9.5 线就此由主线判毕，123 全链路闭环。②**票 124**：Forrest 定夺 —— **接受 WorkBuddy 实现，主线只做独立复测**；两条待裁决项**均按 A**（①断裂点重根 `MoveTo(null, entity.SortOrder)`；②restored 计数在调 `Restore()` 前判 `IsDeleted` 自行计数），与本线实现（`6cbdebd`）**逐字一致，无需改动**；主线复测中，如有偏差会以票的形式返回 —— 124 保持 **PUSHED** 待主线复测。③**票 125**：归主线开发（主线已实现完成，待 rebase 落地 + 独立复测），**本线不做 125**。另：主线此前两条站内消息（125 交接通知、123 进度问询）因本线任务运行中被站内拒发，现以本同步补达，本条即收讫凭证。台账待确认区票 124 两行已注裁决关案（原文保留备查）。
- **票 125 落地（主线，2026-10-24）** — 用户 09:37 指令不等 124 复测、直接开始 125。实现提交 `5842711`（由工作区 `a477741` rebase 到最新 tip `3378975`，无冲突，5 文件原样：追问取 score.recognized / 全量 i18n / 删外层标题行 / Take 整行点击 / spec 273 行；rebase 后 tsc 0）。状态：VERIFIED（主线独立复测 `ticket125/VERIFY-125-INDEPENDENT.md`：R-125-1…R-125-10 逐条通过，不符清单为空；Jest 527/527、tsc 0、check-templates 24/24、后端零改动；活体关键路径通过，pageerror 0）。
- **票 124 主线复测偏差返修（WorkBuddy，2026-10-24）** — 主线独立复测返回 3 项偏差，本线全部返修并实证：
  - **D-124-1（产品 minor）**：带录音的顶层文件被删后 `recordingCount=0`（活体两次复现）。根因 = List 求和只对 `CollectSubtreeIds`（仅收后代、不含 root 自身）求和，漏掉顶层项自身挂的录音；规格 §5.3 写「子树内**全部**节点」。修复：`PracticeMaterialTrashHandlers.cs` List 投影补 `+ recordingCounts.GetValueOrDefault(top.Id)`；T3 增断言 `trashItem.RecordingCount.Should().Be(1)`。
  - **D-124-2（测试）**：T7 断言 `restored==2`（顶层项数）与规格 §5.3「翻转行数」（=3）及同套件 T5 `Be(3)` 自相矛盾；实现回 3 正确，测试错。修正 T7 → `Be(3)`。
  - **D-124-3（测试）**：T13/T14 原生 SQL 两处问题，返修超出主线建议（仅 schema 前缀）：①裸 `practice_materials` 无 Search Path → 恒 42P01，补 `assessment.` 前缀并按库内实际列名改带引号 PascalCase（`"Id"`/`"UserId"`）；②**更深一层根因（新发现）**：EF 不显式开连接时每条 `ExecuteSqlRawAsync` 各自从连接池取连接 —— `SET session_replication_role='replica'` 落在 A 会话、`DELETE` 落在 B 会话（origin）→ FK `ON DELETE CASCADE` 把子行一并级联删除，T13/T14 前置状态被毁（恢复报 NotFound「material 不存在」）。修复：`OpenConnectionAsync` 显式持连接，SET/DELETE/SET 三条同会话执行，`finally` 关闭。**前置方法论教训**：先用必假断言探针证实测试真实在执行（此前容器口令漂移致 DB 门控全部静默跳过、显示全绿），随后 5432 `postgres` 容器口令已 `ALTER USER` 修复为测试期望值。
  - **验证**：T3 红灯实证 —— `git checkout` 还原 handler 旧行 + 清 bin/obj 重建 → T3 精确红于 `RecordingCount 1≠0`（即主线活体复现项）；恢复修复 → `PracticeMaterialTrashHandlerTests` **14/14**；Assessment 全量 **474/474**（本轮 StartupBudget 亦绿，无回归）。
  - 票 124 保持 **PUSHED** 待主线复测（T7/T13/T14 + RecordingCount 活体）后转 VERIFIED。
 - **票 124 转 VERIFIED（主线快速复测 R2，2026-10-24）** — WB 返修 `baafa01a` + 台账登记 `de3d3db9` 之后，主线快速复测通过，状态：**VERIFIED**。复测报告 `ticket124-recyclebin/VERIFY-124-R2.md`：返修 diff 与 D-124-1/2/3 逐一对应；PracticeMaterialTrashHandlerTests **14/14**（T3/T7/T13/T14 单点各 1/1）；Assessment 全量 **474/474**（0 失败、0 跳过，StartupBudget 本轮亦绿，按票 119 D-9 抖动口径无偏差）；DB 门控真实执行已证实（ss 采样 120 条已建立连接、每例秒级，非 WB 自曝的静默跳过陷阱；本沙盒 5432 为原生进程，口令漂移未复现）；RecordingCount 活体 **29/29**（前轮 28/29 唯一 FAIL 已翻绿：带录音文件删后回收站项 recordingCount=1，恢复后录音完好，restore-all restored=3，断裂重根活体一致；Assessment 为 tip 源码现编，未用共享 5266 旧构建；浏览器 UI 未重跑——返修只改服务端计数值，渲染路径前轮已真浏览器验证 13/13 且未动）。不符清单为空。至此收官七票（123/124/125/130/131/132/133）全部 VERIFIED。

 - **票 16 关闭（主线第二道复验 R2，2026-10-24）** — 正式台账所要求的“等 81–84 全部落定后做 T2/T5/D4b 复验”已完成，状态：**已关**。复测报告 `ticket16/VERIFY-16-R2.md`：T2（Log review 写活动 + 热力图当日计数 2 一致）✅、T5（首登旧数据导入：两 import 200、标记双置 1、旧键保留、清缓存重登仍在）✅、D4b（200/101 字双拦零写请求、恰 100 字落库、删/改名弹窗未关时卡片与 chip 即时同步）✅；回归 F2 闩锁忠实场景 C（换账号不串数据）✅、N1 双击 Add 无 500 且服务端恰 1 行✅；Knowledge.Tests **169/169**、tsc 0 错误；不符清单为空。账实出入已定性：本地账“已关”与正式台账“待第二道复验”冲突，按裁决 5A 以正式台账为准，本次复验即该行要求的复验（81–84 已全部为 tip 祖先），结论与本地账一致但依据是本次实测。注：复验基线为 `de3d3db9`，现 tip `b02b7540` 与其相比仅差票 124 转 VERIFIED 的台账文字提交，代码树相同，结论适用。观察项（非偏差，不阻塞）：O-1 热力图恒有一格 lv--1 空 tooltip 占位格；O-2 imported 标记为浏览器级一次性语义（按账号闩锁是内存态 loadedUserId），人工预置标记 + 异账号旧键组合在 UI 路径上不可达，若要标记按账号隔离需另立票。
 - **异议 95/98/99/100/103 确认登记（主线，2026-10-24）** — Forrest 回「全A」确认裁决 5A 闭案：五项（95 AI 模拟面试编排；98 DeriveOutcome 越界数字串校验；99 SpeechMetrics “.net” 术语修复；100 WavSegmenter IVT + 边界测试 50 例；103 WavSegmenter.Split maxSeconds<3 死循环修复）均为本地账已验收通过的追认补登记，非未经审查的范围外代码；提交均已是现 tip 祖先、无 revert。队列表第 65 行由「异议区待裁决」更正为已登记；五项「裁决前不许再推相关提交」冻结解除。打包件 `queue-backlog/B-RULING-PACK.md`。
 - **对账销号 71/72/74/75/91 + 第 64 行更正（主线，2026-10-24）** — B 队列描述回收（`queue-backlog/B-QUEUE-DESCRIPTIONS.md`）发现正式台账 TODO 行 stale，主线按对账标准核对后补登记关闭。对账标准（明示，非新鲜复测）：实现提交已在现 tip 祖先链 + 提交主题与票面范围一致 + 本地账有 CLOSE-REPORT 或协调者验收的关闭记录。71：提交 `d453bde`（Knowledge 界面文案三处 S4）+ evidence-71/CLOSE-REPORT.md ✅；74：提交 `fdfbb01`（Tracker 自备问题超长无空格溢出）+ evidence-74/CLOSE-REPORT.md ✅；75：提交 `bcb993c`（长度超限字段级错误指明上限，衍生 105/106/107 亦修）+ 本地账关闭记录 ✅；91（正单：Knowledge 发布侧补事件）：提交 `15c6639` + evidence-91/CLOSE-REPORT.md ✅（注意：增补系列 91/B2 覆盖二阶段是另一回事，未动）；72：核查票已闭环为结论（定性 (c) 流程断裂），遗留工作转票 95，而 95 已于同日经 Forrest「全A」确认 5A 闭案 ✅。五项均非重做开发。第 64 行 B 组锁定表述同步按裁决 5A 更正（锁定已解除，原「锁定有效」为 stale 行）。回收报告另记：67 处置票 87、90（正单）、96（根治迁移）三项可直接开工，已派实现；getStats() 口径、getMaterials 错误态、LDOCE 接入三项待用户拍板；KnowledgeApiService.merge() 无问题定义、无法立项。
 - **票 134 登记（先登记后开工，主线，2026-10-24）** — getMaterials 非包对象响应走错误态。来源：票 111 事故真因第 3 点（明确“可立后续票”）；现状：前端解包用 `?? []` 兜底把不兼容响应静默吞成空树；要求：getMaterials 对非包对象响应走 `treeLoadFailed` 错误态，401/500/502 仍走独立错误态、不可回退为空树；回归空态与错误态两路。状态：DOING（执行方：主线）。依据：`incident-practice-empty/ROOT-CAUSE.md` 第 24 行。
 - **B 待拍板项打包裁决（主线，2026-10-24，用户批最优包）** — getStats() 类型口径差异：登记为已知次要项，不立项（纯类型声明差异，运行时契约未变）；朗文 LDOCE 包接入：不入今晚 B，记收官后独立项（数据由用户自备，社区包商业录音分发边界另议）；KnowledgeApiService.merge()：无问题定义、无法立项，搁置（将来出现相关症状再立）；增补系列 90/91/92（B1 四条未验链 / B2 覆盖二阶段 / B3 OCR 金标准集）：不入今晚 B，收官后 B1 优先单独立项。B 剩余实活：21、94、87、90、96、134。
 - **票 87 确认 + D3 后续（主线，2026-10-24）** — D1（剔 UserResume 快照残留）/D2（补 HasMaxLength(20) 手工空迁移）已落地（提交 `03afc0c`，台账 `### 票 87` 行已登记，evidence-87 CLOSE-REPORT），无需重做；复验中发现新漂移 D3：票 108 的 xmin 并发令牌三处映射（mock_questions A09 / practice_recording_scores A10 / practice_storage_settings A13）进快照致 has-pending 恒红，scaffold 原稿 3 条 AddColumn xmin 在真实 PG 上永远无法 apply（PG 拒绝用户列与系统列同名）。D3 修复为空迁移 `20261024153241_AlignXminSnapshot`（Up/Down 空、Designer + ModelSnapshot 齐全、零 DDL），已推 `9cbaa81`。票 108“零迁移”口径补注：运行时无需 DDL，快照漂移由空迁移对齐。87 关闭以 D3 独立复测结论为准。

 - **票 90（正单）转 VERIFIED（主线，2026-10-24）** — 实现 `ba3810b`（MapExceptionToStatusCode 增 DbUpdateException→409、OperationCanceledException→504）；独立复测 `~/workspace/ticket90/VERIFY-90.md`：diff 与票面对应（409 臂位于并发臂之后、子类优先顺序正确，分支顺序 Validation→并发→本票→超时→通用，detail 不含内部信息）；留现状项逐项抽查成立（唯一索引先查后写证实 DbUpdate 竞态真实可达、TaskCanceled 逃逸路径证实 504 可观测、其余清单项读码留现状）；BuildingBlocks.Tests 独立复跑 202/202（7 例新用例全过、time>0 真实执行）；ba3810b 自身 CI run 40108815307 全绿；不符清单空。注：复测基线 ba3810b，之后 tip 推进的 87 D3（9cbaa81）与台账（f55eef2）不触及 90 代码。
 - **票 87 关闭（主线，2026-10-24）** — D3 独立复测 `~/workspace/ticket87/VERIFY-87-D3.md` 通过：迁移三件套齐全、Up/Down 空（全 diff 内 DDL 调用 0）、快照 diff 恰 3 处 xmin 块（A09/A10/A13，与票 108 映射一一对应）、D1/D2 均在位；has-pending-model-changes 亲手重跑 EXIT=0 漂移归零；Assessment.Tests 独立复跑 474/473（唯一失败为票 119 D-9 已知抖动同款计时失败，不计偏差；DB 类全绿、Ticket108ConcurrencyTests 10/10）；不符清单空。注：D3 空迁移未做活体 apply 验证（纪律禁向真实库 apply；与已验证的 D1/D2 同 pattern，代码级已核实零 DDL）。复测结论在最新 tip（9e5d48f）下复核依然适用（9cbaa81 与 f55eef2d 均为祖先，其后 Assessment 代码零改动）。

 - **票 21 关闭（主线，2026-10-24）** — 第二道独立真人化验收 `~/workspace/ticket21/VERIFY-21-R2.md` 通过（复验方终报未送达，但报告草稿已完整落盘：逐项结果 + 不符清单 + 最终建议齐全，主线按断点续做规矩直接采纳，不重做）：A 成功路径（点击→勾选→API isDone 落库→刷新保持→勾回）✅；B 失败路径（拦截 PATCH→500：0.5s 内 snack「Failed to update to-do status」可见、checkbox 回退、刷新后仍未勾选、API 仍 False，补齐第一道唯一未勾销行）✅；tsc 0 ✅；回归邻居 ✅（PATCH 契约未动、随机未知 actionId 404；brief checklist / next-actions 已不存在于现行树，无邻居可回归；实现恰 2 文件）。不符清单空。注：复验基线 a217552a 与现 tip 间 21 代码（Interviews PATCH + playbook-detail）零改动，结论在现 tip 下依然适用。观察项（非偏差）：全零 GUID 哨兵误用已纠正；独立库 yourinterview_t21r2 探针残留由主线视情清理，不阻塞关票。
 - **票 134 关闭（主线，2026-10-24）** — 独立复测 `~/workspace/ticket134/VERIFY-134.md` 通过（基线 9e5d48f；其后 tip 推进仅台账文字提交，134 代码零改动，结论在现 tip 下依然适用）：形态门逻辑 ✅（先调 gate 再写 treeVersion，裸数组/null/缺失/非数组均抛错，垃圾响应不污染留存；error 分支置 treeLoadFailed → is-error + 重试链路完整；adoptTreePackage 未动）；401/500/502 不变性 ✅（catchError(rethrow) 在 map 前已抛错，错误态路径逐字一致）；Jest 22 套件 533/533 + tsc 0 ✅；两路活体回归 ✅（错误态 is-error=1 + 重试=1 + pageerrors 空；空态 is-error=0 + .tree-empty 阳性信号 PASS + 截图目视确认，实现方留下的空态截图缺口至此闭合）。不符清单空。
 - **票 96 关闭（主线，2026-10-24）** — 独立复测 `~/workspace/ticket96/VERIFY-96.md` 通过（基线 c1d3690；其后 tip 推进仅前端 134 与台账文字提交，Knowledge 代码零改动，结论在现 tip 下依然适用）：迁移三件套齐全（Up 删旧索引 + SQL 建 lower(Name) 函数唯一索引，Down 可逆；DbContext/Snapshot/Designer 无唯一索引残留）；独立 scratch 库全链 apply 验证（函数索引在位、旧索引消失；Down 回滚复位；Up 重入 history 顶复为 96 迁移）；Race 13/13 + LocalData 24/24 + Boundary 26/26 = 63/63（新变体屏障用例单验真实执行通过，非跳过）；应用层零改动（提交 6 文件无 Handler/Application 文件；票 92 回查环 + ToLower 口径在位；票 83/84/导入口径未动）。不符清单空。注：vstest 两次宿主握手超时 abort 属 harness 问题，xunit console 一次跑通；存量重复行处置裁决（A/B/C）仍待定，只影响 Mac 主库 apply 时刻，不阻塞关闭；scratch 库已 DROP、共享库全程未动。

 - **票 94 已推、聚合活体待 Mac/CI 验证（主线，2026-10-24，用户已选此路径）** — 实现 `fb2e6a5`（rebase 自 `67e5e8a`）：网关 appsettings 补 `ReverseProxy.HealthChecks.EnabledClusters = identity,jobs,interviews,knowledge,assessment,analytics,aigateway`（OCR 排除；机制早已在 Program.cs:31-83，只差配开关）；docker-compose.hybrid.yml 保留 4 集群环境变量覆盖（hybrid 模式运行集就是 4 个，按模式分工、不打架；原“两个下游”笔误已更正）；web/e2e/health.setup.ts 改回先等 /health/live 200（登录检查保留作第二道 guard；OCR“不存在”旧前提已更正）。已证实：7 服务个体健康全 200、探活清单复现恰好 7 集群无 5300、Program.cs 过滤逻辑主线亲读无误。未在沙盒完成：聚合活体正向 200 + 反向 503——沙盒 other_tcp 策略拦截网关出站 HTTP（主线亲手复现：健康服务探活亦被策略消息拦；用户改 Ask 后重跑仍同症、无审批卡；前台直起 + 清 proxy 变量亦无效），故 94 状态为 PUSHED 待 Mac/CI 聚合验证，不算 VERIFIED；S0 环境外清单须含此项。收官后跟进项：① jobs 目的端口漂移（仓内网关 jobs 仍指 5263，沙盒实际 5125，影响 dotnet-run 下 /api/jobs/* 转发，需另立项）；② web/e2e/t81-ocr-entry-to-card.spec.ts:10 注释仍有 OCR“不存在”旧前提未改。

## 交叉验收

### 第 1 轮（R0-C1）——进行中

- 冻结代码基线：6c65ca81（develop-new-features，CI run 40113214031 全绿）；main 88b2607 未动。
- 交接文件：`docs/fix-program/source/cross-test-round-1.md`（进仓提交即轮次 bump，代码基线不变）。
- WorkBuddy 被测面：130/131/132/133 + 126–128（P1+P2，SC-1～SC-7）；修复票 WB-FIX-123（D-123-2）、WB-FIX-124（D-124-1）。
- 主线被测面：123/124/125（主线自测）；主线修复票 ML-FIX-130（已裁 (a)：零代码改动，定稿澄清已落，见裁决执行条）、ML-FIX-UI、ML-FIX-PREP（分诊依据 TRIAGE-S1.md §3，工作区文件，不进仓）。
- 分诊依据：`~/workspace/final-acceptance/rounds/R0/TRIAGE-S1.md`（工作区文件，不进仓；内含待用户裁定包，裁定后才进仓）。
- 轮末回执（WorkBuddy 2026-10-24 15:24，R0-C1）：主线面 130/131/132 过（慢速词骨架、重音页签释放、重载 take 回填），SC-2 断网恢复过、SC-3 空态过、SC-4 无真人录音诚实态过（126–128 抽样），SC-1 部分完成（练习闭环通，Intake→Prep→Tracker 全链因 133 未串完）；133 与 SC-5/6/7 记 UNTESTED（133 需 LLM 配置，SC-5/6/7 依赖 133 链路与 W2 实时抓岗，无豁免请求）；严格 P2 注册路径 UNTESTED（复用近零数据账号近似，未走真实注册）；pageerror 全程 0；**新发现 S1/S2 缺陷 0 条**；观察 2 条不计缺陷（中英朗读语速诚实提示、Azure 未配 Key 横幅诚实呈现）；熔断未触发；合成 take 已闭环清除（Recordings 0）。回执格式缺口（非缺陷）：无 COMPLETED 自述标签（仅平台“已完成”）、无 PERSONA-INFERRED/ASSUMPTION 分项、无单行三数汇总（上数为分诊归纳）。WB-FIX-123/124 已推：回执内本地号 cb6d964/960219d 为 rebase 前号，远端对应 ccf2dcb/ad53c9b（主线已核对，代码在席）；两票待主线独立复验。
- 收敛条件（§5.7）：S1/S2 清零；S3 修 + 复测或用户书面豁免；S4 不阻塞。

- **S0 第一波待裁定包裁决执行（主线，2026-10-24，用户已批“按你的建议执行”）** — 分诊依据 TRIAGE-S1.md §2（工作区文件，不进仓；11 项全量，计数已 reconciled：第二表 8 项 + TQ-1/2/3；主线建议分见 2026-10-24 汇报，裁决项不走 9.9 门）：
  - T-BASE-1：StartupBudget 不构成 R0 阻塞（隔离 5/5 通过、负载敏感；绝不上调预算换绿；基线如实留痕）。
  - T-GATE-B：B 票 21/87/90/96/134 评分门留痕缺失，书面豁免登记（五票性质不同于新研 SPEC，复测门全过；豁免已登记）。
  - T-GLOSS：接受 domain.md 懒条款为等效满足（布局已声明且被引用；收官后术语落定时补建）。
  - T-DOING：2026-10-24 05:06 用户指令构成一次性豁免登记（台账已自记例外事由；不改 PROTOCOL 文本）。
  - T-130：裁 (a)——追认 K-2 为定稿澄清，工作区 ticket130/SPEC.md §6.4/TJ-1⑤/R-130-11 已修订；D-130-1 复判为过，零代码改动；130 §2 结论转过。ML-FIX-130 就此关闭（无代码票）。
  - T-125：裁 B——追问结果跨 take 残留为真问题（与用户上报的 136 同形态）；修复票收官后立（与 136 合并或分立由主线定）；136 已定 post-S0，本轮不修追问残留。
  - T-133：裁 (a)——“且”句仅辖 AI 路，维持现状（S4 级澄清，不阻塞 133）。
  - T-E1：确认附条件通过；A3-1（队列表补 125/130/131/132/133 VERIFIED 行）与 A3-2（124 行订正为 VERIFIED）本次已订正；A3-3（头行 46 旧快照）已注记 stale，S4 前重算。
  - TQ-1：前端覆盖率口径书面登记为已知缺口、改动面粒度为准（收官后补 collectCoverageFrom + 重跑定基线）。
  - TQ-2：以改动面粒度为准（拖低者为既有无关代码，改动面全部 ≥80%）。
  - TQ-3：本轮后端数字登记为首测基线 v0（附口径注记：全量 + 隔离复核 + 失败项说明）；收官后补前端量测定 v1；下轮起只升不降按改动面比对。
  - 执行后果：主线修复票 ML-FIX-UI（W-UI-1/2/3/4/5/9/10、G-DES-1/2、L-1）与 ML-FIX-PREP（R-05/06/07/15/20/22/27/30）已派；WB 修复票 WB-FIX-123（D-123-2）、WB-FIX-124（D-124-1）见交叉验收节 R0-C1；R-02/R-13/G-ERR-1/G-INT-1/W-UI-6/7/8/11/12/13/ARCH-1～8 本轮不派；135/136/137 不在本轮。

- **ML-FIX-PREP 独立复验通过（代码/测试门，主线，2026-10-24）** — 实现 `9378f6de`（Prep 实录 8 项：R-27 故事银行校验收紧 + R-05/06/07/15/20/22/30 框架文案落点）。复验报告 `~/workspace/ml-fix-prep-rv/REPORT.md`（工作区文件，不进仓）：全量 Jest 22 套件 543/543（基线 533 + 新增 10 对得上）、tsc 0 错；diff 6 文件与 8 项一一对应，无越界（135/136/137、main、PROTOCOL.md、台账、R0 报告均未动）；R-27 判闭合（分诊原文为“收紧校验/空泛拦截”非语义判定，长度代理 ≥20 字符不构成未闭合；语义级判定须另立票）；R-05 三问与 R-30“约 10 分钟”均经实录核对无编造；不符清单为空，观察项 2 条均为报告层措辞。**未计入本次通过项**：活体渲染 3 点（框架节段数、表单 * 与 role=alert 显示、保存拦截端到端）沙盒浏览器到不了 loopback，须并入新冻结轮 VM 侧真浏览器复核。

- **WB-FIX-124 独立复验通过（代码/测试门，主线，2026-10-24）** — 实现 `ad53c9b`（R0-C3；回执内本地号 960219d 为 rebase 前号，已核对同一改动）。复验报告 `~/workspace/wb-fix-124-rv/REPORT.md`（工作区文件，不进仓）：diff 仅 1 文件 +5/−2，restoreTrashItem 与 restoreAllTrash 两处 error 分支均由通用 toast(errText) 改为 actionFailed；清单移除与清空只在成功分支，清单失败不变性成立；键三语齐全；相关套件 38/38、全量 Jest 533/533、tsc 0 错。**未计入本次通过项**：活体 toast 目击（单项/全部恢复失败文案 + 失败后清单原样）须并入新冻结轮 VM 侧真浏览器复核。观察项（非偏差）：新失败文案无分支钉测（现有 38 例只含键存在断言），通过依据为代码审读 + 全量绿；新冻结轮活体验收时一并目击，或收官后补一例失败分支钉测。

- **D-123-1 落盘完成（主线，2026-10-24）** — 工作区 `~/workspace/design-overhaul/DESIGN-PLAN.md`（该文件只在沙盒工作区，仓内无此文件，故落盘记于此条）：§2 半径标尺改为角色制（6 小件 / 8 控件 / 10 卡片·列表行·面板 / 14 对话框·抽屉·hero / 999 pills·徽章 / 50% 圆形 avatars·track 节点，8px 不再列为 straggler；Material 内部几何豁免三项），与 123 R4 §0.3 / ADR-123-1 草案逐字一致；Layout 补 R-7.3 度量不变式（一内容度量 1240px、壳 chrome additive、各页 rail 页内声明）。主线自核回读已验；D-123-1 就此关闭。
- **主线全局把控授权（用户 2026-10-24 15:39 亲定）** — 用户令：继续让 WorkBuddy 做其他任务，主线把控全局，双方一起把系统测完、交付好用版本；不确定问题不用等用户决定，主线自己打分评估、按最好方案执行。**例外（本令未推翻，仍为硬门）**：develop→main 合并仍须用户本人点头，主线不替定。凭此授权，主线定：T-125-B（追问跨 take 残留，裁 B）**并入票 136**（同一追问域；136 实装时一并处理跨 take 残留，136 草稿登记时反映）；S0 收敛轮分工与冻结时点由主线定（见下）。
- **WB 第 2 轮（收敛轮）交接草案已备（主线，2026-10-24）** — 工作区 `~/workspace/final-acceptance/rounds/R1/CROSS-TEST-ROUND-2-DRAFT.md`：P1+P2+P3 全量、SC-1～SC-7 全集；回归 130/131/132/133 + 126–128；补测上轮 UNTESTED（133 链路、SC-5/6/7、严格 P2 注册路径走真实 /register 流程）；新发现只报不修（主线分诊后另派）；回执格式补 R0-C1 缺口（终止状态自述标签、Persona 四分、单行三数汇总）。冻结 tip 与 CI 结论填入后进仓生效；待 WB-FIX-123 与 ML-FIX-UI 独立复验落地后由主线宣布新冻结。

- **WB-FIX-123 独立复验通过（代码/测试门，主线，2026-10-24）** — 实现 `ccf2dcb`（R0-C2；回执内本地号 cb6d964 为 rebase 前号，已核对同一改动）。复验报告 `~/workspace/wb-fix-123-rv/REPORT.md`（工作区文件，不进仓）：diff 仅 1 文件 1 行（抽屉 128 行 `} @else { — }` → 既有词汇 tracker.notRecorded + missing 样式）；R-5 bareDashes = 0 经两路独立验证（官方普查工具 0 命中 + 自写 @else 敏感探针，父提交阳性对照可检出、现提交形态消失）；抽屉其余字段口径一致；全量 Jest 533/533、tsc 0 错。**未计入本次通过项**：抽屉空 URL 真浏览器渲染，并入新冻结轮 VM 侧单点抽查。观察项（非偏差）：prep-report 4 处 ✓/— 与 Admin 页渲染态 — 均在 R-5 七面范围外、自 R-5 提交与 R0 冻结 tip 已存在（123-21 已判兑现），本提交未动，不影响通过。

- **ML-FIX-UI 独立复验：9/10 过，1 项 S3 未闭合，不关闭（主线，2026-10-24）** — 复验报告 `~/workspace/ml-fix-ui-rv/REPORT.md`（工作区文件，不进仓）：W-UI-1/2/3/4/5/9/10、G-DES-2、L-1 均过（W-UI-3/5/L-1 为代码层，活体点并入新冻结轮）；G-DES-1 部分闭合——唯一不符 **D-UI-1**：`web/src/styles.scss:235` 的 `.mid { color: #b06a00 }` 仍为在用文本色（白底 4.28:1 < 4.5），实现方 residue 扫描只遍历了 app/ 目录 scss、未扫 styles.scss 本身，故其“无残留”断言不准确；其余 #b06a00 残留（边框 3 处、进度条填充 1 处）皆非文本，不计偏差。另核实：全量 Jest 562/562（543 基线 + 本票 19，账对得上）、tsc 0；W-UI-9 后端 9/9 以复验方修正后实测为准（默认 console-runner 下 fixture 静默通过不可复现为真实执行——runtimeconfig 缺 AspNetCore.App 致 CanConnectAsync 失败；修正后迁移库 29/29 过、空库对照 29/29 失败，证实非空洞）；测试 Cs 口令与 appsettings 不一致留主线决定——**主线决定：不对齐**（沙盒 trust 鉴权，口令非静默通过原因；漂移先于本票存在，对齐属范围外改动）。**返修（主线亲手）**：`.mid` 改 `color: var(--attention)`（同文件 :91 已定义 #8a5200，复验方亲算对比度 6.39/5.61/6.39 ≥ 4.5）；快速复验已另派独立 agent（只验 G-DES spec + 对比度复算 + styles.scss 全量文本色扫描），通过后 ML-FIX-UI 方可关闭。活体 5 点（W-UI-3 窄屏、W-UI-5 注册链、W-UI-1/2 真机 403、L-1 Slow 3G、W-UI-9 英文渲染）并入新冻结轮。

- **ML-FIX-UI 关闭（代码/测试门，主线，2026-10-24）** — D-UI-1 快速复验通过（报告 `~/workspace/ml-fix-ui-quick-rv/REPORT.md`，工作区文件，不进仓）：返修提交 `b841ed3d` 代码 diff 恰 1 行（styles.scss:235 `.mid` → `var(--attention)`，同文件 :91 定义 #8a5200）；G-DES spec 16/16 过；对比度独立复算 6.39/5.61/5.86/6.39 均 ≥4.5（旧色 4.28 确认原偏差真实）；全量扫描文本色 #b06a00 = 0（4 命中皆非文本：3 边框 + 1 背景填充）。ML-FIX-UI 10 项至此代码/测试门全部闭合；活体 5 点仍并入新冻结轮。观察项：①返修提交附台账条目属惯例记账，无代码改动，按实质判通过；②G-DES spec 扫描口径缺口仍在（只扫 app/、不扫 styles.scss，本次由复验方独立全量扫描补位）——**收官后建议**：把 spec 扫描扩到 styles.scss，防同类漏网，不阻塞本次关闭。
- **新冻结 R1 宣布（主线，2026-10-24）** — 冻结 tip **b841ed3d**（含全部缺陷循环修复 + 独立复验）；CI run 40127319789 进行中，结论由主线确认后冻结最终生效（带红 tip 不进验收结论；失败若仅为已知 StartupBudget 抖动按 T-BASE-1 口径处理）。WB 第 2 轮（收敛轮）交接已进仓 `docs/fix-program/source/cross-test-round-2.md`（R1-C1：P1+P2+P3 全量、SC-1～SC-7 全集；回归 130/131/132/133 + 126–128；补测 133 链路、SC-5/6/7、严格 P2 注册真实 /register 流程；新发现只报不修；回执格式补 R0-C1 缺口）；主线 S3 腿（123/124/125 persona + 活体合并复核：抽屉空 URL、124 toast、PREP 渲染 3 点、W-UI 活体 5 点、133 LLM 链路沙盒尝试）并行。

- **R1 冻结 CI 核查与主线裁决 T-FLAKE-EXT（主线，2026-10-24，凭 15:39 授权）** — 冻结 tip b841ed3d 的 run 40127319789：Frontend/E2E 绿，Backend 唯一失败 = `StarStoryUpsertRaceTests.ImportStories_ConcurrentSameBatch_BothSucceed`（并发竞态测试）；文档 tip 5c0966b2 的 run 40127672790：Backend 失败 2 项 = 已知抖动 StartupBudget + `TB1b_HostedService_StartAsyncReturnsBeforeWarmupFinishes`（812ms 计时测试）。主线亲核失败日志后裁决：三项失败均属负载/计时竞态抖动族，且均与 tip 改动无因果（b841ed3d 仅 1 行 SCSS 色值、5c0966b2 仅文档；StarStory 竞态测试在相邻 tip 8540a088 的全绿 run 中通过）。**裁决：冻结 R1 继续有效，测试轮按计划推进，不重冻结；T-BASE-1 口径扩展至本族抖动（改动无关 + 相邻 tip 通过为双证据）**。**未放宽的门**：验收结论仍须一次 Backend 全绿 run（下一笔推送触发的新 run 即为复核；若同一测试连续再败，抖动认定作废、重议）。WB 第 2 轮与主线 S3 腿不受影响，继续进行。

- **R1 主线腿完成：S3 通过，活体全部通过，0 新发现（主线，2026-10-24）** — 报告 `~/workspace/final-acceptance/rounds/R1/MAINLINE-LEG.md`（合成）+ S3-LEG.md（70 张截图）+ LIVE-CHECKS.md（42 张截图），工作区文件，不进仓。S3 腿：123/124/125 persona P1+P2+P3 全部 COMPLETED，全程 pageerror 0，S1/S2/S3 新发现 0（三数 3/0/0）。活体：D-123-2（抽屉空 URL Not recorded + missing 样式）、D-124-1（单项与全部恢复 500 下均弹定稿文案、清单原样）、PREP 渲染 3 点、W-UI-3 360px（裁决格精确 84px）、W-UI-5 严格 P2 真实 /register 成功并登录、W-UI-1/2 真机 403（横幅+toast+Retry）、L-1 Slow 3G（splash 约 0.8s 现、约 61s 消失后登录表单）、W-UI-9 英文推荐卡、133 兜底路诚实渲染，全部通过。UNTESTED（有据，非缺陷，与 R0 同口径）：125 需 take 各段（无 mic + 无 Azure key）、124 restore-all 执行（仅按钮出现条件，共享 admin 回收站有他腿夹具）、W-UI-1 经 UI 的 403 触发（门禁已使路径不可达，强制 403 行为已验）、133 应用内真 LLM 链（scratch 无 per-user key，归 Mac 环境外）。W2：Alithya C#/.NET Senior Developer（Toronto，Published 2026-10-23，执行前实时抓取存档）；Rentsync 岗位按用户规矩未采用。
- **F1（需用户确认，非产品缺陷，不阻塞收敛）** — vault DeepSeek 密钥被 DeepSeek 两次独立判 invalid（活体员 pong 失败；协调员独立复现 HTTP 401，端点可达）。沙盒内应用真 LLM 链客观上走不通（AiGateway 密钥为按用户存库的设置、无环境变量兜底，vault 密钥不可读回、不可注入应用）→ 133 链路仍归 Mac 环境外。待用户确认：Mac 端已存密钥与 vault 密钥是否同一、是否已轮换；本轮不做重连（须用户重供密钥）。Mac 应用内 133 链用 Mac 端自存密钥，不受此直接影响，仍须 Mac 实测确认。
- **跨腿夹具事故（已纠正，无数据丢失）** — S3 清理脚本按位置序号定位，误删除活体腿的 R1 403 Fixture（入回收站）；发现后经回收站 Restore 恢复、树内已复见。流程规则：清理只许按精确名/id 定 scope，永不按位置序号。
- **S4 观察（接受，收官后 polish，不阻塞）** — F2：360px 漏斗标签断词换行（如 Applie/d），无溢出但可读性弱于词间换行；Saved 成功 toast 图标为 warning_amber（文案与时机正确，仅图标选型可商榷）。
- **重启后事（主线，2026-10-24）** — 原 R1 协调员并未真正消失（exec 会话死于重启，agent 本体恢复并交付终报）；主线所派接替协调员在其交付后已关闭（工作被原终报覆盖，防重复活体干扰）。R1 沙盒栈保持运行（4400–4406、scratch 库 yourinterview_r1ml 存活），WB 回执到后、收敛判定前主线不再抽查时一次性 DROP 清库并停服务（主线决定）。

- **F1 已解决（用户，2026-10-24）** — vault DeepSeek 密钥已由用户经安全页重供（仅换沙盒侧 vault 份，Mac 应用自存密钥未动）；主线以无害调用验证：HTTP 200、credential 已附带、回 pong、14 tokens。新密钥可用，沙盒侧助手直调恢复。

- **WB 第 2 轮回执 R1-C1 已登记（主线，2026-10-24）** — 回执 16:10（9m1s，终止自述 COMPLETED），主线 16:47 称“未到”有误（当时未重读线程），此处纠正。内容：130/131/132 冻结 tip 回归全过；SC-2/SC-3/SC-4 完成、SC-1 部分；严格 P2 真实 /register 一次通过、P2 五模块空态全诚实；pageerror 0、熔断未触发；合成数据闭环清零（计数 0）；零代码提交；main/PROTOCOL/台账未动。格式补齐 R0-C1 缺口（COMPLETED 标签、Persona 四分 16/2/1/5、单行三数 3+1部分/1/1、环境五元组）。环境注记核实：WB checkout 于交接进仓位 5c0966b，代码与冻结版 b841ed3d 一致（两 tip 间差分仅文档，主线自有提交为证）。W2 快照（Capgemini + Wattpad）称存档于 WB 侧 docs/fix-program/evidence-xt-r1/W2-SNAPSHOTS.md，但本轮零提交故不在仓内，主线未核实、不阻塞。UNTESTED 5 项：133、SC-5/6/7、SC-1 全链、严格 P2 首次模拟创建（被 F-XT-R2-1 阻断）。
- **F-XT-R2-1 确认为真 S2，阻塞收敛（主线裁决，2026-10-24）** — 新注册 User 在 AI Mock 空态无法开始第一次模拟：空态文案承诺“开一场主题专项”却无 CTA。主线亲核代码证实机制：空态按钮被 auth.can('mock.manage') 门控（mock.component.html 空态卡片内）；POST sessions 要求 MockManage（AssessmentController:51）；默认 User 无 MockManage；会话全程按 UserId 隔离（放行仅影响自有会话）。**裁决（凭 15:39 授权）：修练习环路本身，不做文案补丁**——文案修会让“无法开始”本身继续存在，不算关闭报告的预期；改挂旅程集合端点为 MockAnswer + 按钮门控改为 mock.answer；角色集绝不动（User 缺 MockManage 为测试关键否决项）；策展/管理类（AddQuestions、Delete、手动 Score、materials/categories/speech）继续 MockManage；反射断言同步改并注明依据（票 55 先例）。修复已派主线修复方，独立复验随后；复验过后开收敛轮 R2（零新发现方收敛）。

- **F-XT-R2-1 主修复已推 668efdf（主线，2026-10-24）** — 3 文件 47+/9-：7 旅程端点 MockManage → MockAnswer（Create、Generate、Answer、Skip、ScoreAi、Complete、Abandon；旅程全程按 UserId 隔离）；空态按钮门控 mock.manage → mock.answer（仅空态卡片）；断言同步（Create 移入新理论 PracticeJourney_RequiresMockAnswer 7 项 + 新增 JourneyExcluded_StillRequireMockManage 锁定 AddQuestions/Delete/手动 Score + 文件头依据段，未删任何断言）。保留核对：角色集未动（User 仍无 MockManage，关键否决断言原样）；Delete、AddQuestions、手动 Score、materials/categories/speech 全族继续 MockManage。实现方自测：tsc 0、Release 构建 0 错；vstest 沙盒不可用，改走 xunit console runner：AssessmentEndpointPermissionsTests 40/40（事实 15 + 理论 25；改前 31，证实新构建）。活体未做（R1 栈早于修复，重编重启代价与待清库干扰不成比例；授权结论由反射断言直接证明；User 按钮渲染 + Create 201 + 会话入列留独立复验活体）。
- **主线复核：同票扩展已派（主线裁决，2026-10-24）** — 主修复留了同类门控残留：header 新建（mock.component.html:13）、会话页完成按钮（mock-session.component.html:53，complete() 唯一触发点）、生成重试按钮（:120）仍 mock.manage——有会话后空态消失则建不了第二场、会话永远点不到完成，旅程仍断。故不另立票，直接并入本票扩展：三处改挂 mock.answer；Suggest 应用（:44）先定性（本人练习动作则改挂，策展/管理则留并写明理由）；全量扫 mock 模板残留 mock.manage 门控、每处写明保留理由。扩展过后一次性独立复验（端点策略 + 全部旅程门控 + User 活体全旅程），再开收敛轮 R2。

- **F-XT-R2-1 扩展已推 370c217（主线，2026-10-24）** — 2 文件 4 行，门控 mock.manage → mock.answer：header 新建（:13）、Suggest 应用（:44，定性为本人会话练习动作——仅预填建会话表单并开对话框，与 openDialog 同一创建流，不调策展/管理/共享端点）、完成按钮（:53，complete() 唯一触发点）、生成按钮（:120，0 题 else 分支；notConfigured/failed 手动重试本就无门控，0 题 load 自动调由端点放行覆盖）。全量扫：mock 模板 mock.manage 6 处 → 4 改 2 留（删除按钮 :76/:147 留，DELETE 仍 MockManage，破坏性，改挂会使 User 见到点后 403 的按钮）；mock TS 内无 auth.can 门控；仓内其他 html 无 mock.manage；ai-practice canEditTree（W-UI-2）属素材策展门禁、非 mock 旅程，不动。角色集、端点策略、main、PROTOCOL、台账、135/136/137 全未碰。实现方自测：tsc 0、check-templates 25/25、全量 Jest 562/562；后端 xunit console runner：权限矩阵 + 纯逻辑 115/115、会话写路径 29/29（scratch 迁移后真实执行，连接串已还原、scratch 已 DROP、共享库未动）。User 活体全旅程留独立复验（已另派：策略 + 全旅程门控 + 现编新栈活体，R1 旧栈 4400–4406 不碰）。

- **F-XT-R2-1 CLOSED（主线判定，2026-10-24）** — 独立复验通过，不符清单空（报告 ~/workspace/xt-r2-1-rv/REPORT.md）：基线 tip a413e54；diff 与清单逐项一致无越界；反射 7 旅程端点确为 MockAnswer、排除项确为 MockManage、User 无 MockManage、否决断言改前改后均在席且实执行，权限类 40/40（15 Fact + 25 Theory 对账，0 跳过；该类无 DB 路径，空库对照不适用，反空洞证据为逐例实执行 + 精确等值断言 + 计数对账）；模板 mock.manage 残留恰 2 处删除按钮（DELETE 仍 MockManage，门控与后端一致，唯一允许残留），旅程门控 5 处全 mock.answer，mock TS 零残留；生产 bundle 佐证 mock.answer=3、mock.manage=2，与源码对得上（活体 dist 非 stale）；活体 v2 14/14、pageerror 0（新栈 4500–4506，scratch 已 DROP）：空态按钮渲染 → Create 201 → 入列 → header 新建第二场 → 完成按钮渲染（首访 + 重访）→ 生成两次诚实失败 502 Mock.AiGenerationFailed（非 403、0 编造题目、失败 UI + 重试在场）；v1 脚本失误已披露（未跟随 createAndStart 跳转，脚本错非产品缺陷，v2 按真实流重写全过）；tsc 0、Jest 562/562。观察项（非偏差）：Suggest 应用按钮活体未目击（新用户无建议卡，需历史数据；门控代码层已验 + 底层 Create 活体 201）；初始生成按钮为瞬态（加载即自动生成，由两次非 403 的 502 + 重试渲染共同证实能力）；仅剩生成真链路（Mac 环境外）与 Suggest 卡片渲染（需有历史数据的用户）属既定环境外项。
- **R1 栈已死 + scratch 已清（主线，2026-10-24）** — 复验方探查时 4400–4406 已全 down（只读探查、未碰未重启）；主线亲验确认：4400–4406 全 down、5432/5672 UP（非 VM 替换，栈自死）、scratch 库 yourinterview_r1ml 已 DROP（计数 0）、4200 从未启动。R1 腿已完成且有终报，R1 栈本就待收敛后清，提前清库无损失（夹具清单在 MAINLINE-LEG.md 内）；R2 用新 tip 现编栈。
- **R2 收敛轮已开（主线，2026-10-24）** — 冻结 tip a413e54（含 668efdf + 370c217）：WB 第 3 轮交接已进仓 docs/fix-program/source/cross-test-round-3.md（84adc2a；全量 P1+P2+P3 + SC-1～SC-7 + 回归 130/131/132/133 + 126–128 + XT-R2-1 WB 侧确认 + 严格 P2 真实 /register，只报不修；回执格式：终止标签 + 单行三数 + Persona 四分 + 环境五元组；R-02/R-13 不在本轮范围，收敛判定时由用户裁决豁免或收官后立项）+ 站内一句话提醒已派（浏览器任务，确认待回）；主线腿同步轻量回归冒烟（新 tip 现编栈，登录/Tracker/材料树/回收站/Prep/Mock 轻触，pageerror 计，新发现只报不修）。收敛口径：本轮零新发现（或全部持有用户书面豁免）→ S0 最终判定；任一真 S1/S2/S3 → 不收敛，进缺陷循环。

- **R2 提醒确认（主线，2026-10-24 19:29）** — 站内一句话提醒已发送达（线程身份核过：标题 your-interview，09:33/10:13/14:49/15:24/16:01/16:10 历史标记顺序齐全；原文逐字一致，无其他内容；WB 已开始处理）。

- **R2 主线腿冒烟通过 8/8，新发现 0（主线，2026-10-24）** — 冻结 a413e54，新栈 4510–4516、scratch 库 yourinterview_r2ml（测后已 DROP，共享库未动，4200 未启，R1 栈未碰；报告 ~/workspace/final-acceptance/rounds/R2/MAINLINE-SMOKE.md）：登录（admin + 新注册 User + 复登）、Tracker 列表 + 新建（实时真实岗位 Capco .NET/C# 岗，仅 Tracker 行未投递）、材料树保存（PowerUser）/ 只读（默认 User）、回收站单项恢复、Prep 框架节段数（设计轮 12 节：greeting 5、design-round 7、weakness-plan 5、closing 7、tech-qa 4）、故事银行必填星号（5 项带 *）、Mock 空态按钮轻触（深旅程 14 步由 XT 复验覆盖不重做）；pageerror 全程 0。执行中失败全属脚手架侧已纠正重验（建库竞态 42P04、launch.sh 取参 bug 丢 demo 种子致 401、人机 checkbox 门控、inline 改名定位、编辑态行菜单、保存确认框），其中 1 处空洞通过（未删而“恢复 PASS”）已作废、以真实删除→保存→恢复链路为准。UNTESTED（环境外）：Prep 真 LLM 生成、Azure 发音评分、麦克风录音。WB 第 3 轮回执未到，收敛判定待 WB 腿。

- **WB 第 3 轮进行中、正式回执未到（主线，2026-10-24 22:03 线程只读核查）** — 19:29 之后无正式回执（无终止标签、单行三数、Persona 四分、新发现清单、环境五元组、pageerror 计数）。进程实况：19:35 WB 完成消息称 0.3ms 403 系鉴权中间件、其环境 assessment 镜像为修复前构建（旧 MockManage 策略），故 403 属环境过期非产品缺陷，遂重建镜像重跑旅程；用户 21:44 发 continue；WB 最新消息检查时仍在进行中（思考中 18m53s）：容器重建替换、ng serve 重启、前三步通过（空态按钮渲染 → 创建成功不再 403 → 入列）、header 按钮选择器已修、XT-R2-1 六步全过、P1 冻结 tip 回归进行中。正式回执待 WB 本轮完成。

- **WB 第 3 轮回执已登记（R2-C1，主线，2026-10-24 22:03，COMPLETED）** — 冻结 a413e54，checkout 位 a413e54（其后 3 笔纯文档，代码逐字一致），本轮代码提交 0，pageerror 0，熔断未触发。单行三数：完成场景 3（SC-2/SC-3/SC-4）+ SC-1 部分 / 新发现 0 / 缺陷指针 0。Persona 四分：OBSERVED 18 / PERSONA-INFERRED 1（P3 预算 15 实际 3 步完成 XT 主路径，两处受挫点均消失）/ ASSUMPTION 1（133 UNTESTED 归因环境无 LLM）/ UNTESTED 5（133 报告结构、SC-1 Tech Stack 删恢复、SC-5/6/7）。XT-R2-1 WB 侧独立确认六步全过（空态按钮渲染 → 创建成功非 403 → 入列 → header 新建第二场 → 完成按钮渲染 → 无 LLM 生成诚实失败 502 + Retry 在场、无编造题目）；首跑 403 经排查为 WB 自有栈 assessment 镜像系 668efdf 之前构建（旧 MockManage 策略），重建后消失，诚实登记为环境事件、**不列为发现**。回归：130 过且补全快/慢两路证据（快路无 loading 帧内容即挂无空闪，3 未缓存词 cardLen 154/261/417 全有内容；慢路 1.2s 延迟骨架出现→脱离→内容落定）、131 过、132 过、126–128 抽样过；SC-2/SC-3/SC-4 过；SC-1 部分（Tech Stack 删恢复因 demo 账号 Tech Stack 真实为空 0 items、按铁律不摆拍造数 → UNTESTED；Intake→Prep 段因 133 断链未串完）；SC-5/6/7 与 133 UNTESTED（无 LLM，inspect 存证；W2 快照第 2 轮存档仍有效）。合成数据闭环清零（测试用户 0 / mock_sessions 0 / practice_recordings 0 / W1 素材 11 完好）。WB 自评：本轮零新发现满足收敛前提，但 133 与 SC-5/6/7 的 UNTESTED 是否阻收敛、以及 R-02/R-13 豁免，归用户裁决，本线不自行判定。主线核对：格式齐全（终止标签 + 单行三数 + Persona 四分 + 环境五元组）；R2 两腿新发现均为 0，缺陷轴收敛前提已满足；收敛判定待用户裁决（R-02/R-13 处置 + 环境外 UNTESTED 是否阻收敛）+ CI 最新 tip 全绿 run（本推送触发新 run）。

- **S0 收敛判定（用户裁决，2026-10-24，选项 A）** — R2 两腿新发现均为 0（主线冒烟 8/8 + WB 第 3 轮回执 R2-C1 COMPLETED，单行三数 3 + SC-1 部分 / 0 / 0），缺陷轴收敛前提满足，S0 判定收敛。环境外 UNTESTED（133 真 LLM 链、SC-5/6/7、SC-1 Tech Stack 删恢复）按 R1 同口径走 Mac 跟进、不阻收敛。收敛后序：135/136/137 依次实装 → 开 develop→main PR → PR 上全功能测试 → 用户本人点头才合。验收关闭前仍需 CI 最新 tip 一次后端全绿 run（5f11184 run 进行中；StartupBudget 两连败致抖动认定作废、竞态死锁新败在案，全绿门未变）。
- **R-02/R-13 书面豁免（用户裁决，2026-10-24，选项 A；N-1 规则：S3 修掉或书面豁免）** — R-02（S3，Prep 框架，无单票归属）：Jimmy 编程轮“计时/白板式手写算法练习入口 + 复盘位”缺失，Mock/Practice 均为问答式、非计时手写钻；真缺口、属新练习形态、非纯文案；范围锁（E-4：验收期间不进新功能）下本轮不做；SC-7 会再暴露同一缺口，同一缺口只立一票、不重复计。R-13（S3，Prep 框架）：Hajir 招聘期望“读代码/调试专项准备入口”缺失（故事承载在深挖节，但无专项入口）；真缺口、属新入口形态；范围锁下本轮不做。豁免效力：两项记为已裁决不做（非遗忘、非拒绝）；日后如需，凭此条重立项、无需重新分诊。
- **票 135 已登记（S0 收敛后首项，2026-10-24）** — 轮次轨道改横向（用户 2026-10-24 定，时机 A；草案 ~/workspace/ticket135-rounds-horizontal-track/DRAFT-135.md）：轨道横向左→右（圆点 + 横线，done 着色、当前高亮、末尾加一轮节点保留），当前轮详情卡移到轨道下方全宽展开，轮多时轨道区横向滚动；定稿进行中（SPEC.md），实装与独立复测另派。

- **票 135 定稿已落成 + 实装已派（主线，2026-10-24）** — 定稿 ~/workspace/ticket135-rounds-horizontal-track/SPEC.md（R-135-1…R-135-12 + 不变清单 + 验收 A-1…A-13）：基线亲手核过（playbook-detail .rtrack，123 R-9 语义逐字继承：日期序 + roundNo、done 判据、当前轮派生、节点字形只 reached/hollow、加轮终点节点、共享几何令牌一字不动）；与草案出入 D-1…D-7 均注明原因（D-1 填充改运行时测量；D-2 当前节点补品牌色标签 + 2px 下划线高亮——卡片移到底下后紧贴关系消失、无独立标识则认不出当前轮，圆点字形不变；D-3 当前节点自动滚动可见；D-4 滚动条件改为内容超宽即滚动；D-5 轨道区高度与轮数无关；D-6 维持单段填充；D-7 实装基线改为远端最新 tip）；未核实点 U-1…U-4 已写明（无横向设计稿、360px 未实测、滚动宽度增长未实测、填充量取手段由实装方选）。实装已另派（按定稿逐条实装 + 活体验证，独立复测随后）。

- **票 135 实装已推 33a79b5 + 独立复验已派（主线，2026-10-24）** — 仅 3 文件 208+/89-（playbook-detail 的 html/scss/ts）：滚动容器 + 行（线与点同行内一起滚）、节点列去内嵌卡片、当前轮详情卡移到轨道下方全宽（绑定一字不变）、data-testid 保留、无新增点选切换、无新增 i18n 键；行 flex + 列不挤压 + 行宽 max-content、圆点 16px 令牌不变、水平 2px 轨道线、填充段 width 过渡、当前节点品牌色标签 + 2px 下划线高亮、加轮卡取内容自然宽度；新增 currentRound 计算属性 + trackMetrics 信号 + 运行时圆心测量（滚动内容坐标，待决线首→加轮圆心、填充段首→当前圆心、首轮当前宽 0）+ ResizeObserver 重测 + rAF 合并量取 + 自动滚动仅首测与当前轮变化时（nearest、纯横向、不碰页面滚动、卸载/重挂复位）；竖向填充与 TRACK_NODE_STEP 已移除、无两套填充并存；trackSegmentDone 等语义函数一字未动；几何文件与其他消费者、main、PROTOCOL、台账、136/137 全未碰。实现方自测：tsc 0、check-templates 25/25、Jest 562/562；活体 23/23、pageerror 0（新栈 4530–4536，scratch 已 DROP，4200 未启）：覆盖 A-1…A-12（横向一排、多轮滚动、加轮高度 76→76 不变滚动宽 3000→3358、色对令牌、高亮迁移、卡片全宽、虚线节点进 intake、360px 无溢出、首渲染可见且不滚回、填充对齐 0 误差、卸载/重挂、几何量值）+ 迁移后高亮与卡片同为 Round 3 补查；首轮 3 项“失败”经核查为脚本期望错误（后端基线行为每条目自动带 1 个无日期轮次）已修正、42P04 建库竞态属脚手架侧。留给复验：填充过渡中途帧捕获、R-9 几何探针重跑。

- **CI 全绿 run 已到（主线，2026-10-24）** — run 40158166715（tip 46e21d9，含 135 实装 33a79b5）三项全绿（Backend/Frontend/E2E），验收全绿门满足，打破同一份代码连败四轮（StartupBudget 三次 + 竞态一次）；此前作废的 StartupBudget 抖动认定仍按规矩作废处理（不靠它放行），本次满足靠的是真实全绿 run。注意：全绿门跟 tip 走，最终合并前仍需在最终 tip 上复核一次。
