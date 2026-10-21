import { Injectable, inject, signal } from '@angular/core';
import { PracticeApi } from '../api/practice-api.service';
import { I18nService } from '../i18n/i18n.service';
import {
  getCachedAudio, putCachedAudio, ttsKey
} from '../../features/ai-practice/tts-audio-cache';

/**
 * 标准答案朗读播放器(2026-10-20 详情页 v2,复用 AI Practice 的 TTS 通道)。
 *
 * 通道:Assessment /api/assessment/tts(Azure 神经语音),音频按
 * text+voice+lang 指纹存 IndexedDB(your-interview-tts),与 AI Practice 共用缓存。
 * 与 AI Practice 保持一致:不回退浏览器 speechSynthesis —— 未配 Azure 时如实报错。
 *
 * 句子高亮:播放时按"已播时长 / 总时长"映射到字符进度,回调当前句序号
 * (Azure 通道不给逐句时间戳,这是时长比例估算,UI 上不宣称为精确对齐)。
 */
@Injectable({ providedIn: 'root' })
export class TtsPlayerService {
  private readonly practiceApi = inject(PracticeApi);
  private readonly i18n = inject(I18nService);

  /** 当前正在播的文本指纹(同一时刻只播一段)。 */
  readonly playingKey = signal<string | null>(null);
  readonly loadingKey = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  private audio: HTMLAudioElement | null = null;
  private objectUrl: string | null = null;
  private raf = 0;

  /** 播放状态回调:(key, 当前句序号|null) —— 停止时句序号为 null。 */
  onSentence: ((key: string, sentenceIndex: number | null) => void) | null = null;

  keyFor(text: string, lang = 'en'): string {
    return ttsKey(text, '', lang);
  }

  /** 把文本切句(高亮与进度映射用)。 */
  static splitSentences(text: string): string[] {
    const parts = text.match(/[^.!?。！？]+[.!?。！？]+["')\]]*\s*|[^.!?。！？]+$/g);
    return (parts ?? [text]).map(s => s.trim()).filter(s => s.length > 0);
  }

  async toggle(text: string, lang = 'en'): Promise<void> {
    const key = this.keyFor(text, lang);
    if (this.playingKey() === key) { this.stop(); return; }
    this.stop();
    this.error.set(null);
    this.loadingKey.set(key);
    try {
      let blob: Blob | null = null;
      const cached = await getCachedAudio(key);
      if (cached) blob = cached.blob;
      if (!blob) {
        const res = await new Promise<Blob>((resolve, reject) => {
          this.practiceApi.synthesize(text, undefined, 1, false, lang).subscribe({
            next: r => resolve(r.blob),
            error: reject
          });
        });
        blob = res.type ? res : new Blob([res], { type: 'audio/mpeg' });
        await putCachedAudio({ key, blob, voice: '', lang, savedAt: Date.now(), text });
      }
      if (this.loadingKey() !== key) return; // 期间被停掉/切换
      this.objectUrl = URL.createObjectURL(blob);
      const audio = new Audio(this.objectUrl);
      this.audio = audio;
      audio.onended = () => this.stop();
      audio.onerror = () => { this.error.set(this.i18n.t('tts.audioFailed')); this.stop(); };
      await audio.play();
      this.loadingKey.set(null);
      this.playingKey.set(key);
      this.trackSentences(key, text);
    } catch {
      this.loadingKey.set(null);
      this.error.set(this.i18n.t('tts.unavailable'));
    }
  }

  private trackSentences(key: string, text: string): void {
    const sentences = TtsPlayerService.splitSentences(text);
    if (sentences.length <= 1) return;
    const totalChars = sentences.reduce((a, s) => a + s.length, 0);
    const bounds: number[] = [];
    let acc = 0;
    for (const s of sentences) { acc += s.length; bounds.push(acc / totalChars); }
    const tick = () => {
      if (this.playingKey() !== key || !this.audio) return;
      const dur = this.audio.duration;
      if (dur && Number.isFinite(dur) && dur > 0) {
        const ratio = this.audio.currentTime / dur;
        let idx = bounds.findIndex(b => ratio <= b);
        if (idx < 0) idx = sentences.length - 1;
        this.onSentence?.(key, idx);
      }
      this.raf = requestAnimationFrame(tick);
    };
    this.raf = requestAnimationFrame(tick);
  }

  stop(): void {
    cancelAnimationFrame(this.raf);
    if (this.audio) {
      const key = this.playingKey();
      try { this.audio.pause(); } catch { /* 已停 */ }
      this.audio = null;
      if (key) this.onSentence?.(key, null);
    }
    if (this.objectUrl) { URL.revokeObjectURL(this.objectUrl); this.objectUrl = null; }
    this.playingKey.set(null);
    this.loadingKey.set(null);
  }
}
