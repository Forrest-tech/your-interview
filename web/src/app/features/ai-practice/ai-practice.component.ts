import { ChangeDetectorRef, Component, HostListener, OnDestroy, OnInit, TemplateRef, ViewChild, ViewContainerRef, computed, effect, inject, signal } from '@angular/core';
import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { TemplatePortal } from '@angular/cdk/portal';
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
import { AuthService } from '../../core/auth/auth.service';
import { Recording, RecordingScore, RecorderService } from '../../core/recorder/recorder.service';
import { PracticeMaterialTree } from './practice-material-tree';
import { PracticeTtsPlayer } from './practice-tts-player';
import { PracticeGradingSession } from './practice-grading-session';
import { clampFocusIndex, hasSeekableFiller, wordClickAction } from './word-click.helpers';
import { PracticeDictionary } from './practice-dictionary';
import { DictionaryCardComponent } from './dictionary-card.component';
import { dictPopupGeometry } from './dictionary.helpers';
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
    MaterialTreeComponent, DictionaryCardComponent
  ],
  templateUrl: './ai-practice.component.html',
  styleUrl: './ai-practice.component.scss'
})
export class AiPracticeComponent implements OnInit, OnDestroy {
  readonly tree = new PracticeMaterialTree(this);
  readonly tts = new PracticeTtsPlayer(this);
  readonly grading = new PracticeGradingSession(this);  readonly recError = this.grading.recError;
  /** 票119 篇章点词词典(状态机/发音链全在委托类)。 */
  readonly dict = new PracticeDictionary(this);

  // ---------- 票126 词典卡 popup 浮层宿主(§6:CDK Overlay,锚定触发词) ----------
  private readonly overlay = inject(Overlay);
  private readonly vcr = inject(ViewContainerRef);
  @ViewChild('dictPopupTpl') private dictPopupTpl?: TemplateRef<unknown>;
  private dictOverlayRef: OverlayRef | null = null;
  /** 当前浮层是否为桌面锚定模式(移动 sheet 不走 §6.2 高度钳制,其 60vh 上限归卡 CSS 媒体查询)。 */
  private dictPopupAnchored = false;

  /** 开浮层:锚定触发词跨度;已开时先拆旧层再按新锚点重挂(点另一词 = 内容切换 + 重新锚定,§6.7)。 */
  openDictPopup(anchor: HTMLElement): void {
    if (!this.dictPopupTpl) return;
    this.closeDictPopup();
    this.dictPopupAnchored = window.innerWidth >= 640;
    const ref = window.innerWidth < 640
      ? this.overlay.create({
          // §6.5 移动端:底部 sheet(左右/底部各 8px),锚定/翻转规则不适用
          // 注:global 同时给 left+right 时 CDK 只落一边 margin(E7 活体实测 left 丢失);
          // 改以 left+bottom 定锚,右边距由面板宽 calc(100vw - 16px)(styles.scss)保证。
          positionStrategy: this.overlay.position().global().left('8px').bottom('8px'),
          scrollStrategy: this.overlay.scrollStrategies.noop(),
          panelClass: 'dict-popup-panel',
        })
      : this.overlay.create({
          positionStrategy: this.overlay.position()
            .flexibleConnectedTo(anchor)
            .withPositions([
              // 首选:锚点下方、左缘对齐、间距 8px(§6.2)
              { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 8 },
              // 下方不足 → 翻转至上方
              { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -8 },
              // 右侧溢出 → 右对齐兜底(及其翻转)
              { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 8 },
              { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -8 },
            ])
            .withViewportMargin(8)
            .withPush(false)
            // flexibleDimensions 必须关:开着时 CDK 优先「缩面板」而非走位置回退,
            // 而卡高由卡自身 max-height 决定、面板缩不动 → 翻转永不触发、卡溢出视口
            // (E2 活体实测)。关后四位序回退按序生效;卡自身 max-height+内滚兜底封顶。
            .withFlexibleDimensions(false),
          scrollStrategy: this.overlay.scrollStrategies.noop(),
          panelClass: 'dict-popup-panel',
        });
    this.dictOverlayRef = ref;
    ref.attach(new TemplatePortal(this.dictPopupTpl, this.vcr));
    document.addEventListener('scroll', this.dictScrollHandler, true);
    // 骨架高度下先行一轮钳制(内容落定后 refreshDictPopupPosition 会按真实高度再钳)。
    requestAnimationFrame(() => this.applyDictPopupClamp());
  }

  /**
   * 按卡的真实高度让 CDK 重选浮层位序:开卡瞬间 CDK 量到的是骨架(loading)高度,
   * 内容落定/折叠切换后必须重选,否则下方空间不足时翻转永不触发、卡溢出视口(E2 实测)。
   * rAF 一帧:等信号驱动的渲染落定后再量高;重选后随即按 §6.2 末条钳高(F-126-R1)。
   */
  refreshDictPopupPosition(): void {
    requestAnimationFrame(() => {
      this.dictOverlayRef?.updatePosition();
      this.applyDictPopupClamp();
    });
  }

