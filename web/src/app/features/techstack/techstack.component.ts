import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatTabsModule } from '@angular/material/tabs';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { KnowledgeItem, KnowledgeTopic, KnowledgeStats, MasteryLevel, Paged } from '../../core/models/api.models';
import { KnowledgeDialogComponent, KnowledgeForm } from './knowledge-dialog.component';

/** 熟练度等级顺序 —— 用于展示顺序、进度和"提升一级"的目标值。 */
const MASTERY_ORDER: MasteryLevel[] = ['New', 'Learning', 'Familiar', 'Proficient', 'Mastered'];

const MASTERY_LABELS: Record<MasteryLevel, string> = {
  New: '未接触',
  Learning: '学习中',
  Familiar: '熟悉',
  Proficient: '熟练',
  Mastered: '精通'
};

/** 复习结果档位 —— 必须与后端 RecordReviewCommandValidator 的允许值一致。 */
type ReviewResultLabel = 'Again' | 'Hard' | 'Good' | 'Easy';

const REVIEW_LABELS: Record<ReviewResultLabel, string> = {
  Again: '忘了',
  Hard: '很吃力',
  Good: '记住了',
  Easy: '太简单'
};

/**
 * 四档映射到后端的主观分。
 * 后端 confidenceAfter 是 int?(1-5,对应 SM-2 的 q 值),不是 0-1 小数 ——
 * 传 0.75 会被反序列化成 400。这里按后端 RecordReview 的 q 映射给同一套值。
 */
const CONFIDENCE_FOR: Record<ReviewResultLabel, number> = {
  Again: 1,
  Hard: 3,
  Good: 4,
  Easy: 5
};

/** 后端 POST /api/knowledge/{id}/review 的回包形状(ReviewOutcomeDto)。 */
interface ReviewOutcome {
  nextReviewAt: string | null;
  intervalDays: number;
  easinessFactor: number;
  mastery: string;
  repetitionStreak: number;
}

/** 9 块详情里正文区的定义。用数据驱动模板,新增一块不用改 HTML。 */
interface DetailBlock {
  /**
   * 故意用 string 而不是 keyof KnowledgeItem ——
   * keyPoints / commonMistakes 是"展示用虚拟键",后端并不存在同名字段
   * (真实字段是 keyPointsJson / commonMistakesJson)。用 keyof 会编译不过。
   */
  key: string;
  label: string;
  icon: string;
  hint: string;
}

/**
 * 九块知识卡片的正文区块。
 * 顺序刻意按"从直觉到应试"排列:先一句话直觉建立印象,最后才是面试话术。
 * 这个顺序就是复习时的阅读顺序。
 */
const DETAIL_BLOCKS: DetailBlock[] = [
  // 后端存的是 title/question/conceptExplanation + 两个 JSON 数组,
  // 不是八个独立正文字段。这里把 JSON 拆成"要点/常见坑"两块来展示,
  // 复习时的阅读顺序:题干 → 概念 → 要点 → 常见坑。
  { key: 'question', label: '面试题干', icon: 'help_outline', hint: '被问到时的原题' },
  { key: 'conceptExplanation', label: '概念讲解', icon: 'menu_book', hint: '原理与机制' },
  { key: 'keyPoints', label: '关键要点', icon: 'lightbulb_outline', hint: '答题必须覆盖的点' },
  { key: 'commonMistakes', label: '常见误区', icon: 'warning_amber', hint: '容易被追问打穿的地方' }
];

/**
 * 技术栈知识库。
 *
 * 主从布局(左列表 + 右详情)而不是卡片瀑布流:
 * 复习是"逐个过条目"的线性动作,左列表提供稳定的位置感,
 * 卡片流一滚动就丢失上下文,不适合背题。
 *
 * 熟练度提升/复习记录两个动作都只发一个 POST 就刷新,
 * 不做乐观更新 —— 服务端会算 nextReviewAt,前端猜错反而害用户。
 */
