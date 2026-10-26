import {
  Component, EventEmitter, HostListener, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  InterviewAsset, InterviewDetail, InterviewRound, KnowledgeItem
} from '../../../../core/models/api.models';
import { QaZoomComponent } from '../../../../core/qa-zoom/qa-zoom.component';
import { QaZoomService } from '../../../../core/qa-zoom/qa-zoom.service';
import { TtsPlayerService } from '../../../../core/tts/tts-player.service';
import { PlaybookV2Api } from '../playbook-v2.api';
import { serverFieldErrorSummary } from '../../../../core/api/validation-errors.helpers';
import {
  PrepBlock, PrepQuestionItem, PrepReport, PrepSection, prepStdText, safeJson
} from '../v2.models';
import {
  classifyPrepStage, migrateLegacyPrepReport, prepFrameworkSectionIds, prepQuestionExportBlocks,
  type PrepStageClass
} from './prep-report.helpers';
import { ExportDialogComponent, ExportDialogData } from '../export/export-dialog.component';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { latestPrepInputAsset, makeV2Fmt, makeV2T, normalizeThreadSubject, PREP_INPUT_EXPERIENCES, PREP_INPUT_RESUME, type PrepInputPurpose } from '../v2.helpers';
import { ExportBlock, ExportDoc } from '../export/export-doc';

/**
 * 备考报告(2026-10-20 定稿):按模拟面试流程排布的完整备考文档。
 * 左侧要点目录(点击定位、滚动高亮),右侧正文;题目与 Tech Stack 双向同步 ——
 * 已入库的题答案以 Tech Stack 那份为准(同一条记录),在这里改即同步回去;
 * 未入库的题选分类后一键加入,自动带来源标签(公司 · Round · 日期)。
 * 与短板相关的题置顶标红。Guidance 类旧材料在本页签底部并列,不丢失。
 */
