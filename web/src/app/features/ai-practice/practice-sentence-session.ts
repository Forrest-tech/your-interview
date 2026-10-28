import { computed, signal } from '@angular/core';
import type { AiPracticeComponent } from './ai-practice.component';
import {
  extendSelection, restoreSentenceText, SENTENCE_MIN_WORDS, truncateSentenceTip,
  type SentenceRange,
} from './sentence-select.helpers';

/**
 * 票 269：一句话模式会话（独立委托类，仿 PracticeDictionary / PracticeGradingSession 例，
 * 避免组件本体继续膨胀；宿主组件只挂模板与接线）。
 *
 * 状态机（计划 §3 步骤 2、§6）：
 * - 开启模式 → 篇章进入选区捕获态（拖选 S-A + 键盘 Shift+方向键）；若跟读模式正开着 → 先关跟读；
 * - 开启跟读模式 → 票 287 起不再先关一句话模式：跟读开关在一句话模式开着时被禁用，
 *   须先关一句话模式再开跟读（跟读会话 toggleShadowMode 入口守住）；
 * - 关闭模式 / 换素材 / 进入编辑态 → 清空选区、高亮与锚点（已提交/已评分 take 不受影响，
 *   标签与评分依据在 take 行上，不在模式状态上）；
 * - 录音进行中 → 选区锁定（捕获与键盘扩展直接忽略、模式按钮禁用）；
 * - 选区快照：录音开始时锁定（见 captureForRecording 的等价性说明），提交时随 FormData 落库。
 */
export class PracticeSentenceSession {
  constructor(private readonly host: AiPracticeComponent) {}

  /** 模式开关。 */
  readonly sentenceMode = signal(false);
  /** 选区（词序闭区间，起止均为 wordIndex）；null = 无选区。 */
  readonly sentenceRange = signal<SentenceRange | null>(null);
  /** 键盘选区的锚点词序（首次按 Shift 时以当前焦点词为锚点，之后保持不变）。 */
  readonly sentenceAnchor = signal<number | null>(null);
  /**
   * 录音开始时锁定的选区快照（即提交时上传的 sentenceText）。
   * 注意：快照与选区状态解耦 —— 录完后改选区、退出模式、换素材均不影响已锁定的快照。
   * 快照只由三处清除：新录音开始（grading.startRecording 首行）、提交（透传后）、丢弃。
   * 退出模式不清除快照：待提交的一句话 take 在退出模式后提交仍须带上选区。
   */
  readonly recordingSentenceText = signal<string | null>(null);

  /** 选区词数（词序区间长度，天然只计词 token）。 */
  readonly sentenceWordCount = computed(() => {
    const r = this.sentenceRange();
    return r ? r.end - r.start + 1 : 0;
  });

  /** 选区还原文本（token 区间切片 join，与原文断句一致）；无选区为 null。 */
  readonly sentenceText = computed(() => {
    const r = this.sentenceRange();
    return r ? restoreSentenceText(this.host.dict.tokens(), r) : null;
  });

  /** 模式内录音是否放行（模式关闭时恒放行；模式内须 ≥10 词）。 */
  readonly canRecord = computed(() =>
    !this.sentenceMode() || this.sentenceWordCount() >= SENTENCE_MIN_WORDS);

  /** 录音键禁用态（模式内且选区不足 10 词；模板绑定 + startRecording 双重门禁）。 */
  readonly recordDisabled = computed(() =>
    this.sentenceMode() && this.sentenceWordCount() < SENTENCE_MIN_WORDS);

  /** 词是否在选区内（模板高亮绑定）。 */
  inSentenceRange(wi: number): boolean {
    const r = this.sentenceRange();
    return !!r && wi >= r.start && wi <= r.end;
  }

