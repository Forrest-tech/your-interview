import {
  AfterViewInit, Component, ElementRef, EventEmitter, Input, OnChanges, Output,
  SimpleChanges, ViewChild, computed, inject, signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  DictAccent, DictCardState, accentButtonVisible, anyAccentAvailable, foldView,
  lemmaRoleLabels, loadDictAccent, saveDictAccent,
} from './dictionary.helpers';
import {
  DictShadowAction, DictShadowView, shadowErrorLabelKey,
} from './dictionary-shadow';

/** 卡内播放请求:宿主负责真正的取流播放,卡只表达意图(票126 后不再有设备朗读请求)。 */
export type DictPlayRequest =
  | { target: 'accent'; accent: DictAccent }
  | { target: 'form'; text: string; accent: DictAccent };

/**
 * 词典卡(票119 建,票126 改 popup 载体):
 * 宿主把本组件挂进锚定触发词的 overlay 浮层呈现(§6),卡本身只管内容与卡内交互。
 * 结构:词头行(词元 + 归元标注)→ 音标行(UK/US 音标 + 播放钮,口音可切换)
 * → 词性组(标签 → 义项[默认前 2 条,余者折叠] → 中文行)→ 词形行 → 来源行。
 * 缺失即省略,全卡不出现「—」占位符。
 * 票126 口径:
 * - Close 钮在 DOM 末位(§6.8:Tab 序与视觉序一致,Close 末位),视觉靠绝对定位回右上角。
 * - 入卡首焦点 = 视觉序首个可交互元素(M-6/§6.7),不再写死播放钮优先。
 * - 口音记忆 localStorage(dict.accent,§8.5),默认 US;某口音无真人录音 → 其 tab 禁用并标注(§8.4)。
 * - 两口音皆无真人录音 → 无播放钮,只出「暂无发音」状态文案(§8.3,S-2);负卡无任何发音呈现(§4.3)。
 * - 归元标注由 forms 数据生成、须指明所点词形角色(§9.1)。
 * 无障碍:role=dialog、以词头为 aria-label;Esc 关闭(宿主把焦点送回触发词);卡内 Tab 首尾回绕。
 */
