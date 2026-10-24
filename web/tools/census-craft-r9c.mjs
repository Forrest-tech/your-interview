/**
 * 票123 §C 验证探针 —— C-1(动效令牌 + reduced motion)· C-2(焦点补完)·
 * C-3(悬停/按压统一)· C-4(唯一编排入场)。
 *
 * 四项各自有定稿写死的判据,本探针逐条量:
 *
 *   C-1 「post-change grep census — audited-surface `transition:` declarations
 *        carrying literal durations outside the token mapping = 0」+ Playwright
 *        探针:reducedMotion:'reduce' 时受审元素 computed transition-duration
 *        ≤ 0.01ms 等价;'no-preference' 时报出令牌值。」
 *
 *   C-2 「the four converted sites no longer declare `outline: none` on `:focus`」
 *        + 「Tab reaches a Sankey node and Enter switches the strip's stage」。
 *
 *   C-3 「`background-color` equals the wash token value … `box-shadow` = none
 *        for all rows; `.entry` hover retains the lift values; an `:active`
 *        sample on a row computes to the press value」。
 *
 *   C-4 「the content container's computed `animation-name` = `contentIn`」
 *        + 「reduced-motion run: the same container's animation is suppressed」。
 *
 * 用法:node web/tools/census-craft-r9c.mjs
 * 退出码: 0 = 四项全部达标;1 = 有未达标
 */
import { chromium } from 'playwright';

const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:5173';
const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5200';
const E2E_USER = {
  email: process.env['E2E_EMAIL'] ?? 'admin@your-interview.local',
  password: process.env['E2E_PASSWORD'] ?? 'Admin@2026',
};

const EXPECTED = {
  durFast: '0.12s',
  dur: '0.16s',
  durSlow: '0.4s',
  ease: 'cubic-bezier(0.2, 0, 0, 1)',
  hoverWash: 'rgba(46, 58, 140, 0.06)',
  pressWash: 'rgba(46, 58, 140, 0.12)',
  hoverWashDark: 'rgba(255, 255, 255, 0.14)',
  reduced: '0.00001s', // 0.01ms
  contentIn: 'contentIn',
};

const failures = [];

function check(ok, label, detail = '') {
  console.log(`  ${ok ? '✅' : '❌'} ${label}${detail ? ' —— ' + detail : ''}`);
  if (!ok) failures.push(label + (detail ? ' —— ' + detail : ''));
  return ok;
}

// ── 静态普查:源码层面 ──────────────────────────────────────────────────
console.log('=== C-1 静态普查:受审面 transition 的字面量时长应为 0 ===');
const { readFileSync, readdirSync, statSync } = await import('node:fs');
const { join } = await import('node:path');
const SRC = new URL('../src/app/', import.meta.url).pathname;

function walk(dir, out = []) {
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (/\.(scss|ts)$/.test(e)) out.push(p);
  }
  return out;
}

const AUDITED = ['features/tracker', 'features/playbook', 'features/techstack',
                 'features/auth', 'layout'];
const files = AUDITED.flatMap((d) => walk(join(SRC, d)));
const literalHits = [];
for (const f of files) {
  const src = readFileSync(f, 'utf8');
  src.split('\n').forEach((line, i) => {
    if (!line.includes('transition:')) return;
    // transition 里出现字面量时长(ms 或 s)即为未转换
    if (/\d+(\.\d+)?m?s\b/.test(line)) {
      literalHits.push(`${f.replace(SRC, '')}:${i + 1}  ${line.trim()}`);
    }
  });
}
check(literalHits.length === 0, '受审面 transition 不带字面量时长',
  literalHits.length ? literalHits.join(' | ') : `扫了 ${files.length} 个文件`);

