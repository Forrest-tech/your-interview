import {
  Component, EventEmitter, OnInit, Output, computed, effect, inject, input, signal, viewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewDetail, InterviewRound
} from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { RoundRemindersService } from './round-reminders.service';
import { formatRemindAt, reminderStateForRound } from './reminders.helpers';
import { TranscriptSegment, parseQuestionReview, safeJson } from '../v2.models';
import { PrepReportComponent } from './prep-report.component';
import { RoundRecordComponent } from './round-record.component';
import { QaZoomComponent } from '../../../../core/qa-zoom/qa-zoom.component';
import { QaImportComponent } from './qa-import.component';
import { MaterialsListComponent } from './materials-list.component';
import { ExportDialogComponent, ExportDialogData } from '../export/export-dialog.component';
import { ExportBlock, ExportDoc } from '../export/export-doc';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, formatLabel, makeV2Fmt, makeV2T } from '../v2.helpers';
import { fieldErrorFromServer, textLengthError } from '../../../../core/api/validation-errors.helpers';

type RoundTab = 'prep' | 'record' | 'qa' | 'materials';

/** 轮次阶段口径:与 rounds-timeline / Tracker smart-add / Tech Stack 共用的同一套取值。 */
const ROUND_STAGE_OPTIONS = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];

/**
 * Round 工作卡(2026-10-20 定稿):头行(第 N 轮 · 阶段 · 时间 · 面试官 · 编辑/删除)
 * + 结果行 + 内页签(按时序固定:备考报告 → 面试实录 → 问答入库(计数) → 材料(计数))。
 * 默认页签按阶段:未开始 → 备考报告;已结束 → 面试实录。
 */
