import type { Lang } from './i18n.service';

/**
 * FUN-07(票35):后端结构化消息(Insight / parse MessageCodes / CoverLetter MissingCode)
 * 与维度名的前端本地化纯函数。本文件无副作用、不依赖 Angular,供票 64(ARC-08c Jest)
 * 接 spec 覆盖;本票以 evidence-35/helper-boundary-test.js(Node 直跑编译产物)验证 boundary。
 *
 * t 口径与 I18nService.t 一致:命中返回译文,未命中返回 key 本身 —— 故「译文 !== key」
 * 即视为命中。tf 口径同 I18nService.tf({param} 替换)。
 */
export type TranslateFn = (key: string) => string;
export type FormatFn = (
  key: string,
  params: Record<string, string | number | null | undefined>
) => string;

/** 后端 InsightDto 的前端形状(与 AnalyticsHandlers.InsightDto 对齐)。 */
export interface InsightShape {
  code?: string | null;
  params?: Record<string, string> | null;
  severity?: string | null;
}

/**
 * 维度名(B3 口径,Dashboard/Analytics 共用):优先 dim.<dimension> 字典译文;
 * 缺键时仅 zh 回退后端 nameZh,EN/FR 回退 dimension 原值(绝不把中文名直出到非中文界面)。
 * boundary:dimension 空/null → '';nameZh 空/undefined → dimension 原值。
 */
export function dimensionLabel(
  dimension: string | null | undefined,
  nameZh: string | null | undefined,
  lang: Lang,
  t: TranslateFn
): string {
  if (!dimension) return '';
  const key = 'dim.' + dimension;
  const translated = t(key);
  if (translated !== key) return translated;
  if (lang === 'zh' && nameZh) return nameZh;
  return dimension;
}

/** Insight 参数里的维度键参数名 —— 这些参数的值是 dimension 键,呈现前先本地化为维度名。 */
const INSIGHT_DIM_PARAMS = new Set(['dimension', 'strongestDimension', 'weakestDimension']);

/**
 * 后端 Insight(Code+Params)→ 当前语言的三段文案。
 * boundary:insight 为 null/undefined 或 code 为空 → null(调用方走无洞察态);
 * 未知 code → 三段空串(不直出 code 本身,避免把内部码漏给用户)。
 */
export function localizeInsight(
  insight: InsightShape | null | undefined,
  lang: Lang,
  t: TranslateFn,
  tf: FormatFn
): { headline: string; detail: string; action: string } | null {
  if (!insight || !insight.code) return null;
  const base = 'insight.' + insight.code;
  // 已知码判定:headline 键命中字典才算已知码
  if (t(base + '.headline') === base + '.headline') {
    return { headline: '', detail: '', action: '' };
  }
  const raw: Record<string, string> = { ...(insight.params ?? {}) };
  const params: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw)) {
    params[k] = INSIGHT_DIM_PARAMS.has(k) ? dimensionLabel(v, undefined, lang, t) : v;
  }
  const actionKey = base + '.action';
  return {
    headline: tf(base + '.headline', params),
    detail: tf(base + '.detail', params),
    action: t(actionKey) === actionKey ? '' : tf(actionKey, params)
  };
}

/**
 * parse MessageCodes → 当前语言提示串,按码序拼接。
 * 每码映射 parse.<code 后段> 键:码本身形如 "Parse.JdOk",键为 "parse.jdOk"(首段小写)。
 * boundary:null/空数组 → '';未知码跳过(不直出内部码);分隔符由调用方给
 * (JD 原口径码间带空格,Invite 原口径无空格 —— 与改前拼接逐字对齐)。
 */
export function parseMessageText(
  codes: readonly string[] | null | undefined,
  t: TranslateFn,
  separator: string
): string {
  if (!codes || codes.length === 0) return '';
  const parts: string[] = [];
  for (const code of codes) {
    const key = 'parse.' + code.replace(/^Parse\./, '').replace(/^[A-Z]/, (c) => c.toLowerCase());
    const text = t(key);
    if (text !== key) parts.push(text);
  }
  return parts.join(separator);
}

/** CoverLetter MissingCode → i18n 键;未知/null 码 → null(调用方不显示横幅)。 */
export function coverLetterMissingKey(code: string | null | undefined): string | null {
  switch (code) {
    case 'MissingResume':
      return 'tracker.dClMissingResume';
    case 'MissingJd':
      return 'tracker.dClMissingJd';
    case 'MissingCompanyProfile':
      return 'tracker.dClMissingProfile';
    default:
      return null;
  }
}

/**
 * 日历星期头(周一起始,A10):按界面语言取。
 * zh 与改前逐字一致(一 二 三 四 五 六 日);en Mon…;fr lun.…(法语星期缩写惯例带点)。
 */
export function weekDayHeaders(lang: Lang): string[] {
  switch (lang) {
    case 'zh':
      return ['一', '二', '三', '四', '五', '六', '日'];
    case 'fr':
      return ['lun.', 'mar.', 'mer.', 'jeu.', 'ven.', 'sam.', 'dim.'];
    default:
      return ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  }
}

/**
 * Upcoming 轮次 stage 存储值 → 本地化标签:已知规范值(英文码 Screen/Technical/… 与
 * Playbook 中文存储值 电话初筛/技术一面/…)映射字典键,未知自由文本原样显示
 * (同 v2 formatLabel 先例:用户数据值不翻译)。带补充说明的以已知值作前缀映射、余文保留。
 */
const STAGE_KEYS: Record<string, string> = {
  Screen: 'tracker.rtScreen',
  Technical: 'tracker.rtTechnical',
  SystemDesign: 'tracker.rtSystemDesign',
  Behavioral: 'tracker.rtBehavioral',
  Final: 'tracker.rtFinal',
  '电话初筛': 'pb.format.phoneScreen',
  '技术一面': 'pb.format.techRound1',
  '技术二面': 'pb.format.techRound2',
  '系统设计': 'pb.format.systemDesign',
  '行为面': 'pb.format.behavioral',
  '终面': 'pb.format.final',
  '其他': 'pb.format.other'
};

export function stageLabel(value: string | null | undefined, t: TranslateFn): string {
  if (!value) return '';
  const key = STAGE_KEYS[value];
  if (key) {
    const translated = t(key);
    return translated !== key ? translated : value;
  }
  for (const stored of Object.keys(STAGE_KEYS).sort((a, b) => b.length - a.length)) {
    if (value.startsWith(stored)) {
      const k = STAGE_KEYS[stored];
      const translated = t(k);
      return translated !== k ? translated + value.slice(stored.length) : value;
    }
  }
  return value;
}

/** Upcoming 轮次 format 存储值(英文码 Phone/Video/Onsite)→ 本地化标签;未知原样。 */
const FORMAT_KEYS: Record<string, string> = {
  Phone: 'tracker.fmPhone',
  Video: 'tracker.fmVideo',
  Onsite: 'tracker.fmOnsite'
};

export function roundFormatLabel(value: string | null | undefined, t: TranslateFn): string {
  if (!value) return '';
  const key = FORMAT_KEYS[value];
  if (!key) return value;
  const translated = t(key);
  return translated !== key ? translated : value;
}