  /**
   * §6.2 末条高度钳制(复测缺陷 F-126-R1):CDK 只负责选侧与 8px 间距,卡高原本只受卡自身
   * max-height:70vh 约束——视口矮且所点词上下空间皆不足时(如 560px 视口、词顶 308、
   * 上 300/下 234 皆 < 392),翻转落位正确但整卡仍 392 高,卡顶被推到 −92px,
   * 词头/音标/Close/播放钮溢出视口上方不可达。
   * 本方法在每次落位后收尾:以面板与锚点的实测相对位置定侧(不猜 CDK 的选择),
   * 用 dictPopupGeometry 按该侧可用空间算钳制值,低于 70vh 时落到卡的 inline max-height
   * (卡自带 overflow-y:auto,钳后即卡内滚动),再 updatePosition 以钳后高度重新锚定
   * (上方落位时把卡顶拉回视口内)。空间充足时清掉 inline 值,回归卡 CSS 的 70vh 口径。
   * 移动 sheet(<640)不走本钳制(§6.5:60vh + bottom 8px 已保证完整在视口内)。
   */
  private applyDictPopupClamp(): void {
    const ref = this.dictOverlayRef;
    if (!ref || !this.dictPopupAnchored) return;
    const trigger = this.dict.triggerElement;
    if (!trigger) return;
    const card = ref.overlayElement.querySelector<HTMLElement>('.dict-card');
    if (!card) return;
    const anchor = trigger.getBoundingClientRect();
    const pane = ref.overlayElement.getBoundingClientRect();
    // 面板整体在锚点上方(底缘不低于词顶)即上方落位,其余按下方落位(含右对齐兜底位序)。
    const side: 'above' | 'below' = pane.bottom <= anchor.top + 1 ? 'above' : 'below';
    const geo = dictPopupGeometry({
      viewportHeight: window.innerHeight,
      anchorTop: anchor.top,
      anchorBottom: anchor.bottom,
      // scrollHeight = 未受 max-height 钳制的内容全高(卡 overflow-y:auto,量得到全高)。
      naturalHeight: card.scrollHeight,
      side,
    });
    const next = geo.clamped ? `${geo.maxHeight}px` : '';
    if (card.style.maxHeight !== next) {
      card.style.maxHeight = next;
      ref.updatePosition();
    }
  }

  /** 关浮层(幂等):PracticeDictionary 的 close/reset 路径与本组件卸载都经此收口。 */
  closeDictPopup(): void {
    document.removeEventListener('scroll', this.dictScrollHandler, true);
    // 钳制 inline 值随浮层一并清(卡经模板视图复用,不清会把上一词的钳高带进下一次开卡的首帧)。
    const card = this.dictOverlayRef?.overlayElement.querySelector<HTMLElement>('.dict-card');
    if (card) card.style.maxHeight = '';
    this.dictOverlayRef?.dispose();
    this.dictOverlayRef = null;
    this.dictPopupAnchored = false;
  }

  /**
   * §6.6 滚动跟随:页面/篇章滚动时浮层重定位保持与锚点的相对关系;
   * 触发词完全滚出视口 → 被动关闭(焦点处置走 dict.closeFromScroll 的 preventScroll 路径,M-5)。
   */
  private readonly dictScrollHandler = (): void => {
    const ref = this.dictOverlayRef;
    if (!ref) return;
    const trigger = this.dict.triggerElement;
    if (!trigger) return;
    const r = trigger.getBoundingClientRect();
    if (r.bottom < 0 || r.top > window.innerHeight) this.dict.closeFromScroll();
    else {
      ref.updatePosition();
      // 滚动改锚点位置 → 所选侧可用空间随之变,钳高同步重算(F-126-R1)。
      this.applyDictPopupClamp();
    }
  };
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
  /** ★ 票124:回收站顶层项数(入口徽标的计数)。 */
  readonly trashCount = this.tree.trashCount;
  readonly treeLoading = this.tree.treeLoading;
  readonly treeLoadFailed = this.tree.treeLoadFailed;
  /** ★ ML-FIX-UI(W-UI-1):保存失败原因直通树横幅(空 = 无失败)。 */
  readonly treeSaveError = this.tree.treeError;
  /** ★ ML-FIX-UI(W-UI-2):素材树编辑门禁 —— 与后端 SaveMaterials 的 mock.manage 同源。 */
  readonly canEditTree = computed(() => this.auth.can('mock.manage'));
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
  readonly waveStatus = this.grading.waveStatus;
  readonly waveFailReason = this.grading.waveFailReason;
  readonly waveAvgHz = this.grading.waveAvgHz;

