import { describe, expect, it } from '@jest/globals';
import { RoundReminderDto } from '../../../../core/models/api.models';
import {
  dueReminders,
  formatRemindAt,
  reminderStateForRound,
  remindersForRound,
  withoutReminder
} from './reminders.helpers';

/** CONF-CAL-2:提醒展示纯逻辑——筛选/排序/状态推导/ack 摘除/时刻格式。 */

function rem(partial: Partial<RoundReminderDto> & { id: string }): RoundReminderDto {
  return {
    entryId: 'e1',
    roundId: 'r1',
    companyName: 'Acme',
    role: 'Dev',
    stage: 'Technical',
    roundOrder: 1,
    scheduledDate: '2026-10-30',
    scheduledTime: '14:30',
    startAt: '2026-10-30T14:30:00-04:00',
    kind: 'DayBefore',
    remindAt: '2026-10-29T14:30:00-04:00',
    isDue: false,
    status: 'Pending',
    ...partial
  };
}

const R1 = 'round-1';
const R2 = 'round-2';

describe('remindersForRound', () => {
  it('只取本轮次,按触发时刻升序', () => {
    const list = [
      rem({ id: 'a', roundId: R1, kind: 'HourBefore', remindAt: '2026-10-30T13:30:00-04:00' }),
      rem({ id: 'b', roundId: R2, remindAt: '2026-10-28T10:00:00-04:00' }),
      rem({ id: 'c', roundId: R1, kind: 'DayBefore', remindAt: '2026-10-29T14:30:00-04:00' })
    ];
    expect(remindersForRound(list, R1).map(r => r.id)).toEqual(['c', 'a']);
  });

  it('不改动入参数组顺序', () => {
    const list = [
      rem({ id: 'a', roundId: R1, remindAt: '2026-10-30T13:30:00-04:00' }),
      rem({ id: 'c', roundId: R1, remindAt: '2026-10-29T14:30:00-04:00' })
    ];
    remindersForRound(list, R1);
    expect(list.map(r => r.id)).toEqual(['a', 'c']);
  });

  it('无该轮次提醒 → 空数组', () => {
    expect(remindersForRound([rem({ id: 'a', roundId: R2 })], R1)).toEqual([]);
    expect(remindersForRound([], R1)).toEqual([]);
  });
});

describe('dueReminders', () => {
  it('只取 isDue 的,最早到期在前', () => {
    const list = [
      rem({ id: 'a', isDue: true, remindAt: '2026-10-23T09:00:00-04:00' }),
      rem({ id: 'b', isDue: false, remindAt: '2026-10-23T08:00:00-04:00' }),
      rem({ id: 'c', isDue: true, remindAt: '2026-10-22T09:00:00-04:00' })
    ];
    expect(dueReminders(list).map(r => r.id)).toEqual(['c', 'a']);
  });

  it('无到期 → 空数组', () => {
    expect(dueReminders([rem({ id: 'a' })])).toEqual([]);
  });
});

describe('reminderStateForRound', () => {
  it('无提醒 → none', () => {
    expect(reminderStateForRound([], R1)).toEqual({ kind: 'none' });
    expect(reminderStateForRound([rem({ id: 'a', roundId: R2 })], R1)).toEqual({ kind: 'none' });
  });

  it('有到期 → due,且取最早到期的那条(优先于未到期的)', () => {
    const list = [
      rem({ id: 'up', roundId: R1, isDue: false, remindAt: '2026-10-29T14:30:00-04:00' }),
      rem({ id: 'due2', roundId: R1, isDue: true, remindAt: '2026-10-23T10:00:00-04:00' }),
      rem({ id: 'due1', roundId: R1, isDue: true, remindAt: '2026-10-22T10:00:00-04:00' })
    ];
    const s = reminderStateForRound(list, R1);
    expect(s.kind).toBe('due');
    if (s.kind === 'due') expect(s.reminder.id).toBe('due1');
  });

  it('未到期 → upcoming,取最近一条', () => {
    const list = [
      rem({ id: 'h', roundId: R1, kind: 'HourBefore', remindAt: '2026-10-30T13:30:00-04:00' }),
      rem({ id: 'd', roundId: R1, kind: 'DayBefore', remindAt: '2026-10-29T14:30:00-04:00' })
    ];
    const s = reminderStateForRound(list, R1);
    expect(s.kind).toBe('upcoming');
    if (s.kind === 'upcoming') expect(s.next.id).toBe('d');
  });
});

describe('withoutReminder', () => {
  it('摘除指定 id,其余保序;不存在的 id 原样返回等价列表', () => {
    const list = [rem({ id: 'a' }), rem({ id: 'b' }), rem({ id: 'c' })];
    expect(withoutReminder(list, 'b').map(r => r.id)).toEqual(['a', 'c']);
    expect(withoutReminder(list, 'zz').map(r => r.id)).toEqual(['a', 'b', 'c']);
    expect(list.length).toBe(3);
  });
});

describe('formatRemindAt', () => {
  it('ISO → 本地 MM-dd HH:mm', () => {
    const iso = new Date(2026, 9, 29, 9, 5).toISOString();
    expect(formatRemindAt(iso)).toBe('10-29 09:05');
  });

  it('解析不了 → 原样回显', () => {
    expect(formatRemindAt('not-a-date')).toBe('not-a-date');
  });
});