@Component({
  selector: 'app-prep-report',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, QaZoomComponent],
  template: `
    <div class="prep" [style.--qa-scale]="zoom.scale()">
      @if (notice() && noticeIsError()) { <p class="notice warn">{{ notice() }}</p> }
      @if (tts.error()) { <p class="notice warn">{{ tts.error() }}</p> }

      <!-- 票159：Prep 两框删除，换为读回区——显示本轮当前备考输入资产原文；
           读回与材料列表是同一资产（编辑/删除都在统一入口与材料列表完成）。 -->
      <!-- 票198：@if 外移到 readback 包裹层，隐藏时空 div 不渲染（原空 div 的 margin-top:14px 是页头与正文间空白带的一部分）。 -->
      @if (!hidePrepInputs) {
      <div class="readback">
        <div class="rb-block">
          <div class="rb-head">
            <span class="rb-title">{{ t('pb.v2.p.inputExpTitle') }}</span>
            @if (expAsset(); as ea) {
              <button mat-stroked-button [disabled]="!expLoaded()" (click)="editInput.emit({ purpose: 'experiences', assetId: ea.id, text: expText() })">{{ t('pb.v2.p.editInput') }}</button>
            } @else {
              <button mat-stroked-button (click)="editInput.emit({ purpose: 'experiences', assetId: null, text: '' })">{{ t('pb.v2.p.addInput') }}</button>
            }
          </div>
          @if (expAsset()) {
            <pre class="rb-text">{{ expLoaded() ? expText() : t('pb.v2.d.loading') }}</pre>
          } @else {
            <p class="rb-empty">{{ t('pb.v2.p.noInputExp') }}</p>
          }
        </div>
        <div class="rb-block">
          <div class="rb-head">
            <span class="rb-title">{{ t('pb.v2.p.inputResumeTitle') }}</span>
            @if (resumeAsset(); as ra) {
              <button mat-stroked-button [disabled]="!resumeLoaded()" (click)="editInput.emit({ purpose: 'resume', assetId: ra.id, text: resumeText() })">{{ t('pb.v2.p.editInput') }}</button>
            } @else {
              <button mat-stroked-button (click)="editInput.emit({ purpose: 'resume', assetId: null, text: '' })">{{ t('pb.v2.p.addInput') }}</button>
            }
          </div>
          @if (resumeAsset()) {
            <pre class="rb-text">{{ resumeLoaded() ? resumeText() : t('pb.v2.d.loading') }}</pre>
          } @else {
            <p class="rb-empty">{{ t('pb.v2.p.noInputResume') }}</p>
          }
        </div>
      </div>
      }
        <div class="prep-body">
          <nav class="toc">
            @for (s of displayReport()!.sections; track s.id) {
              <button type="button" class="toc-item" [class.on]="activeSec() === s.id"
                      (click)="jumpTo(s.id)">{{ s.title }}@if (s.scaffold) { <span class="sec-tag">{{ t('pb.v2.p.scaffoldTag') }}</span> }@if (s.unmapped) { <span class="sec-tag">{{ t('pb.v2.p.legacyTag') }}</span> }</button>
            }
          </nav>
          <div class="sections">
            <!-- 票203：标题行整体移入正文列顶部（不再横跨整宽压在菜单上方）——标题即正文列第一行，与下方 Warm-up 等小节 h4 同一竖列左沿对齐；菜单列与该行顶部齐平（同 grid 行起始）。正文首节 h4 仍不渲染，防重复；按钮一字不动。 -->
            <div class="prep-head">
              <div class="ph-title">{{ firstSecTitle() }}@if (firstSecScaffold()) { <span class="sec-tag">{{ t('pb.v2.p.scaffoldTag') }}</span> }@if (firstSecUnmapped()) { <span class="sec-tag">{{ t('pb.v2.p.legacyTag') }}</span> }</div>
              <span class="spacer"></span>
              <div class="ph-actions">
                <app-qa-zoom [compact]="true"></app-qa-zoom>
                @if (displayReport()?.sections?.length) {
                  <button mat-icon-button (click)="openExport()" [matTooltip]="t('pb.v2.p.exportPrep')" [attr.aria-label]="t('pb.v2.p.exportPrep')"><mat-icon>download</mat-icon></button>
                }
                @if (hasHistory()) {
                  <button mat-icon-button (click)="restorePrev()" [disabled]="busy()"
                          [matTooltip]="t('pb.v2.p.restorePrev')" [attr.aria-label]="t('pb.v2.p.restorePrev')"><mat-icon>history</mat-icon></button>
                }
                <button mat-icon-button (click)="generate()" [disabled]="busy()"
                        [matTooltip]="report() ? t('pb.v2.p.regenerate') : t('pb.v2.p.generate')"
                        [attr.aria-label]="report() ? t('pb.v2.p.regenerate') : t('pb.v2.p.generate')">
                  <mat-icon>auto_awesome</mat-icon>
                </button>
              </div>
            </div>
            @if (displayReport()?.sources?.details?.length) {
              <div class="src-row">
                @for (d of displayReport()!.sources!.details!; track d.key) {
                  <span class="src-detail">{{ sourceLabel(d.key) }} · {{ sourceStatusText(d.status) }}@if (d.count != null) { · {{ d.count }} }@if (d.note) { — {{ d.note }} }</span>
                }
              </div>
            }
            @for (s of displayReport()!.sections; track s.id) {
              <section class="psec" [id]="'prep-sec-' + s.id" data-testid="prep-section">
                @if (!$first) { <h4>{{ s.title }}@if (s.scaffold) { <span class="sec-tag">{{ t('pb.v2.p.scaffoldTag') }}</span> }@if (s.unmapped) { <span class="sec-tag">{{ t('pb.v2.p.legacyTag') }}</span> }</h4> }
                @for (b of s.blocks ?? []; track $index) {
                  @if (b.type === 'script') {
                    <div class="script-box">
                      @if (b.title) { <div class="sb-title">{{ b.title }}</div> }
                      @if (editingBlock() === s.id + ':' + $index) {
                        <textarea class="fld qa-text" rows="6" [ngModel]="blockDraft()"
                                  (ngModelChange)="blockDraft.set($event)"></textarea>
                        <div class="row">
                          <button mat-raised-button color="primary" (click)="saveBlock(s, $index)">{{ t('common.save') }}</button>
                          <button mat-button (click)="editingBlock.set(null)">{{ t('common.cancel') }}</button>
                        </div>
                      } @else {
                        <p class="qa-text pre">{{ b.userText || b.text }}</p>
                        <div class="row">
                          <button type="button" class="mini-btn" (click)="playText(b.userText || b.text || '')">
                            {{ tts.playingKey() === tts.keyFor(b.userText || b.text || '') ? t('pb.v2.p.stop') : t('pb.v2.p.play') }}
                          </button>
                          <button type="button" class="mini-btn" (click)="editBlock(s, $index, b)">{{ t('pb.v2.p.edit') }}</button>
                          @if (b.userText) { <span class="mine-tag">{{ t('pb.v2.p.mineTag') }}</span> }
                        </div>
                      }
                    </div>
                  } @else if (b.type === 'points' || b.type === 'list') {
                    @if (b.title) { <div class="sb-title">{{ b.title }}</div> }
                    @if (b.provenance) { <div class="prov-tag">{{ t('pb.v2.p.addedTag') }} · {{ provDate(b.provenance) }}</div> }
                    <ul class="pts pts-scale">
                      @for (it of b.items ?? []; track $index) { <li>{{ it }}</li> }
                    </ul>
                  } @else if (b.text) {
                    <p class="qa-text pre">{{ b.text }}</p>
                  }
                }
                @for (q of sortedQuestions(s); track $index) {
                  <article class="pq" [class.weak]="q.weaknessRelated" data-testid="prep-question">
                    <div class="pq-head">
                      @if (q.weaknessRelated) { <span class="weak-pin">{{ t('pb.v2.p.weakPin') }}</span> }
                      <span class="pq-text qa-text">{{ q.question }}</span>
                    </div>
                    @if (q.note) {
                      <div class="pq-note">{{ q.note }}</div>
                    }
                    @if (q.sourceTags?.length) {
                      <div class="tags">@for (tg of q.sourceTags!; track $index) { <span class="tag">{{ tg }}</span> }</div>
                    }
                    @if (q.knowledgeId) {
                      <div class="sync-row">
                        <span class="sync-tag">{{ t('pb.v2.p.inStack') }}</span>
                        <span class="sync-note">{{ t('pb.v2.p.syncNote') }}</span>
                      </div>
                      <div class="sync-answer">
                        <div class="sa-label">{{ t('pb.v2.p.stackAnswerLabel') }}</div>
                        @if (editingSyncQ() === q.knowledgeId) {
                          <textarea class="fld qa-text" rows="5" [ngModel]="syncDraft()"
                                    (ngModelChange)="syncDraft.set($event)"></textarea>
                          <div class="row">
                            <button mat-raised-button color="primary" (click)="saveSyncAnswer(q)">{{ t('pb.v2.p.saveSync') }}</button>
                            <button mat-button (click)="editingSyncQ.set(null)">{{ t('common.cancel') }}</button>
                          </div>
                        } @else {
                          <p class="qa-text pre">{{ knowledgeAnswer(q.knowledgeId) || t('pb.v2.p.stackNoAnswer') }}</p>
                          <div class="row">
                            <button type="button" class="mini-btn"
                                    (click)="playText(knowledgeAnswer(q.knowledgeId))">{{ t('pb.v2.p.play') }}</button>
                            <button type="button" class="mini-btn" (click)="editSyncAnswer(q)">{{ t('pb.v2.p.editSync') }}</button>
                          </div>
                        }
                      </div>
                    } @else {
                      @for (std of prepStds(q); track std.key) {
                        @if (std.text || true) {
                          <div class="std-line">
                            <span class="std-label">{{ std.label }}</span>
                            @if (std.isUser) { <span class="mine-tag">{{ t('pb.v2.p.mineTag') }}</span> }
                            <span class="spacer"></span>
                            <button type="button" class="mini-btn" [disabled]="!std.text"
                                    (click)="playText(std.text)">▶</button>
                            <button type="button" class="mini-btn"
                                    (click)="editPrepStd(s, q, std.key, std.text)">✎</button>
                          </div>
                          @if (editingPrepStd() === s.id + '|' + q.question + '|' + std.key) {
                            <textarea class="fld qa-text" rows="4" [ngModel]="prepStdDraft()"
                                      (ngModelChange)="prepStdDraft.set($event)"></textarea>
                            <div class="row">
                              <button mat-raised-button color="primary"
                                      (click)="savePrepStd(s, q, std.key)">{{ t('common.save') }}</button>
                              <button mat-button (click)="editingPrepStd.set(null)">{{ t('common.cancel') }}</button>
                            </div>
                          } @else if (std.text) {
                            <p class="qa-text pre std-p">{{ std.text }}</p>
                          }
                        }
                      }
                      <div class="join-row">
                        <label>{{ t('pb.v2.p.category') }}
                          <input class="fld sm" list="prep-topics"
                                 [ngModel]="joinCategory()[q.question]"
                                 (ngModelChange)="setJoinCat(q.question, $event)"
                                 [placeholder]="t('pb.v2.p.categoryPh')">
                          <datalist id="prep-topics">
                            @for (tp of topics(); track tp) { <option [value]="tp"></option> }
                          </datalist>
                        </label>
                        <button mat-stroked-button (click)="joinStack(s, q)" [disabled]="busy()">
                          {{ t('pb.v2.p.joinStack') }}
                        </button>
                        <span class="join-note">{{ fmt('pb.v2.p.sourceAuto', { company: entry().companyName, n: round().order, date: round().scheduledDate ?? '' }) }}</span>
                      </div>
                    }
                  </article>
                }
              </section>
            }

            @if (guidanceAssets().length) {
              <section class="psec">
                <h4>{{ t('pb.v2.p.guidanceTitle') }}</h4>
                <p class="hint">{{ t('pb.v2.p.guidanceHint') }}</p>
                <ul class="pts">
                  @for (g of guidanceAssets(); track g.id) { <li>{{ g.fileName }}</li> }
                </ul>
              </section>
            }
          </div>
        </div>
    </div>
  `,
  styles: [`
    .prep { padding: 4px 2px; }
    .qa-text { font-size: calc(15px * var(--qa-scale, 1)); line-height: 1.65; }
    .pre { white-space: pre-wrap; }
    .prep-head { display: flex; align-items: flex-start; gap: 8px; flex-wrap: wrap; margin-bottom: 8px; } /* 票203：标题行现为正文列首行，下边距取 h4 同款 8px，使首节要点与标题间距同其他小节一致。 */
    /* 票190：三个控件成组，窄屏整组落到标题下一行、不散不溢出。 */
    .ph-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    /* 票205:页头图标按钮统一成 196 三按钮基准（原 Material 默认 48px 圆钮 + 24px 图标 + 生成钮 primary 蓝，与顶部三按钮不一致）。
       只统一样式：图标字形、顺序、tooltip、点击行为、busy disabled 一字不动；百分比数字 .zpct 不是图标、不动。 */
    .ph-actions button { flex: 0 0 auto; display: inline-flex; align-items: center; justify-content: center;
      width: 36px; height: 36px; padding: 0; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px;
      background: #ffffff; color: var(--yi-text-muted, #475569); cursor: pointer;
      transition: background .15s ease, border-color .15s ease, color .15s ease; }
    .ph-actions button:hover { background: #f6f8fb; border-color: var(--yi-border, #e2e8f0); color: var(--yi-text, #0f172a); }
    .ph-actions button mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .ph-title { font-size: 16px; font-weight: 600; }
    /* 票207：脚手架/旧节标记与追加块标记（沿用 .tag pill 视觉，不引入新色板）。 */
    .sec-tag { font-size: 12px; font-weight: 500; border: 1px solid var(--yi-border, #e2e8f0);
               border-radius: 999px; padding: 1px 8px; color: var(--yi-text-muted, #475569);
               margin-left: 6px; white-space: nowrap; }
    .prov-tag { font-size: 12px; color: var(--yi-text-muted, #475569); margin: 2px 0 4px; }
    .src-row { display: flex; gap: 2px 14px; flex-wrap: wrap; margin: 0 0 12px; }
    .src-row .src-detail { font-size: 12px; color: var(--yi-text-muted, #475569); white-space: nowrap; }
    .ph-sub { font-size: 13px; color: var(--yi-text-muted, #475569); margin-top: 2px; max-width: 560px; }
    .ph-sub.src-details { display: flex; gap: 2px 14px; flex-wrap: wrap; max-width: none; }
    .src-detail { white-space: nowrap; }
    .spacer { flex: 1; }
    .notice { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .notice.warn { color: #dc2626; }
    /* 票159 读回区：两块输入原文 + 生成口径提示；窄屏自动单列（父级 pv-grid 模式复用）。 */
    .readback { display: flex; flex-direction: column; gap: 14px; margin-top: 14px; margin-bottom: 12px; max-width: 720px; } /* 票198：下边距承接原 prep-body 上边距 12px，hidePrepInputs 恢复显示时间距不变。 */
    .rb-head { display: flex; align-items: center; gap: 10px; margin-bottom: 6px; }
    .rb-title { font-size: 14px; font-weight: 600; }
    .rb-text { font-size: 14px; line-height: 1.6; white-space: pre-wrap; word-break: break-word;
               margin: 0; padding: 10px 12px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; }
    .rb-empty { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 0; }
    .fld { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 8px; padding: 8px 10px;
           font-size: 14px; background: transparent; color: inherit; box-sizing: border-box; }
    .fld.sm { padding: 5px 8px; font-size: 14px; }
    .hint { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .prep-body { display: grid; grid-template-columns: 170px 1fr; gap: 20px; margin-top: 0; } /* 票198：页头与正文间空白去掉（隐藏态下空 readback 已不渲染）。 */
    @media (max-width: 760px) { .prep-body { grid-template-columns: 1fr; } .toc { position: static !important; flex-direction: row !important; flex-wrap: wrap; } }
    .toc { position: sticky; top: 8px; align-self: start; display: flex; flex-direction: column; gap: 2px; }
    .toc-item { text-align: left; border: none; background: transparent; color: inherit; cursor: pointer;
                font-size: 14px; padding: 6px 9px; border-radius: 6px; }
    .toc-item.on { background: color-mix(in srgb, var(--yi-accent, #2563eb) 14%, transparent); font-weight: 700; }
    .psec { margin-bottom: 22px; scroll-margin-top: 10px; }
    .psec h4 { font-size: 16px; font-weight: 600; margin: 0 0 8px; }
    .script-box { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 11px 13px; margin: 8px 0; }
    .sb-title { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); margin-bottom: 4px; }
    .script-box p { margin: 2px 0 8px; }
    .pts { margin: 4px 0 8px; padding-left: 20px; font-size: 14px; line-height: 1.65; }
    /* 票184 修法 B：仅要点块跟随 Text size 缩放（guidance 文件名列表仍用 .pts 固定字号）。 */
    .pts-scale { font-size: calc(14px * var(--qa-scale, 1)); }
    .row { display: flex; gap: 8px; align-items: center; margin-top: 6px; flex-wrap: wrap; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .mine-tag { font-size: 12px; font-weight: 700; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 1px 7px; }
    .pq { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 11px 13px; margin: 9px 0; }
    .pq.weak { border-color: #fef2f2; background: color-mix(in srgb, #fef2f2 5%, transparent); }
    .pq-head { display: flex; gap: 8px; align-items: flex-start; }
    .pq-note { font-size: 12px; color: #666; margin-top: 2px; }
    .weak-pin { font-size: 12px; font-weight: 700; color: #dc2626; white-space: nowrap; margin-top: 3px; }
    .pq-text { font-weight: 600; }
    .tags { display: flex; gap: 5px; flex-wrap: wrap; margin-top: 5px; }
    .tag { font-size: 12px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 999px; padding: 1px 8px;
           color: var(--yi-text-muted, #475569); }
    .sync-row { display: flex; gap: 8px; align-items: center; margin-top: 8px; flex-wrap: wrap; }
    .sync-tag { font-size: 12px; font-weight: 700; color: #16a34a; border: 1px solid #f0fdf4;
                border-radius: 999px; padding: 2px 9px; }
    .sync-note { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .sync-answer { margin-top: 6px; }
    .sa-label { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); }
    .sync-answer p { margin: 4px 0; }
    .std-line { display: flex; align-items: center; gap: 8px; margin-top: 9px; }
    .std-label { font-size: 13px; font-weight: 700; color: var(--yi-accent, #2563eb); }
    .std-p { margin: 4px 0 2px; }
    .join-row { display: flex; gap: 10px; align-items: flex-end; margin-top: 10px; flex-wrap: wrap;
                border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 10px; }
    .join-row label { display: flex; flex-direction: column; gap: 4px; font-size: 13px;
                      color: var(--yi-text-muted, #475569); }
    .join-note { font-size: 13px; color: var(--yi-text-muted, #475569); }
  `]
})
export class PrepReportComponent {
  readonly entry = input.required<InterviewDetail>();
  readonly round = input.required<InterviewRound>();
  readonly guidanceAssets = input<InterviewAsset[]>([]);
  /** 票159：本轮材料（由轮次卡按轮次过滤后传入），读回区据此取当前备考输入资产。 */
  readonly roundAssets = input<InterviewAsset[]>([]);
  /** 票159：统一入口“保存并生成”的单次生成请求（token 递增）；消费后即由父级清零。 */
  readonly generateRequest = input<number>(0);
  @Output() readonly changed = new EventEmitter<void>();
  /** 票159：读回区编辑 / 添加输入——父级打开统一对话框（预填原文与目标轮次）。 */
  @Output() readonly editInput = new EventEmitter<{ purpose: 'experiences' | 'resume'; assetId: string | null; text: string }>();
  /** 票159：生成请求已被消费（父级据此清零，避免切页签回来时重复触发）。 */
  @Output() readonly generateConsumed = new EventEmitter<void>();
  /** 票174：Prep 页两读回区块暂隐藏（隐藏不是删除：资产、生成取用逻辑、统一入口、i18n 键一律保留；恢复时改回 false 即一处恢复）。 */
  readonly hidePrepInputs = true;

