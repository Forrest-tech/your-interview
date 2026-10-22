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

/** ensure 类一次性加载的状态机(票14/15/16 三面同口径,票82 统一)。 */
export type EnsureState = 'idle' | 'loading' | 'loaded';

/**
 * 是否应发起(或重新发起)一次性加载(票82):
 *   - 未登录(currentUserId 为空)永不触发 —— 无令牌窗口不烧闩锁,
 *     登录后由页面 ngOnInit 再调一次时自然补上;
 *   - 在飞(loading)不重复发起;
 *   - idle 总是发起;
 *   - loaded 只对「加载时那个用户」有效 —— 换账号(登出后另一用户在
 *     同一 SPA 会话内登录,单例服务不重建)必须重载,否则新用户看不到
 *     自己的数据、旧数据也不会导入(票82 场景 C 实测)。
 */
export function shouldEnsureLoad(
  state: EnsureState, loadedUserId: string | null, currentUserId: string | null
): boolean {
  if (!currentUserId) return false;
  if (state === 'loading') return false;
  if (state === 'idle') return true;
  return loadedUserId !== currentUserId;
}

/**
 * 是否应尝试一次性导入(票82 边界矩阵:登录态 × 导入标记 × 本地有无):
 * 须已登录、未打标、本地确有旧数据,三者缺一不发。
 * 导入失败不打标,由 shouldEnsureLoad 的「失败回 idle」保证下次重试,
 * 旧键全程保留不删(落服务端后也只打标,不删键)。
 */
export function shouldAttemptImport(
  authenticated: boolean, importedFlag: boolean, localCount: number
): boolean {
  return authenticated && shouldImport(importedFlag, localCount);
}

/**
 * 分类名上限 —— 与后端单一口径对齐(票83):TechStackLocalDataRules
 * .MaxCategoryNameLength = 100,EF 列宽 TechStackCategory.Name /
 * KnowledgeItem.Topic 均 HasMaxLength(100),Add/Import 校验器均
 * MaximumLength(100)。前端提交前先挡一道,不再把超长名乐观显示成
 * 「已保存」后被后端 400 静默吞掉(D4b)。
 */
export const MAX_CATEGORY_NAME_LENGTH = 100;

/** 分类名校验结果:length 为 Trim 后的计数,max 为上限。 */
export interface CategoryNameValidation {
  valid: boolean;
  reason: 'empty' | 'tooLong' | null;
  length: number;
  max: number;
}

/**
 * 分类名纯校验(票83 §7,node 冒烟覆盖 boundary):
 *   - 计数口径 = **UTF-16 code unit**(JS String.length)—— 与后端 .NET
 *     String.Length(FluentValidation MaximumLength 的计数)逐字一致;
 *     astral 字符(如 emoji 🎯)前后端均计 2,判定永不漂移;
 *   - 校验对象 = Trim 后的名字(与后端 NormalizeCategoryNames /
 *     RenameTopic handler 的 Trim 口径一致,前后空白不占额度);
 *   - null/undefined/纯空白 → 'empty';超上限 → 'tooLong'。
 */
export function validateCategoryName(raw: string | null | undefined): CategoryNameValidation {
  const name = (raw ?? '').trim();
  const length = name.length;
  if (length === 0) return { valid: false, reason: 'empty', length: 0, max: MAX_CATEGORY_NAME_LENGTH };
  if (length > MAX_CATEGORY_NAME_LENGTH) {
    return { valid: false, reason: 'tooLong', length, max: MAX_CATEGORY_NAME_LENGTH };
  }
  return { valid: true, reason: null, length, max: MAX_CATEGORY_NAME_LENGTH };
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
