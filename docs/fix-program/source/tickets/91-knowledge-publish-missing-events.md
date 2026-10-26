# 票 91 — Knowledge 发布侧缺事件（条目新建/删除/复盘改级不发事件）（源：票 34 盘点申报 B）

## 事实（evidence-34/STATUS.md 冲突申报）
- 票 34 已修 Analytics 消费侧（台账增量迁移 + MasteryLevelMapping + reconcile/rebuild 端点，faedc0b）。
- 发布侧缺口：Knowledge 条目新建、删除、RecordReview 内改级均不发布事件 → 消费侧台账仍无法感知这三类变化，mastery/Total 计数与真源分叉（rebuild 端点可人工对平，但事件面不完整）。
- 另：InterviewAnalysisCompleted、MockAnswerSubmitted、AuditEventRecorded 三契约全仓无发布者；KnowledgeMasteryChanged 为死契约（消费/发布关系逐项在 evidence-34 盘点表）。

## 任务
- Knowledge 三类变化补发事件（复用既有契约口径，Level 语义与票 34 MasteryLevelMapping 对齐：字符串优先/枚举 int 回落）；删除事件须带足够信息供消费侧台账迁移（条目 id/用户/原等级/原主题）。
- 死契约四项逐项定性：有真实需求则接线、无则在报告论证后删契约或保留标注（与票 36/37/53 区域相邻，动手前先读其票面避免重复/冲突，冲突报协调者）。
- §7 单测：发布点行为测试（建/删/改级各一 + 不重复发）；Analytics.Tests/Knowledge.Tests 零回退；活体 RabbitMQ 事件流→Analytics 台账迁移实证（法同票 34 一阶段）。
