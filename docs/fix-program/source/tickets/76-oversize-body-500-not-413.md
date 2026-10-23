# 票 76 — 超限请求体返回 500 而非 413（S4，源：票 18 票面外发现 1）

## 事实（票 18 活体探针实测，evidence-18/）
请求体超过 Kestrel 上限时抛 BadHttpRequestException，但全局异常映射
MapExceptionToStatusCode 未包含该类型，最终表面为 500 Internal Server
Error；语义应为 413 Payload Too Large。属既有口径，非票 18 引入。

## 任务
- 在全局异常映射补 BadHttpRequestException → 413（其 ReasonPhrase/请求体
  超限的细分状态按框架 StatusCode 透传，若框架已带 413 则直接用之）。
- 盘点其他框架级异常的映射缺口（只列清单，本票只修 413 这一项；
  其余报协调者另行排）。

## 验收
- 活体：向任一端点发超上限请求体 → 413（改前 500 留对照证据）。
- §7：映射纯逻辑带单测（含 boundary：恰在上限、超限、其他既有映射
  不回退——409/404/400 等票 07 等已定口径全过）。
- 影响面：全部服务的全局异常中间件（共享层改动，各服务探针抽验）；
  不变项：其他异常映射口径、错误响应体形状（ProblemDetails 结构）不变。
