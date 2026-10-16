import { Component, EventEmitter, OnInit, Output, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDividerModule } from '@angular/material/divider';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import type { BriefEntry, BriefRound, RoundEmailItem } from './brief.models';
import {
  applyParams, daysUntil, makeBriefT, nextRound, outcomeClass,
  parseJsonList, todayStr,
} from './brief.helpers';

/** 轮次行内编辑表单 —— 字段与后端 UpdateRoundBody 对齐。 */
interface RoundEditForm {
  stage: string;
  scheduledDate: string;
  scheduledTime: string;
  interviewers: string;
  format: string;
  location: string;
  outcome: string;
  meetingLink: string;
  notes: string;
  feedback: string;
}

/**
 * F4 轮次垂直时间线（selector: app-rounds-timeline）。
 *
 *  - 顶部“下一轮”卡片：最近未过期轮次的倒计时 + 跳 Guidance（jumpToBrief 输出）。
 *  - 垂直时间线：每轮 = 日期 + Stage + 面试官 + 结果徽章 + Feedback + Notes
 *    + 邮件时间线（emailsJson 按日期列 from/subject/snippet；增删后调 PUT emails 整体替换）
 *    + Transcript 折叠。
 *  - Add / Edit round：自己实现表单，调现有 round API（POST body {stage}；PUT 全字段）。
 *
 * ★ 后端缺口（2026-10-16）：GET 详情的 RoundDto 暂不含 meetingLink/scheduledTime/
 *   emailsJson/transcript。读侧按“字段可能缺席”防御；写侧（PUT emails / PUT round）
 *   均可用。extrasCache 记住本会话内写过的 emailsJson 等，轮次 PUT 时回显，避免清空。
 */
@Component({
  selector: 'app-rounds-timeline',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatButtonModule, MatIconModule, MatChipsModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
    MatProgressBarModule, MatSnackBarModule, MatDividerModule,
  ],
  templateUrl: './rounds-timeline.component.html',
  styleUrl: './rounds-timeline.component.scss',
})
export class RoundsTimelineComponent implements OnInit {
  entryId = input.required<string>();
  @Output() jumpToBrief = new EventEmitter<void>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  t = makeBriefT(this.i18n);
  fmt = (key: string, params: Record<string, string | number>): string =>
    applyParams(this.t(key), params);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly rounds = signal<BriefRound[]>([]);

  readonly sortedRounds = computed(() =>
    [...this.rounds()].sort((a, b) => a.order - b.order));
  readonly nextRd = computed(() => nextRound(this.rounds()));

