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
 * 本文件无副作用、不依赖 Angular,供票 64(ARC-08c Jest)接 spec 覆盖。
 */

/** 是否有任一维存在 scored 数据。空/null/undefined 雷达一律 false。 */
export function hasScoredRadarData(
  radar: readonly Pick<RadarPoint, 'current' | 'best'>[] | null | undefined
): boolean {
  if (!radar || radar.length === 0) return false;
  return radar.some((p) => (p.current ?? 0) !== 0 || p.best != null);
}

/**
 * 最弱 count 项:无 scored 数据时返回空数组(调用方据此走既有空态);
 * 有数据时与旧行为完全一致——按 current 升序取前 count 项。
 * 拷贝后排序,不改入参顺序。
 */
export function weakestRadarPoints<T extends Pick<RadarPoint, 'current' | 'best'>>(
  radar: readonly T[] | null | undefined,
  count = 3
): T[] {
  if (!hasScoredRadarData(radar)) return [];
  return [...(radar ?? [])].sort((a, b) => a.current - b.current).slice(0, count);
}
