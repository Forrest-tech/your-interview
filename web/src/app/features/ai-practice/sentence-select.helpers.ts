import { nextRovingIndex, tokenizePassage, type PassageToken } from './dictionary.helpers';

/**
 * 票 269：一句话模式选区纯函数（仿 dictionary.helpers.ts 例）。
 *
 * 口径全部复用既有 tokenizer（票 126 R-T2…R-T5）：
 * 词序 = wordIndex 序列（分隔符/纯数字不在词序内），故区间词数 = end - start + 1；
 * 还原文本按 token 下标区间切片 join，分隔符与标点取原文 token 序列，保证与原文断句一致。
 */

/** 一句话模式的选区下限（词数）。用户原话只设下限，不设上限（计划 §8 D-5）。 */
export const SENTENCE_MIN_WORDS = 10;

/** badge tooltip 截断长度（过长截断 + 全文放 title，计划 §3 步骤 7）。 */
export const SENTENCE_BADGE_TIP_CHARS = 60;

/** 选区：词序闭区间，起止均为 wordIndex。 */
export interface SentenceRange {
  start: number;
  end: number;
}

/**
 * 键盘选区扩展（Shift+方向键，计划 §3 步骤 1 第 5 条）：
 * 锚点保持不变，焦点端按 nextRovingIndex 步进（含 Home/End 与边界钳制），
 * 区间 = min(锚点, 焦点端)..max(锚点, 焦点端)。方向键折叠（不带 Shift）不在此函数，
 * 由调用方直接清选区（标准“方向键折叠选区”语义）。
 */
export function extendSelection(
  anchor: number, focus: number, key: string, wordCount: number,
): { anchor: number; focus: number; start: number; end: number } {
  if (wordCount <= 0) return { anchor, focus, start: anchor, end: anchor };
  const next = nextRovingIndex(focus, key, wordCount);
  return { anchor, focus: next, start: Math.min(anchor, next), end: Math.max(anchor, next) };
}

/**
 * 区间 → 还原文本：按 token 下标区间切原文 token 数组再 join（勿用字符串 split 重拼）。
 * 区间端点解析不到（脏区间）时返回空串，不抛错。
 */
export function restoreSentenceText(tokens: PassageToken[], range: SentenceRange): string {
  const startIdx = tokens.findIndex((t) => t.kind === 'word' && t.wordIndex === range.start);
  const endIdx = tokens.findIndex((t) => t.kind === 'word' && t.wordIndex === range.end);
  if (startIdx < 0 || endIdx < 0 || endIdx < startIdx) return '';
  return tokens.slice(startIdx, endIdx + 1).map((t) => t.text).join('');
}

/** 文本 → 词数（既有 tokenizer 口径：纯数字不计、数字-连字符-字母复合取字母段）。 */
export function sentenceWordCountOf(text: string): number {
  return tokenizePassage(text).filter((t) => t.kind === 'word').length;
}

/** badge tooltip 截断：过长截断 + 省略号，全文由模板 title 属性承载。 */
export function truncateSentenceTip(text: string, maxChars = SENTENCE_BADGE_TIP_CHARS): string {
  return text.length > maxChars ? text.slice(0, maxChars) + '…' : text;
}

/**
 * 选区端点 → wordIndex（拖选吸附：半词选区向整词吸附的实质——端点落在词 span 内即取整词）。
 * 节点可能是文本节点（取 parentElement）或元素节点（自身 closest）；
 * 结构型入参（非 DOM 类型依赖），Node 环境单测可用替身对象覆盖。
 * 解析不到（端点在篇章外、无 data-wi、非整数）返回 null，调用方据此判定“不捕获”。
 */
export function wordIndexOfSelectionNode(node: unknown): number | null {
  if (!node || typeof node !== 'object') return null;
  const n = node as {
    nodeType?: number;
    parentElement?: unknown;
    closest?: (sel: string) => unknown;
  };
  // 文本节点（nodeType 3）取父元素；元素节点自身 closest；其余一律解析不到。
  const el = n.nodeType === 3
    ? n.parentElement as { closest?: (sel: string) => unknown } | null | undefined
    : (typeof n.closest === 'function' ? n as { closest: (sel: string) => unknown } : null);
  if (!el || typeof el.closest !== 'function') return null;
  const span = el.closest('.dict-word') as {
    getAttribute?: (name: string) => string | null;
  } | null;
  if (!span || typeof span.getAttribute !== 'function') return null;
  const raw = span.getAttribute('data-wi');
  if (raw === null || raw === undefined) return null;
  const wi = Number(raw);
  return Number.isInteger(wi) && wi >= 0 ? wi : null;
}