@Component({
  selector: 'app-techstack',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatIconModule, MatButtonModule,
    MatChipsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    MatDialogModule, MatTooltipModule, MatSnackBarModule, MatMenuModule
  ],
  templateUrl: './techstack.component.html',
  styleUrl: './techstack.component.scss'
})
export class TechStackComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly masteryOrder = MASTERY_ORDER;
  readonly blocks = DETAIL_BLOCKS;

  /** 复习结果四档,直接对齐后端 SM-2 的 Again/Hard/Good/Easy。 */
  readonly reviewChoices: { value: ReviewResultLabel; label: string }[] = [
    { value: 'Again', label: REVIEW_LABELS.Again },
    { value: 'Hard', label: REVIEW_LABELS.Hard },
    { value: 'Good', label: REVIEW_LABELS.Good },
    { value: 'Easy', label: REVIEW_LABELS.Easy }
  ];

  // ------------------------------ 今日复习会话 ------------------------------

  /** 是否进入"今日复习"专注模式:进入后隐藏列表/筛选,改走闪卡队列。 */
  readonly reviewMode = signal(false);
  /** 待复习队列(取自 dueOnly 列表,已按优先级排好)。 */
  readonly reviewQueue = signal<KnowledgeItem[]>([]);
  /** 当前卡片下标。 */
  readonly reviewIndex = signal(0);
  /** 当前卡片是否已翻开答案。 */
  readonly reviewRevealed = signal(false);
  /** 拉取队列 / 翻答案时的加载态。 */
  readonly reviewLoading = signal(false);
  /** 本轮是否全部复习完。 */
  readonly reviewDone = signal(false);
  /** 本轮已评分的卡片数。 */
  readonly reviewReviewed = signal(0);
  /** 翻面时拉取的全量条目(列表 DTO 不含答案内容,需 GET 详情)。 */
  readonly reviewCurrent = signal<KnowledgeItem | null>(null);

  /** 当前卡片(列表条目,含题干/主题/熟练度,不含答案正文)。 */
  readonly currentCard = computed<KnowledgeItem | null>(
    () => this.reviewQueue()[this.reviewIndex()] ?? null
  );

  /** 进度文案,如 "3 / 7"。 */
  readonly reviewProgress = computed(() => {
    const total = this.reviewQueue().length;
    return total === 0 ? '0 / 0' : `${this.reviewIndex() + 1} / ${total}`;
  });

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly items = signal<KnowledgeItem[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(30);
  readonly totalPages = signal(1);

  readonly search = signal('');
  readonly topic = signal<string | null>(null);
  readonly mastery = signal<MasteryLevel | null>(null);

  readonly selected = signal<KnowledgeItem | null>(null);

  /** 主题列表:接口优先,404/失败则从当前页条目里推导 —— 至少让筛选可用。 */
  readonly topics = signal<string[]>([]);
  readonly topicSource = signal<'api' | 'derived'>('api');

  /** 熟练度分布:接口失败为 null,此时整条分布栏隐藏而不是显示全 0(全 0 是误导)。 */
  readonly buckets = signal<KnowledgeStats | null>(null);

  readonly acting = signal(false);

  /**
   * 熟练度分布行。
   * /stats 给的是 byMastery 数组,这里转成固定五档的行 —— 顺序固定,
   * 用户每次看的位置一致,比按后端返回顺序渲染更容易形成记忆。
   */
  readonly bucketRows = computed(() => {
    const b = this.buckets();
    if (!b) return [];
    const m = new Map<string, number>();
    for (const x of b.byMastery ?? []) m.set(x.mastery, x.count);

    const rows = [
      { level: 'New' as MasteryLevel, count: m.get('New') ?? 0, pct: 0 },
      { level: 'Learning' as MasteryLevel, count: m.get('Learning') ?? 0, pct: 0 },
      { level: 'Familiar' as MasteryLevel, count: m.get('Familiar') ?? 0, pct: 0 },
      { level: 'Proficient' as MasteryLevel, count: m.get('Proficient') ?? 0, pct: 0 },
      { level: 'Mastered' as MasteryLevel, count: m.get('Mastered') ?? 0, pct: 0 }
    ];
    const denom = b.totalItems > 0 ? b.totalItems : rows.reduce((s, r) => s + r.count, 0);
    return rows.map((r) => ({
      ...r,
      label: MASTERY_LABELS[r.level],
      pct: denom > 0 ? Math.round((r.count / denom) * 100) : 0
    }));
  });

  ngOnInit(): void {
    this.load();
    this.loadBuckets();
  }

  // ------------------------------ 加载 ------------------------------

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    // 列表路由是 /api/knowledge(不是 /items),筛选参数名是 mastery(不是 masteryLevel)。
    // 最初按直觉写的名字全 404 —— 前端对后端契约不能"看起来合理"就写。
    this.api.get<Paged<KnowledgeItem>>('/api/knowledge', {
      page: this.page(),
      pageSize: this.pageSize(),
      topic: this.topic(),
      mastery: this.mastery(),
      search: this.search().trim()
    }).subscribe({
      next: (r) => {
        const list = r.items ?? [];
        this.items.set(list);
        this.total.set(r.total ?? 0);
        this.totalPages.set(r.totalPages ?? 1);
        this.loading.set(false);

        // 从结果里推导主题,作为接口不可用时的兜底(合并而不是覆盖,避免筛选后选项变少)
        this.deriveTopics(list);

        // 详情面板保持选中项;若该项已不在列表(被筛掉/删掉),退而选第一条
        const cur = this.selected();
        if (!cur || !list.some((i) => i.id === cur.id)) {
          this.selected.set(list.length > 0 ? list[0] : null);
        }
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.items.set([]);
        this.loading.set(false);
      }
    });
  }

  /** 主题列表是可选增强:后端没这个接口(404)或出错都降级,不让页面挂掉。 */
  loadTopics(): void {
    this.api.get<KnowledgeTopic[]>('/api/knowledge/topics')
      .pipe(catchError(() => of(null)))
      .subscribe((list) => {
        if (list && Array.isArray(list) && list.length > 0) {
          // 后端返回 {topic,total,...},这里只取名字喂给筛选器
          this.topics.set(list.map((t) => t.topic).filter(Boolean).sort());
          this.topicSource.set('api');
        } else if (this.topics().length === 0) {
          this.topicSource.set('derived');
        }
      });
  }

  loadBuckets(): void {
    // 没有独立的 /mastery 端点 —— 分布数据在 /stats 的 byMastery 里。
    // 顺手把 totalItems 也拿来做分母,顶部统计条就不用再发一次请求。
    this.api.get<KnowledgeStats>('/api/knowledge/stats')
      .pipe(catchError(() => of(null)))
      .subscribe((st) => {
        if (!st) return;
        // 直接存原始 stats —— 分布行由 bucketRows 从 byMastery 推导,
        // 不在这里折叠成另一套形状(两套形状必然有一处忘记同步)。
        this.buckets.set(st);
      });
  }

  reload(): void {
    this.load();
    this.loadBuckets();
  }

  private deriveTopics(list: KnowledgeItem[]): void {
    if (this.topicSource() === 'api' && this.topics().length > 0) return;
    const set = new Set(this.topics());
    for (const it of list) if (it.topic) set.add(it.topic);
    if (set.size > 0) {
      this.topics.set([...set].sort());
      if (this.topicSource() !== 'api') this.topicSource.set('derived');
    }
  }

  // ------------------------------ 筛选 ------------------------------

  onSearchInput(v: string): void {
    this.search.set(v);
  }

  onSearchEnter(): void {
    this.page.set(1);
    this.load();
  }

  selectTopic(t: string | null): void {
    this.topic.set(t);
    this.page.set(1);
    this.load();
  }

  toggleMastery(m: MasteryLevel): void {
    this.mastery.set(this.mastery() === m ? null : m);
    this.page.set(1);
    this.load();
  }

  clearFilters(): void {
    this.search.set('');
    this.topic.set(null);
    this.mastery.set(null);
    this.page.set(1);
    this.load();
  }

  hasFilters(): boolean {
    return this.search().length > 0 || this.topic() !== null || this.mastery() !== null;
  }

  goPage(p: number): void {
    if (p < 1 || p > this.totalPages() || p === this.page()) return;
    this.page.set(p);
    this.load();
  }

  // ------------------------------ 主从交互 ------------------------------

  select(item: KnowledgeItem): void {
    this.selected.set(item);
  }

  isSelected(item: KnowledgeItem): boolean {
    return this.selected()?.id === item.id;
  }

  openCreate(): void {
    const ref = this.dialog.open(KnowledgeDialogComponent, { autoFocus: 'first-tabbable' });
    // 对话框返回的已经是后端契约形状(title/topic/...Json),这里不再二次加工
    ref.afterClosed().subscribe((payload?: Record<string, unknown> | null) => {
      if (!payload) return;
      this.api.post<KnowledgeItem>('/api/knowledge', payload).subscribe({
        next: () => {
          this.notify('已新增条目');
          this.page.set(1);
          this.reload();
          this.loadTopics();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  // ------------------------------ 复习动作 ------------------------------

  /** 提升一级熟练度。confidence 随用户自评传,默认给一个 0.7 的中庸值。 */
  promote(item: KnowledgeItem): void {
    const idx = MASTERY_ORDER.indexOf(item.mastery);
    if (idx < 0 || idx >= MASTERY_ORDER.length - 1) {
      this.notify('已经是最高熟练度,无需再提升');
      return;
    }
    const next = MASTERY_ORDER[idx + 1];
    const ok = confirm(
      `把「${item.title}」从「${MASTERY_LABELS[item.mastery]}」提升到「${MASTERY_LABELS[next]}」?`
    );
    if (!ok) return;

    this.acting.set(true);
    this.api.post<KnowledgeItem>(`/api/knowledge/${item.id}/mastery`, {
      level: next,
      confidence: 0.7
    }).subscribe({
      next: (updated) => {
        this.acting.set(false);
        this.notify(`已提升为「${MASTERY_LABELS[next]}」`);
        this.applyUpdate(updated, item.id);
        this.loadBuckets();
        // 主题列表可能因为新条目而需要刷新,顺手重拉
        this.loadTopics();
      },
      error: (e: Error) => {
        this.acting.set(false);
        this.notify(e.message, true);
      }
    });
  }

  /**
   * 记录一次复习。
   *
   * 后端 RecordReviewCommand 的 Result 是【必填】且只接受 Again/Hard/Good/Easy
   * (SM-2 算法靠它算 easiness factor 与下次间隔)。缺了它整条请求会被 400 拒掉,
   * 所以这里必须带上。result 直接决定 SM-2 的推进档位。
   *
   * ⚠️ 两个已修正的契约陷阱:
   * 1) confidenceAfter 后端是 int?(1-5 主观分),不是 0-1 小数 —— 传 0.75 会 400。
   * 2) 这个端点返回 ReviewOutcomeDto({nextReviewAt, intervalDays, easinessFactor,
   *    mastery, repetitionStreak}),不是 KnowledgeItem。之前按 KnowledgeItem
   *    处理并丢给 applyUpdate 会把回包字段硬塞进列表条目,等于把数据写坏。
   */
  review(item: KnowledgeItem, result: ReviewResultLabel = 'Good'): void {
    this.acting.set(true);
    this.api
      .post<ReviewOutcome>(`/api/knowledge/${item.id}/review`, {
        result,
        confidenceAfter: CONFIDENCE_FOR[result]
      })
      .subscribe({
        next: (outcome) => {
          this.acting.set(false);
          this.notify(`已记录复习(${REVIEW_LABELS[result]}),下次 ${this.fmtDate(outcome.nextReviewAt)}`);
          this.applyReviewOutcome(item.id, outcome);
        },
        error: (e: Error) => {
          this.acting.set(false);
          this.notify(e.message, true);
        }
      });
  }

  /** 用复习回包原地更新条目:只覆盖排期与掌握度相关字段,不动内容字段。 */
  private applyReviewOutcome(id: string, o: ReviewOutcome): void {
    this.items.update((list) =>
      list.map((x) =>
        x.id === id
          ? {
              ...x,
              reviewCount: (x.reviewCount ?? 0) + 1,
              nextReviewAt: o.nextReviewAt ?? x.nextReviewAt,
              mastery: (o.mastery as KnowledgeItem['mastery']) ?? x.mastery,
              easinessFactor: o.easinessFactor ?? x.easinessFactor,
              repetitionStreak: o.repetitionStreak ?? x.repetitionStreak
            }
          : x
      )
    );
  }

  private fmtDate(v: string | null | undefined): string {
    if (!v) return '未排期';
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? '未排期' : d.toLocaleDateString('zh-CN');
  }

  // ------------------------------ 今日复习会话逻辑 ------------------------------

  /**
   * 进入"今日复习"专注模式。
   *
   * 取 dueOnly 列表(后端已按"待复习优先 → 重要度 → 熟练度弱优先 → 最近"排好),
   * 一次性拉满( pageSize 200)避免翻页打断节奏。队列空则提示并退出。
   */
  startReview(): void {
    if (this.reviewMode()) return;
    this.reviewLoading.set(true);
    this.api.get<Paged<KnowledgeItem>>('/api/knowledge', {
      page: 1, pageSize: 200, dueOnly: true
    }).subscribe({
      next: (r) => {
        this.reviewLoading.set(false);
        const queue = r.items ?? [];
        if (queue.length === 0) {
          this.notify(this.t('techstack.reviewNoDue'));
          return;
        }
        this.reviewQueue.set(queue);
        this.reviewIndex.set(0);
        this.reviewRevealed.set(false);
        this.reviewReviewed.set(0);
        this.reviewDone.set(false);
        this.reviewCurrent.set(null);
        this.reviewMode.set(true);
      },
      error: (e: Error) => {
        this.reviewLoading.set(false);
        this.notify(e.message, true);
      }
    });
  }

  /** 退出会话:回到列表,并刷新 dueToday 计数(刚复习过的会退出队列)。 */
  exitReview(): void {
    this.reviewMode.set(false);
    this.reviewCurrent.set(null);
    this.loadBuckets();
  }

  /**
   * 翻答案:列表 DTO 不含答案正文(概念/要点/更好答案),
   * 这里按 id 拉详情,只用于展示,不写入队列条目。
   */
  revealAnswer(): void {
    const card = this.currentCard();
    if (!card || this.reviewRevealed()) return;
    this.reviewLoading.set(true);
    this.api.get<KnowledgeItem>(`/api/knowledge/${card.id}`)
      .pipe(catchError(() => of(null)))
      .subscribe((full) => {
        this.reviewLoading.set(false);
        this.reviewCurrent.set(full ?? card);
        this.reviewRevealed.set(true);
      });
  }

  /**
   * 给当前卡片打分并推进。
   *
   * 直接复用后端 /review 端点(SM-2 算法在服务端算 nextReviewAt 与 mastery),
   * 回包是 ReviewOutcomeDto,不是完整条目 —— 只把排期相关字段原地更新到队列条目,
   * 不动内容字段。打分后:还有卡片就翻到下一张并收起答案;没有就标记本轮完成。
   */
  grade(result: ReviewResultLabel): void {
    const card = this.currentCard();
    if (!card) return;
    this.reviewLoading.set(true);
    this.api
      .post<ReviewOutcome>(`/api/knowledge/${card.id}/review`, {
        result,
        confidenceAfter: CONFIDENCE_FOR[result]
      })
      .subscribe({
        next: (o) => {
          this.reviewLoading.set(false);
          this.reviewReviewed.update((n) => n + 1);
          this.updateQueueItem(card.id, o);
          this.advance();
        },
        error: (e: Error) => {
          this.reviewLoading.set(false);
          this.notify(e.message, true);
        }
      });
  }

  /** 用复习回包原地更新队列里的条目(只覆盖排期/掌握度相关字段)。 */
  private updateQueueItem(id: string, o: ReviewOutcome): void {
    this.reviewQueue.update((list) =>
      list.map((x) =>
        x.id === id
          ? {
              ...x,
              reviewCount: (x.reviewCount ?? 0) + 1,
              nextReviewAt: o.nextReviewAt ?? x.nextReviewAt,
              mastery: (o.mastery as KnowledgeItem['mastery']) ?? x.mastery,
              easinessFactor: o.easinessFactor ?? x.easinessFactor,
              repetitionStreak: o.repetitionStreak ?? x.repetitionStreak
            }
          : x
      )
    );
  }

  /** 推进到下一张:收起答案;若已是最后一张则结束本轮。 */
  private advance(): void {
    const idx = this.reviewIndex();
    if (idx + 1 >= this.reviewQueue().length) {
      this.reviewDone.set(true);
      return;
    }
    this.reviewIndex.set(idx + 1);
    this.reviewRevealed.set(false);
    this.reviewCurrent.set(null);
  }

  /** 后端返回完整对象时就地替换,返回空则只刷新列表,避免详情面板显示旧值。 */
  private applyUpdate(updated: KnowledgeItem | null, id: string): void {
    if (updated && updated.id) {
      this.items.update((list) => list.map((i) => (i.id === updated.id ? updated : i)));
      if (this.selected()?.id === updated.id) this.selected.set(updated);
    } else {
      const cur = this.items().find((i) => i.id === id);
      if (cur) this.selected.set(cur);
      this.load();
    }
  }

  private notify(msg: string, isError = false): void {
    this.snack.open(msg, '关闭', {
      duration: isError ? 5000 : 2500,
      horizontalPosition: 'center',
      verticalPosition: 'bottom'
    });
  }

  // ------------------------------ 展示辅助 ------------------------------

  masteryLabel(m: string): string {
    return MASTERY_LABELS[m as MasteryLevel] ?? m;
  }

  masteryClass(m: string): string {
    return `m-${(m || 'new').toLowerCase()}`;
  }

  /** 熟练度百分比,用于列表里的小进度条。 */
  masteryPct(m: string): number {
    const i = MASTERY_ORDER.indexOf(m as MasteryLevel);
    if (i < 0) return 0;
    return Math.round(((i + 1) / MASTERY_ORDER.length) * 100);
  }

  /**
   * 取出某个区块要显示的文本。
   * keyPoints / commonMistakes 在后端是 JSON 数组字符串,这里解析成带编号的列表;
   * 解析失败就原样返回 —— 宁可显示原始 JSON,也不能因为脏数据让整页崩掉。
   */
  blockText(item: KnowledgeItem, key: string): string {
    if (key === 'keyPoints') return this.jsonList(item.keyPointsJson);
    if (key === 'commonMistakes') return this.jsonList(item.commonMistakesJson);
    const v = (item as unknown as Record<string, unknown>)[key];
    return typeof v === 'string' ? v : '';
  }

  private jsonList(raw?: string): string {
    if (!raw) return '';
    try {
      const arr: unknown = JSON.parse(raw);
      if (Array.isArray(arr)) {
        return arr.map((x, i) => `${i + 1}. ${String(x)}`).join('\n');
      }
      return typeof arr === 'string' ? arr : '';
    } catch {
      return raw;   // 不是合法 JSON 就展示原文
    }
  }

  /** 九块全空时给一句引导,好过展示一片空白的详情页。 */
  allBlocksEmpty(item: KnowledgeItem): boolean {
    return this.blocks.every((b) => this.blockText(item, b.key).length === 0);
  }
  nextReviewLabel(item: KnowledgeItem): string {
    if (!item.nextReviewAt) return '尚未安排';
    return item.nextReviewAt.slice(0, 10);
  }
}
