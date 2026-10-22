/**
 * Tech Stack 本地数据的**纯决策函数**(COD-10c/票16)。
 *
 * 从 TechStackLocalService 抽出:localStorage 旧数据解析、是否需要导入、
 * 本地日期键生成/校验、热力图档位 —— 都是无副作用的判定/转换逻辑,单独
 * 成文件便于独立验证(口径同票14 tracker-preferences.helpers / 票15
 * playbook-local.helpers;前端 Jest 基建接入前以 node 冒烟校验代偿)。
 */

/** 后端 CustomCategoryDto(Knowledge 面)。 */
export interface CustomCategoryDto {
  name: string;
  createdAt: string;
}

/** 后端 ReviewActivityDto(Knowledge 面)。date = 用户本地日历日期 YYYY-MM-DD。 */
export interface ReviewActivityDto {
  date: string;
  count: number;
}

/** 复习活动的前端形状:本地日期串 → 当天次数(与旧 ts.reviewActivity 完全一致)。 */
export type ReviewActivityMap = Record<string, number>;

const DATE_KEY_RE = /^\d{4}-\d{2}-\d{2}$/;

/**
 * 本地日期键 —— 与旧写入端 recordReviewActivity 的算法逐字一致:
 * 用本地年月日拼键,不用 UTC(源码原注:21:00 EDT 用 UTC 会记到第二天)。
 * 热力图/连击的读端此前以「本地午夜 Date → toISOString」取键,在用户时区
 * 与本算法等价;统一到本算法后口径单一且不随时区实现漂移。
 */
export function localDateKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** 是否为真实日历日的 YYYY-MM-DD 键(2026-02-30 这类不存在日期 → false)。 */
export function isValidLocalDateKey(s: unknown): boolean {
  if (typeof s !== 'string' || !DATE_KEY_RE.test(s)) return false;
  const [y, m, day] = s.split('-').map(Number);
  if (m < 1 || m > 12 || day < 1 || day > 31) return false;
  const dt = new Date(y, m - 1, day);
  return dt.getFullYear() === y && dt.getMonth() === m - 1 && dt.getDate() === day;
}

/**
 * 解析 localStorage 里的自定义分类(ts.customCategories)。
 * 容错口径(同票14/15):raw 为 null、非法 JSON、非数组 → [];数组项只收
 * 字符串,Trim 后丢空,大小写不敏感去重保首现原文与顺序。
 */
export function parseCustomCategories(raw: string | null): string[] {
  if (raw === null) return [];
  let parsed: unknown;
  try { parsed = JSON.parse(raw); } catch { return []; }
  if (!Array.isArray(parsed)) return [];
  const seen = new Set<string>();
  const result: string[] = [];
  for (const item of parsed) {
    if (typeof item !== 'string') continue;
    const name = item.trim();
    if (!name) continue;
    const key = name.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(name);
  }
  return result;
}

/**
 * 解析 localStorage 里的复习活动(ts.reviewActivity)。
 * 容错口径:raw 为 null、非法 JSON、非对象 → {};键须是真实日历日
 * YYYY-MM-DD,值须是正的有限数(取整),其余条目丢弃 —— 脏条目不能让
 * 热力图崩,完好条目一条不丢。
 */
export function parseReviewActivity(raw: string | null): ReviewActivityMap {
  if (raw === null) return {};
  let parsed: unknown;
  try { parsed = JSON.parse(raw); } catch { return {}; }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return {};
  const result: ReviewActivityMap = {};
  for (const [k, v] of Object.entries(parsed as Record<string, unknown>)) {
    if (!isValidLocalDateKey(k)) continue;
    if (typeof v !== 'number' || !Number.isFinite(v) || v <= 0) continue;
    result[k] = Math.floor(v);
  }
  return result;
}

/** 是否需要发起一次性导入:未打标且本地确有旧数据(票14/15 同名口径)。 */
export function shouldImport(importedFlag: boolean, localCount: number): boolean {
  return !importedFlag && localCount > 0;
}

/** 复习映射 → 导入条目(日期升序,形状对齐后端 ImportReviewActivityBody)。 */
export function toActivityEntries(map: ReviewActivityMap): ReviewActivityDto[] {
  return Object.keys(map).sort().map((date) => ({ date, count: map[date] }));
}

/** 后端复习列表 → 前端映射(逐字回填,不做任何日期换算)。 */
export function toActivityMap(rows: ReviewActivityDto[]): ReviewActivityMap {
  const result: ReviewActivityMap = {};
  for (const r of rows ?? []) {
    if (r && isValidLocalDateKey(r.date) && typeof r.count === 'number' && r.count > 0) {
      result[r.date] = r.count;
    }
  }
  return result;
}

/**
 * 热力图档位 —— 与改前 computed 的阈值逐字一致(呈现规则不变):
 * 0 次 → 0;1-2 → 1;3-5 → 2;6-9 → 3;≥10 → 4。未来日期的 -1 由组件处理。
 */
export function heatmapLevel(count: number): number {
  return count === 0 ? 0 : count < 3 ? 1 : count < 6 ? 2 : count < 10 ? 3 : 4;
}
