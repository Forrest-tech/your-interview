import { RoundReminderDto } from '../../../../core/models/api.models';

/**
 * CONF-CAL-2 提醒展示纯逻辑(日历页横幅 + 轮次卡日历条共用)。
 * 口径:isDue 以后端判定为准(服务器时钟),前端只做筛选/排序/摘除,不重算到期。
 */

function byRemindAt(a: RoundReminderDto, b: RoundReminderDto): number {
  return a.remindAt < b.remindAt ? -1 : a.remindAt > b.remindAt ? 1 : 0;
}

/** 某轮次的待触发提醒,按触发时刻升序(不改动入参数组)。 */
export function remindersForRound(all: RoundReminderDto[], roundId: string): RoundReminderDto[] {
  return all.filter(r => r.roundId === roundId).sort(byRemindAt);
}

/** 全部已到期未确认的提醒,按触发时刻升序(横幅顺序:最早到期在前)。 */
export function dueReminders(all: RoundReminderDto[]): RoundReminderDto[] {
  return all.filter(r => r.isDue).sort(byRemindAt);
}

/** 轮次卡日历条的真实提醒状态。 */
export type RoundReminderState =
  | { kind: 'none' }
  | { kind: 'due'; reminder: RoundReminderDto }
  | { kind: 'upcoming'; next: RoundReminderDto };

/**
 * 有到期未确认 → due(取最早到期的一条);否则下一条待触发 → upcoming;
 * 该轮次没有任何待触发提醒 → none(日历条只显示静态口径文案)。
 */
export function reminderStateForRound(all: RoundReminderDto[], roundId: string): RoundReminderState {
  const mine = remindersForRound(all, roundId);
  if (mine.length === 0) return { kind: 'none' };
  const due = mine.find(r => r.isDue);
  if (due) return { kind: 'due', reminder: due };
  return { kind: 'upcoming', next: mine[0] };
}

/** ack 成功后把该提醒从本地列表摘除(不可变更新,配合 signal)。 */
export function withoutReminder(all: RoundReminderDto[], id: string): RoundReminderDto[] {
  return all.filter(r => r.id !== id);
}

/** 提醒时刻展示:浏览器本地时区 MM-dd HH:mm;解析不了则原样回显。 */
export function formatRemindAt(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}
