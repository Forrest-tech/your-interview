import { computed, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Howl } from 'howler';
import type { AiPracticeComponent } from './ai-practice.component';
import {
  DictAccent, DictCardState, DictPlayStatus, IDLE_DICT_PLAY_STATUS, PassageToken, errorStatusOf,
  isRovingKey, lookupKeyOf, nextRovingIndex, tokenizePassage,
} from './dictionary.helpers';
import { wordIndexOfSelectionNode } from './sentence-select.helpers';
import { DictionaryShadowSession, DictShadowAction } from './dictionary-shadow';
import type { DictPlayRequest } from './dictionary-card.component';

/** 票130 H3:空壳延迟出示阈值(ms)——落定快于此值不挂浮层、内容直达;慢于此值才挂骨架壳。 */
export const DICT_POPUP_DELAY_MS = 150;

/**
 * 票119 篇章点词词典(宿主委托类,同 PracticeTtsPlayer 例):
 * 篇章 token 化、查词状态机、roving tabindex、发音链播放入口都在这里,
 * 宿主组件只挂模板与一行 effect(换素材即 reset)。
 *
 * 票126 变更:
 * - 卡载体改 popup 浮层(§6):开/关卡时通知宿主把卡挂到锚定触发词的 overlay 上,
 *   篇章文档流内不再有卡节点,开卡零版面位移。
 * - F-1(§4.2):404 判定改读转换后错误的状态码(errorStatusOf),不再 instanceof 硬判。
 * - S-2(§8.1):发音只走 pronounce 端点解析的真人录音链;链耗尽(available=false)
 *   即无发音,不再有设备朗读兜底(词典代码内浏览器朗读 API 调用数 = 0)。
 */
export class PracticeDictionary {
  constructor(private readonly host: AiPracticeComponent) {
    document.addEventListener('click', this.docClickHandler);
    document.addEventListener('keydown', this.docKeydownHandler);
    document.addEventListener('scroll', this.pendingScrollHandler, true);
    // 票 269：末尾点击抑制旗标在下一次 mousedown 即清零（拖选自身的 mouseup
    // 在 mousedown 之后，故不影响拖选落定那次 click 的消费；见 onWordClick）。
    document.addEventListener('mousedown', this.docMousedownHandler);
  }

  /**
   * 票127 跟读会话:目标解析/状态机/评分复用都在 DictionaryShadowSession;
   * 本类只做卡生命周期接线(begin/attach/teardown)与手势口径(§9.4)。
   * 依赖以闭包注入 —— 方法缺失的替身宿主下调用即抛,被会话内 try/catch 归 F-S7,不伤查词主链。
   */
  readonly shadow = new DictionaryShadowSession({
    // 票254 §4.4:跟读走 Shadow 面（真人层 1–6，绝不合成）；会话逻辑本身不动。
    pronounce: (word, accent) => this.host.practiceApi.pronounceDictionaryWord(word, accent, { humanOnly: true }),
    fetchAudio: (key) => this.host.practiceApi.fetchDictionaryAudio(key),
    // ⚠️ 惰性代理:本类在宿主字段初始化序上早于 recorder/practiceApi 的 inject,
    // 构造期直取必为 undefined —— 全部依赖一律闭包延迟到调用时求值(查词主链同例)。
    recorder: {
      startShadowRecording: () => this.host.recorder.startShadowRecording(),
      stopShadowRecording: () => this.host.recorder.stopShadowRecording(),
      cancelShadowRecording: () => this.host.recorder.cancelShadowRecording(),
      assessShadowRecording: (blob, referenceText) =>
        this.host.recorder.assessShadowRecording(blob, referenceText),
    },
  });

  /** 卡内跟读动作回抛入口(模板绑定)。 */
  shadowAction(a: DictShadowAction): void {
    this.shadow.action(a);
  }

  /**
   * 票 269：拖选末尾点击抑制旗标（单次消费）。
   * 浏览器在拖选结束后仍会在终点 span 上触发一次 click；不处理则该 click
   * 经 onWordClick 打开词典卡 —— 拖选一次弹一次卡，S-A 不可用。
   * 置位点：篇章 mouseup/touchend 捕获成功时（见 captureSelectionFromDom）；
   * 消费点：onWordClick 首行（置位则清零并直接 return）；另在下一次 mousedown、
   * 退出模式、换素材（reset）时清零，旗标永不跨操作残留。
   */
  private suppressNextWordClick = false;