  readonly i18n = inject(I18nService);
  readonly auth = inject(AuthService);
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
    this.router.navigateByUrl('/settings/ai');
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
    document.removeEventListener('scroll', this.dictScrollHandler, true);
    this.closeDictPopup();
    this.dict.destroy();
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

    // 票119:换素材即关词典卡并清已查标记(词卡状态属于具体篇章,不跨素材残留)。
    effect(() => {
      this.selectedId();   // 依赖
      queueMicrotask(() => this.dict.reset());
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
    // ★ 票137 R-137-1/R-137-2：effect 同时驱动 waveStatus 状态机 ——
    //   loading 只露节头 + 进度条（不渲染 .wave-strip，不闪空条）；
    //   ready 写柱 + Hz；failed 写分级原因（404 → gone，其余 → unavailable）。
    //   竞态守卫：异步完成时目标仍是这条才写入（含失败写入——迟到的旧失败
    //   不许覆盖新目标的 loading 态）；waveBars/waveCache 语义一字不动。
    effect(() => {
      const id = this.grading.waveTargetId();
      const take = this.grading.activeTake() ?? this.recorder.pendingTake();
      if (!id || !take) {
        queueMicrotask(() => {
          this.grading.waveBars.set([]);
          this.grading.waveAvgHz.set(null);
          this.grading.waveFailReason.set(null);
          this.grading.waveStatus.set('idle');
        });
        return;
      }
      const cached = this.grading.waveCache.get(id);
      if (cached) {
        queueMicrotask(() => {
          this.grading.waveBars.set(cached.bars);
          this.grading.waveAvgHz.set(cached.avgHz);
          this.grading.waveFailReason.set(null);
          this.grading.waveStatus.set('ready');
        });
        return;
      }
      queueMicrotask(() => {
        this.grading.waveBars.set([]);
        this.grading.waveAvgHz.set(null);
        this.grading.waveFailReason.set(null);
        this.grading.waveStatus.set('loading');
      });
      void (async () => {
        const src = await this.grading.waveAudioSourceDetailed(take);
        if (this.grading.waveTargetId() !== id) return;
        if (src.kind !== 'blob' || src.blob.size === 0) {
          this.grading.waveFailReason.set(src.kind === 'gone' ? 'gone' : 'unavailable');
          this.grading.waveStatus.set('failed');
          return;
        }
        const res = await this.grading.computeWaveBars(src.blob);
        // 只有当前目标仍是这条录音时才写入(避免异步竞态写错人的波形)
        if (this.grading.waveTargetId() !== id) return;
        if (!res) {
          this.grading.waveFailReason.set('unavailable');
          this.grading.waveStatus.set('failed');
          return;
        }
        this.grading.waveCache.set(id, res);
        this.grading.waveBars.set(res.bars);
        this.grading.waveAvgHz.set(res.avgHz);
        this.grading.waveFailReason.set(null);
        this.grading.waveStatus.set('ready');
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
  /** ★ 票124:打开素材回收站(入口按钮 / 空态指针行都走这条通道)。 */
  openTrash(): void { return this.tree.openTrash(); }
  reloadTree(): void { return this.tree.reloadTree(); }
  phonetic(_word: string): string | null { return this.grading.phonetic(_word); }
  markDot(): string { return this.tree.markDot(); }
  setMarkColor(c: MarkColor): void { return this.tree.setMarkColor(c); }
  pickWord(i: number): void { return this.grading.pickWord(i); }
  /**
   * 填充词判定一律转调 session(FILLERS 的唯一事实源)。
   * ★ 票120:写成绑定箭头属性 —— 模板要把本判定**作值**传给 helpers 纯函数
   * (hasSeekableFiller 的谓词注入接缝),方法形态作参数传递会丢 this。
   * 模板与 TS 内的调用形态(isFiller(w))不变。
   */
  readonly isFiller = (word: string): boolean => this.grading.isFiller(word);
  replayAt(offsetMs: number): void { return this.grading.replayAt(offsetMs); }

  /** ★ 票120:模板直接调用的 helpers 纯函数(点击分发 / 可回放填充词判定),组件只作暴露,不另实现。 */
  readonly wordClickAction = wordClickAction;
  readonly hasSeekableFiller = hasSeekableFiller;

  /** ★ 票120:逐词 roving tabindex 的焦点索引 —— 只存焦点位置,不碰评分状态机。 */
  readonly recogFocusIdx = signal(0);

  /** 当前 tab 停靠点下标 = 存储索引按现行词数钳制后的值(规则③:重渲染后恰一个停靠点且落在界内)。 */
  recogTabStop(wordCount: number): number { return clampFocusIndex(this.recogFocusIdx(), wordCount); }

  /**
   * ★ 票120:逐词键盘处理 —— roving tabindex。
   * ←/→/Home/End 在词间移动焦点(钳制不回绕:线性文本不是环,规则①);
   * Enter/Space 触发与点击**完全相同**的分发(wordClickAction → replayAt/pickWord)。
   * 焦点移动只改 recogFocusIdx 并对目标 span 调 focus(),不碰评分状态。
   */
  onWordKeydown(ev: KeyboardEvent, index: number, wordCount: number): void {
    if (ev.key === 'Enter' || ev.key === ' ') {
      ev.preventDefault();
      const w = this.activeTake()?.score?.words?.[index];
      if (!w) return;
      if (wordClickAction(this.isFiller(w.word), w.offsetMs) === 'replay') this.replayAt(w.offsetMs!);
      else this.pickWord(index);
      return;
    }
    let target: number | null = null;
    if (ev.key === 'ArrowLeft') target = clampFocusIndex(index - 1, wordCount);
    else if (ev.key === 'ArrowRight') target = clampFocusIndex(index + 1, wordCount);
    else if (ev.key === 'Home') target = clampFocusIndex(0, wordCount);
    else if (ev.key === 'End') target = clampFocusIndex(wordCount - 1, wordCount);
    if (target === null) return;
    ev.preventDefault();
    this.recogFocusIdx.set(target);
    const spans = (ev.currentTarget as HTMLElement).parentElement?.children;
    (spans?.[target] as HTMLElement | undefined)?.focus();
  }
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
  selectTake(id: string): void {
    const prev = this.activeTakeId();
    this.grading.selectTake(id);
    this.closeWordPopOnSwitch(prev);
  }
  toggleTakeReport(id: string): void {
    // ★ 票121:唯一 TS 补丁(焦点归位) —— 行内 Collapse 钮收起了本条报告:同上归位。
    // 切换到另一条(本条本就没开)不做任何程序化焦点移动;滚动一概不动。
    const btn = this.collapseFocusTarget();
    const wasOpen = this.isTakeReportOpen(id);
    const prev = this.activeTakeId();
    this.grading.toggleTakeReport(id);
    this.closeWordPopOnSwitch(prev);
    if (wasOpen && !this.isTakeReportOpen(id) && btn) this.refocusAfterCollapse(btn);
  }
  /**
   * ★ 票120 规则②(ii):切换 Take 后 word-pop 必须关闭 —— 会话的 focusTake 是
   * 「把该条设为当前作品并收起弹层」的指定操作,但现行接线里它没有调用点,
   * selectTake/toggleTakeReport 本身都不收弹层。故在组件包装层于**真正发生
   * 切换**时补调一次 focusTake 对齐弹层状态(activeTakeId 已由前一步设为
   * 新值,focusTake 内的同值重设是幂等空转,只有 pickedWord 归零生效)。
   * 同条收起、重复点选已选行(prev === now)不触发,不改既有语义。
   */
  private closeWordPopOnSwitch(prevId: string | null): void {
    const now = this.activeTakeId();
    if (now && now !== prevId) this.grading.focusTake(now);
  }
  /**
   * ★ 票121:收起动作执行"前"记录目标按钮 —— 行内 Collapse 钮即当前焦点。
   * 票125:报告头部 toggle 钮随 .rep-title 整块删除,其专属的 section 分支成死代码,已清;
   * .hi-view 分支与 refocusAfterCollapse() 逐字保全。
   * 只做焦点归位,不做任何程序化滚动,不碰状态机语义。
   */
  private collapseFocusTarget(): HTMLButtonElement | null {
    const anchor = document.activeElement as HTMLElement | null;
    if (!anchor) return null;
    if (anchor.classList.contains('hi-view')) return anchor as HTMLButtonElement;
    return null;
  }
  /**
   * ★ 票125:行聚焦时 Space 与 Enter 同效（行点击 ≡ hi-view 钮语义）。
   * 焦点在行内元素上时不干预其自身按键行为（避免 Space 在按钮上双触发）；
   * keydown 阶段 preventDefault，防页面滚动。
   * 注：模板 (keydown.space) 的事件在 Angular 类型检查里是 Event（非 KeyboardEvent），
   * 故此处收 Event、内部收窄为 KeyboardEvent 再取 target/currentTarget。
   */
  onRowSpaceKey(ev: Event, id: string): void {
    const ke = ev as KeyboardEvent;
    if (ke.target !== ke.currentTarget) return;
    ke.preventDefault();
    this.toggleTakeReport(id);
  }
  /** ★ 票121:报告子树销毁"后"把焦点送回目标按钮(setTimeout 等 Angular 本轮变更检测完成)。 */
  private refocusAfterCollapse(btn: HTMLButtonElement): void {
    setTimeout(() => { if (btn.isConnected) btn.focus(); }, 0);
  }
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
