import { computed, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MaterialNode } from '../../shared/material-tree/material-tree.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { MaterialTransferDialogComponent } from './material-transfer-dialog.component';
import { MaterialNodeDto, MaterialNodeIn, PracticeCategoryDto } from '../../core/api/practice-api.service';
import { serverFieldErrorSummary } from '../../core/api/validation-errors.helpers';
import { KnowledgeItem, KnowledgeTopic } from '../../core/models/api.models';
import type { AiPracticeComponent } from './ai-practice.component';


/** 标记颜色(2026-09-23 第九轮:Notion 同款颜色状态,后续可作筛选条件)。 */
export type MarkColor = 'none' | 'orange' | 'red' | 'green';
export class PracticeMaterialTree {
  constructor(private readonly host: AiPracticeComponent) {}


  // ---------- 素材树 ----------
  readonly nodes = signal<MaterialNode[]>([]);
  /** From Tech 虚拟树(独立 signal,不污染 nodes) */
  readonly fromTechNodes = signal<MaterialNode[]>([]);

  /**
   * ★ 当前正在展示的树。
   * From Tech 只是换了个数据源(实时读 Knowledge),不是新模块 ——
   * 选中/标题/重命名/展开恢复这些交互,两棵树走同一套逻辑,
   * 差别只在这里:操作哪棵树。
   */
  private activeTree(): MaterialNode[] {
    return this.isFromTechSelected() ? this.fromTechNodes() : this.nodes();
  }

  /** 换数组引用,强制树组件重渲(选中高亮/展开箭头同步)。 */
  private refreshActiveTree(): void {
    if (this.isFromTechSelected()) this.fromTechNodes.set([...this.fromTechNodes()]);
    else this.nodes.set([...this.nodes()]);
  }

  // ---------- 练习类别(★ 2026-09-25 Forrest) ----------
  /** 类别下拉的数据源(服务端加载;空 = 只有"全部/未分类"两项)。 */
  readonly categories = signal<PracticeCategoryDto[]>([]);
  /**
   * 当前类别过滤:'all' | 'uncategorized' | 类别id。
   * 过滤只作用于树**显示**(visibleNodes 在树控件内部),
   * 新建/拖拽/保存永远操作全量树,所以过滤态下也不会丢数据。
   *
   * ★ 2026-09-25 第三轮(Forrest):刷新后要记住上次选的类别,不再每次回"全部"。
   *   初值从 localStorage 恢复(与 practice.lang / user_practice_zoom 同一套约定);
   *   类别列表加载回来后若发现存的 id 已被删除,回落"全部"(见 loadCategories)。
   */
  readonly categoryFilter = signal<string>(PracticeMaterialTree.readStoredCategoryFilter());

  // ---------- Tech Stack 实时视图(Notion linked view 模式) ----------
  /**
   * 当前选中的分类是否链接到 Tech Stack。
   * 是 → 左树显示 Tech Stack 的实时分类/题目(从 Knowledge 服务查,不存副本);
   * 否 → 显示练习自己的素材树。
   */
  readonly isTechStackLinked = computed(() => {
    const f = this.categoryFilter();
    if (f === 'all' || f === 'uncategorized') return false;
    const cat = this.categories().find(c => c.id === f);
    return cat?.linkedSource === 'techstack';
  });
  /** From Tech 区是否显示(Tech Stack 分类选中时显示,不替换原树) */
  readonly showFromTech = computed(() => {
    const f = this.categoryFilter();
    if (f === 'all' || f === 'uncategorized') return false;
    const cat = this.categories().find(c => c.id === f);
    // Tech Stack 分类,或已链接的分类,都显示 From Tech 区
    return cat?.name?.toLowerCase().includes('tech stack') || cat?.linkedSource === 'techstack';
  });
  /** 下拉是否选了 "from Tech" 虚拟选项 */
  readonly isFromTechSelected = computed(() => this.categoryFilter() === '__fromTech');
  /** From Tech 区展开/折叠 */
  readonly fromTechOpen = signal(true);
  /** Tech Stack 的分类列表(实时从 Knowledge 取) */
  readonly tsTopics = signal<KnowledgeTopic[]>([]);
  /** Tech Stack 当前展开的分类 */
  readonly tsExpandedTopic = signal<string | null>(null);
  /** Tech Stack 当前分类下的条目 */
  readonly tsItems = signal<KnowledgeItem[]>([]);
  /** Tech Stack 当前选中的条目(右侧展示用) */
  readonly tsSelected = signal<KnowledgeItem | null>(null);
  readonly tsLoading = signal(false);

  toggleFromTech(): void {
    this.fromTechOpen.update(v => !v);
  }

  // ---------- 素材抽屉 ----------
  /**
   * 素材树抽屉是否展开。
   * 默认折叠 —— 练习时视线应该落在题目上,素材树是"要用才拉开"的东西。
   */
  /**
   * 左侧素材树侧栏是否展开。
   *
   * ⚠️ 2026-09-15 第六轮修复:此处原为 signal(false)。
   *    .practice:not(.tree-open) 的样式把左列压成 0 宽 + opacity:0,
   *    所以初始 false = 侧栏虽然渲染了但**物理不可见** →
   *    表现就是"左侧素材树没渲染"。任务书要求默认展开,故改为 true。
   *    折叠态仍可通过 ◀ 按钮随时收起。
   */
  readonly treeOpen = signal(true);

  /**
   * 左侧素材板块宽度(px)—— Forrest 第十三轮要求可拖动调整。
   * clamp 到 220–560px,并持久化到 localStorage,刷新后保持。
   */
  readonly treeWidth = signal<number>(PracticeMaterialTree.readTreeWidth());

  private static readonly TREE_W_KEY = 'user_practice_tree_width';
  private static readonly TREE_W_MIN = 220;
  private static readonly TREE_W_MAX = 560;

  private static readTreeWidth(): number {
    try {
      const raw = localStorage.getItem(PracticeMaterialTree.TREE_W_KEY);
      const n = raw ? parseInt(raw, 10) : NaN;
      if (Number.isFinite(n)) {
        return Math.min(PracticeMaterialTree.TREE_W_MAX,
                        Math.max(PracticeMaterialTree.TREE_W_MIN, n));
      }
    } catch { /* 读不到就用默认 */ }
    return 300;   // 默认宽度(与旧版 grid 的 300px 一致)
  }

