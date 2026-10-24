import { describe, expect, it } from '@jest/globals';
import {
  classifyPrepStage, prepFrameworkSectionIds, prepQuestionExportBlocks
} from './prep-report.helpers';
import { safeJson } from '../v2.models';

/** 票133:TJ-1 分类镜像 + 骨架同构;TJ-2 note 呈现与导出排除 + 旧报告兼容(数据层)。 */

describe('classifyPrepStage (§4.2 mirror)', () => {
  const cases: Array<[string | null | undefined, boolean, boolean, boolean, boolean, boolean]> = [
    ['Coding', true, false, false, false, false],
    ['Live Coding', true, false, false, false, false],
    ['Pair Programming', true, false, false, false, false],
    ['Online Assessment', true, false, false, false, false],
    ['OA', true, false, false, false, false],
    ['HackerRank', true, false, false, false, false],
    ['Codility', true, false, false, false, false],
    ['System Design', false, true, false, false, false],
    ['SystemDesign', false, true, false, false, false],
    ['Architect', false, true, false, false, false],
    ['Design', false, true, false, false, false],
    ['Screen', false, false, true, false, false],
    ['Phone Screen', false, false, true, false, false],
    ['Recruiter Call', false, false, true, false, false],
    ['HR Round', false, false, true, false, false],
    ['Behavioral', false, false, false, true, false],
    ['Culture Fit', false, false, false, true, false],
    ['Final', false, false, false, false, true],
    ['Hiring Manager', false, false, false, false, true],
    ['Onsite', false, false, false, false, true],
    ['Panel', false, false, false, false, true],
    ['Technical', false, false, false, false, false],
    ['', false, false, false, false, false],
    [null, false, false, false, false, false],
    [undefined, false, false, false, false, false],
  ];
  it.each(cases)('stage %s → coding=%s design=%s screen=%s behavioral=%s final=%s',
    (stage, coding, design, screen, behavioral, final) => {
      const cls = classifyPrepStage(stage);
      expect(cls).toEqual({ isCoding: coding, isDesign: design, isScreen: screen, isBehavioral: behavioral, isFinal: final });
    });
});

describe('prepFrameworkSectionIds (§4.3 matrix)', () => {
  it('SystemDesign → 12 sections, design-round present, no coding-round', () => {
    const ids = prepFrameworkSectionIds(classifyPrepStage('SystemDesign'));
    expect(ids).toEqual(['round-brief', 'greeting', 'intro-listen', 'self-intro', 'deep-dive', 'tech-qa',
      'design-round', 'behavioral', 'weakness-plan', 'ask-back', 'comp-process', 'closing']);
  });
  it('Coding → 12 sections, coding-round at position 7, no design-round (card lives in tech-qa)', () => {
    const ids = prepFrameworkSectionIds(classifyPrepStage('Coding'));
    expect(ids).toHaveLength(12);
    expect(ids[6]).toBe('coding-round');
    expect(ids).not.toContain('design-round');
  });
  it('Behavioral → 11 sections, neither conditional section', () => {
    const ids = prepFrameworkSectionIds(classifyPrepStage('Behavioral'));
    expect(ids).toHaveLength(11);
    expect(ids).not.toContain('coding-round');
    expect(ids).not.toContain('design-round');
  });
  it('empty stage → 11 sections, neither conditional section', () => {
    const ids = prepFrameworkSectionIds(classifyPrepStage(''));
    expect(ids).toHaveLength(11);
    expect(ids).not.toContain('coding-round');
    expect(ids).not.toContain('design-round');
  });
});

describe('prepQuestionExportBlocks (note exclusion)', () => {
  it('note text never appears in export blocks, question + stds do', () => {
    const blocks = prepQuestionExportBlocks(
      {
        question: 'What is a hash map?',
        category: 'Technical',
        sourceTags: ['past'],
        weaknessRelated: true,
        stdSpoken: 'spoken answer here',
        stdTechnical: 'technical answer here',
        note: 'INTERNAL-DIAGNOSIS-ONLY: could not explain collisions'
      },
      { linked: '', stackLabel: 'Stack', spokenLabel: 'Spoken', technicalLabel: 'Technical' });
    const joined = blocks.map(b => ('label' in b ? b.label + ' ' : '') + ('text' in b ? b.text : '')).join('\n');
    expect(joined).toContain('What is a hash map?');
    expect(joined).toContain('spoken answer here');
    expect(joined).toContain('technical answer here');
    expect(joined).not.toContain('INTERNAL-DIAGNOSIS-ONLY');
  });
});

describe('old 8-section report compat (data layer)', () => {
  it('safeJson parses an old 8-section fixture with 8 ids intact', () => {
    const old = JSON.stringify({
      sections: ['greeting', 'intro-listen', 'self-intro', 'deep-dive', 'tech-qa',
        'behavioral', 'ask-back', 'closing'].map(id => ({ id, title: id, blocks: [], questions: [] }))
    });
    const parsed = safeJson<{ sections: Array<{ id: string }> }>(old);
    expect(parsed?.sections.map(s => s.id)).toEqual(['greeting', 'intro-listen', 'self-intro',
      'deep-dive', 'tech-qa', 'behavioral', 'ask-back', 'closing']);
  });
});
