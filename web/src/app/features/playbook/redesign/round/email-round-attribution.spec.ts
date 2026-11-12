import { describe, expect, it } from '@jest/globals';
import {
  ATTRIBUTION_WINDOW_DAYS,
  AttributionMail,
  AttributionRound,
  attributeEmail,
  attributeEmails,
  extractRoundOrdinal,
  isDigestSubject,
} from './email-round-attribution';

/** 票 311a：邮件归属纯函数。样本均为脱敏的真实形态。 */

describe('extractRoundOrdinal —— 主题轮次标记（中/英/法）', () => {
  const cases: Array<[string, number | null]> = [
    // 英
    ['1st round of technical interview — Full Stack Developer', 1],
    ['2nd round of technical interview — Full Stack Developer', 2],
    ['3rd round of interviews', 3],
    ['4th Round Interview Invitation', 4],
    ['Invitation: Second Interview with the team', 2],
    ['Third round — final conversation', 3],
    ['Interview Round 2 confirmation', 2],
    ['Re: 2nd round of technical interview', 2],
    ['Fwd: Re: Round 3 scheduling', 3],
    // 中
    ['第2轮面试邀请', 2],
    ['第 3 轮技术面试', 3],
    ['第二轮面试安排', 2],
    ['回复: 第三轮面试通知', 3],
    ['二面邀请：后端工程师', 2],
    ['恭喜进入三面，时间安排如下', 3],
    // 法
    ['Invitation au 2e tour d\'entretien', 2],
    ['Votre 1er entretien avec notre équipe', 1],
    ['3ème tour — entretien technique', 3],
    ['Deuxième entretien : confirmation', 2],
    ['Troisième tour avec le directeur', 3],
    ['Tour 2 — rencontre avec l\'équipe', 2],
    ['Re: Deuxième tour d\'entretien', 2],
    // 无标记
    ['Following up on your interview', null],
    ['Your upcoming meetings', null],
    ['Interview Invitation — Full Stack Developer', null],
    ['一面之词', null],
    ['', null],
  ];
  it.each(cases)('%s → %s', (subject, expected) => {
    expect(extractRoundOrdinal(subject)).toBe(expected);
  });
  it('null / undefined 主题 → null', () => {
    expect(extractRoundOrdinal(null)).toBeNull();
    expect(extractRoundOrdinal(undefined)).toBeNull();
  });
  it('多处标记只取最先出现的一个', () => {
    expect(extractRoundOrdinal('2nd round — follow-up to 1st round')).toBe(2);
  });
  it('序数 0 不算标记', () => {
    expect(extractRoundOrdinal('Round 0 orientation')).toBeNull();
  });
});

describe('isDigestSubject —— 周报/摘要类', () => {
  it.each([
    'Your upcoming meetings',
    'Weekly agenda',
    'Your week at a glance',
    '本周日程',
    'Votre résumé hebdomadaire',
  ])('%s → true', s => expect(isDigestSubject(s)).toBe(true));
  it.each([
    '2nd round of technical interview',
    'Interview Invitation',
    '',
  ])('%s → false', s => expect(isDigestSubject(s)).toBe(false));
  it('null → false', () => expect(isDigestSubject(null)).toBe(false));
});

// ---- 轮次夹具：Corpay 形态（Round 1 有日期，Round 2 无日期）----
const R1: AttributionRound = { id: 'r1', order: 1, scheduledDate: '2026-10-07' };
const R2: AttributionRound = { id: 'r2', order: 2, scheduledDate: null };
const R2dated: AttributionRound = { id: 'r2', order: 2, scheduledDate: '2026-10-13' };

const mail = (o: Partial<AttributionMail> & { id: string }): AttributionMail => ({ ...o });

describe('attributeEmail —— 规则 1：主题标记优先于存储值', () => {
  it('★ 主题写 2nd round 而存储为 Round 1 → Round 2（本票核心断言）', () => {
    const r = attributeEmail(
      mail({ id: 'b', subject: '2nd round of technical interview — ...', roundId: 'r1', date: '2026-10-02' }),
      [R1, R2],
    );
    expect(r).toEqual({ roundId: 'r2', source: 'subject', needsReview: false });
  });
  it('1st round 且存储为 Round 1 → Round 1（规则 1 与 2 一致）', () => {
    const r = attributeEmail(mail({ id: 'a', subject: '1st round of technical interview', roundId: 'r1' }), [R1, R2]);
    expect(r).toEqual({ roundId: 'r1', source: 'subject', needsReview: false });
  });
  it('带 Re: 前缀照样命中', () => {
    const r = attributeEmail(mail({ id: 'c', subject: 'Re: 2nd round of ...', roundId: 'r1' }), [R1, R2]);
    expect(r.roundId).toBe('r2');
    expect(r.source).toBe('subject');
  });
  it('法语标记命中', () => {
    const r = attributeEmail(mail({ id: 'f', subject: 'Invitation au 2e tour d\'entretien', roundId: 'r1' }), [R1, R2]);
    expect(r.roundId).toBe('r2');
  });
  it('中文标记命中', () => {
    const r = attributeEmail(mail({ id: 'z', subject: '第二轮面试安排', roundId: 'r1' }), [R1, R2]);
    expect(r.roundId).toBe('r2');
  });
  it('序数按展示编号（日期序）映射，而非存储 order', () => {
    // 补录的早日期轮次 order 大，但展示编号小
    const early: AttributionRound = { id: 'rE', order: 3, scheduledDate: '2026-09-20' };
    const r = attributeEmail(mail({ id: 'x', subject: '1st round of interviews' }), [R1, R2, early]);
    expect(r.roundId).toBe('rE');
  });
  it('序数超出轮次数 → 不建 phantom、不丢邮件，落入规则 2', () => {
    const r = attributeEmail(mail({ id: 'y', subject: '3rd round of interviews', roundId: 'r1', date: '2026-10-08' }), [R1, R2]);
    expect(r).toEqual({ roundId: 'r1', source: 'stored', needsReview: false });
  });
});