  /**
   * 开始拖动中间分割线。用 window 级 mousemove/mouseup,
   * 保证鼠标移出细线也能继续拖(否则体验很差)。
   */
  startResize(ev: MouseEvent): void {
    ev.preventDefault();
    const startX = ev.clientX;
    const startW = this.treeWidth();

    const onMove = (e: MouseEvent) => {
      const next = startW + (e.clientX - startX);
      this.treeWidth.set(
        Math.min(PracticeMaterialTree.TREE_W_MAX,
                 Math.max(PracticeMaterialTree.TREE_W_MIN, next)));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try {
        localStorage.setItem(PracticeMaterialTree.TREE_W_KEY, String(this.treeWidth()));
      } catch { /* 存不了就算了,不影响使用 */ }
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    // 拖动期间锁定光标与禁选,避免拖动时选中文字
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }

  // ---------- 素材树持久化 ----------
  /**
   * 素材树是否有未保存修改。
   * 任务书第二节要求提供「保存修改」入口。这里用脏标记驱动按钮状态：
   * 改了没存 → 按钮高亮可点；已保存 → 置灰显示"已保存"。
   */
  readonly treeDirty = signal(false);

  /**
   * ★ 2026-09-20(Forrest):树的"编辑模式"开关。
   * 默认只读 —— 新建/重命名/拖拽/删除都要先点「编辑」;
   * 点「保存修改」并确认后才写数据库,没保存就不入库。
   */
  readonly treeEditing = signal(false);

  /** 进入编辑模式时的整树快照(取消编辑时恢复用)。 */
  private treeSnapshot: MaterialNode[] | null = null;

  /** 本次编辑期间是否发生过删除(保存时要带 force,越过防误删熔断)。 */
  private treeDeletedSinceSave = false;

  /** 素材树是否已从后端加载完成（加载中不显示"空树"误导用户）。 */
  readonly treeLoading = signal(false);

  /** 后端加载失败时的真实原因（不吞错，让用户知道是后端没起还是别的）。 */
  readonly treeError = signal('');

  /**
   * ★★ 第四十轮:素材树"从服务端加载失败"标志。
   * 为 true 时**禁止任何写回服务端的操作**(persistQuiet / saveTree)。
   * 原因:拉取失败时本地 nodes 可能为空或陈旧,把它整树覆盖写回 = 真丢数据。
   */
  readonly treeLoadFailed = signal(false);

  /**
   * ★★ 第四十一轮:数据库为空标志。
   * true → 界面显示"数据库暂无素材,请新建"提示,绝不自行填内容。
   */
  readonly treeEmpty = signal(false);

  /**
   * 把素材树整体保存到后端（整树覆盖）。
   *
   * 2026-09-15 第十七轮：持久化从 localStorage 迁到 PostgreSQL。
   * 为什么整树覆盖而不是逐节点 diff：
   *   素材树的编辑动作五花八门（新增/改名/改正文/拖动排序/删除/折叠），
   *   逐个维护增量接口会引入大量状态同步 bug。整树覆盖只有一次写、
   *   语义绝对明确（把当前看到的树原样存下来），对个人素材库这个体量
   *   （几十到几百节点）开销完全可以忽略。
   */
  saveTree(): void {
    // ★★ 第四十轮 数据安全:加载失败时绝不允许把本地(可能是空的/陈旧的)
    //   整树写回服务端 —— 那会把数据库真数据整树覆盖。
    if (this.treeLoadFailed()) {
      this.treeError.set(this.host.t('practice.treeBlocked'));
      return;
    }
    // ★ 2026-09-20(Forrest):保存是"写入数据库"的决定性动作,先弹确认。
    void this.confirmDialog({
      title: this.host.t('dialog.saveTitle'),
      confirmText: this.host.t('dialog.saveConfirm'),
    }).then((ok) => {
      if (!ok) return;
      this.doSaveTree();
    });
  }

  /** 确认后的真正保存(整树覆盖 + 回读对齐 GUID)。after 可选:保存成功后执行。 */
  doSaveTree(after?: () => void): void {
    // From Tech:写回 Knowledge(同一份数据),不走 Practice API
    if (this.isFromTechSelected()) {
      this.saveFromTechToKnowledge(after);
      return;
    }
    const payload = PracticeMaterialTree.toPayload(this.nodes());
    // ★ 本次编辑期间删过东西 → 带 force 越过服务端"防误删熔断"
    //   (用户已经在弹窗里确认过删除,不应被拦)。
    this.host.practiceApi.saveMaterials(payload, this.treeDeletedSinceSave).subscribe({
      next: () => {
        this.treeDirty.set(false);
        this.treeDeletedSinceSave = false;
        this.treeError.set('');
        // ★ 第二十七轮(Bug2 真根因修复):
        //   后端 PUT /materials 只返回 { saved: N } ——
        //   它给**新建**的素材生成了 GUID,却**不回传**。
        //   若不回读,前端 nodes 里可能留下非 GUID 的临时 id,
        //   于是 submitPending 的 isGuid(materialId) 判定为 false →
        //   录音**静默不上传** → 只存内存 → 刷新即丢(Forrest 报的数据丢失)。
        //   修法:保存成功后立刻回读后端树,用真实 GUID 替换本地节点。
        this.refreshTreeFromServer();
        // 保存成功 = 退出编辑模式,回到只读
        this.treeEditing.set(false);
        this.treeSnapshot = null;
        this.host.toast(this.host.t('dialog.saveDone'));
        after?.();
      },
      error: (e) => {
        // 绝不假装保存成功 —— 把后端给的真实原因显示出来
        this.treeError.set(this.errText(e));
        this.treeDirty.set(true);
        // ★ 票111 段2:树摘要冲突(409)—— 提示已由 ApiClient 统一口径给出,
        //   再回读整树对齐最新状态(本地这份快照已过期,留着只会再撞一次)。
        if ((e as { status?: number })?.status === 409) this.refreshTreeFromServer();
      }
    });
  }

  /** From Tech:把编辑后的推荐答案写回 Knowledge(同一份数据) */
  private saveFromTechToKnowledge(after?: () => void): void {
    const updates: { kid: string; name: string; content: string; version: number | null }[] = [];
    const walk = (nodes: MaterialNode[]) => {
      for (const n of nodes) {
        if (!n.folder && n.id.startsWith('ts:item:')) {
          const kid = (n as any)._kid as string;
          if (kid) {
            updates.push({ kid, name: n.name, content: n.content || '', version: (n as any)._version ?? null });
          }
        }
        if (n.children) walk(n.children);
      }
    };
    walk(this.fromTechNodes());
    if (updates.length === 0) {
      this.treeDirty.set(false);
      this.treeEditing.set(false);
      after?.();
      return;
    }
    // 逐个 PUT 到 Knowledge(一次写回题目+推荐答案,避免并发 xmin 冲突)
    // 串行发送,避免并行导致 xmin 冲突
    let failed = 0;
    const sendNext = (index: number) => {
      if (index >= updates.length) {
        if (failed === 0) {
          this.treeDirty.set(false);
          this.treeEditing.set(false);
          this.host.toast(this.host.t('dialog.saveDone'));
          // 重载 From Tech 树,确保与 Knowledge 一致
          this.loadTsTopics();
        } else {
          this.treeError.set(this.host.t('practice.tsSavePartial'));
        }
        after?.();
        return;
      }
      const u = updates[index];
      this.host.knowledgeApi.updateFromTech<void>(u.kid, {
        question: u.name || null,
        betterAnswer: u.content || null,
        version: u.version
      }).subscribe({
        next: () => sendNext(index + 1),
        error: () => { failed++; sendNext(index + 1); }
      });
    };
    sendNext(0);
  }

  // ---------- 树编辑模式(2026-09-20,Forrest) ----------

  /** 进入编辑模式:先拍快照,取消时可完整恢复。 */
  startTreeEdit(): void {
    if (this.treeLoadFailed()) return;
    this.treeSnapshot = JSON.parse(JSON.stringify(this.nodes())) as MaterialNode[];
    this.treeDeletedSinceSave = false;
    this.treeEditing.set(true);
  }

  /** 取消编辑:放弃未保存的改动,恢复到进入编辑时的样子。 */
  cancelTreeEdit(): void {
    if (!this.treeEditing()) return;
    if (this.treeDirty()) {
      if (this.treeSnapshot) this.nodes.set(this.treeSnapshot);
      this.treeDirty.set(false);
      this.host.toast(this.host.t('dialog.discardToast'));
    }
    this.treeSnapshot = null;
    this.treeDeletedSinceSave = false;
    this.treeEditing.set(false);
    // 恢复快照后,原选中的素材可能已经不在了 → 重新对齐右侧内容
    const id = this.host.selectedId();
    const target = id ? this.findFile(this.nodes(), id) : null;
    if (target && !target.folder) {
      this.host.selectFile(target);
    } else {
      const first = this.firstFile(this.nodes());
      if (first) this.host.selectFile(first);
      else {
        this.host.selectedId.set(null);
        this.host.saved.set('');
        this.host.draft.set('');
        this.host.editing.set(false);
      }
    }
  }

  /**
   * 统一确认弹窗。
   * 返回值:true = 确认按钮;'discard' = 第三个按钮(放弃);其它 = 取消。
   * 全站弹窗风格一致 —— 保存/删除/刷新拦截都走这一个组件。
   */
  confirmDialog(
    opts: Omit<ConfirmDialogData, 'cancelText'> & { cancelText?: string },
    width = '360px'
  ): Promise<boolean | 'discard'> {
    const ref = this.host.dialog.open(ConfirmDialogComponent, {
      width,
      panelClass: 'app-confirm',
      autoFocus: false,
      data: {
        ...opts,
        cancelText: opts.cancelText ?? this.host.t('dialog.cancel')
      } as ConfirmDialogData
    });
    return firstValueFrom(ref.afterClosed()).then((v) =>
      v === true ? true : v === 'discard' ? 'discard' : false
    );
  }

  /**
   * 从后端回读整棵树,替换本地节点(拿到真正的 GUID)。
   *
   * ★ 第二十七轮新增。用途:
   *   1. saveTree 成功后同步服务端生成的 GUID(否则录音上传永远被 isGuid 挡住);
   *   2. 任何"本地 id 与服务端 id 可能不一致"的时刻,重新对齐。
   *
   * 为什么必须回读:后端新建素材时会生成新 GUID,但 PUT 的响应体里没有它。
   *   不回读 = 前端永远不知道自己的种子 id 已被服务端换成 GUID。
   */
  private refreshTreeFromServer(): void {
    this.host.practiceApi.getMaterials().subscribe({
      next: (dtos) => {
        if (!dtos.length) return;   // 异常情况:服务端为空,保持本地不动
        const prevSelected = this.host.selectedId();
        const fresh = PracticeMaterialTree.fromDto(dtos);
        this.nodes.set(fresh);
        // 新树的 id→名字 进缓存(id 换成 GUID 后仍能按名回找)
        this.cacheNames(fresh);
        // ★ 第四十四轮:回读会带来数据库里旧的 expanded 值,
        //   这里按本地记录重新覆盖一次,展开状态不被保存回读冲掉。
        this.restoreExpandedState();
        // 选中项也要按"名字"对齐到新 GUID —— 否则 selectedId 悬空,
        // 界面看起来"没选中任何素材",录音无处可挂。
        if (prevSelected) {
          const remapped = this.findByLegacyIdOrName(prevSelected);
          if (remapped) {
            this.host.selectedId.set(remapped.id);
            this.host.rememberMaterialId(remapped.id);
          }
        }
        // ★ 第二十七轮:树里现在有真 GUID 了 → 把积压的录音补传上去。
        //   映射规则:旧 id 找不到时按"记忆中同 id 的旧节点名字"回找。
        this.host.recorder.flushPendingUploads((oldId) => {
          const hit = this.findById(this.nodes(), oldId);
          return hit ? hit.id : null;
        });
        // ★ 第三十七轮:回读后**重同步编辑缓冲**,否则用户的输入会被换成旧快照。
        //   场景:用户正在编辑某素材,此时另一个自动保存的回包把 nodes
        //   整体换新 —— selectedId 已按名对齐到新 GUID,但 saved/draft 还是旧值。
        //   若不重同步,用户看到的正文会"退回"到保存前的样子(以为没存上)。
        //   规则:未在编辑态时,把缓冲刷新为该节点在**新树里的真实内容**。
        if (!this.host.editing()) {
          const cur = this.findFile(this.nodes(), this.host.selectedId());
          const text = cur?.content ?? '';
          this.host.saved.set(text);
          this.host.draft.set(text);
        }
      },
      error: () => {
        // 回读失败不影响已成功的保存 —— 但要记住树脏了(下次再对齐)
        this.treeDirty.set(true);
      }
    });
  }

  /**
   * 用旧的(可能已失效的)id 或名字,在新树里找回对应节点。
   * 场景:保存前 selectedId 是一个已失效的 id,保存后端换成 GUID;
   *       旧 id 已不存在 → 退化为按"内容/名字"匹配。
   */
  private findByLegacyIdOrName(oldId: string): MaterialNode | null {
    const byId = this.findById(this.nodes(), oldId);
    if (byId) return byId;
    const firstName = this.lastKnownName.get(oldId);
    if (firstName) {
      const byName = this.findFileByName(this.nodes(), firstName);
      if (byName) return byName;
    }
    return null;
  }

  /** 按 id 在整棵树里找节点。 */
  private findById(list: MaterialNode[], id: string): MaterialNode | null {
    for (const n of list) {
      if (n.id === id) return n;
      const hit = n.children?.length ? this.findById(n.children, id) : null;
      if (hit) return hit;
    }
    return null;
  }

  /** 按名字找第一个文件节点(名字在同一用户下通常唯一,够用)。 */
  private findFileByName(list: MaterialNode[], name: string): MaterialNode | null {
    for (const n of list) {
      if (!n.folder && n.name === name) return n;
      const hit = n.children?.length ? this.findFileByName(n.children, name) : null;
      if (hit) return hit;
    }
    return null;
  }

  /** 旧 id → 最近一次已知的名字(用于 id 失效后按名回找)。 */
  readonly lastKnownName = new Map<string, string>();

  /** 递归把整棵树的 id→名字 写进缓存。 */
  private cacheNames(list: MaterialNode[]): void {
    for (const n of list) {
      this.lastKnownName.set(n.id, n.name);
      if (n.children?.length) this.cacheNames(n.children);
    }
  }

  /**
   * 启动时从后端加载素材树。
   *
   * ★★ 第四十一轮(Forrest 明确要求):
   *   ——**数据库是素材树的唯一数据源**。
   *   前端**不再有任何硬编码种子**(原来的 4 个种子 = 12 个节点已彻底删除),
   *   也不再从 localStorage 导入旧树。
   *   数据库为空时,就**空着**,并在界面给出明确提示文字,
   *   绝不自行脑补内容、绝不写回任何东西。
   */
  restoreTree(): void {
    this.treeLoading.set(true);
    this.loadCategories();   // ★ 2026-09-25:类别与树并行加载;类别失败不阻塞树
    this.host.practiceApi.getMaterials().subscribe({
      next: (dtos) => {
        this.nodes.set(PracticeMaterialTree.fromDto(dtos ?? []));
        this.treeEmpty.set(dtos.length === 0);
        this.treeLoading.set(false);
        this.treeLoadFailed.set(false);
        this.treeError.set('');
        this.afterTreeReady();
      },
      error: (e) => {
        // ★★ 第四十轮 数据安全修复(Forrest 报"数据全丢失、树回到初始状态"):
        //   拉取失败时**绝不改动 nodes**,只报错 + 标未连接,并锁住写操作。
        //   宁可页面空白,也绝不把猜测状态覆盖到服务端。
        this.treeError.set(this.errText(e));
        this.treeLoading.set(false);
        this.treeLoadFailed.set(true);
        this.treeEmpty.set(false);
        this.nodes.set([]);
        this.afterTreeReady();
      }
    });
  }

  // ★★ 第四十一轮:LEGACY_TREE_KEY / readLegacyTree / importSeed 已**全部删除**。
  //   原因(Forrest 明确要求):数据库是唯一数据源。
  //   · 不再从 localStorage 导入旧素材树(那也是“非数据库来源”的数据)。
  //   · 不再向数据库写入任何硬编码种子。
  //   空库就空着,界面给提示文字,由用户自己新建。

  // ---------- 练习类别(★ 2026-09-25 Forrest) ----------

  /** 类别过滤的 localStorage 键(刷新后恢复上次选中,第三轮 Forrest)。 */
  private static readonly CATEGORY_FILTER_KEY = 'practice.categoryFilter';

  /**
   * 从 localStorage 读上次选中的类别过滤。
   * 静态方法:字段初始化器要赶在构造器之前用上它。
   * 值不合法(被篡改)一律回落"全部";隐私模式读不了也不影响。
   */
  private static readStoredCategoryFilter(): string {
    try {
      const v = localStorage.getItem(PracticeMaterialTree.CATEGORY_FILTER_KEY);
      return v && v.trim() ? v.trim() : 'all';
    } catch {
      return 'all';
    }
  }

  /**
   * 加载类别下拉。失败**静默降级**:下拉只剩"全部/未分类",
   * 素材树照常可用 —— 类别是锦上添花,不该因为它挂掉整棵树。
   *
   * ★ 第三轮:加载完成后校验恢复的过滤值 —— 存的类别 id 若已被删除
   *   (比如在别的设备上删的),回落"全部",避免下拉显示成空选项。
   */
  private loadCategories(): void {
    this.host.practiceApi.getCategories().subscribe({
      next: (list) => {
        this.categories.set(list ?? []);
        this.validateCategoryFilter();
        // 若下拉选的是 from Tech,加载实时数据
        if (this.isFromTechSelected()) {
          this.loadTsTopics();
        }
      },
      error: () => this.categories.set([])
    });
  }

  /**
   * 校验当前过滤值:若指向一个已不存在的类别 id(被删了/别的设备删了),
   * 回落"全部"并清掉存储,避免下拉显示成空选项。
   */
  private validateCategoryFilter(): void {
    const f = this.categoryFilter();
    if (f === 'all' || f === 'uncategorized' || f === '__fromTech') return;
    if (this.categories().some((c) => c.id === f)) return;
    this.categoryFilter.set('all');
    try { localStorage.removeItem(PracticeMaterialTree.CATEGORY_FILTER_KEY); } catch { /* 忽略 */ }
  }

  /** 下拉切换过滤值(树控件通过事件抛上来,这里回写给 signal 并落盘)。 */
  onCategoryFilterChange(value: string): void {
    this.categoryFilter.set(value);
    try {
      localStorage.setItem(PracticeMaterialTree.CATEGORY_FILTER_KEY, value);
    } catch { /* 隐私模式写不了,不影响本次会话 */ }
    // From Tech 区:切换分类时加载 Tech Stack 实时数据
    this.tsSelected.set(null);
    this.tsExpandedTopic.set(null);
    this.tsItems.set([]);
    // 切换分类:清除旧选中(含 URL 参数)
    this.host.selectedId.set(null);
    try {
      localStorage.removeItem('active_practice_material_id');
    } catch { /* 忽略 */ }
    this.host.router.navigate([], {
      relativeTo: this.host.route,
      queryParams: { materialId: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
    if (this.isFromTechSelected()) {
      this.loadTsTopics();
    } else {
      // 普通分类:直接选中新分类的第一个文件(nodes 未被污染,无需重载)
      const visible = value === 'all'
        ? this.nodes()
        : value === 'uncategorized'
          ? this.nodes().filter(n => !n.categoryId)
          : this.nodes().filter(n => n.categoryId === value);
      const first = this.firstFile(visible);
      if (first) {
        this.expandPathTo(first);
        this.host.selectFile(first);
      } else {
        // 新分类为空:清空右侧
        this.host.saved.set('');
        this.host.draft.set('');
      }
    }
  }

  // ---------- From Tech:Tech Stack 实时内容(下拉虚拟选项) ----------

  /** 加载 Tech Stack 分类(从 Knowledge 实时查) */
  loadTsTopics(): void {
    if (!this.isFromTechSelected()) return;
    this.tsLoading.set(true);
    // From Tech:构建标准 MaterialNode 树(与普通分类同一套逻辑)
    this.host.knowledgeApi.getTopics().subscribe({
      next: (list) => {
        const topics = (list ?? []).filter(t => t.topic).sort((a, b) => a.topic.localeCompare(b.topic));
        this.buildFromTechTree(topics);
      },
      error: () => { this.nodes.set([]); this.tsLoading.set(false); }
    });
  }

  /** 从 Knowledge 数据构建标准 MaterialNode 树(id 带 ts: 前缀以便保存时识别) */
  private buildFromTechTree(topics: KnowledgeTopic[]): void {
    if (topics.length === 0) {
      this.nodes.set([]);
      this.tsLoading.set(false);
      return;
    }
    const tree: MaterialNode[] = [];
    let pending = topics.length;
    const done = () => {
      pending--;
      if (pending === 0) {
        tree.sort((a, b) => a.name.localeCompare(b.name));
        this.fromTechNodes.set(tree);
        this.tsLoading.set(false);
        // 加载过程中用户已切走 → 只留树,不碰选中(切走就不恢复,和本地分类一致)。
        if (!this.isFromTechSelected()) return;
        // ★ 刷新/切回 from-tech 时,恢复上次的选中与展开 —— 和其它分类一致,
        //   不再每次都跳到第一个条目。
        //   选中:URL 参数优先,其次上次记住的 ts: 条目,都没有才取第一个。
        const wanted = this.host.route.snapshot.queryParamMap.get('materialId')
          || this.host.readStoredMaterialId();
        const target = wanted ? this.findFile(tree, wanted) : null;
        const first = target && !target.folder ? target : this.firstFile(tree);
        if (first) {
          // 展开:本地记录里有这棵实时树的展开记录就原样恢复(尊重用户收起);
          //      首次进来则自动展开到当前条目(和本地分类首次使用一致)。
          this.restoreExpandedState();
          if (!this.hasFromTechExpandedRecord()) {
            this.expandPathTo(first);
          }
          this.host.selectFile(first);
        }
      }
    };
    for (const tp of topics) {
      const folder: MaterialNode = {
        id: `ts:topic:${tp.topic}`,
        name: tp.topic,
        folder: true,
        expanded: false,
        children: [],
        categoryId: '__fromTech'
      };
      tree.push(folder);
      this.host.knowledgeApi.getItemsByTopic(tp.topic, 100).subscribe({
        next: (res) => {
          const items = res.items ?? [];
          folder.children = items.map(k => ({
            id: `ts:item:${k.id}`,
            name: k.title || k.question,
            folder: false,
            content: k.betterAnswer || k.conceptExplanation || '',
            // 存原始 Knowledge ID 以便保存时写回
            sortOrder: 0,
            categoryId: '__fromTech'
          } as MaterialNode & { _kid?: string }));
          // 把 Knowledge ID 藏在 id 里,保存时解析
          folder.children.forEach((c, i) => {
            const kid = items[i]?.id;
            (c as any)._kid = kid;
            (c as any)._version = items[i]?.version ?? null; // 票101:读时 xmin,保存回传
          });
          done();
        },
        error: () => done()
      });
    }
  }

  /** 加载某分类下的 Tech Stack 条目 */
  loadTsItems(topic: string): void {
    this.tsExpandedTopic.set(topic);
    this.tsLoading.set(true);
    this.host.knowledgeApi.getItemsByTopic(topic, 100).subscribe({
      next: (res) => {
        this.tsItems.set(res.items ?? []);
        this.tsLoading.set(false);
      },
      error: () => { this.tsItems.set([]); this.tsLoading.set(false); }
    });
  }

  /** 选中 Tech Stack 条目(右侧展示) */
  selectTsItem(item: KnowledgeItem): void {
    this.tsSelected.set(item);
    this.host.selectedId.set(null);
    this.host.editing.set(false);
  }

  /** 跳到 Tech Stack 编辑该条目(Obsidian "view source" 模式) */
  editInTechStack(): void {
    const item = this.tsSelected();
    if (item) {
      this.host.router.navigate(['/techstack'], { queryParams: { id: item.id } });
    }
  }

  /** 解链 Tech Stack 分类,恢复原来的材料树 */
  unlinkTechStack(): void {
    const catId = this.categoryFilter();
    if (!catId || catId === 'all') return;
    this.host.practiceApi.setCategoryLink(catId, null).subscribe({
      next: () => {
        // 刷新分类列表,linkedSource 清掉后自动切回材料树
        this.host.practiceApi.getCategories().subscribe({
          next: (cats) => this.categories.set(cats),
          error: () => {}
        });
        this.tsSelected.set(null);
        this.tsTopics.set([]);
        this.tsItems.set([]);
      },
      error: (e: Error) => this.host.toast(e.message)
    });
  }

  /**
   * ★ 第九轮(Forrest):类别管理与导入导出**合并成一个弹窗的三个页签**。
   * 两个工具栏入口(menu 的 tune 图标 = 类别页签,import_export 图标 = 导出页签)
   * 打开的是同一个弹窗,只是初始落点不同,窗口尺寸一致。
   * 关闭后:用回传的类别列表刷新下拉;合并回传的素材骨架节点。
   */
  openManage(tab: 'categories' | 'export' = 'export'): void {
    if (this.treeLoadFailed()) {
      this.host.toast(this.host.t('practice.treeBlocked'));
      return;
    }
    const ref = this.host.dialog.open(MaterialTransferDialogComponent, {
      width: '680px',
      // ★ 第十一轮:默认更高(min(80vh, 760px)),多显示内容但不无限大;
      //   用户可在 min/max 区间内自己拖拽缩放(见 styles.scss .app-transfer)。
      height: 'min(80vh, 760px)',
      maxWidth: '94vw',
      maxHeight: '94vh',
      panelClass: 'app-transfer',
      data: { nodes: this.nodes(), categories: this.categories(), filter: this.categoryFilter(), tab }
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      // 类别可能在「类别」页签里被增删改/导入模板 → 下拉立刻对齐
      if (result.categories?.length != null) this.categories.set(result.categories);
      this.validateCategoryFilter();
      this.loadCategories();
      // 弹窗里任何来源(模板导入 / 文件导入)产出的素材骨架 → 合并进整树
      this.mergeImportedNodes(result.importedNodes ?? []);
    });
  }

  /**
   * 把「模板导入 / 文件导入」产生的根节点合并进整树并落库。
   * 三条约定:
   *  1. 树加载失败时一律不写服务端(防止用陈旧的本地数据覆盖真数据);
   *  2. 正在编辑时同步刷新快照 —— 之后点"取消"回到的是导入后的状态,不吞导入;
   *  3. 非编辑态直接持久化:导入是用户显式动作,不必再弹一次保存确认。
   */
  private mergeImportedNodes(imported: MaterialNode[]): void {
    if (imported.length === 0) return;
    if (this.treeLoadFailed()) {
      this.host.toast(this.host.t('practice.treeBlocked'));
      return;
    }
    this.nodes.update((list) => [...list, ...imported]);
    this.treeDeletedSinceSave = false;
    this.treeDirty.set(true);
    if (this.treeEditing()) {
      this.treeSnapshot = JSON.parse(JSON.stringify(this.nodes())) as MaterialNode[];
    } else {
      this.doSaveTree();
    }
  }

  /**
   * ★★ 第四十一轮:重试加载素材树。
   * 加载失败后用户点「重试」时调用 —— 清掉失败标志,重新走一次
   * "从数据库读取" 的完整流程。
   */
  reloadTree(): void {
    this.treeLoadFailed.set(false);
    this.treeError.set('');
    this.restoreTree();
  }

  /** 素材树就绪后的收尾：恢复上次选中的素材。 */  private afterTreeReady(): void {
    // ★ 第三十九轮 数据安全兼底:树就绪后,把服务端全部录音拉一次,
    //   跟当前树的素材 id 对齐。即使某个录音因素材 id 变动而“掉队”,
    //   也能在此补回内存,不会“凭空消失”。
    this.host.recorder.syncAllRecordings(this.allMaterialIds());
    // ★ From Tech:选中恢复由 loadTsTopics→buildFromTechTree 负责(本地树里没有 ts: id)。
    //   这里若不返回,会误选本地第一个文件,并把 localStorage 里存着的 ts: id 覆盖掉,
    //   导致刷新后 from-tech 永远回到第一个条目 —— 和其它分类"刷新保持原状"不一致。
    if (this.isFromTechSelected()) return;
    const fromUrl = this.host.route.snapshot.queryParamMap.get('materialId');
    const wanted = fromUrl || this.host.readStoredMaterialId();
    const target = wanted ? this.findFile(this.nodes(), wanted) : null;
    const first = target && !target.folder ? target : this.firstFile(this.nodes());
    if (first) {
      // ★ 2026-09-20(Forrest 第一条):刷新后左树要**自动展开**到当前素材 ——
      //   否则右侧显示的是它,左边却是一排折叠的文件夹,看起来像丢了。
      // ★ 第四十四轮(Forrest):但优先恢复用户自己展开/收起的状态 ——
      //   本地有记录就原样还原(哪怕选中素材的父级是收起的,也尊重用户);
      //   没有记录(首次使用)才走"自动展开到当前素材"。
      if (!this.restoreExpandedState()) {
        this.expandPathTo(first);
      }
      this.host.selectFile(first);
    }
  }

  /**
   * ★ 2026-09-20(Forrest 第一条):把树里通向 target 的每一层文件夹展开。
   * 只改内存态(不标脏、不落盘),展开状态本来就会随整树保存写库;
   * 这里解决的是"回读时库里 expanded=false → 刷新后父级全折叠"的情况。
   */
  private expandPathTo(target: MaterialNode): void {
    const list = this.activeTree();
    const path: MaterialNode[] = [];
    const walk = (l: MaterialNode[], stack: MaterialNode[]): boolean => {
      for (const n of l ?? []) {
        if (n.id === target.id) { path.push(...stack); return true; }
        if (n.folder && n.children?.length && walk(n.children, [...stack, n])) return true;
      }
      return false;
    };
    if (!walk(list, [])) return;      // 没找到就什么都不动
    let changed = false;
    for (const folder of path) {
      if (folder.folder && !folder.expanded) { folder.expanded = true; changed = true; }
    }
    // 换一个数组引用,保证树组件一定重渲(选中高亮 + 展开箭头同步)
    if (changed) this.refreshActiveTree();
  }

  /** ★ 第三十九轮:收集当前树里所有节点的 id(用于录音对齐)。 */
  private allMaterialIds(): Set<string> {
    const out = new Set<string>();
    const walk = (list: MaterialNode[]) => {
      for (const n of list) {
        out.add(n.id);
        if (n.children?.length) walk(n.children);
      }
    };
    walk(this.nodes());
    return out;
  }

  /** 前端节点 → 后端提交结构。改动本地结构时只改这一个映射。 */
  private static toPayload(list: MaterialNode[]): MaterialNodeIn[] {
    // ★ 第三十八轮修复(Forrest 报“拖拽排序不能保存”):
    //   旧代码把 sortOrder 硬写成 0 —— 于是无论用户在界面上怎么拖拽,
    //   落库同层全部是 0,回读时后端只能按 CreatedAt 排 →
    //   拖完看着对了,一刷新就打回原样。
    //   现在**用数组下标当 sortOrder**:数组顺序 = 用户看到的顺序,
    //   下标即权威序号,拖拽后位置变了序号就跟着变,自然落库。
    return list.map((n, i) => ({
      id: n.id,
      name: n.name,
      folder: n.folder,
      content: n.content ?? null,
      sortOrder: i,
      expanded: n.folder ? !!n.expanded : true,
      markColor: n.markColor ?? null,        // ★ 第十三轮:标记色随整树保存
      categoryId: n.categoryId ?? null,      // ★ 2026-09-25:类别随整树保存(服务端只认根节点)
      children: PracticeMaterialTree.toPayload(n.children ?? [])
    }));
  }

  /** 后端结构 → 前端节点。 */
  private static fromDto(list: MaterialNodeDto[]): MaterialNode[] {
    // ★ 第三十八轮:回读时**按 sortOrder 排序**,而不是听信传输顺序。
    //   后端已按 SortOrder 排过,这里再排一次是纵深防御 ——
    //   即使将来某个端点忘了排序,前端也不会把顺序打乱。
    return [...(list ?? [])]
      .sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))
      .map(d => ({
        id: d.id,
        name: d.name,
        folder: d.folder,
        sortOrder: d.sortOrder ?? 0,
        content: d.content ?? '',
        expanded: !!d.expanded,
        markColor: d.markColor ?? null,      // ★ 第十三轮:标记色从库回读
        categoryId: d.categoryId ?? null,    // ★ 2026-09-25:类别从库回读
        children: PracticeMaterialTree.fromDto(d.children ?? [])
      }));
  }

  errText(e: unknown): string {
    // 票109:字段级校验错误(errors{})优先透出具体文案(如素材名超 300),
    // 只读 e.message 时只剩通用英文标题。
    return (serverFieldErrorSummary(e)
      ?? String((e as { message?: string } | null)?.message ?? e ?? this.host.t('practice.errUnknown'))).slice(0, 200);
  }

  // ---------- 2026-09-16 第十九轮:朗读文本「标记」开关 ----------
  // ★ 2026-09-23(Forrest 第十三轮,严重 Bug 修复):标记语义纠正 ——
  //   标记属于**素材条目本身**(如"公司业务"这一条),**不是给正文涂色**。
  //   旧实现有两个错:
  //     1) 选中颜色后把整段正文涂上荧光色 —— 看起来像"对每句话都标了";
  //     2) 只存 localStorage —— 换浏览器/清缓存就丢,更没法后续筛选。
  //   现在:标记是节点上的一个字段(markColor),选完立即 PATCH 到数据库,
  //   树上显示彩色圆点,之后可以直接按颜色筛选(如"把红色标的都列出来")。
  /** 颜色选项:圆点用 Notion 浅色模式图标色。 */
  readonly markColorOptions: { key: MarkColor; label: string; dot: string }[] = [
    { key: 'none', label: 'practice.markNone', dot: '#e1e4ec' },
    { key: 'orange', label: 'practice.markOrange', dot: '#b06a00' },
    { key: 'red', label: 'practice.markRed', dot: '#b3261e' },
    { key: 'green', label: 'practice.markGreen', dot: '#2e3a8c' },
  ];

  /**
   * 标记变更计数器 —— Angular 的 computed 只追踪 signal 本身,
   * 直接改节点对象上的 markColor 字段不会触发重算,必须"敲一下钟"。
   */
  private readonly markTick = signal(0);

  /** 当前素材的标记颜色 —— 直接读节点字段(单一事实来源,不再另存一份)。 */
  readonly markColor = computed<MarkColor>(() => {
    this.markTick();                                   // 依赖计数器:标记一变立即重算
    const id = this.host.selectedId();
    const node = id ? this.findById(this.nodes(), id) : null;
    const c = node?.markColor;
    return c === 'orange' || c === 'red' || c === 'green' ? c : 'none';
  });

  /** 当前颜色对应的圆点色(给 Mark 按钮的旗子图标着色)。 */
  markDot(): string {
    return this.markColorOptions.find((c) => c.key === this.markColor())?.dot ?? '#e1e4ec';
  }

  /**
   * 选择标记颜色(none = 清除):更新节点字段 + 立即 PATCH 落库。
   * 乐观更新 —— 先改界面再发请求;失败则回滚并 toast,不让用户以为存上了。
   */
  setMarkColor(c: MarkColor): void {
    const id = this.host.selectedId();
    if (!id) return;
    const node = this.findById(this.nodes(), id);
    if (!node) return;
    const prev = node.markColor ?? null;
    node.markColor = c === 'none' ? null : c;          // 乐观更新(树是受控可变数据)
    this.markTick.update(v => v + 1);                  // 通知 computed 重算
    this.host.practiceApi.setMaterialMark(id, c).subscribe({
      error: () => {
        node.markColor = prev;                         // 失败回滚
        this.markTick.update(v => v + 1);
        this.host.toast(this.host.t('practice.retryLater'));
      }
    });
  }

  onTreeChange(next: MaterialNode[]): void {
    if (this.isFromTechSelected()) {
      this.fromTechNodes.set([...next]);
    } else {
      this.nodes.set([...next]);
    }
    // 树变了 → 标记未保存,驱动侧栏「保存修改」按钮亮起
    this.treeDirty.set(true);
    // ★ 2026-09-20(Forrest):不再自动落盘 —— 只有点「保存修改」并确认
    //   才写数据库。没保存就不入库,这是本轮明确的行为约定。
  }

  /**
   * 文件夹展开/收起 —— ★ 2026-09-19 修复(Forrest 报"点任何树节点
   * Save Changes 都被激活"):
   *
   *   展开/收起是**纯 UI 状态**,不是内容变更。旧链路里它走 nodeChange,
   *   而 onTreeChange 会无条件 treeDirty.set(true) 并触发 persistQuiet() ——
   *   于是用户每点一下文件夹,侧栏就亮起"保存修改",还多发一次保存请求。
   *
   *   现在单独处理:只把节点状态映回本地数组,**不置脏、不落盘**。
   *   expanded 仍会在下次真实变更时随整树一起保存(见 toPayload)。
   */
  onFolderToggle(node: MaterialNode): void {
    // 组件内已就地改了 expanded,这里只需把数组引用换新,触发变更检测。
    // ★ from-tech:刷新的是实时树那根 signal,不再错刷 nodes()。
    this.refreshActiveTree();
    // ★ 第四十四轮(Forrest):展开/收起状态立即记入本地 ——
    //   刷新页面后原样恢复,不用再依赖"整树保存"才落库。
    this.saveExpandedState();
  }

  // ---------- 展开/收起状态持久化(★ 第四十四轮,Forrest) ----------
  /**
   * 需求:树上文件夹展开/收起是纯 UI 状态,但刷新后要**维持原样**。
   * 方案:每次展开/收起(含一键展开/收起)把"当前展开的文件夹 id 集合"
   *   写进 localStorage;加载树(以及保存后回读)时优先按它恢复。
   * 为什么不直接依赖数据库的 expanded 字段:展开不置脏、不触发保存,
   *   只有用户点保存才会写库 —— 单靠它刷新后会回到上次保存时的样子。
   * localStorage 只存 id 列表,树本身仍以数据库为唯一数据源,互不冲突。
   */
  private static readonly TREE_EXPANDED_KEY = 'practice.tree.expanded';

  /** 把当前"展开着的文件夹 id 集合"写入 localStorage。 */
  private saveExpandedState(): void {
    const ids: string[] = [];
    const walk = (list: MaterialNode[]): void => {
      for (const n of list ?? []) {
        if (n.folder && n.expanded) ids.push(n.id);
        if (n.children?.length) walk(n.children);
      }
    };
    // ★ from-tech 的 topic 文件夹(id 为 ts:topic:…)也记进来 ——
    //   两棵树 id 命名空间天然不重叠(ts:topic:… vs GUID),同一份记录互不干扰。
    //   写入时只替换当前这棵树的记录,另一棵树的原样保留 —— 否则切树 toggle
    //   一次就会把另一棵树的展开状态洗掉。
    walk(this.activeTree());
    try {
      const raw = localStorage.getItem(PracticeMaterialTree.TREE_EXPANDED_KEY);
      const arr = raw ? JSON.parse(raw) : [];
      const prev: string[] = Array.isArray(arr)
        ? arr.filter((x): x is string => typeof x === 'string')
        : [];
      const fromTech = this.isFromTechSelected();
      const keep = prev.filter((id) => fromTech ? !id.startsWith('ts:topic:') : id.startsWith('ts:topic:'));
      const merged = [...keep, ...ids.filter((id) => !keep.includes(id))];
      localStorage.setItem(PracticeMaterialTree.TREE_EXPANDED_KEY, JSON.stringify(merged));
    } catch { /* 隐私模式写不了就算了,不影响本次会话 */ }
  }

  /**
   * 按本地记录恢复展开/收起状态(当前展示的那棵树:本地树或 from-tech 实时树)。
   * @returns true = 本地有记录并已应用;false = 没有记录(首次使用/存储被清)。
   * 恢复是"权威覆盖":数据库里 expanded 是上次保存时的旧值,以本地为准。
   * ★ from-tech 复用同一套:walk 的是 activeTree(),两棵树 id 命名空间天然不重叠
   *   (ts:topic:… vs GUID),同一份记录互不干扰。
   */
  private restoreExpandedState(): boolean {
    try {
      const raw = localStorage.getItem(PracticeMaterialTree.TREE_EXPANDED_KEY);
      if (raw === null) return false;
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr)) return false;
      const ids = new Set<string>(arr.filter((x): x is string => typeof x === 'string'));
      let changed = false;
      const walk = (list: MaterialNode[]): void => {
        for (const n of list ?? []) {
          if (n.folder) {
            const want = ids.has(n.id);
            if (!!n.expanded !== want) { n.expanded = want; changed = true; }
          }
          if (n.children?.length) walk(n.children);
        }
      };
      walk(this.activeTree());
      if (changed) this.refreshActiveTree();
      return true;
    } catch {
      return false;
    }
  }

