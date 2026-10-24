# 票 124 规格：AI Practice 素材回收站（Recycle Bin）

- **票号**：124（本文件为规格稿，待独立评分 ≥9.9 过门后才许登记实现）
- **版本**：R2（R1 独立评分 9.87/10 FAIL，报告 `SCORE-R1.md`；本版仅按扣分项 K1–K4 做定点文字修订，设计决策 D1–D8、范围与验收意图一字未动；修订映射见文末附录「评分史」）
- **需求原文**（用户，2026-10-23）：「加回收站」
- **事实基线**：远端 `develop-new-features` tip `2b4112dc8c3544046ff36088d19e30c517752811`（新鲜 ls-remote 核实）。本规格全部代码断言均在该 tip 上逐条实查，验证命令与观察值见 `evidence/usage-traces.md`（附录 V1–V30，下文引用写作 V#）。
- **事故依据**：`~/workspace/incident-practice-empty/REPORT.md`——整树覆盖保存把用户 135 条素材行全部软删，界面显示「数据库里还没有素材」，应用内无任何自助恢复入口，用户只能求助并最终靠一条经批准的 UPDATE 才恢复。本票即补这个缺口。
- **本文件地位**：实现唯一事实源。实现与本规格不一致 = 实现错；实现中撞到本规格未覆盖的真实冲突，停工上报，不许现场即兴改稿。

---

## 0. 需求复述三件套

**做什么**：给 AI Practice 的练习素材补一个回收站——用户能在 Practice 页材料面板内打开回收站，看到自己被软删除的素材（文件夹/文件），并把它们（含其子树与挂着的录音）恢复回材料树；提供「全部恢复」以应对整树误删事故。

**不做什么**：不做永久删除（彻底删除）——见决策 D4；不改整树保存的防误删熔断——见 D7；不给其他服务/页面（AI Mock 会话、Playbook、Tracker、Identity）做回收站；不加数据库列、不做迁移；不改变现有删除方式（编辑模式删节点 → 保存）与现有空状态文案。

**验收长什么样**：第 11 节的活体剧本 E1–E8 全部通过（核心一条：重演事故——整树 force 清空 → 树空且出现回收站指针 → 打开回收站点「全部恢复」→ 整棵树连同录音原样回来，且此后正常编辑保存不会把恢复的素材再次静默删掉）。

---

## 1. 背景与根因（摘要，详见事故报告）

1. 素材删除是**软删**：`PracticeMaterial.IsDeleted`（Domain/PracticeMaterials.cs:98），实体备 `MarkDeleted()`（:181）/`Restore()`（:188）；`AssessmentDbContext` 给该实体配了全局过滤器 `!x.IsDeleted`（AssessmentDbContext.cs:137），一切普通查询看不见已删行（V1、V2、V3）。
2. 整树保存（`SaveMaterialTreeCommandHandler`，Application/PracticeMaterialHandlers.cs:121）把「未出现在提交树里的存量节点」一律 `MarkDeleted()`（removed 集 :227、标记循环 :247）；防误删熔断只拦「未带 force 且单次删除 ≥5 个且占比 >80%」（:237-240）。客户端在编辑期删过任何节点，保存时即自动带 `force=true`（practice-material-tree.ts:180、:238、:1251，V27），故熔断拦不住「带 force 的整树覆盖」这一事故形态（V8、V24）。
3. 读取路径从 `ParentId == null` 在内存里拼树（GetMaterialTreeQueryHandler :53、Build :68，V10）：父节点被删的子树整体不可达——「行还在、人看不见」。
4. 应用内**没有任何**列出/恢复已删素材的端点或界面：Assessment 里唯一的 `IgnoreQueryFilters()` 调用是保存处理器内部加载存量行（PracticeMaterialHandlers.cs:137，V4）。对照：Knowledge（Tech Stack）早已全套具备——列表 `includeDeleted`（KnowledgeController.cs:35）、`POST {id}/restore`（:207）、Tech Stack 页回收站 UI 与文案键 `ts.trash.*`（V18、V20、V21）。本票即把 Assessment 素材补到同一水平，并针对「树」这一数据形态处理好子树/孤儿/版本契约（第 5、6 节）。

---

## 2. 决策总表

| # | 决策 | 一行理由 |
|---|------|----------|
| D1 | 范围 = **仅 Assessment 练习素材**（practice_materials） | 需求源于素材事故、入口在 Practice 材料面板；其余软删实体各属其他服务/页面，Knowledge 已自备回收站（第 3 节普查） |
| D2 | **零迁移**：删除时间取行 `UpdatedAt`，不加 DeletedAt 列 | 写路径审计证明已删行的 UpdatedAt 只可能由「被标记删除」写入（第 4 节） |
| D3 | 列表单位 = **顶层已删节点**；恢复单位 = 该节点 + 其全部已删后代（+ 防御性已删祖先链）；另设**全部恢复** | 删除以子树为单位发生（整树保存），恢复必须同粒度；事故形态（整树被删）需要一键全复（第 5.3 节） |
| D4 | **永久删除不在本票**，列为后续票候选 | Knowledge 回收站亦为恢复-only 先例；且素材的彻底删牵涉录音行/评分行/磁盘文件的级联设计，不可逆路径须独立规格过门（第 9 节） |
| D5 | 恢复响应 = **整树包** `{ restored, treeVersion, nodes }`，不用 Knowledge 式 204 | 素材树有 treeVersion 一致性契约（票 111）；只回版本号不回节点会制造「版本对齐、内容缺项」的再删通道（第 6 节核心论证） |
| D6 | UI = 材料面板工具行图标按钮（带计数徽标）+ 模态对话框；树空且回收站非空时空状态下加一行回收站指针 | 镜像 Tech Stack 已上线形态（入口/文案/恢复按钮）；指针行直接修复事故里「看着空、其实全在回收站」的无路可走（第 8 节） |
| D7 | **熔断加固不在本票**，列为后续票候选 | 熔断的失效形态是 force 通道，后者与客户端删除确认流程同源，加固 = 保存契约再设计，属最高危写路径，不与恢复面捆绑（第 10 节） |
| D8 | 端点/权限/错误口径全面对齐 Assessment 既有惯例；恢复动词对齐 Knowledge 的 `POST {id}/restore` | 同服务内不许出现第二套风格（第 5.1 节） |

