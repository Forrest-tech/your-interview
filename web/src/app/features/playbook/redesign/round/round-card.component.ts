import {
  Component, EventEmitter, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewDetail, InterviewRound
} from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { TranscriptSegment, parseQuestionReview, safeJson } from '../v2.models';
import { PrepReportComponent } from './prep-report.component';
import { RoundRecordComponent } from './round-record.component';
import { QaImportComponent } from './qa-import.component';
import { MaterialsListComponent } from './materials-list.component';
import { ExportDialogComponent, ExportDialogData } from '../export/export-dialog.component';
import { ExportBlock, ExportDoc } from '../export/export-doc';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, formatLabel, makeV2Fmt, makeV2T } from '../v2.helpers';

type RoundTab = 'prep' | 'record' | 'qa' | 'materials';

/** 轮次阶段口径:与 rounds-timeline / Tracker smart-add / Tech Stack 共用的同一套取值。 */
const ROUND_STAGE_OPTIONS = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];

/**
 * Round 工作卡(2026-10-20 定稿):头行(第 N 轮 · 阶段 · 时间 · 面试官 · 编辑/删除)
 * + 结果行 + 内页签(按时序固定:备考报告 → 面试实录 → 问答入库(计数) → 材料(计数))。
 * 默认页签按阶段:未开始 → 备考报告;已结束 → 面试实录。
 */
