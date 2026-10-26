import { I18nService } from '../../../core/i18n/i18n.service';
import { applyParams } from '../components/brief/brief.helpers';
import { safeJson } from './v2.models';

/**
 * v2 组件共用的 t():票43 ARC-13 起本地 V2_I18N 已全量并入核心
 * I18nService 字典(pb.v2.* 命名空间,键名与三语值逐字不变),
 * 本函数只作签名兼容的直委托 —— 与 brief 的 makeBriefT 同一口径。
 */
export function makeV2T(i18n: I18nService): (key: string) => string {
  return (key: string): string => i18n.t(key);
}

/** 带 {param} 插值的版本:fmt('pb.v2.d.scorePts', { n: 87 }) */
export function makeV2Fmt(
  i18n: I18nService
): (key: string, params: Record<string, string | number>) => string {
  const t = makeV2T(i18n);
  return (key, params) => applyParams(t(key), params);
}

/** 人名列表的连接符:中文用顿号,其它语言用逗号。 */
export function nameSep(i18n: I18nService): string {
  return i18n.lang() === 'zh' ? '、' : ', ';
}

export function joinNames(i18n: I18nService, names: string[]): string {
  return names.join(nameSep(i18n));
}

/**
 * 面试形式是存储值(如 intake 解析出的「线上视频」会原样存进 round.format / meta.format),
 * 存储值不改;展示时把已知的四种规范值映射成本地化标签,未知值原样显示。
 */
const FORMAT_KEYS: Record<string, string> = {
  '线上视频': 'pb.v2.fmt.video',
  '线上做题平台': 'pb.v2.fmt.platform',
  '电话': 'pb.v2.fmt.phone',
  '现场': 'pb.v2.fmt.onsite'
};

export function formatLabel(t: (key: string) => string, v: string | null | undefined): string {
  if (!v) return '';
  const key = FORMAT_KEYS[v];
  if (key) return t(key);
  // 存储值常带补充说明(「线上视频(Teams)」「现场(Toronto 办公室)」):
  // 映射已知前缀,其余原文保留。
  for (const stored of Object.keys(FORMAT_KEYS).sort((a, b) => b.length - a.length)) {
    if (v.startsWith(stored)) return t(FORMAT_KEYS[stored]) + v.slice(stored.length);
  }
  return v;
}

/**
 * 把可能混着整页 HTML 的 JD / 公司介绍文本清成纯文本(展示层清洗,存量数据不动):
 *  - head / script / style / title / meta 等整段丢弃,内容不进正文;
 *  - <br> 与块级标签(p/li/div/section/h1-h6/tr 等)转成换行;
 *  - 其余标签剥掉,只留文本;HTML 实体解码;连续空行收成一行。
 * 纯文本输入基本原样返回(只做空白规范化)。
 */
export function htmlToPlainText(raw: string | null | undefined): string {
  if (!raw) return '';
  let s = raw;
  if (!/<[a-z!/][^>]*>/i.test(s)) {
    return s.replace(/\r\n?/g, '\n').trim();
  }
  s = s
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<head\b[^>]*>[\s\S]*?<\/head\s*>/gi, '')
    .replace(/<script\b[^>]*>[\s\S]*?<\/script\s*>/gi, '')
    .replace(/<style\b[^>]*>[\s\S]*?<\/style\s*>/gi, '')
    .replace(/<title\b[^>]*>[\s\S]*?<\/title\s*>/gi, '')
    .replace(/<noscript\b[^>]*>[\s\S]*?<\/noscript\s*>/gi, '')
    .replace(/<svg\b[^>]*>[\s\S]*?<\/svg\s*>/gi, '');
  // 只剩零散的自闭合杂标签(meta/link/base 等)直接剥掉
  s = s.replace(/<(meta|link|base|input|img|hr)\b[^>]*>/gi, '');
  // 块级结构 → 换行;列表项加项目符号前缀,与原展示习惯接近
  s = s
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<li\b[^>]*>/gi, '\n• ')
    .replace(/<\/(p|div|section|article|header|footer|ul|ol|li|h[1-6]|tr|table|blockquote|pre)\s*>/gi, '\n')
    .replace(/<(p|div|section|article|header|footer|ul|ol|h[1-6]|tr|blockquote|pre)\b[^>]*>/gi, '\n');
  // 其余标签一律剥除
  s = s.replace(/<[^>]+>/g, '');
  // 实体解码(DOM 解析一遍最稳;无 DOM 环境退回手工替换)
  s = decodeEntities(s);
  return s
    .split('\n')
    .map(line => line.replace(/[ \t ]+/g, ' ').trim())
    .filter((line, i, arr) => line !== '' || (i > 0 && arr[i - 1] !== ''))
    .join('\n')
    .trim();
}

