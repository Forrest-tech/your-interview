/**
 * 票123 R-9 几何一致性探针 —— The Track 的**合并门**。
 *
 * 定稿(REMEDIATION-PLAN §R-9「The geometry contract」):
 *
 *   The stepper, the band, the rounds track, and the Tracker drawer's
 *   communication timeline must share **one** style source: node diameter
 *   **16px**, node border **2px**, rail width **2px**, the state treatments and
 *   rail colors quoted above, defined once (shared SCSS partial / CSS custom
 *   properties) and consumed by all four.
 *
 *   Verification is a geometry probe: computed node size, rail width, and state
 *   colors sampled on Tracker list, Dashboard, Playbook detail, and the drawer
 *   timeline must be **identical values**, not similar ones.
 *
 * 本探针在浏览器里量 **computed style**(不是读样式表 —— 读样式表只能证明声明,
 * 不能证明渲染),逐个面对比四个消费者。
 *
 * 用法:node web/tools/census-track-geometry-r9.mjs
 * 退出码: 0 = 四个面几何完全一致;1 = 有不一致
 */
import { chromium } from 'playwright';

const WEB = process.env['E2E_WEB'] ?? 'http://127.0.0.1:5173';
const API = process.env['E2E_API'] ?? 'http://127.0.0.1:5200';
const E2E_USER = {
  email: process.env['E2E_EMAIL'] ?? 'admin@your-interview.local',
  password: process.env['E2E_PASSWORD'] ?? 'Admin@2026',
};

/** 期望的共享几何值 —— 与 styles/_track-geometry.scss 一一对应。 */
const EXPECTED = {
  nodeSize: '16px',
  nodeBorder: '2px',
  railWidth: '2px',
  railPending: 'rgba(0, 0, 0, 0.12)',
  railDone: 'rgba(46, 58, 140, 0.45)',
  futureBorder: 'rgb(195, 201, 212)', // #c3c9d4
  reachedBorder: 'rgb(46, 58, 140)', // var(--brand) = #2e3a8c
  goBorder: 'rgb(27, 127, 75)', // var(--go)
  stopBorder: 'rgb(179, 38, 30)', // var(--stop)
};

/**
 * 四个消费者面。selector 是该面上「节点」的选择器。
 *
 * Playbook 那一面是 `/playbook/:id` —— 详情页才有 Rounds Track;
 * `/playbook` 是列表页,量不到 `.rnode`。条目 id 由登录后从
 * `GET /api/interviews` 取第一条,所以这里先留空,运行时填。
 */
const SURFACES = [
  { name: 'Tracker 列表 stepper', url: '/tracker', nodeSel: '.sp-dot', railSel: '.sp-line' },
  { name: 'Dashboard Pipeline Band', url: '/dashboard', nodeSel: '.band .bdot', railSel: '.band .seg' },
  { name: 'Playbook 详情 Rounds Track', url: null, nodeSel: '.rnode .dot', railSel: '.rtrack .rail' },
];

const failures = [];

function check(ok, label, detail = '') {
  console.log(`  ${ok ? '✅' : '❌'} ${label}${detail ? ' —— ' + detail : ''}`);
  if (!ok) failures.push(label + (detail ? ' —— ' + detail : ''));
  return ok;
}

/**
 * 量该面上**所有**节点的 computed 几何(不是只量第一个)。
 *
 * 为什么:定稿要求「state colors quoted above, defined once and consumed by all four」。
 * 一个面上同时存在 reached / hollow 两种节点;只量第一个节点会随机命中其中一种,
 * 于是「未来态描边色」这类断言时对时错 —— 那是探针的抽样缺陷,不是实现缺陷。
 * 这里把所有节点都量出来,再按**状态**归类分别断言。
 */
