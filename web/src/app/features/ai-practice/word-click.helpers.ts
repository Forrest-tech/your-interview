/**
 * 票120:Recognition 逐词区的**纯决策函数** —— 点击分发、可回放填充词判定、
 * 焦点索引钳制。原先分发条件写死在模板表达式里,无从单测;抽成纯函数后模板
 * 只作调用,行为逐字不变(票122 的分发契约 spec 同步改钉新表达式)。
 *
 * 接缝写死(S120-R2-3):helpers 一律**不直达填充词表** —— FILLERS 的唯一
 * 事实源仍是 practice-grading-session 的 `private static FILLERS`;填充词
 * 知识只经参数进入(已求值的布尔 / 注入谓词),本文件不留任何词表副本。
 */

/** 逐词的最小形状(与落库 DTO 同口径:offsetMs 缺失或 0 = 无偏移,票122 约定)。 */
export interface ClickableWord {
  word: string;
  offsetMs?: number | null;
}

export type WordClickAction = 'replay' | 'pick';

/**
 * 点击/回车一个词时的分发:填充词且带 offsetMs(>0) → 回放该时刻(replay);
 * 其余一律打开 word-pop(pick)。offsetMs=0 与缺失同义(无偏移,绝不假 seek)。
 */
export function wordClickAction(
  isFillerFlag: boolean,
  offsetMs: number | null | undefined,
): WordClickAction {
  return isFillerFlag && offsetMs != null && offsetMs > 0 ? 'replay' : 'pick';
}

/**
 * 词条中是否存在「可回放的填充词」(带 offsetMs 的填充词)。
 * 填充词判定由调用方以谓词注入(组件传其既有 isFiller,转调 session)。
 * 驱动 filler-bar 回放提示语的显隐:界面不许承诺一个不会发生的动作。
 */
export function hasSeekableFiller(
  words: readonly ClickableWord[],
  isFiller: (word: string) => boolean,
): boolean {
  return words.some((w) => isFiller(w.word) && w.offsetMs != null && w.offsetMs > 0);
}

/**
 * roving tabindex 的焦点索引钳制:落到 [0, len-1];len=0 → -1(无词可聚焦,
 * 停靠点撤下)。线性文本不回绕 —— 首词再往左、末词再往右都停在原地。
 */
export function clampFocusIndex(index: number, len: number): number {
  if (len <= 0) return -1;
  return Math.min(Math.max(index, 0), len - 1);
}