  /** 票 269：清零末尾点击抑制旗标（模式退出时由一句话会话调用）。 */
  clearSuppress(): void {
    this.suppressNextWordClick = false;
  }

  /** 票 269：下一次 mousedown 即清零抑制旗标（见构造器注记的时序论证）。 */
  private readonly docMousedownHandler = (): void => {
    this.suppressNextWordClick = false;
  };

  /** 点卡外关闭(SPEC §6.1):词跨度与卡本体内的点击不算「卡外」。 */
  private readonly docClickHandler = (event: MouseEvent): void => {
    if (this.state() === null) return;
    // 票127 §9.4:跟读录音态下点外不关卡(关闭手势被忽略,防手滑点空丢录音)。
    if (this.shadow.isRecording) return;
    const target = event.target as HTMLElement | null;
    if (!target || target.closest('.dict-word, app-dictionary-card')) return;
    this.close(false);
  };

  /**
   * 票130 §6.3:待挂出态下 Esc 取消本次开卡。焦点此时仍在触发词上(浮层未挂、焦点未入卡),
   * 卡根 keydown 够不着,必须在文档层接住;卡已挂出时焦点在卡内,Esc 由卡根处理并
   * stopPropagation,不会与此重复触发。取消后按既有 Esc 收口(焦点留/回触发词)。
   */
  private readonly docKeydownHandler = (event: KeyboardEvent): void => {
    if (event.key !== 'Escape' || !this.pendingMount) return;
    if (this.shadow.isRecording) return;
    this.close(true);
  };

  /**
   * 票130 §6.3:待挂出态无浮层可依宿主滚动跟随——滚动时只记录锚点是否已出视口,
   * 挂出动作(阈值到时/落定直达)发生前以 anchorInView() 复核,出视口则不挂出。
   */
  private readonly pendingScrollHandler = (): void => {
    if (!this.pendingMount || !this.triggerEl) return;
    if (!this.anchorInView()) this.anchorOutOfView = true;
  };

  /** 篇章 token(词/分隔符交替,拼接恒等于原文)。 */
  readonly tokens = computed(() => tokenizePassage(this.host.readonlyText() ?? ''));
  readonly wordCount = computed(() => this.tokens().reduce((n, t) => n + (t.kind === 'word' ? 1 : 0), 0));

  /** 当前词卡状态;null = 未开卡。 */
  readonly state = signal<DictCardState | null>(null);
  /**
   * 票254 §4:卡内播放状态（卡组件 [playStatus] 输入源）。
   * idle = 无进行中/失败播放（playedKind/playedAccent 记录最近一次成功播放，
   * 供合成标签切换）；synthesizing = 回落合成进行中；error = 可见失败 + 可重试。
   */
  readonly playStatus = signal<DictPlayStatus>(IDLE_DICT_PLAY_STATUS);
  /** 最近一次开卡的触发词原文（保大小写；displayForm 用，不作查词键）。 */
  private lastDisplayForm = '';
  /** 播放代际（每次 pronounceAndPlay/开卡/关卡递增；Howl 异步错误据此丢弃过期回调）。 */
  private playSeq = 0;
  /** 开卡对应的词序(触发词);-1 = 未开卡。 */
  readonly openWordIndex = signal(-1);
  /** roving tabindex 停靠的词序(区内仅此词 tabindex=0)。 */
  readonly roving = signal(0);
  /** 已查过的词键(本地状态:词面小写键 + 词元;票126 §7 起无任何视觉呈现,仅作状态钩子)。 */
  readonly lookedUp = signal<ReadonlySet<string>>(new Set());

  private triggerEl: HTMLElement | null = null;
  private requestSeq = 0;
  private howl: Howl | null = null;
  private objectUrl: string | null = null;