  /**
   * ★ from-tech 专用:本地展开记录里有没有这棵实时树的记录。
   * 有 → restoreExpandedState() 已按它原样恢复,调用方不再自动展开(尊重用户);
   * 没有(首次进 from-tech)→ 调用方走"自动展开到当前条目",和本地分类首次使用一致。
   */
  private hasFromTechExpandedRecord(): boolean {
    try {
      const raw = localStorage.getItem(PracticeMaterialTree.TREE_EXPANDED_KEY);
      const arr = raw ? JSON.parse(raw) : [];
      return Array.isArray(arr) && arr.some((x) => typeof x === 'string' && x.startsWith('ts:topic:'));
    } catch {
      return false;
    }
  }

  /**
   * 静默保存到后端(自动持久化用)。
   *
   * 为什么要静默:拖动排序会连发多次变更事件,每次都弹错会刷屏。
   * 但**绝不静默吞掉失败** —— 失败时置回 treeDirty 并记录 treeError,
   * 用户仍能看到"未保存"状态与具体原因,不会误以为存住了。
   */
  persistQuiet(force = false): void {
    // From Tech:虚拟树不走 Practice API(内容编辑已在 saveEdit 直接写回 Knowledge)。
    if (this.isFromTechSelected()) {
      this.treeDirty.set(false);
      return;
    }
    // ★ 第三十七轮 严重 Bug 修复(Forrest 报「新建文件夹+素材+内容,保存后刷新就丢」)。
    //
    // 真根因:并发【整树覆盖保存】互相踩踏。
    //   · 每次树变更都发一个 PUT /materials,而后端是**整树覆盖**语义。
    //   · 建成一个文件夹后立刻再建素材、再输内容 → 会连发多个 PUT。
    //   · 每个 PUT 的成功回调都会 refreshTreeFromServer(),
    //     用服务端快照**整体替换 this.nodes**。
    //   · 于是「先发的 PUT」的回包,会把「后加的东西」直接从本地树上抹掉;
    //     而后续 PUT 又是基于被抹过的树生成的 payload → 真丢数据。
    //
    // 修法:把保存**串行化**(单飞 + 合并排队):
    //   · 有请求在飞时,只记一个 "dirty" 标记,不再并发发第二个;
    //   · 在飞请求回来后,若期间又有改动(dirty),再补发一次最新整树。
    //   这样任意时刻最多一个 PUT 在飞,回读永远不会晚于基于旧树的覆盖,
    //   且最后一次补发一定带的是**最新最全**的树 —— 用户的东西不会丢。
    if (this.saveInFlight) {
      this.saveQueued = true;
      this.saveQueuedForce = this.saveQueuedForce || force;
      return;
    }
    // ★★ 第四十轮 数据安全:从服务端加载失败时,**禁止**把本地树写回。
    //   此时 this.nodes() 可能是初始空树/陈旧数据,整树覆盖写回 = 抹掉云端真数据。
    if (this.treeLoadFailed()) {
      this.treeDirty.set(true);
      return;
    }
    this.saveInFlight = true;
    this.saveQueued = false;
    this.saveQueuedForce = false;
    this.host.practiceApi.saveMaterials(PracticeMaterialTree.toPayload(this.nodes()), force).subscribe({
      next: () => {
        this.treeError.set('');
        // ★ 第二十七轮:自动保存同样要回读 —— 否则拖拽/新建之后
        //   前端仍持种子 id,录音上传被 isGuid 挡住(同 saveTree 的坑)。
        this.refreshTreeFromServer();
        this.finishSave();
      },
      error: (e) => {
        this.treeError.set(this.errText(e));
        this.treeDirty.set(true);
        // ★ 票111 段2:同 doSaveTree —— 409 树摘要冲突时回读整树对齐。
        if ((e as { status?: number })?.status === 409) this.refreshTreeFromServer();
        this.finishSave();
      }
    });
  }