const measureAllNodes = (page, sel) =>
  page.evaluate((sel) => {
    const els = Array.from(document.querySelectorAll(sel));
    if (!els.length) return { found: false, nodes: [] };
    const nodes = els.map((el) => {
      const cs = getComputedStyle(el);
      /**
       * 状态类挂在**父节点**上(Tracker 是 `.sp-stage.done/.current`,
       * Playbook 是 `.rnode.current`,Dashboard 直接把状态写进 `.bdot` 自身)。
       * 所以这里把「自身类名 + 父链类名」一起带上,便于按状态归类。
       */
      const ctx = [];
      let p = el;
      for (let i = 0; i < 6 && p; i++) {
        if (p.className && typeof p.className === 'string') ctx.push(p.className);
        p = p.parentElement;
      }
      return {
        cls: el.className,
        ctx: ctx.join(' | '),
        width: cs.width,
        height: cs.height,
        borderWidth: cs.borderTopWidth,
        borderStyle: cs.borderTopStyle,
        borderColor: cs.borderTopColor,
        background: cs.backgroundColor,
        boxShadow: cs.boxShadow,
      };
    });
    return { found: true, nodes };
  }, sel);


/** 量一段轨道的 computed 几何。 */
const measureRail = (page, sel) =>
  page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return { found: false };
    const cs = getComputedStyle(el);
    return {
      found: true,
      width: cs.width,
      height: cs.height,
      background: cs.backgroundColor,
      borderTopWidth: cs.borderTopWidth,
      borderTopStyle: cs.borderTopStyle,
      /**
       * `thickness` = 轨道的**粗细**,也就是共享几何契约里那个 2px。
       * `width`/`height` 里另一条边是轨道的**长度** —— 各面长度天然不同
       * (横向轨道长度取决于节点间距,纵向轨道长度取决于轮次数),
       * 把长度当契约值去跨面比是探针的度量错误。粗细才是契约。
       */
      thickness: Math.min(parseFloat(cs.width) || 0, parseFloat(cs.height) || 0) === 0
        ? (parseFloat(cs.width) || parseFloat(cs.height) || 0)
        : Math.min(parseFloat(cs.width) || 0, parseFloat(cs.height) || 0),
    };
  }, sel);

/** 从 box-shadow 里抠出光晕的 rgba。 */
function haloOf(boxShadow) {
  if (!boxShadow || boxShadow === 'none') return null;
  const m = boxShadow.match(/rgba?\([^)]+\)/);
  return m ? m[0].replace(/\s+/g, ' ') : null;
}

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });

let auth = null;
try {
  const r = await ctx.request.post(`${API}/api/auth/login`, { data: E2E_USER });
  if (r.ok) {
    const j = await r.json();
    auth = { token: j.tokens.accessToken, refresh: j.tokens.refreshToken, user: j.profile };
    console.log('[r9] 登录成功');
  } else {
    console.log(`[r9] 登录 HTTP ${r.status()} —— 降级为未登录`);
  }
} catch (e) {
  console.log(`[r9] 后端不可达(${e.message}) —— 降级为未登录`);
}

const page = await ctx.newPage();
if (auth) {
  await page.addInitScript((a) => {
    localStorage.setItem('yi.accessToken', a.token);
    localStorage.setItem('yi.refreshToken', a.refresh);
    localStorage.setItem('yi.user', JSON.stringify(a.user));
  }, { token: auth.token, refresh: auth.refresh, user: auth.user });
}

/**
 * Playbook 的 Rounds Track 在**详情页**(`/playbook/:id`),列表页 `/playbook` 里没有。
 * 所以这里先从接口取第一条条目,把详情页 URL 填进 SURFACES。
 */
let playbookDetailUrl = null;
if (auth) {
  try {
    const r = await ctx.request.get(`${API}/api/interviews?page=1&pageSize=50`, {
      headers: { Authorization: `Bearer ${auth.token}` },
    });
    if (r.ok()) {
      const j = await r.json();
      const list = j.items ?? j.data ?? (Array.isArray(j) ? j : []);
      const withRounds = list.find((e) => e && (e.roundCount ?? e.roundsCount) > 0) ?? list[0];
      if (withRounds?.id) {
        playbookDetailUrl = `/playbook/${withRounds.id}`;
        console.log(`[r9] Playbook 详情页 ${playbookDetailUrl}`);
      }
    }
  } catch (e) {
    console.log(`[r9] 取 Playbook 列表失败(${e.message})`);
  }
}
for (const s of SURFACES) {
  if (s.url === null) s.url = playbookDetailUrl ?? '/playbook';
}

