# 59: 共享 OCR 服务 + Playbook 入口切换（平台能力票，非 55 条正单）

**来源：** 用户定方向（2026-10-21）；定稿 `~/workspace/your-interview/replica/platform-ocr-spec.md`（选型与约束以该文件为准，冲突时定稿优先并先报协调者）
**选型证据：** `~/workspace/fix-verify/system-audit-20261021/ocr-test/AB-RESULT.md`（PaddleOCR PP-OCRv6 原图直跑为主、DeepSeek 视觉兜底）
**区域：** 新服务 `src/ocr-service/` + 网关路由配置 + Playbook 入口前端（intake-panel）
**Blocked by:** None（与 AiGateway 区域既有票错峰：本票不改 AiGateway 代码，只经网关并列接入）
**Status:** ready-for-agent

## 既有资产（先读再动手，勿重造）

- 可用 venv：`~/workspace/ocr-ab/venv`（PaddleOCR 3.7.0 / paddlepaddle 3.3.1 已装）；模型缓存 `~/.paddlex`（PP-OCRv6 medium det+rec 已下载）。
- 可用调用样例：`~/workspace/ocr-ab/run_ocr.py`（原图直跑的实测脚本与参数）；真实测试图路径在 `ocr-test/run_ocr_real.py` 内（用户真实邮件截图，即 AB 对测同一张）。
- **踩坑结论（定稿锁定）**：`enable_mkldnn=False` 绕 paddlepaddle 3.3.1 OneDNN bug；**禁止 2× 放大预处理**（更慢更差）；沙盒单图 76.6s 是环境特例，延迟不作生产依据、不得写死同步/异步。

## What to build

### 1. 共享 OCR 服务（独立小服务，无状态）
- 代码落仓库 `src/ocr-service/`（Python；Web 框架自选轻量者，如 FastAPI+uvicorn，缺包 pip 装进 ocr-ab venv 并写明；`requirements.txt` 锁定 paddlepaddle/paddleocr 版本，README 写启动与配置）。
- 接口：
  - `GET /health` → ok + 引擎/模型标识。
  - `POST /v1/ocr`：收 `{imageBase64, mimeType}` → `{status, engine, lines:[{text, confidence, bbox}], text, elapsedMs}`。`text` = 处理层组装的全文（逐行、阅读序）。
  - 异步模式：`POST /v1/ocr/jobs` → `{jobId}`；`GET /v1/ocr/jobs/{jobId}` → `{status: running|done|failed, result?}`（内存型作业表+TTL 即可，服务本身不落持久状态）。
- 服务内处理层（定稿要求，缺一不可）：
  - **原图直跑，禁止放大**；mkldnn 开关走配置（env `OCR_ENABLE_MKLDNN`，默认 False）。
  - **噪声过滤**：低于置信阈值的行剔除（env `OCR_MIN_CONFIDENCE`，默认 0.5；AB 实测图标噪声行置信 0.315、正文行远高于此）；被剔除行数在响应里以 `droppedLines` 计数返回，不静默。
  - **标点归一**：全角→半角归一（`～`→`~` 等，归一映射写清并注释）；不得改动文字内容本身。
  - **行序还原**：按 bbox 坐标还原阅读顺序（先上下、后左右的行分组排序），不得直接用引擎原始散序。
- 配置一律走 env（端口 `OCR_PORT` 默认 5300、阈值、mkldnn、作业 TTL），行为开关不写死。

### 2. 网关接入
- 找到现行 YARP 网关的路由配置（按既有路由的写法），新增 `/api/ocr/{**catch-all}` → OCR 服务集群，与 AiGateway 并列；不改 AiGateway 任何代码。
- `docker-compose.yml` 增 `ocr-service` 条目（构建上下文指向 `src/ocr-service/`，含 Dockerfile）；沙盒无法实建镜像时以 `docker compose config` 校验语法（无 docker 则在报告注明未校验），网关路由的活体验证以 venv 直跑的服务为准。