  /**
   * 一次保存结束后的收尾。
   *
   * ★ 第三十七轮:若在飞期间又攒了改动(saveQueued),
   *   立刻补发一次**最新整树**。用 setTimeout(0) 让当前
   *   调用栈先跑完(refreshTreeFromServer 已把 nodes 更新好),
   *   保证补发的是最终状态,而不是中途快照。
   */
  private finishSave(): void {
    this.saveInFlight = false;
    if (this.saveQueued) {
      this.saveQueued = false;
      const f = this.saveQueuedForce;
      this.saveQueuedForce = false;
      setTimeout(() => this.persistQuiet(f), 0);
      return;
    }
    // ★ F-10:队列排空后消费一次性完成回调(新建素材落库反馈)。
    const cb = this.afterSaveOnce;
    this.afterSaveOnce = null;
    cb?.();
  }

  /** ★ F-10:一次性保存完成回调(finishSave 在队列排空时消费)。 */
  private afterSaveOnce: (() => void) | null = null;

  /**
   * ★ F-10:新建素材(文件/文件夹)在改名提交时立即落库。
   *
   * 背景:素材树的结构编辑走"显式保存"约定(点保存图标 + 确认才写库),
   * 但对**新建**这个动作,用户的直觉是"改完名回车 = 建好了"。
   * 此前新建节点不点保存就刷新 → 条目消失且毫无提示,看起来像数据凭空丢失。
   * 新建是纯新增、整树串行保存(persistQuiet)不会踩踏既有内容,
   * 因此新建单独走自动落库;既有内容的编辑仍保持显式保存约定。
   */
  onNodeCreated(_node: MaterialNode): void {
    if (this.isFromTechSelected()) return;   // 虚拟树不走 Practice API(与 persistQuiet 一致)
    if (this.treeLoadFailed()) {
      // 数据安全守卫拦截写回时必须让用户看见,不能静默丢弃新建。
      this.treeError.set(this.host.t('practice.treeBlocked'));
      return;
    }
    this.treeDirty.set(true);
    this.afterSaveOnce = () => {
      if (!this.treeError()) {
        this.treeDirty.set(false);
        this.host.toast(this.host.t('dialog.saveDone'));
      }
    };
    this.persistQuiet();
  }

