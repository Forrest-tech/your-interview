# Resume（简历）现状规格

> 现状规格（2026-10-22 按代码实查落档）— 票 33 / AUDIT REQ-16
> 代码基线：`origin/develop-new-features` tip `f99cfdd`（只读 worktree 实查）
> 性质：描述现状行为，不是目标设计。冲突见文末，汇总见 `replica/spec-conflicts-index.md`。

## 模块职责
- 路由 `/resume`：用户**主简历**（一人一份长期资产）的维护页——文本正文 + PDF 文件的查看/上传/保存/删除。
- 与 Tracker 内「按投递 tailored 简历」是两个层级：主简历在本页；某条投递的 tailored 版在 Tracker Documents（同一 Jobs 服务、同一 ResumeFile 表按 ApplicationId 区分，见下）。Playbook 四格条的「本投递简历 / Resume Score」读的也是投递级有效简历。
- 前端 `web/src/app/features/resume/resume.component.ts`（238 行）+ 同名 html；后端 Jobs 服务 resume 组端点（`src/Services.Jobs/Api/JobsController.cs:421-618`）。

## 核心实体与数据真源（单一真源 = Jobs，票 17 清理后的现状）
- **UserResume**（`src/Services.Jobs/Domain/JobAggregates.cs:624`）：UserId 主键级一人一条，Content（text 列）+ Version。端点 `GET/PUT/DELETE /api/jobs/resume-text`（`ResumeHandlers.cs`：Get/Save/Delete，Save 对空文本由校验拒绝——见冲突 CONF-RS-2）。**Assessment 侧曾有的同名副本已删除（票 17 / ARC-04），全仓简历文本真源只此一处。**
- **ResumeFile**（`src/Services.Jobs/Domain/ResumeFile.cs`）：PDF 二进制直接存库（Data/ContentType/FileName/TextContent 提取文本），按 (UserId, ApplicationId) 区分：ApplicationId=null 为主简历文件，非空为该投递的 tailored 文件。端点 `POST/GET/DELETE /api/jobs/resume-file`（主）与 `GET/POST/DELETE /api/jobs/applications/{id}/resume|resume-file`（投递级，上传限 PDF、12MB，`JobsController.cs:579-618`）。
- **有效简历（Effective Resume）口径**（`ResumeFileHandlers.cs:114` 起，票 26 修复后）：投递级读取顺序 = tailored 文件 → 基础文件 → **基础文本（UserResume）** → Kind=none；下载 PDF 时 tailored 缺失回落基础文件。引用不复制：删 tailored 即回落基础。
- **前端 localStorage 是草稿缓存、不是真源**：键 `yi-my-resume` / `yi-my-resume-meta`（`resume.component.ts:64-65`）。进页先读本地草稿，本地为空才拉后端 resume-text 并回填本地；保存时先写本地再 PUT 后端，后端失败提示「仅存本地」不算保存失败（组件 `saveResume()`）。**注意跨消费方差异**：`ResumeComponent.getSavedResume()` 静态方法只读 localStorage，个别旧调用方（如备战材料对话框旧路径）可能读到未同步后端的草稿——见冲突 CONF-RS-3。

## 主要流程
1. 上传 txt/md → 前端直接读文本进草稿；PDF → pdf.js 前端提取文本进草稿 + 文件暂存待保存时一并 POST resume-file；docx 等不支持提取，提示复制粘贴（组件 `onResumeFile()`）。
2. 保存：草稿 → localStorage + PUT resume-text（+ 待上传 PDF → POST resume-file）。
3. 预览：有 PDF 时走 `resume-file/meta`（文件名真源在后端）+ blob 下载以 PDF 内容查看器逐页渲染；无 PDF 时仅显示「已存简历」状态头。
4. 删除：清本地两键 + PUT 空文本等价的后端清除（`clearResume()` 走 PUT resumeText=''）+ DELETE resume-file。注意 DELETE resume-text 端点存在但前端清除流程未用它。

## 与既定规则对照结论
- 单一真源：**已对齐**（票 17 删 Assessment 副本；票 26 补文本回落关闭 REQ-05）。
- OCR 平台定稿（`platform-ocr-spec.md`）称「Resume 图片型简历转文本」为 OCR 消费方：**现状未接**——见冲突 CONF-RS-1。

## 现状与规则冲突
- **CONF-RS-1（→ platform-ocr-spec 消费方清单 / 票 59 范围注记）**：图片型简历（扫描件/截图）无 OCR 入口：上传只认 txt/md/pdf/docx，PDF 靠 pdf.js 提文本层，扫描型 PDF 提取为空即失败提示，无共享 OCR 服务兜底。与平台 OCR 定稿的消费方预期不一致（票 59 只接了 Playbook 入口，其余消费方未接，定稿本身亦注记此状态）。
- **CONF-RS-2（→ AUDIT FUN-10，票 37 未实施）**：已存文本简历在页面上**不可见、不可编辑**——模板内无 textarea（`resume.component.html` 实查只有上传行/PDF 预览/按钮），编辑态只能换文件重存；无粘贴文本入口；草稿为空点保存时后端校验 400 而前端提示为「Saved locally, but sync to server failed」类开发者话术（AUDIT 证据 i18n.service.ts:1879）。文本维护入口缺失是现状。
- **CONF-RS-3（→ AUDIT ARC-04 残留面）**：真源虽已收敛 Jobs，但前端读取仍有 localStorage 优先路径（进页本地非空就不拉后端；`getSavedResume()` 纯本地读）——换设备时本地旧草稿可能盖过后端较新正文，与「后端单一真源」的口径存在读侧张力。
