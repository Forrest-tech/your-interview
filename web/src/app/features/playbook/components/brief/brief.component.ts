import { Component, EventEmitter, OnInit, Output, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDividerModule } from '@angular/material/divider';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import type {
  BriefEntry, BriefQuestion, InterviewAction, UpcomingRoundItem,
} from './brief.models';
import {
  applyParams, daysUntil, extractKeywords, makeBriefT, nextRound,
  parseStringList, todayStr, topQuestions,
} from './brief.helpers';

/**
 * F3 一页式备战 Brief（selector: app-prep-brief）。
 *
 * 自上而下固定顺序：
 *  ① 倒计时条（最近未过期轮次：剩余天数 + 形式/会议链接/地点）
 *  ② 公司研究（companyProfile 展示 + 编辑保存）
 *  ③ JD 关键词区（本地高频实词提取，黄底 <mark> 展示）
 *  ④ 必答题 Top5（卡壳优先 + 难度，跳 Q&A 用 jumpToQuestions 输出）
 *  ⑤ “我要问面试官的问题”清单（questionsToAskJson 增删 + PUT 保存）
 *  ⑥ Checklist（A2 actions 里 detail==='checklist' 的事项；为空可一键生成默认三项）
 *
 * 自包含：只依赖 entryId input，自己调 API、自己管 signals。
 * PUT /api/interviews/{id} 的 body 必须带全字段（后端 UpdateBasicInfo 直接赋值，
 * null = 清空；只有 questionsToAskJson 是 null=不动）—— 所以保存时回显详情全部字段。
 */
@Component({
  selector: 'app-prep-brief',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule, MatChipsModule,
    MatFormFieldModule, MatInputModule, MatCheckboxModule,
    MatProgressBarModule, MatSnackBarModule, MatDividerModule,
  ],
  templateUrl: './brief.component.html',
  styleUrl: './brief.component.scss',
})
export class PrepBriefComponent implements OnInit {
  entryId = input.required<string>();
  @Output() jumpToQuestions = new EventEmitter<void>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  t = makeBriefT(this.i18n);
  fmt = (key: string, params: Record<string, string | number>): string =>
    applyParams(this.t(key), params);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entry = signal<BriefEntry | null>(null);
  readonly busy = signal<string | null>(null);

  /** 全局 upcoming 里属于本条目的未来轮次（带 meetingLink/scheduledTime，detail 的 RoundDto 暂无这些字段）。 */
  readonly upcomingMine = signal<UpcomingRoundItem[]>([]);

  // ---------------------------------------------------------- 倒计时条
  /** 优先用 upcoming（字段全），回退 detail rounds。 */
  readonly countdown = computed((): {
    date: string; days: number; format?: string; meetingLink?: string;
    location?: string; stage: string; order: number;
  } | null => {
    const t = todayStr();
    const ups = this.upcomingMine()
      .filter((u) => (u.scheduledDate ?? '') >= t)
      .sort((a, b) => (a.scheduledDate ?? '').localeCompare(b.scheduledDate ?? ''));
    if (ups.length > 0) {
      const u = ups[0];
      return {
        date: u.scheduledDate ?? '', days: daysUntil(u.scheduledDate ?? ''),
        format: u.format, meetingLink: u.meetingLink, location: u.location,
        stage: u.stage, order: u.order,
      };
    }
    const r = nextRound(this.entry()?.rounds);
    if (!r) return null;
    return {
      date: r.scheduledDate ?? '', days: daysUntil(r.scheduledDate ?? ''),
      format: r.format, meetingLink: r.meetingLink, location: r.location,
      stage: r.stage, order: r.order,
    };
  });

  countdownLabel(): string {
    const c = this.countdown();
    if (!c) return this.t('pb.br.countdown.none');
    if (c.days <= 0) return this.t('pb.br.countdown.today');
    if (c.days === 1) return this.t('pb.br.countdown.tomorrow');
    return this.fmt('pb.br.countdown.daysLeft', { n: c.days });
  }

  stageLabel(stage: string): string {
    const key = 'pb.br.stage.' + stage;
    const v = this.t(key);
    return v !== key ? v : stage;
  }

  // ---------------------------------------------------------- 公司研究
  readonly editingProfile = signal(false);
  profileDraft = '';

  openProfileEdit(): void {
    this.profileDraft = this.entry()?.companyProfile ?? '';
    this.editingProfile.set(true);
  }

