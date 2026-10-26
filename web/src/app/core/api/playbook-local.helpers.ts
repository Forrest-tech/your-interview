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
  /** ★ 票111 段3(I05):后端故事行读时 xmin,删除时 If-Match 回传;本地新建未落库为 undefined。 */
  version?: number;
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
  /** ★ 票111 段3(I05):故事行读时 xmin。 */
  version?: number;
}

export interface LegacyDebriefImportItem { entryId: string; payloadJson: string }
export interface LegacyPastedImportItem { entryId: string; content: string }

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

/**
 * 票78:STAR 保存是否可发起 —— 组件级在飞守卫(口径同票92
 * shouldStartCategorySubmit:纯判定,组件入口先查、起步同步置位、结算释放;
 * 模板 [disabled] 只是第二道,同帧双击靠方法级判定挡住)。
 */
export function shouldStartStorySave(saving: boolean): boolean {
  return !saving;
}

/** STAR 故事标题上限 —— 与后端 StarStoryLimits.TitleMax 对齐(500,UTF-16 码元计数,两端一致)。 */
export const STAR_STORY_TITLE_MAX = 500;

/** STAR 故事标题校验结果(票79):null = 通过。 */
export type StarStoryTitleError = 'required' | 'tooLong' | null;

/**
 * 票79:STAR 故事标题校验 —— 纯判定,供组件在保存入口调用并把结果映射为
 * 字段级错误文案(口径同票12 Knowledge 弹窗的字段级呈现)。
 * 空/null/纯空白 → 'required'(trim 判定,与后端 NotEmpty 拒空白一致);
 * 原始长度超过上限 → 'tooLong'(后端 MaximumLength 按原始串计,故此处不 trim 后计长);
 * required 优先于 tooLong(纯空白超长串先报必填)。
 */
export function starStoryTitleError(title: string | null | undefined): StarStoryTitleError {
  if (title === null || title === undefined || title.trim().length === 0) return 'required';
  if (title.length > STAR_STORY_TITLE_MAX) return 'tooLong';
  return null;
}

/**
 * ML-FIX-PREP(R-27):STAR 故事正文校验 —— 纯判定,供组件在保存入口调用。
 * 口径取自 Prep「行为题 STAR」behavioral.p2 既有要求,不新编标准:
 * ① S/T/A/R 四项均必填(此前仅标题必填,正文可空泛存入);
 * ② Result 须带数字(半角/全角数字均认);
 * ③ Action 空泛拦截:机械语义检测不可靠,此处以 trim 后长度作代理
 *    (STAR_ACTION_MIN_CHARS),错误文案仍引用 p2 原文口径,语义复核留给填写者自查。
 * 判定顺序:S→T→A→R 必填,随后 Result 数字、最后 Action 长度;首个失败即返回。
 */
export type StarStoryBodyError =
  | { field: 'S' | 'T' | 'A' | 'R'; kind: 'required' | 'resultNeedsNumber' | 'actionTooVague' }
  | null;

/** Action 长度代理阈值(见上:仅为“具体复数动作”的机械代理,非语义标准)。 */
export const STAR_ACTION_MIN_CHARS = 20;

export function starStoryBodyError(s: {
  situation: string | null | undefined;
  task: string | null | undefined;
  action: string | null | undefined;
  result: string | null | undefined;
}): StarStoryBodyError {
  const fields = [
    ['S', s.situation],
    ['T', s.task],
    ['A', s.action],
    ['R', s.result],
  ] as const;
  for (const [field, value] of fields) {
    if (value === null || value === undefined || value.trim().length === 0) {
      return { field, kind: 'required' };
    }
  }
  if (!/[0-9０-９]/.test(s.result as string)) return { field: 'R', kind: 'resultNeedsNumber' };
  if ((s.action as string).trim().length < STAR_ACTION_MIN_CHARS) {
    return { field: 'A', kind: 'actionTooVague' };
  }
  return null;
}

/** 票78:某 clientId 是否已有保存在飞 —— 服务级按故事守卫的纯判定。 */
export function shouldStartStorySaveFor(
  inFlightClientIds: ReadonlySet<string>, clientId: string
): boolean {
  return !inFlightClientIds.has(clientId);
}

/** 是否应尝试一次性导入:须已登录、未打标、本地确有旧数据(票82)。 */
export function shouldAttemptImport(
  authenticated: boolean, importedFlag: boolean, localCount: number
): boolean {
  return authenticated && shouldImport(importedFlag, localCount);
}

/**
 * 从 localStorage 键值对快照里收集历史复盘(pb.debrief.<后缀>)。
 * payloadJson = 本地原值逐字(不解析不改写 —— 无损归档)。
 * 票77:后缀不再限 Guid 形态 —— 匹配不上现存条目的孤儿键(条目已删等)
 * 正是最需要保全的一类,以后缀原文为归档键一并收集;仅跳过空后缀/空值
 * (那不是旧详情页写出的数据形态)。Guid 规范形归一在后端导入侧收口。
 */
export function collectLegacyDebriefs(
  pairs: readonly { key: string; value: string | null }[]
): LegacyDebriefImportItem[] {
  const out: LegacyDebriefImportItem[] = [];
  for (const { key, value } of pairs) {
    if (!key.startsWith('pb.debrief.')) continue;
    const entryId = key.slice('pb.debrief.'.length);
    if (entryId === '') continue;
    if (value === null || value === '') continue;
    out.push({ entryId, payloadJson: value });
  }
  return out;
}

/**
 * 收集历史粘贴材料(pb-pasted-<后缀>):content = 本地原值逐字;
 * 口径同 collectLegacyDebriefs(票77:孤儿后缀同样收集)。
 */
export function collectLegacyPasted(
  pairs: readonly { key: string; value: string | null }[]
): LegacyPastedImportItem[] {
  const out: LegacyPastedImportItem[] = [];
  for (const { key, value } of pairs) {
    if (!key.startsWith('pb-pasted-')) continue;
    const entryId = key.slice('pb-pasted-'.length);
    if (entryId === '') continue;
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
    version: dto.version,
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
