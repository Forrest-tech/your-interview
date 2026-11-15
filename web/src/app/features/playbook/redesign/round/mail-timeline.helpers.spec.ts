import { describe, expect, it } from '@jest/globals';
import {
  TimelineAssetLike,
  UNASSIGNED_KEY,
  buildMailTimeline,
  timelineMailOf,
} from './mail-timeline.helpers';

const mail = (
  id: string,
  o: { subject?: string; date?: string; roundId?: string | null; interviewAt?: string; uploadedAt?: string } = {},
): TimelineAssetLike => ({
  id,
  kind: 'Text',
  uploadedAt: o.uploadedAt ?? '2026-01-01T00:00:00Z',
  roundId: o.roundId ?? null,
  metaJson: JSON.stringify({
    channel: 'email',
    subject: o.subject ?? 'Hello',
    date: o.date,
    interviewAt: o.interviewAt,
  }),
});

const R1 = { id: 'r1', order: 1, scheduledDate: '2026-10-07' };
const R2 = { id: 'r2', order: 2, scheduledDate: null };
const R3 = { id: 'r3', order: 3, scheduledDate: null };

describe('timelineMailOf —— 邮件判定与字段', () => {
  it('channel=email 识别为邮件，日期取 meta.date', () => {
    const m = timelineMailOf(mail('a', { date: '2026-10-05', subject: 'S' }));
    expect(m).toEqual({ id: 'a', roundId: null, subject: 'S', date: '2026-10-05', interviewAt: null });
  });
  it('无 meta.date 时回落 uploadedAt 日期部', () => {
    expect(timelineMailOf(mail('a', { uploadedAt: '2026-09-30T23:00:00Z' }))!.date).toBe('2026-09-30');
  });
  it('Text + category 也算邮件（与 materials-list 同口径）', () => {
    const a = { id: 'x', kind: 'Text', metaJson: JSON.stringify({ category: 'invite' }) };
    expect(timelineMailOf(a)).not.toBeNull();
  });
  it('非邮件、坏 JSON、无 meta 均返回 null', () => {
    expect(timelineMailOf({ id: 'n', kind: 'Notes', metaJson: JSON.stringify({ channel: 'note' }) })).toBeNull();
    expect(timelineMailOf({ id: 'b', kind: 'Text', metaJson: '{oops' })).toBeNull();
    expect(timelineMailOf({ id: 'c', kind: 'Audio' })).toBeNull();
    expect(timelineMailOf({ id: 'd', kind: 'Notes', metaJson: JSON.stringify({ category: 'x' }) })).toBeNull();
  });
});

describe('buildMailTimeline —— 节点结构', () => {
  it('每个轮次一个节点（含零邮件轮次），按日期序，编号为展示编号', () => {
    const late = { id: 'late', order: 1, scheduledDate: '2026-11-01' };
    const early = { id: 'early', order: 2, scheduledDate: '2026-10-01' };
    const t = buildMailTimeline([], [late, early, R3]);
    expect(t.nodes.map(n => [n.roundId, n.displayNo, n.scheduledDate])).toEqual([
      ['early', 1, '2026-10-01'],
      ['late', 2, '2026-11-01'],
      ['r3', 3, ''],
    ]);
    expect(t.nodes.every(n => n.mailIds.length === 0 && n.firstDate === null)).toBe(true);
    expect(t.total).toBe(0);
  });

  it('无未指派邮件时不渲染未指派节点', () => {
    const t = buildMailTimeline([mail('a', { roundId: 'r1', date: '2026-10-06' })], [R1, R2]);
    expect(t.nodes.some(n => n.key === UNASSIGNED_KEY)).toBe(false);
  });

  it('有未指派邮件时未指派节点排在最后', () => {
    const t = buildMailTimeline([mail('f', { date: '2026-10-20' })], [R1, R2]);
    const last = t.nodes[t.nodes.length - 1];
    expect(last.key).toBe(UNASSIGNED_KEY);
    expect(last.kind).toBe('unassigned');
    expect(last.displayNo).toBeNull();
    expect(last.mailIds).toEqual(['f']);
    expect(last.sources['f']).toBe('unassigned');
  });

  it('没有任何轮次：全部进未指派，默认展开为 null', () => {
    const t = buildMailTimeline([mail('a'), mail('b')], []);
    expect(t.nodes.map(n => n.key)).toEqual([UNASSIGNED_KEY]);
    expect(t.defaultOpenKey).toBeNull();
  });

  it('非邮件资产被忽略；同 id 只计一次', () => {
    const note = { id: 'n', kind: 'Notes', roundId: 'r1', metaJson: JSON.stringify({ channel: 'note' }) };
    const a = mail('a', { roundId: 'r1', date: '2026-10-06' });
    const t = buildMailTimeline([note, a, a], [R1]);
    expect(t.total).toBe(1);
    expect(t.nodes[0].mailIds).toEqual(['a']);
  });
});

