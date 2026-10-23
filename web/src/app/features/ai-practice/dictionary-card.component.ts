import {
  AfterViewInit, Component, ElementRef, EventEmitter, Input, OnChanges, Output,
  SimpleChanges, ViewChild, inject, signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  DictAccent, DictCardState, accentButtonVisible, anyAccentAvailable, foldView,
} from './dictionary.helpers';

/** 卡内播放请求:宿主负责真正的取流/合成/设备朗读,卡只表达意图。 */
export type DictPlayRequest =
  | { target: 'accent'; accent: DictAccent }
  | { target: 'form'; text: string; accent: DictAccent }
  | { target: 'speak'; accent: DictAccent };

/**
 * 票119 词典卡(SPEC §6.2/§6.3):篇章点词后在篇章区下方就地展开。
 * 结构:词头行(词元 + 归元标注)→ 音标行(UK/US 各自的音标 + 播放钮,口音可切换)
 * → 词性组(标签 → 义项[默认前 2 条,余者折叠] → 中文行)→ 词形行 → 来源行。
 * 缺失即省略,全卡不出现「—」占位符。
 * 无障碍:role=dialog、以词头为 aria-label;打开后焦点移入首个可交互元素;
 * Esc 关闭(宿主把焦点送回触发词);卡内 Tab 首尾回绕(轻量焦点陷阱)。
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
            <button type="button" class="dict-icon-btn" (click)="closeRequested.emit()"
                    [attr.aria-label]="t('dict.close')">
              <mat-icon>close</mat-icon>
            </button>
          </div>
          <p class="dict-none">{{ t('dict.loadFailed') }}</p>
          <button type="button" class="dict-retry" (click)="retryRequested.emit()">
            {{ t('dict.retry') }}
          </button>
        }
        @case ('notfound') {
          <div class="dict-head-row">
            <span class="dict-headword">{{ state.query }}</span>
            <button type="button" class="dict-play-btn"
                    (click)="playRequested.emit({ target: 'speak', accent: 'us' })"
                    [attr.aria-label]="t('dict.speakWord')">
              <mat-icon>volume_up</mat-icon>
            </button>
            <button type="button" class="dict-icon-btn" (click)="closeRequested.emit()"
                    [attr.aria-label]="t('dict.close')">
              <mat-icon>close</mat-icon>
            </button>
          </div>
          <p class="dict-none">{{ t('dict.noEntry') }}</p>
        }
        @case ('ok') {
          @if (card(); as c) {
            <div class="dict-head-row">
              <span class="dict-headword">{{ c.lemma }}</span>
              @if (c.query !== c.lemma) {
                <span class="dict-lemma-note">{{ tf('dict.formOf', { lemma: c.lemma }) }}</span>
              }
              <span class="dict-spacer"></span>
              <button type="button" class="dict-icon-btn" (click)="closeRequested.emit()"
                      [attr.aria-label]="t('dict.close')">
                <mat-icon>close</mat-icon>
              </button>
            </div>

            @if (c.phonetics.uk || c.phonetics.us || anyAccent(c)) {
              <div class="dict-phon-row">
                @for (acc of accents; track acc) {
                  @if (phoneticOf(c, acc) || accentVisible(c, acc)) {
                    <span class="dict-phon" [class.dict-phon-active]="accent() === acc">
                      <button type="button" class="dict-accent-tag"
                              (click)="accent.set(acc)"
                              [attr.aria-label]="acc === 'us' ? t('dict.accentUs') : t('dict.accentUk')">
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
                  <button type="button" class="dict-play-btn"
                          (click)="playRequested.emit({ target: 'speak', accent: accent() })"
                          [attr.aria-label]="t('dict.speakWord')">
                    <mat-icon>volume_up</mat-icon>
                  </button>
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
          }
        }
      }
    </section>
  `,
  styles: [`
    .dict-card {
      margin-top: 12px; padding: 14px 16px;
      background: var(--surface); border: 1px solid var(--hairline); border-radius: 14px;
      color: var(--ink);
    }
    .dict-skeleton { height: 132px; display: flex; flex-direction: column; gap: 10px; }
    .sk { border-radius: 6px; background: var(--parked-fill); animation: dict-pulse 1.2s ease-in-out infinite; }
    .sk-head { width: 38%; height: 26px; }
    .sk-line { width: 92%; height: 14px; }
    .sk-line.short { width: 64%; }
    @keyframes dict-pulse { 0%, 100% { opacity: 1; } 50% { opacity: .45; } }
    .dict-sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }
    .dict-head-row { display: flex; align-items: center; gap: 8px; }
    .dict-headword { font-size: 22px; font-weight: 600; color: var(--ink); }
    .dict-lemma-note { font-size: 13px; color: var(--ink-2); }
    .dict-spacer { flex: 1; }
    .dict-icon-btn, .dict-play-btn {
      display: inline-flex; align-items: center; justify-content: center;
      width: 32px; height: 32px; border: none; border-radius: 999px;
      background: transparent; color: var(--brand); cursor: pointer; padding: 0;
    }
    .dict-icon-btn { color: var(--ink-2); }
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
    .dict-phon-active .dict-accent-tag { background: var(--signal-tint); color: var(--brand); border-color: var(--signal-soft); }
    .dict-ipa { font-size: 15px; color: var(--ink); }
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
  @Output() closeRequested = new EventEmitter<void>();
  @Output() retryRequested = new EventEmitter<void>();
  @Output() playRequested = new EventEmitter<DictPlayRequest>();

  @ViewChild('cardRoot') private cardRoot?: ElementRef<HTMLElement>;

  private readonly i18n = inject(I18nService);
  protected readonly accents: DictAccent[] = ['uk', 'us'];
  /** 当前口音(影响词形播放与设备朗读的口音选择)。 */
  protected readonly accent = signal<DictAccent>('us');
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
  }

  ngAfterViewInit(): void {
    this.maybeFocusFirst();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['state']) {
      const prev = changes['state'].previousValue as DictCardState | undefined;
      if (!prev || prev.query !== this.state.query || prev.status === 'error') {
        // 换词即重置折叠与口音(卡内容整体切换,不继承上一个词的展开态)
        this.expandedBlocks.set(new Set());
        this.accent.set('us');
        this.pendingFocus = true;
      }
      this.maybeFocusFirst();
    }
  }

  /** 卡落定后把焦点移入卡内首个可交互元素(SPEC §6.3,默认播放钮);loading 骨架期不抢焦点。 */
  private maybeFocusFirst(): void {
    if (!this.pendingFocus || !this.state || this.state.status === 'loading') return;
    this.pendingFocus = false;
    queueMicrotask(() => {
      const root = this.cardRoot?.nativeElement;
      if (!root) return;
      const target = root.querySelector<HTMLElement>('.dict-play-btn:not([disabled])')
        ?? root.querySelector<HTMLElement>('button:not([disabled])');
      target?.focus();
    });
  }

  /** 卡内键盘:Esc 关闭;Tab 在卡内首尾回绕(轻量焦点陷阱)。 */
  protected onCardKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.stopPropagation();
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
