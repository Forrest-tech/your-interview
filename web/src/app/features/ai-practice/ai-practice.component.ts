import {
  ChangeDetectorRef, Component, HostListener, OnDestroy, OnInit, ViewChild,
  computed, effect, inject, signal
} from '@angular/core';
import { Howl, Howler } from 'howler';
import { firstValueFrom } from 'rxjs';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDividerModule } from '@angular/material/divider';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { MaterialNode, MaterialTreeComponent } from '../../shared/material-tree/material-tree.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { CategoryManagerDialogComponent } from './category-manager-dialog.component';
import { MaterialTransferDialogComponent } from './material-transfer-dialog.component';
import { MaterialNodeDto, MaterialNodeIn, PracticeApi, PracticeCategoryDto } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Recording, RecordingScore, RecorderService } from '../../core/recorder/recorder.service';
import {
  getCachedAudio, getCachedAudioByMaterial, putCachedAudio, ttsKey,
} from './tts-audio-cache';

/**
 * 从 localStorage 恢复字号缩放百分比。
 * 在模块级声明(不是类方法)—— signal() 初始化时需要立刻调用,
 * 此时 this 还不可用。
 * 返回值一定落在 80–150 区间;读失败/值非法都回落 100。
 */
/** "Azure 已配置"标记位的 localStorage 键(与设置页共用)。 */
export const AZURE_READY_KEY = 'azure_speech_configured';

/**
 * 读"Azure 已配置"标记位。
 * ⚠️ 这不是密钥 —— 密钥永远不进浏览器。此标记只表示用户是否去设置页
 *    声明过已配置,用于前端提前引导。默认 false(未配置)。
 */
function readAzureReadyFlag(): boolean {
  try {
    return localStorage.getItem(AZURE_READY_KEY) === '1';
  } catch {
    return false;
  }
}

function readZoomFromStorage(): number {
  // ⚠️ 2026-09-16:MAX 必须与类里的 ZOOM_MAX 同步(150 → 400),
  //    否则旧值 150 会被当成"超上限"在初始化时被夹回去,用户调大后刷新又变小。
  const MIN = 80, MAX = 400, DEFAULT = 100;
  try {
    const raw = localStorage.getItem('user_practice_zoom');
    if (!raw) return DEFAULT;
    const n = Number(raw);
    if (!Number.isFinite(n)) return DEFAULT;
    return Math.min(MAX, Math.max(MIN, Math.round(n)));
  } catch {
    return DEFAULT;
  }
}

/** AI 评分配置 —— 用户可在页面内自己调。 */
interface GradingConfig {
  /** 各维度权重(0-100),用于总分加权 */
  weights: { key: string; label: string; weight: number }[];
  /** 严格度 1-5,影响打分宽严 */
  strictness: number;
  /** 是否检查语法 */
  checkGrammar: boolean;
  /** 是否给改进建议 */
  advice: boolean;
  /** 评分语言 */
  lang: string;
}

/** 标记颜色(2026-09-23 第九轮:Notion 同款颜色状态,后续可作筛选条件)。 */
type MarkColor = 'none' | 'orange' | 'red' | 'green';

/**
 * AI 面试练习。
 *
 * 2026-09-15 第二轮按 Forrest 要求细化:
 *  1. 右侧工作区「编辑 / 只读」双态 —— 默认只读,点编辑才能改,保存后回只读
 *  2. 示范朗读(Web Speech API)+ 倍速切换
 *  3. AI 评分按需触发 —— 只有点「开始评分」才生成,不自动跑
 *  4. AI 评分配置在页面内可调(权重 / 严格度 / 语法检查 / 建议)
 *
 * 当前数据仍在前端内存(阶段一),下一阶段接后端微服务做持久化与真实 AI 评分。
 */
