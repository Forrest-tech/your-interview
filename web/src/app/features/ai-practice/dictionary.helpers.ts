import { HttpErrorResponse } from '@angular/common/http';

/**
 * 票119:篇章点词词典的**纯决策函数** —— 篇章切分(保标点/空白)、义项折叠视图、
 * roving tabindex 导航、口音可用性与卡状态模型。
 * 票126 增补:分词规则 R-T1…R-T5、错误状态码判定(§4.2)、口音记忆(§8.5)、归元角色(§9.1)。
 *
 * 与 word-click.helpers(票120)同例:决策逻辑抽成纯函数进 Jest,组件只作调用与
 * DOM 落地。切分教训(票120 模板空白剥离吞词间空格):分隔符必须是显式 token,
 * 由模板逐个渲染为文本节点,绝不靠模板空白折叠凑数。
 */

/** 篇章 token:word = 可点词(下标为词序);sep = 标点/空白原样保留。 */
export interface PassageToken {
  kind: 'word' | 'sep';
  text: string;
  /** 词序(仅 word 有,从 0 起);sep 恒为 -1。 */
  wordIndex: number;
}

/**
 * 候选串正则:字母/数字串,允许内部 ' ’ -(两侧必须有字母数字)。
 * 候选串只是切分原料,是否成词跨度由 R-T2…R-T5 判定(票126 §5)。
 */