describe('attributeEmail —— 规则 2：存储 roundId', () => {
  it('无标记、存储 Round 1、邮件日期在窗口内 → Round 1', () => {
    const r = attributeEmail(mail({ id: 'd', subject: 'Following up on your interview', roundId: 'r1', date: '2026-10-09' }), [R1, R2]);
    expect(r).toEqual({ roundId: 'r1', source: 'stored', needsReview: false });
  });
  it('★ 存储 Round 1、有后续轮次、邮件晚于 Round 1 超过 3 天 → 未指派 + 待核对', () => {
    const r = attributeEmail(mail({ id: 'd2', subject: 'Following up', roundId: 'r1', date: '2026-10-12' }), [R1, R2]);
    expect(r).toEqual({ roundId: null, source: 'unassigned', needsReview: true });
  });
  it('恰好 3 天 → 仍信任存储值（窗口含边界）', () => {
    const r = attributeEmail(mail({ id: 'e', subject: 'Following up', roundId: 'r1', date: '2026-10-10' }), [R1, R2]);
    expect(r.source).toBe('stored');
    expect(ATTRIBUTION_WINDOW_DAYS).toBe(3);
  });
  it('修正对任意轮次 N 生效（不止 Round 1）', () => {
    const rounds: AttributionRound[] = [
      { id: 'a', order: 1, scheduledDate: '2026-10-01' },
      { id: 'b', order: 2, scheduledDate: '2026-10-08' },
      { id: 'c', order: 3, scheduledDate: '2026-10-20' },
    ];
    const r = attributeEmail(mail({ id: 'n', subject: 'Following up', roundId: 'b', date: '2026-10-14' }), rounds);
    expect(r).toEqual({ roundId: null, source: 'unassigned', needsReview: true });
  });
  it('最后一轮没有后续轮次 → 不修正', () => {
    const r = attributeEmail(mail({ id: 'l', subject: 'Thanks', roundId: 'r2', date: '2026-11-20' }), [R1, R2dated]);
    expect(r).toEqual({ roundId: 'r2', source: 'stored', needsReview: false });
  });
  it('存储轮次无日期或邮件无日期 → 不修正，信任存储值', () => {
    expect(attributeEmail(mail({ id: 'p', subject: 'x', roundId: 'r2', date: '2026-10-30' }), [R1, R2]).source).toBe('stored');
    expect(attributeEmail(mail({ id: 'q', subject: 'x', roundId: 'r1' }), [R1, R2]).source).toBe('stored');
  });
  it('roundId 悬空（轮次已删）→ 视同空，落入后续规则', () => {
    const r = attributeEmail(mail({ id: 'g', subject: 'Following up', roundId: 'gone' }), [R1, R2]);
    expect(r).toEqual({ roundId: null, source: 'unassigned', needsReview: false });
  });
});

describe('attributeEmail —— 规则 3：面试时间贴近度（保守）', () => {
  it('无存储值、interviewAt 紧邻唯一有日期轮次 → 该轮次', () => {
    const r = attributeEmail(mail({ id: 'h', subject: 'Interview Invitation', interviewAt: '2026-10-07 13:00' }), [R1, R2]);
    expect(r).toEqual({ roundId: 'r1', source: 'proximity', needsReview: false });
  });
  it('距离超过 3 天 → 不猜，未指派', () => {
    const r = attributeEmail(mail({ id: 'i', subject: 'Interview Invitation', interviewAt: '2026-10-20 13:00' }), [R1, R2]);
    expect(r.source).toBe('unassigned');
  });
  it('距离并列 → 不猜，未指派', () => {
    const rounds: AttributionRound[] = [
      { id: 'a', order: 1, scheduledDate: '2026-10-07' },
      { id: 'b', order: 2, scheduledDate: '2026-10-11' },
    ];
    const r = attributeEmail(mail({ id: 'j', subject: 'Interview Invitation', interviewAt: '2026-10-09 10:00' }), rounds);
    expect(r.source).toBe('unassigned');
  });
  it('最近者唯一（两轮都有日期）→ 取更近者', () => {
    const r = attributeEmail(mail({ id: 'k', subject: 'Invitation', interviewAt: '2026-10-12 10:00' }), [R1, R2dated]);
    expect(r).toEqual({ roundId: 'r2', source: 'proximity', needsReview: false });
  });
  it('★ 周报类：interviewAt 只是同周，不参与贴近度 → 未指派', () => {
    const r = attributeEmail(mail({ id: 'w', subject: 'Your upcoming meetings', interviewAt: '2026-10-07 09:00' }), [R1, R2]);
    expect(r.source).toBe('unassigned');
  });
  it('无 interviewAt 或条目无有日期轮次 → 跳过本规则', () => {
    expect(attributeEmail(mail({ id: 'n1', subject: 'Hello' }), [R1, R2]).source).toBe('unassigned');
    expect(
      attributeEmail(mail({ id: 'n2', subject: 'Hello', interviewAt: '2026-10-07 10:00' }),
        [{ id: 'x', order: 1 }, { id: 'y', order: 2 }]).source,
    ).toBe('unassigned');
  });
  it('非法日期（2月30日）→ 跳过', () => {
    const r = attributeEmail(mail({ id: 'bad', subject: 'Hello', interviewAt: '2026-02-30 10:00' }), [R1, R2]);
    expect(r.source).toBe('unassigned');
  });
});

