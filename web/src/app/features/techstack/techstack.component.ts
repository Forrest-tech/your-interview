import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { DocViewerComponent } from '../../shared/doc-viewer/doc-viewer.component';
import { CategoryManagerComponent } from './category-manager/category-manager.component';
import { forkJoin } from 'rxjs';
import { CommonModule } from '@angular/common';
import { FormsModule, ReactiveFormsModule, FormControl } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatOptionModule } from '@angular/material/core';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatTabsModule } from '@angular/material/tabs';
import { Subscription, catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { KnowledgeItem, KnowledgeSourceEntry, KnowledgeTopic, KnowledgeStats, MasteryLevel, Paged, PlaybookCompany, PlaybookCompanyRound } from '../../core/models/api.models';
import {
  KnowledgeSourceCompany, KnowledgeDuplicateGroup
} from '../../core/models/api.models';
import { TechStackLocalService } from '../../core/api/techstack-local.service';
import {
  UNCATEGORIZED_TOPIC, heatmapLevel, localDateKey, remapDeletedCategoryTopic, remapTopicName,
  topicDetailsAfterCategoryDelete, topicDetailsAfterCategoryRename,
  topicNamesAfterCategoryDelete, topicNamesAfterCategoryRename,
} from '../../core/api/techstack-local.helpers';
import { KnowledgeDialogComponent, KnowledgeForm } from './knowledge-dialog.component';
import { DuplicatesDialogComponent } from './duplicates-dialog.component';
import { SubnavService } from '../../layout/subnav.service';

/** 熟练度等级顺序 —— 用于展示顺序、进度和"提升一级"的目标值。 */
const MASTERY_ORDER: MasteryLevel[] = ['New', 'Learning', 'Familiar', 'Mastered', 'NeedsReview'];
/** 升级链(不含 NeedsReview,卡壳不是升级目标)。 */
const PROMOTE_ORDER: MasteryLevel[] = ['New', 'Learning', 'Familiar', 'Mastered'];

/** 复习结果档位 —— 必须与后端 RecordReviewCommandValidator 的允许值一致。 */
type ReviewResultLabel = 'Again' | 'Hard' | 'Good' | 'Easy';

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
  labelKey: string;
  icon: string;
  hintKey: string;
}

/**
 * 知识卡片的正文区块。
 * 顺序按用户要求:题干 → 我的回答 → 推荐答案 → 概念 → 要点 → 常见坑。
 * 先看自己怎么答的,再看更好的说法,最后才是原理和要点。
 */
