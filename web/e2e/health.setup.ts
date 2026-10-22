/**
 * E2E 全局前置:等网关与前端都**真的能用**才开跑(票 89)。
 *
 * ⚠️ 为什么不用 /health/live 聚合旗标(票 94):
 *   网关把所有下游集群纳入探活,其中 ocr 集群指向 127.0.0.1:5300 而该服务在多数
 *   环境里并不存在 ⇒ 探活恒失败 ⇒ /health/live 恒返 503,**即便 7 个业务服务
 *   全部 200、所有 API 都正常**。也就是说这个旗标与实际可用性是脱节的:
 *   编排健康检查、起后自检报告若照它判定,会把健康的网关误报成"没起来"。
 *   所以这里改探**能不能真的登录**——网关 + identity 通了才叫就绪。
 *
 * 另一个动机:"页面能开但 API 全挂"是最经典的假成功,页面 HTML 200 但每个接口
 *   都在 500,此时跑 E2E 只会得到一堆莫名其妙的红。这里逐个探真实能力。
 */
const API = process.env.E2E_API ?? 'http://127.0.0.1:5200';
const WEB = process.env.E2E_WEB ?? 'http://127.0.0.1:4200';
const EMAIL = process.env.E2E_EMAIL ?? 'admin@your-interview.local';
const PASSWORD = process.env.E2E_PASSWORD ?? 'Admin@2026';

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
  await waitForLoginWorks(90_000);
  await waitForOk('前端', WEB, 90_000);
  console.log('[e2e] 依赖全部就绪');
}
