import {
  Component, EventEmitter, Output, computed, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewDetail, InterviewQuestion, InterviewRound, KnowledgeItem
} from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { serverFieldErrorSummary } from '../../../../core/api/validation-errors.helpers';
import { parseQuestionReview } from '../v2.models';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { makeV2Fmt, makeV2T } from '../v2.helpers';

interface Candidate {
  question: InterviewQuestion;
  checked: boolean;
  category: string;
  alreadyIn: boolean;
}

const BEHAVIORAL_HINTS = [
  'tell me about', 'describe a time', 'conflict', 'challenge you faced',
  'why do you', 'why should we', 'strength', 'weakness', 'yourself',
  '介绍一下你自己', '为什么', '冲突', '困难', '优点', '缺点', '团队合作'
];

/**
 * 问答入库(2026-10-20 定稿):本轮抽取出的问答对 → 勾选入 Tech Stack。
 * 技术题默认勾选、行为题默认不勾(可改);逐条可选分类;已入库的按
 * 来源+题面判重标记,不重复导入。导入自动带来源标签(公司 · Round · 日期)。
 */
@Component({
  selector: 'app-qa-import',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatCheckboxModule],
  template: `
    <div class="qa-import">
      <div class="qi-head">
        <div>
          <div class="qi-title">{{ t('pb.v2.q.title') }}</div>
          <p class="qi-sub">{{ t('pb.v2.q.sub') }}</p>
        </div>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="extract()" [disabled]="busy()">{{ t('pb.v2.rec.extractQa') }}</button>
        <button mat-raised-button color="primary" (click)="importSelected()"
                [disabled]="busy() || !selectedCount()">
          {{ fmt('pb.v2.q.importSelected', { n: selectedCount() }) }}
        </button>
      </div>
      @if (notice()) { <p class="notice">{{ notice() }}</p> }

      @if (!candidates().length) {
        <p class="empty">{{ t('pb.v2.q.empty') }}</p>
      }
      @for (c of candidates(); track c.question.id) {
        <div class="cand" data-testid="qa-candidate">
          <mat-checkbox [checked]="c.checked" [disabled]="c.alreadyIn"
                        (change)="toggle(c, $event.checked)"></mat-checkbox>
          <div class="cand-main">
            <div class="cand-q">{{ c.question.questionText }}
              @if (scoreOf(c.question) != null) { <span class="cand-score">{{ fmt('pb.v2.rec.qScore', { n: scoreOf(c.question)! }) }}</span> }
              @if (c.alreadyIn) { <span class="in-tag">{{ t('pb.v2.p.inStack') }}</span> }
            </div>
            @if (c.question.askedBy) { <div class="cand-by">{{ fmt('pb.v2.rec.askedBy', { name: c.question.askedBy }) }}</div> }
            @if (c.question.myAnswerText) {
              <div class="cand-a">{{ c.question.myAnswerText }}</div>
            }
            <label class="cand-cat">{{ t('pb.v2.p.category') }}
              <input class="fld sm" list="qa-topics" [ngModel]="c.category"
                     (ngModelChange)="c.category = $event" [disabled]="c.alreadyIn">
            </label>
          </div>
        </div>
      }
      <datalist id="qa-topics">
        @for (tp of topics(); track tp) { <option [value]="tp"></option> }
      </datalist>
    </div>
  `,
  styles: [`
    .qa-import { padding: 4px 2px; }
    .qi-head { display: flex; align-items: flex-start; gap: 10px; flex-wrap: wrap; }
    .qi-title { font-size: 16px; font-weight: 600; }
    .qi-sub { font-size: 13px; color: var(--yi-text-muted, #5b6270); margin: 3px 0 0; max-width: 560px; }
    .spacer { flex: 1; }
    .notice { font-size: 13px; color: var(--yi-text-muted, #5b6270); }
    .empty { font-size: 14px; color: var(--yi-text-muted, #5b6270); padding: 12px 0; }
    .cand { display: flex; gap: 6px; border: 1px solid var(--yi-border, #e1e4ec);
            border-radius: 10px; padding: 10px 12px; margin: 9px 0; }
    .cand-main { flex: 1; }
    .cand-q { font-size: 14px; font-weight: 600; }
    .cand-score { font-size: 12px; font-weight: 700; color: var(--yi-accent, #2e3a8c); margin-left: 8px; }
    .in-tag { font-size: 12px; font-weight: 700; color: #1b7f4b; border: 1px solid #e2f1e8;
              border-radius: 999px; padding: 1px 7px; margin-left: 8px; }
    .cand-by { font-size: 13px; color: var(--yi-text-muted, #5b6270); margin-top: 2px; }
    .cand-a { font-size: 14px; line-height: 1.55; color: var(--yi-text-muted, #5b6270); margin-top: 4px;
              display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
    .cand-cat { display: inline-flex; align-items: center; gap: 6px; font-size: 13px;
                color: var(--yi-text-muted, #5b6270); margin-top: 7px; }
    .fld.sm { border: 1px solid var(--yi-border, #e1e4ec); border-radius: 6px; padding: 4px 8px;
              font-size: 14px; background: transparent; color: inherit; }
  `]
})
export class QaImportComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  readonly knowledgeItems = signal<KnowledgeItem[]>([]);

  readonly questions = computed(() =>
    (this.entry().questions ?? [])
      .filter(q => q.roundId === this.round().id)
      .sort((a, b) => a.sequence - b.sequence));

  readonly topics = computed(() =>
    [...new Set(this.knowledgeItems().map(k => k.topic).filter(Boolean))].sort());

  readonly candidates = computed<Candidate[]>(() => {
    const items = this.knowledgeItems();
    return this.questions().map(q => {
      const already = items.some(k =>
        (k.sourceInterviewEntryId === this.entry().id || !k.sourceInterviewEntryId) &&
        k.question?.trim().toLowerCase() === q.questionText.trim().toLowerCase() &&
        (k.sourceRoundNo == null || k.sourceRoundNo === this.round().order));
      return {
        question: q,
        checked: !already && !isBehavioral(q),
        category: q.category || guessCategory(q),
        alreadyIn: already
      };
    });
  });

  constructor() {
    this.api.listKnowledge().subscribe({
      next: items => this.knowledgeItems.set(items),
      error: () => undefined
    });
  }

  scoreOf(q: InterviewQuestion): number | null {
    return parseQuestionReview(q.reviewJson)?.overall ?? null;
  }

  selectedCount(): number {
    return this.candidates().filter(c => c.checked && !c.alreadyIn).length;
  }

  toggle(c: Candidate, on: boolean): void { c.checked = on; }

  async extract(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.extractQuestions(this.entry().id, this.round().id).toPromise();
      this.notice.set(r && r.created > 0
        ? this.fmt('pb.v2.q.extracted', { n: r.created }) : this.t('pb.v2.q.extractNone'));
      this.changed.emit();
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.rec.extractFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.rec.unknownError') }));
    } finally {
      this.busy.set(false);
    }
  }

  async importSelected(): Promise<void> {
    const sel = this.candidates().filter(c => c.checked && !c.alreadyIn);
    if (!sel.length) return;
    this.busy.set(true);
    try {
      const res = await this.api.importCandidates({
        entryId: this.entry().id,
        company: this.entry().companyName,
        date: this.round().scheduledDate ?? undefined,
        roundNo: this.round().order,
        roundStage: this.round().stage,
        applicationId: this.entry().jobApplicationId ?? undefined,
        items: sel.map(c => ({
          question: c.question.questionText,
          answer: bestAnswerOf(c.question),
          category: c.category || 'General',
          difficulty: c.question.difficulty ?? 3,
          clientKey: c.question.id
        }))
      }).toPromise();
      this.snack.open(
        this.fmt('pb.v2.q.imported', { n: res?.created ?? 0 })
          + (res?.skipped ? this.fmt('pb.v2.q.importedSkipped', { n: res.skipped }) : ''),
        this.t('common.close'), { duration: 3000 });
      const items = await this.api.listKnowledge().toPromise();
      if (items) this.knowledgeItems.set(items);
      this.changed.emit();
    } catch (e) {
      // 票109:字段级校验错误(如分类超 100)透出后端具体文案,其余失败回落原通用提示。
      this.snack.open(serverFieldErrorSummary(e) ?? this.t('pb.v2.q.importFailed'),
        this.t('common.close'), { duration: 3000 });
    } finally {
      this.busy.set(false);
    }
  }
}

function isBehavioral(q: InterviewQuestion): boolean {
  if (q.category && /behavior|hr|行为/i.test(q.category)) return true;
  const t = q.questionText.toLowerCase();
  return BEHAVIORAL_HINTS.some(h => t.includes(h));
}

function guessCategory(q: InterviewQuestion): string {
  return q.category || 'General';
}

function bestAnswerOf(q: InterviewQuestion): string | undefined {
  const rv = parseQuestionReview(q.reviewJson);
  return rv?.stdTechnical?.user || rv?.stdTechnical?.ai
    || rv?.stdSpoken?.user || rv?.stdSpoken?.ai
    || q.recommendedAnswer || q.myAnswerText || undefined;
}