describe('attributeEmail —— 规则 4 与边界', () => {
  it('三无邮件（无主题、无存储值、无日期）→ 未指派，非待核对', () => {
    expect(attributeEmail(mail({ id: 'u' }), [R1, R2])).toEqual({ roundId: null, source: 'unassigned', needsReview: false });
  });
  it('条目没有轮次 → 一律未指派', () => {
    expect(attributeEmail(mail({ id: 'u2', subject: '2nd round' }), [])).toEqual({ roundId: null, source: 'unassigned', needsReview: false });
  });
  it('纯函数：不修改输入', () => {
    const rounds = [R2, R1];
    const m = mail({ id: 'pure', subject: '2nd round', roundId: 'r1' });
    const snap = JSON.stringify([rounds, m]);
    attributeEmail(m, rounds);
    expect(JSON.stringify([rounds, m])).toBe(snap);
  });
  it('轮次顺序不影响结果（按日期序映射）', () => {
    const m = mail({ id: 'o', subject: '2nd round of interviews' });
    expect(attributeEmail(m, [R1, R2]).roundId).toBe(attributeEmail(m, [R2, R1]).roundId);
  });
});

describe('attributeEmails —— 端到端样本（Corpay 形态，6 封）', () => {
  const mails: AttributionMail[] = [
    { id: 'A', subject: '1st round of technical interview — ...', roundId: 'r1', date: '2026-10-02', interviewAt: '2026-10-07 13:00' },
    { id: 'B', subject: '2nd round of technical interview — ...', roundId: 'r1', date: '2026-10-08', interviewAt: '2026-10-13 13:00' },
    { id: 'C', subject: 'Re: 2nd round of technical interview — ...', roundId: 'r1', date: '2026-10-09' },
    { id: 'D', subject: 'Following up on your interview', roundId: 'r1', date: '2026-10-09' },
    { id: 'E', subject: 'Your upcoming meetings', roundId: null, date: '2026-10-05', interviewAt: '2026-10-07 09:00' },
    { id: 'F', subject: '', roundId: null },
  ];
  const res = attributeEmails(mails, [R1, R2]);
  it('A → Round 1（主题）', () => expect(res.get('A')).toEqual({ roundId: 'r1', source: 'subject', needsReview: false }));
  it('B → Round 2（主题覆盖存储）', () => expect(res.get('B')).toEqual({ roundId: 'r2', source: 'subject', needsReview: false }));
  it('C → Round 2（Re: 前缀剥离）', () => expect(res.get('C')?.roundId).toBe('r2'));
  it('D → Round 1（存储，窗口内）', () => expect(res.get('D')).toEqual({ roundId: 'r1', source: 'stored', needsReview: false }));
  it('E（周报）→ 未指派', () => expect(res.get('E')?.source).toBe('unassigned'));
  it('F（三无）→ 未指派', () => expect(res.get('F')?.roundId).toBeNull());
  it('每封邮件都有结果，不丢邮件', () => expect(res.size).toBe(mails.length));
});

describe('attributeEmails —— 性能上限', () => {
  it('1000 封邮件 × 6 轮在 200ms 内', () => {
    const rounds: AttributionRound[] = Array.from({ length: 6 }, (_, i) => ({
      id: `r${i + 1}`, order: i + 1, scheduledDate: `2026-10-${String(i * 3 + 1).padStart(2, '0')}`,
    }));
    const mails: AttributionMail[] = Array.from({ length: 1000 }, (_, i) => ({
      id: `m${i}`,
      subject: i % 3 === 0 ? `${(i % 6) + 1}th round of interviews` : 'Following up',
      roundId: `r${(i % 6) + 1}`,
      date: '2026-10-05',
      interviewAt: '2026-10-05 10:00',
    }));
    const t0 = Date.now();
    const out = attributeEmails(mails, rounds);
    expect(Date.now() - t0).toBeLessThan(200);
    expect(out.size).toBe(1000);
  });
});
