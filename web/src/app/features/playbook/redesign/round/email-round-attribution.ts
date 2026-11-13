import { normalizeThreadSubject, roundDateKey, sortRoundsByDate } from '../v2.helpers';

/**
 * 票 311a：邮件 → 轮次节点的归属（纯函数，展示时计算，不写库）。
 *
 * 性质：只读输入、只产出结果；不改 asset.roundId、不碰导入/匹配/轮次指派/分类。
 * 合入后没有调用方，对用户零影响；311b 系列再接视图。
 *
 * 优先级（先命中者胜）：
 *  1 主题轮次标记（中/英/法，先剥 Re:/Fwd: 前缀）→ 映射到“展示编号”（日期序位置），序数越界则落入规则 2；
 *  2 已存储 roundId（在轮次列表里找得到才算）。例外（待核对）：条目有后续轮次，邮件存的是第 N 轮、
 *    该轮有日期，且邮件日期比它晚超过 3 天 → 不再信任，归未指派并标 needsReview；
 *  3 解析面试时间与有日期轮次的最近者；仅在“最近者唯一、距离 ≤ 3 天、邮件非周报/摘要类”时生效；
 *  4 未指派。
 *
 * 平局：规则 3 距离并列 → 不猜，落未指派（宁缺毋错）。
 */

/** 归属依据（311b2 把它映射成卡片小标签）。 */
export type RoundSource = 'subject' | 'stored' | 'proximity' | 'unassigned';

export interface AttributionMail {
  id: string;
  /** 已存储的轮次 id（asset.roundId）。 */
  roundId?: string | null;
  /** 原文主题（meta.subject），不是短标题。 */
  subject?: string | null;
  /** 邮件日期 YYYY-MM-DD（meta.date 或 uploadedAt 的日期部）。 */
  date?: string | null;
  /** 解析出的面试时间（meta.interviewAt，形如 yyyy-MM-dd HH:mm）。 */
  interviewAt?: string | null;
}

export interface AttributionRound {
  id: string;
  order: number;
  scheduledDate?: string | null;
}

export interface AttributionResult {
  /** 归属的轮次 id；null = 未指派。 */
  roundId: string | null;
  source: RoundSource;
  /** 规则 2 的修正命中：存储的轮次值得怀疑，提示用户核对。 */
  needsReview: boolean;
}

/** 距离与修正窗口（天）。 */
export const ATTRIBUTION_WINDOW_DAYS = 3;

const ZH_DIGITS: Record<string, number> = {
  一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10,
};
const EN_WORDS: Record<string, number> = {
  first: 1, second: 2, third: 3, fourth: 4, fifth: 5, sixth: 6, seventh: 7, eighth: 8,
};
const FR_WORDS: Record<string, number> = {
  premier: 1, première: 1, premiere: 1,
  deuxième: 2, deuxieme: 2, second: 2, seconde: 2,
  troisième: 3, troisieme: 3,
  quatrième: 4, quatrieme: 4,
  cinquième: 5, cinquieme: 5,
};

/** 周报/摘要类主题：它们的 interviewAt 只是“正文第一场会议”，不能用于贴近度归属。 */
const DIGEST_RE = new RegExp(
  [
    'upcoming meetings', 'your week', 'weekly', 'digest', 'agenda', 'newsletter', 'summary of',
    '本周', '周报', '日程', '议程',
    'hebdomadaire', 'hebdo', 'ordre du jour',
  ].join('|'),
  'i',
);

export function isDigestSubject(subject: string | null | undefined): boolean {
  return DIGEST_RE.test(subject ?? '');
}

/**
 * 从原文主题取轮次序数（1 起）；无标记返回 null。
 * 只认“序数 + 轮/round/tour/entretien/interview”这类显式写法，只取第一个命中。
 */
