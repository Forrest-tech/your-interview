import {
  Component, EventEmitter, OnDestroy, Output, computed, effect, inject, input, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subscription } from 'rxjs';
import {
  InterviewsApiService, RefreshMailboxResult
} from '../../../../core/api/interviews-api.service';
import { EmailShotDialogComponent } from './email-shot-dialog.component';
import {
  InterviewAsset, InterviewRound
} from '../../../../core/models/api.models';
import { PlaybookV2Api } from '../playbook-v2.api';
import { AssetMeta, TranscriptSegment, safeJson } from '../v2.models';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { derivePhase, extractMeetingLink, formatInterviewHeadTime, formatLabel, isAudioAsset, isPrepInputPurpose, joinNames, makeV2Fmt, makeV2T, normalizeThreadSubject, roundDisplayNo, shortEmailTitle } from '../v2.helpers';
import { normalizeEmailBodyText, segmentEmailBody, EmailBodySegmentation } from '../email-body-segments';

/** 票165：邮件卡挂载即取的全文状态（仅邮件 kind；id → 取数状态）。 */
interface EmailBodyState { loading: boolean; error: boolean; text: string; seg: EmailBodySegmentation | null; }

/**
 * 材料列表(2026-10-20 定稿):本轮(或条目级)的全部原始件。
 * 每件三种视图 —— 解析字段 / 原文 / 截图(有才显示);每件带归属下拉,可在
 * 轮次与沟通记录之间移动。旧 Before/After 阶段材料也并入这里(按 Round/Phase 展示)。
 * 票165:邮件卡改为卡片内展开式解析详情(主题全称 + 会议卡块 + thread 时间线
 * 正文 + round 锚定,默认展开、无 380px cap);截图附件不再单独成卡;
 * File lost 红字改按 integrityStatus === 'Missing' 渲染;非邮件卡一字不动。
 */
