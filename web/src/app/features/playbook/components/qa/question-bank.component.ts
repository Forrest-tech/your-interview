import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import {
  QaEntryDetail, QaHistoryItem, QaMastery, QaQuestion,
  qaCategoryOf, qaIsSoftSkill, qaIsStuck, qaMasteryOf
} from './qa.models';
import { QA_I18N } from './qa.i18n';
import { QaConfirmDialogComponent } from './qa-confirm-dialog.component';

type StatusFilter = 'all' | 'stuck' | 'todo' | 'practicing' | 'mastered';

interface NewQuestionForm {
  questionText: string;
  myAnswerText: string;
  category: string;
  difficulty: number;
  gotStuck: boolean;
  stuckReason: string;
}

/**
 * F1 Q&A 题库 —— 自包含组件。
 *
 * 数据:只认 entryId 这一个 input,自己调 GET /api/interviews/{id} 拿 questions,
 * 自己管筛选/排序/掌握度/编辑/删除/history,父组件只需
 * <app-question-bank [entryId]="id()"></app-question-bank>。
 *
 * 掌握度(A1):0=待练 1=练习中 2=已掌握 3=还卡壳;卡壳 = mastery==3 || gotStuck。
 * 默认排序:卡壳优先 → 待练(0) → 练习中(1) → 已掌握(2),同级内难度 desc。
 */
@Component({
  selector: 'app-question-bank',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatCardModule, MatIconModule, MatButtonModule, MatChipsModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule,
    MatSnackBarModule, MatProgressSpinnerModule, MatTooltipModule
  ],
  templateUrl: './question-bank.component.html',
  styleUrl: './question-bank.component.scss'
})
export class QuestionBankComponent {
  readonly entryId = input.required<string>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  /** i18n:先查本模块 QA_I18N,缺词再走全局 service(coordinator 并入后依然可用)。 */
  t = (key: string): string => {
    const e = QA_I18N[key];
    if (e) return e[this.i18n.lang()] ?? e.en ?? key;
    return this.i18n.t(key);
  };
  tn = (key: string, n: string | number | null | undefined): string =>
    this.t(key).replace('{n}', String(n ?? ''));

  /** 模板直接可用的纯函数(模板不能 import)。 */
  readonly catOf = qaCategoryOf;
  readonly masteryOf = qaMasteryOf;
  readonly isSoftSkill = qaIsSoftSkill;

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly questions = signal<QaQuestion[]>([]);

  readonly statusOptions: StatusFilter[] = ['all', 'stuck', 'todo', 'practicing', 'mastered'];
  readonly statusFilter = signal<StatusFilter>('all');
  readonly categoryFilter = signal<string>('all');
  readonly categoryOptions = ['Technical', 'SystemDesign', 'Behavioral', 'Coding', 'Culture', 'Other'];

  constructor() {
    // entryId 变化(或首次挂载)时重拉;load 内部只写不读的 signal,无循环。
    effect(() => { this.load(this.entryId()); });
  }

  // ---------------- 顶部聚合头 ----------------

  readonly total = computed(() => this.questions().length);

  private readonly avgDifficulty = computed(() => {
    const ds = this.questions().map(q => q.difficulty ?? 0).filter(d => d > 0);
    if (ds.length === 0) return null;
    return ds.reduce((a, b) => a + b, 0) / ds.length;
  });

  readonly avgLabel = computed(() => {
    const a = this.avgDifficulty();
    return a === null ? '–' : a.toFixed(1);
  });

  readonly stuckCount = computed(() => this.questions().filter(qaIsStuck).length);

  /** 各 category 分布条:按计数 desc,pct 相对最大值归一。 */
  readonly dist = computed(() => {
    const counts = new Map<string, number>();
    for (const q of this.questions()) {
      const c = qaCategoryOf(q);
      counts.set(c, (counts.get(c) ?? 0) + 1);
    }
    const rows = [...counts.entries()].map(([name, count]) => ({ name, count, pct: 0 }));
    const max = Math.max(1, ...rows.map(r => r.count));
    for (const r of rows) r.pct = Math.round((r.count / max) * 100);
    rows.sort((a, b) => b.count - a.count);
    return rows;
  });

  // ---------------- 筛选 + 默认排序 ----------------