function decodeEntities(s: string): string {
  if (typeof document !== 'undefined') {
    const ta = document.createElement('textarea');
    ta.innerHTML = s;
    return ta.value;
  }
  return s
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'");
}

/**
 * 票 173:四格条 Resume Score 的 JD 输入口径 —— 与 Tracker Score 页同源同值,
 * 但比旧实现多一层回落。旧实现只读投递记录(application)的 jdText/jdSummary:
 * 当投递记录未关联、关联失效或其 JD 字段为空时,四格条明明展示着条目 JD
 * (条目优先)却算不出分,显示 Not scored yet,与 Score 页的已算分数对不上。
 * 回落链(前两项与 Tracker recomputeMatch 逐字一致,故关联正常时分值一字不动):
 * 投递 jdText → 投递 jdSummary → 条目 jdText → 条目 jdSummary。
 * 条目字段走与展示同一口径的 htmlToPlainText;投递字段保持 Tracker 原样不剥离。
 * 返回 '' = 两侧 JD 全空,调用方按无分处理,绝不编造。
 */
export function resolveScoreJd(
  app: { jdText?: string | null; jdSummary?: string | null } | null | undefined,
  entry: { jdText?: string | null; jdSummary?: string | null } | null | undefined
): string {
  return (app?.jdText ?? '').trim()
    || (app?.jdSummary ?? '').trim()
    || htmlToPlainText(entry?.jdText)
    || htmlToPlainText(entry?.jdSummary);
}

/**
 * 票 173:分数档位标签键 —— 与 Tracker 对话框 matchTierLabel 阈值逐字一致
 * (>=70 强匹配,>=50 一般匹配,否则弱匹配),四格条与 Score 页圆环标签同值。
 */
export function scoreTierKey(score: number): 'tracker.dStrongMatch' | 'tracker.dMedMatch' | 'tracker.dWeakMatch' {
  if (score >= 70) return 'tracker.dStrongMatch';
  if (score >= 50) return 'tracker.dMedMatch';
  return 'tracker.dWeakMatch';
}

/**
 * 票 183:轮次轨道节点状态判定 —— 业内 ATS 的步骤状态与步骤结果分开:
 * 终态 outcome(Passed / Failed / Cancelled)优先,不受日期影响;非终态
 * (Pending、空串、未知遗留值)一律看日期,日粒度(今天算未进行);
 * scheduledDate 为空或不可解析 → 未安排;不设 No-show 档(无字段支撑)。
 * today 由调用方归零到日初传入,函数内再归零一次以保证日粒度。
 */
export function resolveRoundNodeStatus(
  outcome: string,
  scheduledDate: string | undefined,
  today: Date
): 'unscheduled' | 'scheduled' | 'completed' | 'passed' | 'failed' | 'cancelled' {
  if (outcome === 'Passed') return 'passed';
  if (outcome === 'Failed') return 'failed';
  if (outcome === 'Cancelled') return 'cancelled';
  if (!scheduledDate) return 'unscheduled';
  const d = new Date(scheduledDate);
  if (Number.isNaN(d.getTime())) return 'unscheduled';
  const day = new Date(d);
  day.setHours(0, 0, 0, 0);
  const t = new Date(today);
  t.setHours(0, 0, 0, 0);
  return day.getTime() < t.getTime() ? 'completed' : 'scheduled';
}