@Component({
  selector: 'app-materials-list',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MatDialogModule],
  template: `
    <!-- 票240：条目级邮箱刷新入口（主入口在设置页；此处为条目级快捷入口，仅刷本条目规则）。 -->
    <div class="mail-refresh">
      <button type="button" class="mini-btn" [disabled]="mailRefreshing()" (click)="refreshFromMailbox()">
        <mat-icon>refresh</mat-icon>
        <span>{{ mailRefreshing() ? t('mailbox.refreshing') : t('mailbox.refresh') }}</span>
      </button>
      @if (mailRefreshing()) {
        <button type="button" class="mini-btn" (click)="cancelMailboxRefresh()">{{ t('mailbox.cancel') }}</button>
      }
      @if (mailRefreshResult(); as r) {
        <span class="mail-result">
          @if (r.added === 0 && r.pending === 0 && r.skippedDuplicates === 0 && r.failed === 0) {
            {{ t('mailbox.noNewMail') }}
          } @else {
            {{ mailResultLine(r) }}
          }
        </span>
      }
    </div>
    @if (!displayGroups().length) {
      <p class="empty">{{ t('pb.v2.m.empty') }}</p>
    }
    @for (g of displayGroups(); track g.key) {
      <!-- 票210组头移除口径不变；票236：多封组改渲染 thread 容器（见容器分支注释），单封组仍无组头、仍走旧 email-card；“2 in thread” pill 随旧单卡头行取消（单封组本就不渲染）。 -->
      @if (g.items.length > 1) {
        <!-- 票236：真实邮件式 thread 容器 —— 同 threadKey 只渲染一个容器（容器头 + 消息栏 + 展开体），
             组内各封按收件日正序（displayGroups 已排）；点容器头开合整组，点开后最新一封默认全展开，
             旧封默认收起为一行栏、点栏内联展开/收起；不做 Expand all/Collapse all。
             字段归属全部封级：容器头只放归一化主题 + 封数 + 最新一封发件人/收件日（最新封有链接时附芯片），
             interviewAt/format/Received/Category/截图区一律只在各封展开体内。 -->
        <section class="thread" data-testid="material-thread">
          <div class="th-row">
            <button type="button" class="thread-head" (click)="toggleThread(g)"
                    [attr.aria-expanded]="isThreadOpen(g)">
              <span class="th-subject">{{ g.subject }}</span>
              <span class="th-count">{{ fmt('pb.v2.c.threadCount', { n: g.items.length }) }}</span>
              <span class="th-latest">{{ latestLabel(g) }}</span>
              <span class="chev" [class.open]="isThreadOpen(g)">›</span>
            </button>
            @if (meetingLinkOf(latestOf(g)); as link) {
              <a class="join-chip" [href]="link" target="_blank" rel="noopener"
                 [attr.aria-label]="t('pb.br.countdown.join')">{{ t('pb.br.countdown.join') }}</a>
            }
          </div>
          @if (isThreadOpen(g)) {
            <div class="th-msgs">
              @for (a of g.items; track a.id) {
                <div class="th-msg">
                  <button type="button" class="msg-bar" (click)="toggleMessage(a)"
                          [attr.aria-expanded]="isMessageOpen(a)">
                    <span class="avatar sm">{{ senderInitial(a) }}</span>
                    @if (senderName(a)) { <span class="msg-from">{{ senderName(a) }}</span> }
                    <span class="msg-date">{{ displayDate(a) }}</span>
                    <span class="msg-title">{{ shortTitle(a) }}</span>
                    @if (metaOf(a)?.mailboxNew) { <span class="new-pill">{{ t('mailbox.newImport') }}</span> }
                    <span class="chev" [class.open]="isMessageOpen(a)">›</span>
                  </button>
                  @if (isMessageOpen(a)) {
                    <div class="msg-open">
                      <div class="eo-sender">
                        <span class="avatar sm">{{ senderInitial(a) }}</span>
                        @if (senderName(a)) { <span class="eo-from">{{ senderName(a) }}</span> }
                        <span class="eo-recv">{{ t('pb.v2.m.fReceived') }} {{ displayDate(a) }}</span>
                        @if (metaOf(a)?.category) { <span class="cat-pill">{{ categoryLabel(metaOf(a)!.category!) }}</span> }
                      </div>
                      <div class="eo-title">{{ shortTitle(a) }}</div>
                      @if (metaOf(a); as m) {
                        <dl class="parsed2">
                          @if (m.interviewAt) { <div class="prow"><dt>{{ t('pb.v2.m.fWhen') }}</dt><dd>{{ m.interviewAt }}</dd></div> }
                          @if (m.format) { <div class="prow"><dt>{{ t('pb.v2.m.fFormat') }}</dt><dd>{{ formatLabel(t, m.format) }}</dd></div> }
                          @if (interviewersOf(a)) { <div class="prow"><dt>{{ t('pb.v2.m.fInterviewers') }}</dt><dd>{{ interviewersOf(a) }}</dd></div> }
                          @if (meetingLinkOf(a); as link) { <div class="prow"><dt>{{ t('pb.v2.r.meetingLink') }}</dt><dd><a [href]="link" target="_blank" rel="noopener">{{ t('pb.v2.r.openLink') }}</a></dd></div> }
                        </dl>
                      }
                      @if (bodyState(a); as eb) {
                        @if (eb.loading) { <p class="body-loading">{{ t('pb.v2.d.loading') }}</p> }
                        @else if (eb.error) { <p class="body-loading">{{ t('pb.v2.m.bodyFailed') }}</p> }
                        @else if (!eb.text || !eb.seg) { <p class="body-loading">{{ t('pb.v2.m.noBody') }}</p> }
                        @else if (eb.seg.fallback) { <pre class="body-text-full">{{ eb.text }}</pre> }
                        @else {
                          <div class="timeline">
                            @for (s of eb.seg.timeline; track $index) {
                              <div class="tl-node">
                                <span class="tl-dot"></span>
                                <div class="tl-main">
                                  <div class="tl-meta">{{ s.dateTime }} · {{ s.meetingCard ? t('pb.v2.m.meetingCard') : s.sender }}</div>
                                  <pre class="tl-body">{{ s.body }}</pre>
                                </div>
                              </div>
                            }
                          </div>
                          @for (r of eb.seg.remainder; track $index) {
                            <pre class="tl-rest">{{ r.body }}</pre>
                          }
                        }
                      } @else {
                        <!-- 票236：正文区兜底（与单卡同款 bodyState 机制，不另设第二套）—— entry 缺失时渲染 loading 态，取数到后自动进终态分支。 -->
                        <p class="body-loading">{{ t('pb.v2.d.loading') }}</p>
                      }
                      @if (metaOf(a)?.screenshotAssetId) {
                        <div class="shot-box">
                          <div class="shot-head">
                            <span class="shot-titles">
                              <span class="shot-title">{{ t('pb.v2.m.shotTitle') }}</span>
                              <span class="shot-sub">{{ t('pb.v2.m.shotSub') }}</span>
                            </span>
                            <button type="button" class="mini-btn" (click)="openShotDialog(a)">{{ t('pb.v2.m.viewShot') }}</button>
                          </div>
                        </div>
                      }
                      @if (a.integrityStatus === 'Missing') { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
                    </div>
                  }
                </div>
              }
            </div>
          }
        </section>
      } @else {
      @for (a of g.items; track a.id) {
        @if (isEmailAsset(a)) {
          <article class="mat email-card" data-testid="material-item">
            <!-- 票210：方案 B 摘要展开型 —— 收起态一行卡片，点头部展开见完整邮件内容；
                 轮次归属（Belongs to 下拉框与 Round 锚定行）按用户令整段删除。
                 票215：会议链接已解析（AssetMeta.meetingLink + 读侧派生兜底），有真实链接时
                 头部行右侧渲染 Join meeting 芯片；芯片是头部按钮的兄弟元素（不是 button 的
                 子元素），点击直达会议、不触发展开/收起。
                 票225：截图不再卡片内 inline 展开（推翻 210 的 inline 选择，用户新指令），
                 点 View screenshot 改开 MatDialog 弹窗看大图，弹窗内带 −/百分比/＋ 缩放。
                 票236：多封组不再走此单卡分支，改走 thread 容器；单封组此分支渲染一字不动。 -->
            <div class="eh-row">
            <button type="button" class="email-head" (click)="toggleMessage(a)"
                    [attr.aria-expanded]="isMessageOpen(a)">
              <span class="avatar">{{ senderInitial(a) }}</span>
              <span class="eh-main">
                <span class="eh-top">
                  @if (senderName(a)) { <span class="eh-from">{{ senderName(a) }}</span> }
                  <span class="eh-time">{{ headTime(a) }}</span>
                </span>
                <span class="eh-title">{{ shortTitle(a) }}</span>
                @if (metaOf(a)?.mailboxNew) { <span class="new-pill">{{ t('mailbox.newImport') }}</span> }
                @if (metaOf(a)?.format) { <span class="eh-format">{{ formatLabel(t, metaOf(a)!.format) }}</span> }
              </span>
              <span class="chev" [class.open]="isMessageOpen(a)">›</span>
            </button>
              @if (meetingLinkOf(a); as link) {
                <a class="join-chip" [href]="link" target="_blank" rel="noopener"
                   [attr.aria-label]="t('pb.br.countdown.join')">{{ t('pb.br.countdown.join') }}</a>
              }
            </div>

            @if (isMessageOpen(a)) {
            <div class="email-open">
              <div class="eo-sender">
                <span class="avatar sm">{{ senderInitial(a) }}</span>
                @if (senderName(a)) { <span class="eo-from">{{ senderName(a) }}</span> }
                <span class="eo-recv">{{ t('pb.v2.m.fReceived') }} {{ displayDate(a) }}</span>
                @if (metaOf(a)?.category) { <span class="cat-pill">{{ categoryLabel(metaOf(a)!.category!) }}</span> }
              </div>
              <div class="eo-title">{{ shortTitle(a) }}</div>
              @if (metaOf(a); as m) {
                <dl class="parsed2">
                  @if (m.interviewAt) { <div class="prow"><dt>{{ t('pb.v2.m.fWhen') }}</dt><dd>{{ m.interviewAt }}</dd></div> }
                  @if (m.format) { <div class="prow"><dt>{{ t('pb.v2.m.fFormat') }}</dt><dd>{{ formatLabel(t, m.format) }}</dd></div> }
                  @if (interviewersOf(a)) { <div class="prow"><dt>{{ t('pb.v2.m.fInterviewers') }}</dt><dd>{{ interviewersOf(a) }}</dd></div> }
                  @if (meetingLinkOf(a); as link) { <div class="prow"><dt>{{ t('pb.v2.r.meetingLink') }}</dt><dd><a [href]="link" target="_blank" rel="noopener">{{ t('pb.v2.r.openLink') }}</a></dd></div> }
                </dl>
              }

            @if (bodyState(a); as eb) {
              @if (eb.loading) { <p class="body-loading">{{ t('pb.v2.d.loading') }}</p> }
              @else if (eb.error) { <p class="body-loading">{{ t('pb.v2.m.bodyFailed') }}</p> }
              @else if (!eb.text || !eb.seg) { <p class="body-loading">{{ t('pb.v2.m.noBody') }}</p> }
              @else if (eb.seg.fallback) { <pre class="body-text-full">{{ eb.text }}</pre> }
              @else {
                <div class="timeline">
                  @for (s of eb.seg.timeline; track $index) {
                    <div class="tl-node">
                      <span class="tl-dot"></span>
                      <div class="tl-main">
                        <div class="tl-meta">{{ s.dateTime }} · {{ s.meetingCard ? t('pb.v2.m.meetingCard') : s.sender }}</div>
                        <pre class="tl-body">{{ s.body }}</pre>
                      </div>
                    </div>
                  }
                </div>
                @for (r of eb.seg.remainder; track $index) {
                  <pre class="tl-rest">{{ r.body }}</pre>
                }
              }
            } @else {
              <!-- 票236：正文区兜底 —— entry 缺失时不再留空白 void，直接渲染 loading 态；
                   bodyState 已在读数时触发 ensureEmailBody，取数到后自动进终态分支。 -->
              <p class="body-loading">{{ t('pb.v2.d.loading') }}</p>
            }

            @if (metaOf(a)?.screenshotAssetId) {
              <div class="shot-box">
                <div class="shot-head">
                  <span class="shot-titles">
                    <span class="shot-title">{{ t('pb.v2.m.shotTitle') }}</span>
                    <span class="shot-sub">{{ t('pb.v2.m.shotSub') }}</span>
                  </span>
                  <button type="button" class="mini-btn" (click)="openShotDialog(a)">{{ t('pb.v2.m.viewShot') }}</button>
                </div>
              </div>
            }
            @if (a.integrityStatus === 'Missing') { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
            </div>
            }
          </article>
        } @else {
          <article class="mat" data-testid="material-item">
            <div class="mat-head">
              <span class="kind-chip">{{ kindLabel(a) }}</span>
              <!-- 票159：备考输入资产在材料列表中带标签，与 Prep 页读回区是同一资产。 -->
              @if (isPrepInput(a)) { <span class="prep-tag">{{ t('pb.v2.p.prepInputTag') }}</span> }
              <span class="mat-name">{{ a.fileName }}</span>
              @if (phaseOf(a); as ph) { <span class="phase">{{ ph === 'Pre' ? t('pb.v2.m.phasePre') : t('pb.v2.m.phasePost') }}</span> }
              <span class="mat-date">{{ displayDate(a) }}</span>
              <span class="spacer"></span>
              <label class="assign">{{ t('pb.v2.m.assign') }}
                <select class="fld sm" [ngModel]="a.roundId ?? ''" (ngModelChange)="reassign(a, $event)">
                  <option value="">{{ t('pb.v2.m.commOption') }}</option>
                  @for (r of rounds(); track r.id) {
                    <option [value]="r.id">Round {{ roundNo(r) }} · {{ r.stage }}</option>
                  }
                </select>
              </label>
              @if (!hideMatDelete) {
              <button type="button" class="icon-btn danger" (click)="remove(a)" [title]="t('common.delete')">
                <mat-icon>delete_outline</mat-icon>
              </button>
              }
            </div>

            @if (metaOf(a); as m) {
              @if (m.category || m.interviewAt || m.from) {
                <div class="parsed">
                  @if (m.category) { <span>{{ t('pb.v2.m.fCategory') }}<b>{{ categoryLabel(m.category) }}</b></span> }
                  @if (m.from) { <span>{{ t('pb.v2.m.fFrom') }}<b>{{ m.from }}</b></span> }
                  @if (m.date) { <span>{{ t('pb.v2.m.fDate') }}<b>{{ m.date }}</b></span> }
                  @if (m.interviewAt) { <span>{{ t('pb.v2.m.fWhen') }}<b>{{ m.interviewAt }}</b></span> }
                  @if (m.interviewers?.length) { <span>{{ t('pb.v2.m.fInterviewers') }}<b>{{ joinNamesOf(m.interviewers!) }}</b></span> }
                  @if (m.format) { <span>{{ t('pb.v2.m.fFormat') }}<b>{{ formatLabel(t, m.format) }}</b></span> }
                  @if (m.summary) { <span class="sum">{{ t('pb.v2.m.fSummary') }}{{ m.summary }}</span> }
                </div>
              }
            }

            <div class="mat-actions">
              @if (isAudioAsset(a)) {
                <button type="button" class="mini-btn" (click)="loadFile(a)">{{ t('pb.v2.m.playAudio') }}</button>
              }
              @if (hasBody(a)) {
                <button type="button" class="mini-btn" (click)="toggleBody(a)">
                  {{ expandedId() === a.id ? t('pb.v2.m.collapseBody') : t('pb.v2.m.viewBody') }}
                </button>
              }
              @if (metaOf(a)?.screenshotAssetId) {
                <button type="button" class="mini-btn" (click)="loadShot(a)">{{ t('pb.v2.m.viewShot') }}</button>
              }
              @if (a.integrityStatus === 'Missing') { <span class="lost">{{ t('pb.v2.m.fileLost') }}</span> }
            </div>

            @if (fileUrl(a.id); as url) {
              @if (isAudioAsset(a)) { <audio controls [src]="url" class="player"></audio> }
              @else { <img [src]="url" class="shot-img" [alt]="t('pb.v2.m.imgAltMaterial')"> }
            }
            @if (shotUrl() && shotFor() === a.id) {
              <img [src]="shotUrl()" class="shot-img" [alt]="t('pb.v2.m.imgAltShot')">
            }
            @if (expandedId() === a.id) {
              <div class="body-box">
                @if (bodySegs().length) {
                  @for (s of bodySegs(); track $index) {
                    <p class="seg-line"><b>{{ s.speaker }}:</b>{{ s.text }}</p>
                  }
                } @else {
                  <pre class="body-text">{{ bodyText() }}</pre>
                }
              </div>
            }
          </article>
        }
      }
      }
    }
  `,
  styles: [`
    .empty { font-size: 14px; color: var(--yi-text-muted, #475569); padding: 10px 0; }
    /* 票240：条目级刷新行 + “新导入”标记。 */
    .mail-refresh { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin: 2px 0 6px; }
    .mail-refresh .mini-btn { display: inline-flex; align-items: center; gap: 5px; }
    .mail-refresh .mini-btn mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .mail-result { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .new-pill { font-size: 11px; font-weight: 700; border-radius: 999px; padding: 1px 8px;
                background: #dbeafe; color: #1d4ed8; white-space: nowrap; }
    .mat { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
    .mat-head { display: flex; align-items: center; gap: 9px; flex-wrap: wrap; }
    .kind-chip { font-size: 12px; font-weight: 700; border: 1px solid var(--yi-border, #e2e8f0);
                 border-radius: 999px; padding: 2px 9px; }
    .prep-tag { font-size: 12px; font-weight: 700; border-radius: 999px; padding: 2px 9px;
                background: var(--yi-accent-tint, #eff6ff); color: var(--yi-accent, #2563eb); }
    .mat-name { font-size: 14px; font-weight: 600; }
    .phase { font-size: 12px; color: var(--yi-text-muted, #475569); }
    .mat-date { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .spacer { flex: 1; }
    .assign { font-size: 13px; color: var(--yi-text-muted, #475569); display: inline-flex; gap: 6px; align-items: center; }
    .fld.sm { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 6px; padding: 4px 7px;
              font-size: 14px; background: transparent; color: inherit; }
    .icon-btn { border: none; background: transparent; cursor: pointer; color: inherit; display: inline-flex; }
    .icon-btn.danger { color: #dc2626; }
    .icon-btn mat-icon { font-size: 19px; width: 19px; height: 19px; }
    .parsed { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 13px;
              color: var(--yi-text-muted, #475569); margin-top: 8px; }
    .parsed b { color: var(--yi-text, #0f172a); font-weight: 600; }
    .parsed .sum { flex-basis: 100%; }
    .mat-actions { display: flex; gap: 8px; margin-top: 9px; flex-wrap: wrap; align-items: center; }
    .mini-btn { border: 1px solid var(--yi-border, #e2e8f0); background: transparent; color: inherit;
                border-radius: 999px; padding: 4px 11px; font-size: 14px; font-weight: 500; cursor: pointer; }
    .lost { font-size: 13px; color: #dc2626; }
    .player { width: 100%; height: 36px; margin-top: 8px; }
    .shot-img { max-width: 100%; border-radius: 8px; margin-top: 8px; border: 1px solid var(--yi-border, #e2e8f0); }
    .body-box { margin-top: 9px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 9px; }
    .body-text { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 0;
                 font-family: inherit; max-height: 380px; overflow: auto; }
    .seg-line { font-size: 14px; line-height: 1.6; margin: 3px 0; }
    /* 票165：邮件卡新分层（comm-log thread-head 同款式）。 */
    .thread-head { display: flex; align-items: center; gap: 9px; margin: 16px 0 2px; }
    .thread-subject { font-size: 14px; font-weight: 600; }
    .thread-count { font-size: 12px; font-weight: 700; color: #475569; background: #e2e8f0;
                    border-radius: 999px; padding: 1px 9px; }
    /* 票210：方案 B 邮件卡 —— 收起态一行卡片 + 展开态完整内容。 */
    .email-head { display: flex; gap: 10px; align-items: flex-start; width: 100%;
                  border: none; background: transparent; color: inherit; cursor: pointer;
                  padding: 0; text-align: left; font: inherit; }
    /* 票215：收起态行 —— 头部按钮 + Join 芯片同一行；芯片是按钮的兄弟元素。 */
    .eh-row { display: flex; gap: 8px; align-items: center; }
    .eh-row .email-head { flex: 1 1 auto; min-width: 0; width: auto; }
    .join-chip { flex-shrink: 0; font-size: 12px; font-weight: 700; white-space: nowrap;
                 color: var(--yi-accent, #2563eb); background: var(--yi-accent-tint, #eff6ff);
                 border-radius: 999px; padding: 4px 10px; text-decoration: none; }
    .join-chip:hover { text-decoration: underline; }
    .avatar { flex-shrink: 0; width: 36px; height: 36px; border-radius: 50%;
              background: var(--yi-accent-tint, #eff6ff); color: var(--yi-accent, #2563eb);
              display: inline-flex; align-items: center; justify-content: center;
              font-size: 16px; font-weight: 700; }
    .avatar.sm { width: 28px; height: 28px; font-size: 13px; }
    .eh-main { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .eh-top { display: flex; gap: 8px; align-items: baseline; }
    .eh-from { font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .eh-time { margin-left: auto; flex-shrink: 0; font-size: 13px; color: var(--yi-text-muted, #475569); }
    .eh-title { font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .eh-format { font-size: 13px; color: var(--yi-text-muted, #475569); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .chev { flex-shrink: 0; font-size: 20px; line-height: 1; color: var(--yi-text-muted, #475569);
            transition: transform .15s; align-self: center; }
    .chev.open { transform: rotate(90deg); }
    .email-open { margin-top: 10px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 10px; }
    /* 票236：真实邮件式 thread 容器 —— 容器头（主题 + 封数 + 最新一封信息 + chevron）与消息栏
       均 flex-wrap + min-width:0 + ellipsis，360px 下不溢出；消息栏与展开体之间用虚线分隔。 */
    .thread { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
    .th-row { display: flex; gap: 8px; align-items: center; }
    .th-row .thread-head { flex: 1 1 auto; min-width: 0; width: auto; }
    .thread-head { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap;
                   border: none; background: transparent; color: inherit; cursor: pointer;
                   padding: 0; text-align: left; font: inherit; }
    .th-subject { font-size: 14px; font-weight: 700; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .th-count { flex-shrink: 0; font-size: 12px; font-weight: 700; color: #475569; background: #e2e8f0;
                border-radius: 999px; padding: 1px 9px; white-space: nowrap; }
    .th-latest { font-size: 13px; color: var(--yi-text-muted, #475569); overflow: hidden;
                 text-overflow: ellipsis; white-space: nowrap; }
    .th-msgs { margin-top: 10px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 4px; }
    .th-msg + .th-msg { border-top: 1px dashed var(--yi-border, #e2e8f0); margin-top: 8px; padding-top: 8px; }
    .msg-bar { display: flex; gap: 8px; align-items: center; width: 100%; flex-wrap: wrap;
               border: none; background: transparent; color: inherit; cursor: pointer;
               padding: 0; text-align: left; font: inherit; }
    .msg-from { font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .msg-date { flex-shrink: 0; font-size: 13px; color: var(--yi-text-muted, #475569); }
    .msg-title { flex: 1 1 auto; min-width: 0; font-size: 14px; overflow: hidden;
                 text-overflow: ellipsis; white-space: nowrap; }
    .msg-open { margin-top: 10px; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 10px; }
    .eo-sender { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .eo-from { font-size: 14px; font-weight: 600; }
    .eo-recv { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .cat-pill { font-size: 12px; font-weight: 700; border: 1px solid var(--yi-border, #e2e8f0);
                border-radius: 999px; padding: 2px 9px; }
    .eo-title { font-size: 16px; font-weight: 700; margin-top: 8px; overflow-wrap: anywhere; }
    .shot-box { margin-top: 12px; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 10px 12px; }
    .shot-head { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .shot-titles { display: flex; flex-direction: column; flex: 1; min-width: 0; }
    .shot-title { font-size: 14px; font-weight: 600; }
    .shot-sub { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .shot-box .shot-img { margin-top: 8px; }
    .round-anchor { font-size: 13px; color: var(--yi-text-muted, #475569); margin-top: 6px; }
    .meta-line { display: flex; gap: 6px 16px; flex-wrap: wrap; font-size: 13px;
                 color: var(--yi-text-muted, #475569); margin-top: 6px; }
    .meta-line b { color: var(--yi-text, #0f172a); font-weight: 600; }
    .parsed2 { margin: 8px 0 0; }
    .prow { display: flex; gap: 8px; font-size: 13px; color: var(--yi-text-muted, #475569); margin: 3px 0; }
    .prow dt { flex-shrink: 0; }
    .prow dd { margin: 0; color: var(--yi-text, #0f172a); }
    .meet-block { background: var(--yi-accent-tint, #eff6ff); border-radius: 8px;
                  padding: 8px 10px; font-size: 14px; margin-top: 8px; }
    .body-loading { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 9px 0 0; }
    .body-text-full { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 9px 0 0;
                      font-family: inherit; }
    .timeline { margin-top: 9px; }
    .tl-node { position: relative; padding-left: 20px; margin: 0 0 12px; }
    .tl-node::before { content: ''; position: absolute; left: 4px; top: 7px; width: 9px; height: 9px;
                       border-radius: 50%; background: var(--yi-accent, #2563eb); }
    .tl-node::after { content: ''; position: absolute; left: 8px; top: 20px; bottom: -12px; width: 2px;
                      background: var(--yi-border, #e2e8f0); }
    .tl-node:last-child::after { display: none; }
    .tl-meta { font-size: 13px; color: var(--yi-text-muted, #475569); margin-bottom: 2px; }
    .tl-body { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 0; font-family: inherit; }
    .tl-rest { white-space: pre-wrap; font-size: 14px; line-height: 1.6; margin: 9px 0 0 20px;
               font-family: inherit; border-top: 1px dashed var(--yi-border, #e2e8f0); padding-top: 9px; }
    @media (max-width: 480px) {
      .prow { flex-direction: column; gap: 0; }
      .assign { flex-basis: 100%; }
      .assign .fld.sm { flex: 1; }
    }
  `]
})
export class MaterialsListComponent implements OnDestroy {
  readonly entryId = input.required<string>();
  // 票189：用户令去掉材料卡红色删除图标（先隐藏不删除：remove() 逻辑、后端 deleteAsset、i18n 键保留；恢复只改开关）。
  readonly hideMatDelete = true;
  readonly assets = input.required<InterviewAsset[]>();
  readonly rounds = input<InterviewRound[]>([]);

