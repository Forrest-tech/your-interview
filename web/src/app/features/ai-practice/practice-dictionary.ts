import { computed, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Howl } from 'howler';
import type { AiPracticeComponent } from './ai-practice.component';
import {
  DictAccent, DictCardState, PassageToken, errorStatusOf, isRovingKey, lookupKeyOf,
  nextRovingIndex, tokenizePassage,
} from './dictionary.helpers';
import type { DictPlayRequest } from './dictionary-card.component';

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
  }

  /** 点卡外关闭(SPEC §6.1):词跨度与卡本体内的点击不算「卡外」。 */
  private readonly docClickHandler = (event: MouseEvent): void => {
    if (this.state() === null) return;
    const target = event.target as HTMLElement | null;
    if (!target || target.closest('.dict-word, app-dictionary-card')) return;
    this.close(false);
  };

  /** 篇章 token(词/分隔符交替,拼接恒等于原文)。 */
  readonly tokens = computed(() => tokenizePassage(this.host.readonlyText() ?? ''));
  readonly wordCount = computed(() => this.tokens().reduce((n, t) => n + (t.kind === 'word' ? 1 : 0), 0));

  /** 当前词卡状态;null = 未开卡。 */
  readonly state = signal<DictCardState | null>(null);
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

  /** 当前触发词元素(宿主 overlay 滚动跟随/出视口判定用)。 */
  get triggerElement(): HTMLElement | null {
    return this.triggerEl;
  }

  /** 换素材时由宿主 effect 调用:关卡、清已查标记、roving 归首。 */
  reset(): void {
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.roving.set(0);
    this.lookedUp.set(new Set());
    this.triggerEl = null;
    this.host.closeDictPopup();
  }

  destroy(): void {
    document.removeEventListener('click', this.docClickHandler);
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
    if (this.openWordIndex() === token.wordIndex && this.state() !== null) {
      this.close(false);
      return;
    }
    void this.open(token, event.currentTarget as HTMLElement | null);
  }

  async open(token: PassageToken, el: HTMLElement | null): Promise<void> {
    const key = lookupKeyOf(token.text);
    this.triggerEl = el;
    this.openWordIndex.set(token.wordIndex);
    this.roving.set(token.wordIndex);
    this.state.set({ status: 'loading', query: key });
    // popup 先行挂出(骨架在浮层内呈现,§6.3);点另一词时宿主把浮层重新锚定到新词。
    if (el) this.host.openDictPopup(el);
    const seq = ++this.requestSeq;
    try {
      const dto = await firstValueFrom(this.host.practiceApi.lookupDictionaryWord(key));
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key, dto.lemma);
      this.state.set({ status: 'ok', query: key, card: dto });
    } catch (err) {
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key);
      // F-1(§4.2):状态判定认转换后错误的状态码;404 → 负卡,其余一切错误 → 错误卡。
      this.state.set(
        errorStatusOf(err) === 404
          ? { status: 'notfound', query: key }
          : { status: 'error', query: key },
      );
    }
    // 内容落定后卡高才稳定:让宿主按真实高度重选浮层位序(开卡瞬间量到的是骨架高,
    // CDK 不会自行翻转 —— E2 活体实测)。
    this.host.refreshDictPopupPosition();
  }

  /** 关闭词卡;returnFocus 时焦点送回触发词(Esc 路径,SPEC §6.3)。 */
  close(returnFocus: boolean): void {
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
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.host.closeDictPopup();
    this.triggerEl?.focus({ preventScroll: true });
  }

  /** 错误简卡重试:以当前 query 重新查词。 */
  retry(): void {
    const st = this.state();
    if (!st) return;
    const token: PassageToken = { kind: 'word', text: st.query, wordIndex: this.openWordIndex() };
    void this.open(token, this.triggerEl);
  }

  /**
   * 篇章区键盘(SPEC §6.3):事件自词跨度冒泡至此。
   * 方向键/Home/End 移动 roving 并聚焦目标词;Enter/Space 在触发词上开/关卡。
   */
  onPassageKeydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    const wiRaw = target.dataset?.['wi'];
    if (wiRaw === undefined) return;
    const wi = Number(wiRaw);
    if (isRovingKey(event.key)) {
      event.preventDefault();
      const next = nextRovingIndex(wi, event.key, this.wordCount());
      this.roving.set(next);
      const container = event.currentTarget as HTMLElement;
      container
        .querySelector<HTMLElement>(`[data-wi="${next}"]`)
        ?.focus();
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
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
      const word = st.status === 'ok' ? st.card.lemma : st.query;
      void this.pronounceAndPlay(word, req.accent);
    } else {
      void this.pronounceAndPlay(req.text, req.accent);
    }
  }

  /**
   * pronounce 端点解析真人录音链 → 有 key 取流播。
   * 票126 §8.1/§8.2(S-2):链耗尽(available=false)或端点失败 → 无发音,就此静默,
   * 绝不回退任何机器发音(设备 TTS 档已整级移除)。
   */
  private async pronounceAndPlay(word: string, accent: DictAccent): Promise<void> {
    try {
      const dto = await firstValueFrom(
        this.host.practiceApi.pronounceDictionaryWord(word, accent),
      );
      if (dto.available && dto.key) await this.playKey(dto.key);
    } catch {
      /* 端点失败 → 无发音(卡面状态文案已说明,不另行兜底) */
    }
  }

  private async playKey(key: string): Promise<boolean> {
    try {
      const blob = await firstValueFrom(this.host.practiceApi.fetchDictionaryAudio(key));
      if (!blob || blob.size === 0) return false;
      if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
      this.objectUrl = URL.createObjectURL(blob);
      this.howl?.unload();
      this.howl = new Howl({ src: [this.objectUrl], format: ['mp3'] });
      this.howl.play();
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