@Component({
  selector: 'app-ai-practice',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatIconModule, MatButtonModule, MatTooltipModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatDividerModule,
    MatSliderModule, MatSlideToggleModule, MatMenuModule,
    MaterialTreeComponent
  ],
  templateUrl: './ai-practice.component.html',
  styleUrl: './ai-practice.component.scss'
})
export class AiPracticeComponent implements OnInit, OnDestroy {
  readonly i18n = inject(I18nService);
  readonly recorder = inject(RecorderService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  // ★ 2026-09-20(Forrest):Howler 的事件回调在 Angular 之外触发,
  //   必须手动 detectChanges 才能让模板看到最新的位置/状态。
  private readonly cdr = inject(ChangeDetectorRef);

  /** 录音失败提示(麦克风权限等)。 */
  readonly recError = signal<string | null>(null);

  /** 当前素材的录音列表。 */
  readonly myRecordings = computed(() =>
    this.recorder.forMaterial(this.selectedId())
  );

  /**
   * ★ 第四十九轮(Forrest 报"新录音显示 Take #1"):Take 序号按时间先后。
   * 列表显示顺序是"最新在上"(后端 CreatedAt DESC),旧实现直接用显示序号
   * ($index+1)当 Take 号 → 每录一条新录音,旧的号全被顶下去。
   * 现在:按 createdAt 升序编号,最早 = #1,新录音永远是最大号;
   * 显示顺序保持不变,只有号码与时间轴对齐。
   */
  readonly takeNumbers = computed(() => {
    const sorted = [...this.myRecordings()].sort((a, b) => a.createdAt - b.createdAt);
    const map = new Map<string, number>();
    sorted.forEach((r, i) => map.set(r.id, i + 1));
    return map;
  });

  /** 某条录音的 Take 序号(时间先后,最早=1)。 */
  takeNo(id: string): number {
    return this.takeNumbers().get(id) ?? 1;
  }

  // ---------- 素材树 ----------
  readonly nodes = signal<MaterialNode[]>([]);
  readonly selectedId = signal<string | null>(null);

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
  readonly categoryFilter = signal<string>(AiPracticeComponent.readStoredCategoryFilter());

  /** 编辑态正文(仅编辑模式下可改)。 */
  readonly draft = signal('');

  /** 是否处于编辑态。默认只读。 */
  readonly editing = signal(false);

  /** 已保存的正文快照(退出编辑/保存时写入)。 */
  private readonly saved = signal('');

  readonly selectedNode = computed(() =>
    this.findFile(this.nodes(), this.selectedId())
  );

  /**
   * 当前条目所属【父文件夹名称】(第二十二轮:顶部路径用文件夹名替代题号)。
   * MaterialNode 没有 parentId 字段 → 在树上递归回溯查找。
   * 例如:素材树里 自我介绍 / 学历  → 顶部显示「自我介绍」,大标题显示「学历」。
   * 找不到父文件夹(顶层文件)时回退为 null,模板层再用默认文案。
   */
  /**
   * 导航文字:【父文件夹 - 素材名】(★ 第三十五轮 Forrest 要求)。
   *
   * 原来是分两行、两种样式:
   *   ch-kicker = 小号大写灰字(文件夹名)
   *   ch-title  = 22px 黑体加粗(素材名)
   * Forrest 要求改成一行 "自我介绍 - 工签状态",
   * 且**字体和颜色一致** —— 不再一个大一个小、一灰一黑。
   *
   * 找不到父文件夹时只显示素材名(不留 "undefined -" 这种脏字符)。
   */
  readonly parentFolderName = computed(() =>
    this.findParentFolderName(this.nodes(), this.selectedId(), null)
  );

  /** 只读态显示的正文。 */
  readonly readonlyText = computed(() => this.saved());

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
  readonly treeWidth = signal<number>(AiPracticeComponent.readTreeWidth());

  private static readonly TREE_W_KEY = 'user_practice_tree_width';
  private static readonly TREE_W_MIN = 220;
  private static readonly TREE_W_MAX = 560;

  private static readTreeWidth(): number {
    try {
      const raw = localStorage.getItem(AiPracticeComponent.TREE_W_KEY);
      const n = raw ? parseInt(raw, 10) : NaN;
      if (Number.isFinite(n)) {
        return Math.min(AiPracticeComponent.TREE_W_MAX,
                        Math.max(AiPracticeComponent.TREE_W_MIN, n));
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
        Math.min(AiPracticeComponent.TREE_W_MAX,
                 Math.max(AiPracticeComponent.TREE_W_MIN, next)));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      try {
        localStorage.setItem(AiPracticeComponent.TREE_W_KEY, String(this.treeWidth()));
      } catch { /* 存不了就算了,不影响使用 */ }
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    // 拖动期间锁定光标与禁选,避免拖动时选中文字
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }

  // ============================================================
  // 朗读文本字号缩放(任务书第二节)
  // 范围 80%–150%,步进 10%,基准 18px。
  // 关键要求:**全局持久化** —— 切题/刷新/换分类都要保持。
  //   → 写入 localStorage('user_practice_zoom'),初始化优先读它。
  // ============================================================

  /** localStorage 键名(任务书指定)。 */
  private static readonly ZOOM_KEY = 'user_practice_zoom';

  /** 基准字号 18px —— 100% 时的正文大小(任务书指定)。 */
  private static readonly BASE_FONT_PX = 18;

  /**
   * 缩放百分比下限 / 上限 / 步进。
   *
   * ⚠️ 2026-09-16(Forrest 第 3 条):上限从 150 提到 **400**。
   *    原因:150% 太小,用户想放大看清长句时直接被顶住。
   *    上限 400% 配合基准 18px → 最大 72px,足够把一句话放大到可逐词精读。
   *    步进仍 10%(400/10 = 40 档,手感够细)。
   */
  private static readonly ZOOM_MIN = 80;
  private static readonly ZOOM_MAX = 400;
  /**
   * ⚠️ 2026-09-16(Forrest 最新一条):步进改回 **10**。
   *    点 − / + 每次变动 10(80 → 90 → …),手感细。
   *    (此前曾改 50,Forrest 本轮明确要求回到 10。)
   */
  private static readonly ZOOM_STEP = 10;

  /**
   * 下拉可选档位(Forrest 最新一条:等差数列,首项 100,公差 40)。
   *   100, 140, 180, 220, 260, 300, 340, 380
   * 为什么不用"80–400 每 10 一档"的完整序列:
   *   那样下拉会很长;Forrest 指定的是"从 100 开始、公差 40"这一串。
   * ⚠️ 注意:等差数列不覆盖 80(下限)。手动输入/步进仍可到 80,
   *    下拉只是"快捷档位",不是唯一取值集合。
   */
  private static readonly ZOOM_PRESETS: readonly number[] = (() => {
    const out: number[] = [];
    for (let v = 100; v <= AiPracticeComponent.ZOOM_MAX; v += 40) out.push(v);
    return out;
  })();

  /** 下拉档位列表:等差数列 + (当前值若不在序列里,补进去,免得下拉里看不到自己)。 */
  readonly zoomPresets = computed<number[]>(() => {
    const cur = this.fontZoomLevel();
    const list = AiPracticeComponent.ZOOM_PRESETS;
    return list.includes(cur) ? [...list] : [...list, cur].sort((a, b) => a - b);
  });

  /**
   * 当前缩放百分比(100 = 基准)。
   * 初值从 localStorage 恢复 —— 这样刷新/切题都不会回到默认。
   */
  readonly fontZoomLevel = signal(readZoomFromStorage());

  /** 实际阅读字号(px)= 基准 × 缩放比例。模板绑到 CSS 变量 --read-fs。 */
  readonly readFontPx = computed(() =>
    Math.round((AiPracticeComponent.BASE_FONT_PX * this.fontZoomLevel()) / 100)
  );

  /** 是否还能继续缩小 / 放大(用于按钮 disabled)。 */
  canDecreaseFont = computed(() => this.fontZoomLevel() > AiPracticeComponent.ZOOM_MIN);
  canIncreaseFont = computed(() => this.fontZoomLevel() < AiPracticeComponent.ZOOM_MAX);

  /** 减字号:不低于下限。 */
  decreaseFont(): void {
    if (!this.canDecreaseFont()) return;
    this.applyZoom(this.fontZoomLevel() - AiPracticeComponent.ZOOM_STEP);
  }

  /** 增字号:不超过上限。 */
  increaseFont(): void {
    if (!this.canIncreaseFont()) return;
    this.applyZoom(this.fontZoomLevel() + AiPracticeComponent.ZOOM_STEP);
  }

  // ---------- 点中间数字直接输入精确百分比(Forrest 本轮第 2 条) ----------

  /** 是否处于"手动输入百分比"状态。 */
  readonly zoomEditing = signal(false);

  /** 输入框里正在编辑的原始文本(不立即生效,回车/失焦才提交)。 */
  readonly zoomDraft = signal('');

  /** 点百分比数字 → 进入编辑态,预填当前值。 */
  startZoomEdit(): void {
    this.zoomDraft.set(String(this.fontZoomLevel()));
    this.zoomEditing.set(true);
  }

  /**
   * 选中下拉里的某个档位(Forrest 最新一条)。
   * 与手动输入走同一个 applyZoom,越界同样自动夹取。
   */
  setZoomPreset(pct: number): void {
    this.applyZoom(pct);
  }

  /** 输入框内容变化(只存草稿,不落盘)。 */
  onZoomDraftChange(v: string): void {
    // 只允许数字(去掉 % 与空格等干扰字符)
    this.zoomDraft.set(String(v ?? '').replace(/[^0-9]/g, ''));
  }

  /**
   * 提交输入框里的百分比。
   * - 非数字 / 空 → 放弃本次编辑,保持原值。
   * - 越界值由 applyZoom 统一夹到 [ZOOM_MIN, ZOOM_MAX]。
   */
  commitZoomEdit(): void {
    if (!this.zoomEditing()) return;
    const raw = this.zoomDraft().trim();
    this.zoomEditing.set(false);
    if (!raw) return;                       // 清空 = 不改
    const n = Number(raw);
    if (!Number.isFinite(n)) return;        // 非数字 = 不改
    this.applyZoom(n);                      // 越界自动夹取
  }

  /** 回车提交。 */
  onZoomEnter(): void {
    this.commitZoomEdit();
  }

  /** Esc 取消编辑,不改变字号。 */
  cancelZoomEdit(): void {
    this.zoomEditing.set(false);
    this.zoomDraft.set('');
  }

  /** 写入 signal + 落盘 localStorage。越界值一律夹到合法区间。 */
  private applyZoom(pct: number): void {
    const v = Math.min(
      AiPracticeComponent.ZOOM_MAX,
      Math.max(AiPracticeComponent.ZOOM_MIN, Math.round(pct))
    );
    this.fontZoomLevel.set(v);
    try {
      localStorage.setItem(AiPracticeComponent.ZOOM_KEY, String(v));
    } catch {
      // 隐私模式 / 存储配额满:读失败不影响本次会话使用,只是不持久化
    }
  }

  /** 前往 AI / Azure 设置页(任务书第三节指定的跳转链路)。 */
  goAiSetting(): void {
    this.router.navigateByUrl('/account/ai-setting');
  }

  // ============================================================
  // Azure Speech 凭证校验 —— AI 评分前置门禁(任务书第六节)
  // ============================================================
  //
  // ⚠️ 诚实边界:前端**拿不到也存不到** Azure Key。
  //    Key 只存在于服务端 appsettings.Development.json(gitignored),
  //    浏览器永远不知道真实密钥。所以这里的 azureReady() 只能反映
  //    "用户是否已声明配置过" —— 靠一个本地标记位,不是密钥本身。
  //
  //    真正的密钥校验发生在**服务端**(PronunciationAssessor 调 Azure 失败
  //    会返回 ErrorType/错误信息)。所以本门禁的作用是"早发现早引导",
  //    而不是伪造一个前端安全边界。
  // ============================================================

  /**
   * Azure 是否就绪。
   *
   * ★ 2026-09-20(Forrest BUG1 真因修复):以**服务端**为准。
   *
   * 旧实现只读 localStorage['azure_speech_configured'] —— 该标记只有用户
   * 手动去 AI 设置页保存过才会写入。本机从未写过,于是 azureReady 恒为 false,
   * speak() 第一行就被门禁挡住 → 点播放毫无反应、时间永远 --:-- / --:--
   * (这正是 Forrest 报的"默认时间是空的")。
   *
   * 现在:localStorage 标记为 true 时立即就绪(快速路径);
   *       否则向 GET /speech/settings 查 hasKey,以服务端为准。
   */
  readonly azureReady = signal(readAzureReadyFlag());

  /**
   * ★ 2026-09-20(Forrest BUG1 修复):向服务端核对 Azure 是否真的配好。
   *   服务端持 key(不进浏览器),GET speech/settings 只回 hasKey 布尔值。
   *   本地标记已是 true 就不必再问;否则以服务端回答为准。
   */
  private syncAzureReadyFromServer(): void {
    if (this.azureReady()) return;   // 本地已声明配置过 → 不必等网络
    this.practiceApi.getSpeechSettings().subscribe({
      next: (s) => {
        if (s?.hasKey) {
          this.azureReady.set(true);
          try { localStorage.setItem(AZURE_READY_KEY, '1'); } catch { /* 隐私模式忽略 */ }
          this.cdr.detectChanges();
          // ★ 2026-09-20(Forrest):若首次进入时该状态尚未就绪、导致
          //   本地音频时长没装载,这里补跑一次,保证与响应时序无关。
          this.retryCachedDuration();
        }
      },
      // 服务端不可用时不误判为"未配置",保持原值,让用户点播放时拿到真实错误
      error: () => { /* noop */ }
    });
  }

  /**
   * 待提交的录音是否可以提交。
   * 2026-09-16(Forrest 本轮):提交按钮的行为定义 ——
   *   · 没有录音        → 置灰
   *   · 录好了(有待提交) → 激活
   * 这是界面上的硬门禁,不让用户点到注定失败的按钮。
   */
  readonly canSubmitTake = computed(() => {
    const pt = this.recorder.pendingTake();
    if (!pt) return false;
    // 素材不是 GUID(本地种子)→ 后端存不了,先如实置灰(保存素材树后再来)
    return RecorderService.isGuid(pt.materialId);
  });

  /**
   * 待提交录音是否正在上传。
   * 为什么需要这个:提交 → 列表落一条本地记录 → 上传异步完成。
   * 上传未回来之前,那条录音还不能评分(否则 404)。
   * 界面用这个标志告诉用户"还在上传",而不是让人对着一颗灰按钮发愣。
   */
  readonly uploadingPending = computed(() => {
    const list = this.recorder.recordings();
    return list.some((r) => !r.uploaded);
  });

  // ⚠️ 2026-09-16(Forrest 第 4 条):底部"提交 AI 评分"按钮已删除 ——
  //    它与录音区右侧的 run ai scoring 胶囊是同一动作,重复只会让人困惑。
  //    onSubmitScoringClick() 随之移除(仅有的调用点就是那个被删的按钮)。
  // ★ 第五十一轮(Forrest):胶囊上的 Run AI Scoring 也一并移除,
  //    全站唯一评分入口 = 列表每行的 ✦ 按钮 → gradeRecording(),别无分支。

  /** 轻量 toast(不引入 MatSnackBar,避免多一个依赖)。 */
  readonly toastMsg = signal('');
  private toast(text: string): void {
    this.toastMsg.set(text);
    setTimeout(() => this.toastMsg.set(''), 3200);
  }

  toggleTree(): void {
    this.treeOpen.set(!this.treeOpen());
  }

  // ---------- 题目导航(扁平化后可上一题/下一题) ----------
  /** 所有可作答的素材(扁平列表,按树的中序),用于题号与上下题导航。 */
  readonly flatItems = computed(() => this.flatten(this.nodes()));

  /** 当前题号(从 1 开始)。 */
  readonly qIndex = computed(() => {
    const id = this.selectedId();
    if (!id) return 0;
    return this.flatItems().findIndex((n) => n.id === id) + 1;
  });

  readonly hasPrev = computed(() => this.qIndex() > 1);
  readonly hasNext = computed(() => {
    const i = this.qIndex();
    return i > 0 && i < this.flatItems().length;
  });

  prevItem(): void {
    this.goRelative(-1);
  }

  nextItem(): void {
    this.goRelative(1);
  }

  private goRelative(step: number): void {
    if (this.editing()) return; // 编辑中先保存或取消
    const i = this.qIndex();
    if (i <= 0) return;
    const target = this.flatItems()[i - 1 + step];
    if (target) this.selectFile(target);
  }

  /** 当前素材下的录音序号(第几次录音)。 */
  readonly recOrder = computed(() => this.myRecordings().length);

  /**
   * 当前练习/朗读语言(BCP-47,如 en-US / fr-FR)。
   *
   * ★ 2026-09-19(Forrest 报"Azure key 不支持法语"):
   *   真因是本页把语言写死为英语 —— 朗读用英语音色念法文、
   *   评分也用 en-US 去识别法语。此 signal 是**唯一语言出口**:
   *   朗读(TTS)与评分(assess)都从这里取值,保证两者永远同源。
   *   持久化到 localStorage,切一次以后都记得。
   */
  readonly practiceLang = signal<'en-US' | 'fr-FR' | 'zh-CN'>(
    (() => {
      try {
        const v = localStorage.getItem('practice.lang');
        return v === 'fr-FR' || v === 'zh-CN' || v === 'en-US' ? v : 'en-US';
      } catch { return 'en-US' as const; }
    })()
  );

  /** 切换练习语言 —— 写 localStorage,下次打开仍是这个语言。 */
  setPracticeLang(lang: 'en-US' | 'fr-FR' | 'zh-CN'): void {
    this.practiceLang.set(lang);
    try { localStorage.setItem('practice.lang', lang); } catch { /* 隐私模式写不了,不影响本次会话 */ }
  }

  /** 语言选项表 —— 加语言只改这一处(弹单里遍历它渲染)。 */
  readonly langOptions = [
    { code: 'en-US' as const, label: 'English (US)', note: 'en-US · Aria' },
    { code: 'fr-FR' as const, label: 'Français', note: 'fr-FR · Denise' },
    { code: 'zh-CN' as const, label: '中文', note: 'zh-CN' },
  ];

  // ---------- 示范朗读 ----------
  readonly speaking = signal(false);

  // ══ ★ 2026-09-20(Forrest):**全用 Howler** ══
  //   音源由 Howler 在创建时给定;换素材 = unload() 旧的、按新音频新建。
  //   时长 duration()、位置 seek()、播放/暂停 play()/pause()、
  //   结束 on('end')、倍速 rate() —— 全部是 Howler 自己的 API 与事件。
  private ttsHowl: Howl | null = null;
  /** 当前播放位置(秒) —— 值来自 howl.seek()。 */
  readonly speakPos = signal(0);
  /** 音频真实总时长(秒) —— 来自 howl.duration(),倍速不影响它。 */
  readonly speakDur = signal(0);
  /**
   * ★ 2026-09-20(Forrest):进度刷新 —— 每 200ms 读一次 howl.seek()。
   * 值完全来自 Howler(不是我估算);Howler 的 seek 事件只在程序主动
   * 调 seek() 时触发,播放中不触发,所以这里按方案二用定时器搬运真实值。
   */
  private posTimer: ReturnType<typeof setInterval> | null = null;
  /** 当前播放器装载的音频指纹键(文本+音色+语言)。 */
  private ttsPlayingKey: string | null = null;
  /**
   * 这一段音频是否已经自然播完(用于"再点一次从头播")。
   * Howler 播完后 seek() 未必自动归零,这里显式记一笔,避免重播时
   * 从结尾处开始、看起来像"点了没反应"。
   */
  private ttsEnded = false;
  readonly rate = signal(1);
  readonly rates = [0.5, 0.75, 1, 1.25, 1.5, 2];
  // ★ 2026-09-20(Forrest):utterance / ttsAudio 已删除 ——
  //   两者都是 Web Speech / HTMLAudioElement 时代的产物,
  //   现在统一由 this.ttsHowl(Howl 实例)承载。

  /** Azure TTS 的 objectURL —— 必须回收,否则每次合成都会攒住一份 MP3。 */
  private ttsUrl: string | null = null;

  /**
   * 本次示范朗读实际用的引擎说明(如 "Azure 神经语音(服务端合成)"、
   * "Azure 语音未配置,已回退浏览器语音"）。
   * ⚠️ 这是诚实标记:回退时必须显示,不能让用户误以为听到了 Azure 人声。
   */
  readonly ttsNote = signal('');

  /**
   * ★ 第三十三轮(Forrest):示范朗读的**额度消耗**提示。
   *
   * 三种取值,必须在界面上说清楚(不猜、不含糊):
   *   · 'fresh'  = 本次真调了 Azure 合成 → **消耗了额度**
   *   · 'cache'  = 服务端本地缓存命中 → **未消耗额度**
   *   · ''       = 无提示(未播 / 回退浏览器语音 / 非 Azure 引擎)
   *
   * 数据来源:后端 `X-Tts-Cache: hit|miss` 响应头。
   * ⚠️ 前端**无法自行判断**服务端有没有缓存 ——
   *    所以绝不能根据"这段文本我播过没"在本地猜,
   *    猜错就是骗用户(比如另一个标签页/另一台设备已经合成过)。
   */
  readonly ttsCost = signal<'' | 'fresh' | 'cache' | 'local'>('');

  /**
   * ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):本次示范朗读的精确计费量。
   *
   * ⚠️ 名字里不用 token —— Azure 语音 TTS **根本不以 token 计费**,
   *   真实计费单位是**合成字符数**(神经语音按每 1M 字符计价)。
   *   这里存的 billedChars 就是"本次送给 Azure 的字符数",
   *   由服务端精确算出(UTF-16 代码单元数,与 Azure 口径一致)。
   *   命中缓存时为 0。
   *   null = 服务端未下发该头(旧后端)→ 界面不显示数字,不编造。
   */
  readonly ttsBilledChars = signal<number | null>(null);

  /** 这段文本的完整字符数(命中缓存时用来对比"省了多少")。 */
  readonly ttsFullChars = signal<number | null>(null);

  /** 本次实际使用的音色(服务端默认也如实回报)。 */
  readonly ttsVoice = signal('');

  /** 本次返回的音频字节数(核对用,非计费单位)。 */
  readonly ttsAudioBytes = signal<number | null>(null);

  /**
   * ★ 第三十三轮(Forrest):AI 评分的**额度消耗**标记。
   *
   * 与示范朗读同理,必须告诉用户这次到底花没花 Azure 额度:
   *   · 'fresh' = 本次真调了 Azure 发音评估 → 消耗了额度
   *   · 'cache' = 该录音已有入库评分,直接读库 → 未消耗额度
   *   · ''      = 无提示
   *
   * 为什么评分这边"未消耗"也是真实可考据的:
   *   grade() 开头有一道 `if (rec.score) return;` ——
   *   列表接口已把历史评分带回,有分就不再发请求。
   *   所以看到分数且本次未发评估请求 = 确实是读库的。
   */
  readonly scoreCost = signal<'' | 'fresh' | 'cache'>('');

  /**
   * ★ 第三十四轮(Forrest):本次 AI 评分的精确计费量。
   *
   * ⚠️ 评分这边同样不提 token —— Azure 发音评估按**音频时长**计费。
   *   billedSeconds 由服务端从 WAV 头精确算出(字节率 × data 长度),
   *   不是估算也不是从 Azure 请求返回物里猜的。
   *   null = 服务端/旧后端未提供 → 不显示数字。
   */
  readonly scoreBilledSeconds = signal<number | null>(null);

  /** 评分时送评的音频字节数(核对用)。 */
  readonly scoreBilledBytes = signal<number | null>(null);

  /**
   * ★ 第三十四轮:把评分音频秒数格式化成界面文案用的字符串。
   *
   * 拿不到就返回 '—'(与四项分数的 "拿不到显示 —" 口径一致),
   * **绝不编一个数**。保留 1 位小数(音频长度到 0.1 秒已足够精确)。
   */
  readonly scoreSecondsText = computed(() => {
    const s = this.scoreBilledSeconds();
    if (s === null || !Number.isFinite(s)) return '—';
    return s.toFixed(1);
  });

  /**
   * 示范朗读条上要不要显示引擎提示。
   *
   * ⚠️ 2026-09-16(Forrest 本轮明确要求):
   *   · **不显示** "Azure 神经语音(服务端合成)"这类"正常走 Azure"的标记 ——
   *     用户只想看进度,不关心底层引擎。
   *   · **必须显示**"回退/失败/未配置"类提示 —— 否则用户会把浏览器语音
   *     当成 Azure 人声,这是诚实红线,与"想不想看"无关。
   * 实现:只放行包含 回退/未配置/失败/无法/拦截 等警示词的 note。
   */
  readonly showTtsNote = computed(() => {
    const n = this.ttsNote();
    if (!n) return false;
    // ⚠️ 2026-09-20 修复(Forrest 报"点播放毫无反应"):
    //   旧实现把所有含"合成"二字的提示一律藏起来(初衷是隐藏"合成中"的进度),
    //   结果把 **"语音合成失败"** 也吞了 —— 用户点了播放,界面一点反馈都没有。
    //   现在:纯进度提示不再写入 ttsNote(本组件已无"正在合成"分支),
    //   因此这里只判断"有没有提示" —— 失败/未配置类一律如实显示。
    return true;
  });

  /**
   * ★ 2026-09-20(Forrest 第三十六轮):**取消引擎切换**。
   *
   * 旧实现给用户二选一(浏览器语音 / Azure 神经语音),并在 Azure 不可用时
   * 回退到 window.speechSynthesis。现在按 Forrest 要求**彻底删除该分支**:
   *   · 界面不再出现任何引擎开关;
   *   · 代码里不再有任何 Web Speech 兜底路径;
   *   · 示范朗读**只有一条路** —— 服务端 Azure Speech 合成出的真实音频
   *     (MP3),交给 Howler 播放。
   *
   * 没配 Azure Key 时不伪装能播:播放按钮置灰、时间显示 --:-- / --:--,
   * 点击后把用户引到 Azure 配置页(见 openTtsSettings / speak 的门禁)。
   */

  /** 顶栏齿轮对应的设置浮层触发器 —— 用于"点置灰播放键时自动展开配置"。 */
  @ViewChild('engineTrigger') private engineTrigger?: MatMenuTrigger;

  /** 程序化展开顶栏的朗读设置浮层。 */
  openTtsSettings(): void {
    this.engineTrigger?.openMenu();
  }

  // ---------- 素材树持久化 ----------
  /**
   * 素材树是否有未保存修改。
   * 任务书第二节要求提供「保存修改」入口。这里用脏标记驱动按钮状态：
   * 改了没存 → 按钮高亮可点；已保存 → 置灰显示"已保存"。
   */
  readonly treeDirty = signal(false);

  /** 后端访问层（素材树 / 录音 / 语音设置 / TTS）。 */
  private readonly practiceApi = inject(PracticeApi);

  /** 统一确认弹窗(保存/删除都要先过这一关,Forrest 2026-09-20)。 */
  private readonly dialog = inject(MatDialog);

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
      this.treeError.set(this.t('practice.treeBlocked'));
      return;
    }
    // ★ 2026-09-20(Forrest):保存是"写入数据库"的决定性动作,先弹确认。
    void this.confirmDialog({
      title: this.t('dialog.saveTitle'),
      confirmText: this.t('dialog.saveConfirm'),
    }).then((ok) => {
      if (!ok) return;
      this.doSaveTree();
    });
  }

