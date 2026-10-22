/**
 * Tracker 收藏/自备问题的**纯决策函数**(COD-10a/票14)。
 *
 * 从 TrackerPreferencesService 抽出:localStorage 旧数据解析、是否需要导入、
 * 导入哪几条、后端不可达时的展示合并 —— 这些都是无副作用的判定逻辑,
 * 单独成文件便于单测(前端 Jest 基建由 ARC-08 批次接入后,这组函数是首批 spec
 * 目标;接入前以本文件的纯函数形态保证逻辑可独立验证,服务层只做 IO)。
 */

/**
 * 解析 localStorage 里的字符串数组(收藏 id / 自备问题)。
 * 容错口径:raw 为 null、非法 JSON、非数组、数组含非字符串项 → 一律收敛为
 * 「只含字符串项的数组」,不抛错 —— 旧数据脏了也不能让页面崩,更不能丢掉
 * 其中完好的条目。
 */
export function parseStringArray(raw: string | null): string[] {
  if (raw === null) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((x): x is string => typeof x === 'string');
  } catch {
    return [];
  }
}

/** 是否需要发起一次性导入:未打标且本地确有旧数据。 */
export function shouldImport(importedFlag: boolean, localCount: number): boolean {
  return !importedFlag && localCount > 0;
}

/**
 * 本地收藏中「后端还没有」的部分(导入候选)。保持本地顺序,输入内重复只算一次。
 * 后端导入本身也幂等,这是前端侧的第一道去重(少发无用 id)。
 */
export function missingFavorites(serverIds: readonly string[], localIds: readonly string[]): string[] {
  const server = new Set(serverIds);
  const seen = new Set<string>();
  const out: string[] = [];
  for (const id of localIds) {
    if (server.has(id) || seen.has(id)) continue;
    seen.add(id);
    out.push(id);
  }
  return out;
}

/** 后端 ∪ 本地(导入失败时的展示兜底合并):后端顺序在前,本地独有项续后,去重。 */
export function mergeFavoriteIds(serverIds: readonly string[], localIds: readonly string[]): string[] {
  return [...new Set([...serverIds, ...localIds])];
}
