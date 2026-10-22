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
import { PracticeApi } from '../../../../core/api/practice-api.service';
import { AssetMeta, TranscriptSegment } from '../v2.models';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, joinNames, makeV2Fmt, makeV2T, normalizeThreadSubject } from '../v2.helpers';

export type IntakeType = 'email' | 'transcript' | 'note';
/** 「录音·转写文本」类型内部的二选一模式:一次只呈现/保存一种来源。 */
export type TranscriptMode = 'upload' | 'text';
type Target = { kind: 'round'; roundId: string } | { kind: 'comm' } | { kind: 'new' };

/** 一张已粘入的截图(多图共存,定稿第九节):原图文件 + 缩略图 object URL。 */
interface ShotItem { file: File; name: string; url: string; }

/** 批量解析里的一封邮件卡片:逐张 OCR 抽出的字段,预览里可改/可移除/可重试。 */
interface EmailCard {
  id: number;
  shotIndex: number;
  status: 'ok' | 'failed';
  /** 失败时展示的本地化文案键(识别失败/未配置/不支持/未识别出邮件)。 */
  errorKey: string;
  category: string;
  subject: string;
  from: string;
  date: string;
  when: string;
  interviewers: string;
  format: string;
  /** 保存为材料正文的全文(OCR 转写原文,与粘文字时保存 raw 同口径)。 */
  body: string;
  duplicate: boolean;
  /** 实际识别来源(定稿第九节来源标注口径,按实标注):本地共享 OCR / AI 视觉兜底。 */
  source: 'local' | 'vision';
}

/** 预览聚合组:同归一化主题的卡片,组内时间正序。 */
interface EmailCardGroup { key: string; subject: string; cards: EmailCard[]; }

const CATEGORY_KEYS = ['invitation', 'reschedule', 'rejection', 'followup', 'other'];

/**
 * 共享 OCR 服务(票59,平台能力)前端配置 —— 只放可调常量,不写死在调用里:
 * - 超时阈值:本地引擎慢(沙盒单图可达分钟级),超时即按兜底规则回落视觉;
 *   部署机复测延迟后改这一处即可。
 * - 服务基址:默认经网关 /api/ocr;globalThis.__SHARED_OCR_BASE_URL__ 可注入
 *   覆盖(测试/降级模拟把地址指向死端口,验证视觉兜底分支被触发)。
 */
export const SHARED_OCR_TIMEOUT_MS = 180_000;
export const SHARED_OCR_BASE_URL: string =
  (globalThis as { __SHARED_OCR_BASE_URL__?: string }).__SHARED_OCR_BASE_URL__ ?? '/api/ocr';

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
 * 截图是附件,不是类型;进入方式只有粘贴:直接粘进文本框,缩略图在框内
 * 底部呈现,没有单独的截图文件按钮。截图可多张共存(定稿第九节):再粘只
 * 追加不替换,每张可单独移除。Email 类型文字为空时,Parse 对全部截图逐张
 * OCR → 与粘文字同一解析管线出卡片,预览按归一化主题聚合。OCR 通道(票59):
 * 共享 OCR 服务(本地 PaddleOCR,经网关 /api/ocr)为主,服务不可达/超时/
 * 识别失败/解析后卡片为 failed 时回落 AI 网关视觉;预览来源标注按实标注。
 */