  /** 确认后的真正保存(整树覆盖 + 回读对齐 GUID)。after 可选:保存成功后执行。 */
  private doSaveTree(after?: () => void): void {
    const payload = AiPracticeComponent.toPayload(this.nodes());
    // ★ 本次编辑期间删过东西 → 带 force 越过服务端"防误删熔断"
    //   (用户已经在弹窗里确认过删除,不应被拦)。
    this.practiceApi.saveMaterials(payload, this.treeDeletedSinceSave).subscribe({
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
        this.toast(this.t('dialog.saveDone'));
        after?.();
      },
      error: (e) => {
        // 绝不假装保存成功 —— 把后端给的真实原因显示出来
        this.treeError.set(this.errText(e));
        this.treeDirty.set(true);
      }
    });
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
      this.toast(this.t('dialog.discardToast'));
    }
    this.treeSnapshot = null;
    this.treeDeletedSinceSave = false;
    this.treeEditing.set(false);
    // 恢复快照后,原选中的素材可能已经不在了 → 重新对齐右侧内容
    const id = this.selectedId();
    const target = id ? this.findFile(this.nodes(), id) : null;
    if (target && !target.folder) {
      this.selectFile(target);
    } else {
      const first = this.firstFile(this.nodes());
      if (first) this.selectFile(first);
      else {
        this.selectedId.set(null);
        this.saved.set('');
        this.draft.set('');
        this.editing.set(false);
      }
    }
  }

  /**
   * 统一确认弹窗。
   * 返回值:true = 确认按钮;'discard' = 第三个按钮(放弃);其它 = 取消。
   * 全站弹窗风格一致 —— 保存/删除/刷新拦截都走这一个组件。
   */
  private confirmDialog(
    opts: Omit<ConfirmDialogData, 'cancelText'> & { cancelText?: string },
    width = '360px'
  ): Promise<boolean | 'discard'> {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width,
      panelClass: 'app-confirm',
      autoFocus: false,
      data: {
        ...opts,
        cancelText: opts.cancelText ?? this.t('dialog.cancel')
      } as ConfirmDialogData
    });
    return firstValueFrom(ref.afterClosed()).then((v) =>
      v === true ? true : v === 'discard' ? 'discard' : false
    );
  }

  /**
   * ★ 2026-09-23(Forrest):刷新前的未保存拦截 —— 走**站内弹窗**。
   *
   * 为什么不用浏览器原生 beforeunload 弹窗:那段文案由浏览器自己的语言决定,
   * 站点切到英文/法文时它仍旧是中文 → 语言混乱(Forrest 报的问题)。
   * 浏览器只允许"重新加载/取消"两个原生按钮,无法改文案、无法加第三个选项,
   * 所以在 F5 / Ctrl+R / Cmd+R 这一层拦下来,给用户一个跟语言设置一致的弹窗:
   *   保存并刷新 / 放弃并刷新 / 留在本页。
   * 关闭标签页等无法拦截的场景仍由 beforeunload 兜底(浏览器文案,无法干预)。
   */
  @HostListener('window:keydown', ['$event'])
  guardReload(ev: KeyboardEvent): void {
    if (!(this.treeEditing() && this.treeDirty())) return;
    const k = ev.key;
    const mod = ev.ctrlKey || ev.metaKey;
    const isReload =
      k === 'F5' || (mod && !ev.shiftKey && (k === 'r' || k === 'R')) ||
      (mod && ev.shiftKey && (k === 'r' || k === 'R'));
    if (!isReload) return;

    ev.preventDefault();
    ev.stopPropagation();
    void this.confirmDialog({
      title: this.t('dialog.unsavedTitle'),
      body: this.t('dialog.unsavedBody'),
      confirmText: this.t('dialog.saveAndReload'),
      cancelText: this.t('dialog.stay'),
      discardText: this.t('dialog.discardAndReload'),
      discardDanger: true
    }, '400px').then((res) => {
      if (res === true) {
        // 保存完再刷新 —— 否则写库请求会被刷新打断
        this.doSaveTree(() => window.location.reload());
      } else if (res === 'discard') {
        this.cancelTreeEdit();
        window.location.reload();
      }
      // 'stay' → 什么都不做
    });
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
    this.practiceApi.getMaterials().subscribe({
      next: (dtos) => {
        if (!dtos.length) return;   // 异常情况:服务端为空,保持本地不动
        const prevSelected = this.selectedId();
        const fresh = AiPracticeComponent.fromDto(dtos);
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
            this.selectedId.set(remapped.id);
            this.rememberMaterialId(remapped.id);
          }
        }
        // ★ 第二十七轮:树里现在有真 GUID 了 → 把积压的录音补传上去。
        //   映射规则:旧 id 找不到时按"记忆中同 id 的旧节点名字"回找。
        this.recorder.flushPendingUploads((oldId) => {
          const hit = this.findById(this.nodes(), oldId);
          return hit ? hit.id : null;
        });
        // ★ 第三十七轮:回读后**重同步编辑缓冲**,否则用户的输入会被换成旧快照。
        //   场景:用户正在编辑某素材,此时另一个自动保存的回包把 nodes
        //   整体换新 —— selectedId 已按名对齐到新 GUID,但 saved/draft 还是旧值。
        //   若不重同步,用户看到的正文会"退回"到保存前的样子(以为没存上)。
        //   规则:未在编辑态时,把缓冲刷新为该节点在**新树里的真实内容**。
        if (!this.editing()) {
          const cur = this.findFile(this.nodes(), this.selectedId());
          const text = cur?.content ?? '';
          this.saved.set(text);
          this.draft.set(text);
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
  private readonly lastKnownName = new Map<string, string>();

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
  private restoreTree(): void {
    this.treeLoading.set(true);
    this.loadCategories();   // ★ 2026-09-25:类别与树并行加载;类别失败不阻塞树
    this.practiceApi.getMaterials().subscribe({
      next: (dtos) => {
        this.nodes.set(AiPracticeComponent.fromDto(dtos ?? []));
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
      const v = localStorage.getItem(AiPracticeComponent.CATEGORY_FILTER_KEY);
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
    this.practiceApi.getCategories().subscribe({
      next: (list) => {
        this.categories.set(list ?? []);
        this.validateCategoryFilter();
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
    if (f === 'all' || f === 'uncategorized') return;
    if (this.categories().some((c) => c.id === f)) return;
    this.categoryFilter.set('all');
    try { localStorage.removeItem(AiPracticeComponent.CATEGORY_FILTER_KEY); } catch { /* 忽略 */ }
  }

  /** 下拉切换过滤值(树控件通过事件抛上来,这里回写给 signal 并落盘)。 */
  onCategoryFilterChange(value: string): void {
    this.categoryFilter.set(value);
    try {
      localStorage.setItem(AiPracticeComponent.CATEGORY_FILTER_KEY, value);
    } catch { /* 隐私模式写不了,不影响本次会话 */ }
  }

  /**
   * 打开类别管理弹窗(新增/重命名/删除/拖拽排序都在弹窗里)。
   * 关闭后:用回传列表刷新下拉;若树没有未保存改动,顺带回读一次 ——
   * 弹窗里删除的类别,素材的 categoryId 已被服务端 SetNull,需要对齐。
   */
  openCategoryManager(): void {
    const ref = this.dialog.open(CategoryManagerDialogComponent, {
      width: '480px',
      data: { categories: this.categories() }
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      this.categories.set(result.categories ?? []);
      // 弹窗里可能删了类别 → 当前过滤若指向被删类别,回落"全部"
      this.validateCategoryFilter();
      // ★ 2026-09-25(Forrest 第二轮):弹窗里点了「导入模板」→
      //   回传了素材骨架根节点,这里合并进整树。
      const imported = result.importedNodes ?? [];
      if (imported.length > 0) {
        this.mergeImportedNodes(imported);
        return;
      }
      if (!this.treeDirty()) this.restoreTree();
    });
  }

  /**
   * ★ 2026-09-25 第四轮(Forrest):素材「导入 / 导出」入口。
   * 导出在弹窗里直接生成文件下载;导入只回传待合并的根节点,由这里统一入库。
   */
  openTransfer(): void {
    if (this.treeLoadFailed()) {
      this.toast(this.t('practice.treeBlocked'));
      return;
    }
    const ref = this.dialog.open(MaterialTransferDialogComponent, {
      width: '560px',
      maxWidth: '94vw',
      panelClass: 'app-transfer',
      data: { nodes: this.nodes(), categories: this.categories(), filter: this.categoryFilter() }
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      this.mergeImportedNodes(result.importedNodes ?? []);
      // 弹窗里可能新建了类别 → 下拉要能立刻看到它
      this.loadCategories();
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
      this.toast(this.t('practice.treeBlocked'));
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
    this.recorder.syncAllRecordings(this.allMaterialIds());
    const fromUrl = this.route.snapshot.queryParamMap.get('materialId');
    const wanted = fromUrl || this.readStoredMaterialId();
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
      this.selectFile(first);
    }
  }

  /**
   * ★ 2026-09-20(Forrest 第一条):把树里通向 target 的每一层文件夹展开。
   * 只改内存态(不标脏、不落盘),展开状态本来就会随整树保存写库;
   * 这里解决的是"回读时库里 expanded=false → 刷新后父级全折叠"的情况。
   */
  private expandPathTo(target: MaterialNode): void {
    const path: MaterialNode[] = [];
    const walk = (list: MaterialNode[], stack: MaterialNode[]): boolean => {
      for (const n of list ?? []) {
        if (n.id === target.id) { path.push(...stack); return true; }
        if (n.folder && n.children?.length && walk(n.children, [...stack, n])) return true;
      }
      return false;
    };
    if (!walk(this.nodes(), [])) return;      // 没找到就什么都不动
    let changed = false;
    for (const folder of path) {
      if (folder.folder && !folder.expanded) { folder.expanded = true; changed = true; }
    }
    // 换一个数组引用,保证树组件一定重渲(选中高亮 + 展开箭头同步)
    if (changed) this.nodes.set([...this.nodes()]);
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
      children: AiPracticeComponent.toPayload(n.children ?? [])
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
        children: AiPracticeComponent.fromDto(d.children ?? [])
      }));
  }

  private errText(e: unknown): string {
    return String((e as { message?: string } | null)?.message ?? e ?? this.t('practice.errUnknown')).slice(0, 200);
  }

  // ---------- 发音音标 ----------
  /**
   * 取单词音标。
   * ⚠️ 诚实约束：Azure PA 的返回里**没有音标**（只有 AccuracyScore/ErrorType）。
   * 本地没有音标词典，所以无法给出真实音标 —— 返回 null，
   * 由界面显示"暂无音标"。**绝不编造 /ˈwɜːd/ 这种看起来像真的假音标。**
   * 后续若接发音词典 API（如 Free Dictionary API）再填这里。
   */
  phonetic(_word: string): string | null {
    return null;
  }

  /** 当前在逐词区点开的单词下标（null = 未点开）。 */
  readonly pickedWord = signal<number | null>(null);

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
    { key: 'none', label: 'practice.markNone', dot: '#c8cdd6' },
    { key: 'orange', label: 'practice.markOrange', dot: '#d9730d' },
    { key: 'red', label: 'practice.markRed', dot: '#d44c47' },
    { key: 'green', label: 'practice.markGreen', dot: '#448361' },
  ];

  /**
   * 标记变更计数器 —— Angular 的 computed 只追踪 signal 本身,
   * 直接改节点对象上的 markColor 字段不会触发重算,必须"敲一下钟"。
   */
  private readonly markTick = signal(0);

  /** 当前素材的标记颜色 —— 直接读节点字段(单一事实来源,不再另存一份)。 */
  readonly markColor = computed<MarkColor>(() => {
    this.markTick();                                   // 依赖计数器:标记一变立即重算
    const id = this.selectedId();
    const node = id ? this.findById(this.nodes(), id) : null;
    const c = node?.markColor;
    return c === 'orange' || c === 'red' || c === 'green' ? c : 'none';
  });

  /** 当前颜色对应的圆点色(给 Mark 按钮的旗子图标着色)。 */
  markDot(): string {
    return this.markColorOptions.find((c) => c.key === this.markColor())?.dot ?? '#c8cdd6';
  }

  /**
   * 选择标记颜色(none = 清除):更新节点字段 + 立即 PATCH 落库。
   * 乐观更新 —— 先改界面再发请求;失败则回滚并 toast,不让用户以为存上了。
   */
  setMarkColor(c: MarkColor): void {
    const id = this.selectedId();
    if (!id) return;
    const node = this.findById(this.nodes(), id);
    if (!node) return;
    const prev = node.markColor ?? null;
    node.markColor = c === 'none' ? null : c;          // 乐观更新(树是受控可变数据)
    this.markTick.update(v => v + 1);                  // 通知 computed 重算
    this.practiceApi.setMaterialMark(id, c).subscribe({
      error: () => {
        node.markColor = prev;                         // 失败回滚
        this.markTick.update(v => v + 1);
        this.toast(this.t('practice.retryLater'));
      }
    });
  }

  /** 点词展开音标与得分；再点同一个则收起。 */
  pickWord(i: number): void {
    this.pickedWord.set(this.pickedWord() === i ? null : i);
  }

  /** 在历史列表里点"查看报告"：把该条设为当前作品并弹到结果区。 */
  focusTake(id: string): void {
    this.activeTakeId.set(id);
    this.pickedWord.set(null);
  }

  // ---------- 评分配置(朗读语言 = 朗读与评分的唯一语言出口) ----------
  readonly config = signal<GradingConfig>({
    weights: [],
    strictness: 3,
    checkGrammar: true,
    advice: true,
    lang: 'en'
  });

  /**
   * ★ 2026-09-20(Forrest):编辑中还有未保存改动时,刷新/关闭浏览器要拦一下。
   * 浏览器的 beforeunload 只允许"重新加载 / 取消"两个按钮(全站通用规范,
   * 不允许自定义文案 —— 这是浏览器安全限制),语义上等价于 discard / stay。
   */
  @HostListener('window:beforeunload', ['$event'])
  guardUnsavedTree(ev: BeforeUnloadEvent): void {
    if (this.treeEditing() && this.treeDirty()) {
      ev.preventDefault();
      ev.returnValue = '';   // Chrome/Edge/Safari 都需要这个才弹确认
    }
  }

  ngOnInit(): void {    // ★ 2026-09-20(Forrest BUG1 修复):进页面即校准"Azure 是否就绪"。
    //   本地标记为 false 时以服务端 speech/settings 的 hasKey 为准 ——
    //   否则播放功能会被一个从未写入的 localStorage 标记永久挡死。
    this.syncAzureReadyFromServer();
    // 素材树改为从后端加载(2026-09-15 第十七轮)。
    // 2026-09-15 第十七轮:持久化迁到后端 —— 必须先等数据回来再选素材,
    // 否则 findFile 在空树上永远找不到,会错误地跳到第一个素材。
    // 恢复选中项的逻辑移到 afterTreeReady(),由加载回调触发。
    this.restoreTree();
  }

  /** 从 localStorage 取上次选中的素材 id。 */
  private readStoredMaterialId(): string | null {
    try {
      return localStorage.getItem('active_practice_material_id');
    } catch {
      return null;
    }
  }

  /** 记住当前选中的素材 id(localStorage + URL query 同步)。 */
  private rememberMaterialId(id: string): void {
    try {
      localStorage.setItem('active_practice_material_id', id);
    } catch {
      // 隐私模式:仅本次会话生效,不阻断
    }
    // URL 同步:用 replaceUrl 写,避免在浏览器历史里塞满刷屏条目
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { materialId: id },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  ngOnDestroy(): void {
    this.stopSpeak();
    this.stopPosTimer();
    // TTS 的 objectURL 不回收就是内存泄漏(每次合成一份 MP3 攒在内存里)
    if (this.ttsUrl) {
      URL.revokeObjectURL(this.ttsUrl);
      this.ttsUrl = null;
    }
    this.stopPlayback();
    // ★ 第二十六轮:回收鉴权拉流产生的 objectURL(否则离页泄漏)
    for (const u of this.audioBlobUrls.values()) URL.revokeObjectURL(u);
    this.audioBlobUrls.clear();
    // 离开页面时若还在录,直接丢弃,避免录到无关声音
    if (this.recorder.recording()) this.recorder.cancel();
  }

  t(key: string): string {
    return this.i18n.t(key);
  }

  // ---------- 选择素材:换素材一律回只读,防止误改 ----------
  onSelect(n: MaterialNode): void {
    if (this.editing()) return; // 编辑中先保存或取消
    this.selectFile(n);
  }

  private selectFile(n: MaterialNode): void {
    this.selectedId.set(n.id);
    // ★ 第二十七轮:记住 id↔名字,便于回读后端树后按名找回选中项
    this.lastKnownName.set(n.id, n.name);
    // 持久化选中项(任务书第三节):刷新/切题后仍停在同一素材
    this.rememberMaterialId(n.id);
    // 换素材 → 拉该素材的历史录音(2026-09-15 第十七轮:录音已落库)。
    // 按素材懒加载,同一个素材只拉一次。
    this.recorder.loadForMaterial(n.id);
    const text = n.content ?? '';
    this.saved.set(text);
    this.draft.set(text);
    this.editing.set(false);
    // 换素材后当前作品作废,必须重新点评分
    this.activeTakeId.set(null);
    // ★ 第十三轮:标记颜色现在直接读节点字段(markColor),换素材自动跟随,无需回读
    // ★ 2026-09-20(Forrest):换素材 → 销毁旧音频(stop + unload + 释放 URL),
    //   由统一的 destroyTtsAudio() 处理,避免各调用点漏掉某一项。
    this.destroyTtsAudio();
    void this.showCachedDuration(n.id, text);
  }

  /**
   * ★ 2026-09-20(Forrest):刷新/新进页面时,若本地已有该文本的音频,
   * 就地建一个 Howler 实例(不播放)让它的 on('load') 报出真实时长 ——
   * 这样进度条一开始就显示正确总时长,而不是等用户点播放。
   * 本地没有则什么都不做(时长显示 --,符合选项 1)。
   */
  /** ★ 2026-09-20:Azure 就绪后,若当前素材还没装载本地音频时长,补跑一次。 */
  private retryCachedDuration(): void {
    if (this.ttsHowl) return;             // 已经装载过了
    const id = this.selectedId();
    if (!id) return;
    const node = this.findFile(this.nodes(), id);
    if (node) void this.showCachedDuration(id, node.content ?? '');
  }

  private async showCachedDuration(materialId: string, text: string): Promise<void> {
    // ⚠️ 不要在这里判 azureReady:读**本地已缓存**的音频根本不需要 Azure,
    //    而且 azureReady 是异步校准的,早于它就绪会被误挡(2026-09-20 真因)。
    const clean = (text || '').trim();
    // 优先按文本指纹查;正文还没回填时,退回到按素材 id 查(Forrest 2026-09-20)。
    const key = clean ? ttsKey(clean, '', this.practiceLang()) : '';
    const cached = key ? await getCachedAudio(key) : await getCachedAudioByMaterial(materialId);
    if (!cached) return;
    // 期间用户可能已切走 —— 只在仍是同一素材时装载。
    if (this.selectedId() !== materialId) return;
    // 若正文已回填且与缓存那段不一致,说明文本改过,这份时长不再作数。
    const nowText = (this.saved() || '').trim();
    if (nowText && cached.text && nowText !== cached.text) return;

    const url = URL.createObjectURL(cached.blob);
    if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
    this.ttsUrl = url;
    this.ttsHowl?.unload();
    const h = new Howl({
      src: [url],
      format: this.howlFormat(cached.blob.type),   // blob URL 无扩展名 → 必须显式给
      // ★ 2026-09-20(Forrest 选项 1):预载时长用 Howler 的 Web Audio 模式 ——
      //   html5:true 是流式的,onload 时浏览器还没解析出媒体时长(duration()=0);
      //   html5:false 会完整解码,onload 时 duration() 立即可用。
      //   这是 Howler 原生行为,不做任何自写重试。
      // ★ 2026-09-20(Forrest 报"倍速声音扭曲"):改用 html5 —— 只有浏览器
      //   原生 <audio> 的 preservesPitch 能在变速时**保住音高**。
      //   (Web Audio 的 playbackRate 会连音高一起拉,人声就变味了。)
      html5: true,
      rate: this.rate(),
      onload: () => {
        this.speakDur.set(Math.round(h.duration()));   // Howler 的真实时长(此刻已可用)
        this.speakPos.set(0);
        this.cdr.detectChanges();
      },
      onplay: () => {
        this.speaking.set(true);
        this.startPosTimer();
        this.cdr.detectChanges();
      },
      onpause: () => {
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(h.seek() || 0);
        this.cdr.detectChanges();
      },
      onend: () => {
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(0);
        this.cdr.detectChanges();
      },
      onplayerror: () => {
        h.once('unlock', () => h.play());
      },
    });
    this.ttsHowl = h;
    this.keepPitch(h);
    // 预载的就是这份 → 点播放时文本指纹若与之一致就直接播,不再请求。
    this.ttsPlayingKey = cached.key;
  }

  onTreeChange(next: MaterialNode[]): void {
    this.nodes.set([...next]);
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
    this.nodes.set([...this.nodes()]);
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
    walk(this.nodes());
    try {
      localStorage.setItem(AiPracticeComponent.TREE_EXPANDED_KEY, JSON.stringify(ids));
    } catch { /* 隐私模式写不了就算了,不影响本次会话 */ }
  }

  /**
   * 按本地记录恢复展开/收起状态。
   * @returns true = 本地有记录并已应用;false = 没有记录(首次使用/存储被清)。
   * 恢复是"权威覆盖":数据库里 expanded 是上次保存时的旧值,以本地为准。
   */
  private restoreExpandedState(): boolean {
    try {
      const raw = localStorage.getItem(AiPracticeComponent.TREE_EXPANDED_KEY);
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
      walk(this.nodes());
      if (changed) this.nodes.set([...this.nodes()]);
      return true;
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
  private persistQuiet(force = false): void {
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
    this.practiceApi.saveMaterials(AiPracticeComponent.toPayload(this.nodes()), force).subscribe({
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
    }
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
      title: this.t('dialog.deleteNodeTitle').replace('{name}', n.name),
      body: this.t('dialog.deleteNodeBody'),
      confirmText: this.t('dialog.deleteConfirm'),
      danger: true
    }).then((ok) => {
      if (!ok) return;
      // 连同子树里的所有素材:挂在它们上面的录音也一并清掉
      const collect = (node: MaterialNode): void => {
        this.recorder.removeByMaterial(node.id);
        node.children?.forEach(collect);
      };
      collect(n);
      this.removeNodeById(n.id);
      // 选中项被删 → 清空右侧
      const stillThere = this.selectedId()
        ? this.findFile(this.nodes(), this.selectedId() ?? '') : null;
      if (this.selectedId() && !stillThere) {
        this.selectedId.set(null);
        this.saved.set('');
        this.draft.set('');
        this.editing.set(false);
        this.activeTakeId.set(null);
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
  // ---------- 编辑 / 保存 / 取消 ----------
  startEdit(): void {
    if (!this.selectedId()) return;
    this.draft.set(this.saved());
    this.editing.set(true);
  }

  /**
   * 保存正文编辑。
   *
   * ★ 第三十六轮 严重 Bug 修复(Forrest 报「编辑文本、保存后刷新就消失」)。
   *
   * 原实现只改了内存:
   *   target.content = text; this.nodes.set(...); this.saved.set(text);
   * —— **从头到尾没有调用 saveMaterials()**,所以正文从未进过数据库,
   *    刷新页面自然就回到旧内容。用户看到的就是"保存了但没保存"。
   *
   * 根本原因:树的其它变更(拖拽/改名/新建/删除)都走
   *   material-tree 的 nodeChange → onTreeChange() → persistQuiet()
   * 这条链路;而正文编辑是页面自己发的,绕过了 nodeChange,
   * 于是也一起绕过了持久化。
   *
   * 修法:提交后**显式调用与树变更同一条持久化链路**(persistQuiet),
   * 保证"编辑正文"与"拖动改名"的落库行为完全一致。
   * 失败时绝不假装成功:treeError 会亮起、treeDirty 置回 true。
   */
  saveEdit(): void {
    const id = this.selectedId();
    if (!id || !this.editing()) return;
    const target = this.findFile(this.nodes(), id);
    const text = this.draft();
    if (target) target.content = text;
    this.nodes.set([...this.nodes()]);
    this.saved.set(text);
    this.editing.set(false);
    // 内容变了,旧评分作废 —— 参考文本变了,旧分数不再对应这份文本
    const t = this.activeTake();
    if (t) this.recorder.clearScore(t.id);

    // ★ 关键修复:真正落到数据库。
    //   persistQuiet 内部会标记 treeDirty、失败回置并在成功后回读对齐 GUID,
    //   与拖拽/改名走的是同一条路径 —— 不再有"只有正文不落库"的特例。
    this.treeDirty.set(true);
    this.persistQuiet();

    // ★ 2026-09-20(Forrest 第三十六轮):正文变了 → 示范朗读音频必须作废重建。
    //   否则播放的还是旧文本那份录音(听起来就是"改了文本,读的还是老的")。
    this.destroyTtsAudio();
    this.rebuildTts();
  }

  cancelEdit(): void {
    this.draft.set(this.saved());
    this.editing.set(false);
  }

  /** 编辑态里按 Esc 取消、Ctrl/Cmd+S 保存。 */
  onDraftKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Escape') {
      ev.preventDefault();
      this.cancelEdit();
    } else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 's') {
      ev.preventDefault();
      this.saveEdit();
    }
  }

  // ---------- 示范朗读 ----------
  speak(): void {
    // ══ ★ 2026-09-20(Forrest):只用 Howler 播 Azure 真实音频,无任何兜底 ══
    //     播放 → play() · 暂停 → pause() · 播完再点 → 从头 play()

    // ── 门禁:没配 Azure Key 就**不能播**,并把原因直接摊给用户看 ──
    //    诚实红线的一半是不伪装,另一半是不能让人对着灰按钮发愣 ——
    //    所以这里顺手把右上角的配置浮层展开。
    if (!this.azureReady()) {
      this.ttsNote.set(this.t('practice.azurePlayBlocked'));
      this.toast(this.t('practice.azurePlayBlocked'));
      this.openTtsSettings();
      this.cdr.detectChanges();
      return;
    }

    const h = this.ttsHowl;
    if (h && this.speaking()) {
      h.pause();                 // Howler 的 onpause 会把 speaking 置 false
      return;
    }
    const text = this.editing() ? this.draft() : this.saved();
    const clean = (text || '').trim();
    if (!clean) return;

    // ★ Forrest 逻辑:文本没改 → 直接播本地/已装载的那份。
    const key = ttsKey(clean, '', this.practiceLang());
    if (h && h.state() === 'loaded') {
      if (this.ttsPlayingKey === key) {
        // 上一次是自然播完的 → 先归零,否则会从结尾处开始播
        if (this.ttsEnded) {
          h.seek(0);
          this.speakPos.set(0);
          this.ttsEnded = false;
        }
        this.unlockAudio();      // ★ 先唤醒 AudioContext,再 play
        h.play();                // 文本未变:续播/重播当前这份
        return;
      }
      // 文本变了 → 下面重新合成
    }

    this.synthesizeAndPlay(clean, key);
  }

  /**
   * ★ 2026-09-20(Forrest):按 Forrest 的逻辑取音频并播放 ——
   *   1. 先查**本地缓存**(IndexedDB):有就直接播,一个请求都不发;
   *   2. 本地没有 → 调 Azure 合成,拿到后**存本地**再播;
   *   3. 文本/语言变了 → key 变 → 自然走"重新合成"。
   */
  private async synthesizeAndPlay(text: string, key: string, autoplay = true): Promise<void> {
    this.ttsNote.set('');
    this.ttsCost.set('');
    this.ttsBilledChars.set(null);
    this.ttsFullChars.set(null);
    this.ttsVoice.set('');
    this.ttsAudioBytes.set(null);

    // ── 1. 本地缓存优先 ──
    const cached = await getCachedAudio(key);
    if (cached) {
      this.ttsCost.set('local');      // 如实标记:这次用的是本地那份
      this.ttsVoice.set(cached.voice);
      const url = URL.createObjectURL(cached.blob);
      if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
      this.ttsUrl = url;
      this.ttsPlayingKey = key;
      this.createHowl(url, autoplay, this.howlFormat(cached.blob.type));
      return;
    }

    // ── 2. 本地没有 → Azure 合成(speed 恒为 1.0;倍速是播放行为) ──
    this.practiceApi.synthesize(text, undefined, 1, false, this.practiceLang()).subscribe({
      next: (res) => {
        this.ttsCost.set(res.fromCache ? 'cache' : 'fresh');
        this.ttsBilledChars.set(res.billedChars);
        this.ttsFullChars.set(res.fullChars);
        this.ttsVoice.set(res.voice);
        this.ttsAudioBytes.set(res.audioBytes);

        // ── 3. 存本地,下次(含刷新)直接用,不再请求 ──
        // ★ 2026-09-20(Forrest 真因):Angular responseType:'blob' 拿到的
        //   Blob 常常**没有 MIME type**;<audio> 能嗅探播放,但 Howler 解码
        //   时拿不到时长(duration()=0)→ 刷新后显示 "--s"。
        //   这里显式补上类型,本地取回的 Blob 才有类型可用。
        const typedBlob = res.blob.type
          ? res.blob
          : new Blob([res.blob], { type: 'audio/mpeg' });
        void putCachedAudio({
          key,
          blob: typedBlob,
          voice: res.voice,
          lang: this.practiceLang(),
          savedAt: Date.now(),
          materialId: this.selectedId() ?? undefined,
          text,
        });

        const url = URL.createObjectURL(res.blob);
        if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
        this.ttsUrl = url;
        this.ttsPlayingKey = key;
        this.createHowl(url, autoplay, this.howlFormat(typedBlob.type));
      },
      error: (e) => {
        const status = (e as { status?: number } | null)?.status;
        // ★ 2026-09-23(Forrest):三种失败原因此前写死中文 → 接语言设置。
        const note =
          status === 503 ? this.t('practice.ttsNotConfigured')
            : status === 401 || status === 403 || status === 502
              ? this.t('practice.ttsKeyInvalid')
              : this.t('practice.ttsSynthFailed');
        this.ttsNote.set(note);
        // ★ 2026-09-20(Forrest 报"没反应"):失败必须**弹出来**,
        //   只靠播放条那一行小字容易看不到。
        this.toast(note + (status ? ' · ' + this.t('common.httpStatus').replace('{n}', String(status)) : ''));
        this.speaking.set(false);
        this.ttsBilledChars.set(null);
        this.ttsFullChars.set(null);
        this.ttsVoice.set('');
        this.ttsAudioBytes.set(null);
      }
    });
  }

  /**
   * ★ 2026-09-20(Forrest):按音源**新建** Howl 并播放。
   * Howler 用法:src 创建时给定;换音频就 unload 旧的、重建。
   * 时长/位置/结束/暂停全部由 Howler 自己的事件回调驱动。
   */
  /**
   * ★ 2026-09-20(Forrest 第三十六轮):按音源**新建** Howl。
   *
   * · autoplay = true  → 装载完立刻播放(用户点了播放键)
   * · autoplay = false → 只装载、取真实时长(编辑保存后重建 / 进页面预载)
   *
   * ⚠️ 关键坑(0s/0s 的真因):html5:true 是**流式**模式,onload 时浏览器
   *    还没解析出媒体元数据 → duration() 返回 0 → 时间文本变成 "0s / 0s"。
   *    这里统一 html5:false(Web Audio 全解码):onload 时 duration() 就是
   *    真实秒数。数值全部取自 Howler 自身,不做任何自写估算。
   */
  /**
   * ★ 2026-09-20(Forrest 报"点了没反应"的**真因**):
   * Howler 靠 src 的**扩展名**猜音频格式。我们给它的是 `blob:http://.../uuid`,
   * 没有扩展名 → Howler 判定"无可用解码器",直接抛
   * `No codec support for selected audio sources.` → onloaderror
   * → 旧代码没有 onloaderror 处理 → **点了完全没反应**。
   *
   * 修法:显式传 `format`。按 Blob 的真实 MIME 推导(后端换成 wav/ogg 也不会错),
   * Angular 的 blob 没有 MIME 时退回 mp3(Azure 的默认输出格式)。
   */
  /**
   * ★ 2026-09-20(Forrest 报"蓝色按钮点了没声音"):浏览器的自动播放策略会把
   *   Howler 的 Web Audio 上下文停在 suspended。此时 play() 既不报错也不出声,
   *   speaking 永远不会变 true —— 用户看到的就是"点了没反应"。
   *   点击本身就是合法的用户手势,这里主动把上下文唤醒。
   */
  private unlockAudio(): void {
    try {
      const ctx = (Howler as unknown as { ctx?: AudioContext }).ctx;
      if (ctx && ctx.state !== 'running') void ctx.resume();
    } catch { /* 拿不到上下文就算了,不影响主流程 */ }
  }

  private howlFormat(mime: string): string[] {
    const t = (mime || '').toLowerCase();
    if (t.includes('mpeg') || t.includes('mp3')) return ['mp3'];
    if (t.includes('wav') || t.includes('wave')) return ['wav'];
    if (t.includes('ogg') || t.includes('opus')) return ['oga', 'ogg'];
    if (t.includes('m4a') || t.includes('mp4') || t.includes('aac')) return ['m4a', 'aac'];
    if (t.includes('flac')) return ['flac'];
    if (t.includes('webm')) return ['webm'];
    return ['mp3'];   // 没有 MIME → 按 Azure 默认输出兜底
  }

  /**
   * ★ 2026-09-20(Forrest 报"选了倍速声音扭曲"):
   * Howler 的 `html5:false` 走 Web Audio 的 `playbackRate`,那是**硬拉采样率**,
   * 速度变的同时音高也跟着变 → 人声变成"花栗鼠"。
   *
   * `html5:true` 走浏览器原生 <audio>,它的 `preservesPitch`(Chrome/Safari/
   * Firefox 都有,默认就是 true)会在变速时**保住音高** —— 只是说得快一点,
   * 嗓音还是原来那个人。这才是"倍速只改变播放速度"。
   *
   * 注意:html5 模式下时长同样可靠 —— Howler 在 canplaythrough 里把
   * `_duration` 设成媒体时长(见 howler.js 的 _loadListener),所以
   * duration() 照样拿得到,不会再退回 "0s / 0s"(之前那个 0s 的真因
   * 是 blob URL 没扩展名导致解码失败,已经由 format 修掉)。
   */
  private keepPitch(h: Howl): void {
    try {
      type PitchNode = HTMLAudioElement & {
        preservesPitch?: boolean;
        mozPreservesPitch?: boolean;
        webkitPreservesPitch?: boolean;
      };
      const node = (h as unknown as { _sounds?: { _node?: PitchNode }[] })
        ._sounds?.[0]?._node;
      if (!node) return;
      node.preservesPitch = true;
      node.mozPreservesPitch = true;
      node.webkitPreservesPitch = true;
    } catch { /* 老浏览器没有这个属性就不勉强,退化成普通变速 */ }
  }

  private createHowl(url: string, autoplay = true, format?: string[]): void {
    this.ttsHowl?.unload();          // Howler 原生:释放旧实例
    this.ttsHowl = null;
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
    this.speakDur.set(0);

    const h = new Howl({
      src: [url],                    // ★ 音源在创建时给定(Howler 就是这样用)
      // ★ 必须显式给 format:blob: URL 没有扩展名,Howler 猜不出解码器
      //   → 直接 onloaderror("No codec support") → 这就是"点了没反应"的真因。
      format: format ?? ['mp3'],
      // ★ 2026-09-20(Forrest 报"倍速声音扭曲"):改用 html5 —— 只有浏览器
      //   原生 <audio> 的 preservesPitch 能在变速时**保住音高**。
      //   (Web Audio 的 playbackRate 会连音高一起拉,人声就变味了。)
      html5: true,                  // ★ 必须 false —— 否则拿不到真实时长
      rate: this.rate(),             // 当前倍速(只改播放速度,不动总时长)
      onload: () => {
        // ★ 总时长只在 load 回调里读,且只认 Howler 的返回值
        this.speakDur.set(Math.round(h.duration()));
        this.speakPos.set(0);
        this.cdr.detectChanges();
      },
      onplay: () => {
        this.speaking.set(true);
        this.startPosTimer();
        this.cdr.detectChanges();
      },
      onpause: () => {
        // 暂停 = 就地冻住:保留 seek() 的真实位置,下次 play() 从该处续播
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(h.seek() || 0);
        this.cdr.detectChanges();
      },
      onend: () => {
        // ★ 播完必须彻底复位 —— 否则按钮永远停在 ■、进度条卡在 100%
        this.speaking.set(false);
        this.stopPosTimer();
        this.speakPos.set(0);
        this.ttsEnded = true;
        // ★ 必须显式detectChanges:Howler 回调跑在 Angular 之外,
        //   不推一次变更检测,播放键不会立刻刷回 ▶。
        this.cdr.detectChanges();
      },
      onseek: () => {
        this.speakPos.set(h.seek() || 0);
        this.cdr.detectChanges();
      },
      // ★ 2026-09-20(Forrest 报"点了没反应"):装载/播放失败也必须出声,
      //   否则用户面对一个毫无反馈的按钮(真因排查时的最后一块拼图)。
      onloaderror: (_id: number, err?: unknown) => {
        console.warn('[tts] loaderror:', err);       // 保留一条线索,便于远程排障
        this.speaking.set(false);
        this.ttsNote.set(this.t('practice.noteLoadFail'));
        this.toast(this.t('practice.audioLoadFailToast'));
        this.cdr.detectChanges();
      },
      onplayerror: (_id: number, err?: unknown) => {
        console.warn('[tts] playerror:', err);
        this.speaking.set(false);
        this.ttsNote.set(this.t('practice.notePlayFail'));
        this.toast(this.t('practice.audioPlayFailToast'));
        this.cdr.detectChanges();
        // 浏览器自动播放策略:解锁后重播(Howler 自带 unlock)
        h.once('unlock', () => h.play());
      }
    });

    this.ttsHowl = h;
    this.keepPitch(h);               // ★ 变速不变声(见 keepPitch 的说明)
    this.ttsEnded = false;
    if (autoplay) {
      this.unlockAudio();                 // ★ 先唤醒 AudioContext,再 play
      h.play();
      // ★ 看门狗:1.2s 后仍没真正出声 → 明确说出来,绝不让人对着静音发愣
      setTimeout(() => {
        if (this.ttsHowl === h && h.state() === 'loaded' && !h.playing()) {
          this.ttsNote.set(this.t('practice.noteNotStarted'));
          this.toast(this.t('practice.audioBlockedToast'));
          this.cdr.detectChanges();
        }
      }, 1200);
    }
    this.cdr.detectChanges();
  }

  /**
   * ★ 2026-09-20(Forrest 方案 2):每 200ms 读一次 howl.seek() 搬进视图。
   * 只做"搬运真实值",不做任何估算;停止/暂停/结束即停表。
   */
  private startPosTimer(): void {
    this.stopPosTimer();
    this.posTimer = setInterval(() => {
      const h = this.ttsHowl;
      if (!h || !h.playing()) { this.stopPosTimer(); return; }
      this.speakPos.set(h.seek() || 0);
      // 时长只在尚未拿到时补一次;已拿到就以 onload 的值为准,保持整数秒
      const d = h.duration();
      if (d && isFinite(d) && d > 0 && !(this.speakDur() > 0)) {
        this.speakDur.set(Math.round(d));
      }
      this.cdr.detectChanges();
    }, 200);
  }

  private stopPosTimer(): void {
    if (this.posTimer !== null) {
      clearInterval(this.posTimer);
      this.posTimer = null;
    }
  }

  /** 当前应朗读/录音的文本(编辑态用草稿,只读态用已存)。 */
  private currentText(): string {
    return (this.editing() ? this.draft() : this.saved()) || '';
  }

  // ---------- 录音 ----------
  async startRecording(): Promise<void> {
    this.recError.set(null);
    const id = this.selectedId();
    if (!id) return;
    // ★ 第四十九轮:录音前停掉页面上所有在响的声音 ——
    //   示范朗读 / 历史录音回放 / 待提交试听。否则两路声音叠播,
    //   麦克风还会把正在放的内容录进去。
    this.stopSpeak();
    this.stopPlayback();
    this.pausePendingPreview();

    // ★ 第四十九轮:上一条录完还没提交,用户又按了麦克风 ——
    //   明确丢弃并告知。旧实现是等新录音录完由 finalize() 把它无声顶掉,
    //   那条已经录好的音频就【凭空消失】了,用户却以为它还在。
    if (this.recorder.pendingTake()) {
      this.recorder.discardPending();
      this.toast(this.t('practice.prevTakeDiscarded'));
    }

    const err = await this.recorder.start(id);
    if (err) this.recError.set(err);
  }

  stopRecording(): void {
    this.recorder.stop();
    // ⚠️ 2026-09-16(Forrest 本轮):**不再自动选中**刚录的这条。
    //    录音会停在 recorder.pendingTake(),界面出现"提交"按钮;
    //    用户点提交后它才进列表、才被选中。
    //    (旧行为是立刻选中 → 中间按钮变成"播放" → 用户以为没反应。)
  }

  // ---------- 待提交录音(Forrest 本轮:录完 → 提交 → 进列表) ----------

  /** 正在试听待提交的录音。 */
  /** 试听待提交录音的播放状态。⚠️ 必须是 public —— 模板里绑定了它
   *  ({{ previewingPending() ? 'pause' : 'play_arrow' }})。
   *  2026-09-16 教训:Angular 编译器对模板引用的 private 成员直接报 NG1,
   *  而 `tsc` 根本不查模板 → 本地 tsc 0 error 也拦不住。 */
  readonly previewingPending = signal(false);

  /** 试听/停止试听待提交的录音。 */
  previewPending(): void {
    const pt = this.recorder.pendingTake();
    if (!pt) return;

    if (this.previewingPending()) {
      this.pausePendingPreview();
      return;
    }

    // 试听前停掉其他声音(示范朗读 / 历史回放),避免叠音
    this.stopSpeak();
    this.stopPlayback();

    this.pendingAudio = new Audio(pt.url);
    // ⚠️ 用 addEventListener 而不是 `onended = …`:Zone.js 【不修补】属性式回调,
    //    那样写会让这个回调跑在 Angular Zone 之外,写 signal 也不触发刷新
    //    (第四十九轮"第二次录完没反应"就是同一类根因,详见 RecorderService.zone)。
    this.pendingAudio.addEventListener('ended', () => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    }, { once: true });
    void this.pendingAudio.play().catch(() => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    });
    this.previewingPending.set(true);
  }

  private pendingAudio: HTMLAudioElement | null = null;

  /** ★ 第四十九轮:停掉待提交录音的试听(统一的收口,防两路声音叠播)。 */
  pausePendingPreview(): void {
    if (this.pendingAudio) {
      this.pendingAudio.pause();
      this.pendingAudio = null;
    }
    this.previewingPending.set(false);
  }

  /** 提交待提交录音 → 进列表。 */
  submitPending(): void {
    const pt = this.recorder.pendingTake();
    if (!pt) return;
    // 停掉试听
    this.pausePendingPreview();

    this.recorder.submitPending();
    // 提交后把它设为当前作品 —— 用户下一步大概率就是回听/评分它。
    // ⚠️ 2026-09-16:这里是**本地临时 id**;上传成功后后端会换成正式 GUID,
    //    下面的 effect 会跟着把 activeTakeId 改过去,否则 activeTake() 会找不到人。
    this.activeTakeId.set(pt.id);
    // ★ 第五十轮(Forrest):提交成功不再弹提示条 ——
    //   录音已经出现在下方列表里,结果本身就是反馈;
    //   再弹一条带警示图标的"Submit"反而像出了问题(第五十轮截图反馈)。

    // 上传完成前先记住"待接替的本地 id"。
    this.pendingIdRemap = pt.id;
  }

  /**
   * 上传成功后,把当前作品 id 从本地临时 id 改后端 GUID。
   * 不做这一步的后果:列表里已经是 GUID,而 activeTakeId 还指向 r_xxx
   * → activeTake() 返回 null → 评分/回放全部"无反应"。
   *
   * ⚠️ 2026-09-16(真机报错,严重):这个方法里会写 activeTakeId,
   *    而它是在 effect 里被调用的 —— Angular 默认禁止在 effect 里写 signal,
   *    直接抛 NG0600 "Writing to signals is not allowed in a computed or an effect"。
   *    后果不只是这条报错:effect 整个炸掉,而 Submit / Retry 的可用状态
   *    依赖 recorder 状态流转 —— 于是按钮永远灰着、点了没反应。
   *
   *    修法:不在 effect 里直接写。改为把"待接替 id"记下来,
   *    用 queueMicrotask 推到 effect 之外再写 signal。
   *    (不用 allowSignalWrites:会引 Angular 18+ 才稳定的选项,
   *     且容易掩盖真实的循环依赖问题。)
   */
  private pendingIdRemap: string | null = null;

  private syncPendingIdRemap(): void {
    const oldId = this.pendingIdRemap;
    if (!oldId) return;
    // 本地 id 已不存在 = 已被后端 GUID 替掉
    const stillLocal = this.recorder.recordings().some((r) => r.id === oldId);
    if (stillLocal) return;

    const candidate = this.recorder.recordings().find((r) => r.uploaded);
    if (!candidate) return;

    this.pendingIdRemap = null;

    // ⚠️ 关键:状态写入必须跳出 effect 的同步执行上下文。
    const newId = candidate.id;
    queueMicrotask(() => {
      if (this.activeTakeId() === oldId) this.activeTakeId.set(newId);
    });
  }

  /** 丢弃待提交录音。 */
  discardPending(): void {
    this.pausePendingPreview();
    this.recorder.discardPending();
  }

  removeRecording(id: string): void {
    // ★ 2026-09-20(Forrest):删除录音必须先经确认弹窗,确认了才真删。
    // ★ 第五十轮(Forrest 截图反馈):标题已经问清"删除这条录音?",
    //   不再附加"删除后无法恢复"的说明行 —— 确认弹窗保持最简:
    //   一句问话 + 取消/确认(行业通行的轻量确认样式)。
    void this.confirmDialog({
      title: this.t('dialog.deleteRecTitle'),
      confirmText: this.t('dialog.deleteConfirm'),
      danger: true
    }).then((ok) => {
      if (!ok) return;
      this.recorder.remove(id);
      if (this.activeTakeId() === id) this.activeTakeId.set(null);
    });
  }

  /**
   * 对某条录音做发音评分(以当前素材文本为参考文本)。
   *
   * ★ 第五十轮(Forrest):胶囊上的「Run AI Scoring」已移除,这里是唯一评分入口
   *   (列表每行的 ✦ 按钮)。评分即把它设为当前作品 —— 报告区跟随被评的那条。
   *
   * ★ 第三十三/三十四轮(Forrest):如实标记本次评分花没花 Azure 额度与计费量
   *   判定依据是 grade() 里那条硬规则 `if (rec.score) return;`:
   *   有分 → 读库不调 Azure → 'cache';无分 → 真调 → 'fresh'。
   */
  async gradeRecording(id: string): Promise<void> {
    const t = this.myRecordings().find((r) => r.id === id);
    if (!t || t.grading) return;
    this.stopPlayback();
    this.activeTakeId.set(id);

    // ⚠️ 必须在 grade() 之前定下来:它可能直接把已有分数拿回来,
    //    事后看 rec.score 已经判不出"本次到底发没发请求"。
    this.scoreCost.set(t.score ? 'cache' : 'fresh');
    this.scoreBilledSeconds.set(t.score?.billedSeconds ?? null);
    this.scoreBilledBytes.set(t.score?.billedBytes ?? null);

    await this.recorder.grade(id, this.currentText(), this.practiceLang());

    // grade() 完成后回读计费口径:真调 → 新值;命中缓存 → 库里的旧值。
    const after = this.myRecordings().find((x) => x.id === id) ?? null;
    this.scoreBilledSeconds.set(after?.score?.billedSeconds ?? null);
    this.scoreBilledBytes.set(after?.score?.billedBytes ?? null);
  }

  // ---------- 当前作品(参考站的"录 → 回听 → 评分"流程) ----------

  /**
   * 当前作品 id。
   * 为什么要有"当前作品"这个概念:
   *   参考站是单题工作流 —— 一次只处理一条录音。
   *   没有这个概念,用户会对列表里随手一条录音点评分,
   *   很容易把上一次的分当成这一次的。
   */
  readonly activeTakeId = signal<string | null>(null);

  /** 当前作品(找不到则为 null)。 */
  readonly activeTake = computed(() => {
    const id = this.activeTakeId();
    if (!id) return null;
    return this.myRecordings().find((r) => r.id === id) ?? null;
  });

  /** 当前作品是否正在播放(波形"跳动"高亮用;回放入口在列表每行的播放键)。 */
  readonly activePlaying = computed(() => {
    const t = this.activeTake();
    return !!t && this.playingId() === t.id;
  });

  /** 停掉当前回放。 */
  private stopPlayback(): void {
    this.audio.pause();
    this.playingId.set(null);
    this.playPos.set(0);
  }

  // ---------- Sample Reading 播放进度 ----------

  /** 示范朗读进度百分比 —— 数据源与时间文本完全一致(Howler 真实值)。 */
  speakProgress(): number {
    const total = this.speakDur();
    if (!(total > 0)) return 0;              // 时长未知 → 0
    const raw = Math.min(this.speakPos(), total);
    return Math.min(100, Math.max(0, (raw / total) * 100));
  }

  /**
   * ★ 2026-09-20(Forrest):停止播放 —— Howler 原生 stop() 归零。
   * 实例保留,下次换素材/重播时再按需重建(unload + new Howl)。
   */
  stopSpeak(): void {
    this.ttsHowl?.stop();       // Howler 原生 stop:回到开头
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
  }

  /**
   * ★ 2026-09-20(Forrest 第三十六轮):彻底销毁当前音频。
   *
   * 用于"文本已变"的场景(编辑保存 / 切题):旧音频必须 stop + unload,
   * 否则 Howler 还持有上一次的资源,新音频的 onload 时长会被旧的干扰。
   * 这里只销毁,不重新合成 —— 重新合成由 rebuildTts() 负责。
   */
  private destroyTtsAudio(): void {
    if (this.ttsHowl) {
      this.ttsHowl.stop();
      this.ttsHowl.unload();      // Howler 原生:释放解码资源
      this.ttsHowl = null;
    }
    this.stopPosTimer();
    this.speaking.set(false);
    this.speakPos.set(0);
    this.speakDur.set(0);
    this.ttsPlayingKey = null;
    this.ttsEnded = false;
    if (this.ttsUrl) {
      URL.revokeObjectURL(this.ttsUrl);
      this.ttsUrl = null;
    }
    this.cdr.detectChanges();
  }

  /**
   * ★ 2026-09-20(Forrest 第三十六轮):文本变更后重建音频(不自动播放)。
   *
   * 编辑保存 → 立即调它。行为按 Forrest 要求:
   *   1. 正在播放就停掉,并销毁旧音频(stop + unload);
   *   2. 用**新文本**重新向 Azure 请求合成;
   *   3. 拿到音频后以 autoplay=false 装载 → 触发 onload →
   *      展示这份新文本的真实总时长,进度归零,等用户点播放。
   */
  private rebuildTts(): void {
    const clean = ((this.editing() ? this.draft() : this.saved()) || '').trim();
    if (!clean) { this.destroyTtsAudio(); return; }
    // 没配 Key 就不发请求 —— 发也一定失败,白白让用户等
    if (!this.azureReady()) { this.destroyTtsAudio(); return; }
    const key = ttsKey(clean, '', this.practiceLang());
    void this.synthesizeAndPlay(clean, key, /* autoplay */ false);
  }

  /**
   * ★ 2026-09-20(Forrest):切换倍速。
   *
   * 直接调 Howler 原生 rate() —— 成熟控件已完成原地变速(位置保留、不重播)。
   * 音频本身永远是 1.0 合成的那一份,所以**总时长恒定不变**。
   */
  setRate(r: number): void {
    this.rate.set(r);
    this.ttsHowl?.rate(r);    // 原生原地变速:位置保留、总时长不变
    if (this.ttsHowl) this.keepPitch(this.ttsHowl);   // ★ 每次变速都重申一次"保音高"
  }

  speakTimeText(): string {
    // ★ 2026-09-20(Forrest 第三十六轮):**未配 Azure Key 就没有音频这回事**。
    //   此时一律显示 --:-- / --:--,绝不拿 0 或估算值冒充时长。
    if (!this.azureReady()) return '--:-- / --:--';

    const total = this.speakDur();
    if (!(total > 0)) {
      // Azure 已配置、音频尚未装载 → 中性占位(0s / --s)
      return this.fmtDuration(this.speakPos()) + ' / --s';
    }
    // ★ 总时长**固定不变**:倍速只改播放速度(duration() 仍是 1x 的音源长度)
    return this.fmtDuration(this.speakPos()) + ' / ' + this.fmtDuration(total);
  }


  /**
   * 评分后的识别率 = 被正确识别的词占比。拿不到就返回 0。
   *
   * ⚠️ 2026-09-16(Forrest 本轮):只有**有词级数据且 errorType === 'None'**
   *    才计入"正确";无词级数据的词(errorType 为空串)不能算正确 ——
   *    否则会凭空空提高识别率,让报告看起来"假"。
   */
  recognitionPct(sc: RecordingScore): number {
    const scored = sc.words.filter((w) => !!w.errorType);
    if (!scored.length) return 0;
    const ok = scored.filter((w) => w.errorType === 'None').length;
    return Math.round((ok / scored.length) * 100);
  }

  /**
   * 五类错误计数(参考站右侧图例)。
   * ⚠️ 这五个 key 是 Azure PA 返回的 ErrorType 原值,不能改写成别的词:
   *   None / Mispronunciation / Omission / Insertion / UnexpectedBreak / MissingBreak
   */
  readonly errorCounts = computed(() => {
    const sc = this.activeTake()?.score;
    const empty = [
      { key: 'err.mispron', cls: 'e-mispron', n: 0 },
      { key: 'err.omission', cls: 'e-omission', n: 0 },
      { key: 'err.insertion', cls: 'e-insertion', n: 0 },
      { key: 'err.unexpectedBreak', cls: 'e-break', n: 0 },
      { key: 'err.missingBreak', cls: 'e-pause', n: 0 }
    ];
    if (!sc) return empty;

    const words = sc.words;
    const count = (t: string) => words.filter((w) => w.errorType === t).length;
    return [
      { key: 'err.mispron', cls: 'e-mispron', n: count('Mispronunciation') },
      { key: 'err.omission', cls: 'e-omission', n: count('Omission') },
      { key: 'err.insertion', cls: 'e-insertion', n: count('Insertion') },
      { key: 'err.unexpectedBreak', cls: 'e-break', n: count('UnexpectedBreak') },
      { key: 'err.missingBreak', cls: 'e-pause', n: count('MissingBreak') }
    ];
  });

  /** 逐词颜色:按 Azure 的 ErrorType 分类,而不是只看分数 —— 错误类型更有指导性。 */
  errorClass(errorType: string): string {
    switch (errorType) {
      case 'Mispronunciation': return 'e-mispron';
      case 'Omission': return 'e-omission';
      case 'Insertion': return 'e-insertion';
      case 'UnexpectedBreak': return 'e-break';
      case 'MissingBreak': return 'e-pause';
      // ⚠️ 2026-09-16:空串 = 该词无词级评估数据(Azure 未返回)。
      //    不能归为 e-ok(那会让它看起来"读对了"),用 e-na 中性色。
      case '': return 'e-na';
      default: return 'e-ok';
    }
  }

  /** 错误类型的中文名(用于 tooltip)。 */
  errLabel(errorType: string): string {
    switch (errorType) {
      case 'Mispronunciation': return this.t('err.mispron');
      case 'Omission': return this.t('err.omission');
      case 'Insertion': return this.t('err.insertion');
      case 'UnexpectedBreak': return this.t('err.unexpectedBreak');
      case 'MissingBreak': return this.t('err.missingBreak');
      case '': return this.t('err.noData');
      default: return this.t('err.ok');
    }
  }

  /**
   * 词是否带评分数据。
   * ⚠️ 2026-09-16(Forrest 本轮):没用它之前,无数据的词会渲染成 "0.0",
   *    看起来像"发音 0 分",其实是 Azure 没返回词级分 —— 这就是"假报告"的观感来源。
   */
  hasWordScore(w: { accuracy?: number; errorType?: string }): boolean {
    return !!w.errorType && typeof w.accuracy === 'number';
  }

  /** 时长格式化 —— ★ 2026-09-20(Forrest):秒一律取整,不显示小数。 */
  fmtDuration(sec: number): string {
    const t = Math.max(0, Math.floor(sec || 0));   // 抹掉小数(seek() 返回浮点)
    const m = Math.floor(t / 60);
    const s = t % 60;
    return m > 0
      ? m + ':' + String(s).padStart(2, '0')
      : s + 's';
  }

  fmtTime(ts: number): string {
    const d = new Date(ts);
    const p = (n: number) => String(n).padStart(2, '0');
    return p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' '
      + p(d.getHours()) + ':' + p(d.getMinutes());
  }

  // ---------- 录音回放(自绘播放器) ----------

  /**
   * 当前正在播放的录音 id。
   * 用单例 <audio> 而不是每条录音一个元素 —— 播放器同时只应有一个在响,
   * 单例天然保证互斥,也便于切歌时统一收尾。
   */
  private readonly audio = new Audio();
  readonly playingId = signal<string | null>(null);
  readonly playPos = signal(0);

  /**
   * ★ 第二十六轮:已上传录音的 blob → objectURL 缓存(按 recording id)。
   * 认证拉流拿到的 blob 不再重复下载;同时集中回收,防内存泄漏。
   */
  private readonly audioBlobUrls = new Map<string, string>();

  /** 正在"拉流加载"中的录音 id(界面可显示 loading)。 */
  readonly audioLoadingId = signal<string | null>(null);

  /** 拉流失败的真实原因(如实展示,绝不静默)。 */
  readonly audioError = signal<string | null>(null);
  /** 逐词明细展开状态(按录音 id)。 */
  /** 评分报告面板是否展开(任务书第三节:可随时收起/展开)。默认展开。 */
  readonly reportOpen = signal(true);

  /** 切换报告面板收起/展开。 */
  toggleReport(): void {
    this.reportOpen.set(!this.reportOpen());
  }

  /**
   * 把某条录音设为"当前作品"。
   *
   * ⚠️ 2026-09-15 第十二轮修两个严重 bug(Forrest 反馈"点 Take #1 没反应,
   *    还会跟上面的 Submit AI Scoring / play 联动"):
   *
   *  bug A —— **事件冒泡**:本行内部 4 个按钮都有各自的 (click),但都没
   *    stopPropagation,点击会冒泡到本行的 (click)=selectTake,于是
   *    "点播放" = togglePlay + selectTake 同时执行。
   *
   *  bug B —— **toggle 语义错误**:原实现"再点已选中行就取消选中 + stopPlayback"。
   *    与 bug A 叠加 → 点已选中行的播放:先开播,紧接着 selectTake 判定
   *    "已是选中态"→ 清空 activeTakeId + stopPlayback() → **刚开的播放被掐断**,
   *    上方 Submit/play 也一起变灰。这就是"联动、不能使用"的真因。
   *
   * 修法:
   *  1. selectTake 改为**幂等选择**(点已选中项不动,不再 toggle 取消);
   *  2. 行内所有按钮/track 的 click 一律 $event.stopPropagation()
   *     (模板侧改),让按钮点击不再误触发选行。
   */
  selectTake(id: string): void {
    // 幂等:已经是当前作品就原样返回,不做任何副作用。
    // 取消选中改由「点其他行」或删除行来完成,不用重复点同一行。
    if (this.activeTakeId() === id) return;
    this.stopPlayback();
    this.activeTakeId.set(id);
  }

  /** 录音列表里的 View Report —— 同一个 take 再点一次就收起(任务书要求 toggle)。 */
  toggleTakeReport(id: string): void {
    if (this.activeTakeId() === id && this.reportOpen()) {
      this.reportOpen.set(false);
      return;
    }
    this.activeTakeId.set(id);
    this.reportOpen.set(true);
  }

  /** 当前展开报告的 take 是否为这条录音(驱动按钮文案)。 */
  isTakeReportOpen(id: string): boolean {
    return this.activeTakeId() === id && this.reportOpen();
  }

  constructor() {
    this.audio.addEventListener('timeupdate', () => {
      this.playPos.set(this.audio.currentTime);
    });
    this.audio.addEventListener('ended', () => {
      this.playingId.set(null);
      this.playPos.set(0);
    });
    this.audio.addEventListener('pause', () => {
      // 播完自然暂停也会触发这里 —— 用 ended 已处理归零,这里只清高亮
      if (this.audio.ended) return;
      this.playingId.set(null);
    });

    // 监听录音列表变化:上传成功后把"当前作品"从本地临时 id 接到后端 GUID。
    // 不接的后果:activeTake() 找不到人 → 评分/回放全部无反应。
    effect(() => {
      this.recorder.recordings();   // 依赖
      this.syncPendingIdRemap();
    });

    // ★ 第二十九轮(对齐 ynwac 参考站):评分一产出,就把该录音的
    //   逐词分数明细【自动展开】。参考站点完评分立刻能看到每个词多少分;
    //   我们此前默认收起,把最关键的信息藏在折叠里 ——
    //   这也让"评分没有单词打分"看起来像功能缺失。
    //
    //   ⚠️ 记忆铁律:绝不在 effect 里写 signal(会触发 NG0600)。
    //   这里通过 queueMicrotask 把写操作推迟到 effect 之外执行。
    // ★ 第三十五轮(Forrest):逐词分值区已取消折叠 —— 常驻展开。
    //   原先"评分产出后自动展开"的 effect 已作废(ensureWordsOpen 现为空实现)。
    //   这里整段移除,避免无意义的响应式开销。

    // ★ 第三十轮:静态波形 —— 当前作品/待提交录音变化时重算波形柱高。
    //   同一段音频只解码一次(结果进 waveCache);
    //   异步完成后用 queueMicrotask 写信号,避开 effect 内写信号的 NG0600。
    effect(() => {
      const id = this.waveTargetId();
      const take = this.activeTake() ?? this.recorder.pendingTake();
      if (!id || !take) {
        queueMicrotask(() => this.waveBars.set([]));
        return;
      }
      const cached = this.waveCache.get(id);
      if (cached) {
        queueMicrotask(() => this.waveBars.set(cached));
        return;
      }
      queueMicrotask(() => this.waveBars.set([]));
      void (async () => {
        const src = await this.waveAudioSource(take);
        if (!src || src.size === 0) return;
        const bars = await this.computeWaveBars(src);
        if (!bars) return;
        this.waveCache.set(id, bars);
        // 只有当前目标仍是这条录音时才写入(避免异步竞态写错人的波形)
        if (this.waveTargetId() === id) this.waveBars.set(bars);
      })();
    });
  }

  isPlaying(id: string): boolean {
    return this.playingId() === id;
  }

  /**
   * 播放/暂停切换。切到另一条时先停掉当前这条。
   *
   * ★ 第二十六轮(401 修复):
   *   旧实现 `this.audio.src = api.audioUrl(id)` 把后端录音端点直接交给
   *   <audio src> —— 浏览器发这个请求**不带 Authorization 头**,
   *   而端点带 [Authorize] → 必然 401(Forrest 控制台看到的报错)。
   *
   *   新实现分两条路:
   *     a) 本页刚录、还没上传的 → 直接用本地 Object URL(零网络,零鉴权);
   *     b) 已上传的历史录音 → 先用 HttpClient(带 Bearer)拉回 blob,
   *        再 createObjectURL 交给 <audio>;blob 缓存在本组件 Map 里,
   *        同一条重复播放不再重复下载。
   *   拉取失败时如实提示,绝不静默假装能播。
   */
  togglePlay(r: Recording): void {
    // 播放期间先把示范朗读停掉,避免两种声音叠在一起
    this.stopSpeak();
    // ★ 第四十九轮:待提交录音的试听也一并停掉 —— 全页只允许一路声音
    this.pausePendingPreview();

    if (this.playingId() === r.id) {
      this.audio.pause();
      this.playingId.set(null);
      return;
    }

    // 换目标:若正在放别的,先停
    if (this.playingId() !== null) this.audio.pause();

    // (a) 本地 Object URL:刚录完、还没上传完成的,直接播,不发请求。
    if (r.url) {
      this.startAudio(r, r.url);
      return;
    }

    // (b) 已上传:命中缓存就直接播。
    const cached = this.audioBlobUrls.get(r.id);
    if (cached) {
      this.startAudio(r, cached);
      return;
    }

    // (b-2) 走带鉴权的 HttpClient 拉 blob(拦截器自动附 Bearer + 401 刷新)。
    this.audioLoadingId.set(r.id);
    this.practiceApi.fetchRecordingAudio(r.id).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        this.audioBlobUrls.set(r.id, url);
        this.audioLoadingId.set(null);
        this.startAudio(r, url);
      },
      error: (e) => {
        this.audioLoadingId.set(null);
        // 如实报错,绝不静默。401 已被拦截器处理;其余错误给出可读原因。
        const status = (e as { status?: number } | null)?.status;
        const raw = String((e as { message?: string } | null)?.message ?? e ?? '');
        // ★ 2026-09-20(Forrest 报 "take #1 点播报 数据不存在"):
        //   404 的真实含义不是"系统故障",而是**库里这条记录还在、音频文件没了**
        //   —— 服务端容器重建/清理时,容器内 storage/recordings 会被重置,
        //   而 PostgreSQL 里的录音行还在,于是列表还在、却取不到音频。
        const msg = status === 404
          ? this.t('practice.recGone')
          : this.t('practice.recLoadFail') + (raw || this.t('practice.retryLater'));
        this.audioError.set(msg);
        this.toast(msg);
      }
    });
  }

  /** 绑定 src 并起播(播放/暂停本地共用的收尾逻辑)。 */
  private startAudio(r: Recording, src: string): void {
    this.audio.src = src;
    this.audio.volume = 1;
    // ★ 与示范朗读同一口径:变速只改速度,不改嗓音
    (this.audio as HTMLAudioElement & { preservesPitch?: boolean }).preservesPitch = true;
    this.audio.currentTime = 0;
    this.playPos.set(0);
    void this.audio.play().then(
      () => this.playingId.set(r.id),
      // ★ 2026-09-20(Forrest 报"点了没声音"):浏览器拦截自动播放时
      //   play() 的 Promise 会 reject,旧代码只是把状态置回 null —— 用户
      //   看到的又是"点了没反应"。现在如实说一句。
      () => {
        this.playingId.set(null);
        this.toast(this.t('practice.audioBlockedToast'));
      }
    );
  }

  // ============================================================
  // ★ 第三十轮:静态波形(录完后)
  //   从当前作品/待提交录音的真实音频解出峰值柱,而不是画一条假的装饰波形。
  //   缓存:同一段音频只解码一次(waveCache),避免每次变更检测都重算。
  // ============================================================

  /** 当前是否有可供画波形的录音。 */
  readonly showWaveform = computed(() => {
    const t = this.activeTake();
    if (t) return true;
    return !!this.recorder.pendingTake();
  });

  /** 当前波形归属的录音 id(作品优先,否则待提交)。 */
  private waveTargetId(): string | null {
    const t = this.activeTake();
    if (t) return t.id;
    const p = this.recorder.pendingTake();
    return p?.id ?? null;
  }

  /** 静态波形的柱高数组(0~100,已按最大值归一化)。 */
  readonly waveBars = signal<number[]>([]);

  /** 解码结果缓存:录音 id → 柱高数组。 */
  private readonly waveCache = new Map<string, number[]>();

  /**
   * 音频源:优先内存里的 blob(刚录完),否则走带鉴权接口把已上传录音取回。
   * 与评分同一个取数策略(第二十九轮),避免「刷新后没有 blob」时波形画不出来。
   */
  private async waveAudioSource(rec: Recording): Promise<Blob | null> {
    const b = (rec as { blob?: Blob }).blob;
    if (b && b.size > 0) return b;
    if (RecorderService.isGuid(rec.id)) {
      try {
        return await firstValueFrom(this.practiceApi.fetchRecordingAudio(rec.id));
      } catch {
        return null;
      }
    }
    return null;
  }

  /** 解码音频并算出波形柱高。 */
  private async computeWaveBars(blob: Blob, bars = 56): Promise<number[] | null> {
    const Ctor = window.AudioContext
      || (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return null;
    const ctx = new Ctor();
    try {
      const buf = await blob.arrayBuffer();
      const audio = await this.decodeCompat(ctx, buf);
      const data = audio.getChannelData(0);
      if (!data.length) return null;
      const block = Math.max(1, Math.floor(data.length / bars));
      const out: number[] = [];
      let peak = 0;
      for (let i = 0; i < bars; i++) {
        let max = 0;
        const start = i * block;
        const end = Math.min(data.length, start + block);
        for (let j = start; j < end; j++) {
          const v = Math.abs(data[j]);
          if (v > max) max = v;
        }
        out.push(max);
        if (max > peak) peak = max;
      }
      // 归一化到 0~100:说话录音峰值通常远小于 1,必须归一化才看得出形状。
      const norm = peak > 0 ? out.map((v) => Math.max(6, Math.round((v / peak) * 100))) : [];
      return norm.length ? norm : null;
    } catch {
      return null;
    } finally {
      if (ctx.state !== 'closed') void ctx.close().catch(() => undefined);
    }
  }

  /** 与录音服务同款双形态解码(Safari 只支持回调式)。 */
  private decodeCompat(ctx: BaseAudioContext, buf: ArrayBuffer): Promise<AudioBuffer> {
    return new Promise<AudioBuffer>((resolve, reject) => {
      const anyCtx = ctx as unknown as {
        decodeAudioData: (
          b: ArrayBuffer,
          ok?: (x: AudioBuffer) => void,
          bad?: (e: unknown) => void
        ) => Promise<AudioBuffer> | void;
      };
      try {
        const ret = anyCtx.decodeAudioData(buf, resolve, reject);
        if (ret && typeof (ret as Promise<AudioBuffer>).then === 'function') {
          (ret as Promise<AudioBuffer>).then(resolve).catch(reject);
        }
      } catch (e) {
        reject(e);
      }
    });
  }

  /** 播放位置对应第几根柱子已播过(用于高亮)。 */
  wavePlayed(index: number): boolean {
    const bars = this.waveBars().length;
    if (!bars) return false;
    const t = this.activeTake();
    const p = this.recorder.pendingTake();
    const isActive = !!t && this.playingId() === t.id;
    const isPending = !!p && this.previewingPending();
    if (!isActive && !isPending) return false;
    return index / bars <= this.playPos() / Math.max(1, t?.duration ?? p?.duration ?? 1);
  }

  /** 播放进度百分比。 */
  progress(r: Recording): number {
    if (this.playingId() !== r.id || !r.duration) return 0;
    return Math.min(100, (this.playPos() / r.duration) * 100);
  }

  /** 当前播放位置的显示时间。 */
  playingTime(r: Recording): string {
    if (this.playingId() !== r.id) return '0:00';
    const s = Math.floor(this.playPos());
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  /** 点击时间轴跳转。 */
  seek(r: Recording, ev: MouseEvent): void {
    const el = ev.currentTarget as HTMLElement;
    const rect = el.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (ev.clientX - rect.left) / rect.width));

    if (this.playingId() !== r.id) {
      // 未在播放:先起播再定位
      this.togglePlay(r);
    }
    const target = ratio * (r.duration || 0);
    this.audio.currentTime = target;
    this.playPos.set(target);
  }

  /**
   * ★ 第三十五轮(Forrest):逐词分值区**已取消折叠,改为常驻展开**。
   *
   * 它只是纯文本展示(与 Recognition 同性质),没有任何理由需要点一下展开。
   * 这里保留方法签名并恒返回 true —— 这样旧调用点不会被破坏
   * (但模板里已经不再用了);今后若有人重新引入折叠,也不至于拿到错的值。
   */
  isWordsOpen(_id: string): boolean {
    return true;
  }

  /**
   * ★ 第三十五轮:折叠已取消,故本方法不再被模板调用。
   * 保留为空实现,避免旧引用直接报错;行为上什么也不做。
   */
  toggleWords(_id: string): void {
    // 第三十五轮:无操作 —— 逐词分值已改为常驻显示。
  }

  /**
   * ★ 第二十九轮:确保某条录音的逐词明细处于展开状态(幂等)。
   *
   * 与 toggleWords 的区别:**只展开,不收起** —— 供"评分完成后自动展开"
   * 使用。用户在展开后手动收起,不应被下一次数据刷新重新弹开,
   * 所以只对"尚未展开过"的 id 生效(由调用方判断 has())。
   */
  /**
   * ★ 第三十五轮:折叠已取消,自动展开逻辑也一并作废。
   * 保留空实现,避免旧调用点报错。
   */
  private ensureWordsOpen(_id: string): void {
    // 第三十五轮:无操作 —— 逐词分值始终可见,无需再维护展开状态。
  }

  /** 总分的中文口语化评价 —— 比裸数字直观。 */
  verdict(v: number): string {
    if (v >= 90) return this.t('verdict.excellent');
    if (v >= 80) return this.t('verdict.good');
    if (v >= 70) return this.t('verdict.fair');
    return this.t('verdict.poor');
  }

  /** 逐词颜色的分档。 */
  wordClass(acc: number): string {
    if (acc >= 80) return 'w-good';
    if (acc >= 60) return 'w-mid';
    return 'w-bad';
  }

  scoreClass(v: number): string {
    // 四档:颜色梯度足够细,又不至于让用户分不清
    if (v >= 85) return 's-good';
    if (v >= 70) return 's-mid';
    if (v >= 55) return 's-warn';
    return 's-bad';
  }

  // ---------- 工具 ----------
  /**
   * 把素材树拉平成中序列表(只取文件,不含文件夹)。
   * 题号与上一题/下一题都基于它 —— 树是分组的,但答题是一题一题过的,
   * 所以导航走扁平序列而不是树结构。
   */
  private flatten(list: MaterialNode[]): MaterialNode[] {
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

  private findFile(list: MaterialNode[], id: string | null): MaterialNode | null {
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
  private findParentFolderName(
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
