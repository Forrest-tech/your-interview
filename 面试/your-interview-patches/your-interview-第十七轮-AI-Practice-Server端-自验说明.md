# 第十七轮:AI Practice Server 端 —— 自验说明

日期:2026-09-15(美东)
需求来源:Forrest 五项(微服务 / 数据入库 / AI 语音设置 / 录音+评分持久化 / 本地直跑不 Docker)

---

## 一、需求 → 实现对照

| # | 需求 | 实现 | 位置 |
|---|------|------|------|
| 1 | 与其他 server 一致的微服务,可独立开发/部署/测试 | 复用既有 `src/Services.Assessment`(四层、端口 5266、独占 PG schema `assessment`、网关已挂 `/api/assessment/**`) | 已有骨架,本轮扩展 |
| 2 | 页面数据存 PostgreSQL,建表(含 materials 结构+内容) | 新表 `practice_materials`(邻接表 ParentId + SortOrder,存 name/content/folder/expanded) | `Domain/PracticeMaterials.cs` + 迁移 |
| 3 | AI 语音设置:可配 Azure key;AI scoring 用它;示范朗读也可用它 | `GET/PUT /speech/settings` + `POST /speech/test`(key 只进不出);`POST /tts` 走 Azure Neural TTS | `Controller` + `SpeechKeyProvider` + `SpeechSynthesizer` |
| 4 | 每次录音后保存录音+分析结果;避免重复评分 | 混合方案:音频字节落磁盘(`IAudioStore`),元数据+评分落 PG(`practice_recordings` / `practice_recording_scores` 1:1) | `IAudioStore` + handlers |
| 5 | 先不用 Docker,本地直跑 | `bash tools/dev.sh practice` —— 只起 PG + RabbitMQ + assessment + gateway | `tools/dev.sh` |

---

## 二、为什么录音用"混合方案"(磁盘 + 数据库)

**音频字节 → 磁盘文件**
- 录音是二进制大对象。塞进 PG 的 `bytea` 会让库体积迅速膨胀、`pg_dump` 备份变慢变巨、WAL 写入放大。
- 文件系统天生适合顺序读写大文件;后续要换 S3 / Azure Blob,只需换一个 `IAudioStore` 实现,**调用方一行不用改**。
- 路径:`{Storage:RootDirectory}/recordings/{userId}/{yyyy}/{MM}/{recordingId}.webm`
- 解析优先级:`Storage:RootDirectory` 配置 → `PRACTICE_STORAGE_DIR` 环境变量 → 当前工作目录 `storage/recordings`(**可预测**,不用临时目录,否则重启找不到文件)。

**元数据 + 评分 → 数据库**
- 要按用户/素材查询、要缓存评分、要统计趋势 —— 这些是关系型的强项。
- `practice_recording_scores` 与录音 1:1,**评分落库 = 需求第 4 条的"下次不必重复评分"**:前端 `grade()` 先 `GET /recordings/{id}/score` 读缓存,命中就直接显示,不再花 Azure 额度。

---

## 三、Forrest 的 Mac 上怎么跑起来

### 步骤 1:建库表(执行迁移)
```bash
cd /Users/jadenfly/.openclaw/dev/your-interview
bash tools/dev.sh practice
```
`practice` 会:起 PostgreSQL(5433)+ RabbitMQ → 构建并启动 assessment + gateway → 自动执行 EF 迁移(4 张新表)。

> 若 Assessment 启动日志里出现迁移相关报错,单独看:`tail -f .logs/assessment.log`

### 步骤 2:前端
```bash
cd web && npm start
```
浏览器打开 http://127.0.0.1:4200/practice

### 步骤 3:配置 Azure 语音 key(让评分与 Azure 示范朗读可用)
打开 http://127.0.0.1:4200/account/ai-setting,填入 key + region(`canadacentral`)→ 保存。
保存后**会自动做一次真连通性测试**;失败会如实报 401/403 与原因,不会假装成功。
key 存进数据库(不回传浏览器),生效优先级:**数据库 > 环境变量 > appsettings**,所以保存后**无需重启服务**。