---

## 3. 范围与边界（D1 的普查依据）

全仓带 `IsDeleted` 软删过滤器的实体普查（V29，命令可复跑）：

| 实体/面 | 位置 | 现有恢复面 | 本票处置 |
|---|---|---|---|
| **PracticeMaterial**（练习素材） | AssessmentDbContext.cs:137 | **无**（V4） | **本票范围** |
| MockSession（AI Mock 会话） | AssessmentDbContext.cs:66 | 无 | 不在本票：属 AI Mock 页，会话有 complete/abandon 自有生命周期与显式单删端点（DELETE sessions/{id}）；其回收站是该页面的独立设计，列后续候选 |
| PracticeRecording（练习录音） | 实体有 IsDeleted（PracticeMaterials.cs:261）但**无查询过滤器**，删除走硬删（删文件+删行，PracticeRecordingHandlers.cs:245/:273/:280，V12） | 不适用 | 不在本票：软删行根本不累积，无物可收 |
| Knowledge 三个带软删过滤器的实体：KnowledgeItem（Tech Stack 知识点）、TechStackCategory（分类占位行）、ReviewActivityDay（每日复习活动统计行） | KnowledgeDbContext.cs:121/:155/:170 | KnowledgeItem **已有全套**（列表 includeDeleted + restore 端点 + UI，V18/V20）；TechStackCategory、ReviewActivityDay 无恢复面 | 不在本票：与本票需求同类的 KnowledgeItem 已覆盖；TechStackCategory 的软删/恢复由 Tech Stack 页分类管理自有内部路径自理（软删后重加走 Restore、不新建第二行，DbContext 注释实查）；ReviewActivityDay 是派生统计行、非用户内容——两者均属 Knowledge 页下位数据，日后需要时按 Knowledge 页面独立立票 |
| Interviews 条目等 5 面 | InterviewsDbContext.cs:131/:341/:356/:370/:388 | 服务内自理 | 不在本票：属 Playbook 页、另一服务 |
| Jobs 投递等（多面） | JobsDbContext.cs（V29） | 服务内自理（含收藏 Restore 内部路径） | 不在本票：属 Tracker 页、另一服务 |
| Identity 用户/角色 2 面 | IdentityDbContext.cs:39/:52 | 管理端自理 | 不在本票：属 Admin 面 |

边界理由（除表内逐项外再补一条总的）：回收站的入口被需求钉死在 Practice 材料面板；把其他服务的已删数据跨服务搬进这个面板，违反「多模块共用同一底层数据、按模块分视图」的数据纪律，也会让本票的验收面跨四个服务膨胀。其他页面日后要回收站时，本票的端点/DTO/契约形态即为模板，各自立票。

---

## 4. 数据层事实与不变量（零迁移的证明）

