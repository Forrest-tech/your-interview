/**
 * 票43 ARC-13 i18n 机制收敛 —— parity + boundary 测试(Node 直跑编译产物,仓内既有模式见票35)
 *
 * 方法:自建镜像源码树两份
 *   old/ = 基线 commit ${BASE_SHA}(git show 取出)= 两套机制(本地字典优先 → 回退核心 I18nService)
 *   new/ = 当前工作树 = 一套机制(全部委托核心 I18nService,DICT 已并入本地字典)
 * 两侧 helpers 真实编译执行;I18nService 以与源码逐字等同的桩替代
 * (t: entry[lang] ?? entry.zh ?? key —— 与 i18n.service.ts 的 t 实现一致;该文件除 DICT 数据外零改动)。
 *
 * 判定:全部键 × 三语 oldT === newT;键集只增不减;既有条目零改动;boundary 类别逐组标注。
 * 运行:node docs/fix-program/evidence-43/i18n-parity-check.js(仓库根或任意 cwd 均可)
 */
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const REPO = path.resolve(__dirname, '..', '..', '..');
const WEB = path.join(REPO, 'web');
const TSC = path.join(WEB, 'node_modules', '.bin', 'tsc');
/** 基线 = 本票开工前的远端 tip(票42 推送后的 agents 配置提交);旧机制的最后形态。 */
const BASE_SHA = 'f46b30a';
const ROOT = fs.mkdtempSync(path.join(require('os').tmpdir(), 'i18n-parity-'));

const FILES = [
  'app/core/i18n/i18n.service.ts',
  'app/features/playbook/redesign/v2.helpers.ts',
  'app/features/playbook/redesign/v2.i18n.ts',
  'app/features/playbook/components/brief/brief.helpers.ts',
  'app/features/playbook/components/brief/brief.i18n.ts',
];

function materialize(side, get) {
  for (const f of FILES) {
    const dst = path.join(ROOT, side, f);
    fs.mkdirSync(path.dirname(dst), { recursive: true });
    fs.writeFileSync(dst, get(f));
  }
}
// old: 基线 commit;new: 工作树(v2.i18n/brief.i18n 已删,取空内容占位)
materialize('old', (f) => execSync(`git -C "${REPO}" show ${BASE_SHA}:web/src/${f}`, { maxBuffer: 1 << 24 }).toString());
materialize('new', (f) => {
  const p = path.join(WEB, 'src', f);
  return fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : 'export {};\n';
});

// ---------------------------------------------------------------- 编译两侧(tsc 有类型错误也照样 emit)
function compile(side) {
  const present = FILES.filter((f) => fs.existsSync(path.join(ROOT, side, f)));
  const args = present.map((f) => `"${path.join(ROOT, side, f)}"`).join(' ');
  try {
    execSync(`"${TSC}" --module commonjs --target es2020 --skipLibCheck ${args}`, { stdio: 'pipe' });
  } catch {
    /* @angular/core 不可解析 → 类型错误,但 JS 已 emit;require 时不需要它 */
  }
  for (const f of present) {
    if (!fs.existsSync(path.join(ROOT, side, f.replace(/\.ts$/, '.js')))) {
      throw new Error(`compile failed: ${side}/${f}`);
    }
  }
}
compile('old');
compile('new');

// ------------------------------------------------- 从 .ts 源码抽取 DICT 字面量并 eval(纯数据)
function extractDict(tsPath) {
  const src = fs.readFileSync(tsPath, 'utf8');
  const start = src.indexOf('const DICT');
  const braceStart = src.indexOf('{', start);
  const end = src.indexOf('\n};', braceStart);
  if (end < 0) throw new Error('DICT end not found in ' + tsPath);
  return eval('(' + src.slice(braceStart, end + 2) + ')'); // 本仓自有文件,纯对象字面量
}
const OLD_CORE = extractDict(path.join(ROOT, 'old/app/core/i18n/i18n.service.ts'));
const NEW_CORE = extractDict(path.join(ROOT, 'new/app/core/i18n/i18n.service.ts'));

const R = (side, f) => require(path.join(ROOT, side, f.replace(/\.ts$/, '.js')));
const OLD = {
  V2: R('old', 'app/features/playbook/redesign/v2.i18n.ts').V2_I18N,
  BR: R('old', 'app/features/playbook/components/brief/brief.i18n.ts').BRIEF_I18N,
  makeV2T: R('old', 'app/features/playbook/redesign/v2.helpers.ts').makeV2T,
  makeV2Fmt: R('old', 'app/features/playbook/redesign/v2.helpers.ts').makeV2Fmt,
  makeBriefT: R('old', 'app/features/playbook/components/brief/brief.helpers.ts').makeBriefT,
};
const NEW = {
  makeV2T: R('new', 'app/features/playbook/redesign/v2.helpers.ts').makeV2T,
  makeV2Fmt: R('new', 'app/features/playbook/redesign/v2.helpers.ts').makeV2Fmt,
  makeBriefT: R('new', 'app/features/playbook/components/brief/brief.helpers.ts').makeBriefT,
};