@Component({
  selector: 'app-round-card',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule,
    PrepReportComponent, RoundRecordComponent, QaImportComponent, MaterialsListComponent,
    QaZoomComponent],
  template: `
    <section class="card" data-testid="round-card">
      <!-- 票169:详情头去重——轮次号/类型/日期/Outcome/Materials 数已在上方轨道节点(票164)显示,此处不再重复。
           仅保留轨道上没有的信息:时间(仅当时段存在时,日期本身不重复显示)、面试官、形式。 -->
      <div class="rc-head">
        <span class="rc-meta">
          @if (round().scheduledTime) { <span class="rc-meta-line">{{ round().scheduledTime }}</span> }
          @if (round().interviewers) { <span class="rc-meta-line">{{ round().interviewers }}</span> }
          @if (round().format) { <span class="rc-meta-line">{{ formatLabel(t, round().format) }}</span> }
        </span>
        <span class="spacer"></span>
        <!-- 票206：record 三功能已移至下方 record 页签内容区顶部的 .rec-head 行（与 Prep 页头同一落点），此处不再渲染，避免与 Prep 页头位置不一致。 -->
        <!-- 票188:铅笔按钮已上移至父级页签行最右侧（viewChild 接线），此处不再渲染，避免重复。 -->
      </div>
      <div class="rc-outcome">
        @if (roundScore() != null) {
          <span class="rc-fact"><span class="rc-fact-label">{{ t('pb.v2.r.overall') }}</span><b>{{ roundScore() }}</b></span>
        }
        @if (questionCount()) {
          <span class="rc-fact"><span class="rc-fact-label">{{ t('pb.v2.r.questionsCountLabel') }}</span><b>{{ questionCount() }}</b></span>
        }
        @if (round().interviewers) {
          <span class="rc-fact"><span class="rc-fact-label">{{ t('pb.v2.r.interviewersLabel') }}</span><b>{{ round().interviewers }}</b></span>
        }
        @if (round().meetingLink) {
          <span class="rc-fact"><span class="rc-fact-label">{{ t('pb.v2.r.meetingLink') }}</span><a [href]="round().meetingLink" target="_blank" rel="noopener">{{ t('pb.v2.r.openLink') }}</a></span>
        }
        <!-- 票177:Location 移回此处信息行（169 之前的位置；轨道节点上没有，不算重复）。 -->
        <!-- 票188:Location 显示项暂隐藏（隐藏不是删除：location 数据、编辑表单 fLocation、保存逻辑、i18n 键一律保留；恢复时改回 false 即一处恢复）。 -->
        @if (round().location && !hideLocation) {
          <span class="rc-fact"><span class="rc-fact-label">{{ t('pb.v2.r.location') }}</span><b>{{ round().location }}</b></span>
        }
      </div>
      <!-- 票177:.rc-sub 行删除：Edit round 已改右上角铅笔图标，不再为单个按钮占一整行。 -->

      @if (round().scheduledDate) {
        @if (!hideCalBar) {
        <div class="cal-bar" data-testid="round-cal-bar">
          <mat-icon>event</mat-icon>
          <span>{{ t('pb.v2.r.calInApp') }}</span>
          <span class="cal-remind">{{ t('pb.v2.r.calReminders') }}</span>
          @if (reminderState().kind === 'due') {
            <span class="cal-due" data-testid="round-cal-due">{{ t('pb.v2.r.calDue') }}</span>
          } @else if (reminderState().kind === 'upcoming') {
            <span class="cal-next" data-testid="round-cal-next">{{ t('pb.v2.r.calNext') }} {{ nextRemindLabel() }}</span>
          }
          <span class="spacer"></span>
          <!-- 票 118 / F-4:日历条不再放第二个 Edit round —— 编辑入口只保留轮次标题旁那一个,
               本条只报状态(到期/下一次提醒走上方状态文案)。 -->
        </div>
        }
      }

      @if (editing()) {
        <div class="edit-grid">
          <label>{{ t('pb.v2.r.fStage') }}
            <select class="fld" data-testid="round-stage-select" [ngModel]="eStage()" (ngModelChange)="setStage($event)">
              @for (s of stageOptions(); track s) { <option [value]="s">{{ stageLabel(s) }}</option> }
            </select>
            @if (fieldErrors().stage) { <span class="field-error" role="alert">{{ fieldErrors().stage }}</span> }
          </label>
          <label>{{ t('pb.v2.r.fDate') }} <input class="fld" type="date" [ngModel]="eDate()" (ngModelChange)="eDate.set($event)"></label>
          <label>{{ t('pb.v2.r.fTime') }} <input class="fld" placeholder="11:00" [ngModel]="eTime()" (ngModelChange)="eTime.set($event)"></label>
          <label>{{ t('pb.v2.r.fInterviewers') }} <input class="fld" [ngModel]="eInterviewers()" (ngModelChange)="setInterviewers($event)">
            @if (fieldErrors().interviewers) { <span class="field-error" role="alert">{{ fieldErrors().interviewers }}</span> }</label>
          <label>{{ t('pb.v2.r.fFormat') }} <input class="fld" [ngModel]="eFormat()" (ngModelChange)="eFormat.set($event)"></label>
          <label>{{ t('pb.v2.r.fLocation') }} <input class="fld" [ngModel]="eLocation()" (ngModelChange)="setLocation($event)">
            @if (fieldErrors().location) { <span class="field-error" role="alert">{{ fieldErrors().location }}</span> }</label>
          <label>{{ t('pb.v2.r.meetingLink') }} <input class="fld" [ngModel]="eLink()" (ngModelChange)="eLink.set($event)"></label>
          <label>{{ t('pb.v2.r.fOutcome') }}
            <select class="fld" [ngModel]="eOutcome()" (ngModelChange)="eOutcome.set($event)">
              <option value="Pending">{{ t('pb.v2.r.ocPending') }}</option>
              <option value="Passed">{{ t('outcome.Passed') }}</option>
              <option value="Failed">{{ t('outcome.Failed') }}</option>
              <option value="Cancelled">{{ t('pb.v2.r.ocCancelled') }}</option>
            </select>
          </label>
          <label class="wide">{{ t('pb.v2.r.fFeedback') }} <input class="fld" [ngModel]="eFeedback()" (ngModelChange)="eFeedback.set($event)"></label>
        </div>
        <div class="row">
          <button mat-raised-button color="primary" (click)="saveEdit()">{{ t('pb.v2.r.saveRound') }}</button>
          <span class="spacer"></span>
          <button mat-button color="warn" (click)="deleteRound()">{{ t('pb.v2.r.deleteRound') }}</button>
        </div>
      }

      <div class="rtabs" role="tablist">
        @for (tb of tabs(); track tb.key) {
          <button type="button" role="tab" class="rtab" [class.on]="tab() === tb.key"
                  (click)="tab.set(tb.key)">
            {{ tb.label }}@if (tb.count() != null) { <span class="cnt">{{ tb.count() }}</span> }
          </button>
        }
      </div>

      @switch (tab()) {
        @case ('prep') {
          <app-prep-report [entry]="entry()" [round]="round()"
                           [guidanceAssets]="guidanceAssets()"
                           [roundAssets]="roundAssets()"
                           [generateRequest]="prepGenToken()"
                           (changed)="changed.emit()"
                           (editInput)="editPrepInput.emit({ roundId: round().id, purpose: $event.purpose, assetId: $event.assetId, text: $event.text })"
                           (generateConsumed)="generateConsumed.emit()" />
        }
        @case ('record') {
          <!-- 票206：record 三功能行，与 Prep 页头（.prep-head/.ph-actions）同一落点：页签内容区顶部右上角，padding 照 .prep 同款 4px 2px（行顶距页签行底边与右偏移与 Prep 页头一字相同）、组内 gap 8px、行高由 36px 方框按钮定；Text size 用 compact 版（与 Prep 一致：无“字号”名、保留百分比数字），Export 与逐题分析复用 .rc-icon-btn（196 基准）；票213：逐题分析图标换成 Prep 生成钮同款 auto_awesome（用户令：两边功能类似、要一样，206“功能不同不换图标”口径改口；功能、接线、文案一字不动，只换图标）。 -->
          <div class="rec-head">
            <span class="spacer"></span>
            <div class="rec-actions">
              <app-qa-zoom [compact]="true"></app-qa-zoom>
              <button type="button" class="rc-icon-btn" (click)="openRecordExport()"
                      [title]="t('pb.v2.r.exportRecord')" [attr.aria-label]="t('pb.v2.r.exportRecord')"><mat-icon>download</mat-icon></button>
              @if (recordComp(); as rc) {
                <button type="button" class="rc-icon-btn" (click)="rc.generateReview()" [disabled]="rc.busy()"
                        [title]="rc.roundReview() ? t('pb.v2.rec.regenerateAnalysis') : t('pb.v2.rec.generateAnalysis')"
                        [attr.aria-label]="rc.roundReview() ? t('pb.v2.rec.regenerateAnalysis') : t('pb.v2.rec.generateAnalysis')"><mat-icon>auto_awesome</mat-icon></button>
              }
            </div>
          </div>
          <app-round-record [entry]="entry()" [round]="round()" (changed)="changed.emit()" />
        }
        @case ('qa') {
          <app-qa-import [entry]="entry()" [round]="round()" (changed)="changed.emit()" />
        }
        @case ('materials') {
          <div class="mat-head-row">
            <span class="mh-note">{{ t('pb.v2.r.matNote') }}</span>
            <span class="spacer"></span>
            @if (!hideMatActions) {
            <label class="mini-btn file-label">{{ t('pb.v2.r.uploadFile') }}
              <input type="file" hidden (change)="onUpload($event)">
            </label>
            <button mat-stroked-button (click)="addMaterialHere.emit(round().id)">{{ t('pb.v2.r.addMaterialHere') }}</button>
            <button mat-stroked-button (click)="addNoteHere.emit()">{{ t('pb.v2.d.addNote') }}</button>
            }
          </div>
          <app-materials-list [entryId]="entry().id" [assets]="roundAssets()"
                              [rounds]="entry().rounds ?? []" (changed)="changed.emit()" />
        }
      }
    </section>
  `,
  styles: [`
    .card { padding: 2px 2px 0; margin-top: 6px; /* 票192:14px → 6px，收紧节点行与详情卡之间的空白 */ }
    .rc-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    /* 票206：record 页签内容区顶部三功能行，与 Prep 页头（.prep-head/.ph-actions）同几何：组内 gap 8px、整组可换行，行高由 36px 方框按钮定，与 Prep 行同高；padding 4px 2px 照 .prep 同款，使行顶距页签行底边（10px 页签下边距 + 4px）与右偏移（卡片 2px + 自身 2px）与 Prep 页头一字相同；360px 下整组可落行不溢出。 */
    .rec-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 4px 2px; }
    .rec-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    /* 票177:右上角铅笔图标按钮，与 AI practice 的 .ch-edit 看齐（36px 白底细边框圆角方框）。 */
    .rc-icon-btn { flex: 0 0 auto; display: inline-flex; align-items: center; justify-content: center;
                   width: 36px; height: 36px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px;
                   background: #ffffff; color: var(--yi-text-muted, #475569); cursor: pointer;
                   transition: background .15s ease, border-color .15s ease, color .15s ease; }
    .rc-icon-btn:hover { background: #f6f8fb; border-color: var(--yi-border, #e2e8f0); color: var(--yi-text, #0f172a); }
    .rc-icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    /* 票123 R-3:日期/时间/形式改为堆叠小字行(§9 mock 的 .rwhen),不再串在中点上。 */
    .rc-meta { display: flex; flex-direction: column; gap: 1px; font-size: 12.5px;
               color: var(--yi-text-muted, #475569); }
    .rc-meta-line { display: block; line-height: 1.5; }
    .spacer { flex: 1; }
    /* 票123 R-3:结果行改为「标签 + 值」成对事实(§9 mock 的 .facts/.fact/.k/.v)。 */
    .rc-outcome { display: flex; gap: 26px; flex-wrap: wrap; align-items: baseline;
                  font-size: 13px; color: var(--yi-text, #0f172a); margin-top: 7px; }
    .rc-fact { display: inline-flex; align-items: baseline; gap: 7px; }
    .rc-fact-label { font-size: 12px; font-weight: 500; color: var(--yi-text-muted, #475569); }
    .rc-fact b { font-size: 13px; font-weight: 500; }
    .cal-bar { display: flex; align-items: center; gap: 8px; margin-top: 9px; font-size: 14px;
               border: 1px dashed var(--yi-border, #e2e8f0); border-radius: 10px; padding: 7px 12px; }
    .cal-bar mat-icon { font-size: 17px; width: 17px; height: 17px; color: var(--yi-accent, #2563eb); }
    .cal-bar .cal-remind { color: var(--yi-text-muted, #475569); }
    .cal-bar .cal-due { color: #dc2626; font-weight: 600; }
    .cal-bar .cal-next { color: var(--yi-text-muted, #475569); }
    .rc-outcome a { color: var(--yi-accent, #2563eb); }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 5px 12px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .edit-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                 gap: 10px 14px; margin: 12px 0; }
    .edit-grid label { display: flex; flex-direction: column; gap: 4px; font-size: 13px;
                       color: var(--yi-text-muted, #475569); }
    .edit-grid .wide { grid-column: span 2; }
    .fld { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 8px; padding: 7px 9px;
           font-size: 14px; background: transparent; color: inherit; }
    .field-error { color: #dc2626; font-size: 12px; }
    .row { display: flex; gap: 8px; align-items: center; }
    .rtabs { display: flex; gap: 4px; border-bottom: 1px solid var(--yi-border, #e2e8f0); margin: 8px 0 10px; flex-wrap: wrap; /* 票192:上边距 14px → 8px，下边距与页签内边距不动；票200:容器线 1.5px → 1px，收敛为卡片内页签 */ }
    .rtab { border: none; background: transparent; color: var(--yi-text-muted, #475569); font-size: 13px; font-weight: 500; cursor: pointer;
            padding: 8px 13px; border-bottom: 1.5px solid transparent; margin-bottom: -1px; }
    .rtab.on { border-bottom-color: var(--yi-accent, #2563eb); font-weight: 700; color: var(--yi-text, #0f172a); }
    .cnt { font-size: 12px; font-weight: 700; background: color-mix(in srgb, var(--yi-text, #0f172a) 10%, transparent);
           border-radius: 999px; padding: 1px 7px; margin-left: 5px; }
    .mat-head-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .mh-note { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .file-label { display: inline-flex; align-items: center; }
  `]
})
export class RoundCardComponent implements OnInit {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  /** 票30(REQ-10):展示编号(日期序位置),由详情页传入;缺省回落存储 order(其他调用方零感知)。 */
  readonly displayNo = input<number | null>(null);
  /** 票159：统一入口“保存并生成”的单次生成请求（详情页透传，token 递增）。 */
  readonly prepGenRequest = input<{ roundId: string; token: number } | null>(null);
  @Output() readonly changed = new EventEmitter<void>();
  @Output() readonly addMaterialHere = new EventEmitter<string>();
  @Output() readonly addNoteHere = new EventEmitter<void>();
  /** 票159：读回区编辑 / 添加输入（透传自备考报告，详情页据此打开统一对话框）。 */
  @Output() readonly editPrepInput = new EventEmitter<{ roundId: string; purpose: 'experiences' | 'resume'; assetId: string | null; text: string }>();
  /** 票159：生成请求已被备考报告消费（透传，详情页据此清零）。 */
  @Output() readonly generateConsumed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly remindersSvc = inject(RoundRemindersService);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  /** CONF-CAL-2:本轮真实提醒状态(与日历页横幅共用 RoundRemindersService 一份数据)。 */
  readonly reminderState = computed(() =>
    reminderStateForRound(this.remindersSvc.reminders(), this.round().id));
  readonly nextRemindLabel = computed(() => {
    const s = this.reminderState();
    return s.kind === 'upcoming' ? formatRemindAt(s.next.remindAt) : '';
  });