const WORD_RE = /[A-Za-z0-9]+(?:['’-][A-Za-z0-9]+)*/g;

/**
 * 候选串 → 可点词跨度(票126 §5.1,返回 [起点偏移, 跨度文本],至多一段):
 * - R-T2 字母复合(可含内部撇号/连字符,无数字)→ 整体成跨度:don't / well-known / year-old。
 * - R-T3 纯数字 → 不成跨度(普通文本):80 / 3.5 的每段 / 1,000 的每段。
 * - R-T4 数字-连字符-字母复合 → 在「数字段 + 其后连字符」之后切开,字母段成跨度:
 *   14-year-old → year-old;5-year → year。前提:余段全部由字母起头段组成。
 * - R-T5 其余含数字混合串(a1 / 20th / B2)→ 整体不成跨度,宁缺勿假。
 */
export function wordSpanOf(candidate: string): [number, string] | null {
  if (!/\d/.test(candidate)) return [0, candidate]; // R-T2
  if (/^\d+$/.test(candidate)) return null; // R-T3
  const m = /^(\d+)-(.+)$/.exec(candidate); // R-T4:数字段 + 连字符 + 余段
  if (m) {
    const rest = m[2];
    const segments = rest.split(/['’-]/);
    if (/^[A-Za-z]/.test(rest) && segments.every((s) => /^[A-Za-z]/.test(s))) {
      return [candidate.length - rest.length, rest];
    }
  }
  return null; // R-T5
}

/** 按 token 切分篇章:word 与 sep 交替,拼接全部 token.text === 原文。 */
export function tokenizePassage(text: string): PassageToken[] {
  const tokens: PassageToken[] = [];
  if (!text) return tokens;
  let last = 0;
  let wordIndex = 0;
  const pushSep = (from: number, to: number): void => {
    if (to > from) tokens.push({ kind: 'sep', text: text.slice(from, to), wordIndex: -1 });
  };
  for (const m of text.matchAll(WORD_RE)) {
    const start = m.index ?? 0;
    const span = wordSpanOf(m[0]);
    if (span === null) continue; // 不成跨度:整段候选并入 sep(由相邻 pushSep 合并覆盖)
    const [offset, spanText] = span;
    pushSep(last, start + offset);
    tokens.push({ kind: 'word', text: spanText, wordIndex: wordIndex++ });
    last = start + offset + spanText.length;
  }
  pushSep(last, text.length);
  return tokens;
}

/** 查词键:词面小写(后端再作完整归一化;前端只负责大小写)。 */
export function lookupKeyOf(wordText: string): string {
  return wordText.toLowerCase();
}

/**
 * 错误状态码判定(票126 §4.2,F-1 收口):同时认两路错误形态——
 * 原生 HttpErrorResponse 的 .status,与经 ApiClient rethrow 转换后的普通 Error
 * (状态码挂在 .status 上、原文在 .original)。无状态码(网络断等)返回 null。
 * F-1 的根因正是只认 instanceof HttpErrorResponse:经 ApiClient 后恒假、404 恒落 error 态。
 */
export function errorStatusOf(err: unknown): number | null {
  // typeof 守卫:仓内 Jest 基建把 @angular/* 映射为替身(该导出在替身里不存在),
  // 无守卫时 instanceof 会对 undefined 抛错;生产环境恒为真实类,行为不变。
  if (typeof HttpErrorResponse === 'function' && err instanceof HttpErrorResponse) return err.status;
  const status = (err as { status?: unknown } | null | undefined)?.status;
  return typeof status === 'number' ? status : null;
}

/** 口音记忆键(票126 §8.5,键名钉死 dict.accent)。 */
export const DICT_ACCENT_STORAGE_KEY = 'dict.accent';

/** 读回记忆口音;无记忆/异常 → 默认 US(S-1 美音优先)。 */
export function loadDictAccent(): DictAccent {
  try {
    return localStorage.getItem(DICT_ACCENT_STORAGE_KEY) === 'uk' ? 'uk' : 'us';
  } catch {
    return 'us';
  }
}

export function saveDictAccent(accent: DictAccent): void {
  try {
    localStorage.setItem(DICT_ACCENT_STORAGE_KEY, accent);
  } catch {
    /* 存储不可用时仅影响记忆,不影响当次使用 */
  }
}

/**
 * 归元标注的词形角色(票126 §9.1):取 forms 中 text == 所查词形的条目的 label(可多个)。
 * 标注由数据生成,不硬编码角色名。
 */
export function lemmaRoleLabels(
  forms: { label: string; text: string }[],
  queriedForm: string,
): string[] {
  const q = queriedForm.toLowerCase();
  return forms.filter((f) => f.text.toLowerCase() === q).map((f) => f.label);
}

/** 义项折叠视图(SPEC §6.2:每组默认展开前 2 条,余者折叠)。 */
export interface FoldView {
  /** 本组应渲染的义项数。 */
  visible: number;
  /** 折叠条数(>0 才渲染「还有 N 条义项」)。 */
  folded: number;
}

export function foldView(totalSenses: number, expanded: boolean, pageSize = 2): FoldView {
  const total = Math.max(0, totalSenses);
  if (expanded) return { visible: total, folded: 0 };
  return { visible: Math.min(pageSize, total), folded: Math.max(0, total - pageSize) };
}

/**
 * roving tabindex 的方向键导航(SPEC §6.3):左右/上下在相邻词间移动(线性文本,
 * 不回绕,两端停住 —— 与 word-click.helpers 的 clampFocusIndex 同口径),
 * Home/End 到首/尾词。非导航键返回 current(调用方不拦截)。
 */
export function nextRovingIndex(current: number, key: string, wordCount: number): number {
  if (wordCount <= 0) return -1;
  const cur = Math.min(Math.max(current, 0), wordCount - 1);
  switch (key) {
    case 'ArrowRight':
    case 'ArrowDown':
      return Math.min(cur + 1, wordCount - 1);
    case 'ArrowLeft':
    case 'ArrowUp':
      return Math.max(cur - 1, 0);
    case 'Home':
      return 0;
    case 'End':
      return wordCount - 1;
    default:
      return cur;
  }
}

/** 是否为 roving 导航键(调用方据此决定 preventDefault)。 */
export function isRovingKey(key: string): boolean {
  return key === 'ArrowRight' || key === 'ArrowDown' || key === 'ArrowLeft'
    || key === 'ArrowUp' || key === 'Home' || key === 'End';
}

/** 与后端 schema 对齐的音频选项(单个口音;票126 §8.1:kind 只余真人录音两档,无录音时为 null)。 */
export interface DictAudioOption {
  kind?: 'oxford' | 'commons' | null;
  key?: string | null;
  available: boolean;
}

export type DictAccent = 'us' | 'uk';

/**
 * 口音播放钮渲染判定:available(有真人录音)的口音才渲染播放钮。
 * 两口音都不可用 → 不渲染任何播放钮,卡上只出「暂无发音」状态文案(票126 §8.3,S-2)。
 */
export function accentButtonVisible(
  audio: Partial<Record<DictAccent, DictAudioOption>> | null | undefined,
  accent: DictAccent,
): boolean {
  return audio?.[accent]?.available === true;
}

export function anyAccentAvailable(
  audio: Partial<Record<DictAccent, DictAudioOption>> | null | undefined,
): boolean {
  return accentButtonVisible(audio, 'us') || accentButtonVisible(audio, 'uk');
}

/**
 * 票131 §4 R-T1:口音页签放行判据（卡级析取）——词元该口音有真人录音，
 * 或任一词形该口音有真人录音。词形侧走 formPlayable 严格口径
 * （audio 缺失/null/口音值非布尔 → false）；只读已到达卡面的 DTO，零请求。
 * 与 accentButtonVisible（词元播放钮判据）隔离：页签放行 ≠ 词元钮出现。
 */
export function tabEnabled(
  card: Pick<DictCardData, 'audio' | 'forms'>,
  accent: DictAccent,
): boolean {
  return accentButtonVisible(card.audio, accent)
    || (card.forms ?? []).some((f) => formPlayable(f, accent));
}

/**
 * 票131 §4 R-T1 / 126 §8.3 卡级原文口径：整卡任一口音有声 ⟺
 * 任一口音页签放行。整卡「暂无发音」行出现 ⟺ 本函数为 false。
 */
export function cardHasAnyAudio(card: Pick<DictCardData, 'audio' | 'forms'>): boolean {
  return tabEnabled(card, 'us') || tabEnabled(card, 'uk');
}

/**
 * 词形播放钮渲染判据(票128 §6 R-F1/R-F6,纯函数):钮渲染 ⟺ form.audio 存在
 * 且当前口音值严格为布尔 true。audio 缺失(旧后端混搭)/null/口音值非布尔
 * 一律无钮——诚实默认「不确定就不给假钮」,逐词形隔离,不牵连其他词形与整卡。
 * 可用性只读 DTO,本函数与渲染路径都不发任何探测请求(R-F3)。
 */
export function formPlayable(
  form: { audio?: { us: boolean; uk: boolean } | null },
  accent: DictAccent,
): boolean {
  const audio = form?.audio;
  if (audio === null || typeof audio !== 'object') return false;
  return (audio as Record<string, unknown>)[accent] === true;
}

/** popup 与锚点间距(票126 §6.2:首选位/翻转位均为 8px)。 */
export const DICT_POPUP_GAP_PX = 8;

/** popup 高度上限(票126 §6.3:≤ 70vh,超出卡内纵向滚动;移动 sheet 的 60vh 由卡 CSS 媒体查询管,不走本 helper)。 */
export const DICT_POPUP_MAX_VH_RATIO = 0.7;

export interface DictPopupGeometryInput {
  /** 视口高(px)。 */
  viewportHeight: number;
  /** 锚点(触发词)矩形的视口顶/底(px)。 */
  anchorTop: number;
  anchorBottom: number;
  /** 卡自然高度(内容全高;宿主以 scrollHeight 量,未受任何 max-height 钳制的内容高)。 */
  naturalHeight: number;
  /**
   * 宿主已由 CDK 落位定侧时传入,helper 按该侧钳制/预测,不再自选侧;
   * 缺省时按 §6.2 自选侧:下方装得下 → 下方;上方装得下 → 上方;双侧皆不足 → 较大一侧。
   */
  side?: 'above' | 'below';
}

export interface DictPopupGeometry {
  side: 'above' | 'below';
  /** 所选侧可用空间:锚点到该侧视口缘的距离扣 8px 间距(§6.2)。 */
  sideSpace: number;
  /** 70vh 上限(px,向下取整,与卡 CSS 的 70vh 同值)。 */
  vhCap: number;
  /** 应施加的最大高度 = min(70vh, 所选侧可用空间);双侧不足时即 §6.2 末条的钳制值。 */
  maxHeight: number;
  /** maxHeight 是否低于 70vh 上限(发生了空间钳制,宿主据此决定是否落 inline max-height)。 */
  clamped: boolean;
  /** 以 min(自然高, maxHeight) 渲染的实际高度。 */
  renderedHeight: number;
  /** 渲染后卡的视口顶/底(钳制正确时恒 cardTop ≥ 0 且 cardBottom ≤ viewportHeight)。 */
  cardTop: number;
  cardBottom: number;
}

/**
 * popup 高度钳制几何(票126 §6.2 末条 + 复测缺陷 F-126-R1):
 * 上下皆不足以容纳整卡时,取可用空间较大一侧、卡高收至该侧可用空间、卡内纵向滚动。
 * 缺陷原形(复测 r08):视口 560、词顶 308/词底 ≈318、卡高 392(=70vh)——
 * 只翻转不钳高时卡顶 = 308 − 8 − 392 = −92,词头/Close 溢出视口上方不可达;
 * 经本 helper 钳制:maxHeight = min(392, 300) = 300 → 卡顶 = 0、卡底 = 300,完整在视口内。
 * 纯函数:宿主把 CDK 落位后的实测矩形喂进来定侧与落值,Jest 直接钉几何(无 DOM)。
 */
export function dictPopupGeometry(input: DictPopupGeometryInput): DictPopupGeometry {
  const gap = DICT_POPUP_GAP_PX;
  const vhCap = Math.floor(input.viewportHeight * DICT_POPUP_MAX_VH_RATIO);
  const spaceBelow = input.viewportHeight - input.anchorBottom - gap;
  const spaceAbove = input.anchorTop - gap;
  const cappedNatural = Math.min(input.naturalHeight, vhCap);
  let side = input.side;
  if (!side) {
    if (cappedNatural <= spaceBelow) side = 'below';
    else if (cappedNatural <= spaceAbove) side = 'above';
    else side = spaceAbove >= spaceBelow ? 'above' : 'below';
  }
  const sideSpace = side === 'above' ? spaceAbove : spaceBelow;
  const maxHeight = Math.max(0, Math.min(vhCap, sideSpace));
  const renderedHeight = Math.max(0, Math.min(input.naturalHeight, maxHeight));
  const cardTop = side === 'below' ? input.anchorBottom + gap : input.anchorTop - gap - renderedHeight;
  const cardBottom = side === 'below' ? cardTop + renderedHeight : input.anchorTop - gap;
  return {
    side,
    sideSpace,
    vhCap,
    maxHeight,
    clamped: maxHeight < vhCap,
    renderedHeight,
    cardTop,
    cardBottom,
  };
}

/** 词典卡状态机(组件与卡共用):加载 / 命中 / 负结果 / 网络失败。 */
export type DictCardState =
  | { status: 'loading'; query: string }
  | { status: 'ok'; query: string; card: DictCardData }
  | { status: 'notfound'; query: string }
  | { status: 'error'; query: string };

/** 与后端 DictionaryLookupResponse(schema v2)对齐的卡面数据。 */
export interface DictCardData {
  query: string;
  lemma: string;
  matchedVia: string;
  tier: 'oxford' | 'open';
  phonetics: { uk?: string | null; us?: string | null };
  posBlocks: {
    pos: string;
    label: string;
    senses: { defEn: string; glossZh?: string | null; example?: { en: string; zh?: string | null } | null }[];
    foldedCount: number;
    zhLines: string[];
  }[];
  /** 票128 §5.3:audio 刻意可选——旧后端响应里不存在,渲染判据必须显式处理缺失(§6 R-F1)。 */
  forms: { label: string; text: string; audio?: { us: boolean; uk: boolean } }[];
  audio: Record<DictAccent, DictAudioOption>;
  attribution: { source: string; audioSource?: string | null };
}

/**
 * 负结果简卡模型(票126 §4.3,supersede 119 §6.2):词头 + 「暂无词条释义」一行,
 * 无发音钮、无发音区(S-2③:负卡不出现任何机器朗读呈现)。
 * 全卡禁「—」占位符 —— 本模型不产出任何占位文案,Jest 钉住。
 */
export function negativeCardLines(query: string, noEntryText: string): string[] {
  return [query, noEntryText];
}
