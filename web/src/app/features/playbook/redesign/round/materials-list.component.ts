import {
  Component, EventEmitter, Output, computed, effect, inject, input, signal
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
import { derivePhase, formatLabel, isAudioAsset, isPrepInputPurpose, joinNames, makeV2Fmt, makeV2T, normalizeThreadSubject, roundDisplayNo } from '../v2.helpers';
import { segmentEmailBody, EmailBodySegmentation } from '../email-body-segments';

/** 票165：邮件卡挂载即取的全文状态（仅邮件 kind；id → 取数状态）。 */
interface EmailBodyState { loading: boolean; error: boolean; text: string; seg: EmailBodySegmentation | null; }

/**
 * 材料列表(2026-10-20 定稿):本轮(或条目级)的全部原始件。
 * 每件三种视图 —— 解析字段 / 原文 / 截图(有才显示);每件带归属下拉,可在
 * 轮次与沟通记录之间移动。旧 Before/After 阶段材料也并入这里(按 Round/Phase 展示)。
 * 票165:邮件卡改为卡片内展开式解析详情(主题全称 + 会议卡块 + thread 时间线
 * 正文 + round 锚定,默认展开、无 380px cap);截图附件不再单独成卡;
 * File lost 红字改按 integrityStatus === 'Missing' 渲染;非邮件卡一字不动。
 */
@Component({
  selector: 'app-materials-list',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (!displayGroups().length) {
      <p class="empty">{{ t('pb.v2.m.empty') }}</p>
    }
    @for (g of displayGroups(); track g.key) {
      @if (g.items.length > 1 && g.subject) {
        <div class="thread-head" data-testid="email-thread-head">
          <span class="thread-subject">🧵 {{ g.subject }}</span>
          <span class="thread-count">{{ fmt('pb.v2.c.threadCount', { n: g.items.length }) }}</span>
        </div>
      }
      @for (a of g.items; track a.id) {
        @if (isEmailAsset(a)) {
          <article class="mat email-card" data-testid="material-item">
            <div class="mat-head">
              <span class="kind-chip">{{ kindLabel(a) }}</span>
              <span class="mat-name">{{ emailSubject(a) }}</span>
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

            @if (roundAnchor(a); as anc) {
              <div class="round-anchor">Round {{ anc.no }} · {{ anc.stage }} · {{ anc.date }} · {{ anc.phase }}</div>
            }
            @if (metaOf(a); as m) {
              <div class="meta-line">
                @if (m.from) { <span>{{ t('pb.v2.m.fFrom') }}<b>{{ m.from }}</b></span> }
                @if (displayDate(a)) { <span>{{ t('pb.v2.m.fDate') }}<b>{{ displayDate(a) }}</b></span> }
              </div>
              @if (m.category || m.format || m.interviewers?.length || m.interviewAt) {
                <dl class="parsed2">
                  @if (m.category) { <div class="prow"><dt>{{ t('pb.v2.m.fCategory') }}</dt><dd>{{ categoryLabel(m.category) }}</dd></div> }
                  @if (m.format || m.interviewers?.length) {
                    <div class="prow"><dt>{{ t('pb.v2.m.fFormat') }}</dt><dd>@if (m.format) { {{ formatLabel(t, m.format) }} }@if (m.format && m.interviewers?.length) { · }@if (m.interviewers?.length) { {{ joinNamesOf(m.interviewers!) }} }</dd></div>
                  }
                  @if (m.interviewAt) { <div class="prow"><dt>{{ t('pb.v2.m.fWhen') }}</dt><dd>{{ m.interviewAt }}</dd></div> }
                </dl>
              }
              @if (m.interviewAt || m.format) {
                <div class="meet-block">@if (m.interviewAt) { 🕑 {{ m.interviewAt }} }@if (m.interviewAt && m.format) { · }@if (m.format) { {{ formatLabel(t, m.format) }} }</div>
              }
            }

            @if (emailBodies().get(a.id); as eb) {
              @if (eb.loading) { <p class="body-loading">{{ t('pb.v2.d.loading') }}</p> }
              @else if (eb.error) { <p class="body-loading">{{ t('pb.v2.m.bodyFailed') }}</p> }
              @else if (!eb.text || !eb.seg) { <p class="body-loading">{{ t('pb.v2.m.noBody') }}</p> }
              @else if (eb.seg.fallback) { <pre class="body-text-full">{{ eb.text }}</pre> }
              @else {
                <div class="timeline">
                  @for (s of eb.seg.timeline; track $index) {
                    <div class="tl-node">
                      <span class="tl-dot"></span>
                      <div class="tl-main">
                        <div class="tl-meta">{{ s.dateTime }} · {{ s.meetingCard ? t('pb.v2.m.meetingCard') : s.sender }}</div>
                        <pre class="tl-body">{{ s.body }}</pre>
                      </div>
                    </div>
                  }
                </div>
                @for (r of eb.seg.remainder; track $index) {
                  <pre class="tl-rest">{{ r.body }}</pre>
                }
              }
            }

            <div class="mat-actions">
              @if (metaOf(a)?.screenshotAssetId) {
                <button type="button" class="mini-btn" (click)="loadShot(a)">{{ t('pb.v2.m.viewShot') }}</button>
              }
              @if (a.integrityStatus === 'Missing') { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
            </div>
            @if (shotUrl() && shotFor() === a.id) {
              <img [src]="shotUrl()" class="shot-img" [alt]="t('pb.v2.m.imgAltShot')">
            }
          </article>
        } @else {
          <article class="mat" data-testid="material-item">
            <div class="mat-head">
              <span class="kind-chip">{{ kindLabel(a) }}</span>
              <!-- 票159：备考输入资产在材料列表中带标签，与 Prep 页读回区是同一资产。 -->
              @if (isPrepInput(a)) { <span class="prep-tag">{{ t('pb.v2.p.prepInputTag') }}</span> }
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
              @if (a.integrityStatus === 'Missing') { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
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
      }
    }
  `,
  styles: [`
    .empty { font-size: 14px; color: var(--yi-text-muted, #475569); padding: 10px 0; }
    .mat { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
    .mat-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .kind-chip { font-size: 12px; font-weight: 700; border: 1px solid var(--yi-border, #e2e8f0);
                 border-radius: 999px; padding: 2px 9px; }
    .prep-tag { font-size: 12px; font-weight: 700; border-radius: 999px; padding: 2px 9px;
                background: var(--yi-accent-tint, #eff6ff); color: var(--yi-accent, #2563eb); }
    .mat-name { font-size: 14px; font-weight: 600; }
    .phase { font-size: 12px; color: var(--yi-text-muted, #475569); }
    .mat-date { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .spacer { flex: 1; }
    .assign { font-size: 13px; color: var(--yi-text-muted, #475569); display: inline-flex; gap: 6px; align-items: center; }
    .fld.sm { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 6px; padding: 4px 7px;
              font-size: 14px; background: transparent; color: inherit; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #dc2626; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .parsed { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 13px;
              color: var(--yi-text-muted, #475569); margin-top: 8px; }
    .parsed b { color: var(--yi-text, #0f172a); font-weight: 600; }
    .parsed .sum { flex-basis: 100%; }
    .mat-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; align-items: center; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .lost { font-size: 13px; color: #dc2626; }
    .player { width: 100%; height: 36px; margin-top: 8px; }
    .shot-img { max-width: 100%; border-radius: 8px; margin-top: 8px; border: 1px solid var(--yi-border, #e2e8f0); }
    .body-box { margin-top: 9px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 9px; }
    .body-text { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 0;
                 font-family: inherit; max-height: 380px; overflow: auto; }
    .seg-line { font-size: 14px; line-height: 1.6; margin: 3px 0; }
    /* 票165：邮件卡新分层（comm-log thread-head 同款式）。 */
    .thread-head { display: flex; align-items: center; gap: 9px; margin: 16px 0 2px; }
    .thread-subject { font-size: 14px; font-weight: 600; }
    .thread-count { font-size: 12px; font-weight: 700; color: #475569; background: #e2e8f0;
                    border-radius: 999px; padding: 1px 9px; }
    .round-anchor { font-size: 13px; color: var(--yi-text-muted, #475569); margin-top: 6px; }
    .meta-line { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 13px;
                 color: var(--yi-text-muted, #475569); margin-top: 6px; }
    .meta-line b { color: var(--yi-text, #0f172a); font-weight: 600; }
    .parsed2 { margin: 8px 0 0; }
    .prow { display: flex; gap: 8px; font-size: 13px; color: var(--yi-text-muted, #475569); margin: 3px 0; }
    .prow dt { flex-shrink: 0; }
    .prow dd { margin: 0; color: var(--yi-text, #0f172a); }
    .meet-block { background: var(--yi-accent-tint, #eff6ff); border-radius: 8px;
                  padding: 8px 10px; font-size: 14px; margin-top: 8px; }
    .body-loading { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 9px 0 0; }
    .body-text-full { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 9px 0 0;
                      font-family: inherit; }
    .timeline { margin-top: 9px; }
    .tl-node { position: relative; padding-left: 20px; margin: 0 0 12px; }
    .tl-node::before { content: ''; position: absolute; left: 4px; top: 7px; width: 9px; height: 9px;
                       border-radius: 50%; background: var(--yi-accent, #2563eb); }
    .tl-node::after { content: ''; position: absolute; left: 8px; top: 20px; bottom: -12px; width: 2px;
                      background: var(--yi-border, #e2e8f0); }
    .tl-node:last-child::after { display: none; }
    .tl-meta { font-size: 13px; color: var(--yi-text-muted, #475569); margin-bottom: 2px; }
    .tl-body { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 0; font-family: inherit; }
    .tl-rest { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 9px 0 0 20px;
               font-family: inherit; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 9px; }
    @media (max-width: 480px) {
      .prow { flex-direction: column; gap: 0; }
      .assign { flex-basis: 100%; }
      .assign .fld.sm { flex: 1; }
    }
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
  readonly fmt = makeV2Fmt(this.i18n);

  readonly expandedId = signal<string | null>(null);
  readonly bodyText = signal('');
  readonly bodySegs = signal<TranscriptSegment[]>([]);
  private readonly fileUrls = signal(new Map<string, string>());
  readonly shotUrl = signal<string | null>(null);
  readonly shotFor = signal<string | null>(null);

  readonly emailBodies = signal(new Map<string, EmailBodyState>());
  private readonly fetchedEmailIds = new Set<string>();

  constructor() {
    // 票165：邮件卡挂载即取全文（现成懒取改挂载取），正文不再需要点击才出现；非邮件 kind 不动。
    effect(() => {
      for (const a of this.assets()) {
        if (!this.isEmailAsset(a) || this.fetchedEmailIds.has(a.id)) continue;
        this.fetchedEmailIds.add(a.id);
        this.emailBodies.update(m => new Map(m).set(a.id, { loading: true, error: false, text: '', seg: null }));
        this.api.getTranscript(this.entryId(), a.id).subscribe({
          next: t => {
            const text = t.transcriptText ?? '';
            this.emailBodies.update(m => new Map(m).set(a.id, { loading: false, error: false, text, seg: segmentEmailBody(text) }));
          },
          error: () => {
            this.emailBodies.update(m => new Map(m).set(a.id, { loading: false, error: true, text: '', seg: null }));
          }
        });
      }
    });
  }

  metaOf(a: InterviewAsset): AssetMeta | null {
    return safeJson<AssetMeta>(a.metaJson);
  }

  /** 票159：备考输入资产（kind 不限，只认 purpose 标签）。 */
  isPrepInput(a: InterviewAsset): boolean {
    return isPrepInputPurpose(this.metaOf(a)?.purpose);
  }

  /** 邮件类材料(与 kindLabel 同一判别口径,供 phase 推导回落规则用；模板亦用，须公开)。 */
  isEmailAsset(a: InterviewAsset): boolean {
    const m = this.metaOf(a);
    return m?.channel === 'email' || (a.kind === 'Text' && !!m?.category);
  }

  /** 票165：邮件主题全称（meta.subject），无主题时回落文件名。 */
  emailSubject(a: InterviewAsset): string {
    return this.metaOf(a)?.subject || a.fileName;
  }

  /** 票165：时间线顶部 round 锚定（无归属轮次时无锚定，不编造）。 */
  roundAnchor(a: InterviewAsset): { no: number; stage: string; date: string; phase: string } | null {
    if (!a.roundId) return null;
    const round = this.rounds().find(r => r.id === a.roundId);
    if (!round) return null;
    const ph = this.phaseOf(a);
    if (ph === null) return null;
    return {
      no: this.roundNo(round),
      stage: round.stage,
      date: (round.scheduledDate ?? '').slice(0, 10) || this.t('pb.v2.d.dateTbd'),
      phase: ph === 'Pre' ? this.t('pb.v2.m.phasePre') : this.t('pb.v2.m.phasePost')
    };
  }

  /**
   * 票165：列表层展示分组（只影响展示，底层仍是单条资产）。
   * 截图去重在先：被任一邮件 screenshotAssetId 引用的资产不单独渲染；
   * 邮件按 threadKey（无则主题归一化兜底，comm-log 同口径）分组，组内按日期正序、
   * 多封组带组头（归一化主题 + 封数）、单封组无组头；非邮件资产原序单条。
   */
  readonly displayGroups = computed(() => {
    const referenced = new Set<string>();
    for (const a of this.assets()) {
      const sid = this.metaOf(a)?.screenshotAssetId;
      if (sid) referenced.add(sid);
    }
    const visible = this.assets().filter(a => !referenced.has(a.id));
    interface DisplayGroup { key: string; subject: string; items: InterviewAsset[] }
    const groups: DisplayGroup[] = [];
    const byKey = new Map<string, DisplayGroup>();
    for (const a of visible) {
      if (this.isEmailAsset(a)) {
        const m = this.metaOf(a);
        const key = m?.threadKey ? 'th:' + m.threadKey
          : m?.subject ? 'th:' + normalizeThreadSubject(m.subject).key
          : 'solo:' + a.id;
        let g = byKey.get(key);
        if (!g) { g = { key, subject: '', items: [] }; byKey.set(key, g); groups.push(g); }
        g.items.push(a);
      } else {
        groups.push({ key: 'solo:' + a.id, subject: '', items: [a] });
      }
    }
    for (const g of groups) {
      if (g.items.length > 1) {
        g.items.sort((x, y) => (this.displayDate(x) < this.displayDate(y) ? -1 : 1));
        g.subject = normalizeThreadSubject(this.metaOf(g.items[0])?.subject ?? '').display;
      }
    }
    return groups;
  });

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