  /** 是否有整树保存请求在飞(串行化用)。 */
  private saveInFlight = false;

  /** 在飞期间是否又有改动需要补发(串行化用)。 */
  private saveQueued = false;

  /** ★ 第四十轮:补发时是否要带 force(用户显式删除触发的保存)。 */
  private saveQueuedForce = false;

  onDelete(n: MaterialNode): void {
    // ★ 2026-09-20(Forrest):删除必须先确认;确认后才真正摘除,
    //   而且只标脏 —— 和新建/拖拽一样,点「保存修改」才写数据库。
    void this.confirmDialog({
      title: this.host.t('dialog.deleteNodeTitle').replace('{name}', n.name),
      body: this.host.t('dialog.deleteNodeBody'),
      confirmText: this.host.t('dialog.deleteConfirm'),
      danger: true
    }).then((ok) => {
      if (!ok) return;
      // 连同子树里的所有素材:挂在它们上面的录音也一并清掉
      const collect = (node: MaterialNode): void => {
        this.host.recorder.removeByMaterial(node.id);
        node.children?.forEach(collect);
      };
      collect(n);
      this.removeNodeById(n.id);
      // 选中项被删 → 清空右侧
      const stillThere = this.host.selectedId()
        ? this.findFile(this.nodes(), this.host.selectedId() ?? '') : null;
      if (this.host.selectedId() && !stillThere) {
        this.host.selectedId.set(null);
        this.host.saved.set('');
        this.host.draft.set('');
        this.host.editing.set(false);
        this.host.grading.activeTakeId.set(null);
      }
      this.treeDeletedSinceSave = true;
      this.treeDirty.set(true);
    });
  }