  readonly stages = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];
  readonly outcomes = ['Pending', 'Passed', 'Rejected', 'Ghosted', 'Cancelled', 'NoShow'];

  /** 本会话内写过的轮次扩展字段（GET 暂不下发，PUT round 时回显防清空）。 */
  private extras: Record<string, Partial<BriefRound>> = {};
  /** 邮件列表覆盖（roundId → 最新列表），写成功后立即生效。 */
  private readonly emailOverrides = signal<Record<string, RoundEmailItem[]>>({});

  // ---------------- 行内编辑 ----------------
  editDrafts: Record<string, RoundEditForm> = {};
  isEditing(r: BriefRound): boolean { return r.id in this.editDrafts; }

  openEdit(r: BriefRound): void {
    const ex = this.extras[r.id] ?? {};
    this.editDrafts[r.id] = {
      stage: r.stage ?? 'Technical',
      scheduledDate: r.scheduledDate ?? '',
      scheduledTime: r.scheduledTime ?? ex.scheduledTime ?? '',
      interviewers: r.interviewers ?? '',
      format: r.format ?? '',
      location: r.location ?? '',
      outcome: r.outcome ?? 'Pending',
      meetingLink: r.meetingLink ?? ex.meetingLink ?? '',
      notes: r.notes ?? '',
      feedback: r.feedback ?? '',
    };
  }

  cancelEdit(r: BriefRound): void { delete this.editDrafts[r.id]; }

  saveEdit(r: BriefRound): void {
    const d = this.editDrafts[r.id];
    if (!d || this.busy()) return;
    const ex = this.extras[r.id] ?? {};
    this.busy.set('round-' + r.id);
    const body = {
      stage: d.stage,
      scheduledDate: d.scheduledDate || null,
      interviewers: d.interviewers || null,
      format: d.format || null,
      location: d.location || null,
      outcome: d.outcome,
      notes: d.notes || null,
      feedback: d.feedback || null,
      meetingLink: d.meetingLink || ex.meetingLink || r.meetingLink || null,
      scheduledTime: d.scheduledTime || ex.scheduledTime || r.scheduledTime || null,
      prepQuestionsJson: ex.prepQuestionsJson ?? r.prepQuestionsJson ?? null,
      emailsJson: ex.emailsJson ?? r.emailsJson ?? null,
      transcript: ex.transcript ?? r.transcript ?? null,
      recordingUrl: ex.recordingUrl ?? r.recordingUrl ?? null,
    };
    this.api.put<void>(`/api/interviews/${this.entryId()}/rounds/${r.id}`, body).subscribe({
      next: () => {
        this.busy.set(null);
        delete this.editDrafts[r.id];
        this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
        this.load();
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  // ---------------- 新增 / 删除轮次 ----------------
  readonly showAdd = signal(false);
  newStage = 'Technical';

  addRound(): void {
    if (this.busy()) return;
    this.busy.set('add');
    this.api.post<{ id: string }>(`/api/interviews/${this.entryId()}/rounds`, {
      stage: this.newStage,
    }).subscribe({
      next: () => {
        this.busy.set(null);
        this.showAdd.set(false);
        this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
        this.load();
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  removeRound(r: BriefRound): void {
    if (!confirm(this.t('pb.br.rounds.confirmDelete'))) return;
    this.api.delete<void>(`/api/interviews/${this.entryId()}/rounds/${r.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('pb.br.rounds.deleted'), this.t('common.close'), { duration: 2500 });
        this.load();
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 }),
    });
  }

  // ---------------- 邮件时间线（A7） ----------------
  readonly emailEditorId = signal<string | null>(null);
  emFrom = '';
  emSubject = '';
  emDate = '';
  emSnippet = '';

  emailsOf(r: BriefRound): RoundEmailItem[] {
    const override = this.emailOverrides()[r.id];
    const list = override ?? parseJsonList<RoundEmailItem>(r.emailsJson);
    return [...list].sort((a, b) => (b.date ?? '').localeCompare(a.date ?? ''));
  }

  openEmailEditor(r: BriefRound): void {
    this.emailEditorId.set(r.id);
    this.emFrom = '';
    this.emSubject = '';
    this.emDate = todayStr();
    this.emSnippet = '';
  }

  closeEmailEditor(): void { this.emailEditorId.set(null); }

  addEmail(r: BriefRound): void {
    if (!this.emSubject.trim() && !this.emFrom.trim()) return;
    const list = [...this.emailsOf(r)];
    list.push({
      from: this.emFrom.trim(),
      subject: this.emSubject.trim(),
      date: this.emDate || todayStr(),
      snippet: this.emSnippet.trim(),
    });
    this.saveEmails(r, list);
  }

  removeEmail(r: BriefRound, idx: number): void {
    const list = this.emailsOf(r).filter((_, i) => i !== idx);
    this.saveEmails(r, list);
  }

  private saveEmails(r: BriefRound, list: RoundEmailItem[]): void {
    this.busy.set('emails-' + r.id);
    this.api.put<void>(`/api/interviews/${this.entryId()}/rounds/${r.id}/emails`, {
      emails: list,
    }).subscribe({
      next: () => {
        this.busy.set(null);
        this.emailOverrides.update((m) => ({ ...m, [r.id]: list }));
        this.extras[r.id] = { ...(this.extras[r.id] ?? {}), emailsJson: JSON.stringify(list) };
        this.emailEditorId.set(null);
        this.snack.open(this.t('pb.br.rounds.emailSaved'), this.t('common.close'), { duration: 2500 });
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  // ---------------- Transcript 折叠 ----------------
  readonly transcriptOpen = signal<string | null>(null);
  toggleTranscript(r: BriefRound): void {
    this.transcriptOpen.set(this.transcriptOpen() === r.id ? null : r.id);
  }

  transcriptOf(r: BriefRound): string {
    return r.transcript ?? this.extras[r.id]?.transcript ?? '';
  }

  // ---------------- 展示辅助 ----------------
  outcomeClass = outcomeClass;

  outcomeLabel(o: string): string {
    const key = 'pb.roundOutcome.' + o;
    const v = this.i18n.t(key);
    return v !== key ? v : o;
  }

  stageLabel(stage: string): string {
    const key = 'pb.br.stage.' + stage;
    const v = this.t(key);
    return v !== key ? v : stage;
  }

  daysLabel(r: BriefRound): string {
    if (!r.scheduledDate) return '';
    const d = daysUntil(r.scheduledDate);
    if (d < 0) return '';
    if (d === 0) return this.t('pb.br.countdown.today');
    if (d === 1) return this.t('pb.br.countdown.tomorrow');
    return this.fmt('pb.br.countdown.daysLeft', { n: d });
  }

  goBrief(): void {
    this.jumpToBrief.emit();
  }

  // ---------------- 数据 ----------------
  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.get<BriefEntry>(`/api/interviews/${this.entryId()}`)
      .subscribe({
        next: (e) => {
          const rs = e?.rounds ?? [];
          // 把 GET 下发的扩展字段（若后端已补）记入 extras，供 PUT 回显
          for (const r of rs) {
            const cur = this.extras[r.id] ?? {};
            this.extras[r.id] = {
              meetingLink: r.meetingLink ?? cur.meetingLink,
              scheduledTime: r.scheduledTime ?? cur.scheduledTime,
              emailsJson: r.emailsJson ?? cur.emailsJson,
              transcript: r.transcript ?? cur.transcript,
              prepQuestionsJson: r.prepQuestionsJson ?? cur.prepQuestionsJson,
              recordingUrl: r.recordingUrl ?? cur.recordingUrl,
            };
          }
          this.rounds.set(rs);
          this.loading.set(false);
        },
        error: (e: Error) => {
          this.error.set(e.message);
          this.loading.set(false);
        },
      });
  }
}
