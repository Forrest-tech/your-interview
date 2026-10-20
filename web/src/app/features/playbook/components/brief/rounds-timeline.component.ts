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
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import type { BriefAsset, BriefEntry, BriefRound, RoundEmailItem } from './brief.models';
import {
  applyParams, daysUntil, makeBriefT, nextRound, outcomeClass,
  parseJsonList, parsePastedEmail, renderMarkdown, todayStr,
} from './brief.helpers';
import { PrepPlanDialogComponent } from '../../prep-plan-dialog.component';
import { PasteTextDialogComponent } from '../../paste-text-dialog.component';

/** 备考方案 DTO —— 与后端 PrepPlanDto 对齐。 */
interface PrepPlan {
  assetId: string;
  entryId: string;
  roundId: string;
  roundOrder: number;
  markdown: string;
  model?: string;
  generatedAt: string;
}

/** 资产阶段。 */
type AssetPhase = 'Pre' | 'Post';

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
    MatCheckboxModule, MatDialogModule, MatProgressSpinnerModule, MatMenuModule,
  ],
  templateUrl: './rounds-timeline.component.html',
  styleUrl: './rounds-timeline.component.scss',
})
export class RoundsTimelineComponent implements OnInit {
  /** 强类型阶段常量(模板 @for 直接用字面量数组会被推断为 string) */
  readonly phases: ('Pre' | 'Post')[] = ['Pre', 'Post'];
  entryId = input.required<string>();
  @Output() jumpToBrief = new EventEmitter<void>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  t = makeBriefT(this.i18n);
  fmt = (key: string, params: Record<string, string | number>): string =>
    applyParams(this.t(key), params);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly rounds = signal<BriefRound[]>([]);
  /** 合成轮次:库里无 Round 实体但 entry 有 RoundNo 时展示用(跟 tab 计数一致)。 */
  readonly syntheticRound = signal<BriefRound | null>(null);

  readonly sortedRounds = computed(() => {
    const syn = this.syntheticRound();
    const list = syn ? [syn, ...this.rounds()] : [...this.rounds()];
    return list.sort((a, b) => a.order - b.order);
  });
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

  /** 合成轮次(老数据无 Round 实体时展示用):不可编辑/删除。 */
  isSynthetic(r: BriefRound): boolean { return r.id === '__synthetic_r1'; }

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
  newDate = '';
  newInterviewers = '';
  newFormat = '';
  newLocation = '';