  /** 按 id 从树上摘除节点(就地修改 + 换引用触发重渲)。 */
  private removeNodeById(id: string): void {
    const pull = (list: MaterialNode[]): boolean => {
      const i = list.findIndex((x) => x.id === id);
      if (i >= 0) { list.splice(i, 1); return true; }
      for (const n of list) {
        if (n.children?.length && pull(n.children)) return true;
      }
      return false;
    };
    if (pull(this.nodes())) this.nodes.set([...this.nodes()]);
  }

  // ---------- 工具 ----------
  /**
   * 把素材树拉平成中序列表(只取文件,不含文件夹)。
   * 题号与上一题/下一题都基于它 —— 树是分组的,但答题是一题一题过的,
   * 所以导航走扁平序列而不是树结构。
   */
  flatten(list: MaterialNode[]): MaterialNode[] {
    const out: MaterialNode[] = [];
    for (const n of list) {
      if (n.folder) {
        if (n.children?.length) out.push(...this.flatten(n.children));
      } else {
        out.push(n);
      }
    }
    return out;
  }

  findFile(list: MaterialNode[], id: string | null): MaterialNode | null {
    if (!id) return null;
    for (const n of list) {
      if (n.id === id) return n;
      if (n.children?.length) {
        const hit = this.findFile(n.children, id);
        if (hit) return hit;
      }
    }
    return null;
  }

  /**
   * 递归查找某节点所属的【父文件夹名称】(第二十二轮新增)。
   * 返回最靠近它的那层文件夹名;顶层文件返回传入的回退值。
   */
  findParentFolderName(
    list: MaterialNode[],
    id: string | null,
    fallback: string | null
  ): string | null {
    if (!id) return fallback;
    for (const n of list) {
      if (n.id === id) return fallback;
      if (n.children?.length) {
        const next = n.folder ? n.name : fallback;
        const hit = this.findParentFolderName(n.children, id, next);
        if (hit !== null) return hit;
      }
    }
    return null;
  }

  private firstFile(list: MaterialNode[]): MaterialNode | null {
    for (const n of list) {
      if (!n.folder) return n;
      if (n.children?.length) {
        const hit = this.firstFile(n.children);
        if (hit) return hit;
      }
    }
    return null;
  }
}
