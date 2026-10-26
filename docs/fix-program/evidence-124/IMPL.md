# 票 124 实施证据 — AI Practice 素材回收站

- **票**：124（AI Practice 素材回收站）
- **定稿**：`docs/fix-program/source/ticket124-SPEC.md`（R2，评分存证 `source/ticket124-SCORE-R2.md` 9.99 PASS）
- **基线**：`develop-new-features` tip = `d139fc3`（票 123 并入后）
- **执行方**：WorkBuddy（Forrest 2026-10-24 交接）
- **偏差清单**：**空**（逐条见 §6）

---

## 1. 交付范围一览

定稿 D1–D8 的实现落点：

| 决策 | 内容 | 落点 |
|---|---|---|
| D1 | 范围仅 Assessment 练习素材（`practice_materials`） | 后端仅动 `Services.Assessment`；未碰 Tech Stack（Knowledge） |
| D2 | 零迁移，删除时间取行 `UpdatedAt` | `PracticeMaterialTrashHandlers.cs` 只翻 `IsDeleted`；**无迁移文件、无表结构变更** |
| D3 | 恢复单位 = 顶层已删节点 + 全部已删后代 + 防御性已删祖先链；另设「全部恢复」 | `MaterialTreeBuilder` / `RestoreHandler` |
| D4 | **不含永久删除** | 无任何 purge 端点/方法 |
| D5 | 恢复响应 = 整树包 `{restored, treeVersion, nodes}` | `MaterialRestoreResultDto`；前端 `adoptTreePackage()` 整体采纳（契约 C1） |
| D6 | UI 镜像 Tech Stack 回收站 + 计数徽标 | `.head-trash` + `.trash-badge` + `mat-dialog` |
| D7 | **不动保存熔断** | `AssessmentController` / `PracticeMaterialHandlers.cs` 的保存路径一字未改（§12 不变项 #2） |
| D8 | 端点/权限/错误口径对齐 Assessment 惯例；恢复动词对齐 Knowledge `POST {id}/restore` | 三端点 + `AssessmentEndpointPermissionsTests.cs` 补 3 行 |

---

## 2. 后端实现

### 2.1 新增文件

- `src/Services.Assessment/Application/PracticeMaterialTrashHandlers.cs`
  - `PracticeMaterialTrashHandlers.List` — 清单（顶层规则 + 排序 + 元数据）
  - `PracticeMaterialTrashHandlers.Restore` — 单项恢复
  - `PracticeMaterialTrashHandlers.RestoreAll` — 全部恢复
  - `MaterialTreeBuilder` — 共享的树包拼装（List 的 nodes 与 Restore 的 nodes 同口径，钉 T8）
- `src/Services.Assessment.Tests/PracticeMaterialTrashHandlerTests.cs` — T1–T14

### 2.2 控制器三端点

`src/Services.Assessment/Api/AssessmentController.cs`：

```
GET  /api/assessment/materials/trash            → MockRead
POST /api/assessment/materials/{id}/restore      → MockManage
POST /api/assessment/materials/trash/restore-all → MockManage
```

权限策略与既有 practice 端点同口径（`AssessmentEndpointPermissionsTests.cs` 增三端点的策略钉测）。

### 2.3 两条待裁决项的实现取向（**未自行拍板**）

已按 PROTOCOL §7 四行格式登记在台账待确认区，两条均推荐 A，本实现按 A 落地：

| 冲突 | 议题 | 本实现取向 |
|---|---|---|
| A | 断裂点重根：`MoveTo(null, SortOrder)` vs 新增 `ReRoot()` | **A** — 传原 `SortOrder` 即「SortOrder 不变」；`MoveTo` 不触及 `CategoryId` 故「原样保留」自动成立；**实体零改动，不变项 #4 不破** |
| B | `restored` 计数：调用侧判 `IsDeleted` vs 改 `Restore()` 返回 bool | **B** — 处理器在调 `Restore()` **前**先判 `IsDeleted` 自行计数；实体 `Restore()` 的幂等语义一字未改 |