  /** 票30(REQ-10):归属下拉编号与详情页 stepper 同一日期序口径(helper 内部自排序,input 顺序无关)。 */
  roundNo(r: InterviewRound): number {
    return roundDisplayNo(this.rounds(), r);
  }
  @Output() readonly changed = new EventEmitter<void>();

  private readonly api = inject(PlaybookV2Api);
  private readonly interviewsApi = inject(InterviewsApiService);
  private readonly snack = inject(MatSnackBar);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  private readonly i18n = inject(I18nService);

  /** 票240：条目级刷新状态（取消 = 退订请求，后端按已落库部分保留、cursor 不推进）。 */
  readonly mailRefreshing = signal(false);
  readonly mailRefreshResult = signal<RefreshMailboxResult | null>(null);
  private mailRefreshSub: Subscription | null = null;

  ngOnDestroy(): void {
    this.mailRefreshSub?.unsubscribe();
  }

  refreshFromMailbox(): void {
    this.mailRefreshResult.set(null);
    this.mailRefreshing.set(true);
    this.mailRefreshSub?.unsubscribe();
    this.mailRefreshSub = this.interviewsApi.refreshMailbox(this.entryId()).subscribe({
      next: (r) => {
        this.mailRefreshing.set(false);
        this.mailRefreshResult.set(r);
        if (r.needsReconnect) this.snack.open(this.i18n.t('mailbox.needsReconnect'), this.i18n.t('mailbox.reconnect'), { duration: 5000 }).onAction().subscribe(() => void this.router.navigate(['/settings/mail']));
        else if (r.rateLimited) this.snack.open(this.i18n.t('mailbox.rateLimited'), undefined, { duration: 5000 });
        else if (r.failed > 0 && r.failedReasons.length > 0) this.snack.open(r.failedReasons.join('；'), undefined, { duration: 8000 });
        // 刷新成功后触发详情页重载（新资产 + “新导入”标记随 fresh 数据回来）。
        this.changed.emit();
      },
      error: (err) => {
        this.mailRefreshing.set(false);
        const e = err as { error?: { detail?: string }; message?: string };
        this.snack.open(e?.error?.detail || e?.message || '刷新失败', undefined, { duration: 5000 });
      }
    });
  }