/**
 * 文件资产里区分真录音与截图:两者共用同一条文件管线落库(kind 都是 Audio),
 * 判别靠 contentType 优先、扩展名兜底 —— 否则截图会被当成录音塞进播放器
 * (2026-10-21 审计实测:同轮先落截图再传录音时,实录播放条指向了 PNG)。
 */
export function isAudioAsset(a: { kind: string; contentType?: string | null; fileName?: string | null }): boolean {
  if (a.kind !== 'Audio') return false;
  if (a.contentType) return a.contentType.startsWith('audio/');
  return /\.(mp3|m4a|wav|webm|ogg)$/i.test(a.fileName ?? '');
}

/**
 * 材料 Before/After(面试前/后)判定(2026-10-21 用户裁决,展示与落值共用同一规则):
 * 材料日期与所属轮次日期按日比较 —— 严格早于轮次日 → Pre、严格晚于 → Post;
 * 同日或任一日期缺失(无从比较)→ 回落旧类型规则(邮件 Pre、其余 Post)。
 * 存量材料不做数据迁移:展示时按同一规则推导纠正,新保存的材料落值也走此函数。
 */
export function derivePhase(
  isEmail: boolean,
  materialDate?: string | null,
  roundDate?: string | null
): 'Pre' | 'Post' {
  const md = (materialDate ?? '').slice(0, 10);
  const rd = (roundDate ?? '').slice(0, 10);
  if (md && rd) {
    if (md < rd) return 'Pre';
    if (md > rd) return 'Post';
  }
  return isEmail ? 'Pre' : 'Post';
}

/**
 * 票159 备考输入 purpose 标签（写进 AssetMeta.purpose，全仓唯一键名）。
 * 面经 / 简历要点以 kind=Notes 的资产随轮次归档；Prep 页读回与生成取数
 * 只认这两个值，其余 Notes 资产不受影响。
 */
export const PREP_INPUT_EXPERIENCES = 'prep-input-experiences';
export const PREP_INPUT_RESUME = 'prep-input-resume';
export type PrepInputPurpose = typeof PREP_INPUT_EXPERIENCES | typeof PREP_INPUT_RESUME;

export function isPrepInputPurpose(p: string | null | undefined): p is PrepInputPurpose {
  return p === PREP_INPUT_EXPERIENCES || p === PREP_INPUT_RESUME;
}

/** 备考输入类型 → purpose 标签。 */
export function prepInputPurposeOf(type: 'experiences' | 'resume'): PrepInputPurpose {
  return type === 'experiences' ? PREP_INPUT_EXPERIENCES : PREP_INPUT_RESUME;
}

/**
 * 同轮同 purpose 的最新一份（uploadedAt 降序；uploadedAt 为 ISO 字符串，字典序即时间序）。
 * 旧份保留可见、删除最新一份后次新一份自动晋升——最新一份为准是唯一规则，移动不设特例。
 */
export function latestPrepInputAsset<T extends { roundId?: string | null; uploadedAt?: string; metaJson?: string | null }>(
  assets: readonly T[] | null | undefined, roundId: string, purpose: PrepInputPurpose): T | null {
  let best: T | null = null;
  for (const a of assets ?? []) {
    if ((a.roundId ?? null) !== roundId) continue;
    if (safeJson<{ purpose?: string }>(a.metaJson)?.purpose !== purpose) continue;
    if (!best || (a.uploadedAt ?? '') > (best.uploadedAt ?? '')) best = a;
  }
  return best;
}

/**
 * 邮件主题归一化(2026-10-21 定稿第九节,批量解析预览聚合与 Communications
 * 聚合展示共用同一口径):剥掉 Re:/Fwd:/Fw:/回复:/转发: 等回复转发前缀
 * (可叠多层)后比对。display 保留原文大小写(展示用),key 为小写(分组用)。
 */