  // ---------- 票130 H3 空壳延迟出示状态 ----------
  /** 浮层当前是否已挂出(挂出动作只经 mountPopup 收口,关闭路径统一清)。 */
  private popupMounted = false;
  /** 本次开卡处于延迟窗内(已点词、未挂出):三事件状态机(§6.2)的「待挂出」态。 */
  private pendingMount = false;
  /** 延迟窗计时器(阈值事件);落定/取消先到即作废。 */
  private mountTimer: ReturnType<typeof setTimeout> | null = null;
  /** 待挂出期间锚点曾滚出视口(挂出前复核命中即不挂出,§6.3)。 */
  private anchorOutOfView = false;

  /** 当前触发词元素(宿主 overlay 滚动跟随/出视口判定用)。 */
  get triggerElement(): HTMLElement | null {
    return this.triggerEl;
  }

  /** 换素材时由宿主 effect 调用:关卡、清已查标记、roving 归首。 */
  reset(): void {
    // 票 269：换素材同触发点清零末尾点击抑制旗标（选区属于具体篇章，不跨素材残留）。
    this.suppressNextWordClick = false;
    this.shadow.teardown();
    this.cancelPendingMount();
    this.popupMounted = false;
    this.playSeq++;
    this.playStatus.set(IDLE_DICT_PLAY_STATUS);
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.roving.set(0);
    this.lookedUp.set(new Set());
    this.triggerEl = null;
    this.host.closeDictPopup();
  }

  destroy(): void {
    this.shadow.teardown();
    this.cancelPendingMount();
    document.removeEventListener('click', this.docClickHandler);
    document.removeEventListener('keydown', this.docKeydownHandler);
    document.removeEventListener('scroll', this.pendingScrollHandler, true);
    document.removeEventListener('mousedown', this.docMousedownHandler);
    this.howl?.unload();
    this.howl = null;
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }

  isLookedUp(token: PassageToken): boolean {
    return this.lookedUp().has(lookupKeyOf(token.text));
  }

  /** 点击词:同词再点关闭;点另一词直接切换内容(SPEC §6.1)。 */
  onWordClick(token: PassageToken, event: MouseEvent): void {
    // 票 269：拖选末尾点击抑制 —— 旗标置位则只消费一次（清零并直接 return，
    // 不开卡、不换词）；下一次点击不再受影响（见旗标注记）。
    if (this.suppressNextWordClick) {
      this.suppressNextWordClick = false;
      return;
    }
    // 票127 §9.4:跟读录音态下点任何词都不关不换(关闭/换词手势被忽略)。
    if (this.shadow.isRecording) return;
    if (this.openWordIndex() === token.wordIndex && this.state() !== null) {
      this.close(false);
      return;
    }
    void this.open(token, event.currentTarget as HTMLElement | null);
  }