  cancelMailboxRefresh(): void {
    this.mailRefreshSub?.unsubscribe();
    this.mailRefreshSub = null;
    this.mailRefreshing.set(false);
    this.changed.emit();
  }

  /** 刷新结果条：新增 N 封 · 待确认 M 封 · 跳过重复 K 封（失败另行 snack 提示）。 */
  mailResultLine(r: RefreshMailboxResult): string {
    return this.i18n.t('mailbox.resultLine')
      .replace('{added}', String(r.added))
      .replace('{pending}', String(r.pending))
      .replace('{skipped}', String(r.skippedDuplicates))
      .replace('{failed}', String(r.failed));
  }
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly expandedId = signal<string | null>(null);
  /** 票236：开合模型 —— thread 容器开合（组 key 集合）+ 封级开合（资产 id 集合）；
   * 单封组亦用封级集合，取代旧单 emailOpenId；点开 thread 时最新一封自动进封级集合（默认全展开）。 */
  readonly openThreads = signal(new Set<string>());
  readonly openMessages = signal(new Set<string>());
  readonly bodyText = signal('');
  readonly bodySegs = signal<TranscriptSegment[]>([]);
  private readonly fileUrls = signal(new Map<string, string>());
  readonly shotUrl = signal<string | null>(null);
  readonly shotFor = signal<string | null>(null);

