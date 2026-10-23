import { ChangeDetectorRef, Component, HostListener, OnDestroy, OnInit, ViewChild, computed, effect, inject, signal } from '@angular/core';
import { Howler } from 'howler';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDividerModule } from '@angular/material/divider';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatDialog } from '@angular/material/dialog';
import { ActivatedRoute, Router } from '@angular/router';
import { MaterialNode, MaterialTreeComponent } from '../../shared/material-tree/material-tree.component';
import { PracticeApi } from '../../core/api/practice-api.service';
import { KnowledgeApiService } from '../../core/api/knowledge-api.service';
import { AiApiService } from '../../core/api/ai-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Recording, RecordingScore, RecorderService } from '../../core/recorder/recorder.service';
import { PracticeMaterialTree } from './practice-material-tree';
import { PracticeTtsPlayer } from './practice-tts-player';
import { PracticeGradingSession } from './practice-grading-session';
import type { MarkColor } from './practice-material-tree';


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
    MatSliderModule, MatSlideToggleModule, MatMenuModule, MatProgressBarModule,
    MaterialTreeComponent
  ],
  templateUrl: './ai-practice.component.html',
  styleUrl: './ai-practice.component.scss'
})
export class AiPracticeComponent implements OnInit, OnDestroy {
  readonly tree = new PracticeMaterialTree(this);
  readonly tts = new PracticeTtsPlayer(this);
  readonly grading = new PracticeGradingSession(this);  readonly recError = this.grading.recError;
  readonly myRecordings = this.grading.myRecordings;
  readonly nodes = this.tree.nodes;
  readonly fromTechNodes = this.tree.fromTechNodes;
  readonly categories = this.tree.categories;
  readonly categoryFilter = this.tree.categoryFilter;
  readonly isFromTechSelected = this.tree.isFromTechSelected;
  readonly treeOpen = this.tree.treeOpen;
  readonly treeWidth = this.tree.treeWidth;
  readonly azureReady = this.grading.azureReady;
  readonly canSubmitTake = this.grading.canSubmitTake;
  readonly uploadingPending = this.grading.uploadingPending;
  readonly speaking = this.tts.speaking;
  readonly rate = this.tts.rate;
  readonly rates = this.tts.rates;
  readonly ttsNote = this.tts.ttsNote;
  readonly ttsBilledChars = this.tts.ttsBilledChars;
  readonly ttsFullChars = this.tts.ttsFullChars;
  readonly scoreCost = this.grading.scoreCost;
  readonly scoreSecondsText = this.grading.scoreSecondsText;
  readonly langMismatch = this.tts.langMismatch;
  readonly mismatchLangLabel = this.tts.mismatchLangLabel;
  readonly treeDirty = this.tree.treeDirty;
  readonly treeEditing = this.tree.treeEditing;
  readonly treeLoading = this.tree.treeLoading;
  readonly treeLoadFailed = this.tree.treeLoadFailed;
  readonly pickedWord = this.grading.pickedWord;
  readonly markColorOptions = this.tree.markColorOptions;
  readonly markColor = this.tree.markColor;
  readonly fillerStats = this.grading.fillerStats;
  readonly speechPace = this.grading.speechPace;
  readonly oneFix = this.grading.oneFix;
  readonly followUpQuestion = this.grading.followUpQuestion;
  readonly followUpLoading = this.grading.followUpLoading;
  readonly previewingPending = this.grading.previewingPending;
  readonly activeTakeId = this.grading.activeTakeId;
  readonly activeTake = this.grading.activeTake;
  readonly activePlaying = this.grading.activePlaying;
  readonly errorCounts = this.grading.errorCounts;
  readonly reportOpen = this.grading.reportOpen;
  readonly showWaveform = this.grading.showWaveform;
  readonly waveBars = this.grading.waveBars;

  readonly i18n = inject(I18nService);
  readonly recorder = inject(RecorderService);
  readonly router = inject(Router);
  readonly route = inject(ActivatedRoute);
  // ★ 2026-09-20(Forrest):Howler 的事件回调在 Angular 之外触发,
  //   必须手动 detectChanges 才能让模板看到最新的位置/状态。
  readonly cdr = inject(ChangeDetectorRef);
  readonly selectedId = signal<string | null>(null);

