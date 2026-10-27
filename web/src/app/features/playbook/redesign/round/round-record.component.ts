import {
  Component, DestroyRef, EventEmitter, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewAsset, InterviewDetail, InterviewQuestion, InterviewRound
} from '../../../../core/models/api.models';
import { QaZoomService } from '../../../../core/qa-zoom/qa-zoom.service';
import { TtsPlayerService } from '../../../../core/tts/tts-player.service';
import { PlaybookV2Api } from '../playbook-v2.api';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { isAudioAsset, makeV2Fmt, makeV2T } from '../v2.helpers';
import {
  DimReview, QuestionReview, RoundReview, TranscriptSegment,
  parseQuestionReview, safeJson, serializeQuestionReview
} from '../v2.models';

interface StdEdit { spoken: string; technical: string; }

/**
 * 面试实录(2026-10-20 定稿):
 * 对话原文按段渲染(说话人真名 + 原文,恒标「原文 · 未修改」),有录音时逐段可播、
 * 常驻播放条 + 播放高亮;逐题分析卡四维(切题性/技术深度/表达/发音),
 * 无录音时发音不评分、不猜测并明示;每题两版北美标准回答(口语/技术),
 * 可朗读(当前句高亮)、可编辑(AI 原版与用户版分开存,用户版优先,可还原)。
 * 字号控件只作用于 .qa-text(问题与答案),与 AI Practice 共用同一份偏好。
 */