  readonly emailBodies = signal(new Map<string, EmailBodyState>());
  private readonly fetchedEmailIds = new Set<string>();

  constructor() {
    // 票165：邮件卡挂载即取全文（现成懒取改挂载取），正文不再需要点击才出现；非邮件 kind 不动。
    // 票217：取数收敛到 ensureEmailBody（transcript 优先 + 截图回落 + 空白归一），effect 与展开共用。
    effect(() => {
      for (const a of this.assets()) {
        this.ensureEmailBody(a);
      }
    });
  }

  /**
   * 票217：邮件正文取数（单点，幂等）——本体 transcript 优先；本体为空白时
   * 回落到截图资产（meta.screenshotAssetId）的转写文本；两路皆空则正文置空串
   * （模板 noBody 可见态）；本体取数失败且无回落（或回落亦失败）则 error 可见态。
   * 纯空白文本先归一为空串，绝不渲染不可见空白 <pre>；只展示已存文本，不编造。
   * 215 的芯片派生读 emailBodies().get(a.id)?.text，本回落链恢复全文后芯片随之恢复，
   * 215 逻辑本身一字不动。
   */
  private ensureEmailBody(a: InterviewAsset): void {
    if (!this.isEmailAsset(a) || this.fetchedEmailIds.has(a.id)) return;
    this.fetchedEmailIds.add(a.id);
    this.emailBodies.update(m => new Map(m).set(a.id, { loading: true, error: false, text: '', seg: null }));
    const done = (text: string) => {
      this.emailBodies.update(m => new Map(m).set(a.id, { loading: false, error: false, text, seg: segmentEmailBody(text) }));
    };
    const fail = () => {
      this.emailBodies.update(m => new Map(m).set(a.id, { loading: false, error: true, text: '', seg: null }));
    };
    const shotId = this.metaOf(a)?.screenshotAssetId;
    const fallback = (primaryFailed: boolean) => {
      if (!shotId) { primaryFailed ? fail() : done(''); return; }
      this.api.getTranscript(this.entryId(), shotId).subscribe({
        next: t => { done(normalizeEmailBodyText(t.transcriptText)); },
        error: () => { primaryFailed ? fail() : done(''); },
      });
    };
    this.api.getTranscript(this.entryId(), a.id).subscribe({
      next: t => {
        const text = normalizeEmailBodyText(t.transcriptText);
        if (text) done(text);
        else fallback(false);
      },
      error: () => { fallback(true); },
    });
  }

