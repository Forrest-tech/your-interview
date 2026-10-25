import { describe, expect, it } from '@jest/globals';
import {
  mergeSectionsForReparse,
  normalizeSectionKey,
  normalizeSectionText,
  parseSectionsJson,
  visibleDetailSections,
} from './jd-sections.helpers';
import { JdSection } from '../../core/models/api.models';

const sec = (title: string, body: string, origin: 'auto' | 'manual' = 'auto', edited = false): JdSection =>
  ({ title, key: normalizeSectionKey(title), body, origin, edited });

describe('jd-sections.helpers (票 154)', () => {
  it('normalizeSectionKey：弯引号转直引号 + 去尾冒号 + 小写', () => {
    expect(normalizeSectionKey('What You’ll Bring:')).toBe("what you'll bring");
    expect(normalizeSectionKey('  About Us。')).toBe('about us');
  });

  it('parseSectionsJson：非法 JSON 返回 null，不抛错；缺字段有默认值', () => {
    expect(parseSectionsJson(null)).toBeNull();
    expect(parseSectionsJson('{not json')).toBeNull();
    expect(parseSectionsJson('{"a":1}')).toBeNull();
    const parsed = parseSectionsJson('[{"title":"Benefits","body":"Health."}]')!;
    expect(parsed.length).toBe(1);
    expect(parsed[0].key).toBe('benefits');
    expect(parsed[0].origin).toBe('auto');
    expect(parsed[0].edited).toBe(false);
  });

  it('visibleDetailSections：公司介绍节正文与 companyDescription 相等时排除，否则保留', () => {
    const sections = [
      sec('About Us', 'We build software.'),
      sec('About the Role', 'We build software.'),
      sec('Responsibilities', 'Build software.'),
    ];
    // 正文相等 → 仅 About Us 被排除（About the Role 不是公司介绍类，不受影响）。
    expect(visibleDetailSections(sections, 'We build software.').map((s) => s.title))
      .toEqual(['About the Role', 'Responsibilities']);
    // 正文不等（用户在 142 入口改过 companyDescription）→ 全部保留。
    expect(visibleDetailSections(sections, 'Something else entirely.').length).toBe(3);
    // companyDescription 为空 → 全部保留（不隐藏无处可看的 About Us）。
    expect(visibleDetailSections(sections, null).length).toBe(3);
    expect(visibleDetailSections(null, 'x')).toEqual([]);
  });

  it('mergeSectionsForReparse：自动未编辑节刷新，已编辑/手动节原地保留，无对应项追加', () => {
    const stored = [
      sec('Qualifications', 'OLD body here.', 'auto', false),
      sec('Benefits', 'USER EDITED benefits body.', 'auto', true),
      sec('My Notes', 'Manual note body.', 'manual', false),
    ];
    const fresh = [
      sec('Qualifications', 'NEW body here.', 'auto', false),
      sec('Benefits', 'Fresh benefits body.', 'auto', false),
    ];
    const merged = mergeSectionsForReparse(stored, fresh);
    expect(merged.map((s) => s.title)).toEqual(['Qualifications', 'Benefits', 'My Notes']);
    expect(merged[0].body).toBe('NEW body here.');
    expect(merged[1].body).toBe('USER EDITED benefits body.');
    expect(merged[2].body).toBe('Manual note body.');
  });

  it('mergeSectionsForReparse：同 key 两个保留项，第一个替换、第二个追加不覆盖', () => {
    const stored = [
      sec('Benefits', 'First edited.', 'auto', true),
      sec('Benefits', 'Second edited.', 'auto', true),
    ];
    const fresh = [sec('Benefits', 'Fresh body.', 'auto', false)];
    const merged = mergeSectionsForReparse(stored, fresh);
    expect(merged.map((s) => s.body)).toEqual(['First edited.', 'Second edited.']);
  });

  it('mergeSectionsForReparse：改过标题的保留项 key 对不上时追加在末尾，不丢数据', () => {
    const stored = [sec('My Renamed Section', 'Edited body.', 'auto', true)];
    const fresh = [sec('Qualifications', 'Fresh body.', 'auto', false)];
    const merged = mergeSectionsForReparse(stored, fresh);
    expect(merged.map((s) => s.title)).toEqual(['Qualifications', 'My Renamed Section']);
  });

  it('normalizeSectionText：空白折叠', () => {
    expect(normalizeSectionText('  a\n\n  b\t c ')).toBe('a b c');
    expect(normalizeSectionText(null)).toBe('');
  });
});
