/**
 * ★ 票124:AI Practice 素材回收站 —— 前端纯逻辑层。
 *
 * 为什么要单独抽一个 helpers 文件:
 *   仓内 Jest 基建(web/jest.config.cjs)是最轻路线 —— ts-jest 直跑 TS、
 *   不引 TestBed、测试环境是 node。被测对象必须是**纯函数**才能被钉住。
 *   于是把"回收站清单项 → 界面该显示什么"这套判定抽成纯函数,
 *   组件模板只负责渲染,spec 只钉纯函数 —— 两者都不是二选一。
 *
 * 与规格的对应:
 *   §8.2 列表项字段与显隐条件 → trashItemView() / trashItemMeta()
 *   §8.2 空态 vs 加载失败态   → TrashLoadState 三态判定
 *   §8.1 计数徽标             → trashBadgeLabel()
 *   §8.2 全部恢复按钮显隐     → shouldShowRestoreAll()
 */
import { DeletedMaterialDto } from '../../core/api/practice-api.service';

/** ★ 票124:回收站对话框的加载三态。
 *
 * 纪律(事故教训,规格 §8.2):**加载失败绝不渲染成空态** ——
 * 空态文案只许出现在"请求成功且确实为空"时。
 * 加载中与失败各有各的文案,不许混为一谈。 */
export type TrashLoadState = 'loading' | 'loaded' | 'failed';

/** 删除时间的本地格式(规格 §8.2 逐字:`yyyy-MM-dd HH:mm`)。 */
export const TRASH_DATE_FORMAT = 'yyyy-MM-dd HH:mm';

/**
 * 列表项的字段视图(模板直接用,不再各自判断)。
 */
export interface TrashItemView {
  /** 类型图标:文件夹 folder / 文件 description(与树内图标语义一致)。 */
  icon: string;
  /** 主行:名称(溢出省略由 CSS 负责)。 */
  name: string;
  /** 元信息行:祖先路径(有祖先才显示;不含自身名)。 */
  path: string | null;
  /** 元信息行:类别名(null = 未分类,由调用方回落到既有键文案)。 */
  categoryName: string | null;
  /** 元信息行:删除时间(本地格式字符串)。 */
  deletedAt: string;
  /** 子树提示文案(仅 DescendantTotal > 0 时非 null)。 */
  containsItems: string | null;
  /** 录音提示文案(仅 RecordingCount > 0 时非 null)。 */
  containsRecordings: string | null;
}

/**
 * 清单项 → 字段视图。
 *
 * @param item      清单项(后端 DeletedMaterialDto)
 * @param fmtTime   时间格式化函数(测试注入;生产传 DatePipe/自写格式化器)
 * @param tf        插值函数(把 {count} 换成数字;生产传 i18n.tf)
 */
export function trashItemView(
  item: DeletedMaterialDto,
  fmtTime: (iso: string) => string,
  tf: (key: string, params: Record<string, string | number>) => string
): TrashItemView {
  return {
    icon: item.folder ? 'folder' : 'description',
    name: item.name,
    path: item.path && item.path.trim() ? item.path : null,
    categoryName: item.categoryName && item.categoryName.trim() ? item.categoryName : null,
    deletedAt: fmtTime(item.deletedAt),
    // 子树提示:仅当全部后代数 > 0 才出现(规格 §8.2 显隐条件)。
    containsItems: item.descendantTotal > 0
      ? tf('practice.trash.containsItems', { count: item.descendantTotal })
      : null,
    // 录音提示:仅当未删录音数 > 0 才出现(规格 §8.2 显隐条件)。
    containsRecordings: item.recordingCount > 0
      ? tf('practice.trash.containsRecordings', { count: item.recordingCount })
      : null
  };
}

/**
 * 计数徽标文案(规格 §8.1:>0 显示;>99 显示 99+)。
 * 0 → null(不渲染徽标)。
 */
export function trashBadgeLabel(count: number): string | null {
  if (!Number.isFinite(count) || count <= 0) return null;
  return count > 99 ? '99+' : String(Math.floor(count));
}

/**
 * 「全部恢复」按钮是否显示(规格 §8.2:仅当顶层项 ≥2 时显示)。
 * 只有一项时单项恢复按钮就在那一行上,不必再放一个重复动作。
 */
export function shouldShowRestoreAll(count: number): boolean {
  return count >= 2;
}

/**
 * 空态是否该显示(规格 §8.2 / §8.4 R8)。
 * 只有"加载成功 + 确实为空"才显示空态;加载中、失败都不显示。
 */
export function shouldShowTrashEmpty(state: TrashLoadState, items: DeletedMaterialDto[]): boolean {
  return state === 'loaded' && items.length === 0;
}

/**
 * 加载失败态是否该显示(规格 §8.2:失败态 + 重试按钮)。
 */
export function shouldShowTrashError(state: TrashLoadState): boolean {
  return state === 'failed';
}