  addRound(): void {
    if (this.busy()) return;
    this.busy.set('add');
    this.api.post<{ id: string }>(`/api/interviews/${this.entryId()}/rounds`, {
      stage: this.newStage,
      scheduledDate: this.newDate || null,
      interviewers: this.newInterviewers || null,
      format: this.newFormat || null,
      location: this.newLocation || null,
    }).subscribe({
      next: () => {
        this.busy.set(null);
        this.showAdd.set(false);
        // 重置表单
        this.newDate = ''; this.newInterviewers = ''; this.newFormat = ''; this.newLocation = '';
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
  emPaste = '';

  /** 粘贴完整邮件 → 自动填充 From/Date/Subject/正文（各字段仍可微调）。 */
  onEmailPaste(raw: string): void {
    this.emPaste = raw;
    const p = parsePastedEmail(raw);
    if (p.from) this.emFrom = p.from;
    if (p.subject) this.emSubject = p.subject;
    if (p.date) this.emDate = p.date;
    if (p.body) this.emSnippet = p.body;
  }

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
    this.emPaste = '';
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

  // ---------------- Transcript 折叠/编辑 ----------------
  readonly transcriptOpen = signal<string | null>(null);
  transcriptDraft: Record<string, string> = {};
  toggleTranscript(r: BriefRound): void {
    if (this.transcriptOpen() === r.id) {
      this.transcriptOpen.set(null);
    } else {
      this.transcriptDraft[r.id] = this.transcriptOf(r);
      this.transcriptOpen.set(r.id);
    }
  }
  saveTranscript(r: BriefRound): void {
    this.updateRoundDetails(r, { transcript: this.transcriptDraft[r.id] || null }, () => this.transcriptOpen.set(null));
  }

  /**
   * ★ R07 打通:把本轮转写稿送去分析。
   * 轮次 transcript → 条目级 Text 资产(文件名带轮次) → Worker 的 FetchTranscriptAsync 会拾取。
   */
  sendTranscriptToAnalysis(r: BriefRound): void {
    const text = (this.transcriptDraft[r.id] ?? this.transcriptOf(r)).trim();
    if (!text) {
      this.snack.open(this.t('pb.br.rounds.transcriptEmpty'), this.t('common.close'), { duration: 3000 });
      return;
    }
    const fileName = `round-${r.order}-transcript.txt`;
    this.api.post<{ id: string }>(
      `/api/interviews/${this.entryId()}/assets`,
      { textContent: text, fileName, roundId: r.id, phase: 'Post' }).subscribe({
      next: () => this.snack.open(
        this.t('pb.br.rounds.sentToAnalysis'), this.t('common.close'), { duration: 4000 }),
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 }),
    });
  }

  /**
   * ★ R07 打通:轮次录音文件上传。
   * 复用条目级 /assets multipart 上传 → 回写轮次 recordingUrl 为资产音频流地址。
   */
  uploadRoundRecording(r: BriefRound, ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.api.upload<{ id: string }>(`/api/interviews/${this.entryId()}/assets`, file,
      { roundId: r.id, phase: 'Post' }).subscribe({
      next: (res) => {
        input.value = '';
        const audioUrl = `/api/interviews/${this.entryId()}/assets/${res.id}/audio`;
        this.updateRoundDetails(r, { recordingUrl: audioUrl }, () => {
          this.recordingDraft[r.id] = audioUrl;
          this.snack.open(this.t('pb.br.rounds.recordingUploaded'), this.t('common.close'), { duration: 4000 });
        });
      },
      error: (e: Error) => {
        input.value = '';
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      },
    });
  }

  /** ★ 录音 */
  readonly recordingOpen = signal<string | null>(null);
  recordingDraft: Record<string, string> = {};
  toggleRecording(r: BriefRound): void {
    if (this.recordingOpen() === r.id) {
      this.recordingOpen.set(null);
    } else {
      this.recordingDraft[r.id] = r.recordingUrl ?? '';
      this.recordingOpen.set(r.id);
    }
  }
  saveRecording(r: BriefRound): void {
    this.updateRoundDetails(r, { recordingUrl: this.recordingDraft[r.id] || null }, () => this.recordingOpen.set(null));
  }

  /** ★ 面前问题 */
  readonly prepQOpen = signal<string | null>(null);
  prepQDraft: Record<string, string> = {};
  togglePrepQ(r: BriefRound): void {
    if (this.prepQOpen() === r.id) {
      this.prepQOpen.set(null);
    } else {
      this.prepQDraft[r.id] = this.prepQuestionsOf(r).join('\n');
      this.prepQOpen.set(r.id);
    }
  }
  prepQuestionsOf(r: BriefRound): string[] {
    const json = r.prepQuestionsJson ?? this.extras[r.id]?.prepQuestionsJson;
    if (!json) return [];
    try {
      const arr = JSON.parse(json);
      return Array.isArray(arr) ? arr : [];
    } catch { return json.split('\n').filter((s: string) => s.trim()); }
  }
  savePrepQ(r: BriefRound): void {
    const lines = (this.prepQDraft[r.id] || '').split('\n').map(s => s.trim()).filter(s => s);
    this.updateRoundDetails(r, { prepQuestionsJson: JSON.stringify(lines) }, () => this.prepQOpen.set(null));
  }

  /** ★ 准备清单 */
  readonly checklistOpen = signal<string | null>(null);
  checklistDraft: Record<string, string> = {};
  toggleChecklist(r: BriefRound): void {
    if (this.checklistOpen() === r.id) {
      this.checklistOpen.set(null);
    } else {
      this.checklistDraft[r.id] = this.checklistOf(r).map(i => `${i.done ? '[x]' : '[ ]'} ${i.text}`).join('\n');
      this.checklistOpen.set(r.id);
    }
  }
  checklistOf(r: BriefRound): { text: string; done: boolean }[] {
    const json = (r as any).checklistJson ?? this.extras[r.id]?.checklistJson;
    if (!json) return [];
    try {
      const arr = JSON.parse(json);
      if (Array.isArray(arr)) return arr;
    } catch { /* fallthrough */ }
    // 纯文本每行一项
    return json.split('\n').map((s: string) => s.trim()).filter((s: string) => s)
      .map((text: string) => ({ text, done: false }));
  }
  saveChecklist(r: BriefRound): void {
    const items = (this.checklistDraft[r.id] || '').split('\n').map(s => s.trim()).filter(s => s)
      .map(line => {
        const m = line.match(/^\[x\]\s*(.*)/i);
        if (m) return { text: m[1], done: true };
        const m2 = line.match(/^\[\s?\]\s*(.*)/);
        return { text: m2 ? m2[1] : line, done: false };
      });
    this.updateRoundDetails(r, { checklistJson: JSON.stringify(items) }, () => this.checklistOpen.set(null));
  }
  toggleCheckItem(r: BriefRound, idx: number): void {
    const items = this.checklistOf(r);
    items[idx].done = !items[idx].done;
    this.updateRoundDetails(r, { checklistJson: JSON.stringify(items) });
  }

  /** 通用:更新轮次详情字段。 */
  private updateRoundDetails(r: BriefRound, patch: Record<string, unknown>, onSuccess?: () => void): void {
    const ex = this.extras[r.id] ?? {};
    const body = {
      stage: r.stage,
      scheduledDate: r.scheduledDate ?? null,
      interviewers: r.interviewers ?? null,
      format: r.format ?? null,
      location: r.location ?? null,
      outcome: r.outcome ?? 'Pending',
      notes: (r as any).notes ?? null,
      feedback: (r as any).feedback ?? null,
      meetingLink: ex.meetingLink ?? (r as any).meetingLink ?? null,
      scheduledTime: ex.scheduledTime ?? (r as any).scheduledTime ?? null,
      prepQuestionsJson: ex.prepQuestionsJson ?? (r as any).prepQuestionsJson ?? null,
      emailsJson: ex.emailsJson ?? (r as any).emailsJson ?? null,
      transcript: ex.transcript ?? (r as any).transcript ?? null,
      recordingUrl: ex.recordingUrl ?? (r as any).recordingUrl ?? null,
      checklistJson: (ex as any).checklistJson ?? (r as any).checklistJson ?? null,
      ...patch,
    };
    this.api.put(`/api/interviews/${this.entryId()}/rounds/${r.id}`, body).subscribe({
      next: () => {
        this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2000 });
        this.load();
        onSuccess?.();
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 }),
    });
  }

  transcriptOf(r: BriefRound): string {
    return r.transcript ?? this.extras[r.id]?.transcript ?? '';
  }

  // ================= 轮次资产（2026-10-19：按 Round 管理） =================
  /** 全部资产（与 rounds 同一次 GET 下发）。 */
  readonly assets = signal<BriefAsset[]>([]);

  /** 某轮某阶段的资产。 */
  assetsForRound(roundId: string, phase: AssetPhase): BriefAsset[] {
    return this.assets()
      .filter((a) => a.roundId === roundId && (a.phase ?? '').toLowerCase() === phase.toLowerCase())
      .sort((a, b) => a.uploadedAt.localeCompare(b.uploadedAt));
  }

  /** 本轮是否已有备考方案资产(决定显示生成还是查看入口)。 */
  hasPrepDoc(roundId: string): boolean {
    return this.assetsForRound(roundId, 'Pre').some((a) => a.kind === 'PrepDoc');
  }

  /** 资产类型中文标签。 */
  kindLabel(kind: string): string {
    const map: Record<string, string> = {
      Audio: '录音', Transcript: '转写', Text: '文本', Notes: '纪要',
      PrepDoc: '备考文档', Email: '邮件', Feedback: '反馈', Other: '其他',
    };
    return map[kind] ?? kind;
  }

  /** 上传文件到指定轮次+阶段（context 创建：自动带 roundId/phase）。 */
  uploadRoundAsset(r: BriefRound, phase: AssetPhase, ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file || this.busy()) return;
    this.busy.set('asset-' + r.id + phase);
    this.api.upload<{ id: string }>(
      `/api/interviews/${this.entryId()}/assets`, file,
      { roundId: r.id, phase })
      .subscribe({
        next: () => {
          input.value = '';
          this.busy.set(null);
          this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
          this.load();
        },
        error: (e: Error) => {
          input.value = '';
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        },
      });
  }

  /** 粘贴文本到指定轮次+阶段：先选类型（默认文本），再进对话框。 */
  pasteRoundText(r: BriefRound, phase: AssetPhase): void {
    const kind = phase === 'Pre' ? 'Email' : 'Text';
    const ref = this.dialog.open(PasteTextDialogComponent, { width: '640px' });
    ref.afterClosed().subscribe((text: string | null) => {
      if (!text || this.busy()) return;
      this.busy.set('asset-' + r.id + phase);
      this.api.post<{ id: string }>(
        `/api/interviews/${this.entryId()}/assets`,
        { textContent: text, fileName: `round-${r.order}-${phase.toLowerCase()}.txt`, kind, roundId: r.id, phase })
        .subscribe({
          next: () => {
            this.busy.set(null);
            this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
            this.load();
          },
          error: (e: Error) => {
            this.busy.set(null);
            this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
          },
        });
    });
  }

  // ================= 备考方案（2026-10-19） =================
  /** roundId → 最新备考方案。 */
  readonly prepPlans = signal<Record<string, PrepPlan>>({});
  readonly prepOpen = signal<string | null>(null);
  readonly prepHtmlOpen = signal<string | null>(null);

  /** 某轮备考方案的渲染 HTML。 */
  prepPlanHtml(r: BriefRound): string {
    return renderMarkdown(this.prepPlans()[r.id]?.markdown ?? '');
  }

  /** 打开输入对话框 → 生成备考方案 → 存为轮次资产。 */
  openPrepDialog(r: BriefRound): void {
    const prevCount = this.sortedRounds().filter((x) => x.order < r.order).length;
    const ref = this.dialog.open(PrepPlanDialogComponent, {
      width: '640px',
      data: { roundOrder: r.order, jdHint: '', prevRoundsCount: prevCount },
    });
    ref.afterClosed().subscribe((input: {
      jdText: string; resumeText: string; experiences: string; requirements: string;
    } | null) => {
      if (!input || this.busy()) return;
      this.busy.set('prep-' + r.id);
      this.api.post<PrepPlan>(
        `/api/interviews/${this.entryId()}/rounds/${r.id}/prep-plan`, input)
        .subscribe({
          next: (p) => {
            this.busy.set(null);
            this.prepPlans.update((m) => ({ ...m, [r.id]: p }));
            this.prepOpen.set(r.id);
            this.prepHtmlOpen.set(r.id);
            this.load(); // 刷新资产列表（新 PrepDoc 资产）
            this.snack.open(this.t('pb.br.saved'), this.t('common.close'), { duration: 2500 });
          },
          error: (e: Error) => {
            this.busy.set(null);
            this.snack.open(e.message, this.t('common.close'), { duration: 6000 });
          },
        });
    });
  }

  /** 加载某轮已有的备考方案（在线阅读）。 */
  loadPrepPlan(r: BriefRound): void {
    if (this.prepPlans()[r.id]) {
      this.prepHtmlOpen.set(this.prepHtmlOpen() === r.id ? null : r.id);
      return;
    }
    this.api.get<PrepPlan>(
      `/api/interviews/${this.entryId()}/rounds/${r.id}/prep-plan`)
      .subscribe({
        next: (p) => {
          this.prepPlans.update((m) => ({ ...m, [r.id]: p }));
          this.prepHtmlOpen.set(r.id);
        },
        error: () => this.snack.open(this.t('pb.br.prep.empty'), this.t('common.close'), { duration: 3000 }),
      });
  }

  /** 导出 PDF：浏览器打印（与 Guidance 导出同模式）。 */
  exportPrepPdf(r: BriefRound): void {
    this.prepHtmlOpen.set(r.id);
    setTimeout(() => window.print(), 300);
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

  /** 2026-10-19:被 Assets 总览调用 —— 平滑滚动到指定轮次卡片。 */
  focusRound(roundId: string): void {
    setTimeout(() => {
      document.getElementById('round-card-' + roundId)
        ?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 150);
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
              checklistJson: (r as any).checklistJson ?? cur.checklistJson,
            };
          }
          this.rounds.set(rs);
          // 轮次资产（2026-10-19）：与 rounds 同一次 GET 下发
          this.assets.set(e?.assets ?? []);
          // 无真实轮次但 entry 有 RoundNo(老数据):合成一条展示用,跟 tab 计数的 displayRounds 保持一致
          if (rs.length === 0 && e?.roundNo) {
            this.syntheticRound.set({
              id: '__synthetic_r1',
              order: e.roundNo ?? 1,
              stage: '',
              outcome: e.result ?? '',
              scheduledDate: e.interviewDate ?? '',
              interviewers: e.interviewers ?? '',
            } as BriefRound);
          } else {
            this.syntheticRound.set(null);
          }
          this.loading.set(false);
        },
        error: (e: Error) => {
          this.error.set(e.message);
          this.loading.set(false);
        },
      });
  }
}
