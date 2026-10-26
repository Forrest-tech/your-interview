/**
 * 票123 R-8 渲染字体探针 —— 「字标用产品自己的字体」定稿契约的**渲染层**核验。
 *
 * 定稿依据:docs/fix-program/source/ticket123-REMEDIATION-PLAN.md §R-8(第 178–187 行)
 *   Changes 1 —— 登录页 H1「Your Interview」:Inter → Space Grotesk 600,字号不变(32px)。
 *   Changes 2 —— 顶栏字标(.brand-name):样式表**已经声明** Space-Grotesk-first 栈
 *                 16.5px/600,但审计的渲染字体测量读出的是 Inter 20px ——
 *                 「defect is in the rendered result (font not loaded for that element,
 *                 an override, or a second brand element), not in the declaration」。
 *                 实现必须诊断出是哪一个、记进工作日志、并把**渲染结果**修成
 *                 Space Grotesk 600 @16.5px。「Editing the stylesheet without changing
 *                 the rendered font does not close this item.」
 *
 * 本探针只读、不改任何页面状态。它做三件事:
 *   1. 读 computed fontFamily / fontSize / fontWeight;
 *   2. 用 document.fonts.check() 问浏览器「这个 family+weight 真的可用吗」;
 *   3. 用 Canvas 的实际字形测量做交叉验证(同名不同字体的宽度差是最直接的判据)。
 *
 * 用法:npx tsx tools/census-wordmark-font-r8.mjs
 * 退出码: 0 = 全部达标;1 = 有未达标项
 */
import { chromium } from 'playwright';

const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:5173';
const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5262';
const E2E_USER = {
  email: process.env['E2E_EMAIL'] ?? 'admin@your-interview.local',
  password: process.env['E2E_PASSWORD'] ?? 'Admin@2026',
};

/** 期望:元素选择器 → { family: 'Space Grotesk', size: '32px'|'16.5px', weight: '600' } */
const TARGETS = [
  { page: 'login', url: '/login', selector: '.brand-content h1',
    label: '登录页 H1「Your Interview」', size: '32px', weight: '600' },
  { page: 'dashboard', url: '/dashboard', selector: '.brand-name',
    label: '顶栏字标 .brand-name', size: '16.5px', weight: '600' },
  { page: 'tracker', url: '/tracker', selector: '.brand-name',
    label: '顶栏字标 .brand-name(另一页复测)', size: '16.5px', weight: '600' },
];
const MEASURE = `([sel, size, weight]) => {
  const el = document.querySelector(sel);
  if (!el) return { found: false };
  const cs = getComputedStyle(el);
  const family = cs.fontFamily;
  const first = family.split(',')[0].replace(/["']/g, '').trim();
  const text = (el.textContent || '').trim();

  // document.fonts.check: 浏览器是否真的能用这个 family + weight 渲染
  let fontsCheck = null;
  try {
    fontsCheck = document.fonts.check(\`\${weight} \${size} "\${first}"\`);
  } catch (e) { fontsCheck = null; }

  // 已加载的 face 清单(用于诊断"到底加载了哪些 family/weight")
  const loaded = [];
  try {
    for (const f of document.fonts) {
      loaded.push({ family: f.family, weight: f.weight, status: f.status });
    }
  } catch (e) { /* ignore */ }

  // Canvas 交叉验证:同一段文字在「期望字体」与「回退字体」下的宽度不同 ⇒ 真的生效了
  const probe = 'Your Interview';
  const measure = (fam) => {
    const c = document.createElement('canvas');
    const ctx = c.getContext('2d');
    ctx.font = \`\${weight} \${size} "\${fam}"\`;
    return ctx.measureText(probe).width;
  };
  const wExpected = measure(first);
  const wFallback = measure('Inter');

  return {
    found: true,
    text,
    fontFamily: family,
    first,
    fontSize: cs.fontSize,
    fontWeight: cs.fontWeight,
    fontsCheck,
    loaded,
    wExpected,
    wFallback,
  };
}`;

/**
 * 用一个纯函数形式的 evaluate —— 上面的 MEASURE 模板是箭头函数字符串,
 * page.evaluate 会把它当表达式求值而不是当函数调用,所以这里包一层,
 * 把参数通过第二个实参传进去。
 */
const evaluateMeasure = (page, sel, size, weight) =>
  page.evaluate(
    ({ sel, size, weight }) => {
      const el = document.querySelector(sel);
      if (!el) return { found: false };
      const cs = getComputedStyle(el);
      const family = cs.fontFamily;
      const first = family.split(',')[0].replace(/["']/g, '').trim();
      const text = (el.textContent || '').trim();

      let fontsCheck = null;
      try {
        fontsCheck = document.fonts.check(`${weight} ${size} "${first}"`);
      } catch (e) { fontsCheck = null; }

      const loaded = [];
      try {
        for (const f of document.fonts) {
          loaded.push({ family: f.family, weight: f.weight, status: f.status });
        }
      } catch (e) { /* ignore */ }

      const probe = 'Your Interview';
      const measure = (fam) => {
        const c = document.createElement('canvas');
        const ctx = c.getContext('2d');
        ctx.font = `${weight} ${size} "${fam}"`;
        return ctx.measureText(probe).width;
      };
      const wExpected = measure(first);
      const wFallback = measure('Inter');

      return {
        found: true,
        text,
        fontFamily: family,
        first,
        fontSize: cs.fontSize,
        fontWeight: cs.fontWeight,
        fontsCheck,
        loaded,
        wExpected,
        wFallback,
      };
    },
    { sel, size, weight },
  );

const failures = [];
const log = [];

function check(ok, label, detail = '') {
  const line = `  ${ok ? '✅' : '❌'} ${label}${detail ? ' —— ' + detail : ''}`;
  console.log(line);
  log.push(line);
  if (!ok) failures.push(label + (detail ? ' —— ' + detail : ''));
  return ok;
}

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });

// 登录(后端不在时降级为未登录 —— 登录页不需要认证)
let auth = null;
try {
  const r = await ctx.request.post(`${API}/api/auth/login`, { data: E2E_USER });
  if (r.ok) {
    const j = await r.json();
    auth = { token: j.tokens.accessToken, refresh: j.tokens.refreshToken, user: j.profile };
    console.log('[r8] 登录成功');
  } else {
    console.log(`[r8] 登录 HTTP ${r.status()} —— 降级为未登录`);
  }
} catch (e) {
  console.log(`[r8] 后端不可达(${e.message}) —— 降级为未登录`);
}

const page = await ctx.newPage();
if (auth) {
  await page.addInitScript((a) => {
    localStorage.setItem('yi.accessToken', a.token);
    localStorage.setItem('yi.refreshToken', a.refresh);
    localStorage.setItem('yi.user', JSON.stringify(a.user));
  }, { token: auth.token, refresh: auth.refresh, user: auth.user });
}

for (const t of TARGETS) {
  console.log(`\n=== ${t.label} — ${t.page} (${t.url}) ===`);
  await page.goto(WEB + t.url, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1200);

  const m = await evaluateMeasure(page, t.selector, t.size, t.weight);

  if (!m.found) {
    check(false, `${t.label}:元素可定位`, `选择器 ${t.selector} 未命中`);
    continue;
  }

  console.log(`  text: ${JSON.stringify(m.text)}`);
  console.log(`  computed font-family: ${m.fontFamily}`);
  console.log(`  computed size/weight: ${m.fontSize} / ${m.fontWeight}`);
  console.log(`  document.fonts.check("${m.first}" ${t.weight}): ${m.fontsCheck}`);
  console.log(`  canvas 宽度 期望字体=${m.wExpected.toFixed(2)}px  Inter=${m.wFallback.toFixed(2)}px`);
  console.log(`  已加载 face: ${JSON.stringify(m.loaded)}`);

  // 1) 渲染字体族是 Space Grotesk
  check(m.first === 'Space Grotesk',
    `${t.label}:渲染字体族 = Space Grotesk`,
    `实读 ${m.first}`);

  // 2) 字号
  check(m.fontSize === t.size,
    `${t.label}:字号 = ${t.size}`,
    `实读 ${m.fontSize}`);

  // 3) 字重
  check(m.fontWeight === t.weight,
    `${t.label}:字重 = ${t.weight}`,
    `实读 ${m.fontWeight}`);

  // 4) 浏览器确认这个 family+weight 可用
  check(m.fontsCheck === true,
    `${t.label}:document.fonts.check 通过`,
    `实读 ${m.fontsCheck}`);

  // 5) Canvas 交叉验证:宽度与 Inter 不同 ⇒ 真的换了字体,不是同名同宽的巧合
  check(Math.abs(m.wExpected - m.wFallback) > 0.5,
    `${t.label}:canvas 字形宽度与 Inter 不同(交叉验证)`,
    `期望=${m.wExpected.toFixed(2)} Inter=${m.wFallback.toFixed(2)}`);
}

// ── 第三处字标:页脚 .f-brand span ───────────────────────────────────────────
// 仓里共有三处「Your Interview」:登录 H1、顶栏 .brand-name、页脚 .f-brand span。
// 页脚自 2026-10-07 Forrest 要求起被整体隐藏(showFooter = computed(() => false)),
// 因此它**从不渲染** —— 这一条记进证据,免得复测时被当成漏做。
console.log('\n=== 页脚字标 .f-brand span(预期:不渲染) ===');
const footerFound = await page.evaluate(() => {
  const el = document.querySelector('.f-brand span');
  if (!el) return { found: false };
  const cs = getComputedStyle(el);
  return { found: true, fontFamily: cs.fontFamily, fontSize: cs.fontSize, fontWeight: cs.fontWeight };
});
if (footerFound.found) {
  check(false, '页脚字标:.f-brand span 不渲染(showFooter 恒 false)',
    `实读存在,字体 ${footerFound.fontFamily}`);
} else {
  check(true, '页脚字标:.f-brand span 不渲染(showFooter 恒 false)—— 无需改');
}

await browser.close();

console.log('\n' + '='.repeat(60));if (failures.length) {
  console.log(`❌ 未达标 ${failures.length} 项:`);
  for (const f of failures) console.log('   - ' + f);
  process.exit(1);
}
console.log('✅ R-8 渲染字体契约全部达标');