  readonly visibleQuestions = computed(() => {
    const sf = this.statusFilter();
    const cf = this.categoryFilter();
    const list = this.questions().filter(q => {
      if (cf !== 'all' && qaCategoryOf(q) !== cf) return false;
      switch (sf) {
        case 'all': return true;
        case 'stuck': return qaIsStuck(q);
        // "待练"不含已标卡壳的题 —— 它们归"卡壳"管,避免两边重复出现。
        case 'todo': return qaMasteryOf(q) === 0 && !qaIsStuck(q);
        case 'practicing': return qaMasteryOf(q) === 1;
        case 'mastered': return qaMasteryOf(q) === 2;
        default: return true;
      }
    });
    return [...list].sort((a, b) => {
      const ra = this.sortRank(a);
      const rb = this.sortRank(b);
      if (ra !== rb) return ra - rb;
      const dd = (b.difficulty ?? 0) - (a.difficulty ?? 0);
      if (dd !== 0) return dd;
      return (a.sequence ?? 0) - (b.sequence ?? 0);
    });
  });

  /** 排序秩:卡壳 0 → 待练 1 → 练习中 2 → 已掌握 3。 */
  private sortRank(q: QaQuestion): number {
    if (qaIsStuck(q)) return 0;
    return qaMasteryOf(q) + 1;
  }

  // ---------------- 数据加载 ----------------

  private load(id: string): void {
    if (!id) { this.loading.set(false); return; }
    this.loading.set(true);
    this.error.set(null);
    this.api.get<QaEntryDetail>(`/api/interviews/${id}`).subscribe({
      next: d => {
        this.questions.set(d?.questions ?? []);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      }
    });
  }

  retry(): void {
    this.load(this.entryId());
  }

  // ---------------- 掌握度徽章:点击循环 0→1→2→3→0 ----------------

  readonly masteryBusy = signal<string | null>(null);