  async open(
    token: PassageToken,
    el: HTMLElement | null,
    opts?: { keepCurrentCardUntilSettle?: boolean },
  ): Promise<void> {
    const key = lookupKeyOf(token.text);
    const seq = ++this.requestSeq;
    this.playSeq++;
    this.playStatus.set(IDLE_DICT_PLAY_STATUS);
    this.lastDisplayForm = token.text;
    this.triggerEl = el;
    this.openWordIndex.set(token.wordIndex);
    this.roving.set(token.wordIndex);
    // K-2 裁决(评分门微瑕,主线裁):错误卡重试时旧卡保留在席直至新结果落定——
    // 不卸下、不换骨架、不重锚;150ms 延迟窗只管「无卡在席」的全新挂载(与 §6.5 同界)。
    const keepCurrent = !!opts?.keepCurrentCardUntilSettle
      && this.popupMounted
      && this.state()?.status === 'error';
    if (keepCurrent) {
      this.cancelPendingMount();
    } else if (this.popupMounted) {
      // §6.5 已挂出切换:立即重锚、新词 loading 立即可见,不进延迟窗(D7)。
      this.cancelPendingMount();
      this.state.set({ status: 'loading', query: key });
      if (el) this.host.openDictPopup(el);
    } else {
      // §6.2 全新挂载进延迟窗:落定先到直达挂出(事件 1)、150ms 阈值先到挂骨架壳
      // (事件 2)、取消先到永不挂出(事件 3,见 close/reset/代际校验)。
      this.cancelPendingMount();
      this.state.set({ status: 'loading', query: key });
      this.anchorOutOfView = false;
      this.pendingMount = true;
      this.mountTimer = setTimeout(() => {
        this.mountTimer = null;
        if (seq !== this.requestSeq || !this.pendingMount) return;
        this.pendingMount = false;
        if (!this.anchorInView()) {
          // §6.3:锚点已出视口 → 不挂出,按 closeFromScroll 语义收口。
          this.closeFromScroll();
          return;
        }
        this.mountPopup(el);
      }, DICT_POPUP_DELAY_MS);
    }
    // 票127 §5.2:跟读目标解析与卡内容加载并行开跑(自带代际丢弃,不与查词主链互相等待)。
    this.shadow.begin(key);
    try {
      const dto = await firstValueFrom(this.host.practiceApi.lookupDictionaryWord(key));
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key, dto.lemma);
      // 票254 P3:ok 态携带所点词形原文（displayForm），卡词头据此原样显示。
      this.state.set({ status: 'ok', query: key, card: dto, displayForm: this.lastDisplayForm });
      this.shadow.attachCard(dto);
    } catch (err) {
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key);
      // F-1(§4.2):状态判定认转换后错误的状态码;404 → 负卡,其余一切错误 → 错误卡。
      this.state.set(
        errorStatusOf(err) === 404
          ? { status: 'notfound', query: key }
          : { status: 'error', query: key },
      );
      // 负卡/错误卡无跟读区(票127 §5.1)。
      this.shadow.attachNoCard();
    }
    if (seq !== this.requestSeq) return;
    // §6.2 事件 1:终态先于阈值抵达 → 立即以终态挂出,全程无 loading 帧(§6.4 错误同理不等阈值)。
    this.settleMount(el);
    // 内容落定后卡高才稳定:让宿主按真实高度重选浮层位序(开卡瞬间量到的是骨架高,
    // CDK 不会自行翻转 —— E2 活体实测)。
    this.host.refreshDictPopupPosition();
  }

  /** 挂出浮层的唯一收口(挂出后 popupMounted 为真;后续落定在同一浮层内原地换内容)。 */
  private mountPopup(el: HTMLElement | null): void {
    if (!el) return;
    this.host.openDictPopup(el);
    this.popupMounted = true;
  }

  /** 落定收口:待挂出态下终态抵达 → 作废计时器、复核锚点、立即以终态挂出。 */
  private settleMount(el: HTMLElement | null): void {
    if (!this.pendingMount) return;
    this.clearMountTimer();
    this.pendingMount = false;
    if (!this.anchorInView()) {
      this.closeFromScroll();
      return;
    }
    this.mountPopup(el);
  }

  private clearMountTimer(): void {
    if (this.mountTimer !== null) {
      clearTimeout(this.mountTimer);
      this.mountTimer = null;
    }
  }

  /** 取消待挂出(关闭/重置/新开卡接管时):计时器作废、本次开卡永不挂出。 */
  private cancelPendingMount(): void {
    this.clearMountTimer();
    this.pendingMount = false;
  }

  /**
   * 锚点是否仍在视口内(判据与宿主滚动跟随一致:底缘出顶或顶缘出底即出视口)。
   * 元素无几何能力(测试替身)或视口高度不可得时按在视口内处理,不误杀。
   */
  private anchorInView(): boolean {
    if (this.anchorOutOfView) return false;
    const el = this.triggerEl;
    if (!el || typeof el.getBoundingClientRect !== 'function') return true;
    const vh = (globalThis as { innerHeight?: number }).innerHeight;
    if (vh === undefined) return true;
    const r = el.getBoundingClientRect();
    return !(r.bottom < 0 || r.top > vh);
  }

  /** 关闭词卡;returnFocus 时焦点送回触发词(Esc 路径,SPEC §6.3)。 */
  close(returnFocus: boolean): void {
    // 票127 §6.5/§9.4:关卡即 teardown 跟读(录音中经 Close 关闭 = 先停录丢弃再关卡)。
    this.shadow.teardown();
    // 票130 §6.3:待挂出态被关闭 → 计时器作废、永不挂出(取消事件)。
    this.cancelPendingMount();
    this.popupMounted = false;
    this.playSeq++;
    this.playStatus.set(IDLE_DICT_PLAY_STATUS);
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.host.closeDictPopup();
    if (returnFocus) this.triggerEl?.focus();
  }

  /**
   * 滚动被动关闭(票126 §6.6 + M-5):触发词滚出视口时宿主调用。
   * 与 Esc 主动关闭分开:焦点返回必须 preventScroll——不许把用户刚滚走的页面又拉回来。
   */
  closeFromScroll(): void {
    this.shadow.teardown();
    this.cancelPendingMount();
    this.popupMounted = false;
    this.playSeq++;
    this.playStatus.set(IDLE_DICT_PLAY_STATUS);
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.host.closeDictPopup();
    this.triggerEl?.focus({ preventScroll: true });
  }

  /** 错误简卡重试:以当前 query 重新查词(K-2:旧错误卡保留在席直至新结果落定)。 */
  retry(): void {
    const st = this.state();
    if (!st) return;
    // 票254 P3:重试沿用上次所点词形原文（否则小写 query 会丢句首大写等原貌）。
    const token: PassageToken = {
      kind: 'word',
      text: this.lastDisplayForm || st.query,
      wordIndex: this.openWordIndex(),
    };
    void this.open(token, this.triggerEl, { keepCurrentCardUntilSettle: true });
  }

  /** 播放失败重试（卡组件 retryPlayRequested）：以已失败键全集作排除重发 pronounce。
   * 票262：排除重发若链耗尽（available=false），说明排除集已无候选可进——此时
   * Retry 的意义是给瞬时失败的键一次重取机会，而非对着空链再发一次排除请求。
   * 故仅在链耗尽时、无排除重发一次（本轮唯一一次）；仍无候选 → 可见失败。
   * 有候选时行为不变（仍走排除集的前进逻辑），绝不为 Retry 清掉尚有候选的排除集。 */
  retryPlay(): void {
    const ps = this.playStatus();
    if (ps.phase !== 'error' || !ps.word || !ps.accent) return;
    void this.pronounceAndPlay(
      ps.word, ps.accent, ps.targetType ?? 'accent', ps.targetForm ?? undefined, ps.failedKeys, true);
  }

  /**
   * 票 269：篇章拖选落定（S-A，模板 mouseup 绑定）。
   * 仅当选区非折叠且锚点/焦点节点均能解析为词 span 时才落定选区并置位
   * 末尾点击抑制旗标；否则（单击、折叠选区、端点在篇章外）不捕获、不置位，
   * 点词查词典原样进行。捕获后清除原生选区，改上自定义高亮 class。
   * 判定阈值：以“非折叠选区 + 双端点可解析”为唯一判定，不另设像素阈值
   * （像素阈值在触屏与高 DPI 下不稳定；折叠选区 = 单击，已足够区分）。
   */
  onPassageMouseUp(): void {
    this.captureSelectionFromDom();
  }

  /**
   * 票 269：触屏拖选落定（模板 touchend 绑定）。
   * 捕获成功时 preventDefault 阻止合成鼠标序列（mousedown/mouseup/click），
   * 否则合成 click 会经 onWordClick 打开词典；抑制旗标同时置位作后备。
   * （无 HammerJS，touchend 为非被动监听，preventDefault 有效。）
   */
  onPassageTouchEnd(event: TouchEvent): void {
    if (!this.captureSelectionFromDom()) return;
    event.preventDefault();
  }

  /** 拖选捕获本体。返回 true = 本次落定了选区（并置位抑制旗标）。 */
  private captureSelectionFromDom(): boolean {
    const sentence = this.host.sentence;
    if (!sentence?.sentenceMode()) return false;
    // 录音中锁定：选区捕获暂停（计划 §6 第 1 条），录完前改不了选区。
    if (this.host.recorder.recording()) return false;
    const sel = window.getSelection();
    if (!sel || sel.isCollapsed) return false;
    const a = wordIndexOfSelectionNode(sel.anchorNode);
    const f = wordIndexOfSelectionNode(sel.focusNode);
    if (a === null || f === null) return false;
    sentence.setRange(Math.min(a, f), Math.max(a, f));
    this.suppressNextWordClick = true;
    sel.removeAllRanges();
    return true;
  }

  /**
   * 篇章区键盘(SPEC §6.3):事件自词跨度冒泡至此。
   * 方向键/Home/End 移动 roving 并聚焦目标词;Enter/Space 在触发词上开/关卡。
   * 票 269：Shift+方向键 = 键盘选区（须先于 roving 分支：isRovingKey 不区分 Shift，
   * 不先拦截时 Shift+方向键只会移动 roving 焦点，选区永远建不起来）。
   */
  onPassageKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    const wiRaw = target.dataset?.['wi'];
    if (wiRaw === undefined) return;
    const wi = Number(wiRaw);
    const sentence = this.host.sentence;
    if (sentence?.sentenceMode() && event.shiftKey && isRovingKey(event.key)) {
      event.preventDefault();
      // 录音中锁定：键盘扩展同样暂停（与拖选捕获同口径），且不产生原生选区。
      if (this.host.recorder.recording()) return;
      const focus = sentence.extendByKey(wi, event.key);
      const container = event.currentTarget as HTMLElement;
      container
        .querySelector<HTMLElement>(`[data-wi="${focus}"]`)
        ?.focus();
      return;
    }
    if (isRovingKey(event.key)) {
      event.preventDefault();
      const next = nextRovingIndex(wi, event.key, this.wordCount());
      this.roving.set(next);
      const container = event.currentTarget as HTMLElement;
      container
        .querySelector<HTMLElement>(`[data-wi="${next}"]`)
        ?.focus();
      // 票 269：不带 Shift 的方向键折叠选区（标准语义），模式本身不动。
      if (sentence?.sentenceMode()) sentence.clearRange();
      return;
    }
    if (event.key === 'Escape' && sentence?.sentenceMode() && this.state() === null) {
      // 卡已挂出时 Esc 走卡的收口（不冲突）；无卡时 Esc 清选区与锚点。
      sentence.clearRange();
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      // 票127 §9.4:跟读录音态下键盘换词/关卡同点词口径,一律忽略。
      if (this.shadow.isRecording) return;
      event.preventDefault();
      const token = this.tokens().find((t) => t.kind === 'word' && t.wordIndex === wi);
      if (!token) return;
      if (this.openWordIndex() === wi && this.state() !== null) this.close(false);
      else void this.open(token, target);
    }
  }

  // ---------- 发音 ----------

  /** 卡内播放请求分发:口音钮 / 词形钮(票126 后不再有设备朗读钮)。 */
  play(req: DictPlayRequest): void {
    const st = this.state();
    if (!st) return;
    if (req.target === 'accent') {
      // 票254 P3:口音钮播所点词形（query），不再播词元——词元卡上 query 即词元，行为不变。
      void this.pronounceAndPlay(st.query, req.accent, 'accent');
    } else {
      void this.pronounceAndPlay(req.text, req.accent, 'form', req.text);
    }
  }

  /**
   * 票254 §4:pronounce 解析 Main 面 → 有 key 取流播；取流失败 → 一次自动回落
   * （pronounce-with-exclusion，合成键永不可排除，合成键失败不自动回落）；
   * 首次 pronounce 即失败（无失败键可排除）→ 可见 error 态，永不静默。
   * 每次调用至多一次自动回落；手动重试（retryPlay）是新调用，带全集排除重发，
   * 票262：手动重试中排除重发若链耗尽，再无排除重发一次（本轮唯一一次，见下）。
   */
  private async pronounceAndPlay(
    word: string, accent: DictAccent, targetType: 'accent' | 'form',
    targetForm?: string, initialExcludeKeys: string[] = [], clearExclusionsOnExhaustion = false,
  ): Promise<void> {
    const playToken = ++this.playSeq;
    const failedKeys: string[] = [...initialExcludeKeys];
    const base: DictPlayStatus = {
      phase: 'idle', targetType, targetAccent: accent, targetForm: targetForm ?? null,
      word, accent, failedKeys: [...failedKeys], playedKind: null, playedAccent: null,
    };
    this.playStatus.set(base);
    const fail = (): void => {
      if (playToken !== this.playSeq) return;
      this.playStatus.set({ ...base, phase: 'error', failedKeys: [...failedKeys] });
    };
    let dto;
    try {
      dto = await firstValueFrom(
        this.host.practiceApi.pronounceDictionaryWord(
          word, accent, failedKeys.length ? { excludeKeys: [...failedKeys] } : {}),
      );
    } catch {
      fail(); // 首次 pronounce 即失败：无失败键可排除 → 可见失败，永不静默。
      return;
    }
    if (playToken !== this.playSeq) return;
    if ((!dto.available || !dto.key) && clearExclusionsOnExhaustion && failedKeys.length) {
      // 票262：排除重发链耗尽 —— 排除集里已无候选，继续排除只会重复 available=false。
      // 瞬时失败（Wikimedia 限流/超时）的键此时值得一次无排除重取：限流已过则播出，
      // 仍失败则按正常流程走（取流失败 → 自动回落排除该键 → 可见失败），不循环。
      try {
        dto = await firstValueFrom(
          this.host.practiceApi.pronounceDictionaryWord(word, accent, {}));
      } catch {
        fail();
        return;
      }
      if (playToken !== this.playSeq) return;
      failedKeys.length = 0;
    }
    if (!dto.available || !dto.key) {
      fail();
      return;
    }
    if (await this.playKey(dto.key, word, accent, playToken)) {
      if (playToken !== this.playSeq) return;
      this.playStatus.set({
        ...base, phase: 'idle', failedKeys: [...failedKeys],
        playedKind: dto.kind ?? null, playedAccent: accent,
      });
      return;
    }
    // 取流失败 → §4.2 自动回落：合成键失败无回落意义（排除集拦不住合成层），直接可见失败。
    if (playToken !== this.playSeq) return;
    failedKeys.push(dto.key);
    if (dto.kind === 'synthetic') {
      fail();
      return;
    }
    this.playStatus.set({ ...base, phase: 'synthesizing', failedKeys: [...failedKeys] });
    let dto2;
    try {
      dto2 = await firstValueFrom(
        this.host.practiceApi.pronounceDictionaryWord(word, accent, { excludeKeys: [...failedKeys] }),
      );
    } catch {
      fail();
      return;
    }
    if (playToken !== this.playSeq) return;
    if (!dto2.available || !dto2.key) {
      fail();
      return;
    }
    if (await this.playKey(dto2.key, word, accent, playToken)) {
      if (playToken !== this.playSeq) return;
      this.playStatus.set({
        ...base, phase: 'idle', failedKeys: [...failedKeys],
        playedKind: dto2.kind ?? null, playedAccent: accent,
      });
      return;
    }
    failedKeys.push(dto2.key);
    fail();
  }

  private async playKey(key: string, word: string, accent: DictAccent, playToken: number): Promise<boolean> {
    try {
      const blob = await firstValueFrom(
        this.host.practiceApi.fetchDictionaryAudio(key, word, accent));
      if (!blob || blob.size === 0) return false;
      if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
      this.objectUrl = URL.createObjectURL(blob);
      this.howl?.unload();
      const howl = new Howl({ src: [this.objectUrl], format: ['mp3'] });
      // Howl 解码错误是异步的：仅当本次播放仍是当前播放时才翻为可见失败（供手动重试）。
      const markFailed = (): void => {
        if (playToken !== this.playSeq) return;
        const cur = this.playStatus();
        if (cur.word !== word || cur.accent !== accent) return;
        this.playStatus.set({
          ...cur, phase: 'error', failedKeys: [...new Set([...cur.failedKeys, key])],
        });
      };
      howl.on('loaderror', markFailed);
      howl.on('playerror', markFailed);
      this.howl = howl;
      howl.play();
      return true;
    } catch {
      return false;
    }
  }

  private markLookedUp(key: string, lemma?: string): void {
    this.lookedUp.update((set) => {
      const next = new Set(set);
      next.add(key);
      if (lemma) next.add(lemma);
      return next;
    });
  }
}
