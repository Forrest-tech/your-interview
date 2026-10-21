import {
  Component, DestroyRef, EventEmitter, Output, computed, inject, input, signal
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
import { derivePhase, joinNames, makeV2Fmt, makeV2T } from '../v2.helpers';

export type IntakeType = 'email' | 'transcript' | 'note';
/** 「录音·转写文本」类型内部的二选一模式:一次只呈现/保存一种来源。 */
export type TranscriptMode = 'upload' | 'text';
type Target = { kind: 'round'; roundId: string } | { kind: 'comm' } | { kind: 'new' };

const CATEGORY_KEYS = ['invitation', 'reschedule', 'rejection', 'followup', 'other'];

/**
 * 条目级统一入口(2026-10-20 定稿):
 * 类型只有三种 —— 邮件 / 录音·转写文本 / 备注(电话记录写进后两种)。
 * 「录音·转写文本」内部是明确二选一(2026-10-21 用户强调):分段切换
 * 「上传录音」|「粘贴转写文本」,一次只显示一种输入、保存只取当前模式的来源;
 * 切换模式不清空另一模式的内容,粘贴音频/文本会自动对齐到对应模式。
 * 流程:粘贴 → 解析预览(先分类再提取,可改)→ 归属确认(系统按面试官/日期建议,
 * 可改;选新建轮次且有时间则建轮次并进应用内日历)→ 保存。
 * 面板不设标题输入框:标题一律保存时自动生成 —— 邮件取主题(无主题回落
 * 发件人/首行)、录音取文件名、转写取首段说话人行、备注取首行(见 autoFileName)。
 * 任何类型都可附一张截图原图 —— 截图是附件,不是类型;进入方式只有粘贴:
 * 直接粘进文本框,缩略图在框内底部呈现,没有单独的截图文件按钮。
 */
@Component({
  selector: 'app-intake-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (open()) {
      <section class="panel" data-testid="intake-panel">
        <ng-template #shotTpl>
          @if (shotFile()) {
            <div class="shot-strip" data-testid="shot-box">
              <img class="shot-thumb" [src]="shotPreviewUrl()" [alt]="shotName()">
              <span class="shot-name">{{ shotName() }} · {{ shotSizeText() }}</span>
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
            <p class="hint">{{ transcriptMode() === 'upload' ? t('pb.v2.i.hintTranscriptUpload') : t('pb.v2.i.hintTranscriptText') }}</p>
          } @else {
            <p class="hint">{{ t('pb.v2.i.hintNote') }}</p>
          }
          @if (type() === 'transcript') {
            <div class="type-tabs mode-tabs" role="tablist" data-testid="transcript-mode-tabs">
              <button type="button" role="tab" class="ttab" data-testid="mode-upload"
                      [class.on]="transcriptMode() === 'upload'"
                      (click)="setTranscriptMode('upload')">{{ t('pb.v2.i.modeUpload') }}</button>
              <button type="button" role="tab" class="ttab" data-testid="mode-text"
                      [class.on]="transcriptMode() === 'text'"
                      (click)="setTranscriptMode('text')">{{ t('pb.v2.i.modeText') }}</button>
            </div>
          }
          @if (type() !== 'transcript' || transcriptMode() === 'text') {
            <div class="paste-box">
              <textarea class="paste" rows="9"
                        [placeholder]="type() === 'email' ? t('pb.v2.i.pasteEmailPh') : type() === 'transcript' ? t('pb.v2.i.pasteTranscriptPh') : t('pb.v2.i.pasteNotePh')"
                        [ngModel]="text()" (ngModelChange)="text.set($event)"></textarea>
              <ng-container [ngTemplateOutlet]="shotTpl"></ng-container>
            </div>
          }
          @if (type() === 'transcript' && transcriptMode() === 'upload') {
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
            <ng-container [ngTemplateOutlet]="shotTpl"></ng-container>
          }
          <div class="row">
            <span class="spacer"></span>
            <button mat-stroked-button (click)="close()">{{ t('common.cancel') }}</button>
            <button mat-raised-button color="primary" [disabled]="!canParse() || busy()"
                    (click)="parse()">{{ t('pb.v2.i.parsePreview') }}</button>
          </div>
        } @else {
          <!-- 解析预览 -->
          <div class="preview" data-testid="intake-preview">
            @if (previewAudio()) {
              <p class="audio-info" data-testid="intake-audio-info">
                {{ t('pb.v2.i.sourceAudio') }}<br>
                {{ fmt('pb.v2.i.audioInfo', { name: audioName(), size: audioSizeText() }) }}<br>
                {{ t('pb.v2.i.audioWillTranscribe') }}
              </p>
            }
            @if (type() === 'transcript' && transcriptMode() === 'text') {
              <p class="hint" data-testid="intake-source-text">{{ t('pb.v2.i.sourceText') }}</p>
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
    /* 字号对齐网站统一四档(与 Tracker/详情页一致):标题 20/600、正文 14/400
       行高 1.5、次要标签 12/700、次要说明 13/400、按钮 14/500。 */
    .panel-head h3 { margin: 0; font-size: 20px; font-weight: 600; line-height: 1.3; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .type-tabs { display: flex; gap: 6px; margin: 10px 0; flex-wrap: wrap; }
    .mode-tabs { margin: 4px 0 2px; }
    .ttab { border: 1px solid var(--yi-border, #ccc); background: transparent; color: inherit;
            border-radius: 999px; padding: 6px 14px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .ttab.on { background: var(--yi-accent, #3f51b5); color: #fff; border-color: transparent; }
    .hint { font-size: 13px; line-height: 1.5; color: var(--yi-text-muted, #777); margin: 6px 0; }
    .fld { width: 100%; border: 1px solid var(--yi-border, #ccc); border-radius: 8px;
           padding: 8px 10px; font-size: 14px; background: transparent; color: inherit; box-sizing: border-box; }
    /* 文本框与粘入的截图同属一个带边框容器:截图不是框外附件行,
       而是直接粘在框里 —— 缩略图条出现在框内底部。 */
    .paste-box { border: 1px solid var(--yi-border, #ccc); border-radius: 10px; margin-top: 8px; overflow: hidden; }
    .paste-box:focus-within { border-color: var(--yi-accent, #3f51b5); }
    .paste { width: 100%; border: none; border-radius: 0; padding: 10px 12px;
             font-size: 14px; line-height: 1.5; background: transparent; color: inherit;
             box-sizing: border-box; resize: vertical; display: block; outline: none; }
    .paste-box .shot-strip { display: flex; border: none; border-top: 1px solid var(--yi-border, #e3e3e3);
                             border-radius: 0; margin-top: 0; padding: 6px 8px; }
    .row { display: flex; align-items: center; gap: 10px; margin-top: 12px; flex-wrap: wrap; }
    .spacer { flex: 1; }
    .file-btn { display: inline-flex; align-items: center; gap: 6px; font-size: 14px; font-weight: 500; cursor: pointer;
                border: 1px dashed var(--yi-border, #bbb); border-radius: 8px; padding: 6px 10px; }
    .file-btn mat-icon { font-size: 17px; width: 17px; height: 17px; }
    .shot-name { font-size: 13px; color: var(--yi-text-muted, #777); }
    .shot-strip { display: inline-flex; align-items: center; gap: 9px; margin-top: 10px;
                  border: 1px solid var(--yi-border, #ccc); border-radius: 10px; padding: 6px 10px 6px 6px; }
    .shot-thumb { width: 54px; height: 40px; object-fit: cover; border-radius: 6px; display: block; }
    .audio-row { display: flex; align-items: center; gap: 10px; margin-top: 10px; flex-wrap: wrap; }
    .audio-row .file-btn mat-icon { font-size: 17px; width: 17px; height: 17px; }
    .audio-info { font-size: 14px; line-height: 1.5; border: 1px dashed var(--yi-border, #bbb);
                  border-radius: 10px; padding: 9px 12px; margin: 4px 0 10px; }
    .pv-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 10px 14px; margin: 8px 0; }
    .pv-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--yi-text-muted, #777); }
    .sec { font-size: 12px; font-weight: 700; color: var(--yi-text-muted, #777); margin: 14px 0 6px; }
    .targets { display: flex; flex-direction: column; gap: 6px; }
    .tgt { display: flex; align-items: center; gap: 8px; font-size: 14px; cursor: pointer; }
    .check-row { display: flex; align-items: center; gap: 8px; font-size: 14px; margin: 8px 0; cursor: pointer; }
    .warn { background: #fff7e0; border: 1px solid #e6c86e; color: #6d5410; border-radius: 8px;
            padding: 8px 12px; font-size: 13px; line-height: 1.5; }
    .err { color: #b3261e; font-size: 13px; }
    .ok-row { font-size: 14px; color: #2e7d32; }
    .link-btn { border: none; background: none; color: var(--yi-accent, #3f51b5); cursor: pointer;
                font-size: 14px; text-decoration: underline; padding: 0; }
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

  /**
   * 文档级粘贴监听(仅面板打开期间挂载,关闭/销毁即摘除):用户打开面板后
   * 往往直接 Ctrl+V,焦点还在 body 上 —— 绑在面板节点上的监听收不到事件,
   * 必须在 document 上接,再按剪贴板内容分发(见 onPaste)。
   */
  private readonly docPasteHandler = (ev: ClipboardEvent) => this.onPaste(ev);
  private docPasteOn = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.setDocPaste(false));
  }

  private setDocPaste(on: boolean): void {
    if (on === this.docPasteOn) return;
    this.docPasteOn = on;
    if (on) document.addEventListener('paste', this.docPasteHandler);
    else document.removeEventListener('paste', this.docPasteHandler);
  }

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
  /** 录音类型二选一模式,默认文本(主流程是粘转写);切换不清空另一模式已填内容。 */
  readonly transcriptMode = signal<TranscriptMode>('text');
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
    this.transcriptMode.set('text');
    this.step.set('input');
    this.errorMsg.set(null);
    this.presetRoundId.set(roundId ?? null);
    this.target.set(roundId ? { kind: 'round', roundId } : { kind: 'comm' });
    this.open.set(true);
    this.setDocPaste(true);
  }

  close(): void {
    this.open.set(false);
    this.setDocPaste(false);
  }

  setType(t: IntakeType): void {
    this.type.set(t);
    this.step.set('input');
  }

  /**
   * 截图附件入口(只有粘贴一条路,没有文件选择按钮):附件只留一张,后来的
   * 替换先前的并给提示;缩略图用 object URL 即时预览,呈现在文本框容器内底部。
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
   * 粘贴分发(文档级监听,面板打开期间焦点在页面任意位置都接得住):
   * - 剪贴板带图片文件 → 接住作截图附件,缩略图呈现在文本框容器内(两模式通用);
   * - 带音频文件 → 「录音·转写」类型下接住作录音附件并自动切到「上传录音」模式
   *   (与 Upload a recording 同一状态/保存路径);其它类型下不接,只提示去录音类型粘;
   * - 纯文字 → 原则上不拦截:聚焦在输入框/文本域时浏览器默认粘贴照常落入该字段,
   *   无字段聚焦时默认粘贴本就无处可落,面板不代填、不吞字。唯一例外:录音类型当前
   *   在「上传录音」模式且转写文本为空、焦点又不在任何字段上时,自动切到
   *   「粘贴转写文本」模式并把文字落进文本框 —— 二选一的来源由粘贴内容自动对齐;
   * - 其它文件 → 忽略。
   */
  onPaste(ev: ClipboardEvent): void {
    if (!this.open()) return;
    const file = firstClipboardFile(ev.clipboardData);
    if (!file) {
      this.maybeAutoSwitchTextPaste(ev);
      return;
    }
    if (file.type.startsWith('image/')) {
      ev.preventDefault();
      const ext = (file.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
      const d = new Date();
      const p = (n: number) => String(n).padStart(2, '0');
      const name = `${this.t('pb.v2.i.clipName')}-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}` +
        `-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}.${ext}`;
      this.setShot(new File([file], name, { type: file.type }));
      return;
    }
    if (isAudioFile(file)) {
      ev.preventDefault();
      if (this.type() === 'transcript') {
        this.setAudio(file);
        this.setTranscriptMode('upload');
      } else {
        this.snack.open(this.t('pb.v2.i.pasteAudioWrongType'), this.t('common.close'), { duration: 3500 });
      }
    }
  }

  /**
   * 纯文字粘贴的自动对齐:录音类型在上传模式、转写文本为空、且焦点不在任何
   * 输入字段上时,切到文本模式并落入粘贴文字;其余情况一律不拦截(见 onPaste 注释)。
   */
  private maybeAutoSwitchTextPaste(ev: ClipboardEvent): void {
    if (this.type() !== 'transcript' || this.transcriptMode() !== 'upload') return;
    if (this.text().trim()) return;
    const ae = document.activeElement as HTMLElement | null;
    if (ae && (ae.tagName === 'INPUT' || ae.tagName === 'TEXTAREA' || ae.isContentEditable)) return;
    const clip = (ev.clipboardData?.getData('text/plain') ?? '').trim();
    if (!clip) return;
    ev.preventDefault();
    this.text.set(ev.clipboardData!.getData('text/plain'));
    this.setTranscriptMode('text');
  }

  /** 录音附件统一入口(文件选择与粘贴共用同一份状态/保存路径)。 */
  setAudio(f: File): void {
    this.audioFile.set(f);
  }

  async onAudio(ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0] ?? null;
    if (f) this.setAudio(await detachFile(f));
    input.value = '';
  }

  removeAudio(): void { this.audioFile.set(null); }

  shotSizeText(): string {
    return fmtFileSize(this.shotFile()?.size ?? 0);
  }

  audioSizeText(): string {
    return fmtFileSize(this.audioFile()?.size ?? 0);
  }

  /** 目标轮次的日期(phase 推导用):已有轮次取其 scheduledDate,新建轮次取表单日期。 */
  private roundDateOf(roundId: string, t: Target): string | null {
    if (t.kind === 'new') return this.nrDate() || null;
    return this.rounds().find(r => r.id === roundId)?.scheduledDate ?? null;
  }

  /** 当前是否处于「上传录音」模式(仅录音类型下有意义):呈现、解析、保存都只认它。 */
  readonly inUploadMode = computed(() =>
    this.type() === 'transcript' && this.transcriptMode() === 'upload');
  /** 预览步是否呈现录音来源(上传模式且已附录音)。 */
  readonly previewAudio = computed(() => this.inUploadMode() && !!this.audioFile());

  /** 录音模式判定:转写类型 + 当前选中「上传录音」—— 与文本二选一,只取当前模式。 */
  private isAudioMode(): boolean {
    return this.inUploadMode();
  }

  /** 「解析预览」可用性:上传模式要有录音,其余情况要有文本。 */
  canParse(): boolean {
    if (this.type() === 'transcript') {
      return this.transcriptMode() === 'upload' ? !!this.audioFile() : !!this.text().trim();
    }
    return !!this.text().trim();
  }

  /** 二选一切换:只切呈现与保存来源,不清空另一模式已填的内容(防误切丢失)。 */
  setTranscriptMode(m: TranscriptMode): void {
    this.transcriptMode.set(m);
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
        // phase 与材料展示推导同一规则(derivePhase):按发生日期 vs 轮次日期。
        const audioPhase = roundId
          ? derivePhase(false, this.fDate() || todayStr(), this.roundDateOf(roundId, t)) : '';
        const shot0 = this.shotFile();
        if (shot0) {
          await this.api.uploadFile(entry.id, shot0,
            { roundId: roundId ?? '', phase: audioPhase }).toPromise();
        }
        await this.api.uploadFile(entry.id, this.audioFile()!,
          { roundId: roundId ?? '', phase: audioPhase }).toPromise();
        this.finishSave();
        return;
      }

      // phase 落值与展示推导同一规则(derivePhase):材料日期 vs 轮次日期,
      // 不再写死"邮件一律 Pre"—— 面试后的反馈邮件要落到 After。
      const matPhase = roundId
        ? derivePhase(this.type() === 'email', this.fDate() || todayStr(), this.roundDateOf(roundId, t))
        : null;

      // 截图先传(原图作为附件资产,归到同一目标,phase 与主材料一致)
      let shotId: string | undefined;
      const shot = this.shotFile();
      if (shot) {
        const up = await this.api.uploadFile(entry.id, shot,
          { roundId: roundId ?? '', phase: matPhase ?? '' }).toPromise();
        shotId = up?.id;
      }

      const meta = this.buildMeta(shotId);
      const raw = this.text();
      const kind = this.type() === 'email' ? 'Text'
        : this.type() === 'transcript' ? 'Transcript' : 'Notes';
      const fileName = this.autoFileName(raw);
      const segs = this.type() === 'transcript' ? this.segments() : [];
      const attached = await this.api.attachAsset(entry.id, {
        kind,
        fileName,
        transcriptText: raw,
        transcriptSegmentsJson: segs.length ? JSON.stringify(segs) : null,
        roundId,
        phase: matPhase,
        metaJson: JSON.stringify(meta)
      }).toPromise();
      const assetId = attached!.id;

      // 沟通目标且条目关联了投递:同源镜像到 Jobs 沟通时间线(Tracker 同表)。
      if (!roundId && entry.jobApplicationId) {
        const comm = await this.api.createCommunication(entry.jobApplicationId, {
          type: this.type() === 'email' ? 'Email' : this.type() === 'transcript' ? 'Call' : 'Note',
          subject: this.type() === 'email' ? (this.fSubject() || null) : fileName,
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
    this.clearShot();
    this.audioFile.set(null);
    this.open.set(false);
    this.setDocPaste(false);
    this.step.set('input');
    this.saved.emit();
  }

  /**
   * 自动标题(面板无标题框,保存时一律自动生成):
   * 邮件 → 主题,无主题回落发件人、再回落正文首行;转写 → 首段「说话人:原文」
   * (无分段时回落首行);备注 → 首行。都截到 60 字防材料名过长;全空时用类型名兜底,
   * 保证落库 fileName 非空(后端 FileName NotEmpty)。
   */
  private autoFileName(raw: string): string {
    if (this.type() === 'email') {
      return trimSummary(this.fSubject())
        || trimSummary(this.fFrom())
        || firstLineSummary(raw)
        || this.t('pb.v2.i.typeEmail');
    }
    if (this.type() === 'transcript') {
      const segs = this.segments();
      const first = segs.length ? `${segs[0].speaker}: ${segs[0].text}` : '';
      return trimSummary(first) || firstLineSummary(raw) || this.t('pb.v2.i.nameTranscript');
    }
    return firstLineSummary(raw) || this.t('pb.v2.i.typeNote');
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

/** 自动标题截断:折叠空白,超过 60 字截断加省略号;空串原样返回(供回落链判断)。 */
function trimSummary(s: string): string {
  const t = (s ?? '').replace(/\s+/g, ' ').trim();
  return t.length > 60 ? t.slice(0, 60).trimEnd() + '…' : t;
}

/** 正文首个非空行作摘要(转写/备注/邮件兜底的自动标题)。 */
function firstLineSummary(raw: string): string {
  const line = (raw ?? '').split(/\r?\n/).map(s => s.trim()).find(s => s.length > 0) ?? '';
  return trimSummary(line);
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

/**
 * 把文件选择拿到的 File 立刻读进内存另存一份独立 File。
 * input 的 File 内容与该 input 元素绑定:切到解析预览步后 input 被销毁,
 * 保存时再序列化原 File 只剩 0 字节(后端报"没有收到文件")—— 实测教训。
 * 粘贴来的 File 来自 DataTransfer、不绑元素,无需此处理。
 */
async function detachFile(f: File): Promise<File> {
  const buf = await f.arrayBuffer();
  return new File([buf], f.name, { type: f.type, lastModified: f.lastModified });
}

function fmtFileSize(bytes: number): string {
  if (!bytes) return '0 KB';
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/** 剪贴板里的第一个文件(files 列表优先,其次 items 的 file 项);纯文字粘贴返回 null。 */
function firstClipboardFile(cd: DataTransfer | null): File | null {
  if (!cd) return null;
  if (cd.files && cd.files.length) return cd.files[0];
  for (let i = 0; i < cd.items.length; i++) {
    if (cd.items[i].kind === 'file') {
      const f = cd.items[i].getAsFile();
      if (f) return f;
    }
  }
  return null;
}

/** 音频文件判别:MIME 优先,Finder 复制等场景 MIME 可能为空,用扩展名兜底。 */
function isAudioFile(f: File): boolean {
  if (f.type.startsWith('audio/')) return true;
  return /\.(mp3|m4a|wav|webm|ogg)$/i.test(f.name ?? '');
}