export function normalizeThreadSubject(subject?: string | null): { display: string; key: string } {
  let s = (subject ?? '').replace(/\s+/g, ' ').trim();
  for (let i = 0; i < 6; i++) {
    const m = s.match(/^(re|fwd|fw|回复|转发|答复)\s*[:：]\s*/i);
    if (!m) break;
    s = s.slice(m[0].length).trim();
  }
  return { display: s, key: s.toLowerCase() };
}

// ============================================================================
//  轮次日期序展示编号 —— 票30(REQ-10):
//  存储 order 是创建序(后端 AddRound = max+1 落库),只作数据关联键保留;
//  展示编号改为日期序位置:有日期轮次按 scheduledDate 升序、同日按 order
//  (= 创建序),无日期轮次排在有日期之后、按 order。补录的早日期轮次因此
//  拿到小编号,不再因为创建晚而成大编号排末尾。
//  日期只做 YYYY-MM-DD 字符串比较,绝不构造 Date —— 避免时区偏移把日期挪一天。
// ============================================================================

/** 轮次排序所需的最小结构(InterviewRound 满足)。 */
export interface RoundDateSortable {
  id: string;
  order: number;
  scheduledDate?: string | null;
}

/** 取 scheduledDate 的日期部;须合 YYYY-MM-DD 形态才算有日期,否则 ''(无日期)。 */
export function roundDateKey(r: RoundDateSortable): string {
  const d = (r.scheduledDate ?? '').slice(0, 10);
  return /^\d{4}-\d{2}-\d{2}$/.test(d) ? d : '';
}

