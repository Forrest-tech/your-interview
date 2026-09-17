import {
  Component, OnDestroy, OnInit, computed, effect, inject, signal
} from '@angular/core';
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
import { MatMenuModule } from '@angular/material/menu';
import { ActivatedRoute, Router } from '@angular/router';
import { MaterialNode, MaterialTreeComponent } from '../../shared/material-tree/material-tree.component';
import { MaterialNodeDto, MaterialNodeIn, PracticeApi } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Recording, RecordingScore, RecorderService } from '../../core/recorder/recorder.service';

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

  /** 录音失败提示(麦克风权限等)。 */
  readonly recError = signal<string | null>(null);

  /** 当前素材的录音列表。 */
  readonly myRecordings = computed(() =>
    this.recorder.forMaterial(this.selectedId())
  );

  // ---------- 素材树 ----------
  readonly nodes = signal<MaterialNode[]>([]);
  readonly selectedId = signal<string | null>(null);

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
   * Azure 是否就绪(声明级)。
   * 优先看本地标记;默认 **false** —— 未配置就不允许提交,这是任务书要求。
   */
  readonly azureReady = signal(readAzureReadyFlag());

  /** 能否提交评分:三个条件全满足。 */
  readonly canSubmitScoring = computed(() => {
    const t = this.activeTake();
    // ⚠️ 2026-09-16(真机 404 根因):必须同时满足
    //   · 已上传到服务端(t.uploaded)—— 否则 recordings/{id:guid}/score 直接 404
    //   · 还没评过分、也不在评分中
    return this.azureReady() && !!t && t.uploaded && !t.score && !t.grading;
  });

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
  //    现在唯一的评分入口是 scoreActiveTake(),它内部自行处理未就绪分支。

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

  // ---------- 2026-09-16 第十九轮:题号跳转(底部操作栏右侧) ----------
  /** 跳转输入框的当前值。 */
  readonly jumpDraft = signal('');

  /** 输入框变化(只允许数字)。 */
  onJumpDraftChange(v: string): void {
    this.jumpDraft.set(v.replace(/[^0-9]/g, ''));
  }

  /** 执行跳转:输入题号 → 选中对应素材。越界则 toast 提示,不静默失败。 */
  jumpToItem(): void {
    if (this.editing()) return; // 编辑中先保存或取消
    const n = Number(this.jumpDraft());
    const total = this.flatItems().length;
    if (!n || n < 1 || n > total) {
      this.toast(this.t('practice.jumpInvalid'));
      return;
    }
    const target = this.flatItems()[n - 1];
    if (target) {
      this.selectFile(target);
      this.jumpDraft.set('');
    }
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

  // ---------- 示范朗读 ----------
  readonly speaking = signal(false);
  readonly rate = signal(1);
  readonly rates = [0.5, 0.75, 1, 1.25, 1.5, 2];
  private utterance: SpeechSynthesisUtterance | null = null;

  /**
   * 当前正在播的 Azure TTS 音频（服务端合成的 MP3）。
   * 与 utterance 并存是因为两个引擎的停止方式不同:一个 cancel(),一个 pause()。
   */
  private ttsAudio: HTMLAudioElement | null = null;

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
  readonly ttsCost = signal<'' | 'fresh' | 'cache'>('');

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
    // 合成中的进度提示也不显示(用户看进度条就够)
    if (n.includes('合成')) return false;
    return /回退|未配置|失败|无法|拦截|invalid|failed/i.test(n);
  });

  /**
   * 示范朗读引擎。任务书第三节要求二选一,且选中状态要记住。
   *   browser = Web Speech API（免费/本地、即时、音质普通）
   *   azure   = Azure Neural TTS（高质量、消耗额度）
   * 注意：azure 只是**选中偏好**，真实合成仍走服务端
   * （key 绝不能下发浏览器 —— 见后端 PronunciationAssessor 的安全约定）。
   */
  readonly engine = signal<'browser' | 'azure'>(
    (localStorage.getItem('practice.ttsEngine') as 'browser' | 'azure') || 'azure'
  );

  setEngine(e: 'browser' | 'azure'): void {
    if (this.engine() === e) return;
    this.stopSpeak();
    this.engine.set(e);
    localStorage.setItem('practice.ttsEngine', e);
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

  /** 素材树是否已从后端加载完成（加载中不显示"空树"误导用户）。 */
  readonly treeLoading = signal(false);

  /** 后端加载失败时的真实原因（不吞错，让用户知道是后端没起还是别的）。 */
  readonly treeError = signal('');

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
    const payload = AiPracticeComponent.toPayload(this.nodes());
    this.practiceApi.saveMaterials(payload).subscribe({
      next: () => {
        this.treeDirty.set(false);
        this.treeError.set('');
        // ★ 第二十七轮(Bug2 真根因修复):
        //   后端 PUT /materials 只返回 { saved: N } ——
        //   它给**新建**的素材生成了 GUID,却**不回传**。
        //   若不回读,前端 nodes 里永远是 'f_intro_edu' 这类种子 id,
        //   于是 submitPending 的 isGuid(materialId) 判定为 false →
        //   录音**静默不上传** → 只存内存 → 刷新即丢(Forrest 报的数据丢失)。
        //   修法:保存成功后立刻回读后端树,用真实 GUID 替换本地节点。
        this.refreshTreeFromServer();
      },
      error: (e) => {
        // 绝不假装保存成功 —— 把后端给的真实原因显示出来
        this.treeError.set(this.errText(e));
        this.treeDirty.set(true);
      }
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
      },
      error: () => {
        // 回读失败不影响已成功的保存 —— 但要记住树脏了(下次再对齐)
        this.treeDirty.set(true);
      }
    });
  }

  /**
   * 用旧的(可能已失效的)id 或名字,在新树里找回对应节点。
   * 场景:保存前 selectedId='f_intro_edu',保存后端换成 GUID;
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
   * 首次使用（后端返回空树）时：把前端原有的 4 个种子素材**一次性导入后端**，
   * 这样老用户升级后不会发现素材全没了。导入后立刻回读确认真的落库了。
   */
  private restoreTree(): void {
    this.treeLoading.set(true);
    this.practiceApi.getMaterials().subscribe({
      next: (dtos) => {
        if (!dtos.length) {
          // 后端是空库 → 把种子导入，而不是只在前端显示（否则永远存不下来）
          this.importSeed();
          return;
        }
        this.nodes.set(AiPracticeComponent.fromDto(dtos));
        this.treeLoading.set(false);
        this.afterTreeReady();
      },
      error: (e) => {
        // 后端不可用：如实告知，前端仍可临时用种子数据做题，
        // 但明确标出"未连接后端，改动不会保存"，不让用户误以为存住了
        this.treeError.set(this.errText(e));
        this.treeLoading.set(false);
        this.nodes.set(this.seed());
        this.afterTreeReady();
      }
    });
  }

  /**
   * 旧版前端素材树的 localStorage 键。
   *
   * 第十七轮把持久化从 localStorage 迁到 PostgreSQL 时，设计上写了
   * 「把 localStorage 里的数据一次性导入」，但实际漏掉了读取这一步 ——
   * 结果老用户升级后只要后端是空库，就只剩 4 个硬编码种子，
   * 自己存的素材看起来「全没了」（数据其实还在浏览器里）。
   * 这里补上：空库时优先用 localStorage 的旧数据，没有才退回种子。
   */
  private static readonly LEGACY_TREE_KEY = 'practice.materials.v1';

  /** 读旧版 localStorage 素材树；损坏或不存在返回 null。 */
  private readLegacyTree(): MaterialNode[] | null {
    try {
      const raw = localStorage.getItem(AiPracticeComponent.LEGACY_TREE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as MaterialNode[];
      return Array.isArray(parsed) && parsed.length ? parsed : null;
    } catch {
      return null;
    }
  }

  /**
   * 空库时导入素材，并回读确认落库。
   *
   * 优先导入旧版 localStorage 里的真实素材（老用户升级不丢数据）；
   * 没有遗留数据时才用 4 个种子。
   */
  private importSeed(): void {
    const legacy = this.readLegacyTree();
    const source = legacy ?? this.seed();
    // 导入成功后清掉旧键，避免下次空库又被重复导入
    const isLegacy = legacy !== null;
    const payload = AiPracticeComponent.toPayload(source);
    this.practiceApi.saveMaterials(payload).subscribe({
      next: () => {
        // 旧数据已成功落库 → 清掉 localStorage 遗留键（幂等，不必再导）
        if (isLegacy) {
          try { localStorage.removeItem(AiPracticeComponent.LEGACY_TREE_KEY); } catch { /* 忽略 */ }
        }
        // 回读：不信任"保存返回成功"，直接问后端要一遍真实数据
        this.practiceApi.getMaterials().subscribe({
          next: (dtos) => {
            this.nodes.set(AiPracticeComponent.fromDto(dtos));
            this.treeLoading.set(false);
            this.afterTreeReady();
          },
          error: (e) => {
            this.treeError.set(this.errText(e));
            this.treeLoading.set(false);
            this.afterTreeReady();
          }
        });
      },
      error: (e) => {
        this.treeError.set(this.errText(e));
        this.treeLoading.set(false);
        this.nodes.set(this.seed());
        this.afterTreeReady();
      }
    });
  }

  /** 素材树就绪后的收尾：恢复上次选中的素材。 */
  private afterTreeReady(): void {
    const fromUrl = this.route.snapshot.queryParamMap.get('materialId');
    const wanted = fromUrl || this.readStoredMaterialId();
    const target = wanted ? this.findFile(this.nodes(), wanted) : null;
    const first = target && !target.folder ? target : this.firstFile(this.nodes());
    if (first) this.selectFile(first);
  }

  /** 前端节点 → 后端提交结构。改动本地结构时只改这一个映射。 */
  private static toPayload(list: MaterialNode[]): MaterialNodeIn[] {
    return list.map(n => ({
      id: n.id,
      name: n.name,
      folder: n.folder,
      content: n.content ?? null,
      sortOrder: 0,
      expanded: n.folder ? !!n.expanded : true,
      children: AiPracticeComponent.toPayload(n.children ?? [])
    }));
  }

  /** 后端结构 → 前端节点。 */
  private static fromDto(list: MaterialNodeDto[]): MaterialNode[] {
    return (list ?? []).map(d => ({
      id: d.id,
      name: d.name,
      folder: d.folder,
      content: d.content ?? '',
      expanded: !!d.expanded,
      children: AiPracticeComponent.fromDto(d.children ?? [])
    }));
  }

  private errText(e: unknown): string {
    return String((e as { message?: string } | null)?.message ?? e ?? '未知错误').slice(0, 200);
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
  /** 正文是否处于已标记(高亮)状态。 */
  readonly marked = signal(false);

  /** 标记当前朗读文本(高亮正文)。 */
  markAllText(): void {
    this.marked.set(true);
  }

  /** 清除标记。 */
  clearMark(): void {
    this.marked.set(false);
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

  // ---------- 评分配置(仅保留朗读语言,供 speechSynthesis 用) ----------
  readonly config = signal<GradingConfig>({
    weights: [],
    strictness: 3,
    checkGrammar: true,
    advice: true,
    lang: 'en'
  });

  ngOnInit(): void {
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
    this.stopSpeak();
  }

  onTreeChange(next: MaterialNode[]): void {
    this.nodes.set([...next]);
    // 树变了 → 标记未保存,驱动侧栏「保存修改」按钮亮起
    this.treeDirty.set(true);
    // 自动落盘一份:拖拽/重命名后自动持久化,不打断交互。
    // 显式「保存修改」按钮仍保留 —— 自动保存是兜底,按钮给用户掌控感。
    this.persistQuiet();
  }

  /**
   * 静默保存到后端(自动持久化用)。
   *
   * 为什么要静默:拖动排序会连发多次变更事件,每次都弹错会刷屏。
   * 但**绝不静默吞掉失败** —— 失败时置回 treeDirty 并记录 treeError,
   * 用户仍能看到"未保存"状态与具体原因,不会误以为存住了。
   */
  private persistQuiet(): void {
    this.practiceApi.saveMaterials(AiPracticeComponent.toPayload(this.nodes())).subscribe({
      next: () => {
        this.treeError.set('');
        // ★ 第二十七轮:自动保存同样要回读 —— 否则拖拽/新建之后
        //   前端仍持种子 id,录音上传被 isGuid 挡住(同 saveTree 的坑)。
        this.refreshTreeFromServer();
      },
      error: (e) => {
        this.treeError.set(this.errText(e));
        this.treeDirty.set(true);
      }
    });
  }

  onDelete(n: MaterialNode): void {
    // 素材删了,挂在它上面的录音也一并清掉
    this.recorder.removeByMaterial(n.id);
    if (n.id === this.selectedId()) {
      this.selectedId.set(null);
      this.saved.set('');
      this.draft.set('');
      this.editing.set(false);
      this.activeTakeId.set(null);
    }
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
    if (this.speaking()) {
      this.stopSpeak();
      return;
    }
    const text = this.editing() ? this.draft() : this.saved();
    if (!text.trim()) return;

    if (this.engine() === 'azure') {
      this.speakAzure(text);
      return;
    }

    // ---- 浏览器默认 TTS(Web Speech API) ----
    if (typeof speechSynthesis === 'undefined') return;
    this.speakDone.set(false);   // 点"朗读"即清除上一次的已读完定格

    const u = new SpeechSynthesisUtterance(text);
    u.rate = this.rate();
    u.lang = this.config().lang === 'zh' ? 'zh-CN'
      : this.config().lang === 'fr' ? 'fr-FR' : 'en-US';
    u.onend = () => { this.speakDone.set(true); this.stopSpeak(); };
    u.onerror = () => this.stopSpeak();
    this.utterance = u;
    this.speaking.set(true);
    speechSynthesis.speak(u);

    this.startSpeakTimer();
  }

  /**
   * Azure Neural TTS 示范朗读（2026-09-15 第十七轮：后端接口已就绪，改为真调用）。
   *
   * 链路：前端 POST /api/assessment/tts → 服务端持 key 调 Azure 神经语音 →
   *       回 MP3 流 → 前端 objectURL 播放。key 全程不进浏览器。
   *
   * ⚠️ 诚实回退规则（绝不含糊）：
   *   · 后端 503（未配 key / 服务未起）→ 回退浏览器 TTS，并**明确告知**
   *     "Azure 未配置，已用浏览器语音"，不让用户以为听到的是 Azure 人声。
   *   · 其他失败（401/403/502）→ 同样回退，但把真实原因说出来。
   *   · 回退绝不修改 azureReady：该 signal 第七轮起还兼管"AI 评分门禁",
   *     在此重置会把评分按钮连带锁死。
   */
  private speakAzure(text: string): void {
    // ⚠️ 2026-09-16(Forrest 本轮):开播时**不显示**"正在用 Azure 神经语音合成…" ——
    //    这是正常路径不是警示,用户只想看进度条。回退/出错时才会写入 note。
    this.ttsNote.set('');
    this.ttsCost.set('');   // 新一次朗读:先清掉上次的额度提示,结果出来再如实标记
    // 数字也一并清掉 —— 否则上一次的字符数会残留到新一次还没回来的时候
    this.ttsBilledChars.set(null);
    this.ttsFullChars.set(null);
    this.ttsVoice.set('');
    this.ttsAudioBytes.set(null);

    this.practiceApi.synthesize(text, undefined, this.rate()).subscribe({
      next: (res) => {
        const blob = res.blob;
        // ★ 第三十三/三十四轮:如实标记本次到底花没花额度、花了多少。
        //   来源是后端响应头,不是本地猜测。
        this.ttsCost.set(res.fromCache ? 'cache' : 'fresh');
        this.ttsBilledChars.set(res.billedChars);
        this.ttsFullChars.set(res.fullChars);
        this.ttsVoice.set(res.voice);
        this.ttsAudioBytes.set(res.audioBytes);
        const url = URL.createObjectURL(blob);
        // 上一次的 TTS objectURL 要先回收,否则连播多次会攒住内存
        if (this.ttsUrl) URL.revokeObjectURL(this.ttsUrl);
        this.ttsUrl = url;

        this.stopSpeakTimerOnly();
        this.speakElapsed.set(0);
        this.speakDone.set(false);   // 新一次播放:清除上一次的"已读完"定格
        this.speaking.set(true);
        // ⚠️ 2026-09-16(Forrest 本轮):这里**不再**设置
        //    "Azure 神经语音(服务端合成)" 这类正常标记 —— 样板朗读条上不显示引擎.
        //    回退/失败时下面的 error 分支会设入警示文案,那种才会显示(见 showTtsNote)。
        this.ttsNote.set('');
        this.ttsAudio = new Audio(url);
        // ⚠️ 2026-09-16:onended = **自然读完** → 先置 speakDone(进度条定格 100%),再 stopSpeak。
        //    顺序要紧:stopSpeak 会清零 elapsed,若后置 speakDone 会被看成一瞬间的 0%。
        this.ttsAudio.onended = () => {
          this.speakDone.set(true);
          this.stopSpeak();
        };
        this.ttsAudio.onerror = () => {
          // 合成成功但浏览器放不出来(极罕见)→ 如实回退,不装无声
          this.ttsNote.set('音频无法播放,已回退浏览器语音');
          this.speakAzureFallback(text);
        };
        void this.ttsAudio.play().catch(() => {
          this.ttsNote.set('浏览器拦截了自动播放,已回退浏览器语音');
          this.speakAzureFallback(text);
        });
        this.startSpeakTimer();
      },
      error: (e) => {
        const status = (e as { status?: number } | null)?.status;
        // ⚠️ 2026-09-16:后端已不再用 401 表示"Azure 密钥错误"(那会与"会话过期"
        // 混淆,导致点示范朗读被弹回登录页)。现在:
        //   503 = 未配置 / 502 = Azure 拒绝凭据或合成失败
        // 这里保留对 401/403 的识别仅作防御(旧版本后端可能仍返回)。
        const why = status === 503
          ? 'Azure 语音未配置'
          : status === 401 || status === 403 || status === 502
            ? 'Azure 密钥/区域无效'
            : '语音合成失败';
        this.ttsNote.set(`${why},已回退浏览器语音`);
        // 回退了就不是 Azure 合成:额度标记与数字都必须清掉,
        // 否则会把浏览器语音算到 Azure 头上(诚实红线)。
        this.ttsCost.set('');
        this.ttsBilledChars.set(null);
        this.ttsFullChars.set(null);
        this.ttsVoice.set('');
        this.ttsAudioBytes.set(null);
        this.speakAzureFallback(text);
      }
    });
  }

  /** 清掉计时器但不改 speaking —— Azure 播放接管前用,避免状态抖动。 */
  private stopSpeakTimerOnly(): void {
    this.clearSpeakTimer();
  }

  /** Azure 未就绪时的回退：仍用浏览器 TTS，保证可听。 */
  private speakAzureFallback(text: string): void {
    if (typeof speechSynthesis === 'undefined') return;
    const u = new SpeechSynthesisUtterance(text);
    u.rate = this.rate();
    u.lang = 'en-US';
    u.onend = () => { this.speakDone.set(true); this.stopSpeak(); };
    u.onerror = () => this.stopSpeak();
    this.utterance = u;
    this.speaking.set(true);
    speechSynthesis.speak(u);
    this.startSpeakTimer();
  }

  /**
   * speechSynthesis 没有进度回调 —— 只能自己定时走秒，仅供进度条展示。
   * 抽出来是因为两个引擎（browser / azure 回退）都得用。
   */
  private startSpeakTimer(): void {
    this.speakElapsed.set(0);
    this.speakDone.set(false);   // 开播即清除"已读完"定格,进度条重新从 0 走
    this.clearSpeakTimer();
    // ⚠️ 2026-09-16(第 2 条 bug B):总量在**开播那一刻锁死**。
    //    旧实现每秒钟重新调 estimateSeconds(),而它可能因实测值写入而变小,
    //    导致"已过秒数不变、总量变小"→ 进度条百分比反而往回退。
    //    现在把开播时的总量快照入变量,全程不变 —— 进度条只前进不后退。
    this.speakTotalSnapshot = this.estimateSeconds();
    this.speakTimer = setInterval(() => {
      this.speakElapsed.update((s) => {
        const next = s + 1;
        // 超出锁定的总时长就停在总量上,不让进度条冲过 100%。
        // ⚠️ 这**不会**截断音频 —— 朗读照样继续到 onend,
        //    只是秒表停在估算刻度(估算本身就偏保守)。
        return next > this.speakTotalSnapshot ? this.speakTotalSnapshot : next;
      });
    }, 1000);
  }

  stopSpeak(): void {
    // ⚠️ 顺序要紧:只有"自然读完"才算实测时长。
    //    用户在播放中途手动停止时不能把它当成总时长 —— 否则下次显示就偏短。
    //    所以下面记录实测前,先判断本次是否已经走到 onend(onend 会先置 speaking=false)。
    //
    // ⚠️ 2026-09-16(第 2 条 bug B):写入实测值必须同时写 measuredKey ——
    //    否则这个时长会被误用到下一段不同的文本上,导致 "0s / Ns" 显示错误。
    if (this.speaking() && this.speakElapsed() > 0) {
      this.measuredSpeakSeconds.set(this.speakElapsed());
      this.measuredKey.set(this.textKey(this.readonlyText() || ''));
    }
    if (typeof speechSynthesis !== 'undefined') {
      speechSynthesis.cancel();
    }
    // Azure TTS 音频也要停 —— 否则切素材后上一段人声还在念
    if (this.ttsAudio) {
      this.ttsAudio.pause();
      this.ttsAudio = null;
    }
    this.clearSpeakTimer();
    // ⚠️ 2026-09-16(Forrest 本轮):这里**故意不再清零 speakElapsed**。
    //    因为清零会让 speakProgress() 瞬间返回 0 → 进度条读完后又回跑。
    //    进度条改由 speakProgress() 用 speakDone 定格 100%(见其注释);
    //    elapsed 的清零统一在"下一次开播"(startSpeakTimer / speakAzure)做。
    this.speaking.set(false);
    this.utterance = null;
  }

  private clearSpeakTimer(): void {
    if (this.speakTimer) {
      clearInterval(this.speakTimer);
      this.speakTimer = null;
    }
  }

  setRate(r: number): void {
    this.rate.set(r);
    // 正在播就按新倍速重来,否则用户改了没感觉
    if (this.speaking()) {
      this.stopSpeak();
      this.speak();
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
    // 录音前先停掉示范朗读,否则录进去的是机器音
    this.stopSpeak();
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
      this.pendingAudio?.pause();
      this.pendingAudio = null;
      this.previewingPending.set(false);
      return;
    }

    // 试听前停掉其他声音(示范朗读 / 历史回放),避免叠音
    this.stopSpeak();
    this.stopPlayback();

    this.pendingAudio = new Audio(pt.url);
    this.pendingAudio.onended = () => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    };
    void this.pendingAudio.play().catch(() => {
      this.previewingPending.set(false);
      this.pendingAudio = null;
    });
    this.previewingPending.set(true);
  }

  private pendingAudio: HTMLAudioElement | null = null;

  /** 提交待提交录音 → 进列表。 */
  submitPending(): void {
    const pt = this.recorder.pendingTake();
    if (!pt) return;
    // 停掉试听
    if (this.pendingAudio) {
      this.pendingAudio.pause();
      this.pendingAudio = null;
    }
    this.previewingPending.set(false);

    this.recorder.submitPending();
    // 提交后把它设为当前作品 —— 用户下一步大概率就是回听/评分它。
    // ⚠️ 2026-09-16:这里是**本地临时 id**;上传成功后后端会换成正式 GUID,
    //    下面的 effect 会跟着把 activeTakeId 改过去,否则 activeTake() 会找不到人。
    this.activeTakeId.set(pt.id);
    this.toast(this.t('practice.submitTake'));

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
    if (this.pendingAudio) {
      this.pendingAudio.pause();
      this.pendingAudio = null;
    }
    this.previewingPending.set(false);
    this.recorder.discardPending();
  }

  removeRecording(id: string): void {
    this.recorder.remove(id);
    if (this.activeTakeId() === id) this.activeTakeId.set(null);
  }

  /** 对某条录音做发音评分(以当前素材文本为参考文本)。 */
  async gradeRecording(id: string): Promise<void> {
    await this.recorder.grade(id, this.currentText());
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

  /** 当前作品是否正在播放。 */
  readonly activePlaying = computed(() => {
    const t = this.activeTake();
    return !!t && this.playingId() === t.id;
  });

  /** 回放当前作品 / 暂停。 */
  togglePlayActive(): void {
    const t = this.activeTake();
    if (t) this.togglePlay(t);
  }

  /** 对当前作品跑评分。用当前素材文本作参考文本(scripted 模式)。 */
  async runActiveScoring(): Promise<void> {
    const t = this.activeTake();
    if (!t) return;
    this.stopPlayback();

    // ★ 第三十三轮(Forrest):如实标记本次评分花没花 Azure 额度。
    //
    // 判定依据不是"猜",而是 grade() 里那条硬规则:
    //   `if (rec.score) return;` —— 列表接口已把历史分带回,
    //   有分就直接返回、**不发任何评估请求**。
    // 所以:
    //   有分 → 本次不会调 Azure → 'cache'(读取本地已存评分)
    //   无分 → 本次会真调 Azure → 'fresh'(消耗额度)
    // ⚠️ 必须在 grade() **之前**就定下来,因为它可能直接把分数拿回来了,
    //    事后看 rec.score 已经判不出"本次到底发没发请求"。
    const hadScore = !!t.score;
    this.scoreCost.set(hadScore ? 'cache' : 'fresh');

    // ★ 第三十四轮:计费数字也要先清 —— 命中缓存时没有新计费,
    //   应回显库里存的旧值(下面根据录音最新状态同步)。
    const before = t.score;
    this.scoreBilledSeconds.set(before?.billedSeconds ?? null);
    this.scoreBilledBytes.set(before?.billedBytes ?? null);

    await this.recorder.grade(t.id, this.currentText());

    // grade() 完成后从录音最新状态里回读计费口径:
    //   · 本次真调了 Azure → 新值(刚生成的 billedSeconds)
    //   · 命中缓存 → 库里的旧值(刷新后仍能显示当时花了多少)
    const after = this.myRecordings().find((x) => x.id === t.id) ?? null;
    this.scoreBilledSeconds.set(after?.score?.billedSeconds ?? null);
    this.scoreBilledBytes.set(after?.score?.billedBytes ?? null);
  }

  /**
   * 重置当前作品。
   * 参考站原文 "Reset before scoring again" —— 重录前先清掉旧的评分痕迹,
   * 否则屏幕上的分数会让人分不清是哪一次的。
   */
  /**
   * 重置当前 take 的评分(只清分,保留录音)。
   * 这是给"评分结果不满意、想重评"用的。
   */
  resetTake(): void {
    const t = this.activeTake();
    if (!t) return;
    this.stopPlayback();
    this.recorder.clearScore(t.id);
    this.wordsOpen.set(new Set());
  }

  /**
   * 「Retry」按钮的真实语义(2026-09-15 第十轮修)。
   *
   * ⚠️ 之前 Retry 直接绑 resetTake(),只会把分数抹掉:
   *   录音还在 → activeTake() 仍非 null → 中间按钮仍是"回听",
   *   用户点完看到的界面几乎没变化,自然觉得"retry 没有用"。
   *
   * 现在改成真的"重录一遍" + **刷新下方列表**:
   *   2026-09-16(Forrest 本轮):
   *     · Retry 不再要求先选中某条 —— 没选中时也能用(只刷新列表);
   *     · 有选中时:删掉这条、重录;
   *     · 无论哪种,最后都重新拉一次录音列表("刷新下面的内容")。
   */
  retryTake(): void {
    const t = this.activeTake();
    this.stopPlayback();

    if (t) {
      // 有当前作品:这条不要了,删掉重录
      this.recorder.remove(t.id);
      this.wordsOpen.set(new Set());
      this.activeTakeId.set(null);
      void this.startRecording();
    } else {
      // 没选中:当作"刷新下方列表"按钮
      this.toast(this.t('practice.retryRefreshed'));
    }

    // 强制重新拉取当前素材的录音列表 —— 这就是 Forrest 要的"刷新下面的内容"。
    this.refreshRecordings();
  }

  /** 强制重新拉取当前素材的录音列表(忽略已加载缓存)。 */
  refreshRecordings(): void {
    const id = this.selectedId();
    if (!id) return;
    this.recorder.reloadForMaterial(id);
  }

  /** 停掉当前回放。 */
  private stopPlayback(): void {
    this.audio.pause();
    this.playingId.set(null);
    this.playPos.set(0);
  }

  // ---------- Sample Reading 播放进度 ----------

  /**
   * 示范朗读的估算时长(秒)。
   * speechSynthesis 没有可靠的进度回调,所以用词数估算 —— 只用于展示进度条,
   * 不用它做任何逻辑判断(估算不准不影响功能)。
   */
  /**
   * 示范朗读的显示总时长。
   *
   * ⚠️ 重要澄清(2026-09-15 核查):这里**没有**任何 10 秒硬编码,
   *    也没有 `setTimeout(…, 10000)` 截断定时器 —— 全文件 setTimeout 计数 = 0。
   *    speechSynthesis 是浏览器把文本**整段**读完才触发 onend,不存在截断。
   *    之前看到的固定值是因为它是**词数估算**(非固定 10s),而估算值在
   *    文本不变时恒定,看起来像"写死"。
   *
   * 这里改为:ll浏览器 TTS 且已经实测过真实音频时长时,优先用实测值。
   *    实测来源 = 录音回放(HTMLAudioElement.duration),或 speak() 完成后的计量。
   *    拿不到实测才回落词数估算——估算只用于进度条,不参与任何逻辑判断。
   */
  /**
   * 示范朗读总时长(秒),用于进度条与 "0s / Ns" 显示。
   *
   * ⚠️ 2026-09-16(Forrest 第 2 条)修两个真 bug:
   *
   *   bug A — 切换素材后时长显示错:
   *     旧实现只存一个全局 measuredSpeakSeconds。读完文本 A(比如 8s)
   *     再切到文本 B(比如长句),measured 仍是 A 的 8s → 界面显示
   *     "0s / 8s",与实际不符。
   *     → 现在把实测值**绑定到它对应的文本哈希**。文本一变,实测值立即失效,
   *       回落词数估算。这样"切换一下时间就错了"不会再发生。
   *
   *   bug B — 读完后进度条往回跑:
   *     秒表在跑的时候用的是**估算值**(比如 12s);读完后 stopSpeak 写回
   *     实测值(比如 9s)。总量突然从 12 变 9,而滑块位置 = 已过/总量,
   *     于是进度条会从 75% 猛地跳回 100% 再回落 —— 视觉上就是"往回跑"。
   *     → 现在实测值只在**同一文本、且未在播放中**时接管;
   *       播放期间总量恒定不变,进度条只前进不后退。
   */
  readonly estimateSeconds = computed(() => {
    const text = this.readonlyText() || '';

    // 只有"实测值属于当前这段文本"时才采用实测 —— 切文本即失效
    if (this.measuredKey() === this.textKey(text)) {
      const measured = this.measuredSpeakSeconds();
      if (measured > 0) return measured;
    }

    const words = text.trim().split(/\s+/).filter(Boolean).length;
    // 英语朗读大约 150 词/分钟 ≈ 2.5 词/秒,除以倍速
    return Math.max(1, Math.round(words / 2.5 / this.rate()));
  });

  /**
   * 实测的示范朗读时长(秒)。0 = 尚未测得。
   * ⚠️ 必须与 measuredKey 配套使用 —— 单看它无法判断这个时长属于哪段文本。
   */
  readonly measuredSpeakSeconds = signal(0);

  /** 上面那个实测值对应的文本指纹(空 = 无实测值可用)。 */
  private readonly measuredKey = signal('');

  /** 文本指纹:用长度 + 首尾片段做轻量标识,足够区分"换没换素材"。 */
  private textKey(text: string): string {
    const t = (text || '').trim();
    if (!t) return '';
    return t.length + ':' + t.slice(0, 24) + ':' + t.slice(-24);
  }

  readonly speakElapsed = signal(0);
  private speakTimer: ReturnType<typeof setInterval> | null = null;

  /**
   * 开播那一刻锁定的总时长(秒)。
   * ⚠️ 2026-09-16(第 2 条 bug B):进度条必须用**锁定的快照**而不是实时
   *    estimateSeconds();否则实测值写入使总量变小时,百分比会往回退。
   */
  private speakTotalSnapshot = 0;

  /** 示范朗读进度百分比。
   *
   * ⚠️ 2026-09-16(Forrest 本轮):**读完了进度条就停在 100%,不要再动**。
   *    旧实现在播放结束时 stopSpeak() 会把 speakElapsed 清零 + speaking=false,
   *    于是 speakProgress() 立刻返回 0 → 进度条唰地**倒回去**,
   *    正是 Forrest 反映的"读完了进度条又自己往回跑"。
   *
   *    现在改成三态:
   *      · 播放中 → 按已过/锁定总量的真实百分比;)
   *      · 已读完(见 speakDone)→ 恒定 100%,停在满格不动;
   *      · 从未播放 → 0%。
   *    speakDone 只在**自然读完**时置位,手动停播不会置位(避免假装读完)。
   */
  speakProgress(): number {
    if (this.speakDone()) return 100;   // 读完了:定格满格,不再回跑
    if (!this.speaking()) return 0;     // 从未播放/已重置
    const total = this.speakTotalSnapshot || this.estimateSeconds();
    if (!total) return 0;
    return Math.min(100, (this.speakElapsed() / total) * 100);
  }

  /**
   * 本次示范朗读是否已**自然读完**(读到结尾)。
   * ⚠️ 只有 onend 才算;用户中途按停**不算**(否则会伪装成"读完了")。
   * 切素材 / 重新开播 / 手动停播都会清除它。
   */
  readonly speakDone = signal(false);

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

  /** 录音时长格式化。 */
  fmtDuration(sec: number): string {
    const m = Math.floor(sec / 60);
    const s = sec % 60;
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

  /**
   * 绿色胶囊右侧的「点击进行AI评分」(任务书第二节)。
   * 对当前选中的那条录音发起真实评分;没有选中就不做任何事。
   */
  scoreActiveTake(): void {
    const t = this.activeTake();
    if (!t || t.grading) return;
    void this.gradeRecording(t.id);
  }

  /** 当前展开报告的 take 是否为这条录音(驱动按钮文案)。 */
  isTakeReportOpen(id: string): boolean {
    return this.activeTakeId() === id && this.reportOpen();
  }

  /**
   * 逐词明细折叠状态。
   *
   * ★ 第三十五轮(Forrest):逐词分值区已取消折叠,改为常驻展开。
   *   保留该 signal 仅为兼容旧重置调用点(已经不需要跟踪状态)。
   */
  private readonly wordsOpen = signal<Set<string>>(new Set());

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
        const msg = String((e as { message?: string } | null)?.message ?? e ?? '');
        this.audioError.set(msg || '录音回放加载失败,请稍后重试。');
        this.toast(this.audioError()!);
      }
    });
  }

  /** 绑定 src 并起播(播放/暂停本地共用的收尾逻辑)。 */
  private startAudio(r: Recording, src: string): void {
    this.audio.src = src;
    this.audio.currentTime = 0;
    this.playPos.set(0);
    void this.audio.play().then(
      () => this.playingId.set(r.id),
      () => this.playingId.set(null)  // 浏览器拦自动播放时如实置回
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

  private seed(): MaterialNode[] {
    return [
      {
        id: 'f_intro', name: '自我介绍', folder: true, expanded: true,
        children: [
          {
            id: 'f_intro_edu', name: '学历', folder: false,
            content:
              'I hold a Master of Science in Computational Science from Laurentian University, ' +
              'completed in 2025, and a Bachelor of Engineering from Chengdu University of ' +
              'Information Technology.'
          },
          {
            id: 'f_intro_wp', name: '工签状态', folder: false,
            content:
              'I am currently authorized to work in Canada and my status is valid through 2027. ' +
              'I do not require sponsorship for this role.'
          },
          {
            id: 'f_intro_pitch', name: '一分钟自我介绍', folder: false,
            content:
              'Hi, I am Forrest. I am a senior full-stack engineer with over ten years of ' +
              'experience building high-concurrency financial and logistics platforms with ' +
              'C#/.NET and Angular.'
          }
        ]
      },
      {
        id: 'f_company', name: '目前的公司介绍', folder: true, expanded: true,
        children: [
          {
            id: 'f_company_now', name: '公司业务', folder: false,
            content:
              'LaughTale builds a logistics and customs clearance platform serving cross-border ' +
              'freight forwarders in North America.'
          },
          {
            id: 'f_company_duty', name: '我的工作职责', folder: false,
            content:
              'I own the backend services for shipment tracking and customs document workflows, ' +
              'and I lead the Angular front-end for the operations console.'
          }
        ]
      },
      {
        id: 'f_tech', name: '技术介绍', folder: true, expanded: false,
        children: [
          {
            id: 'f_tech_dotnet', name: '.NET 高并发', folder: false,
            content:
              'I design RESTful services on .NET 8 following Clean Architecture, with EF Core for ' +
              'data access and Redis for caching hot paths.'
          },
          {
            id: 'f_tech_arch', name: '架构演进', folder: false,
            content:
              'I have migrated a monolithic scheduling service into bounded contexts behind an ' +
              'API gateway using the strangler-fig pattern.'
          }
        ]
      },
      {
        id: 'f_bq', name: '常见追问', folder: true, expanded: false,
        children: [
          {
            id: 'f_bq_why', name: '为什么换工作', folder: false,
            content: 'I am looking for a team where I can own architecture decisions end to end.'
          },
          {
            id: 'f_bq_gap', name: '职业空档说明', folder: false,
            content: 'I used the period to complete my master degree in Canada.'
          }
        ]
      }
    ];
  }
}