/** 桩:与 I18nService 行为逐字等同(lang 信号 + t 回退链 entry[lang] ?? entry.zh ?? key) */
function stub(dict, lang) {
  return {
    lang: () => lang,
    t: (k) => {
      const e = dict[k];
      if (!e) return k;
      return e[lang] ?? e.zh ?? k;
    },
  };
}

// ---------------------------------------------------------------- 断言框架
let pass = 0;
let fail = 0;
const failures = [];
function check(cat, name, cond, detail) {
  if (cond) pass++;
  else {
    fail++;
    failures.push(`[${cat}] ${name}${detail ? ' :: ' + detail : ''}`);
  }
}
const LANGS = ['zh', 'en', 'fr'];

// ================================================================ A. 键集与数据完整性
const oldLocalKeys = [...new Set([...Object.keys(OLD.V2), ...Object.keys(OLD.BR)])];
const oldCoreKeys = Object.keys(OLD_CORE);
const newCoreKeys = Object.keys(NEW_CORE);

check('A 键集', '本地 577 键全部进入核心 DICT', oldLocalKeys.every((k) => k in NEW_CORE),
  `缺失: ${oldLocalKeys.filter((k) => !(k in NEW_CORE)).slice(0, 5).join(', ')}`);
check('A 键集', '核心 DICT 旧键零丢失(只增不减)', oldCoreKeys.every((k) => k in NEW_CORE),
  `缺失: ${oldCoreKeys.filter((k) => !(k in NEW_CORE)).slice(0, 5).join(', ')}`);
check('A 键集', 'DICT 净增 498 键', newCoreKeys.length === oldCoreKeys.length + 498,
  `old=${oldCoreKeys.length} new=${newCoreKeys.length}`);
check('A 键集', '两本地表键名无交集(并入无冲突)', Object.keys(OLD.V2).every((k) => !(k in OLD.BR)));
check('A 键集', '核心 DICT 无重复键', new Set(newCoreKeys).size === newCoreKeys.length);

let edited = [];
for (const k of oldCoreKeys) {
  for (const l of LANGS) {
    if (OLD_CORE[k][l] !== NEW_CORE[k][l]) edited.push(`${k}.${l}`);
  }
}
check('A 数据', '既有 DICT 条目逐字零改动', edited.length === 0, edited.slice(0, 5).join(', '));

let mergeDiff = [];
for (const k of oldLocalKeys) {
  const src = OLD.V2[k] ?? OLD.BR[k];
  const dst = NEW_CORE[k];
  if (!dst) continue;
  if (dst.zh !== src.zh) mergeDiff.push(`${k}.zh`);
  if (dst.en !== src.en) mergeDiff.push(`${k}.en`);
  if (src.fr !== undefined ? dst.fr !== src.fr : dst.fr !== src.en) mergeDiff.push(`${k}.fr`);
}
check('A 数据', '并入键 zh/en 逐字承袭、fr 缺项按 en 同文补齐', mergeDiff.length === 0, mergeDiff.slice(0, 5).join(', '));

// ================================================================ B. 行为 parity(核心锁)
// 口径:parity 按各接线的真实键域断言(生产调用点键域已 grep 双证:
//   makeBriefT 唯一消费方 prep-plan-dialog 仅用 pb.br.prep.*;redesign 组件零 pb.br.*;brief 目录零 pb.v2.*)
//   跨命名空间键(pb.v2.* 过 makeBriefT 等)在生产不可达 —— 旧接线对该类键原样直出(缺陷态),
//   新接线正确解析,单列 B4 按验收口径「无原始键直出」断言。
const v2Keys = Object.keys(OLD.V2);
const brKeys = Object.keys(OLD.BR);
const coreKeyList = Object.keys(OLD_CORE);

let parityV2Fail = 0;
const parityDetail = [];
for (const k of [...v2Keys, ...coreKeyList]) {
  for (const l of LANGS) {
    const o = OLD.makeV2T(stub(OLD_CORE, l))(k);
    const n = NEW.makeV2T(stub(NEW_CORE, l))(k);
    if (o !== n) { parityV2Fail++; if (parityDetail.length < 8) parityDetail.push(`v2 ${k}@${l}: ${o} != ${n}`); }
  }
}
check('B parity', `makeV2T:全部 pb.v2.* ${v2Keys.length} 键 + 核心既有键 × 三语 oldT === newT`, parityV2Fail === 0, parityDetail.join(' | '));