export function extractRoundOrdinal(subject: string | null | undefined): number | null {
  const s = normalizeThreadSubject(subject).display;
  if (!s) return null;
  const hits: Array<{ idx: number; n: number }> = [];
  const add = (m: RegExpExecArray | null, n: number | undefined) => {
    if (m && n !== undefined && Number.isFinite(n) && n >= 1) hits.push({ idx: m.index, n });
  };

  // 英：1st/2nd/3rd/4th round|interview|stage
  {
    const m = /(\d{1,2})\s*(?:st|nd|rd|th)\s+(?:round|interview|stage)\b/i.exec(s);
    if (m) add(m, parseInt(m[1], 10));
  }
  // 英：first/second/third round|interview
  {
    const m = /\b(first|second|third|fourth|fifth|sixth|seventh|eighth)\s+(?:round|interview|stage)\b/i.exec(s);
    if (m) add(m, EN_WORDS[m[1].toLowerCase()]);
  }
  // 英：Round 2 / Interview Round 2
  {
    const m = /\bround\s+(\d{1,2})\b/i.exec(s);
    if (m) add(m, parseInt(m[1], 10));
  }
  // 中：第2轮 / 第二轮 / 二面
  {
    const m = /第\s*(\d{1,2})\s*轮/.exec(s);
    if (m) add(m, parseInt(m[1], 10));
  }
  {
    const m = /第\s*([一二三四五六七八九十])\s*轮/.exec(s);
    if (m) add(m, ZH_DIGITS[m[1]]);
  }
  {
    const m = /([二三四五六])面(?=邀请|通知|安排|反馈|结果|时间|面试|\s|$|[，,。：:—-])/.exec(s);
    if (m) add(m, ZH_DIGITS[m[1]]);
  }
  // 法：1er/2e/3ème tour|entretien；deuxième entretien；tour 2
  {
    const m = /\b(\d{1,2})\s*(?:er|re|e|ème|eme)\s+(?:tour|entretien)\b/i.exec(s);
    if (m) add(m, parseInt(m[1], 10));
  }
  {
    const m = /\b(premier|première|premiere|deuxième|deuxieme|second|seconde|troisième|troisieme|quatrième|quatrieme|cinquième|cinquieme)\s+(?:tour|entretien)\b/i.exec(s);
    if (m) add(m, FR_WORDS[m[1].toLowerCase()]);
  }
  {
    const m = /\btour\s+(\d{1,2})\b/i.exec(s);
    if (m) add(m, parseInt(m[1], 10));
  }
  if (hits.length === 0) return null;
  hits.sort((x, y) => x.idx - y.idx);
  return hits[0].n;
}

/** YYYY-MM-DD → UTC 日序号；不合法返回 null（用 Date.UTC，不受时区影响）。 */
function dayNumber(ymd: string | null | undefined): number | null {
  const d = (ymd ?? '').slice(0, 10);
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(d);
  if (!m) return null;
  const y = +m[1], mo = +m[2], da = +m[3];
  const t = Date.UTC(y, mo - 1, da);
  const chk = new Date(t);
  if (chk.getUTCFullYear() !== y || chk.getUTCMonth() !== mo - 1 || chk.getUTCDate() !== da) return null;
  return Math.floor(t / 86_400_000);
}

const unassigned = (needsReview = false): AttributionResult => ({ roundId: null, source: 'unassigned', needsReview });

/** 单封邮件的归属。 */
export function attributeEmail(
  mail: AttributionMail,
  rounds: readonly AttributionRound[],
): AttributionResult {
  if (rounds.length === 0) return unassigned();
  const sorted = sortRoundsByDate(rounds);

  // 规则 1：主题轮次标记 → 展示编号（日期序位置）
  const ord = extractRoundOrdinal(mail.subject);
  if (ord !== null && ord <= sorted.length) {
    return { roundId: sorted[ord - 1].id, source: 'subject', needsReview: false };
  }

  // 规则 2：已存储 roundId
  if (mail.roundId) {
    const idx = sorted.findIndex(r => r.id === mail.roundId);
    if (idx >= 0) {
      const stored = sorted[idx];
      const mailDay = dayNumber(mail.date);
      const roundDay = dayNumber(roundDateKey(stored));
      const hasLater = idx < sorted.length - 1;
      if (hasLater && mailDay !== null && roundDay !== null && mailDay - roundDay > ATTRIBUTION_WINDOW_DAYS) {
        return unassigned(true);
      }
      return { roundId: stored.id, source: 'stored', needsReview: false };
    }
    // roundId 悬空 → 视同空，落入规则 3
  }

  // 规则 3：面试时间贴近度（保守：唯一、≤3 天、非周报）
  const atDay = dayNumber((mail.interviewAt ?? '').slice(0, 10));
  if (atDay !== null && !isDigestSubject(mail.subject)) {
    const dated = sorted
      .map(r => ({ r, day: dayNumber(roundDateKey(r)) }))
      .filter((x): x is { r: AttributionRound; day: number } => x.day !== null)
      .map(x => ({ r: x.r, dist: Math.abs(atDay - x.day) }))
      .sort((a, b) => a.dist - b.dist);
    if (dated.length > 0) {
      const nearest = dated[0];
      const unique = dated.length === 1 || nearest.dist < dated[1].dist;
      if (unique && nearest.dist <= ATTRIBUTION_WINDOW_DAYS) {
        return { roundId: nearest.r.id, source: 'proximity', needsReview: false };
      }
    }
  }

  // 规则 4：未指派
  return unassigned();
}

/** 批量：返回 mail.id → 归属。顺序无关。 */
export function attributeEmails(
  mails: readonly AttributionMail[],
  rounds: readonly AttributionRound[],
): Map<string, AttributionResult> {
  const out = new Map<string, AttributionResult>();
  for (const m of mails) out.set(m.id, attributeEmail(m, rounds));
  return out;
}