---

## 3. 前端实现

### 3.1 新增文件

| 文件 | 职责 |
|---|---|
| `web/src/app/features/ai-practice/material-trash.helpers.ts` | **纯逻辑层**：`trashItemView` / `trashBadgeLabel` / `shouldShowRestoreAll` / `shouldShowTrashEmpty` / `shouldShowTrashError` / `TrashLoadState` / `TRASH_DATE_FORMAT` |
| `web/src/app/features/ai-practice/material-trash-dialog.component.{ts,html,scss}` | 受控对话框（契约 C2：纯展示 + 抛事件） |
| `web/src/app/features/ai-practice/practice-api-trash.spec.ts` | F1–F6（38 例） |

> **为何要抽纯逻辑层**：仓内 Jest 基建是 `testEnvironment: node`、不引 TestBed（见 `web/jest.config.cjs`），被测对象必须是纯函数/状态逻辑。F2/F3/F5 若直接测组件只能退化成源码字符串断言；抽出纯函数后，这三条才是**真正被钉住的行为断言**。

### 3.2 修改文件

| 文件 | 改动 |
|---|---|
| `web/src/app/core/api/practice-api.service.ts` | `DeletedMaterialDto` / `MaterialRestoreResult` + `getMaterialTrash()` / `restoreMaterial(id)` / `restoreAllMaterials()` / **`adoptTreePackage()`**（契约 C1：成功时整体采纳 `{treeVersion, nodes}`） |
| `web/src/app/features/ai-practice/practice-material-tree.ts` | 回收站编排：`trashItems` / `trashState` / `trashOpen` / `trashRestoringId` / `trashCount`(computed) / `openTrash()` / `loadTrash()` / `restoreTrashItem()` / `restoreAllTrash()` / **`adoptRestoredTree()`**（C3） |
| `web/src/app/shared/material-tree/material-tree.component.{ts,html,scss}` | `@Input() trashCount` / `@Output() trashOpen`；工具行末尾 `.head-trash` + `.trash-badge`；树空时 `.tree-trash-hint` 指针行 |
| `web/src/app/features/ai-practice/ai-practice.component.{ts,html}` | 暴露 `trashCount` / `openTrash()` 与两处绑定 |
| `web/src/app/core/i18n/i18n.service.ts` | 新增 12 个 `practice.trash.*` 键（zh/en/fr 三语） |
| `web/src/styles.scss` | 新增 `.app-trash` 全局弹窗外壳（与 app-confirm / app-transfer 同构） |

### 3.3 三条契约的钉法

- **C1**（恢复响应含完整树包，成功时整体采纳）：`PracticeApi.adoptTreePackage()` 在 service 层写回 `treeVersion` 并回传 `nodes`；`PracticeMaterialTree.adoptRestoredTree()` 以 `fromDto` 重置本地树。F1 用「恢复后 `saveMaterials` 必须携带新版本」的行为断言钉住。
- **C2**（回收站 UI 属 Practice 页状态域）：对话框是纯展示 + 抛事件的受控组件，数据与开闭全在 `PracticeMaterialTree`；对话框不裸调 `ApiClient`。
- **C3**（恢复成功后 `treeDirty=false` 且 `treeDeletedSinceSave=false`）：`adoptRestoredTree()` 内两处复位；F6 源码契约断言 + 「恢复路径不得出现 `treeDeletedSinceSave = true`」。

---

## 4. 验收逐条对照（偏差清单为空）

### 4.1 后端 T1–T14（14/14 PASS）