  /**
   * 票236：正文区渲染时保证 —— 模板 body 区读此方法而非直接读 map；entry
   * 缺失时当场触发 ensureEmailBody（单点幂等，fetchedEmailIds 守卫重复调用），
   * 并返回新建的 loading 条目，字段行与截图区之间永不出现空白 void 态。
   * 取数到后信号更新，视图自动进 loading / bodyFailed / noBody / fallback
   * 全文 / 时间线终态分支；终态语义一字不动。
   */
  bodyState(a: InterviewAsset): EmailBodyState | null {
    const eb = this.emailBodies().get(a.id);
    if (eb) return eb;
    this.ensureEmailBody(a);
    return this.emailBodies().get(a.id) ?? null;
  }

  metaOf(a: InterviewAsset): AssetMeta | null {
    return safeJson<AssetMeta>(a.metaJson);
  }

  /** 票159：备考输入资产（kind 不限，只认 purpose 标签）。 */
  isPrepInput(a: InterviewAsset): boolean {
    return isPrepInputPurpose(this.metaOf(a)?.purpose);
  }

  /** 邮件类材料(与 kindLabel 同一判别口径,供 phase 推导回落规则用；模板亦用，须公开)。 */
  isEmailAsset(a: InterviewAsset): boolean {
    const m = this.metaOf(a);
    return m?.channel === 'email' || (a.kind === 'Text' && !!m?.category);
  }

