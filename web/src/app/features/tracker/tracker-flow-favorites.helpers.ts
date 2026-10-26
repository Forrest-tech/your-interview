/**
 * 票73:流程视图收藏的纯逻辑(节点徽标计数 / 条带行推导 / 阶段选中切换)。
 *
 * 单一路径不变项:收藏真源只有 TrackerPreferencesService.favorites 一份信号,
 * 本文件只做「给定投递全量 + 收藏集合 → 展示什么」的无副作用推导。
 * 任一视图切换收藏后,看板/列表/详情读 isFavorite、流程视图读这里的推导,
 * 同一信号即时同步,不存在第二份状态。
 */

/** 流程推导所需的投递最小形状(Application 的子集)。 */
export interface FlowAppLike {
  id: string;
  status: string;
}

/** 各状态下的已收藏数(节点右上角徽标):只数收藏集合与投递全量的交集。 */
export function favCountsByStatus<T extends FlowAppLike>(
  items: readonly T[], favorites: ReadonlySet<string>
): Map<string, number> {
  const map = new Map<string, number>();
  for (const a of items) {
    if (favorites.has(a.id)) map.set(a.status, (map.get(a.status) ?? 0) + 1);
  }
  return map;
}

/** 条带默认态「收藏的投递」:全部已收藏投递,保持投递全量原有顺序。 */
export function favoriteItems<T extends FlowAppLike>(
  items: readonly T[], favorites: ReadonlySet<string>
): T[] {
  return items.filter(a => favorites.has(a.id));
}

/** 条带阶段态:该阶段的全部投递(收藏与否都列,爱心可切换)。 */
export function itemsAtStatus<T extends FlowAppLike>(
  items: readonly T[], status: string
): T[] {
  return items.filter(a => a.status === status);
}

/**
 * 点阶段节点的选中切换:未选 → 选中该阶段;已选同一节点 → 取消(条带回
 * 默认「收藏的投递」);已选另一节点 → 切到新阶段。
 */
export function nextFlowStageSelection(current: string | null, clicked: string): string | null {
  return current === clicked ? null : clicked;
}
