import { RadarPoint } from './api.models';

/**
 * REQ-08(票 27):雷达「有 scored 数据」判定与最弱项选取的纯函数。
 *
 * 后端语义(Services.Analytics AnalyticsHandlers):某维无快照时 current=0、
 * best=null;best 为 null 时序列化直接省略该字段,前端读到的是 undefined——
 * 故守卫以 `best != null` 同时覆盖 null 与 undefined。任一维被真实评分过
 * (哪怕评 0 分,best=0 非空)即算有数据;六维全部 current=0 且 best 为空
 * 才算无数据,此时不列弱项、不渲染 Best,由调用方走既有空态。
 *
 * 票 88(REQ-08 边角收尾):账户级守卫之外再按维度过滤——部分维度有分时,
 * 未评分维度(current=0 且 best 空)不进最弱清单。它排进 Top 3 只会渲染
 * 空白 Best,且把「未评分」误报成「最该改进」,与票 27 消除的账户级假信号
 * 同源。已评分维度不足 count 个时清单短于 count;为 0 时即上面的无数据
 * 口径,返回空数组走既有空态。全有分时过滤不剔除任何维度,排序/切片与
 * 票 27 改前逐字一致。
 *
 * 本文件无副作用、不依赖 Angular,供票 64(ARC-08c Jest)接 spec 覆盖。
 */

/** 单维是否被真实评分过:current 非 0,或 best 非空(评 0 分 best=0 也算)。 */
export function isScoredRadarPoint(
  p: Pick<RadarPoint, 'current' | 'best'>
): boolean {
  return (p.current ?? 0) !== 0 || p.best != null;
}

/** 是否有任一维存在 scored 数据。空/null/undefined 雷达一律 false。 */
export function hasScoredRadarData(
  radar: readonly Pick<RadarPoint, 'current' | 'best'>[] | null | undefined
): boolean {
  if (!radar || radar.length === 0) return false;
  return radar.some((p) => isScoredRadarPoint(p));
}

/**
 * 最弱 count 项:无 scored 数据时返回空数组(调用方据此走既有空态);
 * 有数据时先剔除未评分维度(票 88),再按 current 升序取前 count 项。
 * 全有分时与旧行为完全一致。拷贝后排序,不改入参顺序。
 */
export function weakestRadarPoints<T extends Pick<RadarPoint, 'current' | 'best'>>(
  radar: readonly T[] | null | undefined,
  count = 3
): T[] {
  if (!hasScoredRadarData(radar)) return [];
  return [...(radar ?? [])]
    .filter((p) => isScoredRadarPoint(p))
    .sort((a, b) => a.current - b.current)
    .slice(0, count);
}
