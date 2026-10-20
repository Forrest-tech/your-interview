import {
  Component, EventEmitter, Output, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewAsset, InterviewRound
} from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, TranscriptSegment, safeJson } from '../v2.models';

/**
 * 材料列表(2026-10-20 定稿):本轮(或条目级)的全部原始件。
 * 每件三种视图 —— 解析字段 / 原文 / 截图(有才显示);每件带归属下拉,可在
 * 轮次与沟通记录之间移动。旧 Before/After 阶段材料也并入这里(按 Round/Phase 展示)。
 */
@Component({
  selector: 'app-materials-list',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (!assets().length) {
      <p class="empty">这里还没有材料。用上方「＋ 添加材料」粘贴,或直接上传文件。</p>
    }
    @for (a of assets(); track a.id) {
      <article class="mat" data-testid="material-item">
        <div class="mat-head">
          <span class="kind-chip">{{ kindLabel(a) }}</span>
          <span class="mat-name">{{ a.fileName }}</span>
          @if (a.phase) { <span class="phase">{{ a.phase === 'Pre' ? '面试前' : '面试后' }}</span> }
          <span class="mat-date">{{ displayDate(a) }}</span>
          <span class="spacer"></span>
          <label class="assign">归属
            <select class="fld sm" [ngModel]="a.roundId ?? ''" (ngModelChange)="reassign(a, $event)">
              <option value="">沟通记录(条目级)</option>
              @for (r of rounds(); track r.id) {
                <option [value]="r.id">Round {{ r.order }} · {{ r.stage }}</option>
              }
            </select>
          </label>
          <button type="button" class="icon-btn danger" (click)="remove(a)" title="删除">
            <mat-icon>delete_outline</mat-icon>
          </button>
        </div>

        @if (metaOf(a); as m) {
          @if (m.category || m.interviewAt || m.from) {
            <div class="parsed">
              @if (m.category) { <span>分类:<b>{{ categoryLabel(m.category) }}</b></span> }
              @if (m.from) { <span>发件人:<b>{{ m.from }}</b></span> }
              @if (m.date) { <span>日期:<b>{{ m.date }}</b></span> }
              @if (m.interviewAt) { <span>面试时间:<b>{{ m.interviewAt }}</b></span> }
              @if (m.interviewers?.length) { <span>面试官:<b>{{ m.interviewers!.join('、') }}</b></span> }
              @if (m.format) { <span>形式:<b>{{ m.format }}</b></span> }
              @if (m.summary) { <span class="sum">摘要:{{ m.summary }}</span> }
            </div>
          }
        }

        <div class="mat-actions">
          @if (a.kind === 'Audio') {
            <button type="button" class="mini-btn" (click)="loadFile(a)">▶ 播放录音</button>
          }
          @if (hasBody(a)) {
            <button type="button" class="mini-btn" (click)="toggleBody(a)">
              {{ expandedId() === a.id ? '收起原文' : '查看原文' }}
            </button>
          }
          @if (metaOf(a)?.screenshotAssetId) {
            <button type="button" class="mini-btn" (click)="loadShot(a)">查看截图</button>
          }
          @if (a.fileExists === false) { <span class="lost">⚠ 文件已丢失(巡检标记)</span> }
        </div>

        @if (fileUrl(a.id); as url) {
          @if (a.kind === 'Audio') { <audio controls [src]="url" class="player"></audio> }
          @else { <img [src]="url" class="shot-img" alt="材料文件"> }
        }
        @if (shotUrl() && shotFor() === a.id) {
          <img [src]="shotUrl()" class="shot-img" alt="粘贴的截图原图">
        }
        @if (expandedId() === a.id) {
          <div class="body-box">
            @if (bodySegs().length) {
              @for (s of bodySegs(); track $index) {
                <p class="seg-line"><b>{{ s.speaker }}:</b>{{ s.text }}</p>
              }
            } @else {
              <pre class="body-text">{{ bodyText() }}</pre>
            }
          </div>
        }
      </article>
    }
  `,
  styles: [`
    .empty { font-size: 13px; color: var(--yi-text-muted, #888); padding: 10px 0; }
    .mat { border: 1px solid var(--yi-border, #ddd); border-radius: 12px; padding: 12px 14px; margin: 10px 0; }
    .mat-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .kind-chip { font-size: 11px; font-weight: 700; border: 1px solid var(--yi-border, #ccc);
                 border-radius: 999px; padding: 2px 9px; }
    .mat-name { font-size: 13.5px; font-weight: 600; }
    .phase { font-size: 11px; color: var(--yi-text-muted, #888); }
    .mat-date { font-size: 11.5px; color: var(--yi-text-muted, #888); }
    .spacer { flex: 1; }
    .assign { font-size: 12px; color: var(--yi-text-muted, #777); display: inline-flex; gap: 6px; align-items: center; }
    .fld.sm { border: 1px solid var(--yi-border, #ccc); border-radius: 7px; padding: 4px 7px;
              font-size: 12px; background: transparent; color: inherit; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #b3261e; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .parsed { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 12.5px;
              color: var(--yi-text-muted, #666); margin-top: 8px; }
    .parsed b { color: var(--yi-text, #222); font-weight: 600; }
    .parsed .sum { flex-basis: 100%; }
    .mat-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; align-items: center; }
    .mini-btn { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 12px; cursor: pointer; }
    .lost { font-size: 12px; color: #b3261e; }
    .player { width: 100%; height: 36px; margin-top: 8px; }
    .shot-img { max-width: 100%; border-radius: 8px; margin-top: 8px; border: 1px solid var(--yi-border, #ddd); }
    .body-box { margin-top: 9px; border-top: 1px dashed var(--yi-border, #ddd); padding-top: 9px; }
    .body-text { white-space: pre-wrap; font-size: 12.5px; line-height: 1.6; margin: 0;
                 font-family: inherit; max-height: 380px; overflow: auto; }
    .seg-line { font-size: 12.5px; line-height: 1.6; margin: 3px 0; }
  `]
})
export class MaterialsListComponent {
  readonly entryId = input.required<string>();
  readonly assets = input.required<InterviewAsset[]>();
  readonly rounds = input<InterviewRound[]>([]);
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);

  readonly expandedId = signal<string | null>(null);
  readonly bodyText = signal('');
  readonly bodySegs = signal<TranscriptSegment[]>([]);
  private readonly fileUrls = signal(new Map<string, string>());
  readonly shotUrl = signal<string | null>(null);
  readonly shotFor = signal<string | null>(null);

  metaOf(a: InterviewAsset): AssetMeta | null {
    return safeJson<AssetMeta>(a.metaJson);
  }

  kindLabel(a: InterviewAsset): string {
    const m = this.metaOf(a);
    if (m?.channel === 'email' || (a.kind === 'Text' && m?.category)) return '邮件';
    switch (a.kind) {
      case 'Audio': return '录音';
      case 'Transcript': return '转写';
      case 'Notes': return '备注';
      case 'Text': return '文本';
      case 'Guidance': return '指导材料';
      default: return a.kind;
    }
  }

  categoryLabel(c: string): string {
    return ({
      invitation: '面试邀约', reschedule: '改期', rejection: '拒绝',
      followup: '跟进', other: '其它'
    } as Record<string, string>)[c] ?? c;
  }

  displayDate(a: InterviewAsset): string {
    const m = this.metaOf(a);
    return (m?.date ?? a.uploadedAt ?? '').slice(0, 10);
  }

  hasBody(a: InterviewAsset): boolean {
    return a.kind === 'Transcript' || a.kind === 'Text' || a.kind === 'Notes' || !!a.hasTranscript;
  }

  fileUrl(id: string): string | null {
    return this.fileUrls().get(id) ?? null;
  }

  loadFile(a: InterviewAsset): void {
    if (this.fileUrls().has(a.id)) return;
    this.api.audioBlob(this.entryId(), a.id).subscribe({
      next: blob => {
        const next = new Map(this.fileUrls());
        next.set(a.id, URL.createObjectURL(blob));
        this.fileUrls.set(next);
      },
      error: () => this.snack.open('文件加载失败(可能已丢失)', '关闭', { duration: 3000 })
    });
  }

  loadShot(a: InterviewAsset): void {
    const shotId = this.metaOf(a)?.screenshotAssetId;
    if (!shotId) return;
    if (this.shotFor() === a.id && this.shotUrl()) { this.shotUrl.set(null); return; }
    this.api.audioBlob(this.entryId(), shotId).subscribe({
      next: blob => { this.shotUrl.set(URL.createObjectURL(blob)); this.shotFor.set(a.id); },
      error: () => this.snack.open('截图加载失败', '关闭', { duration: 3000 })
    });
  }

  toggleBody(a: InterviewAsset): void {
    if (this.expandedId() === a.id) { this.expandedId.set(null); return; }
    this.expandedId.set(a.id);
    this.bodyText.set('加载中…');
    this.bodySegs.set([]);
    this.api.getTranscript(this.entryId(), a.id).subscribe({
      next: t => {
        const segs = safeJson<TranscriptSegment[]>(t.segmentsJson);
        if (segs?.length) this.bodySegs.set(segs);
        this.bodyText.set(t.transcriptText ?? '(无原文)');
      },
      error: () => this.bodyText.set('(原文读取失败)')
    });
  }

  reassign(a: InterviewAsset, roundId: string): void {
    const target = roundId || null;
    if ((a.roundId ?? null) === target) return;
    this.api.reassignAsset(this.entryId(), a.id, target).subscribe({
      next: () => {
        this.snack.open(target ? '已移入所选轮次' : '已移到沟通记录', '关闭', { duration: 2200 });
        this.changed.emit();
      },
      error: () => this.snack.open('改归属失败', '关闭', { duration: 3000 })
    });
  }

  remove(a: InterviewAsset): void {
    this.api.deleteAsset(this.entryId(), a.id).subscribe({
      next: () => { this.snack.open('已删除', '关闭', { duration: 2000 }); this.changed.emit(); },
      error: () => this.snack.open('删除失败', '关闭', { duration: 3000 })
    });
  }
}
