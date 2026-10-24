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
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, formatLabel, isAudioAsset, joinNames, makeV2T, roundDisplayNo } from '../v2.helpers';

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
      <p class="empty">{{ t('pb.v2.m.empty') }}</p>
    }
    @for (a of assets(); track a.id) {
      <article class="mat" data-testid="material-item">
        <div class="mat-head">
          <span class="kind-chip">{{ kindLabel(a) }}</span>
          <span class="mat-name">{{ a.fileName }}</span>
          @if (phaseOf(a); as ph) { <span class="phase">{{ ph === 'Pre' ? t('pb.v2.m.phasePre') : t('pb.v2.m.phasePost') }}</span> }
          <span class="mat-date">{{ displayDate(a) }}</span>
          <span class="spacer"></span>
          <label class="assign">{{ t('pb.v2.m.assign') }}
            <select class="fld sm" [ngModel]="a.roundId ?? ''" (ngModelChange)="reassign(a, $event)">
              <option value="">{{ t('pb.v2.m.commOption') }}</option>
              @for (r of rounds(); track r.id) {
                <option [value]="r.id">Round {{ roundNo(r) }} · {{ r.stage }}</option>
              }
            </select>
          </label>
          <button type="button" class="icon-btn danger" (click)="remove(a)" [title]="t('common.delete')">
            <mat-icon>delete_outline</mat-icon>
          </button>
        </div>

        @if (metaOf(a); as m) {
          @if (m.category || m.interviewAt || m.from) {
            <div class="parsed">
              @if (m.category) { <span>{{ t('pb.v2.m.fCategory') }}<b>{{ categoryLabel(m.category) }}</b></span> }
              @if (m.from) { <span>{{ t('pb.v2.m.fFrom') }}<b>{{ m.from }}</b></span> }
              @if (m.date) { <span>{{ t('pb.v2.m.fDate') }}<b>{{ m.date }}</b></span> }
              @if (m.interviewAt) { <span>{{ t('pb.v2.m.fWhen') }}<b>{{ m.interviewAt }}</b></span> }
              @if (m.interviewers?.length) { <span>{{ t('pb.v2.m.fInterviewers') }}<b>{{ joinNamesOf(m.interviewers!) }}</b></span> }
              @if (m.format) { <span>{{ t('pb.v2.m.fFormat') }}<b>{{ formatLabel(t, m.format) }}</b></span> }
              @if (m.summary) { <span class="sum">{{ t('pb.v2.m.fSummary') }}{{ m.summary }}</span> }
            </div>
          }
        }

        <div class="mat-actions">
          @if (isAudioAsset(a)) {
            <button type="button" class="mini-btn" (click)="loadFile(a)">{{ t('pb.v2.m.playAudio') }}</button>
          }
          @if (hasBody(a)) {
            <button type="button" class="mini-btn" (click)="toggleBody(a)">
              {{ expandedId() === a.id ? t('pb.v2.m.collapseBody') : t('pb.v2.m.viewBody') }}
            </button>
          }
          @if (metaOf(a)?.screenshotAssetId) {
            <button type="button" class="mini-btn" (click)="loadShot(a)">{{ t('pb.v2.m.viewShot') }}</button>
          }
          @if (a.fileExists === false) { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
        </div>

        @if (fileUrl(a.id); as url) {
          @if (isAudioAsset(a)) { <audio controls [src]="url" class="player"></audio> }
          @else { <img [src]="url" class="shot-img" [alt]="t('pb.v2.m.imgAltMaterial')"> }
        }
        @if (shotUrl() && shotFor() === a.id) {
          <img [src]="shotUrl()" class="shot-img" [alt]="t('pb.v2.m.imgAltShot')">
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
    .empty { font-size: 14px; color: var(--yi-text-muted, #5b6270); padding: 10px 0; }
    .mat { border: 1px solid var(--yi-border, #e1e4ec); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
    .mat-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .kind-chip { font-size: 12px; font-weight: 700; border: 1px solid var(--yi-border, #e1e4ec);
                 border-radius: 999px; padding: 2px 9px; }
    .mat-name { font-size: 14px; font-weight: 600; }
    .phase { font-size: 12px; color: var(--yi-text-muted, #5b6270); }
    .mat-date { font-size: 13px; color: var(--yi-text-muted, #5b6270); }
    .spacer { flex: 1; }
    .assign { font-size: 13px; color: var(--yi-text-muted, #5b6270); display: inline-flex; gap: 6px; align-items: center; }
    .fld.sm { border: 1px solid var(--yi-border, #e1e4ec); border-radius: 6px; padding: 4px 7px;
              font-size: 14px; background: transparent; color: inherit; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #b3261e; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .parsed { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 13px;
              color: var(--yi-text-muted, #5b6270); margin-top: 8px; }
    .parsed b { color: var(--yi-text, #1f2430); font-weight: 600; }
    .parsed .sum { flex-basis: 100%; }
    .mat-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; align-items: center; }
    .mini-btn { border: 1px solid var(--yi-border, #e1e4ec); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .lost { font-size: 13px; color: #b3261e; }
    .player { width: 100%; height: 36px; margin-top: 8px; }
    .shot-img { max-width: 100%; border-radius: 8px; margin-top: 8px; border: 1px solid var(--yi-border, #e1e4ec); }
    .body-box { margin-top: 9px; border-top: 1px dashed var(--yi-border, #e1e4ec); padding-top: 9px; }
    .body-text { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 0;
                 font-family: inherit; max-height: 380px; overflow: auto; }
    .seg-line { font-size: 14px; line-height: 1.6; margin: 3px 0; }
  `]
})
export class MaterialsListComponent {
  readonly entryId = input.required<string>();
  readonly assets = input.required<InterviewAsset[]>();
  readonly rounds = input<InterviewRound[]>([]);

  /** 票30(REQ-10):归属下拉编号与详情页 stepper 同一日期序口径(helper 内部自排序,input 顺序无关)。 */
  roundNo(r: InterviewRound): number {
    return roundDisplayNo(this.rounds(), r);
  }
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);

  readonly expandedId = signal<string | null>(null);
  readonly bodyText = signal('');
  readonly bodySegs = signal<TranscriptSegment[]>([]);
  private readonly fileUrls = signal(new Map<string, string>());
  readonly shotUrl = signal<string | null>(null);
  readonly shotFor = signal<string | null>(null);

  metaOf(a: InterviewAsset): AssetMeta | null {
    return safeJson<AssetMeta>(a.metaJson);
  }

  /** 邮件类材料(与 kindLabel 同一判别口径,供 phase 推导回落规则用)。 */
  private isEmailAsset(a: InterviewAsset): boolean {
    const m = this.metaOf(a);
    return m?.channel === 'email' || (a.kind === 'Text' && !!m?.category);
  }

  /**
   * Before/After 展示值(2026-10-21 用户裁决):有归属轮次时按「材料日期 vs
   * 轮次日期」推导(见 derivePhase),存量写错的 phase 在展示时就被纠正,
   * 不做数据迁移;无归属轮次(条目级材料)无从比较,保持存量值。
   */
  phaseOf(a: InterviewAsset): 'Pre' | 'Post' | null {
    const stored = a.phase === 'Pre' || a.phase === 'Post' ? a.phase : null;
    if (!a.roundId) return stored;
    const round = this.rounds().find(r => r.id === a.roundId);
    if (!round) return stored;
    return derivePhase(this.isEmailAsset(a), this.displayDate(a), round.scheduledDate);
  }

  readonly formatLabel = formatLabel;
  readonly isAudioAsset = isAudioAsset;

  joinNamesOf(names: string[]): string {
    return joinNames(this.i18n, names);
  }

  kindLabel(a: InterviewAsset): string {
    const m = this.metaOf(a);
    if (m?.channel === 'email' || (a.kind === 'Text' && m?.category)) return this.t('pb.v2.m.kindEmail');
    switch (a.kind) {
      case 'Audio': return this.t('pb.v2.m.kindAudio');
      case 'Transcript': return this.t('pb.v2.m.kindTranscript');
      case 'Notes': return this.t('pb.v2.m.kindNotes');
      case 'Text': return this.t('pb.v2.m.kindText');
      case 'Guidance': return this.t('pb.v2.m.kindGuidance');
      default: return a.kind;
    }
  }

  categoryLabel(c: string): string {
    const key = 'pb.v2.cat.' + c;
    const v = this.t(key);
    return v === key ? c : v;
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
      error: () => this.snack.open(this.t('pb.v2.m.fileLoadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  loadShot(a: InterviewAsset): void {
    const shotId = this.metaOf(a)?.screenshotAssetId;
    if (!shotId) return;
    if (this.shotFor() === a.id && this.shotUrl()) { this.shotUrl.set(null); return; }
    this.api.audioBlob(this.entryId(), shotId).subscribe({
      next: blob => { this.shotUrl.set(URL.createObjectURL(blob)); this.shotFor.set(a.id); },
      error: () => this.snack.open(this.t('pb.v2.m.shotLoadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  toggleBody(a: InterviewAsset): void {
    if (this.expandedId() === a.id) { this.expandedId.set(null); return; }
    this.expandedId.set(a.id);
    this.bodyText.set(this.t('pb.v2.d.loading'));
    this.bodySegs.set([]);
    this.api.getTranscript(this.entryId(), a.id).subscribe({
      next: t => {
        const segs = safeJson<TranscriptSegment[]>(t.segmentsJson);
        if (segs?.length) this.bodySegs.set(segs);
        this.bodyText.set(t.transcriptText ?? this.t('pb.v2.m.noBody'));
      },
      error: () => this.bodyText.set(this.t('pb.v2.m.bodyFailed'))
    });
  }

  reassign(a: InterviewAsset, roundId: string): void {
    const target = roundId || null;
    if ((a.roundId ?? null) === target) return;
    this.api.reassignAsset(this.entryId(), a.id, target).subscribe({
      next: () => {
        this.snack.open(target ? this.t('pb.v2.m.movedToRound') : this.t('pb.v2.m.movedToComm'), this.t('common.close'), { duration: 2200 });
        this.changed.emit();
      },
      error: () => this.snack.open(this.t('pb.v2.m.reassignFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  remove(a: InterviewAsset): void {
    this.api.deleteAsset(this.entryId(), a.id).subscribe({
      next: () => { this.snack.open(this.t('pb.v2.m.deleted'), this.t('common.close'), { duration: 2000 }); this.changed.emit(); },
      error: () => this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 })
    });
  }
}
