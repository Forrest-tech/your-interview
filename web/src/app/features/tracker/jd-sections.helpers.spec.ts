import { describe, expect, it } from '@jest/globals';
import {
  dedupSectionsAgainstSummary,
  derivedCompanyFromJd,
  isListPhraseLine,
  mergeSectionsForReparse,
  normalizeSectionKey,
  normalizeSectionText,
  parseSectionsJson,
  prepareSectionsSave,
  SectionEditDraft,
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

describe('jd-sections.helpers（票 189）', () => {
  it('dedupSectionsAgainstSummary：Intuit 式——摘要即首段时，Job Overview 首段去掉、余段全留', () => {
    const summary = 'Come join the Intuit Toronto site as a Senior Software Developer.';
    const sections = [
      sec('Job Overview', 'Come join the Intuit Toronto site as a Senior Software Developer.\nThe ideal candidate is an experienced full stack Software Developer.\nIntuit has embraced a hybrid way of working.'),
      sec('Responsibilities', 'Development of customer-facing applications.'),
    ];
    const out = dedupSectionsAgainstSummary(summary, sections);
    expect(out.map((s) => s.title)).toEqual(['Job Overview', 'Responsibilities']);
    expect(out[0].body).toBe(
      'The ideal candidate is an experienced full stack Software Developer.\nIntuit has embraced a hybrid way of working.',
    );
    expect(out[1]).toBe(sections[1]);
  });

  it('dedupSectionsAgainstSummary：摘要句末截断、行内还有后半段时只去重叠前缀、不丢新内容', () => {
    const summary = 'First sentence here.';
    const sections = [sec('Job Overview', 'First sentence here. Second sentence is new.')];
    const out = dedupSectionsAgainstSummary(summary, sections);
    expect(out.length).toBe(1);
    expect(out[0].body).toBe('Second sentence is new.');
  });

  it('dedupSectionsAgainstSummary：整节都被摘要覆盖时整节去掉；无重叠时原样返回同一对象', () => {
    const summary = 'Only paragraph.';
    const sections = [sec('Job Overview', 'Only paragraph.'), sec('Benefits', 'Health and dental.')];
    const out = dedupSectionsAgainstSummary(summary, sections);
    expect(out.map((s) => s.title)).toEqual(['Benefits']);
    expect(out[0]).toBe(sections[1]);
    expect(dedupSectionsAgainstSummary('', sections)).toBe(sections);
    expect(dedupSectionsAgainstSummary(null, sections)).toBe(sections);
  });

  it('isListPhraseLine：列表项行（单句、大写开头）为真；多句段落行与小写续行为假', () => {
    expect(isListPhraseLine('Development of customer-facing applications from design through implementation')).toBe(true);
    expect(isListPhraseLine('BS/MS in computer science or relevant field or equivalent work experience.')).toBe(true);
    expect(isListPhraseLine('Roughly 70-85% hands-on coding')).toBe(true);
    expect(isListPhraseLine('Pay offered is based on factors. To drive ongoing fair pay for employees, we compare.')).toBe(false);
    expect(isListPhraseLine('across multiple lines in the paste')).toBe(false);
    expect(isListPhraseLine('')).toBe(false);
  });
});

describe('jd-sections.helpers（票 204：derivedCompanyFromJd 兜底派生）', () => {
  const intro = 'Intuit is the global financial technology platform that powers prosperity for the people we serve.';
  const job = 'Come join the Intuit Toronto site as a Senior Software Developer building partner platforms.';

  it('Company Overview + 段落 + Job Overview：只取介绍段落，不含 Job Overview 内容', () => {
    const text = `Senior Software Developer, Full-Stack\nCompany Overview\n${intro}\nJob Overview\n${job}`;
    expect(derivedCompanyFromJd(text)).toBe(intro);
  });

  it('标题容忍尾冒号与大小写：Company Overview: / COMPANY OVERVIEW 均命中', () => {
    expect(derivedCompanyFromJd(`Job Title\nCompany Overview:\n${intro}\nBenefits\nHealth.`)).toBe(intro);
    expect(derivedCompanyFromJd(`Job Title\nCOMPANY OVERVIEW\n${intro}\nBenefits\nHealth.`)).toBe(intro);
  });

  it('现有公司介绍标题同样派生：About Us 段落 + 下一标题截断', () => {
    expect(derivedCompanyFromJd(`Role\nAbout Us\n${intro}\nResponsibilities\nBuild things.`)).toBe(intro);
  });

  it('介绍不足 20 字返回 null；无公司标题返回 null；空输入返回 null', () => {
    expect(derivedCompanyFromJd(`Role\nCompany Overview\nShort co.\nJob Overview\n${job}`)).toBeNull();
    expect(derivedCompanyFromJd(`Role\nJob Overview\n${job}`)).toBeNull();
    expect(derivedCompanyFromJd(null)).toBeNull();
    expect(derivedCompanyFromJd('')).toBeNull();
  });
});

describe('jd-sections.helpers（票 226：prepareSectionsSave）', () => {
  const auto = (over: Partial<SectionEditDraft> = {}): SectionEditDraft => ({
    title: 'About Us', body: 'We build things.', origin: 'auto',
    snapTitle: 'About Us', snapBody: 'We build things.', ...over,
  });

  it('全空节静默丢弃；全部丢弃后返回 json null、无错误', () => {
    expect(prepareSectionsSave([
      { title: '   ', body: '', origin: 'manual', snapTitle: '', snapBody: '' },
      { title: '', body: '  ', origin: 'manual', snapTitle: '', snapBody: '' },
    ])).toEqual({ error: null, json: null });
    expect(prepareSectionsSave([])).toEqual({ error: null, json: null });
  });

  it('标题 >60 字阻断（60 字通过、61 字 tooLong），与 SmartAdd 同规则', () => {
    expect(prepareSectionsSave([auto({ title: 'x'.repeat(60) })]).error).toBeNull();
    expect(prepareSectionsSave([auto({ title: 'x'.repeat(61) })])).toEqual({ error: 'tooLong', json: null });
  });

  it('有正文无标题阻断 titleRequired', () => {
    expect(prepareSectionsSave([auto({ title: '  ', body: 'Body' })])).toEqual({ error: 'titleRequired', json: null });
  });

  it('首个失败节决定错误（按顺序，不跳序）', () => {
    const r = prepareSectionsSave([
      auto({ title: '', body: 'Body' }),
      auto({ title: 'y'.repeat(61) }),
    ]);
    expect(r.error).toBe('titleRequired');
  });

  it('edited：自动节改过快照为 true、未改为 false、手动节恒 false；标题/正文 trim、key 归一化', () => {
    const r = prepareSectionsSave([
      auto({ title: '  About Us  ', body: '  We build things. ', snapTitle: 'About Us', snapBody: 'Different.' }),
      auto(),
      { title: 'Perks', body: 'Free lunch.', origin: 'manual', snapTitle: 'Other', snapBody: 'Other body.' },
    ]);
    expect(r.error).toBeNull();
    expect(JSON.parse(r.json!)).toEqual([
      { title: 'About Us', key: 'about us', body: 'We build things.', origin: 'auto', edited: true },
      { title: 'About Us', key: 'about us', body: 'We build things.', origin: 'auto', edited: false },
      { title: 'Perks', key: 'perks', body: 'Free lunch.', origin: 'manual', edited: false },
    ]);
  });
});