  /** 编辑态正文(仅编辑模式下可改)。 */
  readonly draft = signal('');

  /** 是否处于编辑态。默认只读。 */
  readonly editing = signal(false);

  /** 已保存的正文快照(退出编辑/保存时写入)。 */
  readonly saved = signal('');

  readonly selectedNode = computed(() => {
    const id = this.selectedId();
    if (!id) return null;
    // ★ From Tech:实时条目活在 fromTechNodes() 里(id 带 ts: 前缀),
    //   右侧标题必须去那棵树里找,否则永远 null → 显示 "No material selected"。
    const tree = id.startsWith('ts:') ? this.tree.fromTechNodes() : this.tree.nodes();
    return this.tree.findFile(tree, id);
  });

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
  readonly parentFolderName = computed(() => {
    const id = this.selectedId();
    if (!id) return null;
    // ★ 同上:from-tech 的父文件夹(即 Topic)也在 fromTechNodes() 里找,
    //   否则标题丢 "Topic - 条目名" 的前缀,和其它分类不一致。
    const tree = id.startsWith('ts:') ? this.tree.fromTechNodes() : this.tree.nodes();
    return this.tree.findParentFolderName(tree, id, null);
  });

  /** 只读态显示的正文。 */
  readonly readonlyText = computed(() => this.saved());

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

  // ⚠️ 2026-09-16(Forrest 第 4 条):底部"提交 AI 评分"按钮已删除 ——
  //    它与录音区右侧的 run ai scoring 胶囊是同一动作,重复只会让人困惑。
  //    onSubmitScoringClick() 随之移除(仅有的调用点就是那个被删的按钮)。
  // ★ 第五十一轮(Forrest):胶囊上的 Run AI Scoring 也一并移除,
  //    全站唯一评分入口 = 列表每行的 ✦ 按钮 → gradeRecording(),别无分支。

  /** 轻量 toast(不引入 MatSnackBar,避免多一个依赖)。 */
  readonly toastMsg = signal('');
  toast(text: string): void {
    this.toastMsg.set(text);
    setTimeout(() => this.toastMsg.set(''), 3200);
  }

  toggleTree(): void {
    this.tree.treeOpen.set(!this.tree.treeOpen());
  }

  // ---------- 题目导航(扁平化后可上一题/下一题) ----------
  /** 所有可作答的素材(扁平列表,按树的中序),用于题号与上下题导航。 */
  readonly flatItems = computed(() => this.tree.flatten(this.tree.nodes()));

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

  /**
   * ★ 2026-09-24:引擎/失败提示不再渲染在播放胶囊里(Forrest 指令),
   *   改为音频区下方的行内警示条(.tts-alert),ttsNote 仅作数据源。
   *   原 showTtsNote 门禁随之删除 —— 警示条永远如实显示 ttsNote。
   */

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
  @ViewChild('engineTrigger') engineTrigger?: MatMenuTrigger;

  /** 后端访问层（素材树 / 录音 / 语音设置 / TTS）。 */
  readonly practiceApi = inject(PracticeApi);
  /** Knowledge API(Tech Stack 实时视图用) */
  readonly knowledgeApi = inject(KnowledgeApiService);
  /** AI complete(追问生成) */
  readonly aiApi = inject(AiApiService);

  /** 统一确认弹窗(保存/删除都要先过这一关,Forrest 2026-09-20)。 */
  readonly dialog = inject(MatDialog);

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
    if (!(this.tree.treeEditing() && this.tree.treeDirty())) return;
    const k = ev.key;
    const mod = ev.ctrlKey || ev.metaKey;
    const isReload =
      k === 'F5' || (mod && !ev.shiftKey && (k === 'r' || k === 'R')) ||
      (mod && ev.shiftKey && (k === 'r' || k === 'R'));
    if (!isReload) return;

