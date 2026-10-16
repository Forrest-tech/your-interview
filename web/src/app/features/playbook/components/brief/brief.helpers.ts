import { I18nService } from '../../../../core/i18n/i18n.service';
import { BRIEF_I18N } from './brief.i18n';
import type { BriefQuestion, BriefRound, Keyword, RoundEmailItem } from './brief.models';

/**
 * 各组件共用的 t()：先查本地 BRIEF_I18N（按当前语言），
 * 查不到再回退全局 I18nService —— 所以 common.* / pb.roundOutcome.* 等全局键照样能用。
 * Coordinator 把 BRIEF_I18N 并入全局 service 后，行为不变。
 */
export function makeBriefT(i18n: I18nService): (key: string) => string {
  return (key: string): string => {
    const e = BRIEF_I18N[key];
    if (e) {
      const l = i18n.lang();
      return e[l] ?? e.en ?? e.zh ?? key;
    }
    return i18n.t(key);
  };
}

/** 模板文案插值：fmt('pb.br.countdown.daysLeft', { n: 3 }) */
export function applyParams(s: string, params: Record<string, string | number>): string {
  let out = s;
  for (const [k, v] of Object.entries(params)) out = out.replace(`{${k}}`, String(v));
  return out;
}

/** 解析后端 JSON 数组字段 —— 坏数据/非数组一律当空数组，页面不崩。 */
export function parseJsonList<T>(json: string | undefined | null): T[] {
  if (!json) return [];
  try {
    const v: unknown = JSON.parse(json);
    return Array.isArray(v) ? (v as T[]) : [];
  } catch {
    return [];
  }
}

/** 解析 JSON 字符串数组。 */
export function parseStringList(json: string | undefined | null): string[] {
  return parseJsonList<unknown>(json)
    .map((x) => (typeof x === 'string' ? x : ''))
    .filter((x) => x.length > 0);
}

/** 本地日期 YYYY-MM-DD（按字符串比较，避免时区坑）。 */
export function todayStr(): string {
  const d = new Date();
  return (
    d.getFullYear() +
    '-' + String(d.getMonth() + 1).padStart(2, '0') +
    '-' + String(d.getDate()).padStart(2, '0')
  );
}

/** 距今天的天数：正数=未来，0=今天，负数=过去。 */
export function daysUntil(dateStr: string): number {
  const t = todayStr();
  const a = new Date(t + 'T00:00:00').getTime();
  const b = new Date(dateStr.slice(0, 10) + 'T00:00:00').getTime();
  return Math.round((b - a) / 864e5);
}

/** rounds 里最近的未过期轮次（scheduledDate >= 今天）。 */
export function nextRound(rounds: BriefRound[] | undefined): BriefRound | null {
  const t = todayStr();
  const list = (rounds ?? [])
    .filter((r) => (r.scheduledDate ?? '') >= t)
    .sort(
      (a, b) =>
        (a.scheduledDate ?? '').localeCompare(b.scheduledDate ?? '') || a.order - b.order,
    );
  return list[0] ?? null;
}

/** 轮次结果徽章色档 —— 与 spec F4 约定一致。 */
export function outcomeClass(outcome: string): string {
  if (outcome === 'Passed') return 'ok';
  if (outcome === 'Pending') return 'warn';
  if (outcome === 'Rejected' || outcome === 'Ghosted') return 'bad';
  return 'idle';
}

/** 从各轮 emailsJson 取最近一封邮件的 from + date（F5 第二行用）。 */
export function latestEmail(rounds: BriefRound[] | undefined): { from: string; date: string } | null {
  let best: { from: string; date: string } | null = null;
  for (const r of rounds ?? []) {
    for (const e of parseJsonList<RoundEmailItem>(r.emailsJson)) {
      const d = (e.date ?? '').slice(0, 10);
      if (!e.from && !d) continue;
      if (!best || (d && d > best.date)) best = { from: e.from ?? '', date: d };
    }
  }
  return best;
}