### 3. Playbook 入口切换（仅此一个消费方）
- `intake-panel.component.ts` 的截图 OCR 路径改为：**先调共享 OCR 服务**（经网关 `/api/ocr`），拿到 `text` 后走既有 `parsePastedEmail`/`cardFromText` 管线（解析规则不另起口径，票 57 的 extractInterviewWhen 等保持不变）。
- **兜底判定**：服务不可达/超时/`status` 非 ok/文本为空/解析后卡片为 failed（头部三件套全空）→ 回落现有 AiGateway 视觉路径（`ExtractImageText`，保持原样不动）。超时阈值做成前端可配置常量，不写死在调用里。
- **来源标注口径不变**：预览里沿用第九节既有来源标注机制，按实际来源（本地 OCR / 视觉兜底）如实标注。
- 其余消费方（JD 截图、图片简历、题目截图）**本轮不接**，只保证服务接口通用。

## Acceptance criteria

- [ ] 服务活体：venv 直跑启动，`GET /health` ok；对真实测试图 `POST /v1/ocr` → 200，lines 非空，`text` 含会议卡日期行（2026-10-13）与时间（10:00），关键串按 AB 口径抽查（只记一致/不一致，不把敏感串原文抄进证据）
- [ ] 处理层证据：响应含 droppedLines 且低置信图标噪声行（AB 中的「分」0.315 类）不在 `text` 内；`～` 已归一为 `~`；行序与原文阅读序一致（证据里贴 text 前 20 行即可，避开敏感串行）
- [ ] 异步作业接口可用：提交 → 轮询 → done 拿到同一结果形状
- [ ] 入口解析同源：以服务返回 text 喂既有管线（可用 evidence-57 的机械抽取 harness 扩展）→ subject/from/date 可定位、`when = 2026-10-13 10:00`
- [ ] 兜底路径：代码审查证据（判定条件与回落调用点）+ 服务停机时的降级模拟（前端以可注入的服务地址指向死端口，验证走视觉路径的分支被触发——沙盒无浏览器时以代码级/请求级证据为准并诚实标注边界）
- [ ] 网关路由活体：经网关地址调 `/api/ocr/health`（或 /v1/ocr 小图）通；`npx tsc --noEmit` 通过；不改 .NET 业务代码（网关配置除外），既有测试项目不回归

## 关闭纪律（全票通用）

- 复现/现状记录 → 实现 → 验收逐条证据，存 evidence-59/
- 延迟只记录不设门槛（沙盒特例）；与定稿冲突先报协调者，不许自行改口径
- 在独立 worktree 工作（Python 服务文件同样进 git）；push 前 pull --rebase 到 develop-new-features 最新 tip，只许推 develop-new-features


## 附则：单元测试门槛（2026-10-21 用户立规，总纲 skill 第七节，即刻生效）

- 本票新写/改动的纯函数与规则逻辑必须带单测，含 boundary 类别（空/null、空集合、超长上限±1、特殊字符/Unicode、0 与负数、日期时区临界、文件类 0 字节/损坏/超限——按适用者取），命名/注释标注类别，缺类在报告说明理由；缺测协调者不予验收。
- 改到无覆盖的旧纯逻辑：先补特征测试锁现状再改行为。flaky 按缺陷立票；陈旧测试按票 55 先例（改测试+注明依据，不默默删）。细则以 skill 第七节为准。


## 附则：变更安全协议（2026-10-21 用户立规，总纲 skill 第八节，即刻生效）

- 动手前在本票补两张清单（写进票面，交付报告复述并打勾）：**影响面**——本票将改动的共享组件/函数/数据还被谁用、哪些流程经过；**不变项**——本票明确不动的东西，逐条写出。
- 验收范围 = 本票功能 + 影响面全部邻居：邻居逐项按总纲第六节验证剧本回归、不变项逐条打勾才许关票。只验新功能 = 未验收，协调者不予关票。


## 附则：偏差零容忍 + 对照表交付（总纲 skill 第八节第 5/6 关，即刻生效）

- **偏差零容忍**：票面/定稿/审计条目已定口径的，任何偏离（布局、字段、文案、顺序、删减、自创优化）未经批准禁止；撞上真实冲突停工上报协调者（写明冲突点 + 两个可选解法），禁止现场即兴改口径。事后发现的未批偏差按缺陷立票返工。
- **对照表交付**：UI 类交付报告必须附对照表——设计依据（定稿/审计条目期望行为/改前形态）与成果并排逐项打勾（布局结构/字段与文案/顺序/状态与空态/间距档位），偏差清单单列（为空才算按稿交付；非空每条注明原因与批准出处）。修复类票以审计条目期望行为 + 改前形态为设计依据，比例适用。无对照表的 UI 票协调者不予验收。
