import { Injectable, computed, inject, signal } from '@angular/core';
import { InterviewsApiService } from '../../../../core/api/interviews-api.service';
import { RoundReminderDto } from '../../../../core/models/api.models';
import { dueReminders, withoutReminder } from './reminders.helpers';

/**
 * CONF-CAL-2 提醒的共享状态(日历页 + 条目详情的各轮次卡共用一份)。
 * 单次拉取、ack 后本地摘除;拉取失败不毒化 loaded 标记,下次进入页面重试。
 * 提醒是后端派生数据:排期变更后 refresh() 重新对账读取即可自愈。
 */
@Injectable({ providedIn: 'root' })
export class RoundRemindersService {
  private readonly api = inject(InterviewsApiService);

  readonly reminders = signal<RoundReminderDto[]>([]);
  readonly due = computed(() => dueReminders(this.reminders()));
  private loaded = false;

  /** 首次进入任一消费页面时拉一次;之后复用缓存。 */
  ensureLoaded(): void {
    if (this.loaded) return;
    this.loaded = true;
    this.refresh();
  }

  refresh(): void {
    this.api.getReminders().subscribe({
      next: list => this.reminders.set(list ?? []),
      error: () => { this.loaded = false; }
    });
  }

  /** 确认一条提醒:成功后本地摘除(横幅/日历条即时消失);失败保持原状。 */
  ack(id: string): void {
    this.api.acknowledgeReminder(id).subscribe({
      next: () => this.reminders.update(list => withoutReminder(list, id)),
      error: () => { /* 保持原状,下次 refresh 自愈 */ }
    });
  }
}
