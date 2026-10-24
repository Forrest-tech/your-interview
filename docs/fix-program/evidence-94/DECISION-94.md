# 票 94 修复决策：两份 EnabledClusters 清单的处置（DECISION-94）

- 结论：**两份都保留，各按模式分工；不强制统一成一份。** 强行统一会在一个模式里复活本票要修的 bug。
- appsettings.json（默认清单，7 集群）：`identity,jobs,interviews,knowledge,assessment,analytics,aigateway`（排除 ocr）。
  适用：dotnet-run 全栈模式与完整 `docker compose up`（compose 未设该变量，不覆盖）。
  排除 ocr 的理由（票面）：网关转发业务 API 不依赖 OCR；OCR 自身可用性由票 92 金标准集守。
- docker-compose.hybrid.yml:76（覆盖清单，4 集群）：`identity,assessment,interviews,knowledge`。
  适用：仅 hybrid 模式。hybrid 只起 identity（容器）+ assessment/interviews/knowledge（宿主 dotnet run），
  jobs/analytics/aigateway 不在 hybrid 运行集内，故不列入。
  机制：.NET 配置层级中环境变量优先于 appsettings.json，因此 hybrid 模式下生效的是 4 集群清单，
  且仅在 hybrid 模式下生效；其它模式不受影响。
- 为什么不统一成一份：
  - 若两处都写 7 集群：hybrid 模式下 jobs/analytics/aigateway 没起，网关 /health/live 会因探不到它们而 503——
    正是本票在 hybrid 模式里的复发。
  - 若两处都写 4 集群：dotnet-run 全栈验收（停 jobs/analytics/aigateway 任一 → 必须 503）会失效，
    探活口径小于票面验收要求。
- 防误导措施：两处注释已互相引用对方（appsettings 注释指 hybrid 覆盖；hybrid 注释指默认清单与本文件），
  读者在任一处都能看到另一份的存在与分工。
- 另：hybrid 原注释“只探实际在跑的两个下游”与实际 4 集群清单自相矛盾（两个 ≠ 四个），已随本次更正。