  ngOnInit(): void {
    this.remindersSvc.ensureLoaded();
  }

  readonly tab = signal<RoundTab>('prep');
  // 票189：用户令去掉的两处（先隐藏不删除：日历提醒数据、上传/addMaterialHere/addNoteHere 接线、i18n 键一律保留；恢复只改开关）。
  readonly hideCalBar = true;
  readonly hideMatActions = true;
  /** 票191：实录页子组件（188 铅笔同款 viewChild 接线，供 .rc-head 的逐题分析图标按钮调用）。 */
  readonly recordComp = viewChild(RoundRecordComponent);
  readonly editing = signal(false);
  /** 票188：详情卡 Location 显示项暂隐藏（见模板处注释）。 */
  readonly hideLocation = true;
  readonly eStage = signal('');
  readonly eDate = signal('');
  readonly eTime = signal('');
  readonly eInterviewers = signal('');
  readonly eFormat = signal('');
  readonly eLocation = signal('');
  readonly eLink = signal('');
  readonly eOutcome = signal('Pending');
  readonly eFeedback = signal('');
  /** 票106(C2):轮次编辑字段级错误(后端 UpdateRound 校验器 Stage≤50 / Interviewers≤1000 / Location≤300)。 */
  readonly STAGE_MAX = 50;
  readonly INTERVIEWERS_MAX = 1000;
  readonly LOCATION_MAX = 300;
  readonly fieldErrors = signal<Partial<Record<'stage' | 'interviewers' | 'location', string>>>({});

