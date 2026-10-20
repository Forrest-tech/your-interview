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
import { ExportBlock, ExportDoc, downloadBlob } from '../export/export-doc';

type RoundTab = 'prep' | 'record' | 'qa' | 'materials';

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
          {{ round().scheduledDate ?? '日期待定' }}{{ round().scheduledTime ? ' ' + round().scheduledTime : '' }}
          @if (round().interviewers) { · {{ round().interviewers }} }
          @if (round().format) { · {{ round().format }} }
        </span>
        <span class="spacer"></span>
        @if (tab() === 'record') {
          <button mat-stroked-button (click)="openRecordExport()">导出实录</button>
        }
        @if (round().scheduledDate) {
          <button type="button" class="mini-btn" (click)="downloadIcs()">导出 .ics</button>
        }
        <button type="button" class="mini-btn" (click)="toggleEdit()">{{ editing() ? '收起' : '编辑本轮' }}</button>
      </div>
      <div class="rc-outcome">
        结果:<b>{{ outcomeLabel(round().outcome) }}</b>
        @if (roundScore() != null) { · 综合分 <b>{{ roundScore() }}</b> }
        @if (questionCount()) { · {{ questionCount() }} 道题 }
        · {{ roundAssets().length }} 件材料
        @if (round().meetingLink) { · <a [href]="round().meetingLink" target="_blank" rel="noopener">会议链接</a> }
        @if (round().location) { · {{ round().location }} }
      </div>

      @if (editing()) {
        <div class="edit-grid">
          <label>阶段 <input class="fld" [ngModel]="eStage()" (ngModelChange)="eStage.set($event)"></label>
          <label>日期 <input class="fld" type="date" [ngModel]="eDate()" (ngModelChange)="eDate.set($event)"></label>
          <label>时间 <input class="fld" placeholder="11:00" [ngModel]="eTime()" (ngModelChange)="eTime.set($event)"></label>
          <label>面试官 <input class="fld" [ngModel]="eInterviewers()" (ngModelChange)="eInterviewers.set($event)"></label>
          <label>形式 <input class="fld" [ngModel]="eFormat()" (ngModelChange)="eFormat.set($event)"></label>
          <label>地点 <input class="fld" [ngModel]="eLocation()" (ngModelChange)="eLocation.set($event)"></label>
          <label>会议链接 <input class="fld" [ngModel]="eLink()" (ngModelChange)="eLink.set($event)"></label>
          <label>结果
            <select class="fld" [ngModel]="eOutcome()" (ngModelChange)="eOutcome.set($event)">
              <option value="Pending">待进行</option>
              <option value="Passed">通过</option>
              <option value="Failed">未通过</option>
              <option value="Cancelled">取消</option>
            </select>
          </label>
          <label class="wide">反馈 / 备注 <input class="fld" [ngModel]="eFeedback()" (ngModelChange)="eFeedback.set($event)"></label>
        </div>
        <div class="row">
          <button mat-raised-button color="primary" (click)="saveEdit()">保存本轮</button>
          <span class="spacer"></span>
          <button mat-button color="warn" (click)="deleteRound()">删除本轮</button>
        </div>
      }

      <div class="rtabs" role="tablist">
        @for (tb of tabs; track tb.key) {
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
            <span class="mh-note">本轮全部原始件:邮件 / 录音 / 转写 / 备注 / 指导材料都在这里,旧材料按面试前/后归置。</span>
            <span class="spacer"></span>
            <label class="mini-btn file-label">上传文件
              <input type="file" hidden (change)="onUpload($event)">
            </label>
            <button mat-stroked-button (click)="addMaterialHere.emit(round().id)">＋ 添加材料到本轮</button>
          </div>
          <app-materials-list [entryId]="entry().id" [assets]="roundAssets()"
                              [rounds]="entry().rounds ?? []" (changed)="changed.emit()" />
        }
      }
    </section>
  `,
  styles: [`
    .card { border: 1px solid var(--yi-border, #ddd); border-radius: 16px; padding: 16px 18px; margin-top: 14px; }
    .rc-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .rc-head h3 { margin: 0; font-size: 17px; }
    .rc-meta { font-size: 12.5px; color: var(--yi-text-muted, #888); }
    .spacer { flex: 1; }
    .rc-outcome { font-size: 13px; color: var(--yi-text-muted, #777); margin-top: 5px; }
    .rc-outcome a { color: var(--yi-accent, #3f51b5); }
    .mini-btn { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
                border-radius: 999px; padding: 5px 12px; font-size: 12px; cursor: pointer; }
    .edit-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                 gap: 10px 14px; margin: 12px 0; }
    .edit-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 12px;
                       color: var(--yi-text-muted, #777); }
    .edit-grid .wide { grid-column: span 2; }
    .fld { border: 1px solid var(--yi-border, #ccc); border-radius: 8px; padding: 7px 9px;
           font-size: 13px; background: transparent; color: inherit; }
    .row { display: flex; gap: 8px; align-items: center; }
    .rtabs { display: flex; gap: 4px; border-bottom: 1.5px solid var(--yi-border, #e0e0e0); margin: 14px 0 10px; flex-wrap: wrap; }
    .rtab { border: none; background: transparent; color: inherit; font-size: 13.5px; cursor: pointer;
            padding: 8px 13px; border-bottom: 2.5px solid transparent; margin-bottom: -1.5px; }
    .rtab.on { border-bottom-color: var(--yi-accent, #3f51b5); font-weight: 700; }
    .cnt { font-size: 11px; font-weight: 700; background: color-mix(in srgb, var(--yi-text, #222) 10%, transparent);
           border-radius: 999px; padding: 1px 7px; margin-left: 5px; }
    .mat-head-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .mh-note { font-size: 12.5px; color: var(--yi-text-muted, #888); }
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

  readonly tabs: { key: RoundTab; label: string; count: () => number | null }[] = [
    { key: 'prep', label: '备考报告', count: () => null },
    { key: 'record', label: '面试实录', count: () => null },
    { key: 'qa', label: '问答入库', count: () => this.questionCount() || null },
    { key: 'materials', label: '材料', count: () => this.roundAssets().length || null }
  ];

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
    return ({ Pending: '待进行', Passed: '通过', Failed: '未通过', Cancelled: '已取消' } as
      Record<string, string>)[o] ?? o;
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
      this.snack.open('本轮已保存', '关闭', { duration: 2000 });
      this.changed.emit();
    } catch {
      this.snack.open('保存失败', '关闭', { duration: 3000 });
    }
  }

  deleteRound(): void {
    this.api.deleteRound(this.entry().id, this.round().id).subscribe({
      next: () => { this.snack.open('本轮已删除', '关闭', { duration: 2000 }); this.changed.emit(); },
      error: () => this.snack.open('删除失败', '关闭', { duration: 3000 })
    });
  }

  onUpload(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0];
    if (!f) return;
    this.api.uploadFile(this.entry().id, f, { roundId: this.round().id, phase: 'Post' }).subscribe({
      next: () => {
        this.snack.open('文件已上传到本轮材料', '关闭', { duration: 2200 });
        input.value = '';
        this.changed.emit();
      },
      error: () => this.snack.open('上传失败', '关闭', { duration: 3000 })
    });
  }

  downloadIcs(): void {
    this.api.downloadIcs(this.entry().id, this.round().id).subscribe({
      next: blob => downloadBlob(blob,
        `${this.entry().companyName}-Round${this.round().order}.ics`),
      error: () => this.snack.open('日历文件生成失败', '关闭', { duration: 3000 })
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
      title: '导出本轮实录',
      subtitle: `${e.companyName} · Round ${r.order} ${r.stage}`,
      fileBase: `${e.companyName}-Round${r.order}-实录-${date}`,
      storageKey: 'record',
      toggles: [
        { key: 'transcript', label: '对话原文', locked: true, defaultOn: true },
        { key: 'analysis', label: '逐题分析', defaultOn: true },
        { key: 'std', label: '标准回答(我的编辑版)', defaultOn: true },
        { key: 'summary', label: '总评分与通过概率', defaultOn: true }
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
      if (rv.overall != null) lines.push(`综合分:${rv.overall}`);
      if (rv.passProbability != null) lines.push(`通过概率 ≈ ${rv.passProbability}%(AI 估计)`);
      if (rv.passBasis) lines.push(`估计依据:${rv.passBasis}`);
      if (rv.strongest) lines.push(`最强项:${rv.strongest}`);
      if (rv.weakest) lines.push(`最大短板:${rv.weakest}`);
      if (rv.summary) lines.push(rv.summary);
      if (lines.length) blocks.push({ kind: 'analysis', title: '整轮总览', lines });
    }

    // 对话原文(必选,时间戳自带、恒导出):整段转写逐段输出。
    if (this.exportSegments.length) {
      blocks.push({ kind: 'heading', text: '对话原文' });
      for (const s of this.exportSegments) {
        blocks.push({
          kind: 'qa', speaker: s.speaker, text: s.text,
          timeLabel: s.start != null ? fmtClock(s.start) : undefined
        });
      }
    }

    for (const q of questions) {
      blocks.push({ kind: 'heading', text: `Q${q.sequence} ${q.questionText}` });
      if (q.askedBy) blocks.push({ kind: 'para', text: `提问人:${q.askedBy}` });
      blocks.push({
        kind: 'qa', speaker: q.askedBy ?? '面试官', text: q.questionText,
        timeLabel: q.askedAtSeconds != null ? fmtClock(q.askedAtSeconds) : undefined
      });
      if (q.myAnswerText) {
        blocks.push({ kind: 'qa', speaker: '我', text: q.myAnswerText });
      }
      const review = parseQuestionReview(q.reviewJson);
      if (sel.has('analysis') && review) {
        const lines: string[] = [];
        if (review.overall != null) lines.push(`本题分:${review.overall}`);
        const dims = review.dims ?? {};
        const dimNames: [string, string][] = [
          ['relevance', '切题性'], ['depth', '技术深度'],
          ['expression', '表达'], ['pronunciation', '发音']
        ];
        for (const [k, label] of dimNames) {
          const d = dims[k as keyof typeof dims];
          if (!d) continue;
          if (d.score == null) lines.push(`${label}:不评分(无录音)`);
          else lines.push(`${label}:${d.score} 分${d.comment ? ' —— ' + d.comment : ''}${d.fix ? ' 改法:' + d.fix : ''}`);
        }
        if (lines.length) blocks.push({ kind: 'analysis', title: '逐题分析', lines });
      }
      if (sel.has('std') && review) {
        const sp = review.stdSpoken?.user || review.stdSpoken?.ai;
        const te = review.stdTechnical?.user || review.stdTechnical?.ai;
        if (sp) blocks.push({ kind: 'std', label: '标准回答 · 口语版', text: sp });
        if (te) blocks.push({ kind: 'std', label: '标准回答 · 技术版', text: te });
      }
    }
    return {
      title: `${e.companyName} · Round ${r.order} ${r.stage} 面试实录`,
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
