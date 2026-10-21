import {
  Component, EventEmitter, Output, computed, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewDetail, InterviewRound
} from '../../../../core/models/api.models';
import { normalizeEmailDate, parsePastedEmail } from '../../components/brief/brief.helpers';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, TranscriptSegment } from '../v2.models';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { joinNames, makeV2Fmt, makeV2T } from '../v2.helpers';

export type IntakeType = 'email' | 'transcript' | 'note';
type Target = { kind: 'round'; roundId: string } | { kind: 'comm' } | { kind: 'new' };

const CATEGORY_KEYS = ['invitation', 'reschedule', 'rejection', 'followup', 'other'];

/**
 * 条目级统一入口(2026-10-20 定稿):
 * 类型只有三种 —— 邮件 / 录音·转写文本 / 备注(电话记录写进后两种)。
 * 流程:粘贴 → 解析预览(先分类再提取,可改)→ 归属确认(系统按面试官/日期建议,
 * 可改;选新建轮次且有时间则建轮次并进应用内日历)→ 保存。
 * 任何类型都可附一张截图原图 —— 截图是附件,不是类型。
 */
@Component({
  selector: 'app-intake-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (open()) {
      <section class="panel" data-testid="intake-panel" (paste)="onPaste($event)">
        <ng-template #shotTpl>
          @if (shotFile()) {
            <div class="shot-box" data-testid="shot-box">
              <img class="shot-thumb" [src]="shotPreviewUrl()" [alt]="shotName()">
              <span class="shot-name">{{ shotName() }}</span>
              <button type="button" class="icon-btn" (click)="clearShot()" [attr.aria-label]="t('pb.v2.i.removeShot')">
                <mat-icon>close</mat-icon>
              </button>
            </div>
          }
        </ng-template>
        <div class="panel-head">
          <h3>{{ t('pb.v2.d.addMaterial') }}</h3>
          <button type="button" class="icon-btn" (click)="close()" [attr.aria-label]="t('common.close')">
            <mat-icon>close</mat-icon>
          </button>
        </div>

        <div class="type-tabs" role="tablist">
          @for (tb of typeTabs(); track tb.key) {
            <button type="button" role="tab" class="ttab" [class.on]="type() === tb.key"
                    (click)="setType(tb.key)">{{ tb.label }}</button>
          }
        </div>

        @if (step() === 'input') {
          @if (type() === 'email') {
            <p class="hint">{{ t('pb.v2.i.hintEmail') }}</p>
          } @else if (type() === 'transcript') {
            <p class="hint">{{ t('pb.v2.i.hintTranscript') }}</p>
          } @else {
            <p class="hint">{{ t('pb.v2.i.hintNote') }}</p>
          }
          <input class="fld" [placeholder]="t('pb.v2.i.titlePh')"
                 [ngModel]="title()" (ngModelChange)="title.set($event)">
          <textarea class="paste" rows="9"
                    [placeholder]="type() === 'email' ? t('pb.v2.i.pasteEmailPh') : type() === 'transcript' ? t('pb.v2.i.pasteTranscriptPh') : t('pb.v2.i.pasteNotePh')"
                    [ngModel]="text()" (ngModelChange)="text.set($event)"></textarea>
          @if (type() === 'transcript') {
            <div class="audio-row">
              <label class="file-btn">
                <mat-icon>mic</mat-icon> {{ t('pb.v2.i.attachAudio') }}
                <input type="file" accept=".mp3,.m4a,.wav,.webm,.ogg,audio/*" hidden (change)="onAudio($event)">
              </label>
              @if (audioFile()) {
                <span class="shot-name">{{ fmt('pb.v2.i.audioInfo', { name: audioName(), size: audioSizeText() }) }}</span>
                <button type="button" class="icon-btn" (click)="removeAudio()" [attr.aria-label]="t('pb.v2.i.removeAudio')">
                  <mat-icon>close</mat-icon>
                </button>
              }
            </div>
          }
          <ng-container [ngTemplateOutlet]="shotTpl"></ng-container>
          <div class="row">
            <label class="file-btn">
              <mat-icon>image</mat-icon> {{ t('pb.v2.i.attachShot') }}
              <input type="file" accept="image/*" hidden (change)="onShot($event)">
            </label>
            @if (!shotFile()) { <span class="shot-hint">{{ t('pb.v2.i.shotHint') }}</span> }
            <span class="spacer"></span>
            <button mat-stroked-button (click)="close()">{{ t('common.cancel') }}</button>
            <button mat-raised-button color="primary" [disabled]="(!text().trim() && !audioFile()) || busy()"
                    (click)="parse()">{{ t('pb.v2.i.parsePreview') }}</button>
          </div>
        } @else {
          <!-- 解析预览 -->
          <div class="preview" data-testid="intake-preview">
            @if (audioFile()) {
              <p class="audio-info" data-testid="intake-audio-info">
                {{ fmt('pb.v2.i.audioInfo', { name: audioName(), size: audioSizeText() }) }}<br>
                {{ t('pb.v2.i.audioWillTranscribe') }}
              </p>
            }
            <ng-container [ngTemplateOutlet]="shotTpl"></ng-container>
            @if (type() === 'email') {
              <div class="pv-grid">
                <label>{{ t('pb.v2.i.fCategory') }}
                  <select class="fld" [ngModel]="category()" (ngModelChange)="category.set($event)">
                    @for (c of categories(); track c.key) { <option [value]="c.key">{{ c.label }}</option> }
                  </select>
                </label>
                <label>{{ t('pb.v2.i.fSubject') }} <input class="fld" [ngModel]="fSubject()" (ngModelChange)="fSubject.set($event)"></label>
                <label>{{ t('pb.v2.i.fFrom') }} <input class="fld" [ngModel]="fFrom()" (ngModelChange)="fFrom.set($event)"></label>
                <label>{{ t('pb.v2.i.fEmailDate') }} <input class="fld" type="date" [ngModel]="fDate()" (ngModelChange)="fDate.set($event)"></label>
                <label>{{ t('pb.v2.i.fWhen') }} <input class="fld" [placeholder]="t('pb.v2.i.fWhenPh')" [ngModel]="fWhen()" (ngModelChange)="fWhen.set($event)"></label>
                <label>{{ t('pb.v2.i.fInterviewers') }} <input class="fld" [placeholder]="t('pb.v2.i.commaPh')" [ngModel]="fInterviewers()" (ngModelChange)="fInterviewers.set($event)"></label>
                <label>{{ t('pb.v2.i.fFormat') }}
                  <select class="fld" [ngModel]="fFormat()" (ngModelChange)="fFormat.set($event)">
                    <option value="">{{ t('pb.v2.fmt.unknown') }}</option>
                    <option value="线上视频">{{ t('pb.v2.fmt.video') }}</option>
                    <option value="线上做题平台">{{ t('pb.v2.fmt.platform') }}</option>
                    <option value="电话">{{ t('pb.v2.fmt.phone') }}</option>
                    <option value="现场">{{ t('pb.v2.fmt.onsite') }}</option>
                  </select>
                </label>
              </div>
            } @else {
              <div class="pv-grid">
                <label>{{ t('pb.v2.i.fOccurredDate') }} <input class="fld" type="date" [ngModel]="fDate()" (ngModelChange)="fDate.set($event)"></label>
                <label>{{ t('pb.v2.i.fParticipants') }} <input class="fld" [placeholder]="t('pb.v2.i.commaPh')" [ngModel]="fInterviewers()" (ngModelChange)="fInterviewers.set($event)"></label>
              </div>
              @if (segments().length) {
                <p class="hint">{{ fmt('pb.v2.i.segsFound', { n: segments().length, names: speakerNames() }) }}</p>
              }
              <label class="check-row">
                <input type="checkbox" [ngModel]="hasArrangement()" (ngModelChange)="hasArrangement.set($event)">
                {{ t('pb.v2.i.hasArrangement') }}
              </label>
              @if (hasArrangement()) {
                <div class="pv-grid">
                  <label>{{ t('pb.v2.i.arrDate') }} <input class="fld" type="date" [ngModel]="arrDate()" (ngModelChange)="arrDate.set($event)"></label>
                  <label>{{ t('pb.v2.r.fTime') }} <input class="fld" [placeholder]="t('pb.v2.i.arrTimePh')" [ngModel]="arrTime()" (ngModelChange)="arrTime.set($event)"></label>
                  <label>{{ t('pb.v2.r.fStage') }} <input class="fld" [placeholder]="t('pb.v2.i.arrStagePh')" [ngModel]="arrStage()" (ngModelChange)="arrStage.set($event)"></label>
                </div>
              }
            }

            @if (duplicate()) {
              <p class="warn">{{ t('pb.v2.i.dupWarn') }}</p>
            }

            <div class="sec">{{ t('pb.v2.i.assignTitle') }}</div>
            <div class="targets">
              @for (r of rounds(); track r.id) {
                <label class="tgt">
                  <input type="radio" name="tgt" [checked]="isTargetRound(r.id)" (change)="target.set({kind:'round', roundId: r.id})">
                  Round {{ r.order }} · {{ r.stage }}{{ r.scheduledDate ? ' · ' + r.scheduledDate : '' }}
                </label>
              }
              <label class="tgt">
                <input type="radio" name="tgt" [checked]="target().kind === 'comm'" (change)="target.set({kind:'comm'})">
                {{ t('pb.v2.i.targetComm') }}
              </label>
              <label class="tgt">
                <input type="radio" name="tgt" [checked]="target().kind === 'new'" (change)="target.set({kind:'new'})">
                {{ t('pb.v2.i.targetNew') }}
              </label>
            </div>
            @if (target().kind === 'new') {
              <div class="pv-grid">
                <label>{{ t('pb.v2.r.fDate') }} <input class="fld" type="date" [ngModel]="nrDate()" (ngModelChange)="nrDate.set($event)"></label>
                <label>{{ t('pb.v2.r.fTime') }} <input class="fld" [placeholder]="t('pb.v2.i.nrTimePh')" [ngModel]="nrTime()" (ngModelChange)="nrTime.set($event)"></label>
                <label>{{ t('pb.v2.r.fStage') }} <input class="fld" [placeholder]="t('pb.v2.i.nrStagePh')" [ngModel]="nrStage()" (ngModelChange)="nrStage.set($event)"></label>
                <label>{{ t('pb.v2.r.fInterviewers') }} <input class="fld" [ngModel]="nrInterviewers()" (ngModelChange)="nrInterviewers.set($event)"></label>
                <label>{{ t('pb.v2.r.fFormat') }} <input class="fld" [placeholder]="t('pb.v2.i.nrFormatPh')" [ngModel]="nrFormat()" (ngModelChange)="nrFormat.set($event)"></label>
              </div>
              <p class="hint">{{ t('pb.v2.i.newRoundHint') }}</p>
            }

            <div class="row">
              <button mat-stroked-button (click)="step.set('input')">{{ t('pb.v2.i.backEdit') }}</button>
              <span class="spacer"></span>
              <button mat-stroked-button (click)="close()">{{ t('common.cancel') }}</button>
              <button mat-raised-button color="primary" [disabled]="busy()" (click)="save()">
                {{ busy() ? t('pb.v2.i.saving') : t('common.save') }}
              </button>
            </div>
            @if (errorMsg()) { <p class="err">{{ errorMsg() }}</p> }
          </div>
        }
      </section>
    }
  `,
  styles: [`
    .panel { border: 1.5px solid var(--yi-accent, #3f51b5); border-radius: 14px; padding: 16px 18px;
             margin: 14px 0; background: var(--yi-surface, transparent); }
    .panel-head { display: flex; align-items: center; justify-content: space-between; }
    .panel-head h3 { margin: 0; font-size: 16px; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .type-tabs { display: flex; gap: 6px; margin: 10px 0; flex-wrap: wrap; }
    .ttab { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
            border-radius: 999px; padding: 6px 14px; font-size: 13px; cursor: pointer; }
    .ttab.on { background: var(--yi-accent, #3f51b5); color: #fff; border-color: transparent; }
    .hint { font-size: 12.5px; color: var(--yi-text-muted, #777); margin: 6px 0; }
    .fld { width: 100%; border: 1px solid var(--yi-border, #ccc); border-radius: 8px;
           padding: 8px 10px; font-size: 13px; background: transparent; color: inherit; box-sizing: border-box; }
    .paste { width: 100%; border: 1px solid var(--yi-border, #ccc); border-radius: 10px; padding: 10px 12px;
             font-size: 13px; line-height: 1.55; background: transparent; color: inherit;
             box-sizing: border-box; resize: vertical; margin-top: 8px; }
    .row { display: flex; align-items: center; gap: 10px; margin-top: 12px; flex-wrap: wrap; }
    .spacer { flex: 1; }
    .file-btn { display: inline-flex; align-items: center; gap: 6px; font-size: 12.5px; cursor: pointer;
                border: 1px dashed var(--yi-border, #bbb); border-radius: 8px; padding: 6px 10px; }
    .file-btn mat-icon { font-size: 17px; width: 17px; height: 17px; }
    .shot-name { font-size: 12px; color: var(--yi-text-muted, #777); }
    .shot-hint { font-size: 12px; color: var(--yi-text-muted, #999); }
    .shot-box { display: inline-flex; align-items: center; gap: 9px; margin-top: 10px;
                border: 1px solid var(--yi-border, #ccc); border-radius: 10px; padding: 6px 10px 6px 6px; }
    .shot-thumb { width: 54px; height: 40px; object-fit: cover; border-radius: 6px; display: block; }
    .audio-row { display: flex; align-items: center; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
    .audio-row .file-btn mat-icon { font-size: 17px; width: 17px; height: 17px; }
    .audio-info { font-size: 13px; line-height: 1.6; border: 1px dashed var(--yi-border, #bbb);
                  border-radius: 10px; padding: 9px 12px; margin: 4px 0 10px; }
    .pv-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px 14px; margin: 8px 0; }
    .pv-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--yi-text-muted, #777); }
    .sec { font-size: 12px; font-weight: 700; color: var(--yi-text-muted, #777); margin: 14px 0 6px; }
    .targets { display: flex; flex-direction: column; gap: 6px; }
    .tgt { display: flex; align-items: center; gap: 8px; font-size: 13.5px; cursor: pointer; }
    .check-row { display: flex; align-items: center; gap: 8px; font-size: 13px; margin: 8px 0; cursor: pointer; }
    .warn { background: #fff7e0; border: 1px solid #e6c86e; color: #6d5410; border-radius: 8px;
            padding: 8px 12px; font-size: 12.5px; }
    .err { color: #b3261e; font-size: 12.5px; }
    .ok-row { font-size: 13px; color: #2e7d32; }
    .link-btn { border: none; background: none; color: var(--yi-accent, #3f51b5); cursor: pointer;
                font-size: 13px; text-decoration: underline; padding: 0; }
  `]
})
export class IntakePanelComponent {
  readonly entry = input.required<InterviewDetail>();
  @Output() readonly saved = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly categories = computed(() =>
    CATEGORY_KEYS.map(key => ({ key, label: this.t('pb.v2.cat.' + key) })));
  readonly typeTabs = computed<{ key: IntakeType; label: string }[]>(() => [
    { key: 'email', label: this.t('pb.v2.i.typeEmail') },
    { key: 'transcript', label: this.t('pb.v2.i.typeTranscript') },
    { key: 'note', label: this.t('pb.v2.i.typeNote') }
  ]);

  readonly open = signal(false);
  readonly step = signal<'input' | 'preview'>('input');
  readonly type = signal<IntakeType>('email');
  readonly title = signal('');
  readonly text = signal('');
  readonly shotFile = signal<File | null>(null);
  readonly shotName = signal('');
  readonly shotPreviewUrl = signal<string | null>(null);
  readonly audioFile = signal<File | null>(null);
  readonly audioName = computed(() => this.audioFile()?.name ?? '');

  // 预览字段
  readonly category = signal('invitation');
  readonly fSubject = signal('');
  readonly fFrom = signal('');
  readonly fDate = signal('');
  readonly fWhen = signal('');
  readonly fInterviewers = signal('');
  readonly fFormat = signal('');
  readonly segments = signal<TranscriptSegment[]>([]);
  readonly hasArrangement = signal(false);
  readonly arrDate = signal('');
  readonly arrTime = signal('');
  readonly arrStage = signal('');
  readonly target = signal<Target>({ kind: 'comm' });
  readonly nrDate = signal('');
  readonly nrTime = signal('');
  readonly nrStage = signal('');
  readonly nrInterviewers = signal('');
  readonly nrFormat = signal('');

  readonly busy = signal(false);
  /** 「添加到本轮」快捷入口的显式预设轮次:解析建议不得覆盖它。 */
  readonly presetRoundId = signal<string | null>(null);
  readonly errorMsg = signal<string | null>(null);
  readonly duplicate = signal(false);

  readonly rounds = computed(() =>
    [...(this.entry().rounds ?? [])].sort((a, b) => a.order - b.order));

  speakerNames(): string {
    return joinNames(this.i18n, [...new Set(this.segments().map(s => s.speaker))]);
  }

  isTargetRound(id: string): boolean {
    const t = this.target();
    return t.kind === 'round' && t.roundId === id;
  }

  /** 父组件调用:打开面板,可预置类型与目标轮次。 */
  openPanel(type: IntakeType = 'email', roundId?: string): void {
    this.type.set(type);
    this.step.set('input');
    this.errorMsg.set(null);
    this.presetRoundId.set(roundId ?? null);
    this.target.set(roundId ? { kind: 'round', roundId } : { kind: 'comm' });
    this.open.set(true);
  }

  close(): void { this.open.set(false); }

  setType(t: IntakeType): void {
    this.type.set(t);
    this.step.set('input');
  }

  onShot(ev: Event): void {
    const f = (ev.target as HTMLInputElement).files?.[0] ?? null;
    if (f) this.setShot(f);
    (ev.target as HTMLInputElement).value = '';
  }

  /**
   * 截图附件统一入口(文件选择与粘贴共用同一份状态/保存路径):
   * 附件只留一张,后来的替换先前的并给提示;缩略图用 object URL 即时预览。
   */
  setShot(f: File): void {
    const replaced = !!this.shotFile();
    if (this.shotPreviewUrl()) URL.revokeObjectURL(this.shotPreviewUrl()!);
    this.shotFile.set(f);
    this.shotName.set(f.name);
    this.shotPreviewUrl.set(URL.createObjectURL(f));
    if (replaced) {
      this.snack.open(this.t('pb.v2.i.shotReplaced'), this.t('common.close'), { duration: 2500 });
    }
  }

  clearShot(): void {
    if (this.shotPreviewUrl()) URL.revokeObjectURL(this.shotPreviewUrl()!);
    this.shotPreviewUrl.set(null);
    this.shotFile.set(null);
    this.shotName.set('');
  }

  /**
   * 面板内粘贴(定稿:截图可"粘"进来):剪贴板带图片 → 接住作截图附件并
   * preventDefault;纯文字不拦截,保持默认粘贴落入当前聚焦的输入框/文本域。
   * 事件绑在面板根节点,焦点在面板内任意位置都接得住图片。
   */
  onPaste(ev: ClipboardEvent): void {
    const items = ev.clipboardData?.items;
    let raw: File | null = null;
    if (items) {
      for (let i = 0; i < items.length; i++) {
        if (items[i].type.startsWith('image/')) { raw = items[i].getAsFile(); break; }
      }
    }
    if (!raw) return;
    ev.preventDefault();
    const ext = (raw.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
    const d = new Date();
    const p = (n: number) => String(n).padStart(2, '0');
    const name = `${this.t('pb.v2.i.clipName')}-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}` +
      `-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}.${ext}`;
    this.setShot(new File([raw], name, { type: raw.type }));
  }

  onAudio(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0] ?? null;
    if (f) this.audioFile.set(f);
    input.value = '';
  }

  removeAudio(): void { this.audioFile.set(null); }

  audioSizeText(): string {
    return fmtFileSize(this.audioFile()?.size ?? 0);
  }

  /** 录音模式:转写类型 + 已选录音 —— 与粘贴文本二选一,录音优先。 */
  private isAudioMode(): boolean {
    return this.type() === 'transcript' && !!this.audioFile();
  }

  // ---------------- 解析 ----------------

  parse(): void {
    const raw = this.text();
    this.errorMsg.set(null);
    this.duplicate.set(false);
    if (this.isAudioMode()) {
      // 录音没有可解析的文本:预览只呈现文件信息 + 归属,不假装已解析出内容。
      this.segments.set([]);
      if (!this.fDate()) this.fDate.set(todayStr());
      this.step.set('preview');
      return;
    }
    if (this.type() === 'email') this.parseEmail(raw);
    else if (this.type() === 'transcript') this.parseTranscript(raw);
    else this.parseNote(raw);
    this.step.set('preview');
  }

  private parseEmail(raw: string): void {
    const p = parsePastedEmail(raw);
    this.fSubject.set(p.subject ?? '');
    this.fFrom.set(p.from ?? '');
    this.fDate.set(p.date ? (normalizeEmailDate(p.date) || '') : '');
    this.category.set(classifyEmail(p.subject ?? '', p.body ?? raw));
    const when = extractInterviewWhen(p.body ?? raw);
    this.fWhen.set(when ?? '');
    const names = extractInterviewerNames(p.body ?? raw);
    this.fInterviewers.set(names.join(', '));
    this.fFormat.set(extractFormat(p.body ?? raw));
    // 建议归属:面试日期命中某轮 → 该轮;否则邀约类建议新建/沟通,其余进沟通记录。
    // 但显式预设(材料页签「添加到本轮」)优先于内容建议,不许被解析覆盖。
    const datePart = (when ?? '').slice(0, 10);
    const hit = this.rounds().find(r => r.scheduledDate && r.scheduledDate === datePart);
    if (this.presetRoundId()) {
      this.target.set({ kind: 'round', roundId: this.presetRoundId()! });
    } else if (hit) this.target.set({ kind: 'round', roundId: hit.id });
    else if (this.category() === 'invitation' && when) {
      this.target.set({ kind: 'new' });
      this.nrDate.set(datePart);
      this.nrTime.set((when ?? '').slice(11, 16));
      this.nrStage.set('');
      this.nrInterviewers.set(names.join(', '));
      this.nrFormat.set(this.fFormat());
    } else this.target.set({ kind: 'comm' });
    // 重复检测:发件人+日期+主题
    const key = dedupeKey(p.from ?? '', this.fDate(), p.subject ?? '');
    const dup = (this.entry().assets ?? []).some(a => {
      try {
        const m = JSON.parse(a.metaJson ?? '{}') as AssetMeta;
        return m.dedupeKey === key;
      } catch { return false; }
    });
    this.duplicate.set(dup);
  }

  private parseTranscript(raw: string): void {
    this.segments.set(parseSegments(raw));
    this.fDate.set(todayStr());
    const dateHit = this.rounds().find(r => r.scheduledDate === this.fDate());
    if (this.target().kind === 'comm' && dateHit) {
      this.target.set({ kind: 'round', roundId: dateHit.id });
    }
  }

  private parseNote(_raw: string): void {
    this.fDate.set(todayStr());
  }

  // ---------------- 保存 ----------------

  async save(): Promise<void> {
    this.busy.set(true);
    this.errorMsg.set(null);
    try {
      const entry = this.entry();
      let roundId: string | null = null;
      const t = this.target();
      if (t.kind === 'round') roundId = t.roundId;
      if (t.kind === 'new') {
        const created = await this.api.addRound(entry.id, {
          stage: this.nrStage().trim() || guessStage(this.category(), entry),
          scheduledDate: this.nrDate() || null,
          scheduledTime: this.nrTime() || null,
          interviewers: this.nrInterviewers() || null,
          format: this.nrFormat() || null
        }).toPromise();
        roundId = created!.id;
      }

      if (this.isAudioMode()) {
        // 录音与材料页签走同一个上传端点/资产类型(Audio),落到所选归属,
        // 保存后由转写管线处理(实录页签可见),这里不生成任何文本资产。
        // 同附的截图照常同归属落库(独立资产,不与录音做 meta 关联)。
        const shot0 = this.shotFile();
        if (shot0) {
          await this.api.uploadFile(entry.id, shot0,
            { roundId: roundId ?? '', phase: '' }).toPromise();
        }
        await this.api.uploadFile(entry.id, this.audioFile()!,
          { roundId: roundId ?? '', phase: roundId ? 'Post' : '' }).toPromise();
        this.finishSave();
        return;
      }

      // 截图先传(原图作为附件资产,归到同一目标)
      let shotId: string | undefined;
      const shot = this.shotFile();
      if (shot) {
        const up = await this.api.uploadFile(entry.id, shot,
          { roundId: roundId ?? '', phase: '' }).toPromise();
        shotId = up?.id;
      }

      const meta = this.buildMeta(shotId);
      const raw = this.text();
      const kind = this.type() === 'email' ? 'Text'
        : this.type() === 'transcript' ? 'Transcript' : 'Notes';
      const fileName = this.title().trim()
        || (this.type() === 'email' ? (this.fSubject() || this.t('pb.v2.i.typeEmail')) : '')
        || (this.type() === 'transcript' ? this.t('pb.v2.i.nameTranscript') : this.t('pb.v2.i.typeNote'));
      const segs = this.type() === 'transcript' ? this.segments() : [];
      const attached = await this.api.attachAsset(entry.id, {
        kind,
        fileName,
        transcriptText: raw,
        transcriptSegmentsJson: segs.length ? JSON.stringify(segs) : null,
        roundId,
        phase: roundId ? (this.type() === 'email' ? 'Pre' : 'Post') : null,
        metaJson: JSON.stringify(meta)
      }).toPromise();
      const assetId = attached!.id;

      // 沟通目标且条目关联了投递:同源镜像到 Jobs 沟通时间线(Tracker 同表)。
      if (!roundId && entry.jobApplicationId) {
        const comm = await this.api.createCommunication(entry.jobApplicationId, {
          type: this.type() === 'email' ? 'Email' : this.type() === 'transcript' ? 'Call' : 'Note',
          subject: this.type() === 'email' ? (this.fSubject() || null) : (this.title().trim() || null),
          content: raw,
          contactName: this.fFrom() || this.fInterviewers() || null,
          occurredAt: new Date(`${this.fDate() || todayStr()}T12:00:00`).toISOString(),
          metaJson: JSON.stringify({ ...meta, assetId }),
          handlingStatus: meta.arrangement ? 'Pending' : null
        }).toPromise();
        if (comm?.id) {
          await this.api.updateAssetMeta(entry.id, assetId,
            { ...meta, communicationId: comm.id }).toPromise().catch(() => undefined);
        }
      }

      this.finishSave();
    } catch (e) {
      this.errorMsg.set(this.fmt('pb.v2.i.saveFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.i.unknownError') }));
    } finally {
      this.busy.set(false);
    }
  }

  /** 保存成功后的统一收尾(文本/录音两条路径共用)。 */
  private finishSave(): void {
    this.snack.open(this.t('pb.v2.d.saved'), this.t('common.close'), { duration: 2500 });
    this.text.set('');
    this.title.set('');
    this.clearShot();
    this.audioFile.set(null);
    this.open.set(false);
    this.step.set('input');
    this.saved.emit();
  }

  private buildMeta(shotId?: string): AssetMeta {
    const names = this.fInterviewers().split(/[,、,]/).map(s => s.trim()).filter(Boolean);
    const meta: AssetMeta = {
      channel: this.type() === 'email' ? 'email'
        : this.type() === 'transcript' ? 'call' : 'note',
      date: this.fDate() || undefined,
      interviewers: names.length ? names : undefined,
      screenshotAssetId: shotId
    };
    if (this.type() === 'email') {
      meta.category = this.category();
      meta.subject = this.fSubject() || undefined;
      meta.from = this.fFrom() || undefined;
      meta.interviewAt = this.fWhen() || undefined;
      meta.format = this.fFormat() || undefined;
      meta.dedupeKey = dedupeKey(meta.from ?? '', meta.date ?? '', meta.subject ?? '');
      if (this.category() === 'invitation' && this.fWhen()) {
        meta.arrangement = {
          date: this.fWhen().slice(0, 10),
          time: this.fWhen().slice(11, 16) || undefined,
          interviewers: names.join(', ') || undefined,
          format: this.fFormat() || undefined
        };
      }
    }
    if (this.hasArrangement()) {
      meta.arrangement = {
        date: this.arrDate() || undefined,
        time: this.arrTime() || undefined,
        stage: this.arrStage() || undefined,
        interviewers: names.join(', ') || undefined
      };
      meta.handlingStatus = 'Pending';
    }
    return meta;
  }
}

// ---------------- 本地解析小工具 ----------------

function todayStr(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function dedupeKey(from: string, date: string, subject: string): string {
  return `${from.trim().toLowerCase()}|${date.trim()}|${subject.trim().toLowerCase()}`;
}

export function classifyEmail(subject: string, body: string): string {
  const s = `${subject}\n${body}`.toLowerCase();
  if (/reschedule|postpone|改期|重新安排|another time/.test(s)) return 'reschedule';
  if (/regret|unfortunately|not (be )?moving forward|未通过|很抱歉|遗憾地通知|other candidates/.test(s)) return 'rejection';
  if (/invit|interview|schedule|安排面试|邀请您参加|book your|confirm your interview/.test(s)) return 'invitation';
  if (/follow ?up|checking in|跟进|想确认|touching base/.test(s)) return 'followup';
  return 'other';
}

/** 从正文里找面试时间:优先 "Oct 7, 2026 at 11:00 AM" / "2026-10-07 11:00" 一类明确写法。 */
export function extractInterviewWhen(text: string): string | null {
  const iso = text.match(/(20\d{2})[-/](\d{1,2})[-/](\d{1,2})[ T](\d{1,2}):(\d{2})/);
  if (iso) {
    const p = (n: string) => n.padStart(2, '0');
    return `${iso[1]}-${p(iso[2])}-${p(iso[3])} ${p(iso[4])}:${iso[5]}`;
  }
  const months: Record<string, number> = {
    jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6, jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12
  };
  const en = text.match(/(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(20\d{2})[^0-9]{0,12}?(\d{1,2}):(\d{2})\s*(AM|PM)?/i);
  if (en) {
    const mon = months[en[1].slice(0, 3).toLowerCase()];
    let hh = Number(en[4]);
    if (en[6]?.toUpperCase() === 'PM' && hh < 12) hh += 12;
    if (en[6]?.toUpperCase() === 'AM' && hh === 12) hh = 0;
    const p = (n: number) => String(n).padStart(2, '0');
    return `${en[3]}-${p(mon)}-${p(Number(en[2]))} ${p(hh)}:${en[5]}`;
  }
  return null;
}

function extractFormat(text: string): string {
  if (/jdoodle/i.test(text)) return '线上做题平台';
  if (/teams|zoom|google meet|video call/i.test(text)) return '线上视频';
  if (/phone call|电话沟通/i.test(text)) return '电话';
  if (/on-?site|in person|现场/i.test(text)) return '现场';
  return '';
}

function extractInterviewerNames(text: string): string[] {
  // 保守提取:只认 "with X Y" / "面试官:X" 这类明确句式,认不到就留空让用户补。
  const out = new Set<string>();
  for (const m of text.matchAll(/(?:面试官|interviewer)[:：]\s*([A-Za-z][A-Za-z .'-]{1,40})/gi)) {
    out.add(m[1].trim());
  }
  for (const m of text.matchAll(/with\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,2})\s*[,.(]/g)) {
    out.add(m[1].trim());
  }
  return [...out].slice(0, 4);
}

function guessStage(category: string, _entry: InterviewDetail): string {
  return category === 'invitation' ? 'Interview' : 'Screen';
}

/** 把粘贴的转写文本切段:「说话人:内容」逐行;认不出说话人的行并入上一段。 */
export function parseSegments(raw: string): TranscriptSegment[] {
  const segs: TranscriptSegment[] = [];
  for (const line of raw.split(/\r?\n/)) {
    const t = line.trim();
    if (!t) continue;
    const m = t.match(/^([^:：]{1,40})[:：]\s*(.+)$/);
    if (m && !/^(https?|时间|日期|主题|subject|date|from|to)$/i.test(m[1].trim())) {
      segs.push({ speaker: m[1].trim(), text: m[2].trim() });
    } else if (segs.length) {
      segs[segs.length - 1].text += ' ' + t;
    } else {
      segs.push({ speaker: '我', text: t });
    }
  }
  // 合并相邻同说话人段落(粘贴文本常按行断句)
  const merged: TranscriptSegment[] = [];
  for (const s of segs) {
    const last = merged[merged.length - 1];
    if (last && last.speaker === s.speaker) last.text += ' ' + s.text;
    else merged.push({ ...s });
  }
  return merged;
}

function fmtFileSize(bytes: number): string {
  if (!bytes) return '0 KB';
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