const DETAIL_BLOCKS: DetailBlock[] = [
  // 后端存的是 title/question/conceptExplanation + 两个 JSON 数组,
  // 不是八个独立正文字段。这里把 JSON 拆成"要点/常见坑"两块来展示。
  // ★ 2026-10-18(Forrest):去掉 question 块 —— Title 已在顶部显示,冗余。
  { key: 'myAnswer', labelKey: 'ts.block.myAnswer', icon: 'record_voice_over', hintKey: 'ts.block.myAnswerHint' },
  { key: 'betterAnswer', labelKey: 'ts.block.betterAnswer', icon: 'stars', hintKey: 'ts.block.betterAnswerHint' },
  { key: 'conceptExplanation', labelKey: 'ts.block.conceptExplanation', icon: 'menu_book', hintKey: 'ts.block.conceptExplanationHint' },
  { key: 'keyPoints', labelKey: 'ts.block.keyPoints', icon: 'lightbulb_outline', hintKey: 'ts.block.keyPointsHint' },
  { key: 'commonMistakes', labelKey: 'ts.block.commonMistakes', icon: 'warning_amber', hintKey: 'ts.block.commonMistakesHint' }
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
    CommonModule, FormsModule, ReactiveFormsModule, RouterLink, MatCardModule, MatIconModule, MatButtonModule,
    MatChipsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    MatDialogModule, MatTooltipModule, MatSnackBarModule, MatMenuModule, MatCheckboxModule,
    MatOptionModule, MatAutocompleteModule, MatSelectModule, DocViewerComponent
  ],
  templateUrl: './techstack.component.html',
  styleUrl: './techstack.component.scss'
})
export class TechStackComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiClient);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly subnav = inject(SubnavService);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  tf = (key: string, params: Record<string, string | number | null | undefined>): string => this.i18n.tf(key, params);

  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  /** ★ 票16:自定义分类/复习活动的单一读写路径(后端真源,见 TechStackLocalService)。 */
  private readonly tsLocal = inject(TechStackLocalService);

  readonly masteryOrder = MASTERY_ORDER;
  readonly blocks = DETAIL_BLOCKS;

  /** 复习结果四档,直接对齐后端 SM-2 的 Again/Hard/Good/Easy。 */
  readonly reviewChoices: { value: ReviewResultLabel }[] = [
    { value: 'Again' },
    { value: 'Hard' },
    { value: 'Good' },
    { value: 'Easy' }
  ];

  // ------------------------------ 今日复习会话 ------------------------------

  /** 是否进入"今日复习"专注模式:进入后隐藏列表/筛选,改走闪卡队列。 */
  readonly reviewMode = signal(false);
  /** ★ 回收站模式:显示已删除的卡片,可恢复。 */
  readonly trashMode = signal(false);
  readonly trashItems = signal<KnowledgeItem[]>([]);
  /** ★ #5:重做模式 (Anki-for-LeetCode 模式:只看题,重做/重讲,pass/fail)。 */
  readonly resoleMode = signal<boolean>(
    localStorage.getItem('ts.resoleMode') === '1');
  /** ★ #5:重做模式下是否已翻开答案。 */
  readonly resoleRevealed = signal(false);

  toggleResoleMode(): void {
    const v = !this.resoleMode();
    this.resoleMode.set(v);
    localStorage.setItem('ts.resoleMode', v ? '1' : '0');
    this.resoleRevealed.set(false);
  }

  /** ★ #5:二元评分 (Pass/Fail) 映射到 SM-2。 */
  gradeBinary(pass: boolean): void {
    this.grade(pass ? 'Good' : 'Again');
  }
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

  /** 筛选区折叠状态,记 localStorage */
  readonly filtersCollapsed = signal(
    typeof localStorage !== 'undefined' && localStorage.getItem('ts.filtersCollapsed') === '1');

  /** 左列表宽度(可拖拽,记 localStorage) */
  readonly listWidth = signal(
    (() => {
      try {
        const v = parseInt(localStorage.getItem('ts.listWidth') ?? '', 10);
        return v >= 220 && v <= 600 ? v : 340;
      } catch { return 340; }
    })());

  startListResize(ev: MouseEvent): void {
    ev.preventDefault();
    const startX = ev.clientX;
    const startW = this.listWidth();
    const onMove = (e: MouseEvent) => {
      const next = startW + (e.clientX - startX);
      this.listWidth.set(Math.min(600, Math.max(220, next)));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try { localStorage.setItem('ts.listWidth', String(this.listWidth())); } catch { /* ignore */ }
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }

  toggleFilters(): void {
    const v = !this.filtersCollapsed();
    this.filtersCollapsed.set(v);
    try { localStorage.setItem('ts.filtersCollapsed', v ? '1' : '0'); } catch { /* ignore */ }
  }

  /** 打开分类管理弹窗 */
  openCategoryManager(): void {
    // 用 topics 接口的全量计数,不用当前页条目(分页会导致计数错)
    const counts = new Map<string, number>();
    for (const t of this.topicDetails()) {
      if (t.topic) counts.set(t.topic, t.total ?? 0);
    }
    // 兜底:接口不可用时从当前页推导
    if (counts.size === 0) {
      for (const it of this.items()) {
        const tp = (it.topic || '').trim() || 'Uncategorized';
        counts.set(tp, (counts.get(tp) ?? 0) + 1);
      }
      for (const tp of this.topics()) {
        if (!counts.has(tp)) counts.set(tp, 0);
      }
    }
    // 加上用户手动建的空分类(票16:后端真源,经 TechStackLocalService 信号)
    for (const n of this.tsLocal.customCategories()) {
      if (![...counts.keys()].some(k => k.toLowerCase() === n.toLowerCase())) {
        counts.set(n, 0);
      }
    }
    const dialogRef = this.dialog.open(CategoryManagerComponent, {
      width: '520px',
      data: {}
    });
    dialogRef.componentInstance.categories.set(
      [...counts.entries()].map(([name, count]) => ({ name, count })));
    dialogRef.afterClosed().subscribe((changed) => {
      if (changed) {
        this.load();
        this.loadBuckets();
      }
    });
  }

  /**
   * 票84:分类删除成功后的本地同步 —— 与服务端 DeleteTopic 同语义
   * (名下条目回落 Uncategorized),把本页全部本地状态消费方一次对齐,
   * 不发请求、不等关弹窗重拉(关弹窗后既有 load() 对账仍保留):
   * 条目卡/详情头/复习队列副本的 topic、Topic chips、管理器计数行、
   * 以及正指向被删名的当前筛选值。纯映射在 techstack-local.helpers。
   */
  private applyCategoryDeleted(name: string): void {
    const deleted = (name ?? '').trim();
    if (!deleted) return;
    const isDeleted = (tp: string | null | undefined) =>
      (tp ?? '').trim().toLowerCase() === deleted.toLowerCase();

    // 受影响条数:以 topics 全量计数行为准(当前页只是分页子集);
    // 接口计数缺失时退当前页命中数 —— 只用来决定要不要补 Uncategorized chip
    const detailRow = this.topicDetails().find((d) => isDeleted(d.topic));
    let pageHits = 0;
    this.items.update((list) => list.map((it) => {
      if (!isDeleted(it.topic)) return it;
      pageHits++;
      return { ...it, topic: UNCATEGORIZED_TOPIC };
    }));
    const hadItems = (detailRow?.total ?? 0) > 0 || pageHits > 0;

    const sel = this.selected();
    if (sel && isDeleted(sel.topic)) {
      this.selected.set({ ...sel, topic: UNCATEGORIZED_TOPIC });
    }
    this.reviewQueue.update((list) => remapDeletedCategoryTopic(list, deleted));
    const rc = this.reviewCurrent();
    if (rc && isDeleted(rc.topic)) {
      this.reviewCurrent.set({ ...rc, topic: UNCATEGORIZED_TOPIC });
    }

    this.topics.update((names) => topicNamesAfterCategoryDelete(names, deleted, hadItems));
    this.topicDetails.update((rows) => topicDetailsAfterCategoryDelete(rows, deleted));

    // 当前筛选正指着被删名:改指 Uncategorized,同一批条目仍在眼前
    // (分页/总数在关弹窗的 load() 对账时归位,这里不额外发请求)
    if (this.topic() !== null && isDeleted(this.topic())) {
      this.topic.set(UNCATEGORIZED_TOPIC);
      this.syncFiltersToUrl();
    }
  }

  /**
   * 票84 邻居:分类改名成功后的本地同步 —— 与删分类同一通道、同一批
   * 消费方(条目卡/详情头/复习队列副本/Topic chips/管理器计数行/当前
   * 筛选值),把旧名整体换成新名(合并时计数并入目标行)。不发请求;
   * 关弹窗后的 load() 对账不变。
   */
  private applyCategoryRenamed(oldName: string, newName: string): void {
    const from = (oldName ?? '').trim();
    const to = (newName ?? '').trim();
    if (!from || !to || from.toLowerCase() === to.toLowerCase()) return;
    const isFrom = (tp: string | null | undefined) =>
      (tp ?? '').trim().toLowerCase() === from.toLowerCase();

    this.items.update((list) => remapTopicName(list, from, to));
    const sel = this.selected();
    if (sel && isFrom(sel.topic)) this.selected.set({ ...sel, topic: to });
    this.reviewQueue.update((list) => remapTopicName(list, from, to));
    const rc = this.reviewCurrent();
    if (rc && isFrom(rc.topic)) this.reviewCurrent.set({ ...rc, topic: to });

    this.topics.update((names) => topicNamesAfterCategoryRename(names, from, to));
    this.topicDetails.update((rows) => topicDetailsAfterCategoryRename(rows, from, to));

    if (this.topic() !== null && isFrom(this.topic())) {
      this.topic.set(to);
      this.syncFiltersToUrl();
    }
  }

  /** 激活的筛选数量(用于折叠态的计数徽标) */
  readonly activeFilterCount = computed(() => {
    let n = 0;
    if (this.search().length > 0) n++;
    if (this.topic() !== null) n++;
    if (this.mastery() !== null) n++;
    if (this.company() !== null) n++;
    return n;
  });
  /** 来源公司筛选(需求 6.4.4:按"这题来自哪家公司"反查)。 */
  readonly company = signal<string | null>(null);

  /** 来源公司清单。接口不可用时为 null,整条筛选行隐藏而不是显示空选项。 */
  readonly sourceCompanies = signal<KnowledgeSourceCompany[] | null>(null);
  /** 当前选中条目的来源反查结果。 */
  /** 重复条目组数(0 = 无重复,入口隐藏)。 */
  readonly duplicateGroups = signal<KnowledgeDuplicateGroup[]>([]);

  readonly selected = signal<KnowledgeItem | null>(null);

  /** 主题列表:接口优先,404/失败则从当前页条目里推导 —— 至少让筛选可用。 */
  readonly topics = signal<string[]>([]);
  /** topics 接口的全量数据(含 total 计数),供分类管理用 */
  readonly topicDetails = signal<KnowledgeTopic[]>([]);
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
      { level: 'Mastered' as MasteryLevel, count: m.get('Mastered') ?? 0, pct: 0 },
      { level: 'NeedsReview' as MasteryLevel, count: m.get('NeedsReview') ?? 0, pct: 0 }
    ];
    const denom = b.totalItems > 0 ? b.totalItems : rows.reduce((s, r) => s + r.count, 0);
    return rows.map((r) => ({
      ...r,
      pct: denom > 0 ? Math.round((r.count / denom) * 100) : 0
    }));
  });

  /** 票84:分类删除/改名事件订阅(组件销毁时退订)。 */
  private readonly categorySubs = new Subscription();

  ngOnInit(): void {
    this.tsLocal.ensureLoaded(); // 票16:分类/复习活动改读后端(首次自动导入本地存量)
    // 票84:分类管理器删/改名成功后即时同步本地呈现,不等关弹窗/整页刷新(F4)
    this.categorySubs.add(this.tsLocal.categoryDeleted$
      .subscribe((name) => this.applyCategoryDeleted(name)));
    this.categorySubs.add(this.tsLocal.categoryRenamed$
      .subscribe(({ oldName, newName }) => this.applyCategoryRenamed(oldName, newName)));
    this.restoreFiltersFromUrl();
    this.load();
    this.loadBuckets();
    this.loadSources();
    this.loadDuplicates();
    this.loadTopics();
    // ★ 子导航:按模式显示可用项 —— 列表模式显示 Concepts/Due review,
    //   复习模式显示 Exit review。解决默认锚点在隐藏 DOM 上失效的问题。
    this.subnav.register('/tech-stack', [
      {
        key: 'ts:concepts',
        labelKey: 'sub.concepts',
        icon: 'account_tree',
        active: () => !this.reviewMode(),
        action: () => { if (this.reviewMode()) this.exitReview(); }
      },
      {
        key: 'ts:due',
        labelKey: 'sub.dueReview',
        icon: 'schedule',
        active: () => this.reviewMode(),
        action: () => { if (!this.reviewMode()) this.startReview(); }
      }
    ]);
    // 来源公司搜索框:输了字但没从下拉选,清空已选公司,避免张冠李戴
    this.companySearchCtrl.valueChanges.subscribe((v) => {
      if ((v ?? '').trim() !== this.newDetailSourceCompany) this.newDetailSourceCompany = '';
    });
  }

  /** 从 URL 恢复筛选值(mastery 默认为 'New',URL 里没有时保持默认) */
  private restoreFiltersFromUrl(): void {
    const qp = this.route.snapshot.queryParamMap;
    const search = qp.get('search');
    const topic = qp.get('topic');
    const mastery = qp.get('mastery');
    const company = qp.get('company');
    if (search) this.search.set(search);
    if (topic) this.topic.set(topic);
    if (mastery) this.mastery.set(mastery as MasteryLevel);
    if (company) this.company.set(company);
  }

  /** 筛选值同步到 URL */
  private syncFiltersToUrl(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        search: this.search() || null,
        topic: this.topic(),
        mastery: this.mastery(),
        company: this.company(),
      },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  /** 来源公司清单属于可选增强(老库没有来源字段时为空),失败静默降级。 */
  loadSources(): void {
    this.api.get<KnowledgeSourceCompany[]>('/api/knowledge/sources')
      .pipe(catchError(() => of(null)))
      .subscribe((list) => {
        this.sourceCompanies.set(
          Array.isArray(list) && list.length > 0 ? list : null);
      });
  }

  /** 重复条目是"偶发需要"的功能:只报个数,点开才真正拉取分组。 */
  loadDuplicates(): void {
    this.api.get<KnowledgeDuplicateGroup[]>('/api/knowledge/duplicates')
      .pipe(catchError(() => of(null)))
      .subscribe((groups) => this.duplicateGroups.set(
        Array.isArray(groups) ? groups : []));
  }

  openDuplicates(): void {
    const ref = this.dialog.open(DuplicatesDialogComponent, {
      data: { groups: this.duplicateGroups() },
      maxWidth: '860px',
      autoFocus: false
    });
    ref.afterClosed().subscribe((changed?: boolean) => {
      if (!changed) return;
      this.notify(this.t('ts.dup.merged'));
      this.reload();
      this.loadDuplicates();
    });
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
      company: this.company(),
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

        // 详情面板保持选中项;若该项已不在列表(被筛掉/删掉),退而选第一条(走 select 取全量详情)
        // 刷新恢复:URL 里有 id 参数时优先恢复该项
        const urlId = this.route.snapshot.queryParamMap.get('id');
        const cur = this.selected();
        if (urlId && list.some((i) => i.id === urlId)) {
          const target = list.find((i) => i.id === urlId)!;
          if (!cur || cur.id !== urlId) this.select(target);
        } else if (!cur || !list.some((i) => i.id === cur.id)) {
          if (list.length > 0) this.select(list[0]);
          else this.selected.set(null);
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
          // 后端返回 {topic,total,...},名字喂筛选器,全量对象留给分类管理计数
          this.topics.set(list.map((t) => t.topic).filter(Boolean).sort());
          this.topicDetails.set(list);
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
    this.loadSources();
  }

  /** 按来源公司反查(需求 6.4.4)。再点一次同一家即取消。 */
  selectCompany(c: string | null): void {
    this.company.set(this.company() === c ? null : c);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
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
    this.syncFiltersToUrl();
    this.load();
  }

  selectTopic(t: string | null): void {
    this.topic.set(t);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
  }

  toggleMastery(m: MasteryLevel): void {
    this.mastery.set(this.mastery() === m ? null : m);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
  }

  selectMastery(m: MasteryLevel | null): void {
    this.mastery.set(m);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
  }

  clearFilters(): void {
    this.search.set('');
    this.topic.set(null);
    this.mastery.set(null);  // 回到 All
    this.company.set(null);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
  }

  /** 单独清除某个筛选(折叠态 chips 用) */
  clearOneFilter(kind: 'search' | 'topic' | 'mastery' | 'company'): void {
    if (kind === 'search') this.search.set('');
    if (kind === 'topic') this.topic.set(null);
    if (kind === 'mastery') this.mastery.set(null);
    if (kind === 'company') this.company.set(null);
    this.page.set(1);
    this.syncFiltersToUrl();
    this.load();
  }

  hasFilters(): boolean {
    return this.search().length > 0 || this.topic() !== null
        || this.mastery() !== null || this.company() !== null;
  }

  goPage(p: number): void {
    if (p < 1 || p > this.totalPages() || p === this.page()) return;
    this.page.set(p);
    this.load();
  }

  // ------------------------------ 主从交互 ------------------------------

  /** 选中条目:调详情接口拿全量字段(列表 DTO 不带内容块),再展示。选中 id 同步到 URL,刷新保持。 */
  select(item: KnowledgeItem): void {
    // 先给个即时反馈,详情回来再换全量
    this.selected.set(item);
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { id: item.id },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
    this.api.get<KnowledgeItem>(`/api/knowledge/${item.id}`).subscribe({
      next: (full) => {
        this.selected.set(full);
        this.items.update(list => list.map((x) => x.id === full.id ? { ...x, ...full } : x));
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  isSelected(item: KnowledgeItem): boolean {
    return this.selected()?.id === item.id;
  }

  /** 清除 URL 中的选中 id 参数。 */
  private clearUrlId(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { id: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  // ------------------------------ 批量编辑(删除) ------------------------------

  /** 右上角"编辑":进入/退出批量管理模式。退出时清空已选项。 */
  readonly editing = signal(false);
  readonly batchIds = signal<string[]>([]);

  toggleEdit(): void {
    const next = !this.editing();
    this.editing.set(next);
    if (!next) this.batchIds.set([]);
  }

  isBatchSelected(id: string): boolean {
    return this.batchIds().includes(id);
  }

  /** 编辑模式下点条目 = 勾选/取消勾选,不打开详情。 */
  toggleBatchSelect(id: string, ev: Event): void {
    if (!this.editing()) return;
    ev.preventDefault();
    ev.stopPropagation();
    this.batchIds.update(list =>
      list.includes(id) ? list.filter(x => x !== id) : [...list, id]);
  }

  /** 批量删除:逐个调删除接口(后端暂无批量接口),完成后退出编辑模式并刷新。 */
  deleteSelected(): void {
    const ids = this.batchIds();
    if (ids.length === 0) return;
    const names = this.items().filter(e => ids.includes(e.id))
      .map(e => e.title).join('、');
    const ok = confirm(
      this.t('ts.list.confirmDeleteBatch')
        .replace('{n}', String(ids.length))
        .replace('{names}', names));
    if (!ok) return;

    forkJoin(ids.map(id => this.api.delete<void>(`/api/knowledge/${id}`))).subscribe({
      next: () => {
        this.notify(this.t('ts.deleted'));
        this.editing.set(false);
        this.batchIds.set([]);
        this.selected.set(null);
        this.clearUrlId();
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** ★ 进入回收站:加载已删除的卡片。 */
  openTrash(): void {
    this.trashMode.set(true);
    this.api.get<Paged<KnowledgeItem>>('/api/knowledge', {
      page: 1, pageSize: 100, includeDeleted: true
    }).subscribe({
      next: (p) => this.trashItems.set(p.items ?? []),
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** ★ 退出回收站。 */
  closeTrash(): void {
    this.trashMode.set(false);
    this.trashItems.set([]);
  }

  /** ★ 从回收站恢复卡片。 */
  restoreItem(id: string): void {
    this.api.post<void>(`/api/knowledge/${id}/restore`, {}).subscribe({
      next: () => {
        this.notify(this.t('ts.trash.restored'));
        this.openTrash(); // 刷新回收站列表
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** 来源标记文案。没有来源信息时返回 null。 */
  sourceLabel(item: KnowledgeItem): string | null {
    if (item.source !== 'FromInterview') return null;
    // 多来源:只显示公司名,不写轮次和日期
    const names = this.parseSources(item)
      .map((s) => s.companyName?.trim())
      .filter((n): n is string => !!n);
    if (names.length > 0) return names.join(' · ');
    return this.i18n.t('nav.playbook');
  }

  /** 来源公司 chip 的 tooltip:"已掌握 3 / 待复习 2"。 */
  sourceTooltip(c: KnowledgeSourceCompany): string {
    return this.i18n.t('ts.source.tooltip')
      .replace('{m}', String(c.masteredCount))
      .replace('{d}', String(c.dueCount));
  }

  /** 来源标记 tooltip:可点击跳回机经。 */
  sourceJumpTooltip(src: string): string {
    return this.i18n.t('ts.source.jumpBack').replace('{src}', src);
  }

  /** 来源标记 tooltip:不可跳转。 */
  sourceFromTooltip(src: string): string {
    return this.i18n.t('ts.source.from').replace('{src}', src);
  }

  openCreate(): void {
    const ref = this.dialog.open(KnowledgeDialogComponent, { autoFocus: 'first-tabbable' });
    // FUN-05:保存已收进对话框内部(成功才关窗,失败在弹窗内给字段级提示),
    // 这里只在保存成功(回传 payload)时刷新列表,不再二次 POST。
    ref.afterClosed().subscribe((payload?: Record<string, unknown> | null) => {
      if (!payload) return;
      this.notify(this.t('ts.created'));
      this.page.set(1);
      this.reload();
      this.loadTopics();
    });
  }

  /** "一行一条"转 JSON 数组;空给 null。 */
  private linesToJson(text: string): string | null {
    const lines = text.split('\n').map((x) => x.trim()).filter((x) => x.length > 0);
    return lines.length > 0 ? JSON.stringify(lines) : null;
  }

  // ------------------------------ 详情统一编辑模式 ------------------------------

  /** 详情是否处于编辑态。右上角 Edit 进入,Save/Cancel 退出。 */
  readonly detailEditing = signal(false);
  readonly savingDetail = signal(false);
  readonly difficultyOptions = [1, 2, 3, 4, 5];
  /** 编辑态下所有字段的草稿。 */
  detailDraft: {
    title: string;
    topic: string;
    difficulty: number;
    importance: number;
    sources: KnowledgeSourceEntry[];
    blocks: Record<string, string>;
  } = { title: '', topic: '', difficulty: 3, importance: 3, sources: [], blocks: {} };
  newDetailSourceCompany = '';
  newDetailSourceRound: number | null = null;
  newDetailSourceDate = '';
  /** 来源添加表单是否展开:默认只显示 chips + 添加按钮。 */
  readonly showDetailSourceForm = signal(false);
  /** 来源公司下拉:Playbook 备考中的公司(带搜索)。 */
  readonly companySearchCtrl = new FormControl('');
  readonly playbookCompanies = signal<PlaybookCompany[]>([]);
  readonly selectedCompanyId = signal<string | null>(null);
  readonly companyRounds = signal<PlaybookCompanyRound[]>([]);
  readonly selectedRoundId = signal<string | null>(null);
  readonly selectedEntryId = signal<string | null>(null);
  readonly filteredCompanies = computed(() => {
    const q = (this.companySearchCtrl.value ?? '').trim().toLowerCase();
    const list = this.playbookCompanies();
    if (!q) return list;
    return list.filter((c) => c.companyName.toLowerCase().includes(q));
  });

  /** 懒加载 Playbook 公司库(只加载一次)。 */
  loadPlaybookCompanies(): void {
    if (this.playbookCompanies().length > 0) return;
    this.api.get<PlaybookCompany[]>('/api/interviews/companies').subscribe({
      next: (list) => this.playbookCompanies.set(list ?? []),
      error: () => this.playbookCompanies.set([]),
    });
  }

  /** 从下拉选中公司:只允许选库里的,不手动填;同时加载该公司的轮次。 */
  onCompanySelected(c: PlaybookCompany): void {
    this.newDetailSourceCompany = c.companyName;
    this.companySearchCtrl.setValue(c.companyName, { emitEvent: false });
    this.selectedCompanyId.set(c.companyId);
    this.selectedRoundId.set(null);
    this.selectedEntryId.set(null);
    this.newDetailSourceRound = null;
    this.newDetailSourceDate = '';
    this.companyRounds.set([]);
    this.api.get<PlaybookCompanyRound[]>(`/api/interviews/companies/${c.companyId}/rounds`).subscribe({
      next: (list) => this.companyRounds.set(list ?? []),
      error: () => this.companyRounds.set([]),
    });
  }

  /** 选中轮次:日期自动带出,不手动填。 */
  onRoundSelected(roundId: string): void {
    this.selectedRoundId.set(roundId);
    const r = this.companyRounds().find((x) => x.roundId === roundId);
    if (!r) return;
    this.selectedEntryId.set(r.entryId);
    this.newDetailSourceRound = r.order;
    this.newDetailSourceDate = r.scheduledDate ?? '';
  }

  /** 轮次下拉显示:Round 1 · Technical · 2026-10-07 */
  roundOptionLabel(r: PlaybookCompanyRound): string {
    const parts = [`Round ${r.order}`];
    if (r.stage) parts.push(r.stage);
    parts.push(r.scheduledDate ?? this.t('ts.source.noDate'));
    return parts.join(' · ');
  }

  /** 展开来源表单时重置搜索框并加载公司库。 */
  toggleSourceForm(): void {
    const next = !this.showDetailSourceForm();
    this.showDetailSourceForm.set(next);
    if (next) {
      this.companySearchCtrl.setValue('');
      this.newDetailSourceCompany = '';
      this.loadPlaybookCompanies();
    }
  }

  /** 解析条目的多来源;没有则从单来源字段回退构造一条。 */
  parseSources(item: KnowledgeItem): KnowledgeSourceEntry[] {
    if (item.sourcesJson) {
      try {
        const arr = JSON.parse(item.sourcesJson);
        if (Array.isArray(arr)) return arr as KnowledgeSourceEntry[];
      } catch { /* 忽略,回退 */ }
    }
    // 回退:单来源字段
    if (item.sourceCompanyName || item.sourceInterviewEntryId) {
      return [{
        companyName: item.sourceCompanyName ?? undefined,
        roundNo: item.sourceRoundNo ?? undefined,
        roundStage: item.sourceRoundStage ?? undefined,
        date: item.sourceDate ?? undefined,
        interviewEntryId: item.sourceInterviewEntryId ?? undefined,
        jobApplicationId: item.sourceJobApplicationId ?? undefined,
      }];
    }
    return [];
  }

  /** 多来源显示文案:公司 · Round N · 日期。 */
  sourceEntryLabel(s: KnowledgeSourceEntry): string {
    const parts: string[] = [];
    if (s.companyName) parts.push(s.companyName);
    if (s.roundNo) parts.push(`Round ${s.roundNo}`);
    else if (s.roundStage) parts.push(s.roundStage);
    if (s.date) parts.push(s.date);
    return parts.join(' · ') || this.t('ts.source.unknown');
  }

  /** 进入详情编辑:把当前值全部填进草稿。 */
  enterDetailEdit(item: KnowledgeItem): void {
    const blocks: Record<string, string> = {};
    for (const b of this.blocks) {
      if (b.key === 'keyPoints' || b.key === 'commonMistakes') {
        const json = b.key === 'keyPoints' ? item.keyPointsJson : item.commonMistakesJson;
        blocks[b.key] = this.jsonToLines(json);
      } else {
        const v = (item as unknown as Record<string, unknown>)[b.key];
        blocks[b.key] = typeof v === 'string' ? v : '';
      }
    }
    this.detailDraft = {
      title: item.title,
      topic: item.topic,
      difficulty: item.difficulty,
      importance: item.importance,
      sources: this.parseSources(item),
      blocks,
    };
    this.newDetailSourceCompany = '';
    this.newDetailSourceRound = null;
    this.newDetailSourceDate = '';
    this.companySearchCtrl.setValue('', { emitEvent: false });
    this.selectedCompanyId.set(null);
    this.selectedRoundId.set(null);
    this.selectedEntryId.set(null);
    this.companyRounds.set([]);
    this.showDetailSourceForm.set(false);
    this.detailEditing.set(true);
  }

  cancelDetailEdit(): void {
    this.detailEditing.set(false);
  }

  /** 详情编辑中:添加一条来源。 */
  addDetailSource(): void {
    const company = this.newDetailSourceCompany.trim();
    if (!company && this.newDetailSourceRound == null && !this.newDetailSourceDate) return;
    this.detailDraft.sources = [...this.detailDraft.sources, {
      companyName: company || undefined,
      roundNo: this.newDetailSourceRound ?? undefined,
      date: this.newDetailSourceDate || undefined,
      interviewEntryId: this.selectedEntryId() ?? undefined,
      roundId: this.selectedRoundId() ?? undefined,
    }];
    this.newDetailSourceCompany = '';
    this.newDetailSourceRound = null;
    this.newDetailSourceDate = '';
    this.companySearchCtrl.setValue('', { emitEvent: false });
    this.selectedCompanyId.set(null);
    this.selectedRoundId.set(null);
    this.selectedEntryId.set(null);
    this.companyRounds.set([]);
  }

  /** 详情编辑中:删除一条来源。 */
  removeDetailSource(idx: number): void {
    this.detailDraft.sources = this.detailDraft.sources.filter((_, i) => i !== idx);
  }

  /** 保存详情编辑:所有字段一次 PUT。 */
  saveDetailEdit(item: KnowledgeItem): void {
    const title = this.detailDraft.title.trim();
    if (!title) return;
    this.savingDetail.set(true);
    const d = this.detailDraft;
    const payload: Record<string, unknown> = {
      title,
      topic: d.topic.trim() || item.topic,
      // 票61:DETAIL_BLOCKS 无 question 块(详情不展示题干),保存时必须回填条目现存值,
      // 否则恒发空串、后端全量 PUT 会把已存 Question 清空(数据丢失)。
      question: item.question,
      conceptExplanation: d.blocks['conceptExplanation'] || null,
      keyPointsJson: this.linesToJson(d.blocks['keyPoints'] ?? ''),
      commonMistakesJson: this.linesToJson(d.blocks['commonMistakes'] ?? ''),
      myAnswer: d.blocks['myAnswer'] || null,
      betterAnswer: d.blocks['betterAnswer'] || null,
      difficulty: d.difficulty,
      importance: d.importance,
      sourcesJson: d.sources.length > 0 ? JSON.stringify(d.sources) : null,
    };
    this.api.put<void>(`/api/knowledge/${item.id}`, payload).subscribe({
      next: () => {
        this.savingDetail.set(false);
        this.detailEditing.set(false);
        this.notify(this.t('ts.updated'));
        const updated: KnowledgeItem = {
          ...item,
          title,
          topic: d.topic.trim() || item.topic,
          difficulty: d.difficulty,
          importance: d.importance,
          question: item.question,
          conceptExplanation: d.blocks['conceptExplanation'] || undefined,
          keyPointsJson: this.linesToJson(d.blocks['keyPoints'] ?? '') ?? undefined,
          commonMistakesJson: this.linesToJson(d.blocks['commonMistakes'] ?? '') ?? undefined,
          myAnswer: d.blocks['myAnswer'] || undefined,
          betterAnswer: d.blocks['betterAnswer'] || undefined,
          sourcesJson: d.sources.length > 0 ? JSON.stringify(d.sources) : undefined,
        };
        this.selected.set(updated);
        this.items.update(list => list.map((x) => x.id === updated.id ? updated : x));
        this.loadTopics();
      },
      error: (e: Error) => {
        this.savingDetail.set(false);
        this.notify(e.message, true);
      }
    });
  }


  /** JSON 数组转回"一行一条"。 */
  private jsonToLines(json?: string | null): string {
    if (!json) return '';
    try {
      const arr = JSON.parse(json);
      return Array.isArray(arr) ? arr.join('\n') : '';
    } catch { return ''; }
  }

  // ------------------------------ 复习动作 ------------------------------

  /** 提升一级熟练度。confidence 随用户自评传,默认给一个 0.7 的中庸值。 */
  promote(item: KnowledgeItem): void {
    const idx = PROMOTE_ORDER.indexOf(item.mastery);
    if (idx < 0 || idx >= PROMOTE_ORDER.length - 1) {
      this.notify(this.t('ts.promote.maxReached'));
      return;
    }
    const next = PROMOTE_ORDER[idx + 1];
    const ok = confirm(
      this.t('ts.promote.confirm')
        .replace('{title}', item.title)
        .replace('{from}', this.masteryLabel(item.mastery))
        .replace('{to}', this.masteryLabel(next))
    );
    if (!ok) return;

    this.acting.set(true);
    this.api.post<KnowledgeItem>(`/api/knowledge/${item.id}/mastery`, {
      level: next,
      confidence: 0.7
    }).subscribe({
      next: (updated) => {
        this.acting.set(false);
        this.notify(this.t('ts.promote.done').replace('{to}', this.masteryLabel(next)));
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
          this.notify(
            this.t('ts.review.recorded')
              .replace('{grade}', this.reviewLabel(result))
              .replace('{date}', this.fmtDate(outcome.nextReviewAt)));
          this.applyReviewOutcome(item.id, outcome);
          this.recordReviewActivity(); // ★ #9:记录复习活动(热力图)——与 grade() 同接线,此前 Log review 漏调致热力图无痕(票81)
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
    if (!v) return this.t('ts.review.notScheduled');
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? this.t('ts.review.notScheduled') : d.toLocaleDateString(this.i18n.locale());
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
        let queue = r.items ?? [];
        if (queue.length === 0) {
          this.notify(this.t('techstack.reviewNoDue'));
          return;
        }
        // ★ 200 上限提示:如果总数超过 200,告诉用户
        if ((r.total ?? 0) > 200) {
          this.notify(this.t('techstack.reviewTruncated').replace('{n}', String(r.total)));
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

  /** ★ #20:考试模式 (倒计时器)。 */
  /** 退出会话:回到列表,并刷新 dueToday 计数(刚复习过的会退出队列)。 */
  exitReview(): void {
    this.reviewMode.set(false);
    this.reviewCurrent.set(null);
    this.loadBuckets();
  }

  /** ★ 打通:Tech Stack 卡片 → AI Practice 口语练习。 */
  practiceCard(item: KnowledgeItem): void {
    const q = item.question || item.title;
    this.router.navigate(['/practice'], {
      queryParams: { q: q.slice(0, 200) }
    });
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
          this.recordReviewActivity(); // ★ #9:记录复习活动(热力图)
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

  /** ★ #9:记录每日复习数 (票16:后端真源,经 TechStackLocalService;日期仍按本地口径)。 */
  private recordReviewActivity(): void {
    this.tsLocal.recordReview();
  }

  /** ★ #9:过去 12 周的热力图数据。(票16:数据源改后端信号;网格/档位规则逐字不变,键统一走 localDateKey 本地口径) */
  readonly heatmapWeeks = computed(() => {
    const data: Record<string, number> = this.tsLocal.reviewActivity();
    const weeks: { date: string; count: number; level: number }[][] = [];
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const dayOfWeek = (today.getDay() + 6) % 7;
    const start = new Date(today);
    start.setDate(start.getDate() - dayOfWeek - 7 * 11);
    for (let w = 0; w < 12; w++) {
      const week: { date: string; count: number; level: number }[] = [];
      for (let d = 0; d < 7; d++) {
        const dt = new Date(start);
        dt.setDate(dt.getDate() + w * 7 + d);
        const key = localDateKey(dt);
        const count = dt > today ? -1 : (data[key] || 0);
        const level = count < 0 ? -1 : heatmapLevel(count);
        week.push({ date: key, count, level });
      }
      weeks.push(week);
    }
    return weeks;
  });

  /** ★ #9:当前连击 (连续复习天数)。(票16:数据源改后端信号;算法逐字不变) */
  readonly reviewStreak = computed(() => {
    const data: Record<string, number> = this.tsLocal.reviewActivity();
    let streak = 0;
    const d = new Date();
    d.setHours(0, 0, 0, 0);
    const todayKey = localDateKey(d);
    if (!data[todayKey]) d.setDate(d.getDate() - 1);
    while (true) {
      const key = localDateKey(d);
      if (data[key]) { streak++; d.setDate(d.getDate() - 1); }
      else break;
    }
    return streak;
  });

  /** ★ #11:冲刺模式 (按面试倒计时重排复习优先级)。 */
  /** 推进到下一张:收起答案;若已是最后一张则结束本轮。 */
  private advance(): void {
    const idx = this.reviewIndex();
    if (idx + 1 >= this.reviewQueue().length) {
      this.reviewDone.set(true);
      return;
    }
    this.reviewIndex.set(idx + 1);
    this.reviewRevealed.set(false);
    this.resoleRevealed.set(false); // ★ #5:重做模式翻页时重置
    this.reviewCurrent.set(null);
  }

  /** ★ #8:从当前块创建闪卡 (RemNote 模式:选中内容一键变卡片)。 */
  makeFlashcard(blockKey: string): void {
    const item = this.selected();
    if (!item) return;
    const content = this.blockText(item, blockKey);
    if (!content.trim()) { this.notify(this.t('ts.detail.blockEmpty')); return; }
    // 用块内容的前 60 字作标题,全文作问题
    const title = content.trim().slice(0, 60) + (content.length > 60 ? '…' : '');
    this.api.post<KnowledgeItem>('/api/knowledge', {
      title: `🃏 ${title}`,
      topic: item.topic,
      question: content,
      conceptExplanation: '',
      keyPointsJson: '[]',
      commonMistakesJson: '[]',
      difficulty: 3,
      importance: 3
    }).subscribe({
      next: () => {
        this.notify(this.t('ts.detail.flashcardCreated'));
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }
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
    this.snack.open(msg, this.t('common.close'), {
      duration: isError ? 5000 : 2500,
      horizontalPosition: 'center',
      verticalPosition: 'bottom'
    });
  }

  // ------------------------------ 展示辅助 ------------------------------

  masteryLabel(m: string): string {
    const key = 'ts.mastery.' + m;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : m;
  }

  /** 复习结果档位标签(找不到键时回退英文枚举)。 */
  reviewLabel(v: string): string {
    const key = 'ts.review.' + v;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : v;
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
    if (!item.nextReviewAt) return this.t('ts.detail.notScheduled');
    return this.t('ts.detail.nextReview').replace('{date}', item.nextReviewAt.slice(0, 10));
  }

  ngOnDestroy(): void {
    this.categorySubs.unsubscribe();
    this.subnav.unregister('/tech-stack');
  }
}
