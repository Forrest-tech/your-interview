import { roundDateKey, sortRoundsByDate } from '../v2.helpers';
import {
  AttributionMail,
  AttributionRound,
  RoundSource,
  attributeEmails,
} from './email-round-attribution';

/**
 * 票 311b1：条目邮件时间线的节点模型（纯函数，展示时计算，不写库）。
 *
 * 规格：docs/fix-program/source/specs/ticket311-SPEC.md §2（节点 = 轮次）；
 * 归属一律走 311a 的 attributeEmails（与规格 §3 的已定偏离以 311a 台账为准）。
 * 本刀只产出节点数据，无调用方、无界面；311b3 接组件，311b2 挂入口。
 *
 * 节点规则：
 *  - 每个轮次一个节点（含零邮件轮次），按 sortRoundsByDate 顺序，编号 = 展示编号；
 *  - 未指派节点仅在有未指派邮件时出现，排在最后；
 *  - 节点内邮件按日期正序，同日保持输入原序（稳定排序，不编造时刻）；
 *  - 默认展开（决策 4，2026-11-14 独立评分定）：最后一个轮次节点；未指派节点不参与。
 */

/** 时间线需要的最小资产形状（InterviewAsset 满足）。 */
export interface TimelineAssetLike {
  id: string;
  kind: string;
  uploadedAt?: string | null;
  roundId?: string | null;
  metaJson?: string | null;
}

interface MailMetaLike {
  channel?: string;
  category?: string;
  subject?: string;
  date?: string;
  interviewAt?: string;
}

export interface TimelineMail extends AttributionMail {
  date: string;
}

export interface TimelineNode {
  /** 'round:<id>' 或 'unassigned'。 */
  key: string;
  kind: 'round' | 'unassigned';
  /** 轮次 id；未指派节点为 null。 */
  roundId: string | null;
  /** 展示编号（1 起）；未指派节点为 null。 */
  displayNo: number | null;
  /** 轮次日期 YYYY-MM-DD；无日期或未指派为 ''。 */
  scheduledDate: string;
  /** 节点内邮件 id，日期正序、同日稳定。 */
  mailIds: string[];
  /** 节点内邮件日期跨度（无邮件或无有效日期时为 null）。 */
  firstDate: string | null;
  lastDate: string | null;
  /** 归属依据（311b2 映射成卡片小标签）。 */
  sources: Record<string, RoundSource>;
  /** 需用户核对的邮件 id（311a 规则 2 修正命中）。 */
  needsReviewIds: string[];
}

export interface MailTimeline {
  nodes: TimelineNode[];
  /** 默认展开的节点 key；无轮次时为 null。 */
  defaultOpenKey: string | null;
  /** 参与时间线的邮件总数。 */
  total: number;
}

function parseMeta(json: string | null | undefined): MailMetaLike | null {
  if (!json) return null;
  try {
    const v = JSON.parse(json);
    return v && typeof v === 'object' ? (v as MailMetaLike) : null;
  } catch {
    return null;
  }
}

/** 邮件判定与 materials-list.isEmailAsset 同口径；非邮件返回 null。 */
export function timelineMailOf(a: TimelineAssetLike): TimelineMail | null {
  const m = parseMeta(a.metaJson);
  const isEmail = m?.channel === 'email' || (a.kind === 'Text' && !!m?.category);
  if (!isEmail) return null;
  return {
    id: a.id,
    roundId: a.roundId ?? null,
    subject: m?.subject ?? null,
    // 与 materials-list.displayDate 同口径：meta.date，否则上传时间的日期部
    date: (m?.date ?? a.uploadedAt ?? '').slice(0, 10),
    interviewAt: m?.interviewAt ?? null,
  };
}

const roundKey = (id: string) => 'round:' + id;
export const UNASSIGNED_KEY = 'unassigned';

/** 构建时间线节点。assets 可混有非邮件资产（会被忽略）；同 id 只取第一次出现。 */
export function buildMailTimeline(
  assets: readonly TimelineAssetLike[],
  rounds: readonly AttributionRound[],
): MailTimeline {
  const seen = new Set<string>();
  const mails: TimelineMail[] = [];
  for (const a of assets) {
    if (seen.has(a.id)) continue;
    seen.add(a.id);
    const m = timelineMailOf(a);
    if (m) mails.push(m);
  }

  const sorted = sortRoundsByDate(rounds);
  const attribution = attributeEmails(mails, sorted);

  const nodes: TimelineNode[] = sorted.map((r, i) => ({
    key: roundKey(r.id),
    kind: 'round' as const,
    roundId: r.id,
    displayNo: i + 1,
    scheduledDate: roundDateKey(r),
    mailIds: [],
    firstDate: null,
    lastDate: null,
    sources: {},
    needsReviewIds: [],
  }));
  const byRound = new Map(nodes.map(n => [n.roundId as string, n]));
  const unassigned: TimelineNode = {
    key: UNASSIGNED_KEY,
    kind: 'unassigned',
    roundId: null,
    displayNo: null,
    scheduledDate: '',
    mailIds: [],
    firstDate: null,
    lastDate: null,
    sources: {},
    needsReviewIds: [],
  };

  // 稳定排序：日期正序，同日（或日期缺失）按输入原序
  const order = new Map(mails.map((m, i) => [m.id, i]));
  const byDate = [...mails].sort((x, y) => {
    if (x.date !== y.date) return x.date < y.date ? -1 : 1;
    return (order.get(x.id) ?? 0) - (order.get(y.id) ?? 0);
  });

  for (const m of byDate) {
    const res = attribution.get(m.id)!;
    const node = (res.roundId && byRound.get(res.roundId)) || unassigned;
    node.mailIds.push(m.id);
    node.sources[m.id] = res.source;
    if (res.needsReview) node.needsReviewIds.push(m.id);
    if (/^\d{4}-\d{2}-\d{2}$/.test(m.date)) {
      if (node.firstDate === null || m.date < node.firstDate) node.firstDate = m.date;
      if (node.lastDate === null || m.date > node.lastDate) node.lastDate = m.date;
    }
  }

  if (unassigned.mailIds.length > 0) nodes.push(unassigned);
  const lastRound = sorted.length > 0 ? roundKey(sorted[sorted.length - 1].id) : null;
  return { nodes, defaultOpenKey: lastRound, total: mails.length };
}
