/**
 * 票 147：详情抽屉宽度工具（openDetail 初始宽 + 对话框拖拽共用同一口径）。
 *
 * 规则（定稿 U-147-2/3，用户 2026-10-25 已确认）：
 * - 拖动范围 [900px, 1600px]，且受视口约束：上界 min(1600px, 92vw)，
 *   下界 min(900px, 92vw)——900px 下限保住 Overview 两列布局不塌。
 * - 宽度跨会话记忆：localStorage 'yi-detail-drawer-width'；缺失/非法值回落 66vw。
 *   （66vw 若落在范围外则钳制到范围边缘——否则初始宽会落在拖拽带之外，
 *   第一次拖动就跳变；66vw 在范围内时与字面 66vw 完全一致。）
 * - 视口 < 1024px 时抽屉取满宽 100vw，不受 900px 下限牵制（一并修掉
 *   旧 minWidth 900px 在小视口下的溢出）。
 */
export const DRAWER_WIDTH_KEY = 'yi-detail-drawer-width';
export const DRAWER_MIN = 900;
export const DRAWER_MAX = 1600;
export const DRAWER_NARROW_BELOW = 1024;
export const DRAWER_KEY_STEP = 40;

/** 当前视口下的拖动范围（上下界均受 92vw 约束）。 */
export function drawerWidthRange(viewportWidth: number): { min: number; max: number } {
  const cap = Math.floor(viewportWidth * 0.92);
  return { min: Math.min(DRAWER_MIN, cap), max: Math.min(DRAWER_MAX, cap) };
}

/** 读记忆宽度；非法（非数字、非有限、明显越界）返回 null，由调用方回落 66vw。 */
export function readStoredDrawerWidth(): number | null {
  try {
    const raw = localStorage.getItem(DRAWER_WIDTH_KEY);
    if (raw == null) return null;
    const v = Number(raw);
    if (!Number.isFinite(v) || v < 200 || v > 4000) return null;
    return Math.round(v);
  } catch {
    return null;
  }
}

export function saveDrawerWidth(px: number): void {
  try {
    localStorage.setItem(DRAWER_WIDTH_KEY, String(Math.round(px)));
  } catch {
    /* 私密模式等写失败不阻断拖动 */
  }
}

/** 打开抽屉时的初始宽度：窄屏 100vw；否则记忆宽（钳制到范围）或 66vw（钳制到范围）。 */
export function initialDrawerWidth(viewportWidth: number): string {
  if (viewportWidth < DRAWER_NARROW_BELOW) return '100vw';
  const { min, max } = drawerWidthRange(viewportWidth);
  const stored = readStoredDrawerWidth();
  if (stored != null) return Math.min(Math.max(stored, min), max) + 'px';
  return Math.min(Math.max(Math.round(viewportWidth * 0.66), min), max) + 'px';
}