let parityBrFail = 0;
for (const k of [...brKeys, ...coreKeyList]) {
  for (const l of LANGS) {
    const o = OLD.makeBriefT(stub(OLD_CORE, l))(k);
    const n = NEW.makeBriefT(stub(NEW_CORE, l))(k);
    if (o !== n) { parityBrFail++; if (parityDetail.length < 8) parityDetail.push(`br ${k}@${l}: ${o} != ${n}`); }
  }
}
check('B parity', `makeBriefT:全部 pb.br.* ${brKeys.length} 键 + 核心既有键 × 三语 oldT === newT`, parityBrFail === 0, parityDetail.join(' | '));

// B4:验收口径「无原始键直出」—— 新接线对全部键(两本地表全集 × 三语)不得直出 key 本身
{
  let rawOut = 0;
  for (const k of oldLocalKeys) {
    for (const l of LANGS) {
      if (NEW.makeV2T(stub(NEW_CORE, l))(k) === k) rawOut++;
      if (NEW.makeBriefT(stub(NEW_CORE, l))(k) === k) rawOut++;
    }
  }
  check('B 验收口径', `无原始键直出(本地 ${oldLocalKeys.length} 键 × 三语 × 两接线)`, rawOut === 0, `直出 ${rawOut} 处`);
}

// B2:playbook.component.ts F6 内联 t(旧形态 = BRIEF_I18N 优先)覆盖键 parity
const f6Keys = ['pb.br.cov.title', 'pb.br.cov.sub', 'pb.br.cov.expand', 'pb.br.cov.collapse', 'pb.br.cov.mastered', 'pb.br.cov.stuck', 'pb.br.cov.empty', 'pb.br.cov.askedTimes'];
let f6Fail = 0;
for (const k of f6Keys) {
  for (const l of LANGS) {
    const e = OLD.BR[k];
    const oldF6 = e ? (e[l] ?? e.en ?? e.zh ?? k) : stub(OLD_CORE, l).t(k);
    const newF6 = NEW.makeBriefT(stub(NEW_CORE, l))(k);
    if (oldF6 !== newF6) { f6Fail++; failures.push(`[B parity] F6 ${k}@${l}: ${oldF6} != ${newF6}`); }
  }
}
check('B parity', 'playbook 列表页 F6 技术覆盖图谱文案 parity', f6Fail === 0);

// B3:fmt 插值 parity({n} 占位键抽样 × 三语)
const fmtKeys = ['pb.br.countdown.daysLeft', 'pb.br.cov.askedTimes', 'pb.v2.c.pendingBanner'];
let fmtFail = 0;
for (const k of fmtKeys) {
  for (const l of LANGS) {
    const o = OLD.makeV2Fmt(stub(OLD_CORE, l))(k, { n: 3 });
    const n = NEW.makeV2Fmt(stub(NEW_CORE, l))(k, { n: 3 });
    if (o !== n) { fmtFail++; failures.push(`[B parity] fmt ${k}@${l}: ${o} != ${n}`); }
  }
}
check('B parity', 'fmt 占位插值 parity({n} 键 × 三语)', fmtFail === 0);