@Component({
  selector: 'app-round-card',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule,
    PrepReportComponent, RoundRecordComponent, QaImportComponent, MaterialsListComponent
  ],
  template: `
    <section class="card" data-testid="round-card">
      <div class="rc-head">
        <h3>Round {{ round().order }} · {{ round().stage }}</h3>
        <span class="rc-meta">
          {{ round().scheduledDate ?? t('pb.v2.d.dateTbd') }}{{ round().scheduledTime ? ' ' + round().scheduledTime : '' }}
          @if (round().interviewers) { · {{ round().interviewers }} }
          @if (round().format) { · {{ formatLabel(t, round().format) }} }
        </span>
        <span class="spacer"></span>
        @if (tab() === 'record') {
          <button mat-stroked-button (click)="openRecordExport()">{{ t('pb.v2.r.exportRecord') }}</button>
        }
        <button type="button" class="mini-btn" (click)="toggleEdit()">{{ editing() ? t('pb.v2.d.collapse') : t('pb.v2.r.editRound') }}</button>
      </div>
      <div class="rc-outcome">
        {{ t('pb.v2.r.outcome') }}<b>{{ outcomeLabel(round().outcome) }}</b>
        @if (roundScore() != null) { · {{ t('pb.v2.r.overall') }} <b>{{ roundScore() }}</b> }
        @if (questionCount()) { · {{ fmt('pb.v2.r.questionsCount', { n: questionCount() }) }} }
        · {{ fmt('pb.v2.r.assetsCount', { n: roundAssets().length }) }}
        @if (round().meetingLink) { · <a [href]="round().meetingLink" target="_blank" rel="noopener">{{ t('pb.v2.r.meetingLink') }}</a> }
        @if (round().location) { · {{ round().location }} }
      </div>

      @if (round().scheduledDate) {
        <div class="cal-bar" data-testid="round-cal-bar">
          <mat-icon>event</mat-icon>
          <span>{{ t('pb.v2.r.calInApp') }}</span>
          <span class="cal-remind">{{ t('pb.v2.r.calReminders') }}</span>
          <span class="spacer"></span>
          <button type="button" class="mini-btn" (click)="toggleEdit()">{{ t('pb.v2.r.editRound') }}</button>
        </div>
      }

      @if (editing()) {
        <div class="edit-grid">
          <label>{{ t('pb.v2.r.fStage') }}
            <select class="fld" data-testid="round-stage-select" [ngModel]="eStage()" (ngModelChange)="eStage.set($event)">
              @for (s of stageOptions(); track s) { <option [value]="s">{{ stageLabel(s) }}</option> }
            </select>
          </label>
          <label>{{ t('pb.v2.r.fDate') }} <input class="fld" type="date" [ngModel]="eDate()" (ngModelChange)="eDate.set($event)"></label>
          <label>{{ t('pb.v2.r.fTime') }} <input class="fld" placeholder="11:00" [ngModel]="eTime()" (ngModelChange)="eTime.set($event)"></label>
          <label>{{ t('pb.v2.r.fInterviewers') }} <input class="fld" [ngModel]="eInterviewers()" (ngModelChange)="eInterviewers.set($event)"></label>
          <label>{{ t('pb.v2.r.fFormat') }} <input class="fld" [ngModel]="eFormat()" (ngModelChange)="eFormat.set($event)"></label>
          <label>{{ t('pb.v2.r.fLocation') }} <input class="fld" [ngModel]="eLocation()" (ngModelChange)="eLocation.set($event)"></label>
          <label>{{ t('pb.v2.r.meetingLink') }} <input class="fld" [ngModel]="eLink()" (ngModelChange)="eLink.set($event)"></label>
          <label>{{ t('pb.v2.r.fOutcome') }}
            <select class="fld" [ngModel]="eOutcome()" (ngModelChange)="eOutcome.set($event)">
              <option value="Pending">{{ t('pb.v2.r.ocPending') }}</option>
              <option value="Passed">{{ t('outcome.Passed') }}</option>
              <option value="Failed">{{ t('outcome.Failed') }}</option>
              <option value="Cancelled">{{ t('pb.v2.r.ocCancelled') }}</option>
            </select>
          </label>
          <label class="wide">{{ t('pb.v2.r.fFeedback') }} <input class="fld" [ngModel]="eFeedback()" (ngModelChange)="eFeedback.set($event)"></label>
        </div>
        <div class="row">
          <button mat-raised-button color="primary" (click)="saveEdit()">{{ t('pb.v2.r.saveRound') }}</button>
          <span class="spacer"></span>
          <button mat-button color="warn" (click)="deleteRound()">{{ t('pb.v2.r.deleteRound') }}</button>
        </div>
      }

      <div class="rtabs" role="tablist">
        @for (tb of tabs(); track tb.key) {
          <button type="button" role="tab" class="rtab" [class.on]="tab() === tb.key"
                  (click)="tab.set(tb.key)">
            {{ tb.label }}@if (tb.count() != null) { <span class="cnt">{{ tb.count() }}</span> }
          </button>
        }
      </div>

      @switch (tab()) {
        @case ('prep') {
          <app-prep-report [entry]="entry()" [round]="round()"
                           [guidanceAssets]="guidanceAssets()"
                           (changed)="changed.emit()" />
        }
        @case ('record') {
          <app-round-record [entry]="entry()" [round]="round()" (changed)="changed.emit()" />
        }
        @case ('qa') {
          <app-qa-import [entry]="entry()" [round]="round()" (changed)="changed.emit()" />
        }
        @case ('materials') {
          <div class="mat-head-row">
            <span class="mh-note">{{ t('pb.v2.r.matNote') }}</span>
            <span class="spacer"></span>
            <label class="mini-btn file-label">{{ t('pb.v2.r.uploadFile') }}
              <input type="file" hidden (change)="onUpload($event)">
            </label>
            <button mat-stroked-button (click)="addMaterialHere.emit(round().id)">{{ t('pb.v2.r.addMaterialHere') }}</button>
          </div>
          <app-materials-list [entryId]="entry().id" [assets]="roundAssets()"
                              [rounds]="entry().rounds ?? []" (changed)="changed.emit()" />
        }
      }
    </section>
  `,
  styles: [`
    .card { padding: 2px 2px 0; margin-top: 14px; }
    .rc-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .rc-head h3 { margin: 0; font-size: 16px; font-weight: 600; }
    .rc-meta { font-size: 13px; color: var(--yi-text-muted, #888); }
    .spacer { flex: 1; }
    .rc-outcome { font-size: 14px; color: var(--yi-text-muted, #777); margin-top: 5px; }
    .cal-bar { display: flex; align-items: center; gap: 8px; margin-top: 9px; font-size: 14px;
               border: 1px dashed var(--yi-border, #bbb); border-radius: 10px; padding: 7px 12px; }
    .cal-bar mat-icon { font-size: 17px; width: 17px; height: 17px; color: var(--yi-accent, #3f51b5); }
    .cal-bar .cal-remind { color: var(--yi-text-muted, #777); }
    .rc-outcome a { color: var(--yi-accent, #3f51b5); }
    .mini-btn { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
                border-radius: 999px; padding: 5px 12px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .edit-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                 gap: 10px 14px; margin: 12px 0; }
    .edit-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 13px;
                       color: var(--yi-text-muted, #777); }
    .edit-grid .wide { grid-column: span 2; }
    .fld { border: 1px solid var(--yi-border, #ccc); border-radius: 8px; padding: 7px 9px;
           font-size: 14px; background: transparent; color: inherit; }
    .row { display: flex; gap: 8px; align-items: center; }
    .rtabs { display: flex; gap: 4px; border-bottom: 1.5px solid var(--yi-border, #e0e0e0); margin: 14px 0 10px; flex-wrap: wrap; }
    .rtab { border: none; background: transparent; color: inherit; font-size: 14px; font-weight: 500; cursor: pointer;
            padding: 8px 13px; border-bottom: 2.5px solid transparent; margin-bottom: -1.5px; }
    .rtab.on { border-bottom-color: var(--yi-accent, #3f51b5); font-weight: 700; }
    .cnt { font-size: 12px; font-weight: 700; background: color-mix(in srgb, var(--yi-text, #222) 10%, transparent);
           border-radius: 999px; padding: 1px 7px; margin-left: 5px; }
    .mat-head-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .mh-note { font-size: 13px; color: var(--yi-text-muted, #888); }
    .file-label { display: inline-flex; align-items: center; }
  `]
})
export class RoundCardComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly addMaterialHere = new EventEmitter<string>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly tab = signal<RoundTab>('prep');
  readonly editing = signal(false);
  readonly eStage = signal('');
  readonly eDate = signal('');
  readonly eTime = signal('');
  readonly eInterviewers = signal('');
  readonly eFormat = signal('');
  readonly eLocation = signal('');
  readonly eLink = signal('');
  readonly eOutcome = signal('Pending');
  readonly eFeedback = signal('');

  private tabRoundId = '';

  readonly roundAssets = computed(() =>
    (this.entry().assets ?? []).filter(a => a.roundId === this.round().id));
  readonly guidanceAssets = computed(() =>
    this.roundAssets().filter(a => a.kind === 'Guidance'));
  readonly questionCount = computed(() =>
    (this.entry().questions ?? []).filter(q => q.roundId === this.round().id).length);
  readonly roundScore = computed(() =>
    safeJson<{ overall?: number | null }>(this.round().reviewJson)?.overall ?? null);

  readonly tabs = computed<{ key: RoundTab; label: string; count: () => number | null }[]>(() => [
    { key: 'prep', label: this.t('pb.v2.r.tabPrep'), count: () => null },
    { key: 'record', label: this.t('pb.v2.r.tabRecord'), count: () => null },
    { key: 'qa', label: this.t('pb.v2.r.tabQa'), count: () => this.questionCount() || null },
    { key: 'materials', label: this.t('pb.v2.r.tabMaterials'), count: () => this.roundAssets().length || null }
  ]);

  readonly formatLabel = formatLabel;

  /** Stage 下拉选项:标准口径在前;存量阶段值不在口径内时(如 Coding / Deep Dive)
   *  动态补为第一个选项并保持选中,显示不丢、保存前不被改写。 */
  readonly stageOptions = computed(() => {
    const cur = this.eStage().trim();
    return cur && !ROUND_STAGE_OPTIONS.includes(cur)
      ? [cur, ...ROUND_STAGE_OPTIONS]
      : [...ROUND_STAGE_OPTIONS];
  });

  stageLabel(s: string): string {
    const key = 'ts.stage.' + s;
    const v = this.t(key);
    return v === key ? s : v;
  }

  constructor() {
    // 轮次切换时按阶段选默认页签:未开始 → 备考;已结束 → 实录。
    effect(() => {
      const r = this.round();
      if (r.id !== this.tabRoundId) {
        this.tabRoundId = r.id;
        this.tab.set(r.outcome === 'Pending' ? 'prep' : 'record');
        this.editing.set(false);
      }
    }, { allowSignalWrites: true });
  }

  outcomeLabel(o: string): string {
    switch (o) {
      case 'Pending': return this.t('pb.v2.r.ocPending');
      case 'Passed': return this.t('outcome.Passed');
      case 'Failed': return this.t('outcome.Failed');
      case 'Cancelled': return this.t('pb.v2.r.ocCancelled');
      default: return o;
    }
  }

  toggleEdit(): void {
    const r = this.round();
    this.eStage.set(r.stage);
    this.eDate.set(r.scheduledDate ?? '');
    this.eTime.set(r.scheduledTime ?? '');
    this.eInterviewers.set(r.interviewers ?? '');
    this.eFormat.set(r.format ?? '');
    this.eLocation.set(r.location ?? '');
    this.eLink.set(r.meetingLink ?? '');
    this.eOutcome.set(r.outcome);
    this.eFeedback.set(r.feedback ?? '');
    this.editing.set(!this.editing());
  }

  async saveEdit(): Promise<void> {
    try {
      await this.api.updateRound(this.entry().id, this.round().id, {
        stage: this.eStage(),
        scheduledDate: this.eDate() || null,
        scheduledTime: this.eTime() || null,
        interviewers: this.eInterviewers() || null,
        format: this.eFormat() || null,
        location: this.eLocation() || null,
        meetingLink: this.eLink() || null,
        outcome: this.eOutcome(),
        feedback: this.eFeedback() || null
      }).toPromise();
      this.editing.set(false);
      this.snack.open(this.t('pb.v2.r.roundSaved'), this.t('common.close'), { duration: 2000 });
      this.changed.emit();
    } catch {
      this.snack.open(this.t('pb.v2.d.saveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  deleteRound(): void {
    this.api.deleteRound(this.entry().id, this.round().id).subscribe({
      next: () => { this.snack.open(this.t('pb.v2.r.roundDeleted'), this.t('common.close'), { duration: 2000 }); this.changed.emit(); },
      error: () => this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  onUpload(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0];
    if (!f) return;
    // phase 落值与展示推导同一规则:按上传日(即材料日期)vs 本轮日期 derivePhase。
    const phase = derivePhase(false, new Date().toISOString().slice(0, 10), this.round().scheduledDate);
    this.api.uploadFile(this.entry().id, f, { roundId: this.round().id, phase }).subscribe({
      next: () => {
        this.snack.open(this.t('pb.v2.r.uploaded'), this.t('common.close'), { duration: 2200 });
        input.value = '';
        this.changed.emit();
      },
      error: () => this.snack.open(this.t('pb.v2.r.uploadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  // ---------------- 导出实录 ----------------

  private exportSegments: TranscriptSegment[] = [];

  async openRecordExport(): Promise<void> {
    // 先把本轮转写段落取齐(对话原文是必选内容),再开导出对话框。
    const e = this.entry();
    const r = this.round();
    const segs: TranscriptSegment[] = [];
    for (const a of this.roundAssets().filter(x => x.kind === 'Transcript' || x.kind === 'Text')) {
      try {
        const t = await this.api.getTranscript(e.id, a.id).toPromise();
        const parsed = safeJson<TranscriptSegment[]>(t?.segmentsJson ?? null);
        if (parsed?.length) segs.push(...parsed);
      } catch { /* 单件失败不阻塞导出 */ }
    }
    this.exportSegments = segs;
    const date = new Date().toISOString().slice(0, 10);
    const data: ExportDialogData = {
      title: this.t('pb.v2.r.exportTitle'),
      subtitle: `${e.companyName} · Round ${r.order} ${r.stage}`,
      fileBase: `${e.companyName}-Round${r.order}-${this.t('pb.v2.r.fileSuffix')}-${date}`,
      storageKey: 'record',
      toggles: [
        { key: 'transcript', label: this.t('pb.v2.r.tgTranscript'), locked: true, defaultOn: true },
        { key: 'analysis', label: this.t('pb.v2.r.tgAnalysis'), defaultOn: true },
        { key: 'std', label: this.t('pb.v2.r.tgStd'), defaultOn: true },
        { key: 'summary', label: this.t('pb.v2.r.tgSummary'), defaultOn: true }
      ],
      buildDoc: sel => this.buildRecordDoc(sel)
    };
    this.dialog.open(ExportDialogComponent, { width: '560px', maxWidth: '94vw', data });
  }

  private buildRecordDoc(sel: Set<string>): ExportDoc {
    const e = this.entry();
    const r = this.round();
    const blocks: ExportBlock[] = [];
    const questions = (e.questions ?? [])
      .filter(q => q.roundId === r.id).sort((a, b) => a.sequence - b.sequence);
    const rv = safeJson<{
      overall?: number | null; passProbability?: number | null; passBasis?: string;
      strongest?: string; weakest?: string; summary?: string;
    }>(r.reviewJson);

    if (sel.has('summary') && rv) {
      const lines: string[] = [];
      if (rv.overall != null) lines.push(this.fmt('pb.v2.r.docOverall', { n: rv.overall }));
      if (rv.passProbability != null) lines.push(this.fmt('pb.v2.r.docPass', { n: rv.passProbability }));
      if (rv.passBasis) lines.push(this.fmt('pb.v2.r.docBasis', { v: rv.passBasis }));
      if (rv.strongest) lines.push(this.fmt('pb.v2.r.docStrongest', { v: rv.strongest }));
      if (rv.weakest) lines.push(this.fmt('pb.v2.r.docWeakest', { v: rv.weakest }));
      if (rv.summary) lines.push(rv.summary);
      if (lines.length) blocks.push({ kind: 'analysis', title: this.t('pb.v2.r.docOverview'), lines });
    }

    // 对话原文(必选,时间戳自带、恒导出):整段转写逐段输出。
    if (this.exportSegments.length) {
      blocks.push({ kind: 'heading', text: this.t('pb.v2.r.docTranscript') });
      for (const s of this.exportSegments) {
        blocks.push({
          kind: 'qa', speaker: s.speaker, text: s.text,
          timeLabel: s.start != null ? fmtClock(s.start) : undefined
        });
      }
    }

    for (const q of questions) {
      blocks.push({ kind: 'heading', text: `Q${q.sequence} ${q.questionText}` });
      if (q.askedBy) blocks.push({ kind: 'para', text: this.fmt('pb.v2.r.docAskedBy', { v: q.askedBy }) });
      blocks.push({
        kind: 'qa', speaker: q.askedBy ?? this.t('pb.v2.r.docInterviewer'), text: q.questionText,
        timeLabel: q.askedAtSeconds != null ? fmtClock(q.askedAtSeconds) : undefined
      });
      if (q.myAnswerText) {
        blocks.push({ kind: 'qa', speaker: this.t('pb.v2.r.docMe'), text: q.myAnswerText });
      }
      const review = parseQuestionReview(q.reviewJson);
      if (sel.has('analysis') && review) {
        const lines: string[] = [];
        if (review.overall != null) lines.push(this.fmt('pb.v2.r.docQScore', { n: review.overall }));
        const dims = review.dims ?? {};
        const dimKeys: [string, string][] = [
          ['relevance', 'pb.v2.dim.relevance'], ['depth', 'pb.v2.dim.depth'],
          ['expression', 'pb.v2.dim.expression'], ['pronunciation', 'pb.v2.dim.pronunciation']
        ];
        for (const [k, labelKey] of dimKeys) {
          const d = dims[k as keyof typeof dims];
          if (!d) continue;
          const label = this.t(labelKey);
          if (d.score == null) lines.push(this.fmt('pb.v2.dim.noScore', { label }));
          else lines.push(this.fmt('pb.v2.dim.scored', { label, score: d.score })
            + `${d.comment ? ' —— ' + d.comment : ''}${d.fix ? ' ' + this.fmt('pb.v2.r.docFix', { v: d.fix }) : ''}`);
        }
        if (lines.length) blocks.push({ kind: 'analysis', title: this.t('pb.v2.r.docQAnalysis'), lines });
      }
      if (sel.has('std') && review) {
        const sp = review.stdSpoken?.user || review.stdSpoken?.ai;
        const te = review.stdTechnical?.user || review.stdTechnical?.ai;
        if (sp) blocks.push({ kind: 'std', label: this.t('pb.v2.r.docStdSpoken'), text: sp });
        if (te) blocks.push({ kind: 'std', label: this.t('pb.v2.r.docStdTechnical'), text: te });
      }
    }
    return {
      title: this.fmt('pb.v2.r.docTitle', { company: e.companyName, n: r.order, stage: r.stage }),
      subtitle: `${e.role}${r.scheduledDate ? ' · ' + r.scheduledDate : ''}${r.interviewers ? ' · ' + r.interviewers : ''}`,
      blocks
    };
  }
}

function fmtClock(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
