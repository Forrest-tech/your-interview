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

/**
 * ★ ML-FIX-UI(W-UI-10):日历页横幅去重 —— 一轮合法产两条提醒
 * (DayBefore + HourBefore),而横幅只显示公司/阶段/日期、不显示种类与触发时刻,
 * 两条到期横幅看起来完全重复。口径与轮次卡 reminderStateForRound 一致:
 * 每轮只留一条横幅(取最早到期的一行),按触发时刻升序;不改动入参数组。
 */
export function dueBanners(all: RoundReminderDto[]): RoundReminderDto[] {
  const earliest = new Map<string, RoundReminderDto>();
  for (const r of all) {
    if (!r.isDue) continue;
    const prev = earliest.get(r.roundId);
    if (!prev || r.remindAt < prev.remindAt) earliest.set(r.roundId, r);
  }
  return [...earliest.values()].sort(byRemindAt);
}

/** 某轮次全部已到期未确认提醒的 id(横幅确认时整轮 ack,免得去重后另一条又冒出来)。 */
export function dueReminderIdsForRound(all: RoundReminderDto[], roundId: string): string[] {
  return all.filter(r => r.roundId === roundId && r.isDue).map(r => r.id);
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