  cycleMastery(q: QaQuestion): void {
    const next = ((qaMasteryOf(q) + 1) % 4) as QaMastery;
    this.masteryBusy.set(q.id);
    this.api.put<void>(`/api/interviews/${this.entryId()}/questions/${q.id}/mastery`, { mastery: next })
      .subscribe({
        next: () => {
          this.masteryBusy.set(null);
          this.patchQuestion(q.id, { mastery: next });
          this.snack.open(this.t('pb.qa.masterySaved'), this.t('common.close'), { duration: 2000 });
        },
        error: (e: Error) => {
          this.masteryBusy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
  }

  private patchQuestion(id: string, patch: Partial<QaQuestion>): void {
    this.questions.update(list => list.map(q => q.id === id ? { ...q, ...patch } : q));
  }

  // ---------------- 我的回答:行内编辑 ----------------

  readonly editingId = signal<string | null>(null);
  readonly answerDrafts: Record<string, string> = {};
  readonly savingAnswer = signal(false);

  startEditAnswer(q: QaQuestion): void {
    this.answerDrafts[q.id] = q.myAnswerText ?? '';
    this.editingId.set(q.id);
  }

  cancelEditAnswer(): void {
    this.editingId.set(null);
  }

  saveAnswer(q: QaQuestion): void {
    const draft = this.answerDrafts[q.id] ?? '';
    this.savingAnswer.set(true);
    // PUT 契约(UpdateQuestionBody):全量字段,按当前值回填,只换 myAnswerText。
    const body = {
      questionText: q.questionText,
      myAnswerText: draft.trim() ? draft : null,
      category: qaCategoryOf(q),
      difficulty: q.difficulty ?? 3,
      assessment: q.assessment ?? null,
      gotStuck: q.gotStuck ?? false,
      stuckReason: q.stuckReason ?? null,
      recommendedAnswer: q.recommendedAnswer ?? null
    };
    this.api.put<void>(`/api/interviews/${this.entryId()}/questions/${q.id}`, body).subscribe({
      next: () => {
        this.savingAnswer.set(false);
        this.editingId.set(null);
        this.patchQuestion(q.id, { myAnswerText: draft.trim() ? draft : null });
        this.snack.open(this.t('pb.qa.answerSaved'), this.t('common.close'), { duration: 2000 });
      },
      error: (e: Error) => {
        this.savingAnswer.set(false);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  // ---------------- 历史回答折叠区(A4) ----------------

  readonly historyOpen = signal<string | null>(null);
  readonly historyLoading = signal(false);
  readonly historyCache = signal<Record<string, QaHistoryItem[]>>({});
  readonly historyErr = signal<Record<string, string>>({});

  toggleHistory(q: QaQuestion): void {
    if (this.historyOpen() === q.id) {
      this.historyOpen.set(null);
      return;
    }
    this.historyOpen.set(q.id);
    if (this.historyCache()[q.id]) return; // 命中缓存(含空数组)不再请求
    this.historyLoading.set(true);
    this.api.get<QaHistoryItem[]>(`/api/interviews/${this.entryId()}/questions/${q.id}/history`)
      .subscribe({
        next: items => {
          this.historyLoading.set(false);
          this.historyCache.update(c => ({ ...c, [q.id]: items ?? [] }));
        },
        error: (e: Error) => {
          this.historyLoading.set(false);
          this.historyErr.update(m => ({ ...m, [q.id]: e.message }));
        }
      });
  }

  historyItems(qid: string): QaHistoryItem[] {
    return this.historyCache()[qid] ?? [];
  }

  historyError(qid: string): string | null {
    return this.historyErr()[qid] ?? null;
  }

  // ---------------- 删除:MatDialog 二次确认(B7,替代原生 confirm) ----------------

  removeQuestion(q: QaQuestion): void {
    const raw = q.questionText.trim();
    const snippet = raw.length > 48 ? raw.slice(0, 48) + '…' : raw;
    const ref = this.dialog.open(QaConfirmDialogComponent, {
      width: '440px',
      data: {
        title: this.t('pb.qa.confirmDeleteTitle'),
        body: this.t('pb.qa.confirmDeleteBody').replace('{q}', snippet),
        confirmText: this.t('pb.qa.delete'),
        cancelText: this.t('common.cancel'),
        danger: true
      }
    });
    ref.afterClosed().subscribe(ok => {
      if (!ok) return;
      this.api.delete<void>(`/api/interviews/${this.entryId()}/questions/${q.id}`).subscribe({
        next: () => {
          this.questions.update(list => list.filter(x => x.id !== q.id));
          this.snack.open(this.t('pb.qa.deleted'), this.t('common.close'), { duration: 2000 });
        },
        error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
      });
    });
  }

  // ---------------- 新增题目表单 ----------------

  readonly showAdd = signal(false);
  readonly adding = signal(false);
  newQ: NewQuestionForm = this.emptyNewQ();

  private emptyNewQ(): NewQuestionForm {
    return {
      questionText: '', myAnswerText: '', category: 'Technical',
      difficulty: 3, gotStuck: false, stuckReason: ''
    };
  }

  catLabel(c: string): string {
    return QA_I18N[`pb.qa.cat.${c}`] ? this.t(`pb.qa.cat.${c}`) : c;
  }

  addQuestion(): void {
    const text = this.newQ.questionText.trim();
    if (!text) {
      this.snack.open(this.t('pb.qa.qRequired'), this.t('common.close'), { duration: 3000 });
      return;
    }
    this.adding.set(true);
    const f = this.newQ;
    // POST 契约(AddQuestionBody):questionText/myAnswerText/category/difficulty/assessment。
    this.api.post<{ id: string }>(`/api/interviews/${this.entryId()}/questions`, {
      questionText: text,
      myAnswerText: f.myAnswerText.trim() || null,
      category: f.category || 'Technical',
      difficulty: f.difficulty || 3,
      assessment: null
    }).subscribe({
      next: res => {
        // POST 不收 gotStuck/stuckReason,填了就用 PUT 补一笔;失败也不挡主流程。
        if ((f.gotStuck || f.stuckReason.trim()) && res?.id) {
          this.api.put<void>(`/api/interviews/${this.entryId()}/questions/${res.id}`, {
            questionText: text,
            myAnswerText: f.myAnswerText.trim() || null,
            category: f.category || 'Technical',
            difficulty: f.difficulty || 3,
            assessment: null,
            gotStuck: f.gotStuck,
            stuckReason: f.stuckReason.trim() || null,
            recommendedAnswer: null
          }).subscribe({ next: () => this.afterAdd(), error: () => this.afterAdd() });
        } else {
          this.afterAdd();
        }
      },
      error: (e: Error) => {
        this.adding.set(false);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  private afterAdd(): void {
    this.adding.set(false);
    this.newQ = this.emptyNewQ();
    this.showAdd.set(false);
    this.snack.open(this.t('pb.qa.added'), this.t('common.close'), { duration: 2000 });
    this.load(this.entryId());
  }

  // ---------------- 展示小工具 ----------------

  /** 难度星:3 → ★★★☆☆。 */
  stars(d: number | null | undefined): string {
    const n = Math.max(0, Math.min(5, Math.round(d ?? 0)));
    return '★'.repeat(n) + '☆'.repeat(5 - n);
  }
}
