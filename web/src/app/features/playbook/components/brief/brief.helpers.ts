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

/**
 * 极简 Markdown → 安全 HTML(标题/加粗/行内代码/列表/段落)。
 * 与 playbook-detail 的 renderMarkdown 同款逻辑,先转义防 XSS。
 * 备考方案在线阅读 + 打印导出用。
 */
export function renderMarkdown(md: string): string {
  const esc = (md ?? '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const inline = (s: string): string => s
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/`([^`]+)`/g, '<code>$1</code>');
  const lines = esc.split('\n');
  const out: string[] = [];
  let inList = false;
  for (const line of lines) {
    const t = line.trim();
    if (/^#{1,3}\s/.test(t)) {
      if (inList) { out.push('</ul>'); inList = false; }
      const level = t.match(/^#+/)![0].length;
      out.push(`<h${level + 1}>${inline(t.replace(/^#+\s*/, ''))}</h${level + 1}>`);
    } else if (/^[-*]\s/.test(t)) {
      if (!inList) { out.push('<ul>'); inList = true; }
      out.push(`<li>${inline(t.replace(/^[-*]\s*/, ''))}</li>`);
    } else if (/^\d+[.)]\s/.test(t)) {
      if (inList) { out.push('</ul>'); inList = false; }
      out.push(`<p class="md-num">${inline(t)}</p>`);
    } else if (t === '') {
      if (inList) { out.push('</ul>'); inList = false; }
    } else {
      if (inList) { out.push('</ul>'); inList = false; }
      out.push(`<p>${inline(t)}</p>`);
    }
  }
  if (inList) out.push('</ul>');
  return out.join('\n');
}

/** 把各种日期写法归一为 yyyy-MM-dd；无法识别返回空串。 */
export function normalizeEmailDate(s: string): string {
  const t = s.trim();
  const m1 = t.match(/(\d{4})[年\/\-.](\d{1,2})[月\/\-.](\d{1,2})日?/);
  if (m1) {
    const mm = String(+m1[2]).padStart(2, '0');
    const dd = String(+m1[3]).padStart(2, '0');
    return `${m1[1]}-${mm}-${dd}`;
  }
  const months: Record<string, number> = {
    jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6,
    jul: 7, aug: 8, sep: 9, sept: 9, oct: 10, nov: 11, dec: 12,
  };
  const m2 = t.match(/(\d{1,2})\s+([A-Za-z]+)\.?\s+(\d{4})/);
  if (m2 && months[m2[2].toLowerCase()]) {
    const mm = String(months[m2[2].toLowerCase()]).padStart(2, '0');
    const dd = String(+m2[1]).padStart(2, '0');
    return `${m2[3]}-${mm}-${dd}`;
  }
  const m3 = t.match(/([A-Za-z]+)\.?\s+(\d{1,2}),?\s+(\d{4})/);
  if (m3 && months[m3[1].toLowerCase()]) {
    const mm = String(months[m3[1].toLowerCase()]).padStart(2, '0');
    const dd = String(+m3[2]).padStart(2, '0');
    return `${m3[3]}-${mm}-${dd}`;
  }
  const cleaned = t.replace(/\s+at\s+/i, ' ').replace(/GMT[+-]\d+.*$/, '').replace(/\([A-Za-z ]+\)/, '');
  const d = new Date(cleaned);
  if (!isNaN(d.getTime())) {
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${d.getFullYear()}-${mm}-${dd}`;
  }
  return '';
}

/**
 * 粘贴进来的完整邮件一键解析：识别顶部头区块（From/Date/Subject，含中文客户端
 * 发件人/发送时间/主题），其余内容当正文；没有头时全文当正文。
 */
export function parsePastedEmail(raw: string): { from: string; date: string; subject: string; body: string } {
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const headerLine = /^([A-Za-z\u4e00-\u9fa5][\w\u4e00-\u9fa5\- ]{0,20})\s*[:：]\s*(.*)$/;
  const out = { from: '', date: '', subject: '', body: '' };
  let i = 0;
  let lastKey = '';
  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === '') { i++; break; }
    const m = line.match(headerLine);
    if (m) {
      lastKey = m[1].trim().toLowerCase();
      const val = m[2].trim();
      if (lastKey === 'from' || lastKey === '发件人') out.from = val.replace(/^"|"$/g, '');
      else if (lastKey === 'date' || lastKey === 'sent' || lastKey === '发送时间' || lastKey === '发送日期') out.date = val;
      else if (lastKey === 'subject' || lastKey === '主题' || lastKey === '标题') out.subject = val;
      i++;
    } else if (/^\s+/.test(line) && lastKey) {
      // 折行续接上一条头
      const val = line.trim();
      if (lastKey === 'subject') out.subject += ' ' + val;
      else if (lastKey === 'from') out.from += ' ' + val;
      i++;
    } else {
      break;
    }
  }
  // 没扫到任何头 → 全文都是正文
  if (!out.from && !out.date && !out.subject) {
    return { from: '', date: '', subject: '', body: raw.trim() };
  }
  out.date = normalizeEmailDate(out.date);
  out.body = lines.slice(i).join('\n').trim();
  return out;
}
