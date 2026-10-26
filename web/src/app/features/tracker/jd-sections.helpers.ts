import { JdSection } from '../../core/models/api.models';

/**
 * 票 154：JD 动态小节前端纯函数（无 TestBed 可直接 Jest 覆盖）。
 * 与后端 JdSectionJson / JdSectionProjection 同口径；标题归一化规则与后端
 * NormalizeTitle 一致（小写 + 空白折叠 + 弯引号转直引号 + 去尾冒号/句号）。
 */

/** 空白折叠（前后 trim + 连续空白 → 单空格）。 */
export function normalizeSectionText(text: string | null | undefined): string {
  return (text ?? '').replace(/\s+/g, ' ').trim();
}

/** 标题归一化 key（仅用于匹配/去重，不改原标题）。 */
export function normalizeSectionKey(title: string | null | undefined): string {
  return normalizeSectionText(title)
    .toLowerCase()
    .replace(/[‘’]/g, "'")
    .replace(/[“”]/g, '"')
    .replace(/[:：.。]+$/, '');
}

/**
 * 安全解析 SectionsJson：非法 JSON / 非数组 / 非对象项一律返回 null（读路径不抛错，
 * 调用方回退 legacy 渲染）。空数组返回 []（调用方按长度判断，与 null 同义处理）。
 */
export function parseSectionsJson(json: string | null | undefined): JdSection[] | null {
  if (json == null) return null;
  try {
    const raw = JSON.parse(json);
    if (!Array.isArray(raw)) return null;
    return raw
      .filter((it) => it && typeof it === 'object')
      .map((it) => ({
        title: typeof it.title === 'string' ? it.title : '',
        key: typeof it.key === 'string' && it.key
          ? it.key : normalizeSectionKey(typeof it.title === 'string' ? it.title : ''),
        body: typeof it.body === 'string' ? it.body : '',
        origin: it.origin === 'manual' ? 'manual' as const : 'auto' as const,
        edited: it.edited === true,
      }));
  } catch {
    return null;
  }
}

/** 公司介绍类小节标题集合（归一化 key）。 */
const COMPANY_INTRO_KEYS = new Set(['about the company', 'about us', 'who we are', 'our company']);

/** 是否为公司介绍类小节。 */
export function isCompanyIntroSection(section: JdSection): boolean {
  return COMPANY_INTRO_KEYS.has(normalizeSectionKey(section.title));
}

/**
 * 详情页可见小节（票 154 首步定法 ①：内容相等去重）。
 * 公司介绍类小节仅当其正文（空白折叠后）与已存 companyDescription 逐字相等时，
 * 才从 Overview/Score 的小节列表中排除 —— 否则照常渲染。
 * 纯前端渲染规则：存储两侧都保留，不做静默合并；companyDescription 为空时不排除。
 */
export function visibleDetailSections(
  sections: JdSection[] | null | undefined,
  companyDescription: string | null | undefined,
): JdSection[] {
  if (!sections || sections.length === 0) return [];
  const desc = normalizeSectionText(companyDescription);
  if (!desc) return sections;
  return sections.filter(
    (s) => !(isCompanyIntroSection(s) && normalizeSectionText(s.body) === desc),
  );
}

/**
 * 重解析合并（票 154 R-154-7）。
 * - 保留项：已编辑节（edited）与手动节（origin manual），其余自动节全部刷新；
 * - 同 key 保留项在新列表对应位置原地替换（标题改过导致 key 对不上的保留项，
 *   以及无新节对应的保留项，按旧顺序追加在末尾 —— 不丢数据，重复项用户可删）；
 * - 同 key 多个保留项时，第一个替换、其余追加（不互相覆盖）。
 */
export function mergeSectionsForReparse(
  stored: JdSection[],
  fresh: JdSection[],
): JdSection[] {
  const kept = stored.filter((s) => s.edited || s.origin === 'manual');
  const used = new Set<JdSection>();
  const out: JdSection[] = [];
  for (const f of fresh) {
    const fk = normalizeSectionKey(f.title);
    const k = kept.find((c) => !used.has(c) && normalizeSectionKey(c.title) === fk);
    if (k) {
      used.add(k);
      out.push(k);
    } else {
      out.push(f);
    }
  }
  for (const k of kept) {
    if (!used.has(k)) out.push(k);
  }
  return out;
}