@Component({
  selector: 'app-dictionary-card',
  standalone: true,
  imports: [CommonModule, MatIconModule],
  template: `
    <section #cardRoot class="dict-card" role="dialog" [attr.aria-label]="dialogLabel()"
             (keydown)="onCardKeydown($event)">
      @switch (state.status) {
        @case ('loading') {
          <div class="dict-skeleton" aria-hidden="true">
            <div class="sk sk-head"></div>
            <div class="sk sk-line"></div>
            <div class="sk sk-line short"></div>
          </div>
          <span class="dict-sr">{{ t('dict.loading') }}</span>
        }
        @case ('error') {
          <div class="dict-head-row">
            <span class="dict-headword">{{ state.query }}</span>
          </div>
          <p class="dict-none">{{ t('dict.loadFailed') }}</p>
          <button type="button" class="dict-retry" (click)="retryRequested.emit()">
            {{ t('dict.retry') }}
          </button>
          <button type="button" class="dict-icon-btn dict-close" (click)="closeRequested.emit()"
                  [attr.aria-label]="t('dict.close')">
            <mat-icon>close</mat-icon>
          </button>
        }
        @case ('notfound') {
          <div class="dict-head-row">
            <span class="dict-headword">{{ state.query }}</span>
          </div>
          <p class="dict-none">{{ t('dict.noEntry') }}</p>
          <button type="button" class="dict-icon-btn dict-close" (click)="closeRequested.emit()"
                  [attr.aria-label]="t('dict.close')">
            <mat-icon>close</mat-icon>
          </button>
        }
        @case ('ok') {
          @if (card(); as c) {
            <div class="dict-head-row">
              <span class="dict-headword">{{ c.lemma }}</span>
              @if (roleNote(c); as note) {
                <span class="dict-lemma-note">{{ note }}</span>
              }
              <span class="dict-spacer"></span>
            </div>

            <div class="dict-phon-row">
              @for (acc of accents(); track acc) {
                @if (phoneticOf(c, acc) || accentVisible(c, acc)) {
                  <span class="dict-phon" [class.dict-phon-active]="accent() === acc">
                    <button type="button" class="dict-accent-tag"
                            (click)="setAccent(acc)"
                            [disabled]="!accentVisible(c, acc)"
                            [attr.title]="!accentVisible(c, acc) ? noAudioLabel(acc) : null"
                            [attr.aria-label]="!accentVisible(c, acc) ? noAudioLabel(acc) : (acc === 'us' ? t('dict.accentUs') : t('dict.accentUk'))">
                      {{ acc === 'us' ? 'US' : 'UK' }}
                    </button>
                    @if (phoneticOf(c, acc); as ipa) {
                      <span class="dict-ipa">{{ ipa }}</span>
                    }
                    @if (accentVisible(c, acc)) {
                      <button type="button" class="dict-play-btn"
                              (click)="playRequested.emit({ target: 'accent', accent: acc })"
                              [attr.aria-label]="acc === 'us' ? t('dict.playUs') : t('dict.playUk')">
                        <mat-icon>volume_up</mat-icon>
                      </button>
                    }
                  </span>
                }
              }
              @if (!anyAccent(c)) {
                <span class="dict-noaudio">{{ t('dict.noAudio') }}</span>
              }
            </div>

            @if (shadow; as sh) {
              <div class="dict-shadow" data-dict-shadow>
                <div class="dict-shadow-head">
                  <span class="dict-shadow-title">{{ t('dict.shadow.title') }}</span>
                  @if (sh.targetText) {
                    <span class="dict-shadow-target" data-shadow-target>{{ sh.targetText }}</span>
                  }
                </div>

                @switch (sh.phase) {
                  @case ('resolveFailed') {
                    <p class="dict-shadow-msg" data-shadow-error>{{ t(\'dict.shadow.resolveFailed\') }}</p>
                    <button type="button" class="dict-shadow-btn" data-shadow-retry
                            (click)="shadowAction.emit({ type: \'retryResolve\' })">
                      {{ t('dict.retry') }}
                    </button>
                  }
                  @case ('idle') {
                    @if (sh.notice === 'tooShort') {
                      <p class="dict-shadow-msg">{{ t('dict.shadow.tooShort') }}</p>
                    }
                    <div class="dict-shadow-row">
                      <button type="button" class="dict-shadow-btn" data-shadow-play-original
                              (click)="shadowAction.emit({ type: \'playOriginal\' })">
                        {{ t('dict.shadow.playOriginal') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-record
                              (click)="shadowAction.emit({ type: \'record\' })">
                        {{ t(\'dict.shadow.record\') }}
                      </button>
                    </div>
                  }
                  @case ('playing') {
                    <div class="dict-shadow-row">
                      <button type="button" class="dict-shadow-btn" data-shadow-play-original disabled>
                        {{ t('dict.shadow.playOriginal') }}
                      </button>
                      @if (sh.hasRecording) {
                        <button type="button" class="dict-shadow-btn" data-shadow-play-mine disabled>
                          {{ t('dict.shadow.myRecording') }}
                        </button>
                      }
                      <button type="button" class="dict-shadow-btn" data-shadow-record disabled>
                          {{ t('dict.shadow.record') }}
                      </button>
                    </div>
                  }
                  @case ('recording') {
                    <div class="dict-shadow-row">
                      <span class="dict-shadow-rec">
                        <span class="dict-shadow-dot"></span>{{ t('dict.shadow.recording') }} {{ sh.elapsedSec }}s
                      </span>
                      <button type="button" class="dict-shadow-btn" data-shadow-stop
                              (click)="shadowAction.emit({ type: 'stop' })">
                        {{ t('dict.shadow.stop') }}
                      </button>
                    </div>
                  }
                  @case ('recorded') {
                    <div class="dict-shadow-row">
                      <button type="button" class="dict-shadow-btn" data-shadow-play-original
                              (click)="shadowAction.emit({ type: \'playOriginal\' })">
                        {{ t('dict.shadow.original') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-play-mine
                              (click)="shadowAction.emit({ type: \'playMine\' })">
                        {{ t('dict.shadow.myRecording') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-score
                              (click)="shadowAction.emit({ type: 'score' })">
                        {{ t('dict.shadow.score') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-rerecord
                              (click)="shadowAction.emit({ type: \'reRecord\' })">
                        {{ t('dict.shadow.reRecord') }}
                      </button>
                    </div>
                  }
                  @case ('scoring') {
                    <p class="dict-shadow-msg">{{ t('dict.shadow.scoring') }}</p>
                  }
                  @case ('result') {
                    @if (sh.result; as r) {
                      <div class="dict-shadow-result" aria-live="polite">
                        <div class="dict-shadow-total" data-shadow-score-total>
                          <span>{{ t('dim.pronunciation') }}</span>
                          <b>{{ r.total }}</b><i>{{ t('practice.points') }}</i>
                        </div>
                        @if (r.dims.length > 0) {
                          <div class="dict-shadow-dims">
                            @for (d of r.dims; track d.labelKey) {
                              <span class="dict-shadow-dim">{{ t(d.labelKey) }}<b>{{ d.score }}</b></span>
                            }
                          </div>
                        }
                        @if (r.recognized) {
                          <p class="dict-shadow-recog">{{ r.recognized }}</p>
                        }
                        @for (w of r.words; track $index) {
                          <p class="dict-shadow-word">
                            {{ w.word }} · {{ w.accuracy }} · {{ t(wordErrorKey(w.errorType)) }}
                          </p>
                        }
                        @if (r.billedSeconds !== null) {
                          <p class="dict-shadow-billed">{{ r.billedSeconds }}s</p>
                        }
                      </div>
                    }
                    <div class="dict-shadow-row">
                      <button type="button" class="dict-shadow-btn" data-shadow-play-original
                              (click)="shadowAction.emit({ type: \'playOriginal\' })">
                        {{ t('dict.shadow.original') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-play-mine
                              (click)="shadowAction.emit({ type: \'playMine\' })">
                        {{ t('dict.shadow.myRecording') }}
                      </button>
                      <button type="button" class="dict-shadow-btn"
                              (click)="shadowAction.emit({ type: 'again' })">
                        {{ t('dict.shadow.again') }}
                      </button>
                      <button type="button" class="dict-shadow-btn" data-shadow-rerecord
                              (click)="shadowAction.emit({ type: \'reRecord\' })">
                        {{ t('dict.shadow.reRecord') }}
                      </button>
                    </div>
                  }
                  @case ('error') {
                    <p class="dict-shadow-msg" data-shadow-error>{{ t(shadowErrorKey(sh.errorKind)) }}</p>
                    <div class="dict-shadow-row">
                      @if (sh.hasRecording) {
                        <button type="button" class="dict-shadow-btn" [disabled]="sh.originalFailed" data-shadow-play-original
                                (click)="shadowAction.emit({ type: \'playOriginal\' })">
                          {{ t('dict.shadow.original') }}
                        </button>
                        <button type="button" class="dict-shadow-btn" data-shadow-play-mine
                                (click)="shadowAction.emit({ type: \'playMine\' })">
                          {{ t('dict.shadow.myRecording') }}
                        </button>
                      }
                      @if (sh.errorKind === 'micDenied') {
                        <button type="button" class="dict-shadow-btn" data-shadow-retry
                                (click)="shadowAction.emit({ type: \'record\' })">
                          {{ t(\'dict.retry\') }}
                        </button>
                      }
                      @if (sh.errorKind === 'scoringUnavailable' || sh.errorKind === 'scoringFailed') {
                        <button type="button" class="dict-shadow-btn" data-shadow-score
                                (click)="shadowAction.emit({ type: 'score' })">
                          {{ t('dict.shadow.score') }}
                        </button>
                      }
                      @if (sh.errorKind === 'notRecognized') {
                        <button type="button" class="dict-shadow-btn" data-shadow-rerecord
                                (click)="shadowAction.emit({ type: \'reRecord\' })">
                          {{ t('dict.shadow.reRecord') }}
                        </button>
                      }
                    </div>
                  }
                }
              </div>
            }

            @for (block of c.posBlocks; track block.pos; let bi = $index) {
              <div class="dict-pos-block">
                <div class="dict-pos-label">{{ block.label }}</div>
                <ol class="dict-senses" [id]="'dict-senses-' + bi">
                  @for (sense of visibleSenses(block, bi); track $index) {
                    <li class="dict-sense">
                      <span class="dict-def">{{ sense.defEn }}</span>
                      @if (sense.glossZh) {
                        <span class="dict-gloss">{{ sense.glossZh }}</span>
                      }
                      @if (sense.example; as ex) {
                        <div class="dict-example">
                          <span>{{ ex.en }}</span>
                          @if (ex.zh) {
                            <span class="dict-example-zh">{{ ex.zh }}</span>
                          }
                        </div>
                      }
                    </li>
                  }
                </ol>
                @if (foldOf(block, bi).folded > 0) {
                  <button type="button" class="dict-fold-btn"
                          [attr.aria-expanded]="isExpanded(bi)"
                          [attr.aria-controls]="'dict-senses-' + bi"
                          (click)="toggleFold(bi)">
                    {{ tn('dict.moreSenses', foldOf(block, bi).folded) }}
                  </button>
                } @else if (isExpanded(bi) && block.senses.length > 2) {
                  <button type="button" class="dict-fold-btn"
                          [attr.aria-expanded]="true"
                          [attr.aria-controls]="'dict-senses-' + bi"
                          (click)="toggleFold(bi)">
                    {{ t('dict.fewerSenses') }}
                  </button>
                }
                @for (line of block.zhLines; track $index) {
                  <p class="dict-zh">{{ line }}</p>
                }
              </div>
            }

            @if (c.forms.length > 0) {
              <div class="dict-forms">
                @for (form of c.forms; track form.text + form.label) {
                  <span class="dict-form">
                    <span class="dict-form-label">{{ form.label }}</span>
                    <span class="dict-form-text">{{ form.text }}</span>
                    <button type="button" class="dict-play-btn dict-play-sm"
                            (click)="playRequested.emit({ target: 'form', text: form.text, accent: accent() })"
                            [attr.aria-label]="tf('dict.playForm', { form: form.text })">
                      <mat-icon>volume_up</mat-icon>
                    </button>
                  </span>
                }
              </div>
            }

            <div class="dict-source">
              <span>{{ c.attribution.source }}</span>
              @if (c.attribution.audioSource) {
                <span class="dict-source-audio">{{ t('dict.pronunciationSource') }}: {{ c.attribution.audioSource }}</span>
              }
            </div>

            <button type="button" class="dict-icon-btn dict-close" (click)="closeRequested.emit()"
                    [attr.aria-label]="t('dict.close')">
              <mat-icon>close</mat-icon>
            </button>
          }
        }
      }
    </section>
  `,
  styles: [`
    .dict-card {
      position: relative;
      padding: 14px 16px;
      background: var(--surface); border: 1px solid var(--hairline); border-radius: 14px;
      color: var(--ink);
      max-height: 70vh; overflow-y: auto;
    }
    @media (max-width: 639px) {
      .dict-card { max-height: 60vh; }
    }
    .dict-skeleton { height: 132px; display: flex; flex-direction: column; gap: 10px; }
    .sk { border-radius: 6px; background: var(--parked-fill); animation: dict-pulse 1.2s ease-in-out infinite; }
    .sk-head { width: 38%; height: 26px; }
    .sk-line { width: 92%; height: 14px; }
    .sk-line.short { width: 64%; }
    @keyframes dict-pulse { 0%, 100% { opacity: 1; } 50% { opacity: .45; } }
    .dict-sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    .dict-head-row { display: flex; align-items: center; gap: 8px; padding-right: 36px; }
    .dict-headword { font-size: 22px; font-weight: 600; color: var(--ink); }
    .dict-lemma-note { font-size: 13px; color: var(--ink-2); }
    .dict-spacer { flex: 1; }
    .dict-icon-btn, .dict-play-btn {
      display: inline-flex; align-items: center; justify-content: center;
      width: 32px; height: 32px; border: none; border-radius: 999px;
      background: transparent; color: var(--brand); cursor: pointer; padding: 0;
    }
    .dict-icon-btn { color: var(--ink-2); }
    .dict-close { position: absolute; top: 10px; right: 10px; }
    .dict-icon-btn:hover, .dict-play-btn:hover { background: var(--signal-tint); }
    .dict-icon-btn:focus-visible, .dict-play-btn:focus-visible,
    .dict-accent-tag:focus-visible, .dict-fold-btn:focus-visible, .dict-retry:focus-visible {
      outline: 2px solid var(--brand); outline-offset: 1px;
    }
    .dict-play-sm { width: 26px; height: 26px; }
    .dict-play-sm mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .dict-phon-row { display: flex; align-items: center; gap: 18px; margin-top: 6px; flex-wrap: wrap; }
    .dict-phon { display: inline-flex; align-items: center; gap: 6px; }
    .dict-accent-tag {
      border: 1px solid var(--hairline); background: var(--paper); color: var(--ink-2);
      border-radius: 6px; font-size: 11px; font-weight: 600; padding: 2px 6px; cursor: pointer;
    }
    .dict-accent-tag[disabled] { opacity: .55; cursor: not-allowed; }
    .dict-phon-active .dict-accent-tag { background: var(--signal-tint); color: var(--brand); border-color: var(--signal-soft); }
    .dict-ipa { font-size: 15px; color: var(--ink); }
    .dict-noaudio { font-size: 13px; color: var(--ink-2); }
    .dict-shadow { margin-top: 12px; padding-top: 10px; border-top: 1px solid var(--hairline); }
    .dict-shadow-head { display: flex; align-items: baseline; gap: 8px; }
    .dict-shadow-title {
      font-size: 12px; font-weight: 600; color: var(--brand);
      background: var(--signal-tint); border-radius: 6px; padding: 2px 8px;
    }
    .dict-shadow-target { font-size: 14px; font-weight: 500; color: var(--ink); }
    .dict-shadow-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
    .dict-shadow-btn {
      border: 1px solid var(--hairline); background: var(--surface); color: var(--brand);
      border-radius: 8px; padding: 5px 12px; cursor: pointer; font-size: 13px;
    }
    .dict-shadow-btn:hover { background: var(--signal-tint); }
    .dict-shadow-btn[disabled] { opacity: .55; cursor: not-allowed; }
    .dict-shadow-btn:focus-visible { outline: 2px solid var(--brand); outline-offset: 1px; }
    .dict-shadow-msg { margin: 8px 0 0; font-size: 13.5px; color: var(--ink-2); }
    .dict-shadow-rec { display: inline-flex; align-items: center; gap: 6px; font-size: 13.5px; color: var(--ink); }
    .dict-shadow-dot {
      width: 8px; height: 8px; border-radius: 999px; background: #d33;
      animation: dict-pulse 1.2s ease-in-out infinite;
    }
    .dict-shadow-result { margin-top: 8px; }
    .dict-shadow-total { display: flex; align-items: baseline; gap: 6px; font-size: 14px; color: var(--ink-2); }
    .dict-shadow-total b { font-size: 20px; color: var(--ink); }
    .dict-shadow-total i { font-style: normal; font-size: 12px; }
    .dict-shadow-dims { display: flex; gap: 6px 14px; flex-wrap: wrap; margin-top: 6px; font-size: 13px; color: var(--ink-2); }
    .dict-shadow-dim b { margin-left: 4px; color: var(--ink); }
    .dict-shadow-recog { margin: 6px 0 0; font-size: 14px; color: var(--ink); }
    .dict-shadow-word { margin: 4px 0 0; font-size: 13px; color: var(--ink-2); }
    .dict-shadow-billed { margin: 4px 0 0; font-size: 12px; color: var(--ink-2); }
    .dict-pos-block { margin-top: 12px; }
    .dict-pos-label {
      display: inline-block; font-size: 12px; font-weight: 600; color: var(--brand);
      background: var(--signal-tint); border-radius: 6px; padding: 2px 8px;
    }
    .dict-senses { margin: 8px 0 0; padding-left: 20px; }
    .dict-sense { margin-bottom: 6px; line-height: 1.55; }
    .dict-def { font-size: 15px; }
    .dict-gloss { font-size: 14px; color: var(--ink-2); margin-left: 8px; }
    .dict-example { font-size: 13.5px; color: var(--ink-2); margin-top: 2px; }
    .dict-example-zh { margin-left: 8px; }
    .dict-fold-btn {
      border: none; background: transparent; color: var(--brand); cursor: pointer;
      font-size: 13px; padding: 2px 0; margin-top: 2px;
    }
    .dict-zh { margin: 6px 0 0; font-size: 14.5px; line-height: 1.6; color: var(--ink); }
    .dict-forms {
      display: flex; flex-wrap: wrap; gap: 8px 16px; margin-top: 12px;
      padding-top: 10px; border-top: 1px solid var(--hairline);
    }
    .dict-form { display: inline-flex; align-items: center; gap: 6px; font-size: 14px; }
    .dict-form-label { color: var(--ink-2); font-size: 13px; }
    .dict-form-text { font-weight: 500; }
    .dict-source {
      margin-top: 12px; padding-top: 8px; border-top: 1px solid var(--hairline);
      font-size: 12px; color: var(--ink-2); display: flex; gap: 6px; flex-wrap: wrap;
    }
    .dict-none { margin: 8px 0 0; font-size: 14.5px; color: var(--ink-2); }
    .dict-retry {
      margin-top: 10px; border: 1px solid var(--hairline); background: var(--surface);
      color: var(--brand); border-radius: 8px; padding: 6px 14px; cursor: pointer; font-size: 13.5px;
    }
  `],
})
export class DictionaryCardComponent implements AfterViewInit, OnChanges {
  @Input({ required: true }) state!: DictCardState;
  /** 票127 跟读区视图(null = 不渲染:无美音真人录音/解析中/非 ok 卡)。 */
  @Input() shadow: DictShadowView | null = null;
  @Output() closeRequested = new EventEmitter<void>();
  @Output() retryRequested = new EventEmitter<void>();
  @Output() playRequested = new EventEmitter<DictPlayRequest>();
  /** 票127 跟读动作回抛(会话状态机在宿主侧 DictionaryShadowSession)。 */
  @Output() shadowAction = new EventEmitter<DictShadowAction>();
  /** 卡高变化(折叠展开/收起)→ 宿主据此让浮层按真实高度重选位序(§6.3 完整可见)。 */
  @Output() layoutChanged = new EventEmitter<void>();