@Component({
  selector: 'app-round-record',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    <div class="record" [style.--qa-scale]="zoom.scale()">
      <div class="toolbar">
        <span class="tb-title">{{ t('pb.v2.rec.transcriptTitle') }} <span class="orig-tag">{{ t('pb.v2.rec.origTag') }}</span></span>
        <span class="spacer"></span>
        @if (audioAsset()) {
          <button mat-stroked-button (click)="toggleTranscribe()" [disabled]="busy()">
            {{ hasTranscript() ? t('pb.v2.rec.retranscribe') : t('pb.v2.rec.startTranscribe') }}
          </button>
        }
        <!-- 票191：Extract Q&A 直接去掉（先隐藏不删除：开关 + @if，extractQuestions() 仍供自动抽取调用；解析完成自动进 Q&A 页签候选，不再需要手动按钮）。 -->
        @if (!hideExtractQa) {
          <button mat-stroked-button (click)="extractQuestions()" [disabled]="busy() || !segments().length">
            {{ t('pb.v2.rec.extractQa') }}
          </button>
        }
        <!-- 票191：Text size 与逐题分析按钮已上移至父级详情卡 .rc-head 右上角一行，此处不再渲染，避免重复。 -->
      </div>
      @if (notice()) { <p class="notice">{{ notice() }}</p> }
      @if (tts.error()) { <p class="notice warn">{{ tts.error() }}</p> }

      <!-- 说话人改名 -->
      @if (speakerList().length) {
        <div class="rename-row">
          <span class="lbl">{{ t('pb.v2.rec.renameLabel') }}</span>
          <select class="fld sm" [ngModel]="renameFrom()" (ngModelChange)="renameFrom.set($event)">
            @for (s of speakerList(); track s) { <option [value]="s">{{ s }}</option> }
          </select>
          <span>→</span>
          <input class="fld sm" [placeholder]="t('pb.v2.rec.renamePh')" [ngModel]="renameTo()" (ngModelChange)="renameTo.set($event)">
          <button mat-stroked-button (click)="doRename()" [disabled]="!renameTo().trim()">{{ t('pb.v2.rec.rename') }}</button>
        </div>
      }

      <!-- 对话原文 -->
      @if (segments().length) {
        <div class="segments" data-testid="record-segments">
          @for (seg of segments(); track $index) {
            <div class="seg" [class.playing]="currentSeg() === $index">
              <div class="seg-head">
                @if (seg.speaker) { <span class="speaker">{{ seg.speaker }}</span> }
                @if (seg.start != null) {
                  <span class="time">{{ fmtTime(seg.start) }}–{{ fmtTime(seg.end) }}</span>
                  @if (audioAsset()) {
                    <button type="button" class="play-btn" (click)="playSegment($index)"
                            [title]="playMode() === 'segment' ? t('pb.v2.rec.playSegOnly') : t('pb.v2.rec.playFromHere')">▶</button>
                  }
                }
              </div>
              <p class="seg-text qa-text">{{ seg.text }}</p>
            </div>
          }
        </div>
      } @else {
        <p class="empty">{{ t('pb.v2.rec.empty') }}</p>
      }

      <!-- 整轮总览 -->
      @if (roundReview(); as rv) {
        <div class="overview" data-testid="round-overview">
          <div class="ov-scores">
            @if (rv.overall != null) { <div class="ov-big">{{ rv.overall }}<span class="ov-unit">{{ t('pb.v2.rec.overallUnit') }}</span></div> }
            @if (rv.passProbability != null) {
              <div class="ov-pass">{{ fmt('pb.v2.rec.passProb', { n: rv.passProbability }) }}
                <span class="ai-tag">{{ t('pb.v2.rec.aiEstimate') }}</span></div>
            }
          </div>
          @if (rv.passBasis) { <p class="ov-line"><b>{{ t('pb.v2.rec.basis') }}</b>{{ rv.passBasis }}</p> }
          @if (rv.strongest) { <p class="ov-line"><b>{{ t('pb.v2.rec.strongest') }}</b>{{ rv.strongest }}</p> }
          @if (rv.weakest) { <p class="ov-line"><b>{{ t('pb.v2.rec.weakest') }}</b>{{ rv.weakest }}</p> }
          @if (rv.summary) { <p class="ov-line">{{ rv.summary }}</p> }
        </div>
      }

      <!-- 逐题分析 -->
      @for (q of questions(); track q.id) {
        <article class="qcard" data-testid="question-card">
          <div class="q-head">
            <span class="q-idx">Q{{ q.sequence }}</span>
            <span class="q-text qa-text">{{ q.questionText }}</span>
            @if (reviewOf(q)?.overall != null) {
              <span class="q-score">{{ fmt('pb.v2.rec.qScore', { n: reviewOf(q)!.overall! }) }}</span>
            }
          </div>
          @if (q.askedBy) { <p class="q-by">{{ fmt('pb.v2.rec.askedBy', { name: q.askedBy }) }}</p> }

          @if (q.myAnswerText) {
            <div class="my-answer">
              <div class="ma-label">{{ t('pb.v2.rec.myAnswer') }} <span class="orig-tag">{{ t('pb.v2.rec.origTag') }}</span></div>
              <p class="qa-text">{{ q.myAnswerText }}</p>
            </div>
          }

          @if (reviewOf(q); as rv) {
            <div class="dims">
              @for (d of dimList(rv); track d.key) {
                <div class="dim">
                  <div class="dim-head">
                    <span>{{ d.label }}</span>
                    @if (d.review.score != null) {
                      <span class="dim-score">{{ d.review.score }}</span>
                    } @else {
                      <span class="dim-na">{{ t('pb.v2.rec.notScored') }}</span>
                    }
                  </div>
                  @if (d.review.score != null) {
                    <div class="bar"><div class="bar-fill" [style.width.%]="d.review.score"></div></div>
                    @if (d.review.comment) { <p class="dim-c">{{ d.review.comment }}</p> }
                    @if (d.review.fix) { <p class="dim-f"><b>{{ t('pb.v2.rec.fix') }}</b>{{ d.review.fix }}</p> }
                  } @else {
                    <p class="dim-c">{{ d.key === 'pronunciation' ? t('pb.v2.rec.noAudioNote') : t('pb.v2.rec.noComment') }}</p>
                  }
                </div>
              }
            </div>

            <!-- 标准回答:口语版 / 技术版 -->
            @for (std of stdBlocks(q, rv); track std.key) {
              <div class="std-block">
                <div class="std-head">
                  <span class="std-label">{{ std.label }}</span>
                  @if (std.isUser) { <span class="mine-tag">{{ t('pb.v2.p.mineTag') }}</span> }
                  <span class="spacer"></span>
                  @if (editingQ() === q.id + ':' + std.key) {
                    <button mat-stroked-button (click)="saveStd(q, std.key)">{{ t('common.save') }}</button>
                    <button mat-button (click)="editingQ.set(null)">{{ t('common.cancel') }}</button>
                  } @else {
                    <button type="button" class="mini-btn" (click)="playStd(q, std)"
                            [disabled]="!std.text">
                      {{ tts.playingKey() === tts.keyFor(std.text) ? t('pb.v2.p.stop') : t('pb.v2.rec.playStd') }}
                    </button>
                    <button type="button" class="mini-btn" (click)="startEditStd(q, std)">{{ t('pb.v2.p.edit') }}</button>
                    @if (std.isUser) {
                      <button type="button" class="mini-btn" (click)="restoreStd(q, std.key)">{{ t('pb.v2.rec.restoreAi') }}</button>
                    }
                  }
                </div>
                @if (editingQ() === q.id + ':' + std.key) {
                  <textarea class="std-edit qa-text" rows="5"
                            [ngModel]="editDraft()[std.key]"
                            (ngModelChange)="setDraft(std.key, $event)"></textarea>
                } @else if (std.text) {
                  <p class="std-text qa-text">@for (s of sentencesOf(std.text); track $index) {
                    <span [class.sent-on]="isActiveSentence(q, std, $index)">{{ s }} </span>
                  }</p>
                } @else {
                  <p class="empty-sm">{{ t('pb.v2.rec.stdNotGenerated') }}</p>
                }
              </div>
            }
          } @else {
            <p class="empty-sm">{{ t('pb.v2.rec.qNoAnalysis') }}</p>
          }
        </article>
      }

      <!-- 常驻播放条 -->
      @if (audioAsset()) {
        <div class="player-bar">
          <button mat-stroked-button (click)="ensureAudio()">{{ t('pb.v2.rec.loadAudio') }}</button>
          @if (audioUrl()) {
            <audio #player controls [src]="audioUrl()" (timeupdate)="onTimeUpdate(player)"></audio>
            <label class="rate">{{ t('pb.v2.rec.rate') }}
              <select [ngModel]="rate()" (ngModelChange)="setRate(player, $event)">
                <option [value]="0.75">0.75×</option>
                <option [value]="1">1×</option>
                <option [value]="1.25">1.25×</option>
                <option [value]="1.5">1.5×</option>
                <option [value]="2">2×</option>
              </select>
            </label>
            <button type="button" class="mini-btn" (click)="playMode.set(playMode() === 'all' ? 'segment' : 'all')">
              {{ playMode() === 'all' ? t('pb.v2.rec.playAll') : t('pb.v2.rec.playSegOnly') }}
            </button>
          }
          <span class="pb-name">{{ audioAsset()?.fileName }}</span>
        </div>
      }
    </div>
  `,
  styles: [`
    .record { padding: 4px 2px 70px; position: relative; }
    .qa-text { font-size: calc(15px * var(--qa-scale, 1)); line-height: 1.65; }
    .toolbar { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; }
    .tb-title { font-size: 16px; font-weight: 600; }
    .orig-tag { font-size: 12px; font-weight: 600; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 1px 7px; margin-left: 6px; vertical-align: 2px; }
    .spacer { flex: 1; }
    .notice { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .notice.warn { color: #dc2626; }
    .rename-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin: 8px 0; }
    .rename-row .lbl { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .fld.sm { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 6px; padding: 5px 8px;
              font-size: 14px; background: transparent; color: inherit; }
    .segments { display: flex; flex-direction: column; gap: 10px; margin: 10px 0; }
    .seg { border-left: 3px solid var(--yi-border, #e2e8f0); padding: 2px 0 2px 12px; border-radius: 6px; }
    .seg.playing { border-left-color: var(--yi-accent, #2563eb); background: color-mix(in srgb, var(--yi-accent, #2563eb) 6%, transparent); }
    .seg-head { display: flex; align-items: center; gap: 8px; }
    .speaker { font-size: 14px; font-weight: 700; }
    .time { font-size: 12px; color: var(--yi-text-muted, #475569); font-variant-numeric: tabular-nums; }
    .play-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                width: 22px; height: 22px; border-radius: 50%; font-size: 10px; cursor: pointer; }
    .seg-text { margin: 3px 0 0; white-space: pre-wrap; }
    .empty { font-size: 14px; color: var(--yi-text-muted, #475569); padding: 14px 0; }
    .empty-sm { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .overview { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 14px 16px; margin: 14px 0; }
    .ov-scores { display: flex; align-items: baseline; gap: 18px; flex-wrap: wrap; }
    .ov-big { font-size: 30px; font-weight: 800; }
    .ov-unit { font-size: 12px; font-weight: 600; color: var(--yi-text-muted, #475569); margin-left: 6px; }
    .ov-pass { font-size: 16px; font-weight: 600; }
    .ai-tag { font-size: 12px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 999px;
              padding: 1px 7px; color: var(--yi-text-muted, #475569); margin-left: 4px; }
    .ov-line { font-size: 14px; margin: 6px 0 0; line-height: 1.6; }
    .qcard { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 14px 16px; margin: 12px 0; }
    .q-head { display: flex; gap: 10px; align-items: flex-start; }
    .q-idx { font-size: 12px; font-weight: 800; color: var(--yi-accent, #2563eb);
             border: 1px solid currentColor; border-radius: 6px; padding: 2px 6px; margin-top: 2px; }
    .q-text { font-weight: 600; flex: 1; }
    .q-score { font-size: 13px; font-weight: 700; white-space: nowrap; }
    .q-by { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 4px 0 0; }
    .my-answer { background: color-mix(in srgb, var(--yi-text, #0f172a) 4%, transparent);
                 border-radius: 10px; padding: 10px 12px; margin: 10px 0; }
    .ma-label { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); }
    .my-answer p { margin: 4px 0 0; white-space: pre-wrap; }
    .dims { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 10px; margin: 10px 0; }
    .dim { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 10px 12px; }
    .dim-head { display: flex; justify-content: space-between; font-size: 13px; font-weight: 700; }
    .dim-score { font-size: 14px; }
    .dim-na { font-size: 12px; color: var(--yi-text-muted, #475569); font-weight: 600; }
    .bar { height: 5px; border-radius: 6px; background: color-mix(in srgb, var(--yi-text, #0f172a) 10%, transparent); margin: 7px 0; }
    .bar-fill { height: 100%; border-radius: 6px; background: var(--yi-accent, #2563eb); }
    .dim-c { font-size: 14px; line-height: 1.55; margin: 4px 0 0; }
    .dim-f { font-size: 14px; line-height: 1.55; margin: 4px 0 0; color: var(--yi-text-muted, #475569); }
    .std-block { margin-top: 12px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 10px; }
    .std-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .std-label { font-size: 13px; font-weight: 700; color: var(--yi-accent, #2563eb); }
    .mine-tag { font-size: 12px; font-weight: 700; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 1px 7px; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .std-text { margin: 6px 0 0; white-space: pre-wrap; }
    .std-text .sent-on { background: color-mix(in srgb, var(--yi-accent, #2563eb) 22%, transparent); border-radius: 6px; }
    .std-edit { width: 100%; box-sizing: border-box; border: 1px solid var(--yi-border, #e2e8f0);
                border-radius: 8px; padding: 8px 10px; background: transparent; color: inherit; margin-top: 6px; }
    .player-bar { position: sticky; bottom: 0; display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
                  background: var(--yi-surface, #ffffff); border: 1px solid var(--yi-border, #e2e8f0);
                  border-radius: 10px; padding: 10px 14px; margin-top: 16px;
                  box-shadow: 0 -4px 18px rgba(15, 23, 42, .08); }
    .player-bar audio { height: 34px; flex: 1; min-width: 220px; }
    .rate { font-size: 13px; display: inline-flex; gap: 5px; align-items: center; }
    .pb-name { font-size: 13px; color: var(--yi-text-muted, #475569); }
  `]
})
export class RoundRecordComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  private readonly destroyRef = inject(DestroyRef);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);
  readonly zoom = inject(QaZoomService);
  readonly tts = inject(TtsPlayerService);

  readonly segments = signal<TranscriptSegment[]>([]);
  readonly audioUrl = signal<string | null>(null);
  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  readonly currentSeg = signal(-1);
  readonly playMode = signal<'all' | 'segment'>('all');
  readonly rate = signal(1);
  readonly renameFrom = signal('');
  readonly renameTo = signal('');
  readonly editingQ = signal<string | null>(null);
  readonly editDraft = signal<StdEdit>({ spoken: '', technical: '' });
  readonly activeSentence = signal<{ key: string; idx: number } | null>(null);

  readonly roundAssets = computed(() =>
    (this.entry().assets ?? []).filter(a => a.roundId === this.round().id));
  readonly audioAsset = computed(() =>
    this.roundAssets().find(a => isAudioAsset(a)) ?? null);
  readonly hasTranscript = computed(() => this.segments().length > 0);
  readonly questions = computed(() =>
    (this.entry().questions ?? [])
      .filter(q => q.roundId === this.round().id)
      .sort((a, b) => a.sequence - b.sequence));
  readonly roundReview = computed(() =>
    safeJson<RoundReview>(this.round().reviewJson));
  readonly speakerList = computed(() =>
    // 票234：空说话人段落（无分段纯文本整块）不进说话人列表、不参与 Rename。
    [...new Set(this.segments().map(s => s.speaker).filter(s => !!s))]);
  /** 票191：Extract Q&A 按钮先隐藏不删除（恢复时改回 false 即一处恢复）。 */
  readonly hideExtractQa = true;

  private loadedRoundId = '';
  private loadedAssetSig = '';
  /** 票191：已自动抽取过的轮次（每轮一次；组件重建后由 questions() 非空与后端幂等双重兜底）。 */
  private readonly autoExtractTried = new Set<string>();
  /** 票195：销毁旗（DestroyRef 回调置位；loadTranscript/extractQuestions 每次 await 后查旗，丢弃迟到写入）。 */
  private destroyed = false;
  /**
   * 票195：转写加载世代号。每次新发起一次 loadTranscript 就 +1 并由本次加载捕获；
   * 迟到返回的旧世代结果整批丢弃（防旧轮次迟到转写覆盖新轮次）。
   * 注意：只在真正发起加载时 +1——effect 也会被 speakerList/renameFrom 变化重触发，
   * 那些重触发不发起加载，若也 +1 会无故作废一次正在飞行的同轮加载且无替代加载补位，
   * 反而留下旧转写；只在发起点 +1 与“旧轮次覆盖新轮次”的防护目标等价且无此副作用。
   */
  private loadGen = 0;
  /** 票221：服务端转写轮询定时器（191 遗留③）。完成信号是录音资产本身的转写稿
   * （Worker 经 CompleteTranscription 对录音资产 SetTranscript，getTranscript 可读），
   * 无需新后端端点、不建 SSE/WebSocket。 */
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private pollInFlight = false;
  /** 票221：轮询间隔 10s、总超时 10 分钟（60 次）。短录音 STT 通常 1–2 分钟内完成，
   * 10s 一次小 GET 对服务端无压力；超时停并保留 transcribeSubmitted 提示（与今日行为一致）。 */
  private readonly pollIntervalMs = 10000;
  private readonly pollMaxAttempts = 60;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      // 票221：销毁必清轮询（与 195 销毁旗双重防护，迟到 tick 不写任何信号）。
      this.stopPolling();
      // 票195：解绑 TTS 句高亮回调，销毁后不再写 activeSentence。
      this.tts.onSentence = null;
    });
    this.tts.onSentence = (key, idx) => {
      this.activeSentence.set(idx == null ? null : { key, idx });
    };
    effect(() => {
      const rid = this.round().id;
      // 票191：轮次切换或转写材料签名变化（入口新增转写）都重载转写，无需切轮次。
      const sig = this.roundAssets()
        .filter(a => a.kind === 'Transcript' || a.kind === 'Text' || a.kind === 'Notes')
        .map(a => a.id).sort().join('|');
      if (rid !== this.loadedRoundId || sig !== this.loadedAssetSig) {
        this.loadedRoundId = rid;
        this.loadedAssetSig = sig;
        this.currentSeg.set(-1);
        void this.loadTranscript(++this.loadGen);
      }
      const list = this.speakerList();
      if (list.length && !list.includes(this.renameFrom())) this.renameFrom.set(list[0]);
    }, { allowSignalWrites: true });
  }

  /** 票221：停轮询（完成/超时/切轮/重发/销毁共用）。 */
  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
    this.pollInFlight = false;
  }

  /**
   * 票221：开始轮询指定录音资产的转写稿。只在用户发起转写成功后调用；未发起时绝不轮询。
   * 每 tick 查一次该资产的 getTranscript：segmentsJson 非空或 transcriptText 非空即完成，
   * 停轮询并走既有 loadTranscript（已纳入录音资产）刷新段落显示；单次失败不中断轮询。
   * 切轮、销毁、超时均停；重发先停旧轮询再起新轮询。
   */
  private startPolling(entryId: string, roundId: string, audioId: string): void {
    this.stopPolling();
    let attempts = 0;
    this.pollTimer = setInterval(() => {
      // 票195 同款防护：已销毁或已切轮时停，不写任何信号。
      if (this.destroyed || this.round().id !== roundId) {
        this.stopPolling();
        return;
      }
      if (this.pollInFlight) return;
      if (++attempts > this.pollMaxAttempts) {
        this.stopPolling();
        return;
      }
      this.pollInFlight = true;
      void this.api.getTranscript(entryId, audioId).toPromise()
        .then(t => {
          if (this.destroyed || this.round().id !== roundId) return;
          const segs = safeJson<TranscriptSegment[]>(t?.segmentsJson ?? null);
          if ((segs?.length ?? 0) > 0 || !!t?.transcriptText?.trim()) {
            this.stopPolling();
            void this.loadTranscript(++this.loadGen);
          }
        })
        .catch(() => { /* 单次失败不中断轮询，下次 tick 再试 */ })
        .finally(() => { this.pollInFlight = false; });
    }, this.pollIntervalMs);
  }

  private async loadTranscript(gen: number): Promise<void> {
    // 票221：录音资产也纳入转写读取——服务端转写稿回写在录音资产本身
    // （CompleteTranscription 对录音资产 SetTranscript），不纳入则转写完成也永不显示。
    // 空/失败照既有规则跳过；fromTranscript 判定不动（仍只认 Transcript/Text），自动抽取逻辑一字不动。
    const audioId = this.audioAsset()?.id;
    // 票234：实录候选只排除 Text（邮件类，定稿 §4.4 邮件归 Materials 页签）；
    // 保留 Transcript / Notes / 已转写录音。邮件不再进实录。
    const candidates = this.roundAssets()
      .filter(a => a.kind === 'Transcript' || a.kind === 'Notes'
        || (audioId !== undefined && a.id === audioId));
    const all: TranscriptSegment[] = [];
    for (const a of candidates) {
      try {
        const t = await this.api.getTranscript(this.entry().id, a.id).toPromise();
        const segs = safeJson<TranscriptSegment[]>(t?.segmentsJson ?? null);
        if (segs?.length) all.push(...segs);
        else if (t?.transcriptText?.trim()) {
          // 票234：无分段纯文本改为整块无说话人段落（不再按行贴“Transcript”标签）；
          // speaker 为空，不进说话人列表、不参与 Rename（.seg-text 已是 pre-wrap，块内换行保留）。
          all.push({ speaker: '', text: t.transcriptText.trim() });
        }
      } catch { /* 单件失败不阻塞其它材料 */ }
      // 票195：每次 await 之后查销毁旗与世代号——已销毁或已有更新的加载发起时，本批结果整批丢弃。
      if (this.destroyed || gen !== this.loadGen) return;
    }
    // 票195：写信号之前再查一次（覆盖材料为空、循环零次的情况）。
    if (this.destroyed || gen !== this.loadGen) return;
    this.segments.set(all);
    // 票191：解析完成自动抽取——转写段落就绪且本轮尚无题目时，调既有抽取逻辑（每轮一次；
    // 后端幂等：已有题目返回 0，不重复建题；抽取结果经 changed 刷新进 Save Q&A 候选）。
    // 仅 Transcript/Text 材料触发（纯 Notes 不触发，避免无转写时的失败提示噪音）。
    const fromTranscript = candidates.some(a => a.kind === 'Transcript' || a.kind === 'Text');
    const rid = this.round().id;
    // 票195：发起自动抽取之前查销毁旗与世代号，避免为已销毁/已过期的轮次发起抽取调用。
    if (this.destroyed || gen !== this.loadGen) return;
    if (fromTranscript && all.length && !this.questions().length && !this.autoExtractTried.has(rid)) {
      this.autoExtractTried.add(rid);
      void this.extractQuestions();
    }
  }

  reviewOf(q: InterviewQuestion): QuestionReview | null {
    return parseQuestionReview(q.reviewJson);
  }

  dimList(rv: QuestionReview): { key: string; label: string; review: DimReview }[] {
    const d = rv.dims ?? {};
    const na: DimReview = { score: null };
    return [
      { key: 'relevance', label: this.t('pb.v2.dim.relevance'), review: d.relevance ?? na },
      { key: 'depth', label: this.t('pb.v2.dim.depth'), review: d.depth ?? na },
      { key: 'expression', label: this.t('pb.v2.dim.expression'), review: d.expression ?? na },
      { key: 'pronunciation', label: this.t('pb.v2.dim.pronunciation'), review: d.pronunciation ?? na }
    ];
  }

  stdBlocks(q: InterviewQuestion, rv: QuestionReview):
    { key: 'spoken' | 'technical'; label: string; text: string; isUser: boolean }[] {
    const mk = (key: 'spoken' | 'technical', label: string,
      std: { ai?: string | null; user?: string | null } | undefined) => ({
      key, label,
      text: std?.user || std?.ai || (key === 'spoken' ? (q.recommendedAnswer ?? '') : ''),
      isUser: !!std?.user
    });
    return [
      mk('spoken', this.t('pb.v2.rec.stdSpokenLabel'), rv.stdSpoken),
      mk('technical', this.t('pb.v2.rec.stdTechnicalLabel'), rv.stdTechnical)
    ];
  }

  sentencesOf(text: string): string[] {
    return TtsPlayerService.splitSentences(text);
  }

  isActiveSentence(q: InterviewQuestion, std: { key: string; text: string }, idx: number): boolean {
    const a = this.activeSentence();
    return !!a && a.idx === idx && this.tts.playingKey() === this.tts.keyFor(std.text);
  }

  async playStd(q: InterviewQuestion, std: { key: string; text: string }): Promise<void> {
    // 以答案文本为缓存键(与 TTS 服务内部一致);stdKey 仅作组件内标识。
    await this.tts.toggle(std.text);
  }

  startEditStd(q: InterviewQuestion, std: { key: 'spoken' | 'technical'; text: string }): void {
    const rv = this.reviewOf(q);
    const next: StdEdit = {
      spoken: rv?.stdSpoken?.user || rv?.stdSpoken?.ai || q.recommendedAnswer || '',
      technical: rv?.stdTechnical?.user || rv?.stdTechnical?.ai || ''
    };
    next[std.key] = std.text;
    this.editDraft.set(next);
    this.editingQ.set(q.id + ':' + std.key);
  }

  setDraft(key: 'spoken' | 'technical', v: string): void {
    this.editDraft.set({ ...this.editDraft(), [key]: v });
  }

  async saveStd(q: InterviewQuestion, key: 'spoken' | 'technical'): Promise<void> {
    const rv = this.reviewOf(q) ?? {};
    const draft = this.editDraft();
    const next: QuestionReview = {
      ...rv,
      stdSpoken: key === 'spoken'
        ? { ai: rv.stdSpoken?.ai ?? null, user: draft.spoken }
        : rv.stdSpoken,
      stdTechnical: key === 'technical'
        ? { ai: rv.stdTechnical?.ai ?? null, user: draft.technical }
        : rv.stdTechnical
    };
    await this.api.setQuestionReview(this.entry().id, q.id, serializeQuestionReview(next), q.version ?? null).toPromise();
    this.editingQ.set(null);
    this.snack.open(this.t('pb.v2.rec.savedYours'), this.t('common.close'), { duration: 2200 });
    this.changed.emit();
  }

  async restoreStd(q: InterviewQuestion, key: 'spoken' | 'technical'): Promise<void> {
    const rv = this.reviewOf(q);
    if (!rv) return;
    const next: QuestionReview = { ...rv };
    if (key === 'spoken') next.stdSpoken = { ai: rv.stdSpoken?.ai ?? null, user: null };
    else next.stdTechnical = { ai: rv.stdTechnical?.ai ?? null, user: null };
    await this.api.setQuestionReview(this.entry().id, q.id, serializeQuestionReview(next), q.version ?? null).toPromise();
    this.snack.open(this.t('pb.v2.rec.restoredAi'), this.t('common.close'), { duration: 2200 });
    this.changed.emit();
  }

  async generateReview(): Promise<void> {
    this.busy.set(true);
    this.notice.set(null);
    try {
      if (!this.questions().length && this.segments().length) {
        await this.api.extractQuestions(this.entry().id, this.round().id).toPromise();
      }
      await this.api.generateRoundReview(this.entry().id, this.round().id).toPromise();
      this.snack.open(this.t('pb.v2.rec.reviewGenerated'), this.t('common.close'), { duration: 2500 });
      this.changed.emit();
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.rec.reviewFailed', { msg: errText(e, this.t('pb.v2.rec.unknownError')) }));
    } finally {
      this.busy.set(false);
    }
  }

  async extractQuestions(): Promise<void> {
    // 票195：已销毁则不发起任何工作。
    if (this.destroyed) return;
    this.busy.set(true);
    try {
      const r = await this.api.extractQuestions(this.entry().id, this.round().id).toPromise();
      // 票195：await 之后、写信号与 changed.emit() 之前查销毁旗，已销毁则整批丢弃。
      if (this.destroyed) return;
      this.notice.set(r && r.created > 0
        ? this.fmt('pb.v2.rec.extracted', { n: r.created })
        : this.t('pb.v2.rec.extractNone'));
      this.changed.emit();
    } catch (e) {
      // 票195：失败分支写信号之前同样查销毁旗。
      if (this.destroyed) return;
      this.notice.set(this.fmt('pb.v2.rec.extractFailed', { msg: errText(e, this.t('pb.v2.rec.unknownError')) }));
    } finally {
      // 票195：已销毁的组件不再写 busy（正常路径行为一字不变）。
      if (!this.destroyed) this.busy.set(false);
    }
  }

  async toggleTranscribe(): Promise<void> {
    const a = this.audioAsset();
    if (!a) return;
    this.busy.set(true);
    try {
      await this.api.startTranscription(this.entry().id, a.id).toPromise();
      this.notice.set(this.t('pb.v2.rec.transcribeSubmitted'));
      // 票221：服务端转写是异步 STT，完成后页面内无感知是 191 遗留③——成功后开始轮询
      // 录音资产转写稿，出现即停并刷新段落显示；超时停并保留本提示（用户可重载或切轮，与今日一致）。
      this.startPolling(this.entry().id, this.round().id, a.id);
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.rec.transcribeFailed', { msg: errText(e, this.t('pb.v2.rec.unknownError')) }));
    } finally {
      this.busy.set(false);
    }
  }

  async doRename(): Promise<void> {
    const from = this.renameFrom();
    const to = this.renameTo().trim();
    if (!from || !to || from === to) return;
    this.busy.set(true);
    try {
      await this.api.renameSpeaker(this.entry().id, this.round().id, from, to).toPromise();
      this.renameTo.set('');
      this.snack.open(this.fmt('pb.v2.rec.renamed', { from, to }), this.t('common.close'), { duration: 2500 });
      this.changed.emit();
      // 票195：同轮重载同样取新世代号，使飞行中的旧加载结果作废。
      void this.loadTranscript(++this.loadGen);
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.rec.renameFailed', { msg: errText(e, this.t('pb.v2.rec.unknownError')) }));
    } finally {
      this.busy.set(false);
    }
  }

  ensureAudio(): void {
    const a = this.audioAsset();
    if (!a || this.audioUrl()) return;
    this.api.audioBlob(this.entry().id, a.id).subscribe({
      next: blob => this.audioUrl.set(URL.createObjectURL(blob)),
      error: () => this.notice.set(this.t('pb.v2.rec.audioLoadFailed'))
    });
  }

  playSegment(idx: number): void {
    this.ensureAudio();
    const tryPlay = () => {
      const audio = document.querySelector<HTMLAudioElement>('.player-bar audio');
      const seg = this.segments()[idx];
      if (!audio || !seg || seg.start == null) return;
      audio.currentTime = seg.start;
      void audio.play();
      this.currentSeg.set(idx);
    };
    if (this.audioUrl()) tryPlay();
    else setTimeout(tryPlay, 600);
  }

  onTimeUpdate(audio: HTMLAudioElement): void {
    const t = audio.currentTime;
    const segs = this.segments();
    let idx = -1;
    for (let i = 0; i < segs.length; i++) {
      const s = segs[i];
      if (s.start != null && t >= s.start && (s.end == null || t < s.end)) { idx = i; break; }
      if (s.start != null && t >= s.start) idx = i;
    }
    if (idx !== this.currentSeg()) this.currentSeg.set(idx);
    const cur = segs[this.currentSeg()];
    if (this.playMode() === 'segment' && cur?.end != null && t >= cur.end) audio.pause();
  }

  setRate(audio: HTMLAudioElement, r: number): void {
    this.rate.set(r);
    audio.playbackRate = r;
  }

  fmtTime(sec: number | null | undefined): string {
    if (sec == null) return '';
    const m = Math.floor(sec / 60);
    const s = Math.floor(sec % 60);
    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  }
}

function errText(e: unknown, unknownLabel = 'Unknown error'): string {
  if (e instanceof Error) return e.message;
  const anyE = e as { error?: { detail?: string; title?: string }; message?: string };
  return anyE?.error?.detail || anyE?.error?.title || anyE?.message || unknownLabel;
}