  /**
   * 拖选落定。录音中锁定：直接忽略，不改选区（§6 第 1 条）。
   * 拖选后键盘再扩展时锚点取选区起点（焦点端 = 选区终点），符合编辑器直觉。
   */
  setRange(start: number, end: number): void {
    if (this.host.recorder.recording()) return;
    this.sentenceRange.set({ start, end });
    this.sentenceAnchor.set(start);
  }

  /**
   * 键盘 Shift+方向键扩展。返回新的焦点词序（供调用方聚焦目标 span）。
   * 锚点保持不变；无选区时以当前焦点词为锚点；录音中锁定则原样返回当前词序。
   */
  extendByKey(currentWi: number, key: string): number {
    if (this.host.recorder.recording()) return currentWi;
    const wordCount = this.host.dict.wordCount();
    const anchor = this.sentenceAnchor() ?? currentWi;
    const range = this.sentenceRange();
    const focus = range ? (anchor === range.start ? range.end : range.start) : currentWi;
    const res = extendSelection(anchor, focus, key, wordCount);
    this.sentenceAnchor.set(res.anchor);
    this.sentenceRange.set({ start: res.start, end: res.end });
    return res.focus;
  }

  /** 方向键折叠选区（标准语义）：清选区与锚点，模式本身不动。 */
  clearRange(): void {
    this.sentenceRange.set(null);
    this.sentenceAnchor.set(null);
  }

  /**
   * 退出模式：清选区、高亮与锚点、末尾点击抑制旗标；计数行随选区消失。
   * 已提交/已评分的一句话 take 不受影响（标签与评分依据在 take 行上）。
   * 注意：不清除 recordingSentenceText（待提交 take 的选区身份须保留到提交/丢弃）。
   */
  exitSentenceMode(): void {
    this.clearRange();
    this.host.dict.clearSuppress();
    this.sentenceMode.set(false);
  }

  /**
   * 模式开关。录音进行中（常规/跟读）禁用（模板侧 disabled + 此处双重门禁，
   * 防止录音中途模式翻转破坏“录音内容与选区对应”的不变量）。
   * 互斥（票 287 后为单向）：开一句话模式先关跟读模式；反向不再自动关——
   * 一句话模式开着时跟读开关被禁用，须先关一句话模式再开跟读。
   */
  async toggleSentenceMode(): Promise<void> {
    if (this.host.recorder.recording() || this.host.shadow.shadowRecording()) return;
    if (this.sentenceMode()) {
      this.exitSentenceMode();
      return;
    }
    if (this.host.shadow.shadowMode()) await this.host.shadow.toggleShadowMode();
    this.sentenceMode.set(true);
  }

  /**
   * 录音开始时锁定选区快照。
   *
   * 等价性说明（计划 §3 步骤 3 要求“停止时快照”）：录音开始即锁定选区
   * （setRange/extendByKey 在 recording() 时直接忽略、模式按钮在录音中禁用），
   * 故开始时刻的选区 == 停止时刻的选区，开始快照与停止快照观测等价；
   * 且快照与选区状态解耦，录完后改选区不影响已锁定的快照（§6 第 2 条）。
   * 选择开始快照而非停止回调的原因：MediaRecorder onstop → finalize() 是
   * recorder 内部异步路径，grading 层无法在不改动 recorder 的前提下挂钩子；
   * 开始快照零 recorder 改动，且在锁定不变量下与停止快照严格等价。
   */
  captureForRecording(): void {
    this.recordingSentenceText.set(this.sentenceText());
  }

  /** 清除录音选区快照（新录音开始 / 提交透传后 / 丢弃时调用）。 */
  clearRecordingSnapshot(): void {
    this.recordingSentenceText.set(null);
  }

  /** badge tooltip：选区文本前 N 字（过长截断），全文由模板 title 属性承载。 */
  badgeTipFor(row: { sentenceText?: string | null }): string {
    const tip = truncateSentenceTip(row.sentenceText ?? '');
    return this.host.t('practice.sentence.badgeTip').replace('{text}', tip);
  }
}
