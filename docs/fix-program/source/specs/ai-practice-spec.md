# AI Practice（口语/材料练习）现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。
> 边界先行：AI Practice ≠ AI Mock。Practice = 拿自备材料（朗读/跟读/复述）练口语 + 发音评测；Mock = AI 当面试官出题问答评分（见 `ai-mock-spec.md`）。两者同住 Assessment 服务，但实体与端点完全分开（后端领域注释明示此区分，`src/Services.Assessment/Domain/AssessmentAggregates.cs` 文件头）。

## 模块职责
- 路由 `/practice`：材料树（文件夹/文件）管理、材料分类、录音跟读、Azure 发音评测、TTS 合成播放、从 Tech Stack 导入题目为材料。
- 前端 `web/src/app/features/ai-practice/ai-practice.component.ts`（3645 行单类，全仓最大，见冲突 CONF-AP-2）；后端 Assessment 服务 `PracticeHandlers.cs` / `PracticeCategoryHandlers.cs`，端点前缀 `api/assessment` 的 materials/categories/recordings/speech/tts/storage 组（`src/Services.Assessment/Api/AssessmentController.cs:275-738`）。

## 核心实体与数据真源（均在 Assessment 库，按 UserId 隔离）
- **PracticeMaterial**（`src/Services.Assessment/Domain/PracticeMaterials.cs:27`）：树节点，Kind=Folder/File（同文件 :19）、ParentId、Name、Content（文件正文）、SortOrder、MarkColor、CategoryId、软删 IsDeleted。保存语义 = **整树覆盖保存**（`PUT /api/assessment/materials`）：接受前端客户端 id 做幂等 upsert，避免「删了重建」丢数据（实体构造注释 :47-64 明示此历史教训）。
- **PracticeCategory**（`Domain/PracticeCategory.cs`）：UserId/Name/SortOrder/**LinkedSource**（可关联来源，如 Tech Stack 分类，迁移 `20261017120000_AddCategoryLinkedSource`）。
- **PracticeRecording + PracticeRecordingScore**（`PracticeMaterials.cs`）：录音存文件系统（StoragePath），评分存 Azure 发音五维（Pron/Accuracy/Fluency/Completeness/Prosody）+ RecognizedText + 计费秒/字节（ScoreBilling 迁移）。
- **SpeechSetting**（同文件）：Azure Speech Key/Region/Endpoint，**只存服务端、绝不下发浏览器明文**（前端 `ai-setting` 页只提交与看掩码/配置状态）。
- **PracticeTtsCache**：TTS 音频按 text+voice+lang 缓存（服务端表 + 前端 IndexedDB `your-interview-tts`，`features/ai-practice/tts-audio-cache.ts`；Playbook 备考/实录播放复用同一 TTS 通道，见 Playbook 定稿 §4.1）。
- **UserStorageSetting**：录音存储根路径偏好（端点 `storage/settings`，仅要求登录、按 UserId 隔离，`AssessmentController.cs:709-738`）；其 UI 入口在 **Profile 页**不在 Practice 页（`web/src/app/features/profile/profile.component.ts:346`）。
- 与 Tech Stack 的关系：材料可由 Tech Stack 条目转入（`material-transfer.ts` / `material-transfer-dialog.component.ts`，节点 id 前缀 `ts:item:` 标识来源）——转入后是 PracticeMaterial 副本，不是 KnowledgeItem 的视图（见冲突 CONF-AP-1）。
- 前端 localStorage：`user_practice_zoom`（问题/答案文本缩放，**与 Playbook 实录页共用同一键**，Playbook 定稿 §4.2 定规）、`practice.lang`、`active_practice_material_id`——均为偏好/游标，非领域数据。

## 主要流程
1. 进页拉材料树（`GET materials`）+ 分类（`GET categories`）+ 语音设置状态（`GET speech/settings`）。
2. 编辑材料树 → 整树 PUT 保存（带自上次保存的删除集）；改名/标记走 `PATCH materials/{id}/mark` 等。
3. 跟读：录音 → 上传 recording → `POST recordings/{id}/score` 或 `POST pronunciation/assess`（Azure）→ 五维分回显；TTS：`POST /api/assessment/tts` 合成、缓存命中直接播。

## 与既定规则对照结论
- 凭据单点保管：Speech key 在 Assessment、LLM key 在 AiGateway（见 `admin-ai-settings-spec.md`），前端不持明文——符合。
- 缩放共用键、TTS 共用通道：符合 Playbook 定稿的跨模块复用口径。
- 迁移守卫（REQ-01 的 RemoveAiSettings 裸 SQL）：票 02 已加守卫口径，现状以票 02 结论为准，本规格不重复判定。

## 现状与规则冲突
- **CONF-AP-1（→ 同源规则 / AUDIT ARC-07 同型，无独立 AUDIT 编号，索引至 REQ-16）**：Tech Stack 题目转入 Practice 后形成 PracticeMaterial **副本**，此后两边各自编辑不回同步——与「同一份底层数据」规则存在张力。现状定性：转入是「生成练习材料」的一次性拷贝（与 Playbook 备考题直引 KnowledgeItem 的做法不同），改动 Practice 材料前须知改动**不会**回写 Tech Stack。
- **CONF-AP-2（→ AUDIT ARC-02，票 53 未实施）**：前端 3645 行单类组件，录音/评分/TTS/素材树/缩放五职责耦合，无 seam——结构现状记录，功能口径不受影响。
- **CONF-AP-3（→ AUDIT COD-15，已修复，备查）**：TTS/评分写端点曾挂 MockRead；票 25/86 已改挂 MockAnswer/MockManage，现行 `AssessmentController.cs:480`（score POST = MockAnswer）、`:579`（speech/test = MockManage）——本条仅作索引留痕，非现行冲突。
