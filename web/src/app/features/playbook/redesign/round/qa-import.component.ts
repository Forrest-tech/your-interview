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
import { parseQuestionReview } from '../v2.models';

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
          <div class="qi-title">把本轮问答沉淀进 Tech Stack</div>
          <p class="qi-sub">技术题默认勾选、行为题默认不勾;分类可逐条改。导入后在备考报告与 Tech Stack 里都是同一条记录。</p>
        </div>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="extract()" [disabled]="busy()">从转写抽取问答</button>
        <button mat-raised-button color="primary" (click)="importSelected()"
                [disabled]="busy() || !selectedCount()">
          入库所选({{ selectedCount() }})
        </button>
      </div>
      @if (notice()) { <p class="notice">{{ notice() }}</p> }

      @if (!candidates().length) {
        <p class="empty">本轮还没有题目。先在「面试实录」里粘贴/生成转写,再点「从转写抽取问答」——或者用「＋ 添加材料」把问答记成转写文本。</p>
      }
      @for (c of candidates(); track c.question.id) {
        <div class="cand" data-testid="qa-candidate">
          <mat-checkbox [checked]="c.checked" [disabled]="c.alreadyIn"
                        (change)="toggle(c, $event.checked)"></mat-checkbox>
          <div class="cand-main">
            <div class="cand-q">{{ c.question.questionText }}
              @if (scoreOf(c.question) != null) { <span class="cand-score">本题 {{ scoreOf(c.question) }} 分</span> }
              @if (c.alreadyIn) { <span class="in-tag">已在 Tech Stack</span> }
            </div>
            @if (c.question.askedBy) { <div class="cand-by">提问人:{{ c.question.askedBy }}</div> }
            @if (c.question.myAnswerText) {
              <div class="cand-a">{{ c.question.myAnswerText }}</div>
            }
            <label class="cand-cat">分类
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
    .qi-title { font-size: 14.5px; font-weight: 700; }
    .qi-sub { font-size: 12.5px; color: var(--yi-text-muted, #888); margin: 3px 0 0; max-width: 560px; }
    .spacer { flex: 1; }
    .notice { font-size: 12.5px; color: var(--yi-text-muted, #777); }
    .empty { font-size: 13px; color: var(--yi-text-muted, #888); padding: 12px 0; }
    .cand { display: flex; gap: 6px; border: 1px solid var(--yi-border, #e0e0e0);
            border-radius: 10px; padding: 10px 12px; margin: 9px 0; }
    .cand-main { flex: 1; }
    .cand-q { font-size: 14px; font-weight: 600; }
    .cand-score { font-size: 11.5px; font-weight: 700; color: var(--yi-accent, #3f51b5); margin-left: 8px; }
    .in-tag { font-size: 10.5px; font-weight: 700; color: #2e7d32; border: 1px solid #a5d6a7;
              border-radius: 999px; padding: 1px 7px; margin-left: 8px; }
    .cand-by { font-size: 12px; color: var(--yi-text-muted, #888); margin-top: 2px; }
    .cand-a { font-size: 12.5px; color: var(--yi-text-muted, #666); margin-top: 4px;
              display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; }
    .cand-cat { display: inline-flex; align-items: center; gap: 6px; font-size: 12px;
                color: var(--yi-text-muted, #777); margin-top: 7px; }
    .fld.sm { border: 1px solid var(--yi-border, #ccc); border-radius: 7px; padding: 4px 8px;
              font-size: 12.5px; background: transparent; color: inherit; }
  `]
})
export class QaImportComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);

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
        ? `已抽出 ${r.created} 道题。` : '没有抽到新题(本轮已有题目或转写为空)。');
      this.changed.emit();
    } catch (e) {
      this.notice.set('抽取失败:' + (e instanceof Error ? e.message : '未知错误'));
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
        `已入库 ${res?.created ?? 0} 道${res?.skipped ? `,跳过重复 ${res.skipped} 道` : ''}`,
        '关闭', { duration: 3000 });
      const items = await this.api.listKnowledge().toPromise();
      if (items) this.knowledgeItems.set(items);
      this.changed.emit();
    } catch {
      this.snack.open('入库失败', '关闭', { duration: 3000 });
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