/**
 * 必答题排序：卡壳优先 → 待练 → 练习中 → 已掌握，同级按难度 desc。
 * mastery 语义：0=待练 1=练习中 2=已掌握 3=卡壳；兼容旧 gotStuck 标记。
 */
export function topQuestions(qs: BriefQuestion[], n = 5): BriefQuestion[] {
  const rank = (q: BriefQuestion): number => {
    if ((q.mastery ?? 0) === 3 || q.gotStuck) return 0;
    if ((q.mastery ?? 0) === 0) return 1;
    if ((q.mastery ?? 0) === 1) return 2;
    return 3;
  };
  return [...qs]
    .sort((a, b) => rank(a) - rank(b) || (b.difficulty ?? 0) - (a.difficulty ?? 0))
    .slice(0, n);
}

// ---------------------------------------------------------------- JD 关键词

const EN_STOP = new Set(
  'a,an,the,and,or,but,if,then,else,for,to,of,in,on,at,by,with,from,as,is,are,was,were,be,been,being,have,has,had,do,does,did,will,would,can,could,should,may,might,must,shall,this,that,these,those,it,its,they,them,their,you,your,we,our,us,he,she,his,her,them,not,no,yes,all,any,each,every,both,few,more,most,other,some,such,only,own,same,so,than,too,very,just,also,well,etc,per,via,including,include,includes,included,using,use,used,work,working,join,team,years,year,experience,strong,good,great,excellent,ability,able,help,ensure,provide,develop,development,design,build,building,maintain,support,lead,drive,collaborate,plus,within,across,between,through,during,about,into,over,under,after,before,new,plus,etc,job,role,candidate,looking,seeking,who,what,when,where,how,why,which,while,bonus,nice,plus,you,will'
    .split(','),
);

/** 中文常见虚词：单字 + 无意义 bigram。 */
const ZH_CHAR_STOP = new Set('的了在是有和与或及为之以于等这那这个些们也都还要就对从将并若如其所能会可以应需'.split(''));
const ZH_BIGRAM_STOP = new Set([
  '以及', '等等', '通过', '作为', '由于', '基于', '包括', '例如', '比如',
  '进行', '一种', '一个', '需要', '这个', '这些', '那些',
]);
const isZhStopBigram = (bg: string): boolean =>
  ZH_BIGRAM_STOP.has(bg) || (ZH_CHAR_STOP.has(bg[0]) && ZH_CHAR_STOP.has(bg[1]));

/**
 * JD 关键词提取（v1 简版，不做简历对照）：
 *  - 去标点 → 分词 → 去中英停用词 → 取 top 高频词（长度≥2）。
 *  - 英文按空白分词；中文连续串 2–6 字整体计频，超长切 bigram。
 */
export function extractKeywords(text: string, topN = 24): Keyword[] {
  if (!text) return [];
  const freq = new Map<string, number>();
  const add = (w: string): void => {
    if (w) freq.set(w, (freq.get(w) ?? 0) + 1);
  };

  const ascii = text.toLowerCase().replace(/[^a-z0-9#+.\s]/g, ' ');
  for (const tok of ascii.split(/\s+/)) {
    const w = tok.replace(/^\.+|\.+$/g, '').trim();
    if (w.length >= 2 && !EN_STOP.has(w)) add(w);
  }

  const runs = text.match(/[\u4e00-\u9fff]{2,}/g) ?? [];
  for (const run of runs) {
    if (run.length <= 6) {
      if (!isZhStopBigram(run) && ![...run].every((c) => ZH_CHAR_STOP.has(c))) add(run);
    } else {
      for (let i = 0; i + 1 < run.length; i++) {
        const bg = run.slice(i, i + 2);
        if (!isZhStopBigram(bg)) add(bg);
      }
    }
  }

  return [...freq.entries()]
    .map(([word, count]) => ({ word, count }))
    .sort((a, b) => b.count - a.count || b.word.length - a.word.length)
    .slice(0, topN);
}