    ev.preventDefault();
    ev.stopPropagation();
    void this.tree.confirmDialog({
      title: this.t('dialog.unsavedTitle'),
      body: this.t('dialog.unsavedBody'),
      confirmText: this.t('dialog.saveAndReload'),
      cancelText: this.t('dialog.stay'),
      discardText: this.t('dialog.discardAndReload'),
      discardDanger: true
    }, '400px').then((res) => {
      if (res === true) {
        // 保存完再刷新 —— 否则写库请求会被刷新打断
        this.tree.doSaveTree(() => window.location.reload());
      } else if (res === 'discard') {
        this.tree.cancelTreeEdit();
        window.location.reload();
      }
      // 'stay' → 什么都不做
    });
  }

  /**
   * ★ 2026-09-20(Forrest):编辑中还有未保存改动时,刷新/关闭浏览器要拦一下。
   * 浏览器的 beforeunload 只允许"重新加载 / 取消"两个按钮(全站通用规范,
   * 不允许自定义文案 —— 这是浏览器安全限制),语义上等价于 discard / stay。
   */
  @HostListener('window:beforeunload', ['$event'])
  guardUnsavedTree(ev: BeforeUnloadEvent): void {
    if (this.tree.treeEditing() && this.tree.treeDirty()) {
      ev.preventDefault();
      ev.returnValue = '';   // Chrome/Edge/Safari 都需要这个才弹确认
    }
  }

  ngOnInit(): void {    // ★ 2026-09-20(Forrest BUG1 修复):进页面即校准"Azure 是否就绪"。
    //   本地标记为 false 时以服务端 speech/settings 的 hasKey 为准 ——
    //   否则播放功能会被一个从未写入的 localStorage 标记永久挡死。
    this.grading.syncAzureReadyFromServer();
    // 素材树改为从后端加载(2026-09-15 第十七轮)。
    // 2026-09-15 第十七轮:持久化迁到后端 —— 必须先等数据回来再选素材,
    // 否则 findFile 在空树上永远找不到,会错误地跳到第一个素材。
    // 恢复选中项的逻辑移到 afterTreeReady(),由加载回调触发。
    this.tree.restoreTree();
    // ★ 打通:从 Playbook 带题目过来 (?q=...)。用订阅而不用 snapshot,
    //   因为 Angular 会复用组件 —— 已在 AI Practice 页时再跳一次,
    //   ngOnInit 不重跑,snapshot 就读不到新参数。
    this.route.queryParamMap.subscribe(params => {
      const q = params.get('q');
      if (q) this.customQuestion.set(q);
    });
  }

  /** ★ 打通:外部带入的自定义练习题 (如从 Playbook 跳过来)。 */
  readonly customQuestion = signal<string | null>(null);

  /** 从 localStorage 取上次选中的素材 id。 */
  readStoredMaterialId(): string | null {
    try {
      return localStorage.getItem('active_practice_material_id');
    } catch {
      return null;
    }
  }

  /** 记住当前选中的素材 id(localStorage + URL query 同步)。 */
  rememberMaterialId(id: string): void {
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
    this.tts.stopSpeak();
    this.tts.stopPosTimer();
    // TTS 的 objectURL 不回收就是内存泄漏(每次合成一份 MP3 攒在内存里)
    if (this.tts.ttsUrl) {
      URL.revokeObjectURL(this.tts.ttsUrl);
      this.tts.ttsUrl = null;
    }
    this.grading.stopPlayback();
    // ★ 第二十六轮:回收鉴权拉流产生的 objectURL(否则离页泄漏)
    for (const u of this.grading.audioBlobUrls.values()) URL.revokeObjectURL(u);
    this.grading.audioBlobUrls.clear();
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

  selectFile(n: MaterialNode): void {
    this.selectedId.set(n.id);
    // ★ 第二十七轮:记住 id↔名字,便于回读后端树后按名找回选中项
    this.tree.lastKnownName.set(n.id, n.name);
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
    this.grading.activeTakeId.set(null);
    // ★ 第十三轮:标记颜色现在直接读节点字段(markColor),换素材自动跟随,无需回读
    // ★ 2026-09-20(Forrest):换素材 → 销毁旧音频(stop + unload + 释放 URL),
    //   由统一的 destroyTtsAudio() 处理,避免各调用点漏掉某一项。
    this.tts.destroyTtsAudio();
    void this.tts.showCachedDuration(n.id, text);
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
    // From Tech:从虚拟树找节点
    const searchTree = id.startsWith('ts:') ? this.tree.fromTechNodes() : this.tree.nodes();
    const target = this.tree.findFile(searchTree, id);
    const text = this.draft();
    if (target) target.content = text;
    if (id.startsWith('ts:')) {
      this.tree.fromTechNodes.set([...this.tree.fromTechNodes()]);
    } else {
      this.tree.nodes.set([...this.tree.nodes()]);
    }
    this.saved.set(text);
    this.editing.set(false);
    // 内容变了,旧评分作废 —— 参考文本变了,旧分数不再对应这份文本
    const t = this.grading.activeTake();
    if (t) this.recorder.clearScore(t.id);

    // From Tech:内容直接写回 Knowledge,不走 Practice API
    if (id.startsWith('ts:item:')) {
      const kid = (target as any)?._kid as string;
      if (kid) {
        this.knowledgeApi.updateBetterAnswer<void>(kid, {
          betterAnswer: text || null,
          version: (target as any)?._version ?? null // 票101:树节点读时 xmin
        }).subscribe({
          next: () => this.toast(this.t('dialog.saveDone')),
          error: (e) => this.toast(this.tree.errText(e))
        });
      }
      // ★ 2026-09-20(Forrest 第三十六轮):正文变了 → 示范朗读音频必须作废重建。
      this.tts.destroyTtsAudio();
      this.tts.rebuildTts();
      return;
    }

    // ★ 关键修复:真正落到数据库。
    //   persistQuiet 内部会标记 treeDirty、失败回置并在成功后回读对齐 GUID,
    //   与拖拽/改名走的是同一条路径 —— 不再有"只有正文不落库"的特例。
    this.tree.treeDirty.set(true);
    this.tree.persistQuiet();

    // ★ 2026-09-20(Forrest 第三十六轮):正文变了 → 示范朗读音频必须作废重建。
    //   否则播放的还是旧文本那份录音(听起来就是"改了文本,读的还是老的")。
    this.tts.destroyTtsAudio();
    this.tts.rebuildTts();
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

  /** 当前应朗读/录音的文本(编辑态用草稿,只读态用已存)。 */
  currentText(): string {
    return (this.editing() ? this.draft() : this.saved()) || '';
  }

  constructor() {
    this.grading.audio.addEventListener('timeupdate', () => {
      this.grading.playPos.set(this.grading.audio.currentTime);
    });
    this.grading.audio.addEventListener('ended', () => {
      this.grading.playingId.set(null);
      this.grading.playPos.set(0);
    });
    this.grading.audio.addEventListener('pause', () => {
      // 播完自然暂停也会触发这里 —— 用 ended 已处理归零,这里只清高亮
      if (this.grading.audio.ended) return;
      this.grading.playingId.set(null);
    });

    // 监听录音列表变化:上传成功后把"当前作品"从本地临时 id 接到后端 GUID。
    // 不接的后果:activeTake() 找不到人 → 评分/回放全部无反应。
    effect(() => {
      this.recorder.recordings();   // 依赖
      this.grading.syncPendingIdRemap();
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
      const id = this.grading.waveTargetId();
      const take = this.grading.activeTake() ?? this.recorder.pendingTake();
      if (!id || !take) {
        queueMicrotask(() => this.grading.waveBars.set([]));
        return;
      }
      const cached = this.grading.waveCache.get(id);
      if (cached) {
        queueMicrotask(() => this.grading.waveBars.set(cached));
        return;
      }
      queueMicrotask(() => this.grading.waveBars.set([]));
      void (async () => {
        const src = await this.grading.waveAudioSource(take);
        if (!src || src.size === 0) return;
        const bars = await this.grading.computeWaveBars(src);
        if (!bars) return;
        this.grading.waveCache.set(id, bars);
        // 只有当前目标仍是这条录音时才写入(避免异步竞态写错人的波形)
        if (this.grading.waveTargetId() === id) this.grading.waveBars.set(bars);
      })();
    });
  }
  takeNo(id: string): number { return this.grading.takeNo(id); }
  startResize(ev: MouseEvent): void { return this.tree.startResize(ev); }
  fixLang(): void { return this.tts.fixLang(); }
  saveTree(): void { return this.tree.saveTree(); }
  startTreeEdit(): void { return this.tree.startTreeEdit(); }
  cancelTreeEdit(): void { return this.tree.cancelTreeEdit(); }
  onCategoryFilterChange(value: string): void { return this.tree.onCategoryFilterChange(value); }
  openManage(tab: 'categories' | 'export' = 'export'): void { return this.tree.openManage(tab); }
  reloadTree(): void { return this.tree.reloadTree(); }
  phonetic(_word: string): string | null { return this.grading.phonetic(_word); }
  markDot(): string { return this.tree.markDot(); }
  setMarkColor(c: MarkColor): void { return this.tree.setMarkColor(c); }
  pickWord(i: number): void { return this.grading.pickWord(i); }
  isFiller(word: string): boolean { return this.grading.isFiller(word); }
  replayAt(offsetMs: number): void { return this.grading.replayAt(offsetMs); }
  addToReview(): Promise<void> { return this.grading.addToReview(); }
  generateFollowUp(): Promise<void> { return this.grading.generateFollowUp(); }
  onTreeChange(next: MaterialNode[]): void { return this.tree.onTreeChange(next); }
  onFolderToggle(node: MaterialNode): void { return this.tree.onFolderToggle(node); }
  onNodeCreated(_node: MaterialNode): void { return this.tree.onNodeCreated(_node); }
  onDelete(n: MaterialNode): void { return this.tree.onDelete(n); }
  speak(): void { return this.tts.speak(); }
  startRecording(): Promise<void> { return this.grading.startRecording(); }
  stopRecording(): void { return this.grading.stopRecording(); }
  previewPending(): void { return this.grading.previewPending(); }
  submitPending(): void { return this.grading.submitPending(); }
  discardPending(): void { return this.grading.discardPending(); }
  removeRecording(id: string): void { return this.grading.removeRecording(id); }
  gradeRecording(id: string): Promise<void> { return this.grading.gradeRecording(id); }
  speakProgress(): number { return this.tts.speakProgress(); }
  setRate(r: number): void { return this.tts.setRate(r); }
  speakTimeText(): string { return this.tts.speakTimeText(); }
  recognitionPct(sc: RecordingScore): number { return this.grading.recognitionPct(sc); }
  errorClass(errorType: string): string { return this.grading.errorClass(errorType); }
  errLabel(errorType: string): string { return this.grading.errLabel(errorType); }
  hasWordScore(w: { accuracy?: number; errorType?: string }): boolean { return this.grading.hasWordScore(w); }
  fmtDuration(sec: number): string { return this.grading.fmtDuration(sec); }
  fmtTime(ts: number): string { return this.grading.fmtTime(ts); }
  toggleReport(): void { return this.grading.toggleReport(); }
  selectTake(id: string): void { return this.grading.selectTake(id); }
  toggleTakeReport(id: string): void { return this.grading.toggleTakeReport(id); }
  isTakeReportOpen(id: string): boolean { return this.grading.isTakeReportOpen(id); }
  isPlaying(id: string): boolean { return this.grading.isPlaying(id); }
  togglePlay(r: Recording): void { return this.grading.togglePlay(r); }
  wavePlayed(index: number): boolean { return this.grading.wavePlayed(index); }
  progress(r: Recording): number { return this.grading.progress(r); }
  playingTime(r: Recording): string { return this.grading.playingTime(r); }
  seek(r: Recording, ev: MouseEvent): void { return this.grading.seek(r, ev); }
  verdict(v: number): string { return this.grading.verdict(v); }
  scoreClass(v: number): string { return this.grading.scoreClass(v); }
}
export { AZURE_READY_KEY } from './practice-grading-session';