describe('buildMailTimeline —— 归属（走 311a）', () => {
  it('Corpay 结构：主题 2nd round 覆盖存储的 Round 1', () => {
    const t = buildMailTimeline(
      [
        mail('inv1', { subject: '1st round of technical interview — Corpay', roundId: 'r1', date: '2026-10-01' }),
        mail('inv2', { subject: '2nd round of technical interview — Corpay', roundId: 'r1', date: '2026-10-08' }),
        mail('re2', { subject: 'Re: 2nd round of technical interview', roundId: 'r1', date: '2026-10-09' }),
        mail('fu', { subject: 'Following up', roundId: 'r1', date: '2026-10-07' }),
        mail('manual', { subject: 'Notes', date: '2026-10-02' }),
      ],
      [R1, R2],
    );
    const [n1, n2, un] = t.nodes;
    expect(n1.mailIds).toEqual(['inv1', 'fu']);
    expect(n1.sources).toEqual({ inv1: 'subject', fu: 'stored' });
    expect(n2.mailIds).toEqual(['inv2', 're2']);
    expect(n2.sources).toEqual({ inv2: 'subject', re2: 'subject' });
    expect(un.key).toBe(UNASSIGNED_KEY);
    expect(un.mailIds).toEqual(['manual']);
    expect(t.total).toBe(5);
  });

  it('needsReview（存储轮次后还有轮次且邮件晚 >3 天）进未指派并记录', () => {
    const r2dated = { id: 'r2', order: 2, scheduledDate: '2026-10-20' };
    const t = buildMailTimeline([mail('late', { subject: 'Next steps', roundId: 'r1', date: '2026-10-15' })], [R1, r2dated]);
    const un = t.nodes.find(n => n.key === UNASSIGNED_KEY)!;
    expect(un.mailIds).toEqual(['late']);
    expect(un.needsReviewIds).toEqual(['late']);
  });

  it('悬空 roundId 不建节点，落入后续规则', () => {
    const t = buildMailTimeline([mail('x', { roundId: 'gone', date: '2026-10-06' })], [R1]);
    expect(t.nodes.map(n => n.key)).toEqual(['round:r1', UNASSIGNED_KEY]);
  });
});

describe('buildMailTimeline —— 排序、跨度、默认展开', () => {
  it('节点内日期正序；同日保持输入原序', () => {
    const t = buildMailTimeline(
      [
        mail('c', { roundId: 'r1', date: '2026-10-06' }),
        mail('a', { roundId: 'r1', date: '2026-10-05' }),
        mail('d', { roundId: 'r1', date: '2026-10-06' }),
        mail('b', { roundId: 'r1', date: '2026-10-05' }),
      ],
      [R1],
    );
    expect(t.nodes[0].mailIds).toEqual(['a', 'b', 'c', 'd']);
  });

  it('日期跨度为节点内最早/最晚日期', () => {
    const t = buildMailTimeline(
      [
        mail('a', { roundId: 'r1', date: '2026-10-09' }),
        mail('b', { roundId: 'r1', date: '2026-10-02' }),
        mail('c', { roundId: 'r1', date: '2026-10-05' }),
      ],
      [R1],
    );
    expect([t.nodes[0].firstDate, t.nodes[0].lastDate]).toEqual(['2026-10-02', '2026-10-09']);
  });

  it('默认展开最后一个轮次节点（未指派节点不参与）', () => {
    const t = buildMailTimeline([mail('f')], [R2, R1]);
    expect(t.defaultOpenKey).toBe('round:r2');
    expect(t.nodes[t.nodes.length - 1].key).toBe(UNASSIGNED_KEY);
  });

  it('不改输入', () => {
    const assets = [mail('a', { roundId: 'r1', date: '2026-10-06' })];
    const rounds = [R2, R1];
    const snapA = JSON.stringify(assets);
    const snapR = JSON.stringify(rounds);
    buildMailTimeline(assets, rounds);
    expect(JSON.stringify(assets)).toBe(snapA);
    expect(JSON.stringify(rounds)).toBe(snapR);
  });

  it('1000 封 × 6 轮 < 200ms', () => {
    const rounds = Array.from({ length: 6 }, (_, i) => ({
      id: 'r' + i, order: i + 1, scheduledDate: `2026-10-${String(i * 4 + 1).padStart(2, '0')}`,
    }));
    const assets = Array.from({ length: 1000 }, (_, i) =>
      mail('m' + i, { roundId: 'r' + (i % 6), date: `2026-10-${String((i % 28) + 1).padStart(2, '0')}`, subject: i % 7 === 0 ? `Round ${(i % 6) + 1}` : 'x' }));
    // 预热：首次调用含 JIT/正则编译，冷启动在慢机上会抖到 200ms 上下；计稳态耗时（阈值不放宽）
    buildMailTimeline(assets.slice(0, 20), rounds);
    const t0 = Date.now();
    const t = buildMailTimeline(assets, rounds);
    expect(Date.now() - t0).toBeLessThan(200);
    expect(t.nodes.reduce((s, n) => s + n.mailIds.length, 0)).toBe(1000);
  });
});