  private readonly api = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);
  readonly zoom = inject(QaZoomService);
  readonly tts = inject(TtsPlayerService);

  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  // 票190：notice 原兼三用（本地聚合说明 / 生成成功 / 生成失败）；页头描述全删后，
  // 渲染只留失败一种（错误反馈不能丢），故用本标志区分，说明类文本不再渲染。
  readonly noticeIsError = signal(false);
  /** 票159：读回区当前资产原文（经 getTranscript 取回；列表 DTO 本身不带正文）。 */
  readonly expText = signal('');
  readonly resumeText = signal('');
  private readonly expLoadedFor = signal<string | null>(null);
  private readonly resumeLoadedFor = signal<string | null>(null);
  readonly activeSec = signal('');
  readonly editingBlock = signal<string | null>(null);
  readonly blockDraft = signal('');
  readonly editingSyncQ = signal<string | null>(null);
  readonly syncDraft = signal('');
  readonly editingPrepStd = signal<string | null>(null);
  readonly prepStdDraft = signal('');
  readonly joinCategory = signal<Record<string, string>>({});
  readonly knowledge = signal(new Map<string, KnowledgeItem>());
  readonly topics = signal<string[]>([]);

  readonly report = computed(() => safeJson<PrepReport>(this.round().prepReportJson));

  /** 票159：本轮当前备考输入资产（同轮同 purpose 最新一份为准，旧份不入选）。 */
  readonly expAsset = computed(() => latestPrepInputAsset(this.roundAssets(), this.round().id, PREP_INPUT_EXPERIENCES));
  readonly resumeAsset = computed(() => latestPrepInputAsset(this.roundAssets(), this.round().id, PREP_INPUT_RESUME));
  readonly expLoaded = computed(() => { const a = this.expAsset(); return !!a && this.expLoadedFor() === a.id; });
  readonly resumeLoaded = computed(() => { const a = this.resumeAsset(); return !!a && this.resumeLoadedFor() === a.id; });

  /**
   * 票159：取当前备考输入资产原文。已取过当前资产 id 的直接返回缓存；
   * 迟到回包（取数途中当前资产已换）不覆盖，避免读回与生成读到旧文本。
   * 生成入口调用本方法自保证新鲜——“保存并生成”跳转过来时即使展示取数尚未完成，
   * 生成用的也是刚保存的新文本，而非旧文本或空。
   */
  private async loadPrepText(purpose: PrepInputPurpose): Promise<string> {
    const isExp = purpose === PREP_INPUT_EXPERIENCES;
    const asset = isExp ? this.expAsset() : this.resumeAsset();
    const textSig = isExp ? this.expText : this.resumeText;
    const loadedSig = isExp ? this.expLoadedFor : this.resumeLoadedFor;
    if (!asset) { textSig.set(''); loadedSig.set(null); return ''; }
    if (loadedSig() === asset.id) return textSig();
    const text = await this.api.getTranscript(this.entry().id, asset.id).toPromise()
      .then(t => t?.transcriptText ?? '').catch(() => '');
    const now = isExp ? this.expAsset() : this.resumeAsset();
    if (now?.id === asset.id) { textSig.set(text); loadedSig.set(asset.id); }
    return text;
  }

  /**
   * 展示用报告:已生成 → 用生成的（票207：先过读侧迁移，旧版 8 节变规范结构，
   * 存储一字不动）;未生成 → 默认框架(票133 定稿 §4.1/§10.1)。
   * 框架与生成报告同构(13 节位顺序 + 左目录 + 滚动高亮),分类走 §4.2 镜像表;
   * 文案全走核心字典 pb.v2 三语键,不调 AI、不落库;读回区(票159)保留在框架上方。
   */
  readonly displayReport = computed<PrepReport | null>(() => {
    const stored = this.report();
    if (!stored) return this.frameworkReport();
    const cls = classifyPrepStage(this.round().stage);
    return migrateLegacyPrepReport(stored, {
      expectedIds: prepFrameworkSectionIds(cls),
      scaffoldSection: id => this.frameworkSection(id, cls),
    });
  });

  /** 票198：页头左标题 = 正文第一小节标题（正文首节 h4 不再渲染，防重复）；报告无小节时为空串。 */
  readonly firstSecTitle = computed(() => {
    const secs = this.displayReport()?.sections;
    return secs && secs.length > 0 ? secs[0].title : '';
  });

  /** 票207：首节为脚手架/旧节时，页头标题旁同样打标记（首节 h4 本就不渲染）。 */
  readonly firstSecScaffold = computed(() => !!this.displayReport()?.sections?.[0]?.scaffold);
  readonly firstSecUnmapped = computed(() => !!this.displayReport()?.sections?.[0]?.unmapped);

  /** 票207：当前存量报告是否有 Generate 前快照（有才渲染“恢复上一版”）。 */
  readonly hasHistory = computed(() => (this.report()?.history?.length ?? 0) > 0);

  /** 票207：追加块标记的日期部分（ISO 取前 10 位）。 */
  provDate(p: { at: string } | null | undefined): string {
    return (p?.at ?? '').slice(0, 10);
  }

  /** 票207：单节框架内容（frameworkReport 与迁移脚手架共用同一组代码）。 */
  private frameworkSection(id: string, cls: PrepStageClass): PrepSection | null {
    const pts = (keys: string[]): PrepBlock[] =>
      [{ type: 'points', items: keys.map(k => this.t(k)) }];
    const sec = (sid: string, name: string, keys: string[]): PrepSection => ({
      id: sid, title: this.t(`pb.v2.p.fw.${name}.title`), blocks: pts(keys), questions: []
    });
    const p = (name: string, n: number): string[] =>
      Array.from({ length: n }, (_, i) => `pb.v2.p.fw.${name}.p${i + 1}`);
    const group: Record<string, string> = {
      'round-brief': 'roundBrief', greeting: 'greeting', 'intro-listen': 'introListen',
      'self-intro': 'selfIntro', 'deep-dive': 'deepDive', 'coding-round': 'codingRound',
      'design-round': 'designRound', behavioral: 'behavioral', 'weakness-plan': 'weaknessPlan',
      'ask-back': 'askBack', 'comp-process': 'compProcess', closing: 'closing'
    };
    /** ML-FIX-PREP:各节要点数(默认 4;新增段落的节在此登记,缺省仍为 4)。 */
    const FW_POINTS: Record<string, number> = {
      greeting: 5, 'design-round': 7, 'weakness-plan': 5, closing: 7,
    };
    if (id === 'tech-qa') {
      const blocks = [...pts(p('techQa', 4))];
      if (!cls.isDesign) {
        blocks.push({
          type: 'points', title: this.t('pb.v2.p.fw.designCard.title'),
          items: p('designCard', 3).map(k => this.t(k))
        });
      }
      return { id, title: this.t('pb.v2.p.fw.techQa.title'), blocks, questions: [] };
    }
    const name = group[id];
    if (!name) return null;
    return sec(id, name, p(name, FW_POINTS[id] ?? 4));
  }

  private frameworkReport(): PrepReport {
    const cls = classifyPrepStage(this.round().stage);
    const sections: PrepSection[] = [];
    for (const id of prepFrameworkSectionIds(cls)) {
      const s = this.frameworkSection(id, cls);
      if (s) sections.push(s);
    }
    return { sections };
  }

  /** Sources 明细行的标签(后端 details.key → 文案键)。 */
  sourceLabel(key: string): string {
    switch (key) {
      case 'questionBank': return this.t('pb.v2.p.srcQb');
      case 'company': return this.t('pb.v2.p.srcCompany');
      case 'web': return this.t('pb.v2.p.srcWeb');
      case 'glassdoor': return this.t('pb.v2.p.srcGlassdoor');
      case 'manualExperiences': return this.t('pb.v2.p.srcManual');
      case 'communications': return this.t('pb.v2.p.srcComm');
      default: return key;
    }
  }

  /** 票207：Sources 明细行的状态词（后端旧口径 → 展示词）。 */
  sourceStatusText(status: string): string {
    switch (status) {
      case 'fetched': return this.t('pb.v2.p.srcStIncluded');
      case 'disabled': return this.t('pb.v2.p.srcStDisabled');
      case 'notConfigured': return this.t('pb.v2.p.srcStNotConfigured');
      case 'noData': return this.t('pb.v2.p.srcStNoData');
      case 'failed': return this.t('pb.v2.p.srcStFailed');
      default: return status;
    }
  }

  /** 票207：恢复上一版（后端弹出最近一条 Generate 前快照；无快照时按钮不渲染）。 */
  async restorePrev(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.notice.set(null);
    this.noticeIsError.set(false);
    try {
      await this.api.restorePrepReport(this.entry().id, this.round().id).toPromise();
      this.changed.emit();
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.p.generateFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.rec.unknownError') }));
      this.noticeIsError.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  private loadedKey = '';

  constructor() {
    // 默认框架(未生成)时目录也要有默认高亮段 —— 与生成报告同一口径。
    effect(() => {
      const secs = this.displayReport()?.sections;
      if (secs?.length && !this.activeSec()) this.activeSec.set(secs[0].id);
    }, { allowSignalWrites: true });

    effect(() => {
      const r = this.report();
      const key = this.round().id + ':' + (r?.generatedAt ?? '');
      if (key === this.loadedKey) return;
      this.loadedKey = key;
      if (r?.sections.length && !this.activeSec()) this.activeSec.set(r.sections[0].id);
      // 拉全量 Tech Stack:分类候选 + 把备考题与已有条目按(来源条目+题面)对齐 ——
      // 对齐上的题就是同一条记录:显示「已在 Tech Stack」,答案读写都走那一份,
      // 并把 knowledgeId 回写进报告,之后不再重复匹配。
      this.api.listKnowledge().subscribe({
        next: items => {
          const map = new Map<string, KnowledgeItem>();
          for (const it of items) map.set(it.id, it);
          this.knowledge.set(map);
          this.topics.set([...new Set(items.map(i => i.topic).filter(Boolean))].sort());
          const rep = this.report();
          if (!rep) return;
          let dirty = false;
          const next: PrepReport = JSON.parse(JSON.stringify(rep)) as PrepReport;
          for (const sec of next.sections) {
            for (const q of sec.questions ?? []) {
              if (q.knowledgeId) continue;
              const hit = items.find(k =>
                k.sourceInterviewEntryId === this.entry().id &&
                (k.question ?? '').trim().toLowerCase() === q.question.trim().toLowerCase());
              if (hit) { q.knowledgeId = hit.id; dirty = true; }
            }
          }
          if (dirty) void this.persist(next);
        },
        error: () => undefined
      });
    }, { allowSignalWrites: true });

    // 票159：读回区展示取数——当前资产变化时拉取原文（取数本身带迟到回包守门）。
    effect(() => {
      const a = this.expAsset();
      if (a && this.expLoadedFor() !== a.id) void this.loadPrepText(PREP_INPUT_EXPERIENCES);
    }, { allowSignalWrites: true });
    effect(() => {
      const a = this.resumeAsset();
      if (a && this.resumeLoadedFor() !== a.id) void this.loadPrepText(PREP_INPUT_RESUME);
    }, { allowSignalWrites: true });

    // 票159：统一入口“保存并生成”请求——每个 token 只触发一次生成；
    // 触发即通知父级清零，避免切页签回来、组件重建时重复生成。
    effect(() => {
      const token = this.generateRequest();
      if (!token || token === this.consumedGenToken) return;
      this.consumedGenToken = token;
      this.generateConsumed.emit();
      void this.generate();
    }, { allowSignalWrites: true });
  }

  private consumedGenToken = 0;

  @HostListener('window:scroll')
  onScroll(): void {
    const r = this.displayReport();
    if (!r) return;
    let current = this.activeSec();
    for (const s of r.sections) {
      const el = document.getElementById('prep-sec-' + s.id);
      if (el && el.getBoundingClientRect().top <= 140) current = s.id;
    }
    if (current !== this.activeSec()) this.activeSec.set(current);
  }

  jumpTo(id: string): void {
    this.activeSec.set(id);
    document.getElementById('prep-sec-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  sortedQuestions(s: PrepSection): PrepQuestionItem[] {
    const qs = [...(s.questions ?? [])];
    return qs.sort((a, b) => Number(b.weaknessRelated ?? false) - Number(a.weaknessRelated ?? false));
  }

  knowledgeAnswer(id: string): string {
    const k = this.knowledge().get(id);
    return k?.betterAnswer || k?.myAnswer || '';
  }

  prepStds(q: PrepQuestionItem): { key: 'spoken' | 'technical'; label: string; text: string; isUser: boolean }[] {
    const sp = prepStdText(q.stdSpoken);
    const te = prepStdText(q.stdTechnical);
    return [
      { key: 'spoken', label: this.t('pb.v2.p.spoken'), text: sp.text, isUser: sp.isUser },
      { key: 'technical', label: this.t('pb.v2.p.technical'), text: te.text, isUser: te.isUser }
    ];
  }

  playText(text: string): void {
    if (text) void this.tts.toggle(text);
  }

  // ---------------- 生成 ----------------

  /**
   * 票207：本条目沟通邮件的结构化摘录（只限本条目 jobApplicationId，绝不取其他条目；
   * 摘录式原文截断，不经模型改写）。按 thread 分组（与 comm-log 同口径：meta.threadKey
   * 优先，存量无 threadKey 的按主题现场归一化兜底），组内按发生时间正序。
   * 读取失败 → status failed（后端如实标注、其余来源继续，绝不静默缺失）；
   * 未关联投递 → status none。
   */
  private async loadCommExcerpts(): Promise<{
    text: string | null; status: 'ok' | 'failed' | 'none'; count: number;
  }> {
    const appId = this.entry().jobApplicationId;
    if (!appId) return { text: null, status: 'none', count: 0 };
    try {
      const rows = await this.api.listCommunications(appId).toPromise();
      const items = (rows ?? []).map(c => PlaybookV2Api.commFromJobs(c));
      const groups = new Map<string, { subject: string; items: typeof items }>();
      for (const it of items) {
        const key = it.meta.threadKey
          ? 'th:' + it.meta.threadKey
          : it.subject
            ? 'th:' + normalizeThreadSubject(it.subject).key
            : 'solo:' + it.id;
        const g = groups.get(key) ?? { subject: it.subject ?? '', items: [] };
        g.items.push(it);
        groups.set(key, g);
      }
      const trunc = (s: string, n: number) => (s.length > n ? s.slice(0, n) : s);
      const lines: string[] = [];
      for (const g of groups.values()) {
        const sorted = [...g.items].sort((a, b) => (a.occurredAt ?? '').localeCompare(b.occurredAt ?? ''));
        if (g.subject) lines.push(`Thread: ${g.subject}`);
        for (const it of sorted) {
          const arr = it.meta.arrangement;
          const arrText = arr
            ? [arr.date, arr.time, arr.format, arr.interviewers].filter(Boolean).join(' ')
            : '';
          lines.push(`- [${it.type}] ${it.contactName ?? ''} @ ${it.occurredAt ?? ''}`
            + (arrText ? ` | arrangement: ${arrText}` : '')
            + (it.subject ? ` | subject: ${it.subject}` : '')
            + ` | ${trunc((it.content ?? '').replace(/\s+/g, ' ').trim(), 500)}`);
        }
      }
      return { text: lines.length ? lines.join('\n') : null, status: 'ok', count: items.length };
    } catch {
      return { text: null, status: 'failed', count: 0 };
    }
  }

  async generate(): Promise<void> {
    this.busy.set(true);
    this.notice.set(null);
    this.noticeIsError.set(false);
    try {
      // 票159：入参取自已归档的当前资产原文（空 → null，留空仍只用 JD 与题库生成，
      // 与旧两框行为一致）；loadPrepText 自保证新鲜，不读展示层可能滞后的缓存。
      const experiences = await this.loadPrepText(PREP_INPUT_EXPERIENCES);
      const resumeText = await this.loadPrepText(PREP_INPUT_RESUME);
      // 票207：沟通邮件默认接入（开关默认开：请求级默认 true；读不到/未关联时
      // 状态如实进 sources.details，绝不静默缺失）。
      const comm = await this.loadCommExcerpts();
      const r = await this.api.generatePrepReport(this.entry().id, this.round().id, {
        jdText: this.entry().jdText ?? null,
        resumeText: resumeText.trim() || null,
        experiences: experiences.trim() || null,
        // 生成语言跟随系统语言(账号 preferredLanguage 决定界面语言,同一口径)
        language: this.i18n.lang(),
        communications: comm.text,
        communicationsStatus: comm.status,
        communicationsCount: comm.count,
        includeCommunications: true
      }).toPromise();
      const parsed = safeJson<PrepReport>(r?.prepReportJson);
      this.notice.set(parsed?.generatedBy === 'local-aggregation'
        ? this.t('pb.v2.p.localNotice')
        : this.t('pb.v2.p.generatedOk'));
      this.changed.emit();
    } catch (e) {
      this.notice.set(this.fmt('pb.v2.p.generateFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.rec.unknownError') }));
      this.noticeIsError.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  // ---------------- 稿子块编辑 ----------------

  editBlock(s: PrepSection, idx: number, b: PrepBlock): void {
    this.blockDraft.set(b.userText || b.text || '');
    this.editingBlock.set(s.id + ':' + idx);
  }

  async saveBlock(s: PrepSection, idx: number): Promise<void> {
    const r = this.report();
    if (!r) return;
    const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
    const sec = next.sections.find(x => x.id === s.id);
    if (!sec?.blocks?.[idx]) return;
    sec.blocks[idx].userText = this.blockDraft();
    await this.persist(next);
    this.editingBlock.set(null);
  }

  // ---------------- 备考题标准答案编辑(未入库) ----------------

  editPrepStd(s: PrepSection, q: PrepQuestionItem, key: 'spoken' | 'technical', text: string): void {
    this.prepStdDraft.set(text);
    this.editingPrepStd.set(s.id + '|' + q.question + '|' + key);
  }

  async savePrepStd(s: PrepSection, q: PrepQuestionItem, key: 'spoken' | 'technical'): Promise<void> {
    const r = this.report();
    if (!r) return;
    const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
    const sec = next.sections.find(x => x.id === s.id);
    const item = sec?.questions?.find(x => x.question === q.question);
    if (!item) return;
    const cur = key === 'spoken' ? item.stdSpoken : item.stdTechnical;
    const ai = typeof cur === 'string' ? cur : (cur?.ai ?? null);
    const merged = { ai, user: this.prepStdDraft() };
    if (key === 'spoken') item.stdSpoken = merged; else item.stdTechnical = merged;
    await this.persist(next);
    this.editingPrepStd.set(null);
  }

  // ---------------- Tech Stack 同步 ----------------

  editSyncAnswer(q: PrepQuestionItem): void {
    if (!q.knowledgeId) return;
    this.syncDraft.set(this.knowledgeAnswer(q.knowledgeId));
    this.editingSyncQ.set(q.knowledgeId);
  }

  async saveSyncAnswer(q: PrepQuestionItem): Promise<void> {
    if (!q.knowledgeId) return;
    this.busy.set(true);
    try {
      await this.api.updateKnowledgeAnswer(q.knowledgeId,
        { betterAnswer: this.syncDraft() }).toPromise();
      const fresh = await this.api.getKnowledge(q.knowledgeId).toPromise();
      if (fresh) {
        const map = new Map(this.knowledge());
        map.set(fresh.id, fresh);
        this.knowledge.set(map);
      }
      this.editingSyncQ.set(null);
      this.snack.open(this.t('pb.v2.p.synced'), this.t('common.close'), { duration: 2200 });
    } catch {
      this.snack.open(this.t('pb.v2.p.syncFailed'), this.t('common.close'), { duration: 3000 });
    } finally {
      this.busy.set(false);
    }
  }

  setJoinCat(question: string, cat: string): void {
    this.joinCategory.set({ ...this.joinCategory(), [question]: cat });
  }

  async joinStack(s: PrepSection, q: PrepQuestionItem): Promise<void> {
    const cat = (this.joinCategory()[q.question] || q.category || '').trim();
    if (!cat) {
      this.snack.open(this.t('pb.v2.p.needCategory'), this.t('common.close'), { duration: 3000 });
      return;
    }
    this.busy.set(true);
    try {
      const std = prepJoinAnswer(q);
      const res = await this.api.importCandidates({
        entryId: this.entry().id,
        company: this.entry().companyName,
        date: this.round().scheduledDate ?? undefined,
        roundNo: this.round().order,
        roundStage: this.round().stage,
        applicationId: this.entry().jobApplicationId ?? undefined,
        items: [{
          question: q.question,
          answer: std || q.myAnswer || undefined,
          category: cat,
          difficulty: 3
        }]
      }).toPromise();
      const newId = res?.ids?.[0];
      if (newId) {
        // 把新建条目并回本地题库映射:同步区读的是这份 map,不刷新就会把
        // 刚带答案加入的题显示成「题库里还没有答案」(2026-10-21 审计 X3)。
        const fresh = await this.api.getKnowledge(newId).toPromise().catch(() => null);
        if (fresh) {
          const map = new Map(this.knowledge());
          map.set(fresh.id, fresh);
          this.knowledge.set(map);
        }
        const r = this.report();
        if (r) {
          const next: PrepReport = JSON.parse(JSON.stringify(r)) as PrepReport;
          const sec = next.sections.find(x => x.id === s.id);
          const item = sec?.questions?.find(x => x.question === q.question);
          if (item) { item.knowledgeId = newId; await this.persist(next); }
        }
        this.snack.open(this.t('pb.v2.p.joined'), this.t('common.close'), { duration: 2500 });
      } else {
        this.snack.open(this.t('pb.v2.p.alreadyIn'), this.t('common.close'), { duration: 3000 });
      }
    } catch (e) {
      // 票109:字段级校验错误(如分类超 100)透出后端具体文案,其余失败回落原通用提示。
      this.snack.open(serverFieldErrorSummary(e) ?? this.t('pb.v2.p.joinFailed'),
        this.t('common.close'), { duration: 3000 });
    } finally {
      this.busy.set(false);
    }
  }

  private async persist(next: PrepReport): Promise<void> {
    await this.api.setPrepReport(this.entry().id, this.round().id, JSON.stringify(next)).toPromise();
    this.changed.emit();
  }

  // ---------------- 导出 ----------------

  openExport(): void {
    const e = this.entry();
    const r = this.round();
    const date = new Date().toISOString().slice(0, 10);
    const data: ExportDialogData = {
      title: this.t('pb.v2.p.exportPrep'),
      subtitle: `${e.companyName} — Round ${r.order} ${r.stage}`,
      fileBase: `${e.companyName}-Round${r.order}-${this.t('pb.v2.p.fileSuffix')}-${date}`,
      storageKey: 'prep',
      toggles: [
        { key: 'flow', label: this.t('pb.v2.p.tgFlow'), defaultOn: true },
        { key: 'qa', label: this.t('pb.v2.p.tgQa'), defaultOn: true }
      ],
      buildDoc: sel => this.buildExportDoc(sel)
    };
    this.dialog.open(ExportDialogComponent, { width: '560px', maxWidth: '94vw', data });
  }

  private buildExportDoc(sel: Set<string>): ExportDoc {
    const e = this.entry();
    const r = this.round();
    // 票207：导出取数由 report() 改为 displayReport()——读侧迁移后的规范结构、
    // 从未生成的框架轮次也能导出；框架节标注“未生成框架”。
    const rep = this.displayReport();
    const isFramework = !this.report();
    const blocks: ExportBlock[] = [];
    for (const s of rep?.sections ?? []) {
      const isQaSection = (s.questions?.length ?? 0) > 0;
      if (isQaSection && !sel.has('qa')) continue;
      if (!isQaSection && !sel.has('flow')) continue;
      blocks.push({
        kind: 'heading',
        text: s.title + (s.scaffold || isFramework ? this.t('pb.v2.p.exportFwSuffix') : '')
      });
      for (const b of s.blocks ?? []) {
        if (b.type === 'script') {
          if (b.title) blocks.push({ kind: 'subheading', text: b.title });
          blocks.push({ kind: 'para', text: b.userText || b.text || '' });
        } else if (b.items?.length) {
          if (b.title) blocks.push({ kind: 'subheading', text: b.title });
          blocks.push({ kind: 'list', items: b.items });
        } else if (b.text) blocks.push({ kind: 'para', text: b.text });
      }
      for (const q of s.questions ?? []) {
        blocks.push(...prepQuestionExportBlocks(q, {
          linked: q.knowledgeId ? this.knowledgeAnswer(q.knowledgeId) : '',
          stackLabel: this.t('pb.v2.p.docStdStack'),
          spokenLabel: this.t('pb.v2.p.spoken'),
          technicalLabel: this.t('pb.v2.p.technical')
        }));
      }
    }
    // 票207：导出含 sources.details 明细（每源一条：标签 + 状态 + 数量 + 备注）。
    const details = rep?.sources?.details;
    if (details?.length) {
      blocks.push({ kind: 'heading', text: this.t('pb.v2.p.srcTitle') });
      blocks.push({
        kind: 'list',
        items: details.map(d =>
          `${this.sourceLabel(d.key)}：${this.sourceStatusText(d.status)}`
          + (d.count != null ? `（${d.count}）` : '')
          + (d.note ? ` — ${d.note}` : ''))
      });
    }
    return {
      title: this.fmt('pb.v2.p.docTitle', { company: e.companyName, n: r.order, stage: r.stage }),
      subtitle: `${e.role}${r.scheduledDate ? ' · ' + r.scheduledDate : ''}`,
      blocks
    };
  }
}

/**
 * 「＋ 加入 Tech Stack」带上的初始答案(2026-10-21 用户裁决):用户编辑版优先、
 * 其次 AI 技术版 —— 取值顺序与问答入库 bestAnswerOf 完全一致:
 * 技术版(用户→AI)→ 口语版(用户→AI)→ 我的回答。字符串形态的标准答案是 AI 稿。
 */
function prepJoinAnswer(q: PrepQuestionItem): string {
  const parts = (v: PrepQuestionItem['stdTechnical']): { ai?: string | null; user?: string | null } =>
    typeof v === 'string' ? { ai: v } : (v ?? {});
  const te = parts(q.stdTechnical);
  const sp = parts(q.stdSpoken);
  return te.user || te.ai || sp.user || sp.ai || q.myAnswer || '';
}
