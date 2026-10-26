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
 * 票 189：单句/短语行判定（JD 渲染列表恢复用）。
 * 原帖 <ul><li> 经纯文本粘贴后 bullet 字符常丢失（自定义 CSS marker 不进剪贴板），
 * 只剩换行分隔的短行；旧 richBlocks 把这些行当散文段落合并成一团。
 * 单句行 = 行内无“句中标点 + 空白 + 文字”（即至多一个句子），且首字符不是小写字母
 * （硬换行续行通常小写开头，不算独立项）。显式 bullet/编号行由调用方先行处理，
 * 不走本判定。中文行首汉字不属小写字母，同样算候选（中文无大小写，硬换行
 * 误判为列表项时仍分行可读，不会挤成一团）。
 */
export function isListPhraseLine(line: string): boolean {
  const ln = line.trim();
  if (!ln) return false;
  if (/[.。!！?？]\s+\S/.test(ln)) return false;
  return !/\p{Ll}/u.test(ln[0]);
}

/**
 * 票 189：小节去重（摘要 vs 小节正文前导重叠）。
 * 票 152 摘要 = JD 正文前 N 字原文摘录，故摘要文本必然与首节（如 Job Overview）
 * 的前导段落逐字重叠；渲染时若两处都显示即重复。本函数纯前端展示层去重，
 * 存储（jdSummary / SectionsJson）一字不动。
 * 算法：按行顺序消费摘要剩余文本 —— 整行被摘要剩余文本覆盖则整行去掉；
 * 行长于摘要剩余且以其开头（摘要句末截断、行内还有后半段）则只去掉重叠前缀、
 * 保留行内剩余新内容；首行与摘要剩余无重叠即判定摘要已无后续重叠，余行全留。
 * 去重后空节整节去掉；无任何改动时返回原节对象（渲染零差异）。
 */
export function dedupSectionsAgainstSummary(
  summary: string | null | undefined,
  sections: JdSection[] | null | undefined,
): JdSection[] {
  if (!sections || sections.length === 0) return [];
  let rest = normalizeSectionText(summary);
  if (!rest) return sections;
  const out: JdSection[] = [];
  for (const s of sections) {
    const lines = (s.body ?? '').split('\n');
    const kept: string[] = [];
    let changed = false;
    for (const raw of lines) {
      const ln = normalizeSectionText(raw);
      if (!ln) continue;
      if (!rest) { kept.push(ln); continue; }
      if (rest.startsWith(ln)) {
        rest = rest.slice(ln.length).trim();
        changed = true;
        continue;
      }
      if (ln.startsWith(rest)) {
        const rem = ln.slice(rest.length).trim();
        rest = '';
        changed = true;
        if (rem) kept.push(rem);
        continue;
      }
      kept.push(ln);
      rest = '';
    }
    if (kept.length === 0) continue;
    out.push(changed ? { ...s, body: kept.join('\n') } : s);
  }
  return out;
}

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
