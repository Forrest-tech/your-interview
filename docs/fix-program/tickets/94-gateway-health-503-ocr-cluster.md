# 票 94 — 网关 /health/live 恒 503:ocr 集群无服务导致健康旗标与实际可用性脱节

严重度：S2（不是功能缺陷，但会让编排与自检**误判**，进而引发假的"起环境失败"）
来源：A2 E2E 套件健康检查时实测发现（2026-10-23 沙盒内），非票面登记

## 事实（已核实）
- 网关 `src/Services.Gateway/appsettings.json` 的 ReverseProxy Clusters 有 **8 个**：
  identity(5262) / jobs(5263) / interviews(5264) / knowledge(5265) / assessment(5266) /
  analytics(5267) / aigateway(5268) / **ocr(5300)**。
- 逐个探活 5262–5268：`/health/live` **全部 200**。
- 但 `GET http://127.0.0.1:5200/health/live` 返回 **503**。
- 而同一时刻 `POST /api/auth/login` 返回 **200** —— **API 完全可用，旗标却说它挂了。**
- 根因：第 8 个集群 `ocr → http://127.0.0.1:5300/` 在多数环境里没有服务在跑，
  探活必然失败 → 聚合 Unhealthy → 整体 503。
  网关 Program.cs:31-42 的注释**早已记载过同类坑**（"探活必然失败 ⇒ 整体 Unhealthy
  ⇒ /health/live 恒返 503"），并已预留 `ReverseProxy:HealthChecks:EnabledClusters`
  解法，但**没有配过**，于是旧行为（全探）持续生效。

## 影响面（为什么必须修，而不只是记一笔）
1. **票 93（B4 一条命令起环境）直接受害**：B4 要做的"起后自检报告，哪个服务没活
   一眼可见"，若照 /health/live 判定，会把**健康的网关误报成没起来** —— 自检报告
   的价值是"一眼看出真故障"，用错旗标就变成假警报，比不做自检更误导。
2. **编排依赖等待会卡死**：docker compose 的 `depends_on: condition: service_healthy`
   若挂在网关 /health/live 上，永远等不到 healthy，起环境必卡住。
3. 本票已实际影响 A2：E2E 健康检查一开始用 /health/live，直接被 503 拦下；
   已改探"登录能否 200"绕过，但那是**绕开**，不是修好。

## 任务
- 用网关已支持的 `ReverseProxy:HealthChecks:EnabledClusters` 显式列出要探的集群
  （identity,jobs,interviews,knowledge,assessment,analytics,aigateway），把 ocr
  排除在健康探活之外 —— 前提：ocr 服务尚未落地（见下）。
- 若票 92（OCR 入口接全）落地后 ocr 服务真的会起，则改为默认不探 ocr、
  待其上线再纳入，避免"没起的服务拖垮整体判定"。
- 不改探活路径口径（仍为各服务的 /health/live），不引入第二套健康判定。

## 验收
- **正向**：只起 7 个业务服务（不起 ocr）→ 网关 `/health/live` 返回 **200**。
- **反向（必须做）**：故意停掉 7 个中的任意一个（如 knowledge 5265）→ `/health/live`
  必须返回 **503**。只验"变绿"不算验收 —— 必须证明它**还能探出真故障**，
  否则就是把旗标改成永远说好话，那更糟。
- E2E 健康检查（e2e/health.setup.ts）改回用 /health/live 后仍能正常放行。
- 证据落盘：`docs/fix-program/evidence-94/`（三次状态：全起 / 停一个 / 停 ocr 之外全起）。

## 相关票
- **票 92**（OCR 入口接全）：ocr 服务是否落地由它决定，本票只管健康判定不被其拖累。
- **票 93**（B4 起后自检）：本票是它的前置，自检报告要靠这个旗标准。
- **票 88**（CI）：CI 里若用网关 health 做依赖等待，同样会被它卡住。
