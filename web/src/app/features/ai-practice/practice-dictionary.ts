import { computed, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Howl } from 'howler';
import type { AiPracticeComponent } from './ai-practice.component';
import {
  DictAccent, DictCardState, PassageToken, isRovingKey, lookupKeyOf,
  nextRovingIndex, tokenizePassage,
} from './dictionary.helpers';
import type { DictPlayRequest } from './dictionary-card.component';

/**
 * 票119 篇章点词词典(宿主委托类,同 PracticeTtsPlayer 例):
 * 篇章 token 化、查词状态机、roving tabindex、发音链播放入口都在这里,
 * 宿主组件只挂模板与一行 effect(换素材即 reset)。
 *
 * 发音一律先走后端 pronounce 端点解析链(卡内已知 key 与端点结果同源;
 * neural 档的 key 只能由端点合成后给出),拿到 key 再取流播放;
 * 取流失败或链落到 device → speechSynthesis 朗读(SPEC §2.1 Tier 3,
 * 仅词典卡范围,不涉及篇章示范朗读)。
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
  /** 已查过的词键(本地状态:词面小写键 + 词元,渲染浅色点线)。 */
  readonly lookedUp = signal<ReadonlySet<string>>(new Set());

  private triggerEl: HTMLElement | null = null;
  private requestSeq = 0;
  private howl: Howl | null = null;
  private objectUrl: string | null = null;

  /** 换素材时由宿主 effect 调用:关卡、清已查标记、roving 归首。 */
  reset(): void {
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    this.roving.set(0);
    this.lookedUp.set(new Set());
    this.triggerEl = null;
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
    const seq = ++this.requestSeq;
    try {
      const dto = await firstValueFrom(this.host.practiceApi.lookupDictionaryWord(key));
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key, dto.lemma);
      this.state.set({ status: 'ok', query: key, card: dto });
    } catch (err) {
      if (seq !== this.requestSeq) return;
      this.markLookedUp(key);
      this.state.set(
        err instanceof HttpErrorResponse && err.status === 404
          ? { status: 'notfound', query: key }
          : { status: 'error', query: key },
      );
    }
  }

  /** 关闭词卡;returnFocus 时焦点送回触发词(Esc 路径,SPEC §6.3)。 */
  close(returnFocus: boolean): void {
    this.requestSeq++;
    this.state.set(null);
    this.openWordIndex.set(-1);
    if (returnFocus) this.triggerEl?.focus();
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

  /** 卡内播放请求分发:口音钮 / 词形钮 / 设备朗读钮。 */
  play(req: DictPlayRequest): void {
    const st = this.state();
    if (!st) return;
    if (req.target === 'accent') {
      const word = st.status === 'ok' ? st.card.lemma : st.query;
      void this.pronounceAndPlay(word, req.accent);
    } else if (req.target === 'form') {
      void this.pronounceAndPlay(req.text, req.accent);
    } else {
      void this.pronounceAndPlay(st.query, req.accent);
    }
  }

  /** pronounce 端点解析链 → 有 key 取流播;落 device 或任何失败 → 设备朗读。 */
  private async pronounceAndPlay(word: string, accent: DictAccent): Promise<void> {
    try {
      const dto = await firstValueFrom(
        this.host.practiceApi.pronounceDictionaryWord(word, accent),
      );
      if (dto.key) {
        const played = await this.playKey(dto.key);
        if (played) return;
      }
    } catch {
      /* 端点失败 → 设备朗读兜底 */
    }
    this.speak(word, accent);
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

  /** Tier 3 设备 TTS(仅词典卡发音兜底,SPEC §2.1)。 */
  private speak(text: string, accent: DictAccent): void {
    try {
      const synth = window.speechSynthesis;
      if (!synth) return;
      const utter = new SpeechSynthesisUtterance(text);
      utter.lang = accent === 'uk' ? 'en-GB' : 'en-US';
      synth.cancel();
      synth.speak(utter);
    } catch {
      /* 设备无语音能力时静默(卡面信息仍完整) */
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