  /** 票165：邮件主题全称（meta.subject），无主题时回落文件名。 */
  emailSubject(a: InterviewAsset): string {
    return this.metaOf(a)?.subject || a.fileName;
  }

  /** 票236：thread 容器开合 —— 点开时最新一封默认进封级集合（全展开）并补取正文；
   * 收起时清掉组内各封的封级开合，下次点开恢复默认（最新展开、旧封收起）。 */
  toggleThread(g: { key: string; items: InterviewAsset[] }): void {
    const threads = new Set(this.openThreads());
    const msgs = new Set(this.openMessages());
    if (threads.has(g.key)) {
      threads.delete(g.key);
      for (const a of g.items) msgs.delete(a.id);
    } else {
      threads.add(g.key);
      const latest = this.latestOf(g);
      msgs.add(latest.id);
      this.ensureEmailBody(latest);
    }
    this.openThreads.set(threads);
    this.openMessages.set(msgs);
  }

  /** 票236：封级开合（单封组头行与 thread 内消息栏共用）。票217：展开时补取正文（effect 未覆盖到的卡，展开即有条目，至少 loading 态）。 */
  toggleMessage(a: InterviewAsset): void {
    const msgs = new Set(this.openMessages());
    if (msgs.has(a.id)) { msgs.delete(a.id); }
    else { msgs.add(a.id); this.ensureEmailBody(a); }
    this.openMessages.set(msgs);
  }

  isThreadOpen(g: { key: string }): boolean {
    return this.openThreads().has(g.key);
  }

  isMessageOpen(a: InterviewAsset): boolean {
    return this.openMessages().has(a.id);
  }

  /** 票236：组内最新一封（displayGroups 已按收件日正序，末位即最新）。 */
  latestOf(g: { items: InterviewAsset[] }): InterviewAsset {
    return g.items[g.items.length - 1];
  }

  /** 票236：容器头最新一封信息 —— 发件人 + 收件日，有发件人才带“·”分隔。 */
  latestLabel(g: { items: InterviewAsset[] }): string {
    const latest = this.latestOf(g);
    const from = this.senderName(latest);
    const date = this.displayDate(latest);
    return from ? from + ' · ' + date : date;
  }

  /** 票210：提炼短标题（原主题全文不当标题）。 */
  shortTitle(a: InterviewAsset): string {
    return shortEmailTitle(this.emailSubject(a));
  }

  /** 票210：收起态右上时间 —— 面试时间格式化，无面试时间时回落收件日期（真实值，不编造）。 */
  headTime(a: InterviewAsset): string {
    return formatInterviewHeadTime(this.metaOf(a)?.interviewAt) || this.displayDate(a);
  }

  /** 票210：发件人（from 优先，fromEmail 兜底，两者皆无为空串）。 */
  senderName(a: InterviewAsset): string {
    const m = this.metaOf(a);
    return m?.from || m?.fromEmail || '';
  }

  /** 票210：发件人头像首字母，无发件人时为信封符号。 */
  senderInitial(a: InterviewAsset): string {
    const n = this.senderName(a);
    return n ? n.trim().charAt(0).toUpperCase() : '✉';
  }

  /** 票210：面试官 —— 解析字段 interviewers 优先，arrangement.interviewers 兜底，两者皆无为 null（该行不渲染）。 */
  interviewersOf(a: InterviewAsset): string | null {
    const m = this.metaOf(a);
    if (m?.interviewers?.length) return this.joinNamesOf(m.interviewers);
    return m?.arrangement?.interviewers || null;
  }

  /** 票215：会议链接 —— 存储字段 meetingLink 优先；存量旧邮件以 165 挂载已取的全文
   *  纯函数派生兜底（读侧派生，不写存储、不批量改写）；两者皆无为 null（芯片与
   *  Meeting 行都不渲染）。全文尚在加载时派生为空，取到后自动出现。 */
  meetingLinkOf(a: InterviewAsset): string | null {
    const m = this.metaOf(a);
    if (m?.meetingLink) return m.meetingLink;
    const text = this.emailBodies().get(a.id)?.text;
    return text ? extractMeetingLink(text) : null;
  }

  /** 票165：时间线顶部 round 锚定（无归属轮次时无锚定，不编造）。 */
  roundAnchor(a: InterviewAsset): { no: number; stage: string; date: string; phase: string } | null {
    if (!a.roundId) return null;
    const round = this.rounds().find(r => r.id === a.roundId);
    if (!round) return null;
    const ph = this.phaseOf(a);
    if (ph === null) return null;
    return {
      no: this.roundNo(round),
      stage: round.stage,
      date: (round.scheduledDate ?? '').slice(0, 10) || this.t('pb.v2.d.dateTbd'),
      phase: ph === 'Pre' ? this.t('pb.v2.m.phasePre') : this.t('pb.v2.m.phasePost')
    };
  }

  /**
   * 票165：列表层展示分组（只影响展示，底层仍是单条资产；分组键一字不动）。
   * 截图去重在先：被任一邮件 screenshotAssetId 引用的资产不单独渲染；
   * 邮件按 threadKey（无则主题归一化兜底，comm-log 同口径）分组，组内按日期正序；
   * 票236：渲染层多封组走 thread 容器、单封组走旧 email-card（210 组头移除口径不变）。
   * 非邮件资产原序单条。
   */
  readonly displayGroups = computed(() => {
    const referenced = new Set<string>();
    for (const a of this.assets()) {
      const sid = this.metaOf(a)?.screenshotAssetId;
      if (sid) referenced.add(sid);
    }
    const visible = this.assets().filter(a => !referenced.has(a.id));
    interface DisplayGroup { key: string; subject: string; items: InterviewAsset[] }
    const groups: DisplayGroup[] = [];
    const byKey = new Map<string, DisplayGroup>();
    for (const a of visible) {
      if (this.isEmailAsset(a)) {
        const m = this.metaOf(a);
        const key = m?.threadKey ? 'th:' + m.threadKey
          : m?.subject ? 'th:' + normalizeThreadSubject(m.subject).key
          : 'solo:' + a.id;
        let g = byKey.get(key);
        if (!g) { g = { key, subject: '', items: [] }; byKey.set(key, g); groups.push(g); }
        g.items.push(a);
      } else {
        groups.push({ key: 'solo:' + a.id, subject: '', items: [a] });
      }
    }
    for (const g of groups) {
      if (g.items.length > 1) {
        g.items.sort((x, y) => (this.displayDate(x) < this.displayDate(y) ? -1 : 1));
        g.subject = normalizeThreadSubject(this.metaOf(g.items[0])?.subject ?? '').display;
      }
    }
    return groups;
  });