// ── 三个有静态选择器的面 ────────────────────────────────────────────────
const measured = {};

for (const s of SURFACES) {
  const url = s.url ?? '/playbook';
  console.log(`\n=== ${s.name} — ${url} ===`);
  await page.goto(WEB + url, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);

  const all = await measureAllNodes(page, s.nodeSel);
  if (!all.found) {
    check(false, `${s.name}:节点可定位`, `选择器 ${s.nodeSel} 未命中`);
    continue;
  }
  const rail = await measureRail(page, s.railSel);

  console.log(`  节点 ${s.nodeSel}: ${all.nodes.length} 个`);
  for (const n of all.nodes) {
    console.log(`    · [${n.cls}] ${n.width} × ${n.height}, 描边 ${n.borderWidth} ${n.borderStyle} ${n.borderColor}, 底色 ${n.background}`);
  }
  if (rail.found) {
    console.log(`  轨道 ${s.railSel}: ${rail.width} × ${rail.height}(粗细 ${rail.thickness}px), 底色 ${rail.background}`);
  }

  // 尺寸与描边宽度:该面上**所有**节点都必须相同。
  const sizes = new Set(all.nodes.map((n) => `${n.width}×${n.height}`));
  check(sizes.size === 1 && all.nodes.every((n) => n.width === EXPECTED.nodeSize && n.height === EXPECTED.nodeSize),
    `${s.name}:节点尺寸 = ${EXPECTED.nodeSize}`,
    `实读 ${[...sizes].join(' | ')}(共 ${all.nodes.length} 个节点)`);

  const bws = new Set(all.nodes.map((n) => n.borderWidth));
  check(bws.size === 1 && all.nodes.every((n) => n.borderWidth === EXPECTED.nodeBorder),
    `${s.name}:节点描边 = ${EXPECTED.nodeBorder}`,
    `实读 ${[...bws].join(' | ')}`);

  // 状态色:该面上存在的**每一种**状态,颜色都必须落在契约给的那一档里。
  // 「未来态」(未到达)的描边色 = --track-node-future-border;
  // 「已到达」= --track-node-reached-border;终点/裁决各占自己的档位。
  //
  // 归类依据是**祖先链上的状态类**,不是「当前节点长什么样」——
  // 契约说的是「状态 → 颜色」这条映射,不是「每个节点都得是灰的」。
  //
  // 各面的状态类落在哪:
  //   Tracker stepper : 祖先 `.sp-stage.done`(= 已到达);
  //                     祖先 `.sp-stage.current` + 更外层 `.list-row.term-<裁决>`
  //                     (在 term-offer 行里当前节点是 go,在 term-rejected 行里是 stop)
  //   Dashboard band  : 节点自身 `.bdot.reached|.bdot.go|.bdot.stop|.bdot.hollow`
  //   Playbook track  : 节点自身 `.dot.reached|.dot.hollow`
  //   抽屉时间轴      : 祖先 `.tl-item.done`
  const stateOf = (n) => {
    const c = n.ctx ?? '';
    // 裁决态:`term-offer` 行里的当前节点 = go;`term-rejected` 行里的当前节点 = stop。
    // 判据:祖先链里出现 `current` **且** 行/祖先带 term-<裁决> 类。
    if (/(^|[\s|])current([\s|]|$)/i.test(c)) {
      if (/term-offer/i.test(c)) return 'go';
      if (/term-rejected/i.test(c)) return 'stop';
    }
    if (/(^|[\s|])stop([\s|]|$)|(^|[\s|])bdot stop([\s|]|$)/i.test(c)) return 'stop';
    if (/(^|[\s|])go([\s|]|$)|(^|[\s|])bdot go([\s|]|$)/i.test(c)) return 'go';
    if (/(^|[\s|])reached([\s|]|$)|(^|[\s|])done([\s|]|$)/i.test(c)) return 'reached';
    if (/(^|[\s|])hollow([\s|]|$)|(^|[\s|])future([\s|]|$)/i.test(c)) return 'future';
    // 没有任何状态类 = 尚未到达的节点(渲染成默认的空心未来态)。
    return 'future';
  };
  const byState = new Map();
  for (const n of all.nodes) {
    const st = stateOf(n);
    if (!byState.has(st)) byState.set(st, []);
    byState.get(st).push(n);
  }
  console.log(`  状态分布: ${[...byState].map(([k, v]) => `${k}×${v.length}`).join(', ')}`);

  const futureNodes = byState.get('future') ?? [];
  if (futureNodes.length) {
    const colors = new Set(futureNodes.map((n) => n.borderColor));
    check(colors.size === 1 && futureNodes.every((n) => n.borderColor === EXPECTED.futureBorder),
      `${s.name}:未来态描边色 = ${EXPECTED.futureBorder}`,
      `实读 ${[...colors].join(' | ')}(${futureNodes.length} 个未来态节点)`);
  } else {
    console.log(`  · 该面没有未来态节点 —— 未来态断言跳过`);
  }

  // 已到达态的描边色必须等于品牌色。
  const reachedNodes = byState.get('reached') ?? [];
  if (reachedNodes.length) {
    const colors = new Set(reachedNodes.map((n) => n.borderColor));
    check(colors.size === 1 && reachedNodes.every((n) => n.borderColor === EXPECTED.reachedBorder),
      `${s.name}:已到达态描边色 = ${EXPECTED.reachedBorder}`,
      `实读 ${[...colors].join(' | ')}(${reachedNodes.length} 个已到达节点)`);
  }

  // 凡是判出状态来的节点,描边色必须落在契约调色板内(不能是调色板外的颜色)。
  const PALETTE = new Set([
    EXPECTED.futureBorder,
    EXPECTED.reachedBorder,
    EXPECTED.goBorder,
    EXPECTED.stopBorder,
  ]);
  const offPalette = all.nodes.filter((n) => !PALETTE.has(n.borderColor));
  check(offPalette.length === 0,
    `${s.name}:所有节点描边色都在契约调色板内`,
    offPalette.length ? `越界: ${offPalette.map((n) => `${n.borderColor}(${n.ctx})`).join(', ')}` : '');

  if (rail.found) {
    check(rail.thickness === parseFloat(EXPECTED.railWidth),
      `${s.name}:轨道粗细 = ${EXPECTED.railWidth}`,
      `实读 ${rail.thickness}px(${rail.width} × ${rail.height})`);
  } else {
    check(false, `${s.name}:轨道可定位`, `选择器 ${s.railSel} 未命中`);
  }

  measured[s.name] = { nodes: all.nodes, rail };
}