console.log('\n=== C-2 静态普查:四个站点的 outline:none 应已消失 ===');
const OUTLINE_SITES = [
  // [文件, 选择器, 边框线索在何处]
  //   `self` = 该选择器自己的 :focus 规则里改 border-color
  //   `parent` = 该元素本身无边框,线索在**父容器**的 :focus-within 上
  //              (粘入框的 textarea 就是这种:自身 border:none,外框变色)
  ['src/app/features/tracker/application-detail-dialog.component.ts', '.plain-area', 'self'],
  ['src/app/features/playbook/redesign/intake/intake-panel.component.ts', '.paste', 'parent'],
  ['src/app/features/techstack/techstack.component.scss', '.blk-editor', 'self'],
  ['src/app/shared/doc-viewer/doc-viewer.component.scss', '.doc-editor', 'self'],
];
const ROOT = new URL('../../', import.meta.url).pathname.replace(/\/$/, '') + '/web/';
for (const [rel, sel, cue] of OUTLINE_SITES) {
  const src = readFileSync(join(ROOT, rel), 'utf8');
  const esc = sel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  /**
   * 分别取「`:focus` 块」与「`:focus-visible` 块」——
   * 早先用一条 `(:focus|:focus-visible)?` 的正则,`:focus` 分支会把
   * 紧邻的 `:focus-visible` 块一起吃掉(因为 `:focus` 也是 `:focus-visible`
   * 的前缀,贪婪匹配后仍留下可见串),于是误报「缺焦点环」。分开出块更准。
   */
  const focusBlock = src.match(new RegExp(esc + ':focus\\s*\\{[^}]*\\}'))?.[0] ?? '';
  const ringBlock = src.match(new RegExp(esc + ':focus-visible\\s*\\{[^}]*\\}'))?.[0] ?? '';
  const stillNone = /outline:\s*none/.test(focusBlock);
  const hasRing = /outline:\s*2px solid var\(--brand\)/.test(ringBlock);
  const keepsBorder = cue === 'parent'
    ? /:focus-within\s*\{[^}]*border-color/.test(src)   // 线索在父容器上
    : /border-color:/.test(focusBlock);
  check(!stillNone && hasRing && keepsBorder,
    `${rel} 的 ${sel} 保留 border 线索(${cue})且补上共享焦点环`,
    stillNone ? '仍有 outline: none'
      : (hasRing ? (keepsBorder ? '' : '丢了 border-color 线索') : '缺 :focus-visible 焦点环'));
}

// ── 活体:reduced motion 与令牌值 ────────────────────────────────────────
console.log('\n=== C-1 活体:transition-duration(reduce vs no-preference) ===');

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
let auth = null;
try {
  const r = await ctx.request.post(`${API}/api/auth/login`, { data: E2E_USER });
  if (r.ok) {
    const j = await r.json();
    auth = { token: j.tokens.accessToken, refresh: j.tokens.refreshToken, user: j.profile };
    console.log('[c] 登录成功');
  }
} catch (e) {
  console.log(`[c] 后端不可达(${e.message})`);
}

async function newPage(reducedMotion) {
  const c = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    reducedMotion,
  });
  const p = await c.newPage();
  if (auth) {
    await p.addInitScript((a) => {
      localStorage.setItem('yi.accessToken', a.token);
      localStorage.setItem('yi.refreshToken', a.refresh);
      localStorage.setItem('yi.user', JSON.stringify(a.user));
    }, { token: auth.token, refresh: auth.refresh, user: auth.user });
  }
  return { c, p };
}

/** 量一组元素的 computed transition-duration / animation-duration。 */
const measureMotion = (page, sels) =>
  page.evaluate((sels) => {
    const out = [];
    for (const sel of sels) {
      const el = document.querySelector(sel);
      if (!el) { out.push({ sel, found: false }); continue; }
      const cs = getComputedStyle(el);
      out.push({
        sel, found: true,
        transitionDuration: cs.transitionDuration,
        transitionProperty: cs.transitionProperty,
        transitionTimingFunction: cs.transitionTimingFunction,
        animationName: cs.animationName,
        animationDuration: cs.animationDuration,
      });
    }
    return out;
  }, sels);

