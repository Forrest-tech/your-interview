# 票 90 — 框架级异常映射缺口盘点处置（S4，源：票 76 盘点只列不修）

## 清单（evidence-76/CLOSE-REPORT.md）
DbUpdateException（候选 409/400，最接近真实路径）、OperationCanceled/TaskCanceled、HttpRequestException（502）、TimeoutException、Format/Overflow、FileNotFound（404 二义）、NotImplementedException（501）。

## 任务
逐项读码定性：有真实可达路径才补映射+单测；不可达/二义的在报告中论证留现状。DbUpdateException 优先核（与票 07 业务冲突 409 口径的边界须划清：业务校验冲突走既有口径，DB 层并发/约束冲突才归本票）。
另两条既有口径备注（非本票默认范围）：ProblemDetails Type 硬编码 15.6.1 URI（票 24/76 同型）、错误响应 Content-Type=application/json——如需治理先报协调者。