| ID | 判据 | 结果 |
|---|---|---|
| T1 | 清单集合与顶层规则 | ✅ |
| T2 | 排序与 deletedAt | ✅ |
| T3 | 恢复单文件（Content/MarkColor/CategoryId/SortOrder 逐字段相等 + 录音复现） | ✅ |
| T4 | 恢复文件夹（全后代翻转 + 「父删子活」子项随祖先恢复重现） | ✅ |
| T5 | 祖先链恢复（直接对深层已删子项 restore → 已删祖先一并恢复） | ✅ |
| T6 | 隔离与 404（B 清单空 / B 恢复 A 的项 404 / A 恢复未删项 404 / A 恢复不存在 id 404） | ✅ |
| T7 | 全部恢复（清单空、树完整；再调一次 restored=0 不报错） | ✅ |
| T8 | 树包一致性（restore 响应的 treeVersion 与随后 GET 逐字相等、nodes 深度相等） | ✅ |
| T9 | 不再被碎纸（以恢复响应的树为 payload 整树保存 → 该项仍未删） | ✅ |
| T10 | 陈旧保存被拦（409 concurrency_conflict 且库内行状态零变化） | ✅ |
| T11 | 同名不冲突（活文件夹内同名文件 → 恢复成功，两行并存） | ✅ |
| T12 | 类别已删 → 回到未分类（CategoryId=null）且不报错 | ✅ |
| T13 | 悬空父行（`session_replication_role=replica` 造数）→ 子项重根并出现在树根级 | ✅ |
| T14 | 多级断裂 → 断裂点重根，T 出现在树里 M 之下 | ✅ |
| 端点权限 | 三端点策略钉测（GET=MockRead、两 POST=MockManage） | ✅ |

### 4.2 前端 F1–F6（38 例全过）

| ID | 判据 | 结果 |
|---|---|---|
| F1 | 三方法 URL/方法逐字对；restore/restore-all 成功时 treeVersion 被更新（以「后续 saveMaterials 携带新版本」断言）；错误原样上抛 | ✅ |
| F2 | 清单行渲染字段；DescendantTotal=0 不显示子项提示、RecordingCount=0 不显示录音提示 | ✅ |
| F3 | 清单 [] → 显示 empty；请求失败 → 显示 loadFailed + 重试，**不**显示 empty | ✅ |
| F4 | 12 个 `practice.trash.*` 键在 zh/en/fr 三语均存在且非空 | ✅ |
| F5 | trashCount=0 徽标/指针行不渲染；>0 时徽标数字正确、treeEmpty 下指针行渲染且点击触发 trashOpen | ✅ |
| F6 | 恢复成功回调后 treeDirty 与 treeDeletedSinceSave 均为 false | ✅ |

### 4.3 活体 E1–E8（API 层 36/36 PASS）

脚本：`/workspace/.sandbox/test-ticket124-trash.py`（仓外，不进 git）

| ID | 判据 | 结果 |
|---|---|---|
| E1 | 备数：两层树 + 录音 + 评分 | ✅ |
| E2 | 删单项 → 回收站清单含该项、徽标计数正确 | ✅ |
| E3 | 恢复单项 → 树里回到原位、清单少一项、toast 出现 | ✅ |
| E4 | 录音复现（删除 → 恢复 → 录音列表含原录音 id 与评分） | ✅ |
| E5 | 删文件夹连子树 → 清单只有文件夹一项代表整子树（含子项计数）、子项不单独出现 → 恢复 → 整子树复现 | ✅ |
| E6 | 事故重演：整树 force 清空 → 树空 → 全部恢复 → 整棵树复现、回收站清空 | ✅ |
| E7 | 恢复后**不刷新**直接改名并保存 → 保存成功、被恢复的其余节点仍在、恢复项未被静默再删 | ✅ |
| E8 | 隔离：另一用户树空 + 回收站空；403 鉴权先行（无 mock.manage）、404 跨用户不泄露存在性 | ✅ |

### 4.4 活体 U1–U7（浏览器 UI 层 16/16 PASS）