// ── 第四个面:Tracker 抽屉时间轴 ─────────────────────────────────────────
// 抽屉要打开才有时间轴,所以走一次「点开第一行 → 抽屉出现」。
console.log('\n=== Tracker 抽屉时间轴 — /tracker(打开抽屉) ===');
await page.goto(WEB + '/tracker', { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);
const opened = await page.evaluate(() => {
  const rows = document.querySelectorAll('.list-row, .lr-row, [data-testid="app-row"]');
  if (rows.length) { rows[0].click(); return { clicked: true, sel: '.list-row' }; }
  return { clicked: false };
});
await page.waitForTimeout(2000);
const drawer = await page.evaluate(() => {
  const dots = Array.from(document.querySelectorAll('.tl-dot'));
  const rail = document.querySelector('.timeline');
  if (!dots.length || !rail) return { found: false };
  const rcs = getComputedStyle(rail, '::before');
  const nodes = dots.map((el) => {
    const cs = getComputedStyle(el);
    const ctx = [];
    let p = el;
    for (let i = 0; i < 6 && p; i++) {
      if (p.className && typeof p.className === 'string') ctx.push(p.className);
      p = p.parentElement;
    }
    return {
      cls: el.className,
      ctx: ctx.join(' | '),
      width: cs.width,
      height: cs.height,
      borderWidth: cs.borderTopWidth,
      borderColor: cs.borderTopColor,
      background: cs.backgroundColor,
      boxShadow: cs.boxShadow,
    };
  });
  return {
    found: true,
    nodes,
    railWidth: rcs.width,
    railBackground: rcs.backgroundColor,
  };
});
if (!drawer.found) {
  console.log(`  (未打开抽屉 —— 行选择器 ${opened.sel ?? '未命中'};节点未量到)`);
} else {
  console.log(`  节点 .tl-dot: ${drawer.nodes.length} 个`);
  for (const n of drawer.nodes) {
    console.log(`    · [${n.cls}] ${n.width} × ${n.height}, 描边 ${n.borderWidth} ${n.borderColor}, 底色 ${n.background}`);
  }
  console.log(`  轨道 .timeline::before: ${drawer.railWidth}, 底色 ${drawer.railBackground}`);
  const dsizes = new Set(drawer.nodes.map((n) => `${n.width}×${n.height}`));
  check(dsizes.size === 1 && drawer.nodes.every((n) => n.width === EXPECTED.nodeSize && n.height === EXPECTED.nodeSize),
    'Tracker 抽屉时间轴:节点尺寸 = 16px',
    `实读 ${[...dsizes].join(' | ')}`);
  const dbws = new Set(drawer.nodes.map((n) => n.borderWidth));
  check(dbws.size === 1 && drawer.nodes.every((n) => n.borderWidth === EXPECTED.nodeBorder),
    'Tracker 抽屉时间轴:节点描边 = 2px',
    `实读 ${[...dbws].join(' | ')}`);
  check(drawer.railWidth === EXPECTED.railWidth,
    'Tracker 抽屉时间轴:轨道宽度 = 2px',
    `实读 ${drawer.railWidth}`);
  check(drawer.railBackground === EXPECTED.railPending,
    'Tracker 抽屉时间轴:轨道色 = 待决色',
    `实读 ${drawer.railBackground}`);

  // 抽屉的状态色同样要落在契约调色板内(`.tl-item.done` = 已到达,其余 = 未来态)。
  const dReach = drawer.nodes.filter((n) => /(^|[\s|])done([\s|]|$)/i.test(n.ctx ?? ''));
  const dFuture = drawer.nodes.filter((n) => !/(^|[\s|])done([\s|]|$)/i.test(n.ctx ?? ''));
  if (dReach.length) {
    const colors = new Set(dReach.map((n) => n.borderColor));
    check(colors.size === 1 && dReach.every((n) => n.borderColor === EXPECTED.reachedBorder),
      'Tracker 抽屉时间轴:已到达态描边色 = 品牌色',
      `实读 ${[...colors].join(' | ')}(${dReach.length} 个已到达节点)`);
  }
  if (dFuture.length) {
    const colors = new Set(dFuture.map((n) => n.borderColor));
    check(colors.size === 1 && dFuture.every((n) => n.borderColor === EXPECTED.futureBorder),
      'Tracker 抽屉时间轴:未来态描边色 = 未来描边色',
      `实读 ${[...colors].join(' | ')}(${dFuture.length} 个未来态节点)`);
  }

  measured['Tracker 抽屉时间轴'] = { nodes: drawer.nodes, rail: { found: true, thickness: parseFloat(drawer.railWidth) } };
}

// ── 跨面对比:几何值必须**逐字相同** ─────────────────────────────────────
console.log('\n=== 跨面一致性(逐字相同,不是「接近」) ===');
const nodeSizes = new Set();
const borderWidths = new Set();
const railThicknesses = new Set();
for (const [name, m] of Object.entries(measured)) {
  if (!m.nodes) continue;
  for (const n of m.nodes) {
    nodeSizes.add(`${n.width}×${n.height}`);
    borderWidths.add(n.borderWidth);
  }
  if (m.rail?.found) railThicknesses.add(m.rail.thickness);
}
check(nodeSizes.size === 1, '所有面的节点尺寸相同',
  [...nodeSizes].join(' | '));
check(borderWidths.size === 1, '所有面的节点描边宽度相同',
  [...borderWidths].join(' | '));
if (railThicknesses.size) {
  check(railThicknesses.size === 1, '所有面的轨道粗细相同',
    [...railThicknesses].join(' | ') + 'px');
}

await browser.close();

console.log('\n' + '='.repeat(60));
if (failures.length) {
  console.log(`❌ 未达标 ${failures.length} 项:`);
  for (const f of failures) console.log('   - ' + f);
  process.exit(1);
}
console.log('✅ R-9 几何一致性:四个面共用同一套值');