  saveProfile(): void {
    if (this.busy()) return;
    this.busy.set('profile');
    this.putEntry({ companyProfile: this.profileDraft.trim() || null }).subscribe({
      next: () => {
        this.busy.set(null);
        this.editingProfile.set(false);
        this.entry.update((e) =>
          e ? { ...e, companyProfile: this.profileDraft.trim() || undefined } : e);
        this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  // ---------------------------------------------------------- JD 关键词
  readonly keywords = computed(() => extractKeywords(this.entry()?.jdText ?? ''));

  // ---------------------------------------------------------- 必答题 Top5
  readonly top5 = computed(() => topQuestions(this.entry()?.questions ?? []));

  answerPreview(q: BriefQuestion): string {
    const a = (q.myAnswerText ?? '').trim().replace(/\s+/g, ' ');
    return a ? a.slice(0, 60) + (a.length > 60 ? '…' : '') : this.t('pb.br.top5.noAnswer');
  }

  isStuck(q: BriefQuestion): boolean {
    return (q.mastery ?? 0) === 3 || !!q.gotStuck;
  }

  goQuestions(): void {
    this.jumpToQuestions.emit();
  }

  // ---------------------------------------------------------- 问面试官的问题
  readonly askList = signal<string[]>([]);
  askDraft = '';

  private seedAskList(e: BriefEntry): void {
    this.askList.set(parseStringList(e.questionsToAskJson));
  }

  addAsk(): void {
    const v = this.askDraft.trim();
    if (!v) return;
    this.askList.update((l) => [...l, v]);
    this.askDraft = '';
    this.saveAskList();
  }

  removeAsk(idx: number): void {
    this.askList.update((l) => l.filter((_, i) => i !== idx));
    this.saveAskList();
  }

  private saveAskList(): void {
    if (this.busy()) return;
    this.busy.set('ask');
    const json = JSON.stringify(this.askList());
    this.putEntry({ questionsToAskJson: json }).subscribe({
      next: () => {
        this.busy.set(null);
        this.entry.update((e) => (e ? { ...e, questionsToAskJson: json } : e));
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  // ---------------------------------------------------------- Checklist（A2）
  readonly actions = signal<InterviewAction[]>([]);
  readonly checklist = computed(() =>
    this.actions().filter((a) => a.detail === 'checklist'));
  checklistDraft = '';

  toggleChecklist(a: InterviewAction): void {
    this.api.patch<void>(`/api/interviews/${this.entryId()}/actions/${a.id}`, {
      isDone: !a.isDone,
    }).subscribe({
      next: () => this.loadActions(),
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 4000 }),
    });
  }

  addChecklistItem(title?: string): void {
    const v = (title ?? this.checklistDraft).trim();
    if (!v) return;
    this.api.post<{ id: string }>(`/api/interviews/${this.entryId()}/actions`, {
      title: v, detail: 'checklist',
    }).subscribe({
      next: () => {
        this.checklistDraft = '';
        this.loadActions();
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 4000 }),
    });
  }

  removeChecklistItem(a: InterviewAction): void {
    if (!confirm(a.title)) return;
    this.api.delete<void>(`/api/interviews/${this.entryId()}/actions/${a.id}`).subscribe({
      next: () => this.loadActions(),
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 4000 }),
    });
  }

  /** 为空时一键生成默认三项：着装 / 设备 / 感谢信草稿。 */
  generateDefaultChecklist(): void {
    const items = [
      this.t('pb.br.cl.d1'), this.t('pb.br.cl.d2'), this.t('pb.br.cl.d3'),
    ];
    this.busy.set('checklist');
    const create = (i: number): void => {
      if (i >= items.length) {
        this.busy.set(null);
        this.loadActions();
        return;
      }
      this.api.post<{ id: string }>(`/api/interviews/${this.entryId()}/actions`, {
        title: items[i], detail: 'checklist',
      }).subscribe({
        next: () => create(i + 1),
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        },
      });
    };
    create(0);
  }

  // ---------------------------------------------------------- 数据
  ngOnInit(): void {
    this.loadEntry();
    this.loadUpcoming();
    this.loadActions();
  }

  private loadEntry(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.get<BriefEntry>(`/api/interviews/${this.entryId()}`).subscribe({
      next: (e) => {
        this.entry.set(e);
        this.seedAskList(e);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      },
    });
  }

  private loadUpcoming(): void {
    this.api.get<UpcomingRoundItem[]>('/api/interviews/rounds/upcoming', { days: 60 })
      .pipe(catchError(() => of([] as UpcomingRoundItem[])))
      .subscribe((list) =>
        this.upcomingMine.set((list ?? []).filter((u) => u.entryId === this.entryId())));
  }

  private loadActions(): void {
    this.api.get<InterviewAction[]>(`/api/interviews/${this.entryId()}/actions`)
      .pipe(catchError(() => of([] as InterviewAction[])))
      .subscribe((list) => this.actions.set(list ?? []));
  }

  /**
   * PUT /api/interviews/{id} —— 必须带全字段回显。
   * 后端 UpdateBasicInfo 对 companyProfile/jdText 等直接赋值（null=清空），
   * 只有 questionsToAskJson 是 null=不动；所以这里全部回显当前值，只改 patch 里的字段。
   */
  private putEntry(patch: { companyProfile?: string | null; questionsToAskJson?: string | null }) {
    const e = this.entry();
    if (!e) throw new Error('entry not loaded');
    const body = {
      companyName: e.companyName,
      role: e.role,
      companyProfile: patch.companyProfile !== undefined ? patch.companyProfile : (e.companyProfile ?? null),
      jdText: e.jdText ?? null,
      jdSummary: e.jdSummary ?? null,
      roundNo: e.roundNo ?? 1,
      interviewDate: e.interviewDate ?? null,
      interviewFormat: e.interviewFormat ?? null,
      interviewers: e.interviewers ?? null,
      location: e.location ?? null,
      result: e.result ?? null,
      notes: e.notes ?? null,
      questionsToAskJson:
        patch.questionsToAskJson !== undefined ? patch.questionsToAskJson : (e.questionsToAskJson ?? null),
    };
    return this.api.put<void>(`/api/interviews/${this.entryId()}`, body);
  }
}