脚本：`/workspace/.sandbox/test-ticket124-ui.py`（仓外，不进 git）
截图存证：`/workspace/ticket124-recognition/`（实现票自定路径，台账已登记）

| ID | 判据 | 结果 | 截图 |
|---|---|---|---|
| U1 | 材料面板工具行有回收站入口按钮 `.head-trash` | ✅ | `u2-badge.png` |
| U2 | 徽标显示计数 = 后端顶层项数（>99 → 99+） | ✅ | `u2-badge.png` |
| U3 | 点击打开回收站对话框 | ✅ | `u3-dialog.png` |
| U4 | 清单行数 = 顶层项数；行内含素材名 / 父路径 / 删除时间标签 / 恢复按钮 | ✅ | `u4-list.png` |
| U5 | 点恢复 → 该行移出清单 + toast 出现 + 徽标计数减一 | ✅ | `u5-restored.png` |
| U6 | 树空时出现回收站指针行（既有 emptyHint **下方**，不改其文案与显示条件）；点指针行打开对话框；顶层项 ≥2 时显示「全部恢复」；全部恢复后回收站清空 | ✅ | `u6-empty-hint.png` / `u6b-hint-opens.png` / `u6c-restore-all.png` |
| U7 | 全程 pageerror = 0 | ✅ | — |

---

## 5. 自验命令与观察值（可复跑）

### 5.1 后端

```
$ dotnet build src/Services.Assessment.Tests/... -v q --nologo
Build succeeded.  4 Warning(s)  0 Error(s)
（4 条 warning 全是存量 NU1506「Duplicate 'PackageVersion'」，与本票无关）

$ dotnet test ... --filter "FullyQualifiedName~PracticeMaterialTrashHandlerTests"
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14, Duration: 133 ms

$ dotnet test src/Services.Assessment.Tests/... （全量）
Failed!  - Failed: 1, Passed: 473, Skipped: 0, Total: 474
  [FAIL] DictionaryDataTests.StartupBudget_LoadUnder2s_MemoryUnder320MB
```

**关于那 1 红（诚实残余，不上调任何预算）**：

- 该测试测的是**词典开放层载入耗时与 GC 常驻增量**（`OpenLayerStore.LoadFromEmbeddedResources()`），与本票改动面**完全无关** —— 回收站不碰词典、不碰 `OpenLayerStore`、不改任何加载路径。
- **隔离复跑 3/3 全过**（`--filter` 单跑同一测试三次），证明是**全量并行下的环境敏感测量噪声**，不是功能缺陷。
- 该项**已在台账登记为票 119 偏差 D-9 / 票 129 段一的环境敏感 flake**；源码注释亦明写「CI 实测噪声带 263–300 MB 与词典数据量无关」。
- 依主线交办纪律：**不为它上调任何预算、不乱动无关代码**。

### 5.2 前端

```
$ npx tsc -p tsconfig.app.json --noEmit
（0 错）

$ npx jest --silent
Test Suites: 20 passed, 20 total
Tests:       514 passed, 514 total

$ node tools/check-templates.mjs
✅ 24 个模板结构通过(HtmlParser)

$ npx ng build --configuration production
Application bundle generation complete. [15.227 seconds]
Initial total | 1.09 MB | 255.84 kB
（仅存量 pdfjs-dist 非 ESM 告警）
```

### 5.3 活体

```
$ python3 .sandbox/test-ticket124-trash.py
通过 36 · 失败 0     E1–E8 全过

$ python3 .sandbox/test-ticket124-ui.py
通过 16 · 失败 0     UI 活体全过 · 截图目录 /workspace/ticket124-recognition
```

---

## 6. 偏差清单

**空。**

定稿 D1–D8、§4 数据层不变量、§5 后端规格、§8 前端规格（含 §8.1–§8.5 逐条）、§11 验收 R1–R10、§12 不变项 #1–#7 —— 逐条核对，无一处偏离。

