/**
 * E2E 全局前置:等网关与前端都**真的能用**才开跑(票 89)。
 *
 * 网关聚合旗标 /health/live(票 94):此前网关默认探全部下游集群,OCR(PaddleOCR,
 * 重服务)没起时也会把网关判成 503,即便 7 个业务服务全部 200、API 正常——旗标与
 * 实际可用性脱节,所以这里一度绕开它、改探真实登录。票 94 修复后:网关
 * ReverseProxy.HealthChecks.EnabledClusters 只探 7 个业务集群(OCR 排除在外,
 * OCR 自身可用性由票 92 金标准集守),/health/live 恢复可信,故这里改回先等
 * /health/live 200 再探登录。登录检查保留:它是"页面能开但 API 全挂"这类假成功
 * 的第二道 guard,与健康旗标分工不同。
 *
 * 旧前提更正:OCR 服务是存在的(compose 里有 ocr-service,票 59 落地,本地识别,
 * 端口 5300),不是"在多数环境里并不存在";当初的 503 是部分起服务时 OCR 没起
 * 又被纳入探活所致,不是 OCR 缺失。
 */
const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5200';
const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:4200';
const EMAIL = process.env['E2E_EMAIL'] ?? 'admin@your-interview.local';
const PASSWORD = process.env['E2E_PASSWORD'] ?? 'Admin@2026';

async function waitForLoginWorks(timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let last = 'never tried';
  while (Date.now() < deadline) {
    try {
      const r = await fetch(`${API}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: EMAIL, password: PASSWORD }),
        signal: AbortSignal.timeout(5000),
      });
      // 200 = 网关 + identity 可用。401 也是"通了但账密不对"——那要立刻说清楚,
      // 否则后面每个用例都会以别的形式红,排查成本更高。
      if (r.ok) {
        console.log('[e2e] 网关可用(登录 200)');
        return;
      }
      last = `HTTP ${r.status} ${(await r.text()).slice(0, 120)}`;
      if (r.status === 401) {
        throw new Error(
          `[e2e] 网关通了但账密不对(${EMAIL})。请检查 E2E_EMAIL / E2E_PASSWORD 环境变量。`
        );
      }
    } catch (e) {
      if ((e as Error).message.startsWith('[e2e] 网关通了')) throw e;
      last = (e as Error).message;
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  throw new Error(
    `[e2e] 依赖未就绪:网关(${API}) 最后一次 = ${last}\n` +
    `      请先把后端与前端起好再跑 e2e(见 ticket 93 的一条命令起环境)。`
  );
}

async function waitForOk(name: string, url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let last = 'never tried';
  while (Date.now() < deadline) {
    try {
      const r = await fetch(url, { signal: AbortSignal.timeout(5000) });
      if (r.ok) {
        console.log(`[e2e] ${name} 就绪`);
        return;
      }
      last = `HTTP ${r.status}`;
    } catch (e) {
      last = (e as Error).message;
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  throw new Error(
    `[e2e] 依赖未就绪:${name}(${url}) 最后一次 = ${last}\n` +
    `      请先把后端与前端起好再跑 e2e(见 ticket 93 的一条命令起环境)。`
  );
}

export default async function globalSetup(): Promise<void> {
  console.log('[e2e] 检查依赖...');
  await waitForOk('网关健康', `${API}/health/live`, 90_000);
  await waitForLoginWorks(90_000);
  await waitForOk('前端', WEB, 90_000);
  console.log('[e2e] 依赖全部就绪');
}