export function compareRoundsByDate(a: RoundDateSortable, b: RoundDateSortable): number {
  const da = roundDateKey(a);
  const db = roundDateKey(b);
  if (da && db && da !== db) return da < db ? -1 : 1;
  if (da && !db) return -1;
  if (!da && db) return 1;
  // 同日 / 都无日期:按 order(= 创建序);order 再同按 id 保证全序稳定
  if (a.order !== b.order) return a.order - b.order;
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/** 日期序排序副本(不改原数组)。 */
export function sortRoundsByDate<T extends RoundDateSortable>(rounds: readonly T[]): T[] {
  return [...rounds].sort(compareRoundsByDate);
}

/** 展示编号 = 轮次在日期序中的位置(1 起);不在列表中时回落存储 order。 */
export function roundDisplayNo(rounds: readonly RoundDateSortable[], round: RoundDateSortable): number {
  const idx = sortRoundsByDate(rounds).findIndex(r => r.id === round.id);
  return idx >= 0 ? idx + 1 : round.order;
}

// ============================================================================
//  条目结果归类 —— 票05(ARC-14)起判读收归后端单一事实源:
//  条目列表/详情 DTO 的 outcome 由后端 DeriveOutcome 派生(当前轮轮次枚举
//  为准 → Result 受控词表精确解析 → 与票04 Jobs 侧 LegacyOutcomeTextAdapter
//  对照的关键词回落),值域为 SharedContracts InterviewOutcome 枚举名。
//  前端只做「枚举 → 分组/徽章」的展示映射,不许再对 result 自由文本做任何
//  关键词判读(旧本地实现已删除,勿在任何组件里恢复一份)。
//  分组口径不变:终局枚举 → Completed;Pending / 无 outcome → In Progress。
// ============================================================================

/** 后端结构化结果枚举名(SharedContracts InterviewOutcome)。 */
export type EntryOutcome =
  | 'Pending' | 'Passed' | 'Rejected' | 'Ghosted' | 'Cancelled' | 'NoShow';

/** 结果归类:终局三态,或 null = 仍在进行中。入参是后端 outcome,不是 result 原文。 */
export type EntryResultOutcome = 'rejected' | 'positive' | 'closed' | null;

export function entryResultOutcome(outcome: string | null | undefined): EntryResultOutcome {
  switch (outcome) {
    case 'Rejected': return 'rejected';
    case 'Passed': return 'positive';
    case 'Ghosted':
    case 'Cancelled':
    case 'NoShow': return 'closed';
    default: return null; // Pending / null / 未知值 → In Progress
  }
}

/**
 * 详情页头部徽章文案键(票80):与分组共用 entryResultOutcome 单一事实源。
 * 无终局(Pending / null / 未知)→ 进行中键;终局 → 对应枚举的全局文案键
 * (终局时 entryResultOutcome 非 null 保证 outcome 必为五枚举之一)。
 * 不判读 result 原文、不看条目分析状态(status),冲突时以 outcome 为准。
 */
export function entryStatusBadgeKey(outcome: string | null | undefined): string {
  if (entryResultOutcome(outcome) === null) return 'pb.v2.d.statusProgress';
  return `pb.roundOutcome.${outcome}`;
}

/**
 * 票116 F-H1:待办 toggle 的 PATCH 失败后,checkbox 应呈现的勾选态。
 * 服务端真值优先:取回读(actions)中该行的 isDone;回读缺失/无此行时
 * 回落点选前的真值(失败即未变更,点前值就是最后已知真值)。
 * 纯函数供组件回写 DOM 与 Jest 断言共用。
 */
export function resolveActionDoneState(
  actions: readonly { id: string; isDone: boolean }[] | null | undefined,
  actionId: string,
  fallbackDone: boolean
): boolean {
  return actions?.find(a => a.id === actionId)?.isDone ?? fallbackDone;
}

/** Completed 排序键:InterviewDate(回落 CreatedAt 日期)—— 与列表页口径一致。 */
export function entryEndKey(e: { interviewDate?: string | null; createdAt?: string | null }): string {
  return e.interviewDate || (e.createdAt ?? '').slice(0, 10);
}

/**
 * 票186:进入 Active 日期格式化(只日期,不要时间)。
 * 时区感知:用账号时区(默认 America/Toronto),绝不对 UTC ISO 串 slice 取日
 * —— EDT 晚上创建的条目会被算成 UTC 的次日(与 Simplify UTC 日期问题同类)。
 * 非法 ISO / 非法时区 → 回落浏览器本地日期,绝不让脏数据炸掉整页。
 */
export function formatEnteredDate(iso: string, timeZone: string, locale: string): string {
  try {
    return new Intl.DateTimeFormat(locale, {
      timeZone,
      dateStyle: 'medium'
    }).format(new Date(iso));
  } catch {
    return new Date(iso).toLocaleDateString(locale);
  }
}

const EMAIL_HEAD_MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * 票210：邮件卡收起态右上时间 —— 面试时间 interviewAt（yyyy-MM-dd HH:mm 或 ISO）
 * 格式化为 "Oct 1 · 13:00"；空值回落 ''，非法值回落原文，绝不抛错。
 */
export function formatInterviewHeadTime(v?: string | null): string {
  if (!v) return '';
  const m = v.match(/^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})/);
  if (!m) return v;
  const mon = EMAIL_HEAD_MONTHS[Number(m[2]) - 1];
  if (!mon) return v;
  return `${mon} ${Number(m[3])} · ${m[4]}:${m[5]}`;
}

/**
 * 票210：邮件卡提炼短标题 —— 去 thread 计数后缀 " (N)"、去 "Nst/nd/rd/th round of "
 * 前缀、去 " position"（仅 position at 前）、去末尾句点、首个 " for " 转 " — "、
 * 首字母大写；不匹配时只做前几步回落，绝不编造。
 */
export function shortEmailTitle(subject?: string | null): string {
  let s = (subject ?? '').replace(/\s+/g, ' ').trim();
  s = s.replace(/\s*\(\d+\)\s*$/, '');
  s = s.replace(/^\d+(st|nd|rd|th)\s+round\s+of\s+/i, '');
  s = s.replace(/\s+position(?=\s+at\s)/i, '');
  s = s.replace(/[.。]\s*$/, '');
  const m = s.match(/^(.*?)\s+for\s+(.+)$/i);
  if (m) s = `${m[1]} — ${m[2]}`;
  s = s.trim();
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : s;
}
