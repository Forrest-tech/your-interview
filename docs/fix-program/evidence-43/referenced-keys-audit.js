/**
 * 票43 ARC-13 引用键审计:全前端(ts + html)引用的每个 pb.* i18n 键都必须在核心 DICT 有定义
 * (验收口径「三语走查无键丢失/无原始键直出」的机械双证)。
 *
 * 口径:
 * - 静态引用:源码中 'pb.*' / "pb.*" 字符串字面量(宽捕,再按下表排除非 i18n 命名空间);
 * - 动态前缀:'prefix' + var / `prefix${var}` 形态,按受控域逐枚展开;
 * - 非 i18n 排除(localStorage 键命名空间,有文件+行号证据,非文案键):
 *     pb.debrief.                playbook-local.helpers.ts:113 startsWith 扫描旧版 localStorage
 *     pb.starStories             playbook-local.service.ts:41 本地数据存储键
 *     pb.localData.imported.*    playbook-local.service.ts:42-44 本地数据导入闩锁键
 */
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const REPO = path.resolve(__dirname, '..', '..', '..');
const SRC = path.join(REPO, 'web/src');

function extractDict(tsPath) {
  const src = fs.readFileSync(tsPath, 'utf8');
  const start = src.indexOf('const DICT');
  const braceStart = src.indexOf('{', start);
  const end = src.indexOf('\n};', braceStart);
  return eval('(' + src.slice(braceStart, end + 2) + ')');
}
const DICT = extractDict(path.join(SRC, 'app/core/i18n/i18n.service.ts'));

const files = execSync(`find "${SRC}" -type f \\( -name '*.ts' -o -name '*.html' \\)`, { maxBuffer: 1 << 24 })
  .toString().trim().split('\n').filter(Boolean);

// 宽捕所有 pb.* 字面量:单/双引号静态串 + 反引号模板串的静态前缀段
const staticRefs = new Map();
const staticRe = /['"`](pb\.[A-Za-z0-9_.]*)['"`]/g;
const tickRe = /`(pb\.[A-Za-z0-9_.]*)\$\{/g; // `pb.xxx.${var}` 的静态前缀
for (const f of files) {
  if (f.endsWith('i18n.service.ts')) continue;
  fs.readFileSync(f, 'utf8').split('\n').forEach((line, i) => {
    for (const m of line.matchAll(staticRe)) {
      const k = m[1];
      if (k === 'pb.' || k === 'pb') continue;
      if (!staticRefs.has(k)) staticRefs.set(k, []);
      staticRefs.get(k).push(`${path.relative(REPO, f)}:${i + 1}`);
    }
    for (const m of line.matchAll(tickRe)) {
      const k = m[1];
      if (!staticRefs.has(k)) staticRefs.set(k, []);
      staticRefs.get(k).push(`${path.relative(REPO, f)}:${i + 1}`);
    }
  });
}

// 非 i18n 命名空间排除(localStorage 键,证据见文件头注释)
const isLocalStorageKey = (k) =>
  k === 'pb.starStories' ||
  k.startsWith('pb.localData.imported.') ||
  k === 'pb.debrief.' ||
  k.startsWith('pb.debrief.');

// 动态前缀受控域(行内注释给出来源)
const DYNAMIC_DOMAINS = {
  'pb.v2.cat.': ['invitation', 'reschedule', 'rejection', 'followup', 'other'], // intake-panel.component.ts:52 CATEGORY_KEYS
  'pb.v2.srcst.': ['fetched', 'disabled', 'notConfigured', 'noData', 'failed'], // v2.models.ts:161 PrepSourceDetail.status
  'pb.status.': ['Draft', 'AssetsUploaded', 'Transcribing', 'Transcribed', 'Analyzing', 'Analyzed', 'Failed'], // api.models.ts:224 InterviewStatus
  'pb.roundOutcome.': ['Pending', 'Passed', 'Rejected', 'Ghosted', 'Cancelled', 'NoShow'], // SharedContracts InterviewOutcome
};
// 复合动态前缀(两段式拼接,逐枚展开全键;来源 prep-report.component.ts frameworkReport)
// sec(name) 八段;techQa 段只取 p1..p3(:331 p('techQa', 3)),其余七段 p1..p4
const FW_SECTIONS = ['greeting', 'introListen', 'selfIntro', 'deepDive', 'techQa', 'behavioral', 'askBack', 'closing']; // :345-353
const FW_FOCUS_CATS = ['screen', 'technical', 'design', 'behavioral', 'final', 'default']; // :358 stageFocusCat
const DYNAMIC_COMPOSITES = {
  // `pb.v2.p.fw.${name}.title` / `.p1..pN`(:327-331)
  'pb.v2.p.fw.': FW_SECTIONS.flatMap((n) =>
    ['title', 'p1', 'p2', 'p3', ...(n === 'techQa' ? [] : ['p4'])].map((s) => `pb.v2.p.fw.${n}.${s}`)),
  // `pb.v2.p.fw.focus.${cat}.p1..p3`(:338)
  'pb.v2.p.fw.focus.': FW_FOCUS_CATS.flatMap((c) => ['p1', 'p2', 'p3'].map((s) => `pb.v2.p.fw.focus.${c}.${s}`)),
};

let missing = 0;
let checked = 0;
const expanded = new Set();

for (const [k, locs] of staticRefs) {
  if (isLocalStorageKey(k)) continue;
  if (k.endsWith('.')) {
    // 动态前缀:先查受控域(扁平),再查复合域(全键展开)
    const domain = DYNAMIC_DOMAINS[k];
    const composite = DYNAMIC_COMPOSITES[k];
    if (domain) {
      for (const v of domain) {
        checked++;
        expanded.add(k + v);
        if (!(k + v in DICT)) { missing++; console.log(`缺失键 ${k + v}(动态展开,域值 ${v}) <- ${locs[0]}`); }
      }
    } else if (composite) {
      for (const full of composite) {
        checked++;
        expanded.add(full);
        if (!(full in DICT)) { missing++; console.log(`缺失键 ${full}(复合展开) <- ${locs[0]}`); }
      }
    } else {
      missing++;
      console.log(`未登记的动态前缀 ${k} <- ${locs[0]}`);
    }
  } else {
    checked++;
    if (!(k in DICT)) { missing++; console.log(`缺失键 ${k} <- ${locs[0]}${locs.length > 1 ? ` (共 ${locs.length} 处)` : ''}`); }
  }
}

// 反向:登记在案的动态域若源码未出现,也报出(防审计本身失效)
for (const prefix of [...Object.keys(DYNAMIC_DOMAINS), ...Object.keys(DYNAMIC_COMPOSITES)]) {
  if (!staticRefs.has(prefix)) console.log(`注意:动态前缀 ${prefix} 未在源码中捕获到(审计口径可能漏扫)`);
}

const pbV2 = Object.keys(DICT).filter((k) => k.startsWith('pb.v2.')).length;
const pbBr = Object.keys(DICT).filter((k) => k.startsWith('pb.br.')).length;
console.log(`\n引用键审计:静态+动态展开共校验 ${checked} 个键引用,缺失 ${missing} 个`);
console.log(`DICT pb.v2.* = ${pbV2},pb.br.* = ${pbBr};本票并入 498 键,既有重叠 79 键保留核心侧条目`);
if (missing > 0) process.exit(1);
console.log('PASS:全前端 pb.* i18n 引用键在核心 DICT 全部有定义 —— 无键丢失、无原始键直出面。');