@Component({
  selector: 'app-intake-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule],
  template: `
    @if (open()) {
      <section class="panel" data-testid="intake-panel">
        <ng-template #shotTpl>
          @if (shots().length) {
            <div class="shot-strip" data-testid="shot-box">
              @for (s of shots(); track s.url; let i = $index) {
                <span class="shot-item" data-testid="shot-item">
                  <img class="shot-thumb" [src]="s.url" [alt]="s.name">
                  <span class="shot-name">{{ s.name }} · {{ sizeText(s.file) }}</span>
                  <button type="button" class="icon-btn" (click)="removeShot(i)" [attr.aria-label]="t('pb.v2.i.removeShot')">
                    <mat-icon>close</mat-icon>
                  </button>
                </span>
              }
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
            @if (ocrBusy()) { <span class="hint">{{ t('pb.v2.i.ocrRunning') }}</span> }
            <span class="spacer"></span>
            <button mat-stroked-button (click)="close()">{{ t('common.cancel') }}</button>
            <button mat-raised-button color="primary" [disabled]="!canParse() || busy() || ocrBusy()"
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
            @if (batch(); as cards) {
              <!-- 批量解析预览(定稿第九节):来源标注 + 按归一化主题聚合,
                   组内时间正序、组间按最新时间倒序;每卡可改字段/移除,
                   识别失败的卡只标自己,可重试/移除,不拖垮整批。 -->
              <p class="hint" data-testid="intake-batch-summary">
                {{ t(batchSourceKey()) }} · {{ fmt('pb.v2.i.batchSummary', { n: cards.length, g: batchGroups().length }) }}
              </p>
              @for (g of batchGroups(); track g.key) {
                <div class="bgroup" data-testid="batch-group">
                  <div class="bgroup-head">
                    <span class="bgroup-subject">{{ g.subject || t('pb.v2.i.noSubject') }}</span>
                    <span class="bgroup-count">{{ fmt('pb.v2.i.batchCount', { n: g.cards.length }) }}</span>
                  </div>
                  @for (card of g.cards; track card.id) {
                    <div class="bcard" data-testid="batch-card">
                      <img class="shot-thumb lg" [src]="shots()[card.shotIndex]?.url" [alt]="shots()[card.shotIndex]?.name">
                      @if (card.status === 'failed') {
                        <div class="bcard-main">
                          <p class="err">{{ t(card.errorKey) }}</p>
                          <div class="row tight">
                            <button mat-stroked-button [disabled]="ocrBusy()" (click)="retryCard(card)">{{ t('pb.v2.i.retry') }}</button>
                            <button mat-button (click)="removeCard(card)">{{ t('pb.v2.i.removeCard') }}</button>
                          </div>
                        </div>
                      } @else {
                        <div class="bcard-main">
                          <div class="pv-grid">
                            <label>{{ t('pb.v2.i.fCategory') }}
                              <select class="fld" [ngModel]="card.category" (ngModelChange)="updateCard(card, 'category', $event)">
                                @for (c of categories(); track c.key) { <option [value]="c.key">{{ c.label }}</option> }
                              </select>
                            </label>
                            <label>{{ t('pb.v2.i.fSubject') }} <input class="fld" [ngModel]="card.subject" (ngModelChange)="updateCard(card, 'subject', $event)"></label>
                            <label>{{ t('pb.v2.i.fFrom') }} <input class="fld" [ngModel]="card.from" (ngModelChange)="updateCard(card, 'from', $event)"></label>
                            <label>{{ t('pb.v2.i.fEmailDate') }} <input class="fld" type="date" [ngModel]="card.date" (ngModelChange)="updateCard(card, 'date', $event)"></label>
                            <label>{{ t('pb.v2.i.fWhen') }} <input class="fld" [placeholder]="t('pb.v2.i.fWhenPh')" [ngModel]="card.when" (ngModelChange)="updateCard(card, 'when', $event)"></label>
                            <label>{{ t('pb.v2.i.fInterviewers') }} <input class="fld" [placeholder]="t('pb.v2.i.commaPh')" [ngModel]="card.interviewers" (ngModelChange)="updateCard(card, 'interviewers', $event)"></label>
                            <label>{{ t('pb.v2.i.fFormat') }}
                              <select class="fld" [ngModel]="card.format" (ngModelChange)="updateCard(card, 'format', $event)">
                                <option value="">{{ t('pb.v2.fmt.unknown') }}</option>
                                <option value="线上视频">{{ t('pb.v2.fmt.video') }}</option>
                                <option value="线上做题平台">{{ t('pb.v2.fmt.platform') }}</option>
                                <option value="电话">{{ t('pb.v2.fmt.phone') }}</option>
                                <option value="现场">{{ t('pb.v2.fmt.onsite') }}</option>
                              </select>
                            </label>
                          </div>
                          @if (card.duplicate) { <p class="warn slim">{{ t('pb.v2.i.dupSuspect') }}</p> }
                          <pre class="bcard-body">{{ card.body }}</pre>
                          <div class="row tight">
                            <span class="spacer"></span>
                            <button mat-button (click)="removeCard(card)">{{ t('pb.v2.i.removeCard') }}</button>
                          </div>
                        </div>
                      }
                    </div>
                  }
                </div>
              }
            } @else {
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
    .shot-strip { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
    .shot-item { display: inline-flex; align-items: center; gap: 9px;
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
    .warn.slim { padding: 5px 10px; margin: 6px 0; }
    /* 批量预览(定稿第九节):主题组 + 邮件卡 */
    .bgroup { margin: 12px 0; }
    .bgroup-head { display: flex; align-items: center; gap: 9px; margin-bottom: 6px; }
    .bgroup-subject { font-size: 14px; font-weight: 600; }
    .bgroup-count { font-size: 12px; font-weight: 700; color: #475467; background: #eef2f7;
                    border-radius: 999px; padding: 1px 9px; }
    .bcard { display: flex; gap: 12px; border: 1px solid var(--yi-border, #ddd);
             border-radius: 12px; padding: 10px 12px; margin: 8px 0; align-items: flex-start; }
    .bcard .shot-thumb.lg { width: 92px; height: 68px; flex: none; }
    .bcard-main { flex: 1; min-width: 0; }
    .bcard-body { white-space: pre-wrap; font-size: 13px; line-height: 1.55; font-family: inherit;
                  max-height: 150px; overflow-y: auto; margin: 6px 0 0;
                  border-top: 1px dashed var(--yi-border, #ddd); padding-top: 7px; }
    .bcard-body::-webkit-scrollbar { width: 10px; }
    .bcard-body::-webkit-scrollbar-track { background: #e7eaee; border-radius: 5px; }
    .bcard-body::-webkit-scrollbar-thumb { background: #7d8797; border-radius: 5px; }
    .bcard-body::-webkit-scrollbar-thumb:hover { background: #3f51b5; }
    .row.tight { margin-top: 6px; }
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
  private readonly aiApi = inject(PracticeApi);
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
  /** 已粘入的截图(多张共存,定稿第九节):再粘只追加,每张可单独移除。 */
  readonly shots = signal<ShotItem[]>([]);
  /** Email 批量解析的卡片组;非 null = 当前预览是批量模式(文字为空+有截图)。 */
  readonly batch = signal<EmailCard[] | null>(null);
  /** 逐张 OCR 进行中(解析按钮禁用 + 进度提示)。 */
  readonly ocrBusy = signal(false);
  /**
   * 批量预览的来源标注(定稿第九节,按实标注):整批全本地 OCR → 本地;
   * 全走视觉兜底 → 视觉;混合 → 混合。单卡实际来源另存于 EmailCard.source。
   */
  readonly batchSourceKey = computed(() => {
    const ok = (this.batch() ?? []).filter(c => c.status === 'ok');
    if (ok.length && ok.every(c => c.source === 'vision')) return 'pb.v2.i.srcScreenshot';
    if (ok.length && ok.every(c => c.source === 'local')) return 'pb.v2.i.srcScreenshotLocal';
    return ok.length ? 'pb.v2.i.srcScreenshotMixed' : 'pb.v2.i.srcScreenshotLocal';
  });
  private cardSeq = 0;
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
   * 截图附件入口(只有粘贴一条路,没有文件选择按钮):多张共存(定稿第九节)——
   * 再粘只追加、不替换、不提示;每张在框内缩略图条里单独呈现/移除。
   * 缩略图用 object URL 即时预览,呈现在文本框容器内底部。
   */
  setShot(f: File): void {
    this.shots.update(list => [...list, { file: f, name: f.name, url: URL.createObjectURL(f) }]);
  }

  removeShot(index: number): void {
    this.shots.update(list => {
      const it = list[index];
      if (it) URL.revokeObjectURL(it.url);
      return list.filter((_, j) => j !== index);
    });
  }

  clearShots(): void {
    for (const s of this.shots()) URL.revokeObjectURL(s.url);
    this.shots.set([]);
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
    const files = allClipboardFiles(ev.clipboardData);
    if (!files.length) {
      this.maybeAutoSwitchTextPaste(ev);
      return;
    }
    const images = files.filter(f => f.type.startsWith('image/'));
    if (images.length) {
      ev.preventDefault();
      // 一次粘多张全部接住(多图共存,定稿第九节);同秒多张以序号后缀避同名。
      images.forEach((img, i) => {
        const ext = (img.type.split('/')[1] || 'png').replace('jpeg', 'jpg');
        const d = new Date();
        const p = (n: number) => String(n).padStart(2, '0');
        const name = `${this.t('pb.v2.i.clipName')}-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}` +
          `-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}${images.length > 1 ? '-' + (i + 1) : ''}.${ext}`;
        this.setShot(new File([img], name, { type: img.type }));
      });
      return;
    }
    const audio = files.find(isAudioFile);
    if (audio) {
      ev.preventDefault();
      if (this.type() === 'transcript') {
        this.setAudio(audio);
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

  sizeText(f: File | null | undefined): string {
    return fmtFileSize(f?.size ?? 0);
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

  /**
   * 「解析预览」可用性:上传模式要有录音;其余模式有文本或任一截图即可
   * (截图走视觉 OCR 进同一解析管线,定稿第九节)。
   */
  canParse(): boolean {
    if (this.type() === 'transcript' && this.transcriptMode() === 'upload') {
      return !!this.audioFile();
    }
    return !!this.text().trim() || this.shots().length > 0;
  }

  /** 二选一切换:只切呈现与保存来源,不清空另一模式已填的内容(防误切丢失)。 */
  setTranscriptMode(m: TranscriptMode): void {
    this.transcriptMode.set(m);
  }

  // ---------------- 解析 ----------------

  /**
   * 解析分流:
   * - 录音上传模式:无文本可解析,预览只呈现文件信息 + 归属(原行为);
   * - Email 且文字为空、有截图:逐张 OCR(共享服务为主、视觉兜底)→ 批量卡片预览(定稿第九节);
   * - 备注/转写文本且文字为空、有截图:OCR 全部截图拼成文本,回落到与粘文字
   *   完全相同的管线;全失败则留在输入步报错,不静默;
   * - 有文字:原同步解析(文字为内容、截图为附件,不变)。
   */
  async parse(): Promise<void> {
    this.errorMsg.set(null);
    this.duplicate.set(false);
    this.batch.set(null);
    if (this.isAudioMode()) {
      // 录音没有可解析的文本:预览只呈现文件信息 + 归属,不假装已解析出内容。
      this.segments.set([]);
      if (!this.fDate()) this.fDate.set(todayStr());
      this.step.set('preview');
      return;
    }
    if (this.type() === 'email' && !this.text().trim() && this.shots().length) {
      await this.parseEmailBatch();
      return;
    }
    if (!this.text().trim() && this.shots().length) {
      if (!(await this.ocrIntoText())) return;
    }
    const raw = this.text();
    if (this.type() === 'email') this.parseEmail(raw);
    else if (this.type() === 'transcript') this.parseTranscript(raw);
    else this.parseNote(raw);
    this.step.set('preview');
  }

  /**
   * 一张截图的视觉 OCR(AiGateway 视觉通道,原样保留作兜底)。
   * 任何失败如实回 status,绝不编造文本。
   */
  private async ocrShotVision(s: ShotItem): Promise<{ status: string; text: string | null }> {
    try {
      const { b64, mime } = await fileToBase64(s.file);
      const r = await this.aiApi.extractImageText(b64, mime).toPromise();
      return { status: r?.status ?? 'failed', text: r?.text ?? null };
    } catch {
      return { status: 'failed', text: null };
    }
  }

  /** 一张截图 → 共享 OCR 服务(本地 PaddleOCR,主通道)。失败如实回 status。 */
  private async ocrShotShared(s: ShotItem): Promise<{ status: string; text: string | null }> {
    try {
      const { b64, mime } = await fileToBase64(s.file);
      const r = await this.aiApi.extractImageTextShared(
        b64, mime, SHARED_OCR_TIMEOUT_MS, SHARED_OCR_BASE_URL).toPromise();
      const text = r?.text?.trim() ? r.text : null;
      return { status: r?.status === 'ok' && text ? 'ok' : 'failed', text };
    } catch {
      // 不可达/超时/HTTP 错误:一律 failed,由调用方按兜底规则回落视觉。
      return { status: 'failed', text: null };
    }
  }

  /**
   * 一张截图 → 文字(票59 入口切换):共享 OCR 为主,AiGateway 视觉为兜底。
   * 兜底判定(任一命中即回落视觉):共享服务不可达/超时/status 非 ok/文本为空;
   * 邮件卡片场景(forEmailCard)再加一条:本地文本走既有管线解析后卡片为
   * failed(头部三件套全空)。返回的 source 按实标注实际来源。
   */
  private async ocrShot(s: ShotItem, forEmailCard = false):
    Promise<{ status: string; text: string | null; source: 'local' | 'vision' }> {
    const local = await this.ocrShotShared(s);
    if (local.status === 'ok' && local.text) {
      if (!forEmailCard || this.cardFromText(0, local.text).status === 'ok') {
        return { ...local, source: 'local' };
      }
    }
    const vision = await this.ocrShotVision(s);
    return { ...vision, source: 'vision' };
  }

  private ocrErrorKey(status: string): string {
    switch (status) {
      case 'notConfigured': return 'pb.v2.i.ocrNotConfigured';
      case 'unsupported': return 'pb.v2.i.ocrUnsupported';
      default: return 'pb.v2.i.ocrFailed';
    }
  }

  /** 备注/转写:把全部截图 OCR 拼成文本走原管线;全失败返回 false 并已置错误。 */
  private async ocrIntoText(): Promise<boolean> {
    this.ocrBusy.set(true);
    try {
      const parts: string[] = [];
      let lastStatus = 'failed';
      for (const s of this.shots()) {
        const r = await this.ocrShot(s);
        if (r.status === 'ok' && r.text) parts.push(r.text);
        else lastStatus = r.status;
      }
      if (!parts.length) {
        this.errorMsg.set(this.t(this.ocrErrorKey(lastStatus)));
        return false;
      }
      this.text.set(parts.join('\n\n'));
      return true;
    } finally {
      this.ocrBusy.set(false);
    }
  }

  /** 既有材料里是否已有同 dedupeKey(发件人+日期+主题)的邮件。 */
  private assetDupExists(key: string): boolean {
    return (this.entry().assets ?? []).some(a => {
      try {
        const m = JSON.parse(a.metaJson ?? '{}') as AssetMeta;
        return m.dedupeKey === key;
      } catch { return false; }
    });
  }

  private blankCard(shotIndex: number, errorKey: string,
                    source: 'local' | 'vision' = 'vision'): EmailCard {
    return {
      id: ++this.cardSeq, shotIndex, status: 'failed', errorKey,
      category: 'other', subject: '', from: '', date: '', when: '',
      interviewers: '', format: '', body: '', duplicate: false, source
    };
  }

  /** OCR 文本 → 邮件卡片:与粘文字 parseEmail 同一套抽取器,不另起口径。 */
  private cardFromText(shotIndex: number, raw: string,
                       source: 'local' | 'vision' = 'vision'): EmailCard {
    const p = parsePastedEmail(raw);
    const body = p.body ?? raw;
    const card: EmailCard = {
      id: ++this.cardSeq, shotIndex, status: 'ok', errorKey: '',
      category: classifyEmail(p.subject ?? '', body),
      subject: p.subject ?? '',
      from: p.from ?? '',
      date: p.date ? (normalizeEmailDate(p.date) || '') : '',
      when: extractInterviewWhen(body) ?? '',
      interviewers: extractInterviewerNames(body).join(', '),
      format: extractFormat(body),
      body: raw,
      duplicate: false,
      source
    };
    // 头部三件套全空 = 没识别出邮件内容(如粘错图):按失败卡处理,可重试/移除。
    if (!card.subject && !card.from && !card.date) {
      card.status = 'failed';
      card.errorKey = 'pb.v2.i.ocrNoEmail';
    }
    return card;
  }

  /** Email 批量解析:逐张 OCR、逐张容错,最后按主题聚合呈现(见 batchGroups)。 */
  private async parseEmailBatch(): Promise<void> {
    this.ocrBusy.set(true);
    try {
      const cards: EmailCard[] = [];
      const seen = new Set<string>();
      const list = this.shots();
      for (let i = 0; i < list.length; i++) {
        const r = await this.ocrShot(list[i], true);
        if (r.status !== 'ok' || !r.text) {
          cards.push(this.blankCard(i, this.ocrErrorKey(r.status), r.source));
          continue;
        }
        const card = this.cardFromText(i, r.text, r.source);
        if (card.status === 'ok') {
          const key = dedupeKey(card.from, card.date, card.subject);
          card.duplicate = this.assetDupExists(key) || seen.has(key);
          seen.add(key);
        }
        cards.push(card);
      }
      this.batch.set(cards);
      // 整批同一归属:显式预设优先;否则默认沟通记录(与单封解析的兜底一致)。
      if (this.presetRoundId()) {
        this.target.set({ kind: 'round', roundId: this.presetRoundId()! });
      } else {
        this.target.set({ kind: 'comm' });
      }
      this.step.set('preview');
    } finally {
      this.ocrBusy.set(false);
    }
  }

  /**
   * 预览聚合(定稿第九节):按归一化主题(剥 Re:/Fwd:)分组,组内时间正序
   * (无日期垫底),组间按组内最新时间倒序;失败卡各自成组沉底,不混进主题组。
   */
  readonly batchGroups = computed<EmailCardGroup[]>(() => {
    const cards = this.batch() ?? [];
    const map = new Map<string, EmailCard[]>();
    for (const c of cards) {
      const key = c.status === 'ok'
        ? (normalizeThreadSubject(c.subject).key || `card-${c.id}`)
        : `card-${c.id}`;
      const arr = map.get(key) ?? [];
      arr.push(c);
      map.set(key, arr);
    }
    const groups = [...map.entries()].map(([key, arr]) => {
      const sorted = [...arr].sort((a, b) =>
        (a.date || '9999-99') < (b.date || '9999-99') ? -1 : 1);
      return {
        key,
        subject: normalizeThreadSubject(sorted[0].subject).display,
        cards: sorted,
        latest: sorted.reduce((m, c) => (c.date > m ? c.date : m), '')
      };
    });
    groups.sort((a, b) => (a.latest < b.latest ? 1 : -1));
    return groups.map(({ key, subject, cards: cs }) => ({ key, subject, cards: cs }));
  });

  /** 卡片字段编辑:改后重算疑似重复并触发重分组(computed 随 batch 引用更新)。 */
  updateCard(card: EmailCard, field: 'category' | 'subject' | 'from' | 'date' | 'when' | 'interviewers' | 'format',
             value: string): void {
    card[field] = value;
    if (field === 'subject' || field === 'from' || field === 'date') {
      const key = dedupeKey(card.from, card.date, card.subject);
      card.duplicate = this.assetDupExists(key)
        || (this.batch() ?? []).some(c => c.id !== card.id && c.status === 'ok'
          && dedupeKey(c.from, c.date, c.subject) === key);
    }
    this.batch.update(list => (list ? [...list] : list));
  }

  removeCard(card: EmailCard): void {
    this.batch.update(list => (list ? list.filter(c => c.id !== card.id) : list));
  }

  /** 失败卡重试:只重跑它那一张图的 OCR,成功则用新抽取覆盖(失败卡无用户编辑可丢)。 */
  async retryCard(card: EmailCard): Promise<void> {
    const s = this.shots()[card.shotIndex];
    if (!s) return;
    this.ocrBusy.set(true);
    try {
      const r = await this.ocrShot(s, true);
      if (r.status !== 'ok' || !r.text) {
        card.errorKey = this.ocrErrorKey(r.status);
      } else {
        const fresh = this.cardFromText(card.shotIndex, r.text, r.source);
        if (fresh.status !== 'ok') {
          card.errorKey = fresh.errorKey;
        } else {
          Object.assign(card, fresh, { id: card.id });
          const key = dedupeKey(card.from, card.date, card.subject);
          card.duplicate = this.assetDupExists(key);
        }
      }
    } finally {
      this.ocrBusy.set(false);
      this.batch.update(list => (list ? [...list] : list));
    }
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
    // 重复检测:发件人+日期+主题(与批量卡片共用同一判定)
    const key = dedupeKey(p.from ?? '', this.fDate(), p.subject ?? '');
    this.duplicate.set(this.assetDupExists(key));
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
        for (const s of this.shots()) {
          await this.api.uploadFile(entry.id, s.file,
            { roundId: roundId ?? '', phase: audioPhase }).toPromise();
        }
        await this.api.uploadFile(entry.id, this.audioFile()!,
          { roundId: roundId ?? '', phase: audioPhase }).toPromise();
        this.finishSave();
        return;
      }

      // 批量邮件(定稿第九节):每封各成一条材料/沟通,共用主题聚合标识。
      // 全部卡片识别失败时没有可保存内容 —— 留在预览报错,不假装已保存。
      if (this.type() === 'email' && this.batch()) {
        if (!(this.batch() ?? []).some(c => c.status === 'ok')) {
          this.errorMsg.set(this.t('pb.v2.i.nothingToSave'));
          this.busy.set(false);
          return;
        }
        await this.saveBatch(entry, roundId, t);
        this.finishSave();
        return;
      }

      // phase 落值与展示推导同一规则(derivePhase):材料日期 vs 轮次日期,
      // 不再写死"邮件一律 Pre"—— 面试后的反馈邮件要落到 After。
      const matPhase = roundId
        ? derivePhase(this.type() === 'email', this.fDate() || todayStr(), this.roundDateOf(roundId, t))
        : null;

      // 截图先传(原图作为附件资产,归到同一目标,phase 与主材料一致;
      // 多张全传,第一张的 id 记进 meta.screenshotAssetId 作主截图)
      let shotId: string | undefined;
      for (const s of this.shots()) {
        const up = await this.api.uploadFile(entry.id, s.file,
          { roundId: roundId ?? '', phase: matPhase ?? '' }).toPromise();
        if (shotId === undefined) shotId = up?.id;
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

  /**
   * 批量保存(定稿第九节):每封成功卡各成一条材料 —— 各自上传自己的截图作
   * screenshotAssetId、正文为 OCR 全文、meta 带共用 threadKey(归一化主题);
   * 归属为沟通且条目关联投递时,逐封镜像到 Jobs 沟通时间线(与单封同路径)。
   * 失败卡不保存(用户可在预览里重试/移除后再存)。
   */
  private async saveBatch(entry: InterviewDetail, roundId: string | null, t: Target): Promise<void> {
    const cards = (this.batch() ?? []).filter(c => c.status === 'ok');
    for (const card of cards) {
      const phase = roundId
        ? derivePhase(true, card.date || todayStr(), this.roundDateOf(roundId, t))
        : null;
      let shotId: string | undefined;
      const shot = this.shots()[card.shotIndex];
      if (shot) {
        const up = await this.api.uploadFile(entry.id, shot.file,
          { roundId: roundId ?? '', phase: phase ?? '' }).toPromise();
        shotId = up?.id;
      }
      const meta = this.buildCardMeta(card, shotId);
      const attached = await this.api.attachAsset(entry.id, {
        kind: 'Text',
        fileName: trimSummary(card.subject) || trimSummary(card.from) || this.t('pb.v2.i.typeEmail'),
        transcriptText: card.body,
        transcriptSegmentsJson: null,
        roundId,
        phase,
        metaJson: JSON.stringify(meta)
      }).toPromise();
      const assetId = attached!.id;
      if (!roundId && entry.jobApplicationId) {
        const comm = await this.api.createCommunication(entry.jobApplicationId, {
          type: 'Email',
          subject: card.subject || null,
          content: card.body,
          contactName: card.from || null,
          occurredAt: new Date(`${card.date || todayStr()}T12:00:00`).toISOString(),
          metaJson: JSON.stringify({ ...meta, assetId }),
          handlingStatus: meta.arrangement ? 'Pending' : null
        }).toPromise();
        if (comm?.id) {
          await this.api.updateAssetMeta(entry.id, assetId,
            { ...meta, communicationId: comm.id }).toPromise().catch(() => undefined);
        }
      }
    }
  }

  /** 批量卡片的 meta:与 buildMeta 的邮件分支同字段,多带 threadKey 聚合标识。 */
  private buildCardMeta(card: EmailCard, shotId?: string): AssetMeta {
    const names = card.interviewers.split(/[,、,]/).map(s => s.trim()).filter(Boolean);
    const meta: AssetMeta = {
      channel: 'email',
      date: card.date || undefined,
      interviewers: names.length ? names : undefined,
      screenshotAssetId: shotId,
      category: card.category,
      subject: card.subject || undefined,
      from: card.from || undefined,
      interviewAt: card.when || undefined,
      format: card.format || undefined,
      dedupeKey: dedupeKey(card.from, card.date, card.subject),
      threadKey: normalizeThreadSubject(card.subject).key || undefined
    };
    if (card.category === 'invitation' && card.when) {
      meta.arrangement = {
        date: card.when.slice(0, 10),
        time: card.when.slice(11, 16) || undefined,
        interviewers: names.join(', ') || undefined,
        format: card.format || undefined
      };
    }
    return meta;
  }

  /** 保存成功后的统一收尾(文本/录音两条路径共用)。 */
  private finishSave(): void {
    this.snack.open(this.t('pb.v2.d.saved'), this.t('common.close'), { duration: 2500 });
    this.text.set('');
    this.clearShots();
    this.batch.set(null);
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
      // 与批量保存同口径:单封粘贴也带主题聚合标识,Communications 一并聚合。
      meta.threadKey = normalizeThreadSubject(meta.subject ?? '').key || undefined;
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

/** 从正文里找面试时间:优先 "Oct 7, 2026 at 11:00 AM" / "2026-10-07 11:00" 一类明确写法;
 *  OCR 邀请卡格式也要认:"2026-10-13 周二 10:00～11:00"(星期夹中间)、"2026年10月13日(星期二) 14:00"。 */
export function extractInterviewWhen(text: string): string | null {
  const p = (n: string) => n.padStart(2, '0');
  // 年月日写法,日期与时间之间可夹星期;时段(～/〜/-)只取起始时间
  const cn = text.match(/(20\d{2})年(\d{1,2})月(\d{1,2})日\s*(?:(?:星期|周)[一二三四五六日天]\s*)?(\d{1,2})[:：](\d{2})/);
  if (cn) {
    return `${cn[1]}-${p(cn[2])}-${p(cn[3])} ${p(cn[4])}:${cn[5]}`;
  }
  // ISO 日期与时间之间可夹星期("2026-10-13 周二 10:00～11:00"),或仍是单个空格/T 分隔
  const iso = text.match(/(20\d{2})[-/](\d{1,2})[-/](\d{1,2})(?:[ T]|\s*(?:星期|周)[一二三四五六日天]\s*)(\d{1,2})[:：](\d{2})/);
  if (iso) {
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

/** 剪贴板里的全部文件(files 列表优先,其次 items 的 file 项);纯文字粘贴返回空数组。 */
function allClipboardFiles(cd: DataTransfer | null): File[] {
  if (!cd) return [];
  if (cd.files && cd.files.length) return Array.from(cd.files);
  const out: File[] = [];
  for (let i = 0; i < cd.items.length; i++) {
    if (cd.items[i].kind === 'file') {
      const f = cd.items[i].getAsFile();
      if (f) out.push(f);
    }
  }
  return out;
}

/** File → base64(视觉 OCR 入参):走 FileReader 的 data URL,剥掉前缀只留正文。 */
function fileToBase64(f: File): Promise<{ b64: string; mime: string }> {
  return new Promise((resolve, reject) => {
    const rd = new FileReader();
    rd.onload = () => {
      const url = String(rd.result ?? '');
      const comma = url.indexOf(',');
      resolve({ b64: comma >= 0 ? url.slice(comma + 1) : url, mime: f.type || 'image/png' });
    };
    rd.onerror = () => reject(rd.error);
    rd.readAsDataURL(f);
  });
}

/** 音频文件判别:MIME 优先,Finder 复制等场景 MIME 可能为空,用扩展名兜底。 */
function isAudioFile(f: File): boolean {
  if (f.type.startsWith('audio/')) return true;
  return /\.(mp3|m4a|wav|webm|ogg)$/i.test(f.name ?? '');
}