  private tooLongText(max: number): string {
    return this.i18n.tf('validation.tooLong', { max });
  }

  private mapFieldError(e: unknown, field: string, sentLength: number, max: number): string | null {
    const c = fieldErrorFromServer(e, field, { sentLength, max });
    if (!c) return null;
    if (c.kind === 'tooLong') return this.tooLongText(max);
    if (c.kind === 'required') return this.t('validation.required');
    return c.text;
  }

  private clearFieldError(field: 'stage' | 'interviewers' | 'location'): void {
    if (this.fieldErrors()[field]) {
      const next = { ...this.fieldErrors() };
      delete next[field];
      this.fieldErrors.set(next);
    }
  }

  setStage(v: string): void { this.eStage.set(v); this.clearFieldError('stage'); }
  setInterviewers(v: string): void { this.eInterviewers.set(v); this.clearFieldError('interviewers'); }
  setLocation(v: string): void { this.eLocation.set(v); this.clearFieldError('location'); }

  private tabRoundId = '';

  readonly roundAssets = computed(() =>
    (this.entry().assets ?? []).filter(a => a.roundId === this.round().id));
  readonly guidanceAssets = computed(() =>
    this.roundAssets().filter(a => a.kind === 'Guidance'));
  readonly questionCount = computed(() =>
    (this.entry().questions ?? []).filter(q => q.roundId === this.round().id).length);
  readonly roundScore = computed(() =>
    safeJson<{ overall?: number | null }>(this.round().reviewJson)?.overall ?? null);

