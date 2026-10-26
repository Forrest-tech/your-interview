/**
 * 每周投递目标的纯规则(票45 §7:从 tracker.component 内联逻辑逐字抽出,
 * 口径与原生 prompt 时代完全一致,仅换输入载体不换校验)。
 *
 * 消费方:TrackerComponent.promptGoal / setWeeklyGoal。
 * 注意:localStorage 已存值的读取口径(readGoal:有限、>0 且 ≤100 才认,否则 5)
 * 与输入口径不同(输入允许 >100,由 clamp 钳到 100),两者不合并,readGoal 留在组件内。
 */

/**
 * 解析用户输入的原始字符串 → 可用的目标值;不可用返回 null(调用方静默不改)。
 * 口径逐字承继原 promptGoal:parseInt 截断("12abc"→12、" 7 "→7、"3.9"→3);
 * 空串/纯空白/非数字 → NaN → null;≤0(含负数) → null;>100 不在此拒绝,由 clamp 处理。
 */
export function parseWeeklyGoalInput(raw: string | null | undefined): number | null {
  if (raw === null || raw === undefined) return null;
  const n = parseInt(raw, 10);
  return Number.isFinite(n) && n > 0 ? n : null;
}

/**
 * 目标值落库/落信号前的钳制,逐字承继原 setWeeklyGoal 首行:
 * round 后钳到 [1, 100];NaN/0 经 `|| 5` 回落默认 5。
 */
export function clampWeeklyGoal(n: number): number {
  return Math.max(1, Math.min(100, Math.round(n) || 5));
}
