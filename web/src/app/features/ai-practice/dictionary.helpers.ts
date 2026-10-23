/**
 * 票119:篇章点词词典的**纯决策函数** —— 篇章切分(保标点/空白)、义项折叠视图、
 * roving tabindex 导航、口音可用性与卡状态模型。
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
 * 词形正则:字母/数字串,允许内部 ' ’ -(两侧必须有字母数字)。
 * 连字符词(year-old)整体一词 —— 后端有连字符回退,前端不拆。
 */
const WORD_RE = /[A-Za-z0-9]+(?:['’-][A-Za-z0-9]+)*/g;

/** 按 token 切分篇章:word 与 sep 交替,拼接全部 token.text === 原文。 */
export function tokenizePassage(text: string): PassageToken[] {
  const tokens: PassageToken[] = [];
  if (!text) return tokens;
  let last = 0;
  let wordIndex = 0;
  for (const m of text.matchAll(WORD_RE)) {
    const start = m.index ?? 0;
    if (start > last) tokens.push({ kind: 'sep', text: text.slice(last, start), wordIndex: -1 });
    tokens.push({ kind: 'word', text: m[0], wordIndex: wordIndex++ });
    last = start + m[0].length;
  }
  if (last < text.length) tokens.push({ kind: 'sep', text: text.slice(last), wordIndex: -1 });
  return tokens;
}

/** 查词键:词面小写(后端再作完整归一化;前端只负责大小写)。 */
export function lookupKeyOf(wordText: string): string {
  return wordText.toLowerCase();
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

/** 与后端 schema v2 对齐的音频选项(单个口音)。 */
export interface DictAudioOption {
  kind: 'oxford' | 'commons' | 'neural' | 'device';
  key?: string | null;
  available: boolean;
}

export type DictAccent = 'us' | 'uk';

/**
 * 口音钮渲染判定(SPEC §4.3/§6.2):available 的口音才渲染钮;
 * 两口音都不可用 → 显示通用设备发音钮(Tier 3,走 pronounce/speechSynthesis)。
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
  forms: { label: string; text: string }[];
  audio: Record<DictAccent, DictAudioOption>;
  attribution: { source: string; audioSource?: string | null };
}

/**
 * 负结果简卡模型(SPEC §6.2):词头 + 发音钮 + 「暂无词条释义」一行。
 * 全卡禁「—」占位符 —— 本模型不产出任何占位文案,Jest 钉住。
 */
export function negativeCardLines(query: string, noEntryText: string): string[] {
  return [query, noEntryText];
}