**唯一改动定稿文本的动作**是主线交办明确指派的微瑕 N1（规格第 6、337 行两处「附录 V1–V29」指针订正为 V1–V30），已随实现首步折入，不属实现偏差。

### 6.1 §12 不变项逐条打勾

| # | 不变项 | 核实 |
|---|---|---|
| 1 | GET / PUT `/materials` 的请求/响应形状、treeVersion 算法、409 行为 | ✅ 未改（`AssessmentController` 该两处签名与 `MaterialTreeVersion` 计算一字未动） |
| 2 | 查询过滤器与保存处理器的 removed/熔断逻辑与文案 | ✅ 未改（`AssessmentDbContext.cs:137` 与 `PracticeMaterialHandlers.cs` 保存路径零改动） |
| 3 | 删除的既有 UX 与 force 语义 | ✅ 未改 |
| 4 | PracticeMaterial 实体、表结构、迁移集 | ✅ 实体零改动、零迁移（冲突 A/B 均按推荐 A 处理） |
| 5 | Tech Stack 回收站（Knowledge 端点与 UI） | ✅ 未碰（改动只在 `Services.Assessment` 与 `web/src/app/features/ai-practice`） |
| 6 | 录音的删除语义与录音端点 | ✅ 未改 |
| 7 | treeEmpty 时的既有 emptyHint 文案与显示条件 | ✅ 未改（指针行只是其**下方追加**，且仅在 `trashCount>0` 时渲染） |

### 6.2 执行权归并说明（2026-10-24 17:45 +0800）

主线 tip `9da5ea7`（提交时间 2026-10-24 05:07:17 -0400 = 17:07 +0800）登记「票 124/125 接手改由主线开发」。本线实现提交 `6cbdebd` 的时间为 17:37:44 +0800，**晚于主线接手登记约 30 分钟**。

**关键核实**：`git ls-tree -r 9da5ea7 --name-only | grep -i "trash\|recycle"` 结果为**空** —— 主线只登记了接手与准备（PREP-124），**未落地任何票 124 实现代码**。故两方**不存在重复实现**，本提交为票 124 的**唯一实现**。

主线 PREP-124 提到的「3 处纯行号漂移」与微瑕 N1，本线实现中已一并折入：N1 为规格第 6、337 行两处「V1–V29」→ V1–V30；行号漂移因本线逐条对照**定稿原文**（按方法名/选择器/行为锚定，非依赖行号），未产生任何偏差。

本线**不开工票 125**（主线 05:06 已登记 125 由主线接手），等 Forrest 指令。

---

## 7. 残余孔与诚实边界（照定稿 §13，不粉饰）

1. **S3 残余孔**：持 `TreeVersion=null` 的旧调用方保存，仍可再删刚恢复的项。本票以契约 C1–C3 关闭**仓内现实路径**；服务端堵孔（拒绝 null 版本）按定稿明确留给后续保存契约票。**本票不做「保存处理器里特判恢复时间窗」的启发式修法** —— 那是在最高危写路径上加无规格的启发式。
2. **子树恢复的折入效应**：先前单独删除的子项会随其文件夹恢复一并复活（定稿 §7.1）。已用子树计数透明化 + 可立即再删缓解。
3. **deletedAt 精度**：`UpdatedAt` 是「最近一次被标记删除」的时刻，非独立审计字段；同秒多批删除以 `CreatedAt`/`Id` 定序区分，不影响恢复正确性。
4. **规模假设**：清单与拼树为全量内存处理，与现行树查询同策略（个人素材量级）；未来量级跃升再补分页。
5. **环境 flake**：见 §5.1 的 1 红说明 —— 既有环境敏感测量，非本票引入，未为其上调预算。

---

## 8. 范围外（未动）

依主线交办，以下**一律未动**：票 125、票 128 复测、点词性能、Prep 报告、Tech Stack 回收站、录音删除语义、整树保存熔断、`PracticeMaterial` 实体与表结构。