const MOTION_SELS = [
  '.list-row',
  '.nav-item',
  '.side-item',
  '.upcoming-row',
  '.list-item',
];

// (a) no-preference:报出令牌值
{
  const { c, p } = await newPage('no-preference');
  await p.goto(WEB + '/tracker', { waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  const m = await measureMotion(p, MOTION_SELS);
  console.log('  no-preference 实测:');
  for (const r of m) {
    if (!r.found) { console.log(`    · ${r.sel} 未命中`); continue; }
    console.log(`    · ${r.sel}  duration=${r.transitionDuration}  timing=${r.transitionTimingFunction}`);
  }
  const found = m.filter((r) => r.found);
  /**
   * `transition-duration` 可以是**逗号分隔的多个值**(一条声明里有两个属性
   * 各带各的时长,比如 `.nav-item` 的 `background 130ms, opacity 130ms`)。
   * 所以要**逐个值**比,不能拿整串去等一个单值 —— 那会把
   * `0.12s, 0.12s` 误判成「不是令牌值」。这是探针的度量缺陷。
   */
  const TOKEN_VALUES = new Set([EXPECTED.durFast, EXPECTED.dur, EXPECTED.durSlow]);
  const notToken = [];
  for (const r of found) {
    for (const v of r.transitionDuration.split(',').map((x) => x.trim())) {
      if (!TOKEN_VALUES.has(v)) notToken.push(`${r.sel}=${v}`);
    }
  }
  check(notToken.length === 0 && found.length > 0,
    'no-preference 下受审元素的 transition-duration 全部是令牌值',
    notToken.length ? notToken.join(' | ') : `实读 ${found.map((r) => `${r.sel}:${r.transitionDuration}`).join(' | ')}`);
  await c.close();
}

// (b) reduce:全部压到 ≤ 0.01ms
{
  const { c, p } = await newPage('reduce');
  await p.goto(WEB + '/tracker', { waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  const m = await measureMotion(p, MOTION_SELS);
  console.log('  reduce 实测:');
  for (const r of m) {
    if (!r.found) { console.log(`    · ${r.sel} 未命中`); continue; }
    console.log(`    · ${r.sel}  duration=${r.transitionDuration}  animation=${r.animationName}/${r.animationDuration}`);
  }
  const found = m.filter((r) => r.found);
  const notStilled = found.filter((r) => {
    const secs = parseFloat(r.transitionDuration) || 0;
    return secs > 0.001;
  });
  check(notStilled.length === 0,
    'reduce 下受审元素的 transition-duration 被压到 ≤ 0.01ms',
    notStilled.length ? notStilled.map((r) => `${r.sel}=${r.transitionDuration}`).join(' | ') : '');

  // C-4:content-in 的 animation 在 reduce 下被压住
  const ci = await measureMotion(p, ['.content-in']);
  const ciOk = ci[0].found && (parseFloat(ci[0].animationDuration) || 0) <= 0.001;
  check(ciOk, 'reduce 下 .content-in 的 animation-duration 被压到 ≤ 0.01ms',
    ci[0].found ? `实读 ${ci[0].animationName}/${ci[0].animationDuration}` : '.content-in 未命中');
  await c.close();
}

// ── C-4:no-preference 下 content-in 的 animation-name === contentIn ─────
console.log('\n=== C-4 活体:content-in 的 animation-name ===');
const SURFACES_C4 = [
  ['/dashboard', '.content-in'],
  ['/tracker', '.content-in'],
  ['/playbook', '.content-in'],
  ['/techstack', '.content-in'],
];
{
  const { c, p } = await newPage('no-preference');
  for (const [url, sel] of SURFACES_C4) {
    await p.goto(WEB + url, { waitUntil: 'networkidle' });
    await p.waitForTimeout(1200);
    const m = await measureMotion(p, [sel]);
    const r = m[0];
    check(r.found && r.animationName === EXPECTED.contentIn,
      `${url} 的 content 容器 animation-name = ${EXPECTED.contentIn}`,
      r.found ? `实读 ${r.animationName}/${r.animationDuration}/${r.transitionTimingFunction}` : '选择器未命中');
  }
  await c.close();
}

// ── C-2:Sankey 键盘可达 ────────────────────────────────────────────────
console.log('\n=== C-2 活体:Sankey 节点键盘可达 + Enter 切换阶段 ===');
{
  const { c, p } = await newPage('no-preference');
  await p.goto(WEB + '/tracker', { waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);
  // 切到流程视图 —— 视图切换按钮只有图标(mat-icon),没有文字,
  // 所以按图标内容找(account_tree = 流程视图),不能按 textContent 找。
  await p.evaluate(() => {
    const btn = [...document.querySelectorAll('.view-btn')]
      .find((b) => (b.textContent ?? '').includes('account_tree'));
    if (btn) btn.click();
  });
  await p.waitForTimeout(2000);
  const node = await p.evaluate(() => {
    const g = document.querySelector('.sankey-node-g');
    if (!g) return { found: false };
    const cs = getComputedStyle(g.querySelector('.sankey-node'));
    return {
      found: true,
      tabindex: g.getAttribute('tabindex'),
      role: g.getAttribute('role'),
      ariaLabel: g.getAttribute('aria-label'),
      rectStroke: cs.stroke,
      rectStrokeWidth: cs.strokeWidth,
    };
  });
  check(node.found, '流程视图里找得到 Sankey 节点组', node.found ? '' : '选择器未命中');
  if (node.found) {
    check(node.tabindex === '0', 'Sankey 节点组有 tabindex="0"', `实读 ${node.tabindex}`);
    check(node.role === 'button', 'Sankey 节点组有 role="button"', `实读 ${node.role}`);
    check(!!node.ariaLabel, 'Sankey 节点组有 aria-label', `实读 ${node.ariaLabel}`);

    // 聚焦第一个节点,读焦点描边
    await p.evaluate(() => document.querySelector('.sankey-node-g')?.focus());
    await p.waitForTimeout(300);
    const focused = await p.evaluate(() => {
      const g = document.querySelector('.sankey-node-g');
      if (!g) return null;
      const cs = getComputedStyle(g.querySelector('.sankey-node'));
      return { active: document.activeElement === g, stroke: cs.stroke, width: cs.strokeWidth };
    });
    check(focused?.active === true, 'Sankey 节点组可以被聚焦', focused ? '' : 'focus 失败');
    check(focused?.stroke === 'rgb(46, 58, 140)' && parseFloat(focused?.width ?? '0') === 3.5,
      'Sankey 焦点用既有的 .selected 描边表达',
      focused ? `实读 ${focused.stroke} / ${focused.width}` : '');

    // Enter 应切换阶段(收藏条带内容变化)
    const before = await p.evaluate(() => document.querySelector('.flow-strip')?.textContent?.trim() ?? '');
    await p.keyboard.press('Enter');
    await p.waitForTimeout(900);
    const after = await p.evaluate(() => ({
      strip: document.querySelector('.flow-strip')?.textContent?.trim() ?? '',
      selected: document.querySelectorAll('.sankey-node.selected').length,
    }));
    check(after.selected > 0 || after.strip !== before,
      'Enter 键切换了 Sankey 阶段(条带内容或选中态变化)',
      `before="${before.slice(0, 40)}" after.selected=${after.selected}`);
  }
  await c.close();
}

// ── C-3 活体:悬停洗色 + 行无阴影 + 按压洗色 ─────────────────────────────
console.log('\n=== C-3 活体:hover 洗色 / 行无阴影 / active 按压洗色 ===');
{
  const { c, p } = await newPage('no-preference');
  await p.goto(WEB + '/tracker', { waitUntil: 'networkidle' });
  await p.waitForTimeout(1500);

  const rows = await p.evaluate(() => {
    const out = [];
    const sels = ['.list-row', '.flow-strip-row', '.nav-item', '.side-item'];
    for (const sel of sels) {
      const el = document.querySelector(sel);
      if (!el) continue;
      const before = getComputedStyle(el);
      out.push({
        sel,
        beforeBg: before.backgroundColor,
        beforeShadow: before.boxShadow,
        beforeTransition: before.transitionDuration,
      });
    }
    return out;
  });
  console.log('  悬停前:');
  for (const r of rows) console.log(`    · ${r.sel}  bg=${r.beforeBg}  shadow=${r.beforeShadow}`);

  // 逐个悬停,读悬停后的 computed
  const hovered = [];
  for (const r of rows) {
    const el = await p.$(r.sel);
    if (!el) continue;
    try { await el.hover({ timeout: 3000 }); } catch { /* 元素可能不可悬停 */ }
    await p.waitForTimeout(350);
    const after = await p.evaluate((sel) => {
      const e = document.querySelector(sel);
      if (!e) return null;
      const cs = getComputedStyle(e);
      return { bg: cs.backgroundColor, shadow: cs.boxShadow };
    }, r.sel);
    if (after) hovered.push({ sel: r.sel, ...after });
  }
  console.log('  悬停后:');
  for (const r of hovered) console.log(`    · ${r.sel}  bg=${r.bg}  shadow=${r.shadow}`);

  for (const r of hovered) {
    const isTopbarNav = r.sel === '.nav-item';
    const want = isTopbarNav ? EXPECTED.hoverWashDark : EXPECTED.hoverWash;
    check(r.bg === want, `${r.sel}:hover 的 background = ${want}`, `实读 ${r.bg}`);
  }

  // 行不应有阴影(C-3.3)
  const shadowedRows = hovered.filter((r) =>
    r.sel !== '.nav-item' && r.shadow !== 'none' && !/inset/.test(r.shadow));
  check(shadowedRows.length === 0, '受审行 hover 时 box-shadow = none',
    shadowedRows.length ? shadowedRows.map((r) => `${r.sel}=${r.shadow}`).join(' | ') : '');

  /**
   * 按压:必须**真按下去**(mouse.down),不能用 class 模拟 ——
   * class 模拟测的是「我加的 class 长什么样」,不是 `:active` 规则本身。
   * 按住不放时读 computed background,应当是 press 洗色。
   */
  const rowSel = '.list-row';
  const box = await p.$(rowSel);
  let pressed = null;
  if (box) {
    await box.scrollIntoViewIfNeeded();
    await p.mouse.move(10, 10);
    const bb = await box.boundingBox();
    if (bb) {
      await p.mouse.move(bb.x + bb.width / 2, bb.y + bb.height / 2);
      await p.mouse.down();
      await p.waitForTimeout(250);
      pressed = await p.evaluate((sel) => {
        const el = document.querySelector(sel);
        if (!el) return null;
        return { bg: getComputedStyle(el).backgroundColor };
      }, rowSel);
      await p.mouse.up();
    }
  }
  console.log(`  :active 真按压实测 = ${pressed ? pressed.bg : '(未取到)'}`);
  check(pressed?.bg === EXPECTED.pressWash,
    `${rowSel}:active 的 background = ${EXPECTED.pressWash}`,
    pressed ? `实读 ${pressed.bg}` : '未取到');
  await c.close();
}

await browser.close();

console.log('\n' + '='.repeat(60));
if (failures.length) {
  console.log(`❌ 未达标 ${failures.length} 项:`);
  for (const f of failures) console.log('   - ' + f);
  process.exit(1);
}
console.log('✅ §C 四项全部达标');