  @ViewChild('cardRoot') private cardRoot?: ElementRef<HTMLElement>;

  private readonly i18n = inject(I18nService);
  /** 音标行序:当前口音行在前(119 既有;§6.8 首焦点「当前口音播放钮」依赖此序)。 */
  protected readonly accents = computed<DictAccent[]>(() =>
    this.accent() === 'uk' ? ['uk', 'us'] : ['us', 'uk']);
  /** 当前口音(影响词形播放):初值读 localStorage 记忆,无记忆默认 US(§8.5/S-1)。 */
  protected readonly accent = signal<DictAccent>(loadDictAccent());
  private readonly expandedBlocks = signal<ReadonlySet<number>>(new Set());
  /** 换词待聚焦标记:新词卡落定(ok/notfound/error)后把焦点移入一次。 */
  private pendingFocus = false;

  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number): string => this.i18n.tn(key, n);
  tf = (key: string, params: Record<string, string | number>): string => this.i18n.tf(key, params);

  protected card() {
    return this.state.status === 'ok' ? this.state.card : null;
  }

  protected dialogLabel(): string {
    return this.state.status === 'ok' ? this.state.card.lemma : this.state.query;
  }

  protected phoneticOf(c: NonNullable<ReturnType<typeof this.card>>, acc: DictAccent): string | null {
    return acc === 'us' ? c.phonetics.us ?? null : c.phonetics.uk ?? null;
  }

  protected accentVisible(c: NonNullable<ReturnType<typeof this.card>>, acc: DictAccent): boolean {
    return accentButtonVisible(c.audio, acc);
  }

  protected anyAccent(c: NonNullable<ReturnType<typeof this.card>>): boolean {
    return anyAccentAvailable(c.audio);
  }

  /** §8.4 禁用标注:「暂无发音 · UK」式(dict.noAudio + 口音名,不新造键)。 */
  protected noAudioLabel(acc: DictAccent): string {
    return `${this.t('dict.noAudio')} · ${acc === 'us' ? 'US' : 'UK'}`;
  }

  /** 切换口音并落 localStorage 记忆(§8.5);禁用 tab 原生不触发 click。 */
  protected setAccent(acc: DictAccent): void {
    this.accent.set(acc);
    saveDictAccent(acc);
  }

  /**
   * 归元标注(§9.1):所查即原形 → 不渲染;否则 = formOf 文案 + forms 数据里的词形角色。
   */
  protected roleNote(c: NonNullable<ReturnType<typeof this.card>>): string | null {
    if (c.query === c.lemma) return null;
    const base = this.tf('dict.formOf', { lemma: c.lemma });
    const labels = lemmaRoleLabels(c.forms, c.query);
    return labels.length > 0 ? `${base} · ${labels.join('、')}` : base;
  }

  protected isExpanded(blockIndex: number): boolean {
    return this.expandedBlocks().has(blockIndex);
  }

  protected foldOf(block: { senses: unknown[] }, blockIndex: number) {
    return foldView(block.senses.length, this.isExpanded(blockIndex));
  }

  protected visibleSenses<T>(block: { senses: T[] }, blockIndex: number): T[] {
    const view = foldView(block.senses.length, this.isExpanded(blockIndex));
    return block.senses.slice(0, view.visible);
  }

  protected toggleFold(blockIndex: number): void {
    this.expandedBlocks.update((set) => {
      const next = new Set(set);
      if (next.has(blockIndex)) next.delete(blockIndex);
      else next.add(blockIndex);
      return next;
    });
    this.layoutChanged.emit();
  }

  ngAfterViewInit(): void {
    this.maybeFocusFirst();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['state']) {
      const prev = changes['state'].previousValue as DictCardState | undefined;
      if (!prev || prev.query !== this.state.query || prev.status === 'error') {
        // 换词重置折叠态(卡内容整体切换);口音不复位——走 localStorage 记忆(§8.5)
        this.expandedBlocks.set(new Set());
        this.pendingFocus = true;
      }
      this.maybeFocusFirst();
    }
    if (changes['shadow']) {
      const prev = changes['shadow'].previousValue as DictShadowView | null | undefined;
      const cur = this.shadow;
      if (prev?.phase !== cur?.phase || prev?.targetText !== cur?.targetText) {
        // 跟读区高度变化 → 宿主按真实高度重选浮层位序(§9.5:不撑破上限、不传导页面位移)
        this.layoutChanged.emit();
        // 焦点衔接(§9.2):录音钮被停止钮替换时把焦点接住,不让键盘用户掉到 body;
        // 仅在焦点本就在卡内时接管,不抢焦点。结果呈现不移焦点(aria-live 播报)。
        const root = this.cardRoot?.nativeElement;
        if (root && root.contains(document.activeElement)) {
          if (cur?.phase === 'recording' && prev?.phase !== 'recording') {
            queueMicrotask(() =>
              this.cardRoot?.nativeElement.querySelector<HTMLElement>('[data-shadow-stop]')?.focus());
          } else if (cur?.phase === 'recorded' && prev?.phase === 'recording') {
            queueMicrotask(() =>
              this.cardRoot?.nativeElement.querySelector<HTMLElement>('[data-shadow-score]')?.focus());
          }
        }
      }
    }
  }

  /** 逐词 errorType 文案键(票127 §7.3:沿用现有报告 err.* 映射,不重造)。 */
  protected wordErrorKey(errorType: string): string {
    return shadowErrorLabelKey(errorType);
  }

  /** 跟读失败文案键(票127 §10 子型 → dict.shadow.* 键)。 */
  protected shadowErrorKey(kind: DictShadowView['errorKind']): string {
    switch (kind) {
      case 'micDenied': return 'dict.shadow.micDenied';
      case 'scoringUnavailable': return 'dict.shadow.scoringUnavailable';
      case 'notRecognized': return 'dict.shadow.notRecognized';
      case 'originalFailed': return 'dict.shadow.originalFailed';
      default: return 'dict.shadow.scoringFailed';
    }
  }

  /** 卡落定后把焦点移入视觉序首个可交互元素(M-6/§6.7);loading 骨架期不抢焦点。 */
  private maybeFocusFirst(): void {
    if (!this.pendingFocus || !this.state || this.state.status === 'loading') return;
    this.pendingFocus = false;
    queueMicrotask(() => {
      const root = this.cardRoot?.nativeElement;
      if (!root) return;
      root.querySelector<HTMLElement>('button:not([disabled])')?.focus();
    });
  }

  /**
   * 卡内键盘:Esc 关闭;Tab 在卡内首尾回绕(轻量焦点陷阱,序 = DOM 序,Close 末位)。
   * 票127 §9.3 Esc 分层:跟读录音态下 Esc 先停录(进 recorded),不关卡;再次 Esc 才关。
   */
  protected onCardKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.stopPropagation();
      if (this.shadow?.phase === 'recording') {
        this.shadowAction.emit({ type: 'stop' });
        return;
      }
      this.closeRequested.emit();
      return;
    }
    if (event.key !== 'Tab') return;
    const root = this.cardRoot?.nativeElement;
    if (!root) return;
    const focusables = Array.from(
      root.querySelectorAll<HTMLElement>('button:not([disabled])'),
    ).filter((el) => el.offsetParent !== null);
    if (focusables.length === 0) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = document.activeElement as HTMLElement | null;
    if (event.shiftKey && active === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }
}
