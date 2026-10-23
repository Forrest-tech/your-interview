# Issue tracker: Local Markdown

本仓库的工单与规格以 Markdown 文件形式本地跟踪，不依赖 GitHub Issues。

## 现有主台账（优先级最高）

- **`docs/fix-program/FIX-STATUS.md`** —— 修复工程执行协议（PROTOCOL.md）下的工单队列与状态机唯一事实源。
- **`docs/fix-program/source/tickets/`** —— 各工单的票面规格。
- 工程技能套件（triage / tdd / handoff 等）涉及「工单」时，**默认读这份台账**，不要另起炉灶。

## 新功能/试验性工作的约定（.scratch）

- 每个功能一个目录：`.scratch/<feature-slug>/`
- 规格文件：`.scratch/<feature-slug>/spec.md`
- 实施工单一文件一票：`.scratch/<feature-slug>/issues/<NN>-<slug>.md`，从 `01` 编号
- 工单文件顶部附近用 `Status:` 行记录分诊状态（角色串见 `triage-labels.md`）
- 评论与对话历史追加到文件底部 `## Comments` 标题下

## 当 skill 说「publish to the issue tracker」

在 `.scratch/<feature-slug>/` 下新建文件（目录不存在则创建）。
但若该工作属于既有修复队列，则更新 `docs/fix-program/FIX-STATUS.md` 及对应 evidence 目录，不要写进 `.scratch/`。

## 当 skill 说「fetch the relevant ticket」

读取被引用的路径。用户通常会直接给出路径或票号（票号在 FIX-STATUS.md 的队列表里）。

## 与其他协议的关系

`docs/fix-program/PROTOCOL.md`（Forrest 制定的修复工程执行协议）是本仓库的**最高执行规则**：
状态机、开工纪律、关票四步（实现 → 测试 → 活体证据 → 远端确认）均以其为准。
工程技能套件在其框架内提供方法（TDD 循环、分诊、交接、架构深化），不替代也不覆盖该协议。
