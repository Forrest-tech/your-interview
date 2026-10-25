import {
  Component, EventEmitter, HostListener, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewAsset, InterviewDetail, InterviewRound, KnowledgeItem
} from '../../../../core/models/api.models';
import { QaZoomComponent } from '../../../../core/qa-zoom/qa-zoom.component';
import { QaZoomService } from '../../../../core/qa-zoom/qa-zoom.service';
import { TtsPlayerService } from '../../../../core/tts/tts-player.service';
import { PlaybookV2Api } from '../playbook-v2.api';
import { serverFieldErrorSummary } from '../../../../core/api/validation-errors.helpers';
import {
  PrepBlock, PrepQuestionItem, PrepReport, PrepSection, prepStdText, safeJson
} from '../v2.models';
import {
  classifyPrepStage, prepFrameworkSectionIds, prepQuestionExportBlocks
} from './prep-report.helpers';
import { ExportDialogComponent, ExportDialogData } from '../export/export-dialog.component';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { makeV2Fmt, makeV2T } from '../v2.helpers';
import { ExportBlock, ExportDoc } from '../export/export-doc';

/**
 * 备考报告(2026-10-20 定稿):按模拟面试流程排布的完整备考文档。
 * 左侧要点目录(点击定位、滚动高亮),右侧正文;题目与 Tech Stack 双向同步 ——
 * 已入库的题答案以 Tech Stack 那份为准(同一条记录),在这里改即同步回去;
 * 未入库的题选分类后一键加入,自动带来源标签(公司 · Round · 日期)。
 * 与短板相关的题置顶标红。Guidance 类旧材料在本页签底部并列,不丢失。
 */
