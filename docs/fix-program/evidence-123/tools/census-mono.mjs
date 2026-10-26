/**
 * 票123 Phase-0 / R-1.6 census 探针(只读,不改任何页面状态)。
 *
 * 目的:定稿 R-1.6 要求「Playbook detail monoCount 3 → 0」,并指明
 * 「phase-0 probe identifies the 3 monospace elements by element path」。
 * 本探针复用浏览器 computed style,直接列出所有 computed fontFamily
 * 里含 mono 的可见元素(附元素路径、类名、tag、字号、文本片段)。
 *
 * 用法:npx tsx tools/census-mono.mjs   (或 node --experimental-strip-types)
 */
import { chromium } from 'playwright';

const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5200';
const E2E_USER = {
  email: process.env['E2E_EMAIL'] ?? 'admin@your-interview.local',
  password: process.env['E2E_PASSWORD'] ?? 'Admin@2026',
};

const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:5173';

const PAGES = [
  { name: 'playbook-list', url: '/playbook' },
  { name: 'playbook-detail', url: '/playbook' },
  { name: 'techstack', url: '/techstack' },
  { name: 'dashboard', url: '/dashboard' },
  { name: 'tracker', url: '/tracker' },
  { name: 'login', url: '/login' },
];

const COLLECT = `() => {
  const out = [];
  const seen = new Set();
  for (const el of document.querySelectorAll('*')) {
    const cs = getComputedStyle(el);
    if (!cs.fontFamily) continue;
    if (!/mono/i.test(cs.fontFamily)) continue;
    if (cs.display === 'none' || cs.visibility === 'hidden') continue;
    const r = el.getBoundingClientRect();
    if (r.width < 1 || r.height < 1) continue;
    const path = [];
    let n = el;
    while (n && n !== document.body && path.length < 8) {
      let s = n.tagName.toLowerCase();
      if (n.id) { s += '#' + n.id; path.unshift(s); break; }
      if (n.className && typeof n.className === 'string') {
        s += '.' + n.className.trim().split(/\\s+/).slice(0, 3).join('.');
      }
      path.unshift(s);
      n = n.parentElement;
    }
    const key = path.join('>');
    if (seen.has(key)) continue;
    seen.add(key);
    out.push({
      path: key,
      tag: el.tagName.toLowerCase(),
      cls: typeof el.className === 'string' ? el.className : '',
      fontFamily: cs.fontFamily,
      fontSize: cs.fontSize,
      text: (el.textContent || '').trim().slice(0, 60),
    });
  }
  return out;
}`;

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const request = await ctx.request;

let auth = null;
try {
  const r = await request.post(`${API}/api/auth/login`, { data: E2E_USER });
  if (!r.ok) throw new Error(`HTTP ${r.status()}`);
  const j = await r.json();
  auth = { token: j.tokens.accessToken, refresh: j.tokens.refreshToken, user: j.profile };
} catch (e) {
  console.error('[census] 登录失败:', e.message);
}

const page = await ctx.newPage();
if (auth) {
  await page.addInitScript((a) => {
    localStorage.setItem('yi.accessToken', a.token);
    localStorage.setItem('yi.refreshToken', a.refresh);
    localStorage.setItem('yi.user', JSON.stringify(a.user));
  }, { token: auth.token, refresh: auth.refresh, user: auth.user });
}

for (const p of PAGES) {
  await page.goto(WEB + p.url, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1200);
  const mono = (await page.evaluate(COLLECT)) ?? [];
  console.log(`\n=== ${p.name} (${p.url}) — mono 元素 ${mono.length} 个 ===`);
  for (const m of mono) {
    console.log(`  ${m.path}`);
    console.log(`    font-family: ${m.fontFamily}  size: ${m.fontSize}`);
    console.log(`    text: ${JSON.stringify(m.text)}`);
  }
}

await browser.close();