  readonly tabs = computed<{ key: RoundTab; label: string; count: () => number | null }[]>(() => [
    { key: 'prep', label: this.t('pb.v2.r.tabPrep'), count: () => null },
    { key: 'record', label: this.t('pb.v2.r.tabRecord'), count: () => null },
    { key: 'qa', label: this.t('pb.v2.r.tabQa'), count: () => this.questionCount() || null },
    { key: 'materials', label: this.t('pb.v2.r.tabMaterials'), count: () => this.roundAssets().length || null }
  ]);

  readonly formatLabel = formatLabel;

  /** Stage 下拉选项:标准口径在前;存量阶段值不在口径内时(如 Coding / Deep Dive)
   *  动态补为第一个选项并保持选中,显示不丢、保存前不被改写。 */
  readonly stageOptions = computed(() => {
    const cur = this.eStage().trim();
    return cur && !ROUND_STAGE_OPTIONS.includes(cur)
      ? [cur, ...ROUND_STAGE_OPTIONS]
      : [...ROUND_STAGE_OPTIONS];
  });

  stageLabel(s: string): string {
    const key = 'ts.stage.' + s;
    const v = this.t(key);
    return v === key ? s : v;
  }

  constructor() {
    // 票179：轮次切换时页签保持不变（不再按阶段重置为备考/实录）；
    // 只关闭编辑表单——编辑字段按轮次填充，带到另一轮会显示旧轮次的值。
    effect(() => {
      const r = this.round();
      if (r.id !== this.tabRoundId) {
        this.tabRoundId = r.id;
        this.editing.set(false);
      }
    }, { allowSignalWrites: true });

    // 票159：“保存并生成”跳转到本轮时切到 Prep 页签；备考报告收到 token 后触发一次生成。
    // 只响应与本轮匹配的请求；父级清零请求后转发 token 同步归零——否则切页签回来、
    // 备考报告重建时旧 token 会被当成新请求再次触发生成。
    effect(() => {
      const req = this.prepGenRequest();
      if (req && req.roundId === this.round().id) {
        this.tab.set('prep');
        this.prepGenToken.set(req.token);
      } else {
        this.prepGenToken.set(0);
      }
    }, { allowSignalWrites: true });
  }