### 步骤 4:自测
```bash
bash tools/e2e.sh assessment
```
新增覆盖:素材树读/写/回读验证、录音列表、评分缓存 404、语音设置状态、
**TTS 未配 key 必须 503(不是 200 假成功)**、空文本被拒、经网关读素材树。

---

## 四、诚实边界(本次严格遵守,未打任何桩)

1. **Free F0 只返回 AccuracyScore** → Fluency/Completeness/Prosody 一律 `null`,界面显示"—"。**绝不用 0 冒充**。
2. **Azure 发音评估不返回音标** → `phonetic()` 返回 `null`,界面显示"暂无音标"。**绝不编造音标**。
3. **无模拟评分兜底** → 拿不到真实结果就报错,不造数字。
4. **密钥保存不假装成功** → 保存后真测;测不过如实报错。
5. **TTS 回退不冒充** → Azure 未配 key 时后端 503,前端回退浏览器语音并**在界面显示** "Azure 语音未配置,已回退浏览器语音"。绝不让人以为听到的是 Azure 人声。
6. **素材树完成状态** → 没有真实数据源,不硬编码绿勾。

---

## 五、本轮改动的文件清单

### 后端(新增)
- `src/Services.Assessment/Domain/PracticeMaterials.cs` — 4 个实体
- `src/Services.Assessment/Application/PracticeHandlers.cs` — 10 个 handler + DTO
- `src/Services.Assessment/Infrastructure/Storage/IAudioStore.cs` — `IAudioStore` + `LocalAudioStore`
- `src/Services.Assessment/Infrastructure/Storage/SpeechKeyProvider.cs` — key 运行期解析(DB>env>appsettings)
- `src/Services.Assessment/Infrastructure/Persistence/Migrations/20260915220000_PracticeMaterialsAndRecordings.cs`(+ `.Designer.cs`)

### 后端(修改)
- `Infrastructure/Persistence/AssessmentDbContext.cs` — 4 DbSet + 映射
- `Infrastructure/Persistence/Migrations/AssessmentDbContextModelSnapshot.cs` — 同步
- `Api/AssessmentController.cs` — 新增 13 个端点
- `Application/PronunciationAssessor.cs` — `PingAsync` + `AzureAuthException` + `SpeechSynthesizer`
- `Program.cs` — 注册 `IAudioStore` / `ISpeechKeyProvider` / `SpeechSynthesizer`
- `appsettings.json` — 补 `Storage:RootDirectory`

### 前端(新增)
- `web/src/app/core/api/practice-api.service.ts` — 后端访问层(8 个端点,d.ts 类型齐全)

### 前端(修改)
- `web/src/app/core/api/api-client.ts` — 补 `postBlob` / `postForm`
- `web/src/app/features/ai-setting/ai-setting.component.ts` — `backendReady` false→true,真调用三接口
- `web/src/app/features/ai-practice/ai-practice.component.{ts,html,scss}` — 素材树接后端(去 localStorage)、TTS 真调、播放走后端流、`ttsNote` 诚实标记
- `web/src/app/core/recorder/recorder.service.ts` — 录音上传/载入/删除/评分落库

### 工具
- `tools/dev.sh` — 新增 `practice` 子命令
- `tools/e2e.sh` — assessment 段扩展练习链路自测

---

## 六、已完成的静态验证(容器内无 dotnet,只能做到这些)

- ✅ 全部后端 `.cs` 括号平衡
- ✅ Controller 使用 22 个 Command/Query,全部有定义(闭环)
- ✅ Controller 4 个构造依赖全部已注册 DI
- ✅ 后端 8 个练习端点 ↔ 前端 PracticeApi 8 个调用 **完全对齐**
- ✅ 前端 `tsc` 0 类型错误
- ✅ 18 个 Angular 模板全部通过真实 `@angular/compiler` 解析
- ✅ SCSS 全部括号平衡
- ✅ `dev.sh` / `e2e.sh` `bash -n` 语法通过

## 七、待 Forrest 的 Mac 验证(容器做不了的)

1. `dotnet build src/Services.Assessment` — C# 真实编译
2. `bash tools/dev.sh practice` — 迁移能否真实建表
3. `bash tools/e2e.sh assessment` — 端到端
4. 浏览器实测:/practice 素材树能存能回读、录音能存能回放、评分能缓存、示读能出声