  /**
   * Before/After 展示值(2026-10-21 用户裁决):有归属轮次时按「材料日期 vs
   * 轮次日期」推导(见 derivePhase),存量写错的 phase 在展示时就被纠正,
   * 不做数据迁移;无归属轮次(条目级材料)无从比较,保持存量值。
   */
  phaseOf(a: InterviewAsset): 'Pre' | 'Post' | null {
    const stored = a.phase === 'Pre' || a.phase === 'Post' ? a.phase : null;
    if (!a.roundId) return stored;
    const round = this.rounds().find(r => r.id === a.roundId);
    if (!round) return stored;
    return derivePhase(this.isEmailAsset(a), this.displayDate(a), round.scheduledDate);
  }

  readonly formatLabel = formatLabel;
  readonly isAudioAsset = isAudioAsset;

  joinNamesOf(names: string[]): string {
    return joinNames(this.i18n, names);
  }

  kindLabel(a: InterviewAsset): string {
    const m = this.metaOf(a);
    if (m?.channel === 'email' || (a.kind === 'Text' && m?.category)) return this.t('pb.v2.m.kindEmail');
    switch (a.kind) {
      case 'Audio': return this.t('pb.v2.m.kindAudio');
      case 'Transcript': return this.t('pb.v2.m.kindTranscript');
      case 'Notes': return this.t('pb.v2.m.kindNotes');
      case 'Text': return this.t('pb.v2.m.kindText');
      case 'Guidance': return this.t('pb.v2.m.kindGuidance');
      default: return a.kind;
    }
  }

  categoryLabel(c: string): string {
    const key = 'pb.v2.cat.' + c;
    const v = this.t(key);
    return v === key ? c : v;
  }

  displayDate(a: InterviewAsset): string {
    const m = this.metaOf(a);
    return (m?.date ?? a.uploadedAt ?? '').slice(0, 10);
  }

  hasBody(a: InterviewAsset): boolean {
    return a.kind === 'Transcript' || a.kind === 'Text' || a.kind === 'Notes' || !!a.hasTranscript;
  }

  fileUrl(id: string): string | null {
    return this.fileUrls().get(id) ?? null;
  }

  loadFile(a: InterviewAsset): void {
    if (this.fileUrls().has(a.id)) return;
    this.api.audioBlob(this.entryId(), a.id).subscribe({
      next: blob => {
        const next = new Map(this.fileUrls());
        next.set(a.id, URL.createObjectURL(blob));
        this.fileUrls.set(next);
      },
      error: () => this.snack.open(this.t('pb.v2.m.fileLoadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  loadShot(a: InterviewAsset): void {
    const shotId = this.metaOf(a)?.screenshotAssetId;
    if (!shotId) return;
    if (this.shotFor() === a.id && this.shotUrl()) { this.shotUrl.set(null); return; }
    this.api.audioBlob(this.entryId(), shotId).subscribe({
      next: blob => { this.shotUrl.set(URL.createObjectURL(blob)); this.shotFor.set(a.id); },
      error: () => this.snack.open(this.t('pb.v2.m.shotLoadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  /** 票 225：邮件截图改弹窗看大图（199 MatDialog 模式；Esc / 点遮罩按默认关闭）。
   * 弹窗内缩放是本地状态；对象 URL 在弹窗关闭后 revoke，不留泄漏。
   * 非邮件分支仍走 loadShot 的卡片内展开，本方法只供邮件分支调用。 */
  openShotDialog(a: InterviewAsset): void {
    const shotId = this.metaOf(a)?.screenshotAssetId;
    if (!shotId) return;
    this.api.audioBlob(this.entryId(), shotId).subscribe({
      next: blob => {
        const url = URL.createObjectURL(blob);
        const ref = this.dialog.open(EmailShotDialogComponent, {
          data: { url },
          width: 'min(900px, calc(100vw - 16px))',
          maxWidth: 'calc(100vw - 16px)',
          maxHeight: '92vh',
        });
        ref.afterClosed().subscribe(() => URL.revokeObjectURL(url));
      },
      error: () => this.snack.open(this.t('pb.v2.m.shotLoadFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  toggleBody(a: InterviewAsset): void {
    if (this.expandedId() === a.id) { this.expandedId.set(null); return; }
    this.expandedId.set(a.id);
    this.bodyText.set(this.t('pb.v2.d.loading'));
    this.bodySegs.set([]);
    this.api.getTranscript(this.entryId(), a.id).subscribe({
      next: t => {
        const segs = safeJson<TranscriptSegment[]>(t.segmentsJson);
        if (segs?.length) this.bodySegs.set(segs);
        this.bodyText.set(t.transcriptText ?? this.t('pb.v2.m.noBody'));
      },
      error: () => this.bodyText.set(this.t('pb.v2.m.bodyFailed'))
    });
  }

  reassign(a: InterviewAsset, roundId: string): void {
    const target = roundId || null;
    if ((a.roundId ?? null) === target) return;
    this.api.reassignAsset(this.entryId(), a.id, target).subscribe({
      next: () => {
        this.snack.open(target ? this.t('pb.v2.m.movedToRound') : this.t('pb.v2.m.movedToComm'), this.t('common.close'), { duration: 2200 });
        this.changed.emit();
      },
      error: () => this.snack.open(this.t('pb.v2.m.reassignFailed'), this.t('common.close'), { duration: 3000 })
    });
  }

  remove(a: InterviewAsset): void {
    this.api.deleteAsset(this.entryId(), a.id).subscribe({
      next: () => { this.snack.open(this.t('pb.v2.m.deleted'), this.t('common.close'), { duration: 2000 }); this.changed.emit(); },
      error: () => this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 })
    });
  }
}