// ================================================================ C. boundary 类别(附则§7)
// C1 空/null:key 为 '' / null / undefined(TS 型为 string,运行时兜底)
for (const l of LANGS) {
  check('C 空/null', `空串 key @${l} 两侧均回吐 key 本身`,
    OLD.makeV2T(stub(OLD_CORE, l))('') === NEW.makeV2T(stub(NEW_CORE, l))('') && NEW.makeV2T(stub(NEW_CORE, l))('') === '');
  check('C 空/null', `null key @${l} parity`, OLD.makeV2T(stub(OLD_CORE, l))(null) === NEW.makeV2T(stub(NEW_CORE, l))(null));
  check('C 空/null', `undefined key @${l} parity`, OLD.makeBriefT(stub(OLD_CORE, l))(undefined) === NEW.makeBriefT(stub(NEW_CORE, l))(undefined));
}
// C2 未知键(不存在于任何表)
for (const k of ['pb.v2.definitely.missing', 'no.such.key.at.all', 'pb.br.prep.nope', 'zzz']) {
  for (const l of LANGS) {
    check('C 未知键', `未知键 ${k} @${l} 原样直出且 parity`,
      OLD.makeV2T(stub(OLD_CORE, l))(k) === k && NEW.makeV2T(stub(NEW_CORE, l))(k) === k &&
      OLD.makeBriefT(stub(OLD_CORE, l))(k) === k && NEW.makeBriefT(stub(NEW_CORE, l))(k) === k);
  }
}
// C3 空集合:查找面对空表桩不崩、未知键仍回吐 key
{
  const emptyStub = stub({}, 'zh');
  check('C 空集合', '空 DICT 桩下未知键仍回吐 key', NEW.makeV2T(emptyStub)('pb.v2.x') === 'pb.v2.x');
}
// C4 特殊字符/Unicode/emoji:值含 emoji、×、–、’ 的键 parity(按各自键域走对应接线)
const uniKeys = [
  ['pb.br.rounds.phasePre', 'br'], ['pb.br.rounds.phasePost', 'br'],
  ['pb.br.kw.times', 'br'], ['pb.br.company.empty', 'br'],
  ['pb.v2.c.pendingBanner', 'v2'], ['pb.v2.c.looseHint', 'v2'],
];
let uniFail = 0;
for (const [k, dom] of uniKeys) {
  for (const l of LANGS) {
    const o = dom === 'br' ? OLD.makeBriefT(stub(OLD_CORE, l))(k) : OLD.makeV2T(stub(OLD_CORE, l))(k);
    const n = dom === 'br' ? NEW.makeBriefT(stub(NEW_CORE, l))(k) : NEW.makeV2T(stub(NEW_CORE, l))(k);
    if (o !== n) { uniFail++; failures.push(`[C Unicode] ${k}@${l}: ${o} != ${n}`); }
  }
}
check('C Unicode/emoji', 'emoji/特殊字符值键 parity(📋 📝 × – ’)', uniFail === 0);
// C5 占位符边界:缺参 / 多参 / 参数含 HTML 特殊字符
{
  const l = 'en';
  const k = 'pb.br.countdown.daysLeft';
  check('C 占位符', '缺参时 {n} 原样保留(与旧接线同口径)', NEW.makeV2Fmt(stub(NEW_CORE, l))(k, {}) === OLD.makeV2Fmt(stub(OLD_CORE, l))(k, {}));
  check('C 占位符', '多参时无关参数被忽略', NEW.makeV2Fmt(stub(NEW_CORE, l))(k, { n: 5, z: 'ignored' }) === OLD.makeV2Fmt(stub(OLD_CORE, l))(k, { n: 5, z: 'ignored' }));
  check('C 占位符', '参数含 HTML 特殊字符时原样替换(不转义,口径同旧)',
    NEW.makeV2Fmt(stub(NEW_CORE, l))(k, { n: '<b>&"</b>' }) === OLD.makeV2Fmt(stub(OLD_CORE, l))(k, { n: '<b>&"</b>' }));
}
// C6 fr=en 补齐的 27 键:fr 值与 en 同文,且 fr 模式输出 === en 模式输出(与改前 en 兜底一致)
{
  const noFrKeys = Object.keys(OLD.BR).filter((k) => OLD.BR[k].fr === undefined && !(k in OLD_CORE));
  check('C fr=en', '本地独有缺 fr 键共 27 个', noFrKeys.length === 27, `实际 ${noFrKeys.length}`);
  let frEnFail = 0;
  for (const k of noFrKeys) {
    if (NEW_CORE[k].fr !== NEW_CORE[k].en) frEnFail++;
    const frOut = NEW.makeBriefT(stub(NEW_CORE, 'fr'))(k);
    const enOut = NEW.makeBriefT(stub(NEW_CORE, 'en'))(k);
    const oldFrOut = OLD.makeBriefT(stub(OLD_CORE, 'fr'))(k); // 改前 fr = en 兜底
    if (frOut !== enOut || frOut !== oldFrOut) frEnFail++;
  }
  check('C fr=en', '27 键 fr=en 同文且 fr 模式输出与改前(en 兜底)逐字一致', frEnFail === 0);
}
// C7 重叠 79 键:核心侧条目即最终值,zh/en 与本地表逐字同(防合并时误改)
{
  const overlap = Object.keys(OLD.BR).filter((k) => k in OLD_CORE);
  check('C 重叠键', '重叠键 79 个', overlap.length === 79, `实际 ${overlap.length}`);
  let ovFail = 0;
  for (const k of overlap) {
    if (OLD_CORE[k].zh !== OLD.BR[k].zh || OLD_CORE[k].en !== OLD.BR[k].en) ovFail++;
    if (NEW_CORE[k].fr !== NEW_CORE[k].en) ovFail++; // 既定惯例 fr=en 同文
  }
  check('C 重叠键', '79 重叠键 zh/en 与本地表逐字同、核心侧条目零改动', ovFail === 0);
}

// ================================================================ 汇总
console.log(`\n票43 ARC-13 parity+boundary: PASS ${pass} / FAIL ${fail}`);
if (failures.length) {
  console.log('--- 失败明细(前 25 条)---');
  for (const f of failures.slice(0, 25)) console.log(' ', f);
  process.exit(1);
}
console.log('全部通过:三语输出逐字 parity、键集只增不减、既有条目零改动、boundary 各类别覆盖。');