1. **行结构已够用**：素材行含 Id / UserId / ParentId / Kind / Name / Content / SortOrder / IsExpanded / CreatedAt / UpdatedAt / IsDeleted / MarkColor / CategoryId（PracticeMaterials.cs:27 起、V2）。回收站需要的全部元数据（名称、类型、父链、类别、排序、删除时刻代理）都在行上，**不需要新列、不需要迁移**。
2. **deletedAt := UpdatedAt 的等价性证明**（写路径审计，V4–V7）：
   - 全 Assessment 内 `db.Materials` 使用点共 6 处（V5）：树查询（读）、版本计算（读）、保存处理器加载（:136，唯一写路径）、Add 新行（:191）、标记色处理器（:285——走普通查询，被过滤器挡住，**够不到已删行**）、录音保存的存在性检查（只读 AnyAsync）。
   - 播种器与类别处理器对素材行零写入（V6）；`practice_materials` 表无跨服务引用（V7）。
   - 保存处理器对「仍然已删」的行从不改动：removed 集显式排除 `x.IsDeleted`（:227），`Restore()` 只在行重回提交树时调用（:196）——那之后行已不再是已删状态。
   - 结论：**一行处于已删状态时，它的 UpdatedAt 必等于它最近一次被 MarkDeleted 的时刻**（MarkDeleted 内 Touch(） 写 UtcNow，V3）。若一行曾恢复又再删，UpdatedAt 是最近一次删除——正是回收站「删除时间」应有的语义。故 DTO 字段 `deletedAt` 取 UpdatedAt，不加列。
3. **类别引用是 SetNull**（AssessmentDbContext.cs:174，V14）：类别被硬删时素材 CategoryId 由数据库自动置空——已删素材行不会挂着死类别 id，恢复时至多是「未分类」，无需特殊处理。
4. **名称无唯一约束**（V15）：(UserId, ParentId, SortOrder) 只是普通索引，Name 仅限长 300——同层同名合法存在。故恢复**永不因重名被拒、永不改名**。
5. **素材→录音无外键**（V13）：PracticeRecording.MaterialId 是普通 Guid 列（索引 (UserId, MaterialId, CreatedAt））；录音行独立于素材行存活。素材软删/恢复都不触碰录音行——录音列表按 (UserId, MaterialId, !IsDeleted) 查（PracticeRecordingHandlers.cs:48，V11），与素材的删除状态无关。
6. **素材自引用 FK 是 Cascade**（AssessmentDbContext.cs:145）——只对**硬删**生效；本票不产生任何硬删，该级联不被触发（它只与第 9 节的后续票有关）。

---

## 5. 后端规格

### 5.1 端点（前缀 `api/assessment`，AssessmentController.cs:22；全部 `[Authorize]` + 策略，与既有 practice 端点同口径，V16/V17）

| 方法与路由 | 策略 | 请求 | 响应 | 说明 |
|---|---|---|---|---|
| `GET materials/trash` | MockRead | — | `200 DeletedMaterialDto[]`（裸数组，同 GET categories/recordings 的返回风格） | 我的回收站清单（顶层已删节点，见 5.3） |
| `POST materials/{materialId:guid}/restore` | MockManage | 无 body | `200 { restored: int, treeVersion: string, nodes: MaterialNodeDto[] }` | 恢复该节点（含已删祖先链 + 全部已删后代，见 5.3）；404 口径见 5.4 |
| `POST materials/trash/restore-all` | MockManage | 无 body | `200 { restored: int, treeVersion: string, nodes: MaterialNodeDto[] }` | 恢复我的全部已删行；无已删行时 restored=0 且照常回当前树包（幂等） |

路由不冲突性：`materials/{materialId:guid}` 系列路由带 guid 约束（既有 PATCH mark :377 同形态），字面量 `trash` 不会被 guid 路由吞掉；`trash/restore-all` 与 `{materialId:guid}/restore` 形状不同，无歧义。
处理器落点：Application 层新增 `PracticeMaterialTrashHandlers.cs`（List / Restore / RestoreAll 三个 MediatR 处理器），复用既有 `MaterialTreeVersion.ComputeForUserAsync` 与树拼装逻辑（拼装需与 GetMaterialTreeQueryHandler.Build 同口径——抽共用或复制其私有 Build 的等价实现，以测试钉住两边一致，见 11.1 T8）。控制器只做 RequireUserId + 转发 + Result 映射（同既有端点）。

### 5.2 DTO

```csharp
public sealed record DeletedMaterialDto(
    Guid Id, string Name, bool Folder,
    Guid? ParentId,
    string? Path,            // 祖先名链（不含自身），根项为 null；分隔符 " / "
    Guid? CategoryId,        // 该项所属根的类别（自身是根则取自身）
    string? CategoryName,    // 类别名；类别不存在/未分类为 null
    string? MarkColor,
    DateTimeOffset DeletedAt,        // = 行 UpdatedAt（第 4 节证明）
    int DescendantTotal,             // 全部后代数（删/未删都算）
    int DescendantDeleted,           // 其中已删数
    int RecordingCount);             // 子树内文件所挂未删录音数
```

树包形状与 GET materials 完全一致（`{ treeVersion, nodes }`，MaterialNodeDto 见 PracticeDtos.cs:15），恢复响应在其上加 `restored`——前端按同一解析路径采纳（第 6 节契约 C1）。

### 5.3 处理器语义

**List（GetMaterialTrashQuery）**：
1. 以 `IgnoreQueryFilters()` 载入我的全部素材行（含已删），内存建图（数据规模为个人素材量级，与树查询同策略，V10）。
2. **顶层已删节点** = `IsDeleted && (ParentId == null || 父行不存在 || !父行.IsDeleted)`。只有它们进清单——已删文件夹内部的已删子项不单独列（它们是该文件夹的子树计数的一部分）；父行存活的已删项（在活文件夹里删的单个文件）其父行未删，故照常单独列出。
3. 每项计算 Path（沿 ParentId 上溯至根的祖先名——上溯必须走第 1 步载入的全量图（含已删行）：祖先链可含已删祖先，如「父行存活、祖父行已删」的孤儿链下的顶层项；**不得只在存活行上拼 Path**，否则这类项的路径会被截断；父行不存在的悬空项 Path 以其可达祖先为准，无可达祖先为 null）、CategoryId/Name（取其根祖先的 CategoryId；自身为根取自身）、子树计数（沿全图 BFS 后代）。
4. RecordingCount：子树内全部节点 id 集合上数 `Recordings.Where(UserId==me && !IsDeleted && MaterialId ∈ 集合)`。
5. 排序：`DeletedAt` 降序，同值以 CreatedAt 降序、再以 Id 升序定序（确定性，便于测试钉住）。

**Restore（RestoreMaterialCommand）**——后置条件：**成功后该节点必出现在 GET materials 的树里**（可达且未删）：
1. 以 `IgnoreQueryFilters()` + `Id` + `UserId` + `IsDeleted` 查目标行；查不到 → 404（不存在、他人的、未删的，三者同口径，不泄露存在性——同 SetMaterialMarkCommandHandler 的 NotFound 惯例）。
2. 祖先链修复：沿 ParentId 上溯全图，直至到达根（ParentId == null）或断裂点——遇已删祖先一并 `Restore()`（根向先行）。**断裂点处理**：上溯途中若某节点的父行在全图中不存在（悬空引用，现有应用路径不可达、仅防御），该节点即断裂点——对它 `MoveTo(null, …)` 重根（无论它本身是否已删），使其下整条链（含目标与途中恢复的祖先）随之成为根级可达，再继续恢复。断裂点不限于目标的直接父行：断在链上任意一层同此办理——只重根目标而其祖先仍指向不存在的父行，不满足本节后置条件（多级断裂由 T14 钉住，直接父行缺失由 T13 钉住）。重根不改断裂点节点的 CategoryId（原样保留，若其类别已被硬删则早已被 SetNull 置空，见第 4 节）。
3. 后代恢复：目标的全部已删后代逐个 `Restore()`。
4. 单次 SaveChanges；`restored` = 本次实际翻转的行数（含祖先与后代）。
5. 回树包：`treeVersion` 以 `MaterialTreeVersion.ComputeForUserAsync` 重算，`nodes` 以与树查询同一拼装产出。

**RestoreAll（RestoreAllMaterialsCommand）**：我的全部已删行逐个 `Restore()`，单次 SaveChanges，`restored` = 翻转行数，回同一树包。无已删行 → restored=0、回当前树包（不报错）。

### 5.4 错误与权限

- 三端点一律 `currentUser.RequireUserId()` 取归属（同既有 practice 端点）；查询/命令内所有行访问都带 `UserId == me` 谓词——**回收站不存在任何跨用户可见路径**（验收 T6/T7 钉住）。
- Restore 目标不合资格（不存在/他人/未删）→ `Error.NotFound("material")` → 404 ProblemDetails（全站 Result 口径）。
- 不设 If-Match 钉版：恢复是不可见行上的标记翻转，唯一并发写者是整树保存，而它受 treeVersion 前置比对保护（第 6 节 S2）——与 Knowledge restore 无钉版的先例一致（V18）。

---

## 6. 与整树保存的交互（本票核心：恢复不能是「送回碎纸机」）

**问题**：恢复只是把 IsDeleted 翻回 false。若客户端手里的树状态不含这个恢复项，下一次整树保存会把它当作「未提交的存量节点」再次软删（removed 集判定 `!incoming && !IsDeleted`，:227），且单项删除触发不了熔断（≥5 才拦，:237）。本节证明在现行契约下哪些路径安全、哪条是残余孔，并给出本票必须落地的契约。

**事实链**：
- treeVersion = 可见行集上 `(Id, xmin)` 的 SHA-256 摘要（MaterialTreeVersion，:25/:37，V9）。恢复必然翻转至少一行并 Touch 它（xmin 必变）且可见集增行——**任何一次成功恢复都会改变 treeVersion**（摘要串 S 改变，哈希随之改变；哈希碰撞不在工程考虑内）。
- 保存前置比对：客户端带 TreeVersion 且与现行摘要不符 → 保存被拒（409 concurrency_conflict，**零写入**），响应体附当前 treeVersion（SaveMaterialTreeCommandHandler :128-135；控制器 PUT materials 的 409 分支，AssessmentController.cs:345 起，V8/V16）。
- 客户端版本纪律：PracticeApi 单例持有 treeVersion，GET 时采纳（practice-api.service.ts:255/:261）、保存时携带（:276）、保存成功与 409 时都以响应值续接（:279、:282-284，V23）；保存的两处调用都经 PracticeApi（V24）；409 时 PracticeMaterialTree 回读整树对齐（practice-material-tree.ts:1145 段错误处理）。

**场景判定**：

| 场景 | 结果 | 依据 |
|---|---|---|
| S1 恢复后客户端采纳恢复响应整树（含新版本与新节点），其后保存 | **安全**：恢复项在 payload 内，不进 removed 集 | 契约 C1（下方） |
| S2 恢复后客户端未刷新（另一页签/旧状态），持旧版本保存 | **安全（fail-safe）**：版本不符 → 409 零写入 → 客户端回读后恢复项出现 | 前置比对 :128-135 + V23 |
| S3 客户端以 `TreeVersion=null` 保存且 payload 不含恢复项（旧调用方兼容路径：SaveMaterialTreeCommand.TreeVersion 为 null 时跳过比对） | **会被再软删**——残余孔 | 命令定义（PracticeMaterialHandlers.cs SaveMaterialTreeCommand 注释：null = 旧调用方不校验）+ :227 |
| S4 「父删子活」孤儿行（未删但不可达，不在回收站清单内——它没被删） | 不属恢复对象；其已删祖先被恢复时它自动重新挂回（拼树恢复可达）；若祖先一直不恢复，它会在下一次整树保存时被转入 removed 集软删（**数据不丢**，转为祖先子树的一部分进入回收站计数）——此为既有保存语义，非本票引入 | Build :68 + :227 |

**本票契约（实现必须逐条满足，验收逐条钉住）**：
- **C1**：两个恢复端点的响应必含恢复后的**完整树包**；前端 PracticeApi 新增恢复方法，成功时以响应 `{treeVersion, nodes}` **整体采纳**（与 getMaterials 同一采纳路径），PracticeMaterialTree 以 nodes 重置本地树状态。**只采纳版本号、不采纳节点，视为未实现本契约**——那会让「版本已对齐、payload 缺恢复项」的下一次保存畅通无阻地把它再删掉（S3 在本页内重演）。
- **C2**：回收站 UI 与材料树同属 Practice 页状态域：恢复动作只能经 PracticeMaterialTree / PracticeApi 的既有状态通道发起，不许在对话框里另起一套独立的树状态或直接裸调 ApiClient。
- **C3**：恢复成功后 `treeDirty=false` 且 `treeDeletedSinceSave=false`——恢复不是删除，绝不允许它把下一次保存的 force 打开（force 是绕过熔断的通道，见 1.2）。
- **残余孔处置**：S3 的 null-version 路径在本票**不堵**——仓内唯一保存调用方恒带版本（V24），页面生命周期内 GET 后 PracticeApi 的版本恒非空；堵它要改保存命令的兼容契约（拒绝 null 版本），影响面是全部旧调用方，属保存路径变更，按 D7 同理列后续票候选。本票的义务是把孔写明（本节即是），不许假装它不存在。

---

## 7. 恢复语义细则

1. **文件夹恢复** = 文件夹自身 + 其全部已删后代（D3）。副作用如实登记：在文件夹被删之前就已单独删除的子项（当时它自成一条回收站清单项），在文件夹被删后会折入该文件夹的子树计数；恢复文件夹时它会**一并复活**。理由：删除批次在数据上无标识（无 DeletedAt、无批次 id，见第 4 节），无法区分「随文件夹一起删的」与「先前单独删的」；以子树为恢复粒度与删除机制（整树保存按子树标记）对称，且复活后用户可立即再删。清单项的 DescendantDeleted 计数让这一粒度在恢复前就可见（第 5.2 节）。
2. **单个文件恢复**：其父链必然全存活（否则它不是顶层项、不会被单独列出）；恢复后回到原父文件夹下、原 SortOrder 位置（SortOrder/IsExpanded/MarkColor/Content 全部原样保留——恢复只翻 IsDeleted 并 Touch UpdatedAt，实体 Restore(） 语义，V3）。树查询排序为 SortOrder 后 CreatedAt（GetMaterialTreeQueryHandler），同序位冲突由既有排序自然消化。
3. **录音随恢复复现**：恢复不触碰任何录音行（第 4.5 条）；文件/文件夹恢复后，前端按 MaterialId 拉录音列表即得回原录音与评分（ListRecordings 口径 V11；事故复现中已实证「树与挂在其下的录音立即可见」）。验收 E4/T3 钉住：恢复前后录音 id 集合与评分内容逐一相等。
4. **类别**：根项恢复后回到其 CategoryId 指向的类别（类别还在时）；类别已被删的，CategoryId 早已被 SetNull 清空（V14），以「未分类」回来。非根项的类别跟随其根祖先（既有口径，不变）。
5. **已删祖先链上的单项恢复**（API 层可达、UI 不提供入口）：按 5.3 Restore 第 2 步连祖先一并恢复，保证后置条件（节点必现于树）。UI 清单只列顶层项，此路径只为端点语义完备与防御悬空/深层 id 而定。
6. **重复恢复**：对已不在回收站的行调用 restore → 404（5.4）；前端在恢复成功后即刷新清单，该行消失，不存在双击双恢复的界面路径。

---

## 8. 前端规格

落点总原则：全部 additive；形态镜像 Tech Stack 回收站的已上线先例（V20）与材料面板既有工具行/对话框惯例，不引入新设计语言、新颜色或新组件库。

### 8.1 入口（材料面板工具行）

- 位置：共享树组件头部工具行 `th-tools`（material-tree.component.html:43，新建文件夹/新建文件/展开收起同行）末尾，加一个 `mat-icon-button.head-tool`：图标 `restore_from_trash`，`matTooltip` 与 aria-label 均为 `t('practice.trash.title')`。该组件全仓仅在 Practice 页实例化一处（ai-practice.component.html:57，V25），入口天然只出现在 Practice。
- 组件契约（沿类别管理的既有 emit 模式）：MaterialTreeComponent 新增 `@Input() trashCount = 0` 与 `@Output() trashOpen = new EventEmitter<void>()`；宿主在 ai-practice.component.html 的 `<app-material-tree>` 上绑定 `[trashCount]` 与 `(trashOpen)`。
- **计数徽标**：trashCount > 0 时按钮右上角显示计数小徽标（纯数字，>99 显示 99+）；= 0 时不显示。理由：事故里用户对「135 条已删素材躺在库里」毫无感知——入口必须在有内容时自己说话。计数来源 = 5.1 的 trash 列表长度（顶层项数，不是行数总数）。
- 计数刷新时机：材料树 restoreTree() 并行加载（与 loadCategories 同模式：失败不阻塞树、徽标保持旧值/0 且不报错）；每次整树保存成功后（删除可能新增已删行）；每次恢复成功后（以恢复响应或重拉列表更新）。

### 8.2 回收站对话框

- 新组件 `web/src/app/features/ai-practice/material-trash-dialog.component.{ts,html,scss}`，形态镜像同目录 `material-transfer-dialog.component.*` 的对话框惯例；由 PracticeMaterialTree 持有开闭与数据（C2），经 PracticeApi 调用（8.3）。
- 结构：标题行（`practice.trash.title` + 顶层项总数 + 「全部恢复」按钮[仅当顶层项 ≥2 时显示] + 关闭按钮）→ 列表 → 空态/错误态。
- 列表项（一行一项，字段与 DeletedMaterialDto 对应）：
  - 类型图标：文件夹 `folder` / 文件 `description`（mat-icon，与树内图标语义一致）；
  - 主行：Name（溢出省略）；
  - 元信息行：Path（有祖先时显示，不含自身名）→ 根项显示类别名（CategoryName，null 显示 `practice.categoryUncategorized` 既有键的文案）→ `practice.trash.deletedAt` + 本地时间格式 `yyyy-MM-dd HH:mm`；
  - 子树提示（仅当 DescendantTotal > 0）：`practice.trash.containsItems`（{count}=DescendantTotal）；录音提示（仅当 RecordingCount > 0）：`practice.trash.containsRecordings`（{count}）；
  - 右侧：恢复按钮（mat-stroked-button + mat-icon `restore` + `practice.trash.restore`，与 ts.trash 恢复按钮同形态，V20）。
- 交互：点恢复 → 调 restore → 成功后 toast `practice.trash.restored`、该项即时从清单移除、树按 C1 采纳、徽标更新；点全部恢复 → 同理（toast `practice.trash.restoredAll`、清单转空态）。恢复请求失败 → 行内/对话框级错误文案（`practice.trash.actionFailed`），清单不假装变化。
- 空态：`practice.trash.empty` 一行文案（镜像 ts.trash.empty，V20/V21）。
- 加载失败态：`practice.trash.loadFailed` + 重试按钮——**绝不把加载失败渲染成空态**（事故教训：空态文案只许出现在请求成功且确实为空时；树的 treeUnavailable 纪律同源）。

### 8.3 PracticeApi 增量（core/api/practice-api.service.ts，与既有方法同风格）

- `getMaterialTrash(): Observable<DeletedMaterialDto[]>` → GET `/materials/trash`；前端 DTO 接口与 5.2 逐字段对齐（camelCase）。
- `restoreMaterial(id): Observable<{ restored: number; treeVersion: string; nodes: MaterialNodeDto[] }>` → POST `/materials/{id}/restore`；`restoreAllMaterials(): Observable<{ restored: number; treeVersion: string; nodes: MaterialNodeDto[] }>` → POST `/materials/trash/restore-all`。两者响应都带树包（与 §5.1 的 HTTP 响应形一致）——**在 service 层即完成 C1 采纳**（把响应 treeVersion 写回 this.treeVersion，并把 nodes 经与 getMaterials 相同的 map 路径返回给调用方），使任何调用方都不可能「只拿计数、忘了树」。
- PracticeMaterialTree 恢复成功后以返回 nodes 走既有 fromDto 重置本地树（同 refreshTreeFromServer 的对齐路径），并执行 C3（treeDirty/treeDeletedSinceSave 复位）。

### 8.4 树空时的回收站指针（事故的直接修复点）

材料树处于 treeEmpty 且 trashCount > 0 时，在既有 emptyHint 文案（`practice.emptyHint`，i18n.service.ts:228；经 material-tree 组件 emptyHint 输入渲染）**下方追加一行**：文案 `practice.trash.hintInEmpty`（{count} = 顶层项数）+ 点击打开回收站（同一 trashOpen 通道）。trashCount = 0 时该行不渲染、空态与现行完全一致（不变项）。追加行是新增 DOM 块，不改既有 hint 的文本与显示条件。

### 8.5 i18n 键（全部新增 `practice.trash.*`，三语 {zh, en, fr} 内联——字典形态 V21、插值口径 V22；title/empty/restore/restored 四键与 ts.trash.* :2083-2086 逐字同文）

| 键 | zh | en | fr |
|---|---|---|---|
| practice.trash.title | 回收站 | Trash | Corbeille |
| practice.trash.empty | 回收站是空的 | Trash is empty | La corbeille est vide |
| practice.trash.restore | 恢复 | Restore | Restaurer |
| practice.trash.restored | 已恢复 | Restored | Restauré |
| practice.trash.restoreAll | 全部恢复 | Restore all | Tout restaurer |
| practice.trash.restoredAll | 全部已恢复 | All restored | Tout a été restauré |
| practice.trash.containsItems | 含子项 {count} 个 | Contains {count} item(s) | Contient {count} élément(s) |
| practice.trash.containsRecordings | 含录音 {count} 条 | Contains {count} recording(s) | Contient {count} enregistrement(s) |
| practice.trash.deletedAt | 删除于 | Deleted | Supprimé le |
| practice.trash.loadFailed | 回收站加载失败 | Failed to load trash | Échec du chargement de la corbeille |
| practice.trash.actionFailed | 操作失败，请重试 | Action failed, please retry | Échec de l'action, veuillez réessayer |
| practice.trash.hintInEmpty | 回收站里有 {count} 个已删除的素材，可恢复 | {count} deleted material(s) in Trash can be restored | {count} élément(s) supprimé(s) dans la corbeille peuvent être restaurés |

三语缺一即未完成（验收 F4 逐键核对 zh/en/fr 均非空且 en/fr 不得与 en 雷同占位）。

---

## 9. 永久删除：不在本票（D4）

决定：本票不做「彻底删除/清空回收站」。理由：
1. **需求不对位**：本票要解决的是「误删后无路可恢复」，不是「删得不够干净」。事故用户要的是找回来，不是删彻底。
2. **先例**：全仓唯一的既有回收站（Knowledge）是恢复-only，其应用层**没有任何硬删**（V19）——本票与之对齐，不另开风格。
3. **级联面未设计清**：素材彻底删牵涉——其子树行、挂着的录音行与 1:1 评分行、磁盘上的音频文件（删文件机制有先例 `IAudioStore.DeleteAsync`，IAudioStore.cs:29，已被录音硬删使用，V12）、以及素材自引用 FK 的 Cascade（AssessmentDbContext.cs:145）与「录音无外键」的不对称（V13）。不可逆操作一旦实现有误，损失的正是本票要保护的数据类别；它值得一份独立规格（含文件清理顺序、孤儿录音处置、二次确认与不可恢复文案）单独过门。
4. **不做的代价可控**：已删行只是文本行（含正文），个人量级下磁盘/性能影响可忽略；回收站清单按删除时间倒序，真需要清理时后续票再补。

后续票候选（名称先行登记，不在本票实现）：「素材彻底删除：回收站内单项/清空，含录音行、评分行与磁盘文件级联清理，二次确认」。

## 10. 熔断加固：不在本票（D7）

决定：整树保存的防误删熔断（≥5 且 >80% 且未 force 才拦，PracticeMaterialHandlers.cs:237）本票**不改**。理由：
1. 事故的失效路径不是阈值松紧，而是 **force 通道**：客户端在「本编辑期删过任何节点」时保存即自动带 force（V27），而事故恰是「状态残缺的整树 + force」——调阈值拦不住它。
2. 真正的加固是**保存契约再设计**（如：删除意图逐项显式化、服务端保存前差异预览、force 需带删除清单摘要核对）——那是全应用最危险的写路径（整树覆盖），改动面与回归面都远超一个恢复面功能；与本票捆绑会让两件事都过不了门。
3. 本票落地后，风险等式已变：任何整树事故的结果从「永久丢失、靠人工 UPDATE 抢救」变为「打开回收站一键恢复」——先有恢复兜底，再从容做预防 Redesign，顺序正确。
4. 已有的另一层防线（treeVersion 前置比对，票 111）已拦住「陈旧状态覆盖」一类（第 6 节 S2），剩余事故面主要是「用户在确认框点了继续」——产品提示层面的事，同样留给后续票连同 force 契约一起设计。

后续票候选：「整树保存删除意图显式化（force 携带删除清单 + 服务端核对）」。

---

## 11. 验收

### 11.0 需求条目（验收逐条对，下表 ID 在 11.1–11.3 中被引用）

| ID | 需求 | 可测判据（摘要） |
|---|---|---|
| R1 | 看见：回收站列出我的全部顶层已删素材及元数据 | T1/T2 + E2：清单集合、字段、排序与库内状态逐项一致 |
| R2 | 单个文件可恢复，内容/标记/类别/位置原样 | T3 + E3 |
| R3 | 文件夹可连子树恢复；「父删子活」子项随祖先恢复重现 | T4 + E5 |
| R4 | 全部恢复一键清空回收站并整树复现 | T7 + E6（事故重演） |
| R5 | 恢复后录音与评分原样复现 | T3 + E4 |
| R6 | 恢复项不会被后续整树保存静默再删；陈旧保存被 409 拦住 | T9/T10 + E7 |
| R7 | 跨用户零泄露（清单/恢复均然） | T6 + E8 |
| R8 | 空回收站有空态；树空且回收站非空时有指针行；加载失败不冒充空态 | F3/F5 + E2/E6 |
| R9 | 三语齐全 | F4 |
| R10 | 既有契约不变（见第 12 节不变项逐条） | 存量套件全绿 + T8 |

### 11.1 后端测试（新增 `src/Services.Assessment.Tests/PracticeMaterialTrashHandlerTests.cs`，真实 PG——与 PracticeCategoryHandlerTests.cs:41 的 UseNpgsql 口径一致，V28；新处理器行覆盖 ≥80%，总纲第七节）

- **T1** 清单集合与顶层规则：造两层树后删文件夹（整子树已删）+ 在活文件夹内删单文件 → 清单恰含 {文件夹， 单文件} 两项；文件夹项 DescendantTotal/DescendantDeleted 与造数一致；单文件项 Path = 其父文件夹名。
- **T2** 排序与 deletedAt：三项不同时刻删除 → 清单按 UpdatedAt 降序；DeletedAt 字段等于行 UpdatedAt。
- **T3** 恢复单文件：恢复后 GET 树含该文件（Content/MarkColor/CategoryId/SortOrder 与删前逐字段相等）；其录音列表返回删前同一批录音 id 与评分（R5）。
- **T4** 恢复文件夹：全部已删后代翻转为未删；「父删子活」造数（子项未删、父已删）下恢复父文件夹后子项在树中可见。
- **T5** 祖先链恢复：直接对深层已删子项 id 调 restore → 其已删祖先一并恢复、该项出现在树里（后置条件断言走真实树查询）。
- **T6** 隔离与 404：用户 B 的清单为空；B 对 A 的已删项调 restore → 404；A 对未删项调 restore → 404；A 对不存在 id 调 restore → 404（三者同口径）。
- **T7** 全部恢复：多项已删 → restore-all 后清单为空、树完整；再调一次 → restored=0 不报错。
- **T8** 树包一致性：恢复响应的 treeVersion 与紧随的 GET materials 的 treeVersion 逐字相等；nodes 与 GET 的 nodes 深度相等（钉住两处拼装同口径）（R10）。
- **T9** 不再被碎纸（核心回归）：恢复一项后，以恢复响应的树为 payload 整树保存（模拟 C1 采纳）→ 该项仍未删、树查询仍含它（R6/S1）。
- **T10** 陈旧保存被拦：恢复前取得的 treeVersion 在恢复后提交整树保存 → 409 concurrency_conflict 且库内行状态零变化（R6/S2）。
- **T11** 同名不冲突：活文件夹内有同名文件时恢复已删同名文件 → 成功，两行并存。
- **T12** 类别已删：根项所属类别被硬删后恢复该项 → 回到未分类（CategoryId 为 null）且不报错。
- **T13** 悬空父行（直接父行缺失）：造数——素材自引用 FK 在模型快照中为 Cascade（AssessmentDbContextModelSnapshot.cs:586-589，V30），真实 PG 上普通删父行必级联删子行、造不出悬空态；故在测试的同一数据库会话内先执行 `SET session_replication_role = 'replica'`（关闭引用完整性触发器），以原生 SQL 直删父行，再执行 `SET session_replication_role = 'origin'` 复位，此后步骤走正常应用路径。对其已删子项 restore → 子项被重根（ParentId=null）并出现在树根级。
- **T14** 多级断裂（断裂点在直接父行之上，钉 §5.3 后置条件）：造三层链 R→M→T（T 已删，M 存活或已删），以 T13 同法物理移除 M 的父行 R。对 T 调 restore → 断裂点 M 被重根（ParentId=null），且 T 出现在 GET materials 的树里 M 之下——后置条件「成功后该节点必出现在树里」在多级断裂下同样成立。
- 端点权限：`AssessmentEndpointPermissionsTests.cs` 增三端点的策略钉测（GET=MockRead、两 POST=MockManage；无权策略集调用被拒），与存量端点同表对齐。
- 存量回归：SoftDeleteFilterTests / Ticket111TreeVersionTests / Ticket111DeleteTests / Ticket113ListDeleteTests 等全套 Assessment.Tests + Assessment 构建零警告升级（以 CI 为准）。

### 11.2 前端测试（Jest，spec 与源码同目录——仓内惯例）

- **F1** PracticeApi: `practice-api-trash.spec.ts`——三方法的 URL/方法逐字对；restore/restore-all 成功时 this.treeVersion 被响应值更新（以可观察的后续 saveMaterials 携带新版本断言）；错误原样上抛。
- **F2** 对话框逻辑：给定 DTO 列表 → 行渲染字段（图标/名/路径/删除时间/计数提示显隐条件）以纯函数/组件 spec 钉住：DescendantTotal=0 不显示子项提示、RecordingCount=0 不显示录音提示。
- **F3** 空态与错误态：清单 [] → 显示 practice.trash.empty；请求失败 → 显示 loadFailed + 重试，**不**显示 empty（R8）。
- **F4** i18n：逐键断言 8.5 表 12 个键在 zh/en/fr 三语均存在且非空（R9）。
- **F5** 徽标与指针：trashCount=0 时徽标不渲染、指针行不渲染；>0 时徽标数字正确、treeEmpty 下指针行渲染且点击触发 trashOpen（组件 spec + 模板断言，R8）。
- **F6** C3 复位：恢复成功回调后 treeDirty 与 treeDeletedSinceSave 均为 false（PracticeMaterialTree spec）。

### 11.3 活体 E2E（沙盒全栈 + 真实浏览器 + 截图存证；数据经应用自身的 UI/API 造，不直写库）

- **E1 备数**：建两层树（文件夹 A 含文件 a1、a2；根文件 r1；文件夹 B 含文件 b1）,给 a1 录一段音并评分；截图基线树。
- **E2 删单项见回收站**:a2 在编辑模式删除并保存 → 打开回收站：清单含 a2（路径显示 A）、徽标计数正确；截图。
- **E3 恢复单项**:E2 中恢复 a2 → 树里 a2 回到 A 下原位；对话框清单少了 a2;toast 出现；截图。
- **E4 录音复现**：删除 a1 并保存 → 恢复 a1 → 打开 a1：录音列表含 E1 的录音、可播放、评分可见（R5）。
- **E5 删文件夹连子树**：删除文件夹 A 并保存 → 回收站清单只有 A 一项代表整子树（含子项计数 = a1+a2），a1/a2 不单独出现 → 恢复 A → 整子树复现。
- **E6 事故重演（核心）**：经整树保存把全树清空（force 路径）→ 树空、emptyHint 出现**且下方有回收站指针行**（截图量测：指针行可见）→ 打开回收站 → 「全部恢复」→ 整棵树（含全部文件夹/文件）复现，a1 录音仍在（R4/R8）。
- **E7 恢复后继续编辑（防碎纸核心）**：紧接 E6 不刷新页面，直接在树上改名任一节点并保存 → 保存成功、被恢复的其余节点仍在（刷新复核一致）（R6/C1）。
- **E8 隔离**：换第二个用户登录 → 回收站为空、徽标不显示；其树为空且无指针行（R7）。
- 全程 pageerror = 0；每步截图存 `~/workspace/ticket124-recognition/` 同级证据目录（实现票自定路径并在台账登记）。

---

## 12. 影响面 + 不变项

**影响面**（改动所及，验收须连带回归）:
- 后端：AssessmentController（+3 端点）、Application 新增处理器文件、Assessment.Tests（新增 + 权限表扩展）。
- 前端：practice-api.service.ts（+3 方法）、material-tree.component（头部 +1 按钮、+2 个 IO、空态追加行）、practice-material-tree.ts（回收站状态/计数/恢复编排）、ai-practice.component.html（两处绑定）、新增 trash 对话框组件、i18n.service.ts（+12 键）。
- 数据：零表结构变更；恢复动作只改既有行的 IsDeleted/UpdatedAt。

**不变项**（逐条，交付时逐条打勾）:
1. GET / PUT `/materials` 的请求/响应形状、treeVersion 算法、409 行为——不变。
2. 查询过滤器（AssessmentDbContext.cs:137）与保存处理器的 removed/熔断逻辑与文案——一字不改。
3. 删除的既有 UX（编辑模式删节点 → 保存生效）与 force 语义——不变。
4. PracticeMaterial 实体、表结构、迁移集——不变（零迁移）。
5. Tech Stack 回收站（Knowledge 端点与 UI）——不碰。
6. 录音的删除语义（硬删 + 删文件）与录音端点——不变。
7. treeEmpty 时的既有 emptyHint 文案与显示条件——不变（8.4 只是其下追加新行，且仅在 trashCount>0 时）。

## 13. 风险与诚实残余

1. **S3 残余孔**（第 6 节）：null-version 的旧调用方保存可再删恢复项。本票以契约 C1–C3 关闭仓内现实路径，并把服务端堵孔（拒绝 null 版本）明确留给后续保存契约票。不接受「顺手在保存处理器里特判恢复时间窗」的修法——那是在最高危写路径上加无规格的启发式。
2. **子树恢复的折入效应**（7.1）：先前单独删除的子项会随其文件夹恢复一并复活。已用计数透明化 + 可立即再删缓解；若用户反馈强烈，后续票再考虑给 MarkDeleted 加批次标识（需迁移，独立评估）。
3. **deletedAt 精度**：UpdatedAt 是「最近一次被标记删除」的时刻（第 4 节已证），不是独立审计字段；同一秒内多批删除在清单里以 CreatedAt/Id 定序区分，不影响恢复正确性。
4. **规模假设**：清单与拼树都是全量内存处理，与现行树查询同策略（个人素材量级，事故样本 135 行）；若未来素材量级跃升，清单端点再补分页——本票不为未出现的规模买单。

---

## 附录

- 验证附录（可复跑）: `evidence/usage-traces.md`(V1–V30）——本规格每条承重断言的命令与观察值，评分者重跑即可复核。
- 事故报告：`~/workspace/incident-practice-empty/REPORT.md`。
- 评分史：R1 独立评分 **9.87/10 FAIL**（`SCORE-R1.md`；扣分 K1–K4 均为定点文字瑕疵，设计决策 D1–D8 未被触动）。**R2 修订映射**（本版，仅文字、设计未动）：K1 → §3 普查表 Knowledge 行补全三个软删实体（KnowledgeItem/TechStackCategory/ReviewActivityDay）及各自处置；K2 → §5.3 Restore 第 2 步改述为全链断裂点重根、§11.1 T13 补造数方法（`session_replication_role` 绕 Cascade FK，新增附录 V30）并新增 T14 钉多级断裂后置条件；K3 → §8.3 两处签名改为完整树包响应形；K4 → §5.3 List 第 3 步 Path 括注改述为全量图（含已删祖先）口径。