  /** 票159：转发给备考报告的生成 token（0 = 无请求）。 */
  readonly prepGenToken = signal(0);

  toggleEdit(): void {
    const r = this.round();
    this.eStage.set(r.stage);
    this.eDate.set(r.scheduledDate ?? '');
    this.eTime.set(r.scheduledTime ?? '');
    this.eInterviewers.set(r.interviewers ?? '');
    this.eFormat.set(r.format ?? '');
    this.eLocation.set(r.location ?? '');
    this.eLink.set(r.meetingLink ?? '');
    // 票216:回读的 Rejected 映射为下拉框的 Failed 值，只映射初始显示；
    // 保存仍发 Failed，走后端别名入库 Rejected。
    this.eOutcome.set(r.outcome === 'Rejected' ? 'Failed' : r.outcome);
    this.eFeedback.set(r.feedback ?? '');
    this.fieldErrors.set({});
    this.editing.set(!this.editing());
  }

  async saveEdit(): Promise<void> {
    // 票106(C2):长度先挡,超限字段下亮含上限提示、编辑保持打开,不走通用 saveFailed
    const preErrors: Partial<Record<'stage' | 'interviewers' | 'location', string>> = {};
    if (textLengthError(this.eStage(), this.STAGE_MAX)) preErrors.stage = this.tooLongText(this.STAGE_MAX);
    if (textLengthError(this.eInterviewers(), this.INTERVIEWERS_MAX)) preErrors.interviewers = this.tooLongText(this.INTERVIEWERS_MAX);
    if (textLengthError(this.eLocation(), this.LOCATION_MAX)) preErrors.location = this.tooLongText(this.LOCATION_MAX);
    if (Object.keys(preErrors).length > 0) { this.fieldErrors.set(preErrors); return; }
    this.fieldErrors.set({});
    try {
      await this.api.updateRound(this.entry().id, this.round().id, {
        stage: this.eStage(),
        scheduledDate: this.eDate() || null,
        // COD-04 部分更新协议:扩展字段传空串 = 清空、缺省/null = 保持。
        // 本表单显式编辑这两个字段,清空须发空串,不能再用 || null(会被后端当作保持)。
        scheduledTime: this.eTime(),
        interviewers: this.eInterviewers() || null,
        format: this.eFormat() || null,
        location: this.eLocation() || null,
        meetingLink: this.eLink(),
        outcome: this.eOutcome(),
        feedback: this.eFeedback() || null,
        version: this.round().version ?? null
      }).toPromise();
      this.editing.set(false);
      this.snack.open(this.t('pb.v2.r.roundSaved'), this.t('common.close'), { duration: 2000 });
      this.remindersSvc.refresh();   // CONF-CAL-2:排期可能已变,提醒状态重新对账
      this.changed.emit();
    } catch (e) {
      // 票106(C2):errors{Stage/Interviewers/Location} 落字段下方;非字段级失败回落原通用 snack
      const mapped: Partial<Record<'stage' | 'interviewers' | 'location', string>> = {};
      const stageText = this.mapFieldError(e, 'Stage', this.eStage().length, this.STAGE_MAX);
      if (stageText) mapped.stage = stageText;
      const intText = this.mapFieldError(e, 'Interviewers', this.eInterviewers().length, this.INTERVIEWERS_MAX);
      if (intText) mapped.interviewers = intText;
      const locText = this.mapFieldError(e, 'Location', this.eLocation().length, this.LOCATION_MAX);
      if (locText) mapped.location = locText;
      if (Object.keys(mapped).length > 0) { this.fieldErrors.set(mapped); return; }
      this.snack.open(this.t('pb.v2.d.saveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  deleteRound(): void {
    this.api.deleteRound(this.entry().id, this.round().id).subscribe({
      next: () => { this.snack.open(this.t('pb.v2.r.roundDeleted'), this.t('common.close'), { duration: 2000 }); this.remindersSvc.refresh(); this.changed.emit(); },
      error: () => this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  onUpload(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    const f = input.files?.[0];
    if (!f) return;
    // phase 落值与展示推导同一规则:按上传日(即材料日期)vs 本轮日期 derivePhase。
    const phase = derivePhase(false, new Date().toISOString().slice(0, 10), this.round().scheduledDate);
    this.api.uploadFile(this.entry().id, f, { roundId: this.round().id, phase }).subscribe({
      next: () => {
        this.snack.open(this.t('pb.v2.r.uploaded'), this.t('common.close'), { duration: 2200 });
        input.value = '';
        this.changed.emit();
      },
      error: () => this.snack.open(this.t('pb.v2.r.uploadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  // ---------------- 导出实录 ----------------

  private exportSegments: TranscriptSegment[] = [];

  async openRecordExport(): Promise<void> {
    // 先把本轮转写段落取齐(对话原文是必选内容),再开导出对话框。
    const e = this.entry();
    const r = this.round();
    const segs: TranscriptSegment[] = [];
    for (const a of this.roundAssets().filter(x => x.kind === 'Transcript' || x.kind === 'Text')) {
      try {
        const t = await this.api.getTranscript(e.id, a.id).toPromise();
        const parsed = safeJson<TranscriptSegment[]>(t?.segmentsJson ?? null);
        if (parsed?.length) segs.push(...parsed);
      } catch { /* 单件失败不阻塞导出 */ }
    }
    this.exportSegments = segs;
    const date = new Date().toISOString().slice(0, 10);
    const data: ExportDialogData = {
      title: this.t('pb.v2.r.exportTitle'),
      subtitle: `${e.companyName} — Round ${r.order} ${r.stage}`,
      fileBase: `${e.companyName}-Round${r.order}-${this.t('pb.v2.r.fileSuffix')}-${date}`,
      storageKey: 'record',
      toggles: [
        { key: 'transcript', label: this.t('pb.v2.r.tgTranscript'), locked: true, defaultOn: true },
        { key: 'analysis', label: this.t('pb.v2.r.tgAnalysis'), defaultOn: true },
        { key: 'std', label: this.t('pb.v2.r.tgStd'), defaultOn: true },
        { key: 'summary', label: this.t('pb.v2.r.tgSummary'), defaultOn: true }
      ],
      buildDoc: sel => this.buildRecordDoc(sel)
    };
    this.dialog.open(ExportDialogComponent, { width: '560px', maxWidth: '94vw', data });
  }

  private buildRecordDoc(sel: Set<string>): ExportDoc {
    const e = this.entry();
    const r = this.round();
    const blocks: ExportBlock[] = [];
    const questions = (e.questions ?? [])
      .filter(q => q.roundId === r.id).sort((a, b) => a.sequence - b.sequence);
    const rv = safeJson<{
      overall?: number | null; passProbability?: number | null; passBasis?: string;
      strongest?: string; weakest?: string; summary?: string;
    }>(r.reviewJson);

    if (sel.has('summary') && rv) {
      const lines: string[] = [];
      if (rv.overall != null) lines.push(this.fmt('pb.v2.r.docOverall', { n: rv.overall }));
      if (rv.passProbability != null) lines.push(this.fmt('pb.v2.r.docPass', { n: rv.passProbability }));
      if (rv.passBasis) lines.push(this.fmt('pb.v2.r.docBasis', { v: rv.passBasis }));
      if (rv.strongest) lines.push(this.fmt('pb.v2.r.docStrongest', { v: rv.strongest }));
      if (rv.weakest) lines.push(this.fmt('pb.v2.r.docWeakest', { v: rv.weakest }));
      if (rv.summary) lines.push(rv.summary);
      if (lines.length) blocks.push({ kind: 'analysis', title: this.t('pb.v2.r.docOverview'), lines });
    }

    // 对话原文(必选,时间戳自带、恒导出):整段转写逐段输出。
    if (this.exportSegments.length) {
      blocks.push({ kind: 'heading', text: this.t('pb.v2.r.docTranscript') });
      for (const s of this.exportSegments) {
        blocks.push({
          kind: 'qa', speaker: s.speaker, text: s.text,
          timeLabel: s.start != null ? fmtClock(s.start) : undefined
        });
      }
    }

    for (const q of questions) {
      blocks.push({ kind: 'heading', text: `Q${q.sequence} ${q.questionText}` });
      if (q.askedBy) blocks.push({ kind: 'para', text: this.fmt('pb.v2.r.docAskedBy', { v: q.askedBy }) });
      blocks.push({
        kind: 'qa', speaker: q.askedBy ?? this.t('pb.v2.r.docInterviewer'), text: q.questionText,
        timeLabel: q.askedAtSeconds != null ? fmtClock(q.askedAtSeconds) : undefined
      });
      if (q.myAnswerText) {
        blocks.push({ kind: 'qa', speaker: this.t('pb.v2.r.docMe'), text: q.myAnswerText });
      }
      const review = parseQuestionReview(q.reviewJson);
      if (sel.has('analysis') && review) {
        const lines: string[] = [];
        if (review.overall != null) lines.push(this.fmt('pb.v2.r.docQScore', { n: review.overall }));
        const dims = review.dims ?? {};
        const dimKeys: [string, string][] = [
          ['relevance', 'pb.v2.dim.relevance'], ['depth', 'pb.v2.dim.depth'],
          ['expression', 'pb.v2.dim.expression'], ['pronunciation', 'pb.v2.dim.pronunciation']
        ];
        for (const [k, labelKey] of dimKeys) {
          const d = dims[k as keyof typeof dims];
          if (!d) continue;
          const label = this.t(labelKey);
          if (d.score == null) lines.push(this.fmt('pb.v2.dim.noScore', { label }));
          else lines.push(this.fmt('pb.v2.dim.scored', { label, score: d.score })
            + `${d.comment ? ' —— ' + d.comment : ''}${d.fix ? ' ' + this.fmt('pb.v2.r.docFix', { v: d.fix }) : ''}`);
        }
        if (lines.length) blocks.push({ kind: 'analysis', title: this.t('pb.v2.r.docQAnalysis'), lines });
      }
      if (sel.has('std') && review) {
        const sp = review.stdSpoken?.user || review.stdSpoken?.ai;
        const te = review.stdTechnical?.user || review.stdTechnical?.ai;
        if (sp) blocks.push({ kind: 'std', label: this.t('pb.v2.r.docStdSpoken'), text: sp });
        if (te) blocks.push({ kind: 'std', label: this.t('pb.v2.r.docStdTechnical'), text: te });
      }
    }
    return {
      title: this.fmt('pb.v2.r.docTitle', { company: e.companyName, n: r.order, stage: r.stage }),
      subtitle: `${e.role}${r.scheduledDate ? ' · ' + r.scheduledDate : ''}${r.interviewers ? ' · ' + r.interviewers : ''}`,
      blocks
    };
  }
}

function fmtClock(sec: number): string {
  const m = Math.floor(sec / 60);
  const s = Math.floor(sec % 60);
  return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