@Component({
  selector: 'app-prep-report',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, QaZoomComponent],
  template: `
    <div class="prep" [style.--qa-scale]="zoom.scale()">
      <div class="prep-head">
        <div>
          <div class="ph-title">{{ t('pb.v2.p.title') }}</div>
          @if (report(); as r) {
            <div class="ph-sub">
              {{ fmt('pb.v2.p.generatedAt', { when: (r.generatedAt ?? '').slice(0, 16).replace('T', ' ') }) }}
              · {{ t('pb.v2.p.sourcesLabel') }}:{{ t('pb.v2.p.srcJd') }} {{ r.sources?.jd ? '✓' : '—' }}
              / {{ t('pb.v2.p.srcCompany') }} {{ r.sources?.company ? '✓' : '—' }}
              / {{ t('pb.v2.p.srcExp') }} {{ r.sources?.experiences ? '✓' : '—' }}
              / {{ t('pb.v2.p.srcResume') }} {{ r.sources?.resume ? '✓' : '—' }}
              / {{ fmt('pb.v2.p.srcPast', { n: r.sources?.pastQuestions ?? 0 }) }}
              / {{ fmt('pb.v2.p.srcWeak', { n: r.sources?.weaknesses ?? 0 }) }}
            </div>
            @if (r.sources?.details?.length) {
              <div class="ph-sub src-details" data-testid="prep-source-details">
                @for (d of r.sources!.details!; track d.key) {
                  <span class="src-detail">{{ sourceLabel(d.key) }}: {{ t('pb.v2.srcst.' + d.status) }}@if (d.count != null) { ({{ d.count }}) }</span>
                }
              </div>
            }
          } @else {
            <div class="ph-sub">{{ t('pb.v2.p.fwNote') }}</div>
          }
        </div>
        <span class="spacer"></span>
        <app-qa-zoom></app-qa-zoom>
        @if (report()) {
          <button mat-stroked-button (click)="openExport()">{{ t('pb.v2.p.exportPrep') }}</button>
        }
        <button mat-raised-button color="primary" (click)="generate()" [disabled]="busy()">
          {{ busy() ? t('pb.v2.p.generating') : (report() ? t('pb.v2.p.regenerate') : t('pb.v2.p.generate')) }}
        </button>
      </div>
      @if (notice()) { <p class="notice">{{ notice() }}</p> }
      @if (tts.error()) { <p class="notice warn">{{ tts.error() }}</p> }

      @if (!report()) {
        <div class="gen-box">
          <label>{{ t('pb.v2.p.genExpLabel') }}
            <textarea rows="4" class="fld" [ngModel]="experiencesDraft()"
                      (ngModelChange)="experiencesDraft.set($event)"
                      [placeholder]="t('pb.v2.p.genExpPh')"></textarea>
          </label>
          <label>{{ t('pb.v2.p.genResumeLabel') }}
            <textarea rows="3" class="fld" [ngModel]="resumeDraft()"
                      (ngModelChange)="resumeDraft.set($event)"
                      [placeholder]="t('pb.v2.p.genResumePh')"></textarea>
          </label>
          <p class="hint">{{ t('pb.v2.p.genHint') }}</p>
        </div>
      }
        <div class="prep-body">
          <nav class="toc">
            @for (s of displayReport()!.sections; track s.id) {
              <button type="button" class="toc-item" [class.on]="activeSec() === s.id"
                      (click)="jumpTo(s.id)">{{ s.title }}</button>
            }
          </nav>
          <div class="sections">
            @for (s of displayReport()!.sections; track s.id) {
              <section class="psec" [id]="'prep-sec-' + s.id" data-testid="prep-section">
                <h4>{{ s.title }}</h4>
                @for (b of s.blocks ?? []; track $index) {
                  @if (b.type === 'script') {
                    <div class="script-box">
                      @if (b.title) { <div class="sb-title">{{ b.title }}</div> }
                      @if (editingBlock() === s.id + ':' + $index) {
                        <textarea class="fld qa-text" rows="6" [ngModel]="blockDraft()"
                                  (ngModelChange)="blockDraft.set($event)"></textarea>
                        <div class="row">
                          <button mat-raised-button color="primary" (click)="saveBlock(s, $index)">{{ t('common.save') }}</button>
                          <button mat-button (click)="editingBlock.set(null)">{{ t('common.cancel') }}</button>
                        </div>
                      } @else {
                        <p class="qa-text pre">{{ b.userText || b.text }}</p>
                        <div class="row">
                          <button type="button" class="mini-btn" (click)="playText(b.userText || b.text || '')">
                            {{ tts.playingKey() === tts.keyFor(b.userText || b.text || '') ? t('pb.v2.p.stop') : t('pb.v2.p.play') }}
                          </button>
                          <button type="button" class="mini-btn" (click)="editBlock(s, $index, b)">{{ t('pb.v2.p.edit') }}</button>
                          @if (b.userText) { <span class="mine-tag">{{ t('pb.v2.p.mineTag') }}</span> }
                        </div>
                      }
                    </div>
                  } @else if (b.type === 'points' || b.type === 'list') {
                    @if (b.title) { <div class="sb-title">{{ b.title }}</div> }
                    <ul class="pts">
                      @for (it of b.items ?? []; track $index) { <li>{{ it }}</li> }
                    </ul>
                  } @else if (b.text) {
                    <p class="qa-text pre">{{ b.text }}</p>
                  }
                }
                @for (q of sortedQuestions(s); track $index) {
                  <article class="pq" [class.weak]="q.weaknessRelated" data-testid="prep-question">
                    <div class="pq-head">
                      @if (q.weaknessRelated) { <span class="weak-pin">{{ t('pb.v2.p.weakPin') }}</span> }
                      <span class="pq-text qa-text">{{ q.question }}</span>
                    </div>
                    @if (q.note) {
                      <div class="pq-note">{{ q.note }}</div>
                    }
                    @if (q.sourceTags?.length) {
                      <div class="tags">@for (tg of q.sourceTags!; track $index) { <span class="tag">{{ tg }}</span> }</div>
                    }
                    @if (q.knowledgeId) {
                      <div class="sync-row">
                        <span class="sync-tag">{{ t('pb.v2.p.inStack') }}</span>
                        <span class="sync-note">{{ t('pb.v2.p.syncNote') }}</span>
                      </div>
                      <div class="sync-answer">
                        <div class="sa-label">{{ t('pb.v2.p.stackAnswerLabel') }}</div>
                        @if (editingSyncQ() === q.knowledgeId) {
                          <textarea class="fld qa-text" rows="5" [ngModel]="syncDraft()"
                                    (ngModelChange)="syncDraft.set($event)"></textarea>
                          <div class="row">
                            <button mat-raised-button color="primary" (click)="saveSyncAnswer(q)">{{ t('pb.v2.p.saveSync') }}</button>
                            <button mat-button (click)="editingSyncQ.set(null)">{{ t('common.cancel') }}</button>
                          </div>
                        } @else {
                          <p class="qa-text pre">{{ knowledgeAnswer(q.knowledgeId) || t('pb.v2.p.stackNoAnswer') }}</p>
                          <div class="row">
                            <button type="button" class="mini-btn"
                                    (click)="playText(knowledgeAnswer(q.knowledgeId))">{{ t('pb.v2.p.play') }}</button>
                            <button type="button" class="mini-btn" (click)="editSyncAnswer(q)">{{ t('pb.v2.p.editSync') }}</button>
                          </div>
                        }
                      </div>
                    } @else {
                      @for (std of prepStds(q); track std.key) {
                        @if (std.text || true) {
                          <div class="std-line">
                            <span class="std-label">{{ std.label }}</span>
                            @if (std.isUser) { <span class="mine-tag">{{ t('pb.v2.p.mineTag') }}</span> }
                            <span class="spacer"></span>
                            <button type="button" class="mini-btn" [disabled]="!std.text"
                                    (click)="playText(std.text)">▶</button>
                            <button type="button" class="mini-btn"
                                    (click)="editPrepStd(s, q, std.key, std.text)">✎</button>
                          </div>
                          @if (editingPrepStd() === s.id + '|' + q.question + '|' + std.key) {
                            <textarea class="fld qa-text" rows="4" [ngModel]="prepStdDraft()"
                                      (ngModelChange)="prepStdDraft.set($event)"></textarea>
                            <div class="row">
                              <button mat-raised-button color="primary"
                                      (click)="savePrepStd(s, q, std.key)">{{ t('common.save') }}</button>
                              <button mat-button (click)="editingPrepStd.set(null)">{{ t('common.cancel') }}</button>
                            </div>
                          } @else if (std.text) {
                            <p class="qa-text pre std-p">{{ std.text }}</p>
                          }
                        }
                      }
                      <div class="join-row">
                        <label>{{ t('pb.v2.p.category') }}
                          <input class="fld sm" list="prep-topics"
                                 [ngModel]="joinCategory()[q.question]"
                                 (ngModelChange)="setJoinCat(q.question, $event)"
                                 [placeholder]="t('pb.v2.p.categoryPh')">
                          <datalist id="prep-topics">
                            @for (tp of topics(); track tp) { <option [value]="tp"></option> }
                          </datalist>
                        </label>
                        <button mat-stroked-button (click)="joinStack(s, q)" [disabled]="busy()">
                          {{ t('pb.v2.p.joinStack') }}
                        </button>
                        <span class="join-note">{{ fmt('pb.v2.p.sourceAuto', { company: entry().companyName, n: round().order, date: round().scheduledDate ?? '' }) }}</span>
                      </div>
                    }
                  </article>
                }
              </section>
            }

            @if (guidanceAssets().length) {
              <section class="psec">
                <h4>{{ t('pb.v2.p.guidanceTitle') }}</h4>
                <p class="hint">{{ t('pb.v2.p.guidanceHint') }}</p>
                <ul class="pts">
                  @for (g of guidanceAssets(); track g.id) { <li>{{ g.fileName }}</li> }
                </ul>
              </section>
            }
          </div>
        </div>
    </div>
  `,
  styles: [`
    .prep { padding: 4px 2px; }
    .qa-text { font-size: calc(15px * var(--qa-scale, 1)); line-height: 1.65; }
    .pre { white-space: pre-wrap; }
    .prep-head { display: flex; align-items: flex-start; gap: 8px; flex-wrap: wrap; }
    .ph-title { font-size: 16px; font-weight: 600; }
    .ph-sub { font-size: 13px; color: var(--yi-text-muted, #475569); margin-top: 2px; max-width: 560px; }
    .ph-sub.src-details { display: flex; gap: 2px 14px; flex-wrap: wrap; max-width: none; }
    .src-detail { white-space: nowrap; }
    .spacer { flex: 1; }
    .notice { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .notice.warn { color: #dc2626; }
    .gen-box { display: flex; flex-direction: column; gap: 12px; margin-top: 14px; max-width: 720px; }
    .gen-box label { display: flex; flex-direction: column; gap: 5px; font-size: 13px;
                     color: var(--yi-text-muted, #475569); }
    .fld { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 8px; padding: 8px 10px;
           font-size: 14px; background: transparent; color: inherit; box-sizing: border-box; }
    .fld.sm { padding: 5px 8px; font-size: 14px; }
    .hint { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .prep-body { display: grid; grid-template-columns: 170px 1fr; gap: 20px; margin-top: 12px; }
    @media (max-width: 760px) { .prep-body { grid-template-columns: 1fr; } .toc { position: static !important; flex-direction: row !important; flex-wrap: wrap; } }
    .toc { position: sticky; top: 8px; align-self: start; display: flex; flex-direction: column; gap: 2px; }
    .toc-item { text-align: left; border: none; background: transparent; color: inherit; cursor: pointer;
                font-size: 14px; padding: 6px 9px; border-radius: 6px; }
    .toc-item.on { background: color-mix(in srgb, var(--yi-accent, #2563eb) 14%, transparent); font-weight: 700; }
    .psec { margin-bottom: 22px; scroll-margin-top: 10px; }
    .psec h4 { font-size: 16px; font-weight: 600; margin: 0 0 8px; }
    .script-box { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 11px 13px; margin: 8px 0; }
    .sb-title { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); margin-bottom: 4px; }
    .script-box p { margin: 2px 0 8px; }
    .pts { margin: 4px 0 8px; padding-left: 20px; font-size: 14px; line-height: 1.65; }
    .row { display: flex; gap: 8px; align-items: center; margin-top: 6px; flex-wrap: wrap; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .mine-tag { font-size: 12px; font-weight: 700; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 1px 7px; }
    .pq { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 11px 13px; margin: 9px 0; }
    .pq.weak { border-color: #fef2f2; background: color-mix(in srgb, #fef2f2 5%, transparent); }
    .pq-head { display: flex; gap: 8px; align-items: flex-start; }
    .pq-note { font-size: 12px; color: #666; margin-top: 2px; }
    .weak-pin { font-size: 12px; font-weight: 700; color: #dc2626; white-space: nowrap; margin-top: 3px; }
    .pq-text { font-weight: 600; }
    .tags { display: flex; gap: 5px; flex-wrap: wrap; margin-top: 5px; }
    .tag { font-size: 12px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 999px; padding: 1px 8px;
           color: var(--yi-text-muted, #475569); }
    .sync-row { display: flex; gap: 8px; align-items: center; margin-top: 8px; flex-wrap: wrap; }
    .sync-tag { font-size: 12px; font-weight: 700; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 2px 9px; }
    .sync-note { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .sync-answer { margin-top: 6px; }
    .sa-label { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); }
    .sync-answer p { margin: 4px 0; }
    .std-line { display: flex; align-items: center; gap: 8px; margin-top: 9px; }
    .std-label { font-size: 13px; font-weight: 700; color: var(--yi-accent, #2563eb); }
    .std-p { margin: 4px 0 2px; }
    .join-row { display: flex; gap: 10px; align-items: flex-end; margin-top: 10px; flex-wrap: wrap;
                border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 10px; }
    .join-row label { display: flex; flex-direction: column; gap: 4px; font-size: 13px;
                      color: var(--yi-text-muted, #475569); }
    .join-note { font-size: 13px; color: var(--yi-text-muted, #475569); }
  `]
})
export class PrepReportComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  readonly guidanceAssets = input<InterviewAsset[]>([]);
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);
  readonly zoom = inject(QaZoomService);
  readonly tts = inject(TtsPlayerService);

  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  readonly experiencesDraft = signal('');
  readonly resumeDraft = signal('');
  readonly activeSec = signal('');
  readonly editingBlock = signal<string | null>(null);
  readonly blockDraft = signal('');
  readonly editingSyncQ = signal<string | null>(null);
  readonly syncDraft = signal('');
  readonly editingPrepStd = signal<string | null>(null);
  readonly prepStdDraft = signal('');
  readonly joinCategory = signal<Record<string, string>>({});
  readonly knowledge = signal(new Map<string, KnowledgeItem>());
  readonly topics = signal<string[]>([]);

  readonly report = computed(() => safeJson<PrepReport>(this.round().prepReportJson));

  /**
   * 展示用报告:已生成 → 用生成的;未生成 → 默认框架(票133 定稿 §4.1/§10.1)。
   * 框架与生成报告同构(13 节位顺序 + 左目录 + 滚动高亮),分类走 §4.2 镜像表;
   * 文案全走核心字典 pb.v2 三语键,不调 AI、不落库;生成入口(gen-box)保留在框架上方。
   */
  readonly displayReport = computed<PrepReport | null>(
    () => this.report() ?? this.frameworkReport());

  private frameworkReport(): PrepReport {
    const cls = classifyPrepStage(this.round().stage);
    const pts = (keys: string[]): PrepBlock[] =>
      [{ type: 'points', items: keys.map(k => this.t(k)) }];
    const sec = (id: string, name: string, keys: string[]): PrepSection => ({
      id, title: this.t(`pb.v2.p.fw.${name}.title`), blocks: pts(keys), questions: []
    });
    const p = (name: string, n: number): string[] =>
      Array.from({ length: n }, (_, i) => `pb.v2.p.fw.${name}.p${i + 1}`);
    const group: Record<string, string> = {
      'round-brief': 'roundBrief', greeting: 'greeting', 'intro-listen': 'introListen',
      'self-intro': 'selfIntro', 'deep-dive': 'deepDive', 'coding-round': 'codingRound',
      'design-round': 'designRound', behavioral: 'behavioral', 'weakness-plan': 'weaknessPlan',
      'ask-back': 'askBack', 'comp-process': 'compProcess', closing: 'closing'
    };
    /** ML-FIX-PREP:各节要点数(默认 4;新增段落的节在此登记,缺省仍为 4)。 */
    const FW_POINTS: Record<string, number> = {
      greeting: 5, 'design-round': 7, 'weakness-plan': 5, closing: 7,
    };
    const sections: PrepSection[] = [];
    for (const id of prepFrameworkSectionIds(cls)) {
      if (id === 'tech-qa') {
        const blocks = [...pts(p('techQa', 4))];
        if (!cls.isDesign) {
          blocks.push({
            type: 'points', title: this.t('pb.v2.p.fw.designCard.title'),
            items: p('designCard', 3).map(k => this.t(k))
          });
        }
        sections.push({
          id, title: this.t('pb.v2.p.fw.techQa.title'), blocks, questions: []
        });
      } else {
        sections.push(sec(id, group[id], p(group[id], FW_POINTS[id] ?? 4)));
      }
    }
    return { sections };
  }

  /** Sources 明细行的标签(后端 details.key → 文案键)。 */
  sourceLabel(key: string): string {
    switch (key) {
      case 'questionBank': return this.t('pb.v2.p.srcQb');
      case 'company': return this.t('pb.v2.p.srcCompany');
      case 'web': return this.t('pb.v2.p.srcWeb');
      case 'glassdoor': return this.t('pb.v2.p.srcGlassdoor');
      case 'manualExperiences': return this.t('pb.v2.p.srcManual');
      default: return key;
    }
  }

  private loadedKey = '';

  constructor() {
    // 默认框架(未生成)时目录也要有默认高亮段 —— 与生成报告同一口径。
    effect(() => {
      const secs = this.displayReport()?.sections;
      if (secs?.length && !this.activeSec()) this.activeSec.set(secs[0].id);
    }, { allowSignalWrites: true });

    effect(() => {
      const r = this.report();
      const key = this.round().id + ':' + (r?.generatedAt ?? '');
      if (key === this.loadedKey) return;
      this.loadedKey = key;
      if (r?.sections.length && !this.activeSec()) this.activeSec.set(r.sections[0].id);
      // 拉全量 Tech Stack:分类候选 + 把备考题与已有条目按(来源条目+题面)对齐 ——
      // 对齐上的题就是同一条记录:显示「已在 Tech Stack」,答案读写都走那一份,
      // 并把 knowledgeId 回写进报告,之后不再重复匹配。
      this.api.listKnowledge().subscribe({
        next: items => {
          const map = new Map<string, KnowledgeItem>();
          for (const it of items) map.set(it.id, it);
          this.knowledge.set(map);
          this.topics.set([...new Set(items.map(i => i.topic).filter(Boolean))].sort());
          const rep = this.report();
          if (!rep) return;
          let dirty = false;
          const next: PrepReport = JSON.parse(JSON.stringify(rep)) as PrepReport;
          for (const sec of next.sections) {
            for (const q of sec.questions ?? []) {
              if (q.knowledgeId) continue;
              const hit = items.find(k =>
                k.sourceInterviewEntryId === this.entry().id &&
                (k.question ?? '').trim().toLowerCase() === q.question.trim().toLowerCase());
              if (hit) { q.knowledgeId = hit.id; dirty = true; }
            }
          }
          if (dirty) void this.persist(next);
        },
        error: () => undefined
      });
    }, { allowSignalWrites: true });
  }

  @HostListener('window:scroll')
  onScroll(): void {
    const r = this.displayReport();
    if (!r) return;
    let current = this.activeSec();
    for (const s of r.sections) {
      const el = document.getElementById('prep-sec-' + s.id);
      if (el && el.getBoundingClientRect().top <= 140) current = s.id;
    }
    if (current !== this.activeSec()) this.activeSec.set(current);
  }

  jumpTo(id: string): void {
    this.activeSec.set(id);
    document.getElementById('prep-sec-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  sortedQuestions(s: PrepSection): PrepQuestionItem[] {
    const qs = [...(s.questions ?? [])];
    return qs.sort((a, b) => Number(b.weaknessRelated ?? false) - Number(a.weaknessRelated ?? false));
  }

  knowledgeAnswer(id: string): string {
    const k = this.knowledge().get(id);
    return k?.betterAnswer || k?.myAnswer || '';
  }

  prepStds(q: PrepQuestionItem): { key: 'spoken' | 'technical'; label: string; text: string; isUser: boolean }[] {
    const sp = prepStdText(q.stdSpoken);
    const te = prepStdText(q.stdTechnical);
    return [
      { key: 'spoken', label: this.t('pb.v2.p.spoken'), text: sp.text, isUser: sp.isUser },
      { key: 'technical', label: this.t('pb.v2.p.technical'), text: te.text, isUser: te.isUser }
    ];
  }

  playText(text: string): void {
    if (text) void this.tts.toggle(text);
  }

  // ---------------- 生成 ----------------

  async generate(): Promise<void> {
    this.busy.set(true);
    this.notice.set(null);
    try {
      const r = await this.api.generatePrepReport(this.entry().id, this.round().id, {
        jdText: this.entry().jdText ?? null,
        resumeText: this.resumeDraft().trim() || null,
        experiences: this.experiencesDraft().trim() || null,
        // 生成语言跟随系统语言(账号 preferredLanguage 决定界面语言,同一口径)
        language: this.i18n.lang()
      }).toPromise();
      const parsed = safeJson<PrepReport>(r?.prepReportJson);
      this.notice.set(parsed?.generatedBy === 'local-aggregation'
        ? this.t('pb.v2.p.localNotice')
        : this.t('pb.v2.p.generatedOk'));
      this.changed.emit();
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.p.generateFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.rec.unknownError') }));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------------- 稿子块编辑 ----------------

  editBlock(s: PrepSection, idx: number, b: PrepBlock): void {
    this.blockDraft.set(b.userText || b.text || '');
    this.editingBlock.set(s.id + ':' + idx);
  }

  async saveBlock(s: PrepSection, idx: number): Promise<void> {
    const r = this.report();
    if (!r) return;
    const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
    const sec = next.sections.find(x => x.id === s.id);
    if (!sec?.blocks?.[idx]) return;
    sec.blocks[idx].userText = this.blockDraft();
    await this.persist(next);
    this.editingBlock.set(null);
  }

  // ---------------- 备考题标准答案编辑(未入库) ----------------

  editPrepStd(s: PrepSection, q: PrepQuestionItem, key: 'spoken' | 'technical', text: string): void {
    this.prepStdDraft.set(text);
    this.editingPrepStd.set(s.id + '|' + q.question + '|' + key);
  }

  async savePrepStd(s: PrepSection, q: PrepQuestionItem, key: 'spoken' | 'technical'): Promise<void> {
    const r = this.report();
    if (!r) return;
    const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
    const sec = next.sections.find(x => x.id === s.id);
    const item = sec?.questions?.find(x => x.question === q.question);
    if (!item) return;
    const cur = key === 'spoken' ? item.stdSpoken : item.stdTechnical;
    const ai = typeof cur === 'string' ? cur : (cur?.ai ?? null);
    const merged = { ai, user: this.prepStdDraft() };
    if (key === 'spoken') item.stdSpoken = merged; else item.stdTechnical = merged;
    await this.persist(next);
    this.editingPrepStd.set(null);
  }

  // ---------------- Tech Stack 同步 ----------------

  editSyncAnswer(q: PrepQuestionItem): void {
    if (!q.knowledgeId) return;
    this.syncDraft.set(this.knowledgeAnswer(q.knowledgeId));
    this.editingSyncQ.set(q.knowledgeId);
  }

  async saveSyncAnswer(q: PrepQuestionItem): Promise<void> {
    if (!q.knowledgeId) return;
    this.busy.set(true);
    try {
      await this.api.updateKnowledgeAnswer(q.knowledgeId,
        { betterAnswer: this.syncDraft() }).toPromise();
      const fresh = await this.api.getKnowledge(q.knowledgeId).toPromise();
      if (fresh) {
        const map = new Map(this.knowledge());
        map.set(fresh.id, fresh);
        this.knowledge.set(map);
      }
      this.editingSyncQ.set(null);
      this.snack.open(this.t('pb.v2.p.synced'), this.t('common.close'), { duration: 2200 });
    } catch {
      this.snack.open(this.t('pb.v2.p.syncFailed'), this.t('common.close'), { duration: 3000 });
    } finally {
      this.busy.set(false);
    }
  }

  setJoinCat(question: string, cat: string): void {
    this.joinCategory.set({ ...this.joinCategory(), [question]: cat });
  }

  async joinStack(s: PrepSection, q: PrepQuestionItem): Promise<void> {
    const cat = (this.joinCategory()[q.question] || q.category || '').trim();
    if (!cat) {
      this.snack.open(this.t('pb.v2.p.needCategory'), this.t('common.close'), { duration: 3000 });
      return;
    }
    this.busy.set(true);
    try {
      const std = prepJoinAnswer(q);
      const res = await this.api.importCandidates({
        entryId: this.entry().id,
        company: this.entry().companyName,
        date: this.round().scheduledDate ?? undefined,
        roundNo: this.round().order,
        roundStage: this.round().stage,
        applicationId: this.entry().jobApplicationId ?? undefined,
        items: [{
          question: q.question,
          answer: std || q.myAnswer || undefined,
          category: cat,
          difficulty: 3
        }]
      }).toPromise();
      const newId = res?.ids?.[0];
      if (newId) {
        // 把新建条目并回本地题库映射:同步区读的是这份 map,不刷新就会把
        // 刚带答案加入的题显示成「题库里还没有答案」(2026-10-21 审计 X3)。
        const fresh = await this.api.getKnowledge(newId).toPromise().catch(() => null);
        if (fresh) {
          const map = new Map(this.knowledge());
          map.set(fresh.id, fresh);
          this.knowledge.set(map);
        }
        const r = this.report();
        if (r) {
          const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
          const sec = next.sections.find(x => x.id === s.id);
          const item = sec?.questions?.find(x => x.question === q.question);
          if (item) { item.knowledgeId = newId; await this.persist(next); }
        }
        this.snack.open(this.t('pb.v2.p.joined'), this.t('common.close'), { duration: 2500 });
      } else {
        this.snack.open(this.t('pb.v2.p.alreadyIn'), this.t('common.close'), { duration: 3000 });
      }
    } catch (e) {
      // 票109:字段级校验错误(如分类超 100)透出后端具体文案,其余失败回落原通用提示。
      this.snack.open(serverFieldErrorSummary(e) ?? this.t('pb.v2.p.joinFailed'),
        this.t('common.close'), { duration: 3000 });
    } finally {
      this.busy.set(false);
    }
  }

  private async persist(next: PrepReport): Promise<void> {
    await this.api.setPrepReport(this.entry().id, this.round().id, JSON.stringify(next)).toPromise();
    this.changed.emit();
  }

  // ---------------- 导出 ----------------

  openExport(): void {
    const e = this.entry();
    const r = this.round();
    const date = new Date().toISOString().slice(0, 10);
    const data: ExportDialogData = {
      title: this.t('pb.v2.p.exportPrep'),
      subtitle: `${e.companyName} — Round ${r.order} ${r.stage}`,
      fileBase: `${e.companyName}-Round${r.order}-${this.t('pb.v2.p.fileSuffix')}-${date}`,
      storageKey: 'prep',
      toggles: [
        { key: 'flow', label: this.t('pb.v2.p.tgFlow'), defaultOn: true },
        { key: 'qa', label: this.t('pb.v2.p.tgQa'), defaultOn: true }
      ],
      buildDoc: sel => this.buildExportDoc(sel)
    };
    this.dialog.open(ExportDialogComponent, { width: '560px', maxWidth: '94vw', data });
  }

  private buildExportDoc(sel: Set<string>): ExportDoc {
    const e = this.entry();
    const r = this.round();
    const rep = this.report();
    const blocks: ExportBlock[] = [];
    for (const s of rep?.sections ?? []) {
      const isQaSection = (s.questions?.length ?? 0) > 0;
      if (isQaSection && !sel.has('qa')) continue;
      if (!isQaSection && !sel.has('flow')) continue;
      blocks.push({ kind: 'heading', text: s.title });
      for (const b of s.blocks ?? []) {
        if (b.type === 'script') {
          if (b.title) blocks.push({ kind: 'subheading', text: b.title });
          blocks.push({ kind: 'para', text: b.userText || b.text || '' });
        } else if (b.items?.length) {
          if (b.title) blocks.push({ kind: 'subheading', text: b.title });
          blocks.push({ kind: 'list', items: b.items });
        } else if (b.text) blocks.push({ kind: 'para', text: b.text });
      }
      for (const q of s.questions ?? []) {
        blocks.push(...prepQuestionExportBlocks(q, {
          linked: q.knowledgeId ? this.knowledgeAnswer(q.knowledgeId) : '',
          stackLabel: this.t('pb.v2.p.docStdStack'),
          spokenLabel: this.t('pb.v2.p.spoken'),
          technicalLabel: this.t('pb.v2.p.technical')
        }));
      }
    }
    return {
      title: this.fmt('pb.v2.p.docTitle', { company: e.companyName, n: r.order, stage: r.stage }),
      subtitle: `${e.role}${r.scheduledDate ? ' · ' + r.scheduledDate : ''}`,
      blocks
    };
  }
}

/**
 * 「＋ 加入 Tech Stack」带上的初始答案(2026-10-21 用户裁决):用户编辑版优先、
 * 其次 AI 技术版 —— 取值顺序与问答入库 bestAnswerOf 完全一致:
 * 技术版(用户→AI)→ 口语版(用户→AI)→ 我的回答。字符串形态的标准答案是 AI 稿。
 */
function prepJoinAnswer(q: PrepQuestionItem): string {
  const parts = (v: PrepQuestionItem['stdTechnical']): { ai?: string | null; user?: string | null } =>
    typeof v === 'string' ? { ai: v } : (v ?? {});
  const te = parts(q.stdTechnical);
  const sp = parts(q.stdSpoken);
  return te.user || te.ai || sp.user || sp.ai || q.myAnswer || '';
}
