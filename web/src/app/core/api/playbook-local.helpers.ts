/**
 * Playbook 本地数据的**纯决策函数**(COD-10b/票15)。
 *
 * 从 PlaybookLocalService 抽出:localStorage 旧数据解析、历史键收集、是否需要
 * 导入、后端 DTO ↔ 前端故事形状映射 —— 都是无副作用的判定/转换逻辑,单独
 * 成文件便于独立验证(口径同票14 tracker-preferences.helpers;前端 Jest 基建
 * 接入前以 node 冒烟校验代偿,见票15交付报告)。
 */

/** 前端故事形状(与 playbook.component 的 StarStory 结构一致)。 */
export interface PlaybookStarStory {
  id: string;
  title: string;
  situation: string;
  task: string;
  action: string;
  result: string;
  tags: string;
}

/** 后端 StarStoryDto(Interviews 面)。 */
export interface StarStoryDto {
  id: string;
  clientId: string;
  title: string;
  situation: string | null;
  task: string | null;
  action: string | null;
  result: string | null;
  tags: string | null;
  sortOrder: number;
}

export interface LegacyDebriefImportItem { entryId: string; payloadJson: string }
export interface LegacyPastedImportItem { entryId: string; content: string }

const GUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

function asString(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

/**
 * 解析 localStorage 里的 STAR 故事库(pb.starStories)。
 * 容错口径(同票14 parseStringArray):raw 为 null、非法 JSON、非数组 → [];
 * 数组项须是带非空字符串 id 的对象,其余字段收敛为字符串(缺失/非串 → '');
 * 同 id 重复只保留最后一版(与后端导入去重口径一致)。旧数据脏了也不能让
 * 页面崩,更不能丢掉其中完好的条目。
 */
export function parseStarStories(raw: string | null): PlaybookStarStory[] {
  if (raw === null) return [];
  let parsed: unknown;
  try { parsed = JSON.parse(raw); } catch { return []; }
  if (!Array.isArray(parsed)) return [];
  const byId = new Map<string, PlaybookStarStory>();
  for (const item of parsed) {
    if (typeof item !== 'object' || item === null) continue;
    const o = item as Record<string, unknown>;
    const id = asString(o['id']);
    if (!id) continue;
    byId.set(id, {
      id,
      title: asString(o['title']),
      situation: asString(o['situation']),
      task: asString(o['task']),
      action: asString(o['action']),
      result: asString(o['result']),
      tags: asString(o['tags']),
    });
  }
  return [...byId.values()];
}

/** 是否需要发起一次性导入:未打标且本地确有旧数据(票14 同名口径)。 */
export function shouldImport(importedFlag: boolean, localCount: number): boolean {
  return !importedFlag && localCount > 0;
}

/** ensure 类一次性加载的状态机(票14/15/16 三面同口径,票82 统一)。 */
export type EnsureState = 'idle' | 'loading' | 'loaded';

/**
 * 是否应发起(或重新发起)一次性加载(票82,口径同 techstack-local.helpers):
 * 未登录不触发;在飞不重复;idle 总是发起;loaded 只对加载时那个用户
 * 有效,换账号必须重载。失败回 idle 由服务层保证(不闩锁)。
 */
export function shouldEnsureLoad(
  state: EnsureState, loadedUserId: string | null, currentUserId: string | null
): boolean {
  if (!currentUserId) return false;
  if (state === 'loading') return false;
  if (state === 'idle') return true;
  return loadedUserId !== currentUserId;
}

/** 是否应尝试一次性导入:须已登录、未打标、本地确有旧数据(票82)。 */
export function shouldAttemptImport(
  authenticated: boolean, importedFlag: boolean, localCount: number
): boolean {
  return authenticated && shouldImport(importedFlag, localCount);
}

/**
 * 从 localStorage 键值对快照里收集历史复盘(pb.debrief.<条目Guid>)。
 * payloadJson = 本地原值逐字(不解析不改写 —— 无损归档);键后缀不是 Guid、
 * 值为空的跳过(那不是旧详情页写出的数据形态)。
 */
export function collectLegacyDebriefs(
  pairs: readonly { key: string; value: string | null }[]
): LegacyDebriefImportItem[] {
  const out: LegacyDebriefImportItem[] = [];
  for (const { key, value } of pairs) {
    if (!key.startsWith('pb.debrief.')) continue;
    const entryId = key.slice('pb.debrief.'.length);
    if (!GUID_RE.test(entryId)) continue;
    if (value === null || value === '') continue;
    out.push({ entryId, payloadJson: value });
  }
  return out;
}

/**
 * 收集历史粘贴材料(pb-pasted-<条目Guid>):content = 本地原值逐字;
 * 口径同 collectLegacyDebriefs。
 */
export function collectLegacyPasted(
  pairs: readonly { key: string; value: string | null }[]
): LegacyPastedImportItem[] {
  const out: LegacyPastedImportItem[] = [];
  for (const { key, value } of pairs) {
    if (!key.startsWith('pb-pasted-')) continue;
    const entryId = key.slice('pb-pasted-'.length);
    if (!GUID_RE.test(entryId)) continue;
    if (value === null || value === '') continue;
    out.push({ entryId, content: value });
  }
  return out;
}

/** 后端 DTO → 前端故事:前端认 clientId 为 id;null 字段收敛为空串。 */
export function toLocalStory(dto: StarStoryDto): PlaybookStarStory {
  return {
    id: dto.clientId,
    title: dto.title ?? '',
    situation: dto.situation ?? '',
    task: dto.task ?? '',
    action: dto.action ?? '',
    result: dto.result ?? '',
    tags: dto.tags ?? '',
  };
}

/** 前端故事 → 保存/导入请求体。 */
export function toStoryInput(s: PlaybookStarStory): {
  clientId: string; title: string; situation: string; task: string;
  action: string; result: string; tags: string;
} {
  return {
    clientId: s.id, title: s.title, situation: s.situation, task: s.task,
    action: s.action, result: s.result, tags: s.tags,
  };
}
