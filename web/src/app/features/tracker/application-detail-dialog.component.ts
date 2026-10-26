import { Component, ElementRef, HostListener, OnDestroy, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { type Observable } from 'rxjs';
import { JobsApiService } from '../../core/api/jobs-api.service';
import { TrackerApi, ApplicationQuestionDto } from '../../core/api/tracker-api.service';
import { TrackerPreferencesService } from '../../core/api/tracker-preferences.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { coverLetterMissingKey } from '../../core/i18n/localized-messages.helpers';
import { Application, ApplicationStatus, Communication, CommunicationType, Company } from '../../core/models/api.models';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { PdfContentViewerComponent } from '../../shared/pdf-viewer/pdf-content-viewer.component';
import { extractPdfText } from '../../core/utils/pdf-text';
import { fieldErrorFromServer, textLengthError } from '../../core/api/validation-errors.helpers';
import { commFormMode, commSaveChannel } from './comm-form.helpers';
import { dedupSectionsAgainstSummary, isListPhraseLine, mergeSectionsForReparse, parseSectionsJson, prepareSectionsSave, visibleDetailSections, type SectionEditDraft } from './jd-sections.helpers';
import { JdSection } from '../../core/models/api.models';
import { computeMatch, type MatchResult } from '../../core/match/resume-match';
import { DRAWER_KEY_STEP, DRAWER_NARROW_BELOW, drawerWidthRange, initialDrawerWidth, saveDrawerWidth } from './detail-drawer-width';

/**
 * 详情抽屉(内联)。
 *
 * 只读展示:要改内容走卡片上的"编辑"按钮。分开的好处是详情页可以放心
 * 展示只读的服务端字段(历史、评分),不必担心用户以为能直接改。
 */
@Component({
  selector: 'app-application-detail-dialog',
  standalone: true,
  imports: [
    CommonModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatChipsModule, MatDividerModule, FormsModule, MatProgressBarModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, MatTooltipModule,
    PdfContentViewerComponent
  ],
  template: `
    <!-- 票 147:抽屉左边缘拖拽手柄(面板内侧 6px,不压遮罩层,故不误关)。
         窄屏(<1024px)时不渲染,拖拽禁用。 -->
    @if (!narrowDrawer()) {
      <div class="drawer-resize-handle" tabindex="0" role="separator" aria-orientation="vertical"
           [title]="t('tracker.drawerResizeHint')" [attr.aria-label]="t('tracker.drawerResizeAria')"
           [class.dragging]="draggingDrawer()"
           (pointerdown)="onDrawerResizeStart($event)"
           (pointermove)="onDrawerResizeMove($event)"
           (pointerup)="onDrawerResizeEnd($event)"
           (pointercancel)="onDrawerResizeEnd($event)"
           (keydown)="onDrawerResizeKey($event)"></div>
    }
    <h2 mat-dialog-title class="detail-header">
      <span class="header-top">
        <span class="logo-wrap detail-logo">
          <span class="logo-avatar" [style.background]="avatarColor(app.companyName)">{{ companyInitials(app.companyName) }}</span>
          @if (app.companyLogoUrl && !logoBroken()) {
            <img class="logo" [src]="app.companyLogoUrl" (error)="logoBroken.set(true)">
          }
        </span>
        <span class="company-title">{{ app.companyName }}</span>
        <span class="header-actions">
          <button class="hdr-btn" (click)="toggleFav()" [matTooltip]="t('tracker.favorite')">
            <mat-icon>{{ isFav() ? 'favorite' : 'favorite_border' }}</mat-icon>
            <span>{{ t('tracker.favorite') }}</span>
          </button>
          <button class="hdr-btn" (click)="doArchive()" [matTooltip]="t('tracker.archive')">
            <mat-icon>archive</mat-icon>
            <span>{{ t('tracker.archive') }}</span>
          </button>
          @if ((!app.companyLogoUrl || logoBroken()) && app.companyId) {
            <button class="hdr-btn" (click)="refreshLogo()" [disabled]="refreshingLogo()" [matTooltip]="t('tracker.logoRefresh')">
              <mat-icon>image_search</mat-icon>
              <span>{{ t('tracker.logoRefresh') }}</span>
            </button>
          }
          <button class="hdr-btn danger" (click)="doDelete()" [matTooltip]="t('common.delete')">
            <mat-icon>delete</mat-icon>
            <span>{{ t('common.delete') }}</span>
          </button>
          <button class="hdr-close" mat-dialog-close [matTooltip]="t('common.close')">
            <mat-icon>close</mat-icon>
          </button>
        </span>
      </span>
      <span class="role-title">{{ app.role }}</span>
    </h2>

    <mat-dialog-content class="detail-body">
      @if (loadError()) {
        <div class="stale-warning">
          <mat-icon>warning</mat-icon>
          <span>{{ t('tracker.detailStale') }}</span>
        </div>
      }
      <div class="tabs">
        <button class="tab" [class.active]="detailTab() === 'overview'"
                (click)="switchTab('overview')">
          <mat-icon>visibility</mat-icon>{{ t('tracker.dOverview') }}</button>
        <button class="tab" [class.active]="detailTab() === 'documents'"
                (click)="switchTab('documents')">
          <mat-icon>folder</mat-icon>{{ t('tracker.dDocuments') }}</button>
        <button class="tab" [class.active]="detailTab() === 'score'"
                (click)="switchTab('score')">
          <mat-icon>assessment</mat-icon>{{ t('tracker.dResumeScore') }}</button>
        <button class="tab" [class.active]="detailTab() === 'email'"
                (click)="switchTab('email')">
          <mat-icon>email</mat-icon>{{ t('tracker.dEmail') }}
          @if (comms().length > 0) { ({{ comms().length }}) }</button>
        <button class="tab" [class.active]="detailTab() === 'questions'"
                (click)="switchTab('questions')">
          <mat-icon>help</mat-icon>{{ t('tracker.dQuestions') }}</button>
      </div>

      @if (detailTab() === 'overview') {
      <div class="two-col">
      <div class="main-col">
      <!-- ★ Simplify 真站:Job Posting 区块 -->
      <div class="sec-head">
        <span>{{ t('tracker.dJobPosting') }}</span>
        <button mat-icon-button class="edit-btn" (click)="startEditPosting()" [matTooltip]="t('common.edit')"
        [attr.aria-label]="t('common.edit')">
          <mat-icon>edit</mat-icon>
        </button>
      </div>

      <!-- 字段网格: Location, Salary / Job type, Work authorization / Applied date, URL / Work mode -->
      <div class="fields">
        <div class="field"><span class="flabel">{{ t('tracker.dLocation') }}</span><span class="fvalue" [class.missing]="!detail.location">{{ detail.location || t('tracker.notRecorded') }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.dSalary') }}</span><span class="fvalue" [class.missing]="!detail.salary">{{ detail.salary || t('tracker.notRecorded') }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.jobType') }}</span><span class="fvalue" [class.missing]="!detail.jobType">{{ detail.jobType || t('tracker.notRecorded') }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.workAuthorization') }}</span><span class="fvalue" [class.missing]="!detail.workAuthorization">{{ detail.workAuthorization || t('tracker.notRecorded') }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.dAppliedDate') }}</span><span class="fvalue" [class.missing]="!detail.appliedDate">{{ detail.appliedDate || t('tracker.notRecorded') }}</span></div>
        <div class="field"><span class="flabel">URL</span>
          <span class="fvalue">
            @if (detail.link) {
              <a [href]="detail.link" target="_blank" class="teal-link">{{ t('tracker.dViewPosting') }} <mat-icon>open_in_new</mat-icon></a>
              <button mat-button class="import-jd-btn" (click)="startEditJdText(); scrollToJd()">
                <mat-icon>input</mat-icon>{{ t('tracker.dImportJd') }}
              </button>
            } @else { <span class="missing">{{ t('tracker.notRecorded') }}</span> }
          </span>
        </div>
        <div class="field"><span class="flabel">{{ t('tracker.saWorkMode') }}</span><span class="fvalue" [class.missing]="!detail.workMode">{{ detail.workMode || t('tracker.notRecorded') }}</span></div>
      </div>

      <!-- 2026-10-19:Simplify 风格 —— 无 "About the job" 大标题,JD 正文直接跟字段网格 -->
      <mat-divider></mat-divider>
      <section class="about-job">
        @if (!hideJdTools) {
        <div class="about-job-edit">
          <button mat-icon-button (click)="reparseJd()" [matTooltip]="t('tracker.dReparse')"
                  class="edit-btn" [disabled]="reparsing()"
          [attr.aria-label]="t('tracker.dReparse')">
            <mat-icon>{{ reparsing() ? 'hourglass_empty' : 'refresh' }}</mat-icon>
          </button>
          <button mat-icon-button (click)="jdSummaryEditing() ? cancelEditJdSummary() : startEditJdSummary()"
                  [matTooltip]="t('common.edit')" class="edit-btn"
          [attr.aria-label]="t('common.edit')">
            <mat-icon>{{ jdSummaryEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        }
        <!-- 票 226：小节编辑入口（不在 hideJdTools 门内，226 已批准实施，与摘要编辑独立） -->
        <div class="about-job-edit">
          <button mat-icon-button (click)="sectionsEditing() ? cancelEditSections() : startEditSections()"
                  [matTooltip]="t('common.edit')" class="edit-btn"
          [attr.aria-label]="t('common.edit')">
            <mat-icon>{{ sectionsEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        @if (jdSummaryEditing()) {
          <textarea class="plain-area" rows="8" [(ngModel)]="jdSummaryDraft"
                    (ngModelChange)="jdSummaryError.set(null)"
                    [placeholder]="t('tracker.dJdSummary')"></textarea>
          @if (jdSummaryError()) {
            <div class="field-error-plain" role="alert">{{ jdSummaryError() }}</div>
          }
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveJdSummary()"
                    [disabled]="jdSummarySaving()">
              @if (jdSummarySaving()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditJdSummary()">{{ t('common.cancel') }}</button>
          </div>
        } @else {
          <div class="about-job-body">
          <!-- Job Description -->
          <div class="jd-subhead">{{ t('tracker.dJobDescHeader') }}</div>
          @if (richBlocks(jdSummaryDisplay()).length > 0) {
            @for (b of richBlocks(jdSummaryDisplay()); track $index) {
              @if (b.kind === 'ul') {
                <ul class="jd-list">@for (li of b.items; track $index) { <li>{{ li }}</li> }</ul>
              } @else {
                <p>{{ b.items[0] }}</p>
              }
            }
          } @else {
            <p class="dim">{{ t('tracker.dNoJd') }}</p>
          }
          <!-- 票 154 R-154-6：有小节渲染原标题小节（公司介绍重复节已去重），
               否则一字不差走今日 legacy 两块。 -->
          <!-- 票 226：编辑态替代渲染（草稿来自存量 sectionsJson 原值，不经过展示层去重，保存不丢数据） -->
          @if (sectionsEditing()) {
            <div class="sec-edit">
              <div class="sa-sections-label">{{ t('tracker.saSections') }}</div>
              @for (d of sectionDrafts; track d.id) {
                <div class="sec-edit-item">
                  <mat-form-field appearance="outline" class="sec-title-field">
                    <mat-label>{{ t('tracker.saSectionTitle') }}</mat-label>
                    <input matInput [(ngModel)]="d.title" (ngModelChange)="sectionsError.set(null)">
                  </mat-form-field>
                  <mat-form-field appearance="outline" class="sec-body-field">
                    <mat-label>{{ t('tracker.saSectionBody') }}</mat-label>
                    <textarea matInput rows="4" [(ngModel)]="d.body" (ngModelChange)="sectionsError.set(null)"></textarea>
                  </mat-form-field>
                  <button mat-button type="button" (click)="removeSectionDraft(d.id)">{{ t('tracker.saSectionDelete') }}</button>
                </div>
              }
              <button mat-stroked-button type="button" (click)="addSectionDraft()">{{ t('tracker.saSectionAdd') }}</button>
              @if (sectionsError()) {
                <div class="field-error-plain" role="alert">{{ sectionsError() }}</div>
              }
              <div class="cl-actions save-row">
                <button mat-flat-button color="primary" (click)="saveSections()"
                        [disabled]="sectionsSaving()">
                  @if (sectionsSaving()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
                  {{ t('common.save') }}
                </button>
                <button mat-button (click)="cancelEditSections()">{{ t('common.cancel') }}</button>
              </div>
            </div>
          } @else if (displaySections.length > 0) {
            @for (s of displaySections; track $index) {
              <div class="jd-subhead">{{ s.title }}</div>
              @for (b of richBlocks(s.body); track $index) {
                @if (b.kind === 'ul') {
                  <ul class="jd-list">@for (li of b.items; track $index) { <li>{{ li }}</li> }</ul>
                } @else {
                  <p>{{ b.items[0] }}</p>
                }
              }
            }
          } @else {
            <!-- Qualifications -->
            @if (detail.requirements) {
              <div class="jd-subhead">{{ t('tracker.dQualifications') }}</div>
              @for (b of richBlocks(detail.requirements); track $index) {
                @if (b.kind === 'ul') {
                  <ul class="jd-list">@for (li of b.items; track $index) { <li>{{ li }}</li> }</ul>
                } @else {
                  <p>{{ b.items[0] }}</p>
                }
              }
            }
            <!-- Additional Information -->
            @if (detail.benefits) {
              <div class="jd-subhead">{{ t('tracker.dAdditionalInfo') }}</div>
              @for (b of richBlocks(detail.benefits); track $index) {
                @if (b.kind === 'ul') {
                  <ul class="jd-list">@for (li of b.items; track $index) { <li>{{ li }}</li> }</ul>
                } @else {
                  <p>{{ b.items[0] }}</p>
                }
              }
            }
          }
          </div>

        }
      </section>

      <!-- 公司介绍:LinkedIn "About the company" 侧边栏 —— 独立成节(票142:空态也渲染) -->
      <mat-divider></mat-divider>
      <section class="about-job">
        <div class="about-job-edit">
          <button mat-icon-button (click)="companyDescEditing() ? cancelEditCompanyDesc() : startEditCompanyDesc()"
                  [matTooltip]="t('common.edit')" class="edit-btn"
          [attr.aria-label]="t('common.edit')">
            <mat-icon>{{ companyDescEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        <div class="jd-subhead" style="margin-top:0">{{ t('tracker.companyDescription') }}</div>
        @if (companyDescEditing()) {
          <textarea class="plain-area" rows="8" [(ngModel)]="companyDescDraft"
                    (ngModelChange)="companyDescError.set(null)"
                    [placeholder]="t('tracker.companyDescription')"></textarea>
          @if (companyDescError()) {
            <div class="field-error-plain" role="alert">{{ companyDescError() }}</div>
          }
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveCompanyDesc()"
                    [disabled]="companyDescSaving()">
              @if (companyDescSaving()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditCompanyDesc()">{{ t('common.cancel') }}</button>
          </div>
        } @else if (detail.companyDescription) {
          @for (b of richBlocks(detail.companyDescription); track $index) {
            @if (b.kind === 'ul') {
              <ul class="jd-list">@for (li of b.items; track $index) { <li>{{ li }}</li> }</ul>
            } @else {
              <p>{{ b.items[0] }}</p>
            }
          }
        } @else {
          <p class="dim">{{ t('tracker.dNoCompanyDesc') }}
            <button mat-button (click)="startEditCompanyDesc()">{{ t('common.add') }}</button>
          </p>
        }
      </section>

      <!-- 票189：用户令去掉 Company intel 节（先隐藏不删除，编辑/抓取逻辑与 i18n 键保留）。 -->
      @if (!hideCompanyIntel) {
      <!-- 公司情报(票142:空态也渲染,公司级共享) -->
      <mat-divider></mat-divider>
      <section>
        <div class="sec-head"><span class="sec-title">{{ t('tracker.dCompanyIntel') }}</span><span class="sec-sub">{{ company()?.name }}</span>
          <button mat-icon-button class="edit-btn" (click)="profileEditing() ? cancelEditProfile() : startEditProfile()"
                  [matTooltip]="t('common.edit')" [attr.aria-label]="t('common.edit')" [disabled]="!company()">
            <mat-icon>{{ profileEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        <p class="dim">{{ t('tracker.dCompanyIntelShared') }}</p>
        @if (profileEditing()) {
          <textarea class="plain-area" rows="8" [(ngModel)]="profileDraft"
                    (ngModelChange)="profileError.set(null)"
                    [placeholder]="t('tracker.dCompanyIntel')"></textarea>
          @if (fetchedProfileSource()) {
            <a [href]="fetchedProfileSource()!" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ fetchedProfileSource() }}
            </a>
          }
          @if (profileFetchError()) {
            <div class="field-error-plain" role="alert">{{ profileFetchError() }}</div>
          }
          @if (profileError()) {
            <div class="field-error-plain" role="alert">{{ profileError() }}</div>
          }
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveProfile()"
                    [disabled]="profileSaving() || profileFetching()">
              @if (profileSaving()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditProfile()">{{ t('common.cancel') }}</button>
            <button mat-button (click)="fetchCompanyProfile()" [disabled]="profileFetching()">
              {{ profileFetching() ? t('tracker.dFetchingProfile') : t('tracker.dRefetchProfile') }}
            </button>
          </div>
        } @else if (company()?.profile) {
          <p class="pre">{{ company()?.profile }}</p>
          @for (src of profileSources(); track $index) {
            <a [href]="src" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ src }}
            </a>
          }
          @if (company()?.website) {
            <a [href]="company()!.website!" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ company()!.website }}
            </a>
          }
        } @else {
          <p class="dim">{{ t('tracker.dNoCompanyIntel') }}
            <button mat-button (click)="startEditProfile()" [disabled]="!company()">{{ t('common.add') }}</button>
            <button mat-button (click)="fetchCompanyProfile()" [disabled]="!company() || profileFetching()">
              {{ profileFetching() ? t('tracker.dFetchingProfile') : t('tracker.dFetchProfile') }}
            </button>
          </p>
          @if (profileFetchError()) {
            <div class="field-error-plain" role="alert">{{ profileFetchError() }}</div>
          }
          @if (company()?.website) {
            <a [href]="company()!.website!" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ company()!.website }}
            </a>
          }
        }
      </section>
      }


      @if ((detail.rounds?.length ?? 0) > 0) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dRounds') }}</span></div>
          <ul class="rounds">
            @for (r of detail.rounds ?? []; track r.id) {
              <li>
                <strong>{{ tn('tracker.dRoundN', r.order) }}</strong>
                @if (r.stage) { <span class="tag">{{ r.stage }}</span> }
                @if (r.scheduledDate) { <span>{{ r.scheduledDate | date: 'yyyy-MM-dd' }}</span> }
                @if (r.format) { <span class="tag">{{ r.format }}</span> }
                @if (r.interviewers) { <span class="tag">{{ r.interviewers }}</span> }
                @if (r.outcome && r.outcome !== 'Pending') {
                  <span class="tag" [class.outcome-pass]="r.outcome === 'Passed'"
                        [class.outcome-fail]="r.outcome === 'Failed'">{{ outcomeLabel(r.outcome) }}</span>
                }
                @if (r.notes) { <p class="pre">{{ r.notes }}</p> }
              </li>
            }
          </ul>
        </section>
      }

      <!-- 票189：用户令去掉 Job link 节（先隐藏不删除，app.link 数据与链接渲染保留）。 -->
      @if (!hideJobLink) {
      @if (app.link) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dJobLink') }}</span></div>
          <a [href]="app.link" target="_blank" rel="noopener noreferrer" class="link">
            {{ app.link }}
          </a>
        </section>
      }
      }
      </div><!-- /.main-col -->

      <!-- ★ Simplify 真站:右侧边栏 -->
      <div class="side-col">
        <!-- Application Status 卡片 -->
        <div class="status-card">
          <h3>{{ t('tracker.dAppStatus') }}</h3>
          <select class="status-select" [value]="detail.status" (change)="quickChangeStatus($event)">
            @for (s of statusOrder; track s) {
              <option [value]="s" [selected]="s === detail.status">{{ statusLabel(s) }}</option>
            }
          </select>
          <div class="timeline">
            @for (s of statusOrder; track s) {
              <div class="tl-item" [class.done]="isStatusDone(s)">
                <span class="tl-dot"></span>
                <span class="tl-name">{{ statusLabel(s) }}</span>
                @if (statusDate(s)) {
                  <span class="tl-date">{{ statusDate(s) }}</span>
                }
              </div>
            }
          </div>
        </div>

        <!-- Notes 卡片(可编辑) -->
        <div class="notes-card">
          <h3 class="nc-head">
            <span>{{ t('tracker.dNotes') }}</span>
            <button mat-icon-button class="edit-btn" (click)="notesEditing() ? cancelEditNotes() : startEditNotes()"
                    [matTooltip]="t('common.edit')"
            [attr.aria-label]="t('common.edit')">
              <mat-icon>{{ notesEditing() ? 'close' : 'edit' }}</mat-icon>
            </button>
          </h3>
          @if (notesEditing()) {
            <textarea class="plain-area" rows="6" [(ngModel)]="notesDraft"
                      [placeholder]="t('tracker.dNotes')"></textarea>
            <div class="cl-actions save-row">
              <button mat-flat-button color="primary" (click)="saveNotes()"
                      [disabled]="notesSaving()">{{ t('common.save') }}</button>
              <button mat-button (click)="cancelEditNotes()">{{ t('common.cancel') }}</button>
            </div>
          } @else {
            <p class="pre" [class.missing]="!detail.notes">{{ detail.notes || t('tracker.dNoNotes') }}</p>
          }
        </div>
      </div><!-- /.side-col -->
      </div><!-- /.two-col -->
      }

      @else if (detailTab() === 'documents') {
        <div class="tab-body">
      <!-- 简历区:投递版本优先,没有则引用基础简历(不复制) -->
      <section class="cl-section">
        <div class="sec-head">
          <mat-icon>description</mat-icon>
          <span>{{ t('tracker.dResume') }}</span>
          @if (appResumeKind() === 'tailored') {
            <span class="cl-status cl-generated">{{ t('tracker.dResumeTailored') }}</span>
          } @else if (appResumeKind() === 'base') {
            <span class="cl-status cl-draft">{{ t('tracker.dResumeBase') }}</span>
          } @else if (appResumeKind() === 'text') {
            <span class="cl-status cl-draft">{{ t('tracker.dResumeText') }}</span>
          }
          <span class="doc-actions">
            <button mat-button (click)="tailoredFile.click()">
              <mat-icon>upload_file</mat-icon>{{ t('tracker.dResumeUploadTailored') }}
            </button>
            <input #tailoredFile type="file" hidden accept=".pdf"
                   (change)="uploadTailoredResume($event)">
            @if (appResumeKind() === 'tailored') {
              <button mat-button (click)="removeTailoredResume()">
                <mat-icon>delete_outline</mat-icon>{{ t('common.delete') }}
              </button>
            }
          </span>
        </div>
        <!-- 文件名 -->
        @if (resumeFileName()) {
          <div class="doc-filename">
            <mat-icon>picture_as_pdf</mat-icon>
            <span>{{ resumeFileName() }}</span>
          </div>
          <!-- PDF 查看器:单页显示,滚动翻页 -->
          @if (resumePdfRawUrl()) {
            <app-pdf-content-viewer [pdfUrl]="resumePdfRawUrl()" [showZoomControls]="true"></app-pdf-content-viewer>
          }
        } @else if (appResumeKind() === 'text') {
          <!-- REQ-05:主简历只有文本版 —— 显示全文,不再误报「No resume yet」 -->
          <p class="field-hint">{{ t('tracker.dResumeHint') }}</p>
          <pre class="resume-text-view">{{ appResumeText() }}</pre>
        } @else {
          <p class="field-hint">{{ t('tracker.dResumeHint') }}</p>
          <p class="dim">{{ t('tracker.dNoResume') }}</p>
        }
      </section>
      <section class="cl-section">
        <div class="sec-head">
          <mat-icon>description</mat-icon>
          <span>{{ t('tracker.dCoverLetter') }}</span>
          @if (cl(); as c) {
            <span class="cl-status" [class]="'cl-' + c.status.toLowerCase()">{{ clStatusLabel(c.status) }}</span>
          }
          <span class="fold-len">
            @if (cl(); as c) { {{ tn('tracker.dClChars', c.content.length) }} }
          </span>
        </div>

        <!-- 缺 JD 黄色提示条 -->
        @if (clReadiness(); as rd) {
          @if (missingText(rd); as missingText) {
            <div class="warn-banner">
              <mat-icon>warning</mat-icon>
              <span>{{ missingText }}</span>
            </div>
          }
          @if (cl()?.isStale) {
            <div class="warn-banner">
              <mat-icon>history</mat-icon>
              <span>{{ tf('tracker.dClStale', { a: cl()?.resumeVersion, b: rd.resumeVersion }) }}</span>
            </div>
          }
        }

        <!-- 生成参数:朴素 textarea -->
        @if (!clGenerating()) {
          <textarea class="plain-area" rows="3" [(ngModel)]="clExtra"
            [placeholder]="t('tracker.dClExtraPh')"></textarea>
          <p class="field-hint">{{ t('tracker.dClHint') }}</p>
        }

        <!-- 生成按钮 / 生成中 -->
        @if (clGenerating()) {
          <div class="cl-generating">
            <mat-progress-bar mode="indeterminate"></mat-progress-bar>
            <p>{{ t('tracker.dClGenerating') }}</p>
          </div>
        } @else {
          <div class="cl-actions">
            <button mat-flat-button color="primary" class="gen-btn" (click)="generateCoverLetter()"
              [disabled]="!!clReadiness()?.missingCode">
              <mat-icon>auto_awesome</mat-icon>
              {{ (cl()?.content ? t('tracker.dClRegen') : t('tracker.dClGenerate')) }}
            </button>
            @if (cl()?.content) {
              <button mat-button (click)="clEditing.set(!clEditing())">
                <mat-icon>{{ clEditing() ? 'visibility' : 'edit' }}</mat-icon>
                {{ clEditing() ? t('tracker.dClPreview') : t('tracker.dClEditMan') }}
              </button>
              @if (cl()?.status !== 'Final') {
                <button mat-button (click)="markCoverLetterFinal()">
                  <mat-icon>check_circle</mat-icon>{{ t('tracker.dClConfirm') }}
                </button>
              }
              <button mat-button (click)="deleteCoverLetter()">
                <mat-icon>delete_outline</mat-icon>{{ t('common.delete') }}
              </button>
            }
          </div>
        }

        <!-- 正文:编辑态 textarea / 预览态只读 -->
        @if (cl()?.content; as content) {
          @if (clEditing()) {
            <textarea class="plain-area" rows="16" [(ngModel)]="clDraft"></textarea>
            <div class="cl-actions save-row">
              <button mat-flat-button color="primary" (click)="saveCoverLetter()">{{ t('common.save') }}</button>
              <button mat-button (click)="cancelEdit()">{{ t('common.cancel') }}</button>
            </div>
          } @else {
            <div class="cl-preview">{{ content }}</div>
          }
        } @else if (!clGenerating()) {
          <p class="cl-empty">{{ t('tracker.dClEmpty') }}</p>
        }
      </section>
        </div>
      }

      @else if (detailTab() === 'score') {
        <div class="tab-body score-tab">
      <!-- 2026-10-19:Simplify 像素级 Resume Score —— 左 JD 卡片 + 右仪表盘/关键词 -->
      @if (match(); as m) {
        <div class="score-cols">
          <!-- 左:Job Description 卡片(关键词高亮) -->
          <div class="score-jd-card">
            <div class="score-jd-head">
              <mat-icon>work</mat-icon>
              <span>{{ t('tracker.dJobDescHeader') }}</span>
            </div>
            <!-- 票 139：Score 页 JD 与 Overview 逐节一致（摘要 richBlocks + Qualifications 节 + Additional Information 节），
                 每节文本经 highlightHtml 标记命中词；卡片头已是 Job Description 标题，卡片内不再重复该小节标题；
                 只读展示，不复制 Overview 的编辑/重解析工具条。 -->
            <div class="score-jd-body">
              @if (richBlocks(jdSummaryDisplay()).length > 0) {
                @for (b of richBlocks(jdSummaryDisplay()); track $index) {
                  @if (b.kind === 'ul') {
                    <ul class="jd-list">@for (li of b.items; track $index) { <li [innerHTML]="highlightHtml(li, m.hit)"></li> }</ul>
                  } @else {
                    <p [innerHTML]="highlightHtml(b.items[0], m.hit)"></p>
                  }
                }
              } @else {
                <p class="dim">{{ t('tracker.dNoJd') }}</p>
              }
              <!-- 票 154 R-154-6：Score 页与 Overview 同规则（小节 + 命中词高亮，否则 legacy）。 -->
              @if (displaySections.length > 0) {
                @for (s of displaySections; track $index) {
                  <div class="jd-subhead">{{ s.title }}</div>
                  @for (b of richBlocks(s.body); track $index) {
                    @if (b.kind === 'ul') {
                      <ul class="jd-list">@for (li of b.items; track $index) { <li [innerHTML]="highlightHtml(li, m.hit)"></li> }</ul>
                    } @else {
                      <p [innerHTML]="highlightHtml(b.items[0], m.hit)"></p>
                    }
                  }
                }
              } @else {
                @if (detail.requirements) {
                  <div class="jd-subhead">{{ t('tracker.dQualifications') }}</div>
                  @for (b of richBlocks(detail.requirements); track $index) {
                    @if (b.kind === 'ul') {
                      <ul class="jd-list">@for (li of b.items; track $index) { <li [innerHTML]="highlightHtml(li, m.hit)"></li> }</ul>
                    } @else {
                      <p [innerHTML]="highlightHtml(b.items[0], m.hit)"></p>
                    }
                  }
                }
                @if (detail.benefits) {
                  <div class="jd-subhead">{{ t('tracker.dAdditionalInfo') }}</div>
                  @for (b of richBlocks(detail.benefits); track $index) {
                    @if (b.kind === 'ul') {
                      <ul class="jd-list">@for (li of b.items; track $index) { <li [innerHTML]="highlightHtml(li, m.hit)"></li> }</ul>
                    } @else {
                      <p [innerHTML]="highlightHtml(b.items[0], m.hit)"></p>
                    }
                  }
                }
              }
            </div>
          </div>
          <!-- 右:仪表盘 + 关键词 -->
          <div class="score-side">
            <div class="gauge-wrap">
              <svg class="gauge" viewBox="0 0 190 190" width="190" height="190">
                <circle cx="95" cy="95" r="80" fill="none" stroke="#e2e8f0" stroke-width="16"/>
                <circle cx="95" cy="95" r="80" fill="none" stroke="#2563eb" stroke-width="16"
                        stroke-linecap="round" [attr.stroke-dasharray]="gaugeArc(m.score)"
                        transform="rotate(-90 95 95)"/>
              </svg>
              <div class="gauge-center">
                <span class="gauge-num">{{ m.score }}</span>
                <span class="gauge-pill" [class]="matchTier(m.score)">{{ matchTierLabel(m.score) }}</span>
              </div>
            </div>
            @if (m.score >= 70) {
              <div class="score-banner ok">
                <mat-icon>check_circle</mat-icon>
                <span>{{ t('tracker.dScoreGreat') }}</span>
              </div>
            } @else if (m.score >= 40) {
              <div class="score-banner warn">
                <mat-icon>info</mat-icon>
                <span>{{ t('tracker.dScoreFair') }}</span>
              </div>
            }
            @if (m.missing.length > 0) {
              <div class="kw-card">
                <div class="kw-card-title">{{ tn('tracker.dMissingKw', m.missing.length) }}</div>
                <div class="kw-pills">
                  @for (k of m.missing; track k) {
                    <span class="kw-pill miss">{{ k }}</span>
                  }
                </div>
              </div>
            }
            @if (m.hit.length > 0) {
              <div class="kw-card">
                <div class="kw-card-title">{{ tn('tracker.dMatchedKw', m.hit.length) }}</div>
                <div class="kw-pills">
                  @for (k of m.hit; track k) {
                    <span class="kw-pill hit">{{ k }}</span>
                  }
                </div>
              </div>
            }
          </div>
        </div>
      } @else {
        <!-- ★ F-08:空态分清缺什么 —— 有 JD 缺简历时不能再说"暂无 JD"。 -->
        <p class="cl-empty">{{ hasJdForScore() ? t('tracker.dNoResume') : t('tracker.dNoJd') }}</p>
      }
        </div>
      }

      @else if (detailTab() === 'questions') {
        <div class="tab-body questions-tab">
          <div class="sec-head">
            <span>{{ t('tracker.dQuestions') }}</span>
            <span class="sec-count">{{ appQuestions().length }}</span>
          </div>
          <p class="sec-desc">{{ t('tracker.dQuestionsDesc') }}</p>

          @if (appQuestions().length === 0 && !questionAdding()) {
            <div class="q-empty">
              <mat-icon>help_outline</mat-icon>
              <p>{{ t('tracker.dQuestionsEmpty') }}</p>
            </div>
          }

          <div class="q-list">
            @for (q of appQuestions(); track q.id; let i = $index) {
              <div class="q-item">
                <span class="q-num">{{ i + 1 }}</span>
                <span class="q-text">{{ q.text }}</span>
                <button mat-icon-button class="q-del" (click)="removeQuestion(q.id)"
                        [matTooltip]="t('common.delete')"
                [attr.aria-label]="t('common.delete')">
                  <mat-icon>delete_outline</mat-icon>
                </button>
              </div>
            }
          </div>

          @if (questionAdding()) {
            <div class="q-add-form">
              <mat-form-field appearance="outline" class="full">
                <mat-label>{{ t('tracker.dQuestionAdd') }}</mat-label>
                <textarea matInput rows="2" [(ngModel)]="questionDraft"
                          (ngModelChange)="questionError.set(null)"
                          (keydown.enter)="addQuestion(); $event.preventDefault()"></textarea>
              </mat-form-field>
              @if (questionError()) {
                <div class="field-error" role="alert">{{ questionError() }}</div>
              }
              <div class="q-add-actions">
                <button mat-flat-button color="primary" (click)="addQuestion()"
                        [disabled]="!questionDraft.trim()">{{ t('common.add') }}</button>
                <button mat-button (click)="questionAdding.set(false); questionDraft = ''; questionError.set(null)">{{ t('common.cancel') }}</button>
              </div>
            </div>
          } @else {
            <button mat-stroked-button (click)="questionAdding.set(true)" class="q-add-btn">
              <mat-icon>add</mat-icon>{{ t('tracker.dQuestionAdd') }}
            </button>
          }
        </div>
      }

      @else {
        <!-- Email/Communications tab -->
        <!-- 沟通记录:按发生时间倒序的时间线 + 增删改 -->
        <div class="tab-body comms">
          <div class="comms-head">
            <button mat-flat-button color="primary" (click)="startAddComm()">
              <mat-icon>add</mat-icon>{{ t('tracker.commsAdd') }}
            </button>
          </div>

          @if (commsLoading()) {
            <mat-progress-bar mode="indeterminate"></mat-progress-bar>
          }

          @if (commMode() !== 'closed') {
            <div class="comm-form">
              <div class="row2">
                <mat-form-field appearance="outline">
                  <mat-label>{{ t('tracker.commsType') }}</mat-label>
                  <mat-select [(ngModel)]="commDraft().type">
                    @for (tp of commTypes; track tp) {
                      <mat-option [value]="tp">{{ commTypeLabel(tp) }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>{{ t('tracker.commsOccurredAt') }}</mat-label>
                  <input matInput type="datetime-local" [(ngModel)]="commDraft().occurredAt">
                </mat-form-field>
              </div>
              <mat-form-field appearance="outline" class="full">
                <mat-label>{{ t('tracker.commsSubject') }}</mat-label>
                <input matInput [(ngModel)]="commDraft().subject" [placeholder]="t('tracker.dCommSubjectPh')"
                       (ngModelChange)="clearCommError('subject')">
              </mat-form-field>
              @if (commFieldErrors().subject) {
                <div class="field-error" role="alert">{{ commFieldErrors().subject }}</div>
              }
              <div class="row2">
                <mat-form-field appearance="outline">
                  <mat-label>{{ t('tracker.commsContact') }}</mat-label>
                  <input matInput [(ngModel)]="commDraft().contactName" [placeholder]="t('tracker.dCommContactPh')"
                         (ngModelChange)="clearCommError('contactName')">
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Email</mat-label>
                  <input matInput [(ngModel)]="commDraft().contactEmail" placeholder="name@company.com"
                         (ngModelChange)="clearCommError('contactEmail')">
                </mat-form-field>
              </div>
              @if (commFieldErrors().contactName) {
                <div class="field-error" role="alert">{{ commFieldErrors().contactName }}</div>
              }
              @if (commFieldErrors().contactEmail) {
                <div class="field-error" role="alert">{{ commFieldErrors().contactEmail }}</div>
              }
              <mat-form-field appearance="outline" class="full">
                <mat-label>{{ t('tracker.commsContent') }}</mat-label>
                <textarea matInput rows="3" [(ngModel)]="commDraft().content"
                          (ngModelChange)="clearCommError('content')"></textarea>
              </mat-form-field>
              @if (commFieldErrors().content) {
                <div class="field-error" role="alert">{{ commFieldErrors().content }}</div>
              }
              <div class="comm-form-actions">
                <button mat-flat-button color="primary" [disabled]="!commDraft().content.trim()"
                        (click)="saveComm()">{{ commEditingId() ? t('common.save') : t('tracker.commsAdd') }}</button>
                <button mat-button (click)="cancelComm()">{{ t('common.cancel') }}</button>
              </div>
            </div>
          }

          @if (comms().length === 0 && !commsLoading()) {
            <p class="comms-empty">{{ t('tracker.commsEmpty') }}</p>
          }

          <ul class="comm-list">
            @for (c of comms(); track c.id) {
              <li class="comm-item">
                <span class="comm-type" [class]="'ct-' + c.type.toLowerCase()">{{ commTypeLabel(c.type) }}</span>
                <div class="comm-body">
                  @if (c.subject) { <div class="comm-subject">{{ c.subject }}</div> }
                  <div class="comm-content pre">{{ c.content }}</div>
                  <div class="comm-meta">
                    @if (c.contactName) { <span>{{ c.contactName }}</span> }
                    @if (c.contactEmail) { <span>{{ c.contactEmail }}</span> }
                    <span>{{ c.occurredAt | date: 'yyyy-MM-dd HH:mm' }}</span>
                  </div>
                </div>
                <div class="comm-actions">
                  <button mat-icon-button [matTooltip]="t('common.edit')" (click)="startEditComm(c)"
                  [attr.aria-label]="t('common.edit')">
                    <mat-icon>edit</mat-icon>
                  </button>
                  <button mat-icon-button class="danger" [matTooltip]="t('common.delete')"
                          (click)="deleteComm(c)"
                  [attr.aria-label]="t('common.delete')">
                    <mat-icon>delete_outline</mat-icon>
                  </button>
                </div>
              </li>
            }
          </ul>
        </div>
      }
    </mat-dialog-content>
  `,
  styles: [`
    /* 票 147:宿主作手柄定位锚点(面板 flex 子项,本身已撑满面板,不改布局)。 */
    :host { position: relative; }
    /* 票 147:抽屉左边缘内侧拖拽手柄(6px 全高,平时透明,悬停/聚焦/拖动时显品牌色竖线)。 */
    .drawer-resize-handle { position: absolute; left: 0; top: 0; bottom: 0; width: 6px;
      z-index: 10; cursor: ew-resize; outline: none; }
    .drawer-resize-handle::before { content: ''; position: absolute; left: 2px; top: 0; bottom: 0;
      width: 2px; background: var(--brand, #2563eb); opacity: 0; transition: opacity .15s; }
    .drawer-resize-handle:hover::before, .drawer-resize-handle:focus-visible::before,
    .drawer-resize-handle.dragging::before { opacity: 1; }
    /* ★ Simplify 对齐:头部标题堆叠 */
    /* ★ Simplify 真站:头部 */
    h2[mat-dialog-title].detail-header {
      display: flex; flex-direction: column; align-items: stretch; gap: 8px;
      margin: 0; padding: 24px 32px 20px;
      border-bottom: 1px solid #e2e8f0;
    }
    h2 .header-top { display: flex; align-items: center; gap: 12px; }
    h2 .company-title { font-size: 21px; font-weight: 700; line-height: 1.3; color: #0f172a; flex: 1; min-width: 0; }
    h2 .role-title { font-size: 23px; font-weight: 700; line-height: 1.3; color: #0f172a; }
    /* 票 224:62px 页头 logo 覆盖已删,logo 回落到下方 40px 通用规则(首字母圆 + 真实 logo 绝对覆盖),判定链不动。 */
    .header-actions { display: flex; align-items: center; gap: 10px; margin-left: auto; }
    .hdr-btn {
      display: inline-flex; align-items: center; gap: 8px;
      height: 38px; padding: 0 16px;
      background: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px;
      font-size: 14px; color: #0f172a; cursor: pointer;
    }
    .hdr-btn mat-icon { font-size: 18px; width: 18px; height: 18px; color: #475569; }
    /* ★ 票123 C-3.2:可点项取统一洗色(原为 "#f6f8fb")。 */
    .hdr-btn:hover { background: var(--hover-wash); }
    .hdr-btn:active { background: var(--press-wash); }
    .hdr-btn.danger { border-color: #dc2626; color: #dc2626; }
    .hdr-btn.danger mat-icon { color: #dc2626; }
    .hdr-close {
      display: inline-flex; align-items: center; justify-content: center;
      width: 38px; height: 38px; border: none; background: none; cursor: pointer;
      color: #0f172a; border-radius: 8px;
    }
    /* ★ 票123 C-3.2:可点项取统一洗色(原为 "#f6f8fb")。 */
    .hdr-close:hover { background: var(--hover-wash); }
    .hdr-close:active { background: var(--press-wash); }
    /* 票 141:Material 默认 .mat-mdc-dialog-content { max-height: 65vh } 未被覆盖，
       抽屉 100vh 时滚动区只有 65vh、底部留约 35vh 死区、滚动条到不了抽屉底。
       抽屉本身就是全高，滚动区应占满标题下方全部剩余高度，故取消该上限。 */
    .detail-body { padding: 0 !important; flex: 1 1 auto !important; overflow-y: auto !important; min-height: 0 !important; max-height: none !important; }
    /* ★ Simplify 真站:双栏布局 */
    .two-col { display: flex; min-height: 100%; align-items: stretch; }
    .main-col { flex: 1; min-width: 0; padding: 24px 32px; }
    .side-col {
      flex: 0 0 360px; width: 360px;
      padding: 24px 32px 24px 0;
      border-left: 1px solid #e2e8f0;
      margin-left: 32px; padding-left: 32px;
    }
    .status-card {
      background: #ffffff; border: 1px solid #e2e8f0; border-radius: 10px;
      padding: 24px; margin-bottom: 24px;
    }
    .status-card h3 { font-family: 'Space Grotesk', Inter, sans-serif; font-size: 16px; font-weight: 600; color: #0f172a; margin: 0 0 16px; }
    .status-select {
      width: 100%; height: 40px; padding: 0 16px;
      border: 1px solid #e2e8f0; border-radius: 8px;
      font-size: 14px; color: #0f172a; background: #ffffff;
      margin-bottom: 20px; cursor: pointer;
    }
    /* 票123 R-9:抽屉时间轴迁移到共享几何源(styles/_track-geometry.scss)——
       节点 20px → 16px、轨道 #E3E6EA → 共享待决轨道色、
       done 字形(品牌描边 + 8px 中心点)→ 共享「已到达」处理(品牌实心 + 光晕)。
       迁移后它与 Tracker 列表 stepper / Dashboard 带 / Playbook 轮次轨道是同一套语言。 */
    .timeline { position: relative; padding-left: 24px; }
    .timeline::before {
      content: ''; position: absolute; left: 7px; top: 8px; bottom: 8px;
      width: var(--track-rail-width); background: var(--track-rail-pending);
    }
    .tl-item { position: relative; padding: 0 0 20px; }
    .tl-item:last-child { padding-bottom: 0; }
    .tl-dot {
      position: absolute; left: -24px; top: 2px;
      width: var(--track-node-size); height: var(--track-node-size); border-radius: 50%;
      border: var(--track-node-border) solid var(--track-node-future-border);
      background: var(--track-node-future-fill);
    }
    .tl-item.done .tl-dot {
      border-color: var(--track-node-reached-border);
      background: var(--track-node-reached-fill);
      box-shadow: var(--track-node-reached-halo);
    }
    .tl-name { font-size: 14px; font-weight: 600; color: #0f172a; }
    .tl-item:not(.done) .tl-name { font-weight: 500; color: #475569; }
    .tl-date { font-size: 13px; color: #64748b; margin-left: 8px; }
    .tl-edit { margin-left: 8px; color: var(--brand); cursor: pointer; font-size: 14px; }
    .notes-card {
      background: #ffffff; border: 1px solid #e2e8f0; border-radius: 10px;
      padding: 24px;
    }
    .notes-card h3 { font-family: 'Space Grotesk', Inter, sans-serif; font-size: 16px; font-weight: 600; color: #0f172a; margin: 0 0 16px; }
    .notes-card .nc-head { display: flex; align-items: center; justify-content: space-between; }
    .tab-body { padding: 24px 32px; box-sizing: border-box; }
    .tab-body > section:first-child { margin-top: 0; }
    .top { margin-bottom: 14px; }
    .chip { font-size: 12px; }
    .rejection { margin: 6px 0 0; font-size: 13px; color: #dc2626; }
    /* ★ Simplify 对齐:状态 pill (灰色圆角) */
    .status-pill {
      display: inline-block; padding: 6px 16px; border-radius: 999px;
      background: rgba(15, 23, 42, 0.06); font-size: 13px; font-weight: 500;
      margin-right: 8px; margin-bottom: 4px;
    }
    .status-pill.warn { background: #fffbeb; color: #b06a00; }
    /* ★ Simplify 对齐:字段两列,标签小灰字在上 */
    .fields {
      display: grid; grid-template-columns: 1fr 1fr; gap: 16px 24px;
      margin: 0 0 24px;
    }
    .field { display: flex; flex-direction: column; gap: 4px; }
    .flabel { font-size: 12px; color: rgba(15, 23, 42, 0.45); letter-spacing: 0.2px; }
    .fvalue { font-size: 14px; line-height: 1.5; }
    .fvalue.missing, .pre.missing { color: var(--ink-2); }
    .kw-gap { margin: 0 0 20px; }
    .kw-gap .flabel { display: block; margin-bottom: 8px; }
    /* ---- Simplify 视觉:section 统一 ---- */
    section { margin: 0; padding: 18px 0; }
    section + section, mat-divider + section { border-top: 1px solid rgba(15, 23, 42, 0.08); }
    mat-divider { display: none; }
    .sec-head {
      display: flex; align-items: center; gap: 6px;
      margin: 0 0 16px; font-size: 17px; font-weight: 700;
      color: #0f172a; letter-spacing: 0;
    }
    .sec-head .edit-btn { margin-left: auto; }
    /* 票123 R-3:公司情报的标题与公司名分列(中点清扫)。 */
    .sec-head .sec-sub { font-size: 14px; font-weight: 500; color: #475569; }
    /* ---- 2026-10-19:Simplify 像素级 Resume Score ---- */
    .score-tab { padding: 24px 28px; }
    /* 票 167：卡片不再自带 560px 内滚动——正文完整展开、滚动只留在抽屉
       (.detail-body) 一处；行交叉轴拉伸，JD 短于右侧列时卡片仍撑满行高、
       与整个抽屉模块高度一致。 */
    .score-cols { display: flex; gap: 24px; align-items: stretch; }
    .score-jd-card {
      flex: 1.2; min-width: 0;
      display: flex; flex-direction: column;
      border: 1px solid #e2e8f0; border-radius: 10px; overflow: hidden;
      background: #ffffff;
    }
    .score-jd-head {
      display: flex; align-items: center; gap: 8px;
      padding: 14px 20px; background: #f6f8fb;
      border-bottom: 1px solid #e2e8f0;
      font-size: 15px; font-weight: 700; color: #0f172a;
    }
    .score-jd-head mat-icon { font-size: 18px; width: 18px; height: 18px; color: #64748b; }
    .score-jd-body { padding: 20px; font-size: 13.5px; line-height: 1.65; color: #0f172a; flex: 1 1 auto; }
    .score-jd-body p { margin: 0 0 12px; }
    /* 票 139：Score 卡片内复用 Overview 的节结构——.jd-subhead/.jd-list 原只在
       .about-job 下有样式，卡片内须另给同值规则；字号沿卡片 13.5px 继承。 */
    .score-jd-body .jd-subhead { margin: 22px 0 10px; font-size: 14px; font-weight: 700; color: #1f2430; }
    .score-jd-body .jd-subhead:first-child { margin-top: 0; }
    .score-jd-body .jd-list { margin: 0 0 16px; padding-left: 20px; }
    .score-jd-body .jd-list li { margin: 0 0 8px; line-height: 1.6; }
    .score-jd-body .dim { color: #475569; }
    .hl-kw {
      color: #2563eb; background: #eff6ff; border-radius: 999px;
      padding: 1px 6px; font-weight: 600; white-space: nowrap;
    }
    .score-side { flex: 1; min-width: 0; max-width: 420px; display: flex; flex-direction: column; align-items: center; gap: 16px; }
    .gauge-wrap { position: relative; width: 190px; height: 190px; }
    .gauge { display: block; }
    .gauge-center {
      position: absolute; inset: 0;
      display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 6px;
    }
    .gauge-num { font-size: 52px; font-weight: 800; color: #0f172a; line-height: 1; }
    .gauge-pill {
      font-size: 12px; font-weight: 700; border-radius: 999px; padding: 4px 12px;
    }
    .gauge-pill.good { background: #f0fdf4; color: #16a34a; }
    .gauge-pill.fair { background: #fffbeb; color: #b06a00; }
    .gauge-pill.poor { background: #fef2f2; color: #dc2626; }
    .score-banner {
      display: flex; align-items: center; gap: 10px;
      width: 100%; border-radius: 10px; padding: 14px 18px;
      font-size: 14px; color: #0f172a;
    }
    .score-banner.ok { background: #f0fdf4; }
    .score-banner.warn { background: #fffbeb; }
    .score-banner mat-icon { font-size: 24px; width: 24px; height: 24px; flex-shrink: 0; }
    .score-banner.ok mat-icon { color: #16a34a; }
    .score-banner.warn mat-icon { color: #b06a00; }
    .kw-card {
      width: 100%; border: 1px solid #e2e8f0; border-radius: 10px;
      padding: 20px; background: #ffffff;
    }
    .kw-card-title { font-size: 15px; font-weight: 700; color: #0f172a; margin: 0 0 16px; }
    .kw-pills { display: flex; flex-wrap: wrap; gap: 8px; }
    .kw-pill {
      font-size: 13px; font-weight: 500; border-radius: 999px; padding: 6px 14px;
    }
    .kw-pill.miss { background: #fef2f2; color: #dc2626; }
    .kw-pill.hit { background: #f0fdf4; color: #16a34a; }
    .about-job-edit { display: flex; justify-content: flex-end; margin: 0 0 8px; }
    .about-job-edit .edit-btn { margin: 0; }
    /* 票 226：详情小节编辑（与 SmartAdd 编辑器同款全宽字段 + 标签样式）。 */
    .sec-edit .sa-sections-label { font-size: 13px; font-weight: 600; margin: 4px 0 8px; }
    .sec-edit .sec-title-field, .sec-edit .sec-body-field { width: 100%; }
    .sec-edit-item { margin-bottom: 8px; }
    .about-job .jd-subhead {
      margin: 22px 0 10px; font-size: 14px; font-weight: 700;
      color: #0f172a;
    }
    .about-job p { margin: 0 0 16px; line-height: 1.7; font-size: 14px; color: #0f172a; }
    .about-job .jd-list { margin: 0 0 16px; padding-left: 20px; }
    .about-job .jd-list li { margin: 0 0 8px; line-height: 1.6; font-size: 14px; color: #0f172a; }
    /* LinkedIn 风格:长描述默认折叠 */
    .about-job-body.collapsed { max-height: 320px; overflow: hidden; position: relative; }
    .about-job-body.collapsed::after {
      content: ''; position: absolute; left: 0; right: 0; bottom: 0; height: 60px;
      background: linear-gradient(to bottom, rgba(255,255,255,0), rgba(255,255,255,1));
      pointer-events: none;
    }
    .doc-filename {
      display: flex; align-items: center; gap: 8px;
      font-weight: 600; font-size: 14px; margin: 12px 0;
      color: #0f172a;
    }
    .doc-filename mat-icon { color: #dc2626; }
    .resume-text-view {
      white-space: pre-wrap; word-break: break-word;
      /* 票123 R-1.6:<pre> 的 UA 默认 font-family 是 monospace,
         这会让简历正文读数成等宽(与全站正文声音不符)。显式 inherit 回正文栈。 */
      font-family: inherit;
      font-size: 13px; line-height: 1.6; color: #0f172a;
      background: #f6f8fb; border: 1px solid #e2e8f0; border-radius: 8px;
      padding: 12px 14px; margin: 12px 0; max-height: 480px; overflow: auto;
    }
    .doc-pdf-viewer {
      width: 100%; height: 700px; border: 1px solid #e2e8f0;
      border-radius: 8px; background: #f6f8fb;
    }
    .more-link {
      background: none; border: none; padding: 4px 0; cursor: pointer;
      color: #475569; font-size: 14px; font-weight: 500;
    }
    .more-link:hover { color: #2563eb; text-decoration: underline; }
    .teal-link {
      color: #2563eb; font-weight: 500; text-decoration: none;
      display: inline-flex; align-items: center; gap: 6px;
    }
    .teal-link mat-icon { font-size: 14px; width: 14px; height: 14px; }
    .teal-link:hover { text-decoration: underline; }
    .import-jd-btn {
      margin-left: 8px; font-size: 13px; color: #2563eb;
      padding: 2px 8px; min-width: 0; line-height: 1.5;
    }
    .import-jd-btn mat-icon { font-size: 14px; width: 14px; height: 14px; margin-right: 4px; }
    .jd-body { margin-top: 24px; }
    .jd-body p { font-size: 14px; color: #0f172a; line-height: 1.57; margin: 0 0 14px; }
    .jd-body .dim { color: #64748b; }
    section h4 { margin: 0 0 8px; font-size: 13px; letter-spacing: 0.3px; opacity: 0.7; }
    .pre { margin: 0; font-size: 13.5px; line-height: 1.65; white-space: pre-wrap; word-break: break-word; }
    .history { margin: 0; padding-left: 18px; font-size: 13px; line-height: 1.9; }
    .history mat-icon {
      font-size: 14px; width: 14px; height: 14px;
      vertical-align: middle; opacity: 0.45;
    }
    .history time { margin-left: 8px; opacity: 0.55; font-size: 12px; }
    .history .note { display: block; opacity: 0.6; font-style: italic; font-size: 12.5px; }
    .rounds { margin: 0; padding-left: 18px; font-size: 13.5px; line-height: 1.8; }
    .tag {
      display: inline-block; margin-left: 6px; padding: 1px 7px;
      border-radius: 10px; background: rgba(15, 23, 42, 0.06); font-size: 11.5px;
    }
    .tag.outcome-pass { background: #f0fdf4; color: #16a34a; }
    .tag.outcome-fail { background: #fef2f2; color: #dc2626; }
    .link { font-size: 13px; word-break: break-all; color: #2563eb; }
    /* ---- 匹配分析 ---- */
    .match-head { display: flex; align-items: center; gap: 14px; margin-bottom: 14px; }
    .score {
      display: flex; align-items: baseline; gap: 2px;
      padding: 6px 14px; border-radius: 10px; font-weight: 600;
    }
    .score-num { font-size: 26px; line-height: 1; }
    .score-unit { font-size: 12px; }
    .score.good { background: #f0fdf4; color: #16a34a; }
    .score.fair { background: #fffbeb; color: #b06a00; }
    .score.poor { background: #fef2f2; color: #dc2626; }
    .score-note { display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; }
    .score-note strong.good { color: #16a34a; }
    .score-note strong.fair { color: #b06a00; }
    .score-note strong.poor { color: #dc2626; }
    .score-note span { opacity: 0.6; }
    .kw-group { margin-bottom: 12px; }
    .kw-title { display: block; font-size: 11.5px; opacity: 0.55; margin-bottom: 6px; }
    .kw-list { display: flex; flex-wrap: wrap; gap: 6px; }
    .kw {
      padding: 2px 9px; border-radius: 999px; font-size: 12px;
      border: 1px solid transparent;
    }
    .kw.miss { background: #fef2f2; color: #dc2626; border-color: #fef2f2; }
    .kw.ok { background: #f0fdf4; color: #16a34a; border-color: #f0fdf4; }
    .soft-note { margin: 8px 0 0; font-size: 12px; opacity: 0.55; line-height: 1.6; }
    /* ---- JD 全文折叠 ---- */
    .fold-head { cursor: default; }
    .fold-head > span:first-child { display: flex; align-items: center; gap: 6px; }
    .fold-head:hover { opacity: 1; }
    .fold-head mat-icon { font-size: 18px; width: 18px; height: 18px; color: rgba(15, 23, 42, 0.45); }
    .fold-len { font-size: 11.5px; opacity: 0.45; font-weight: 400; }
    /* Documents tab 简历区:右侧操作按钮 */
    .doc-actions { margin-left: auto; display: flex; gap: 4px; align-items: center; }
    /* Simplify:标题行右侧的小编辑图标 */
    .edit-btn { width: 26px; height: 26px; line-height: 26px; margin-left: auto; opacity: 0.45; }
    .edit-btn:hover { opacity: 1; }
    .edit-btn mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .jd-full { max-height: 340px; overflow-y: auto; padding-right: 6px; }
    .jd-peek { opacity: 0.65; }
    .src-link {
      display: inline-flex; align-items: center; gap: 4px;
      font-size: 12.5px; color: #2563eb; margin-bottom: 8px;
    }
    .src-link mat-icon { font-size: 15px; width: 15px; height: 15px; }
    /* ---- 求职信 ---- */
    .cl-status {
      margin-left: 6px; padding: 1px 8px; border-radius: 10px;
      font-size: 11px; font-weight: 500;
    }
    .cl-status.cl-draft { background: rgba(15, 23, 42, 0.07); }
    .cl-status.cl-generated { background: #eff6ff; color: #2563eb; }
    .cl-status.cl-final { background: #f0fdf4; color: #16a34a; }
    .cl-hint {
      display: flex; align-items: flex-start; gap: 6px;
      margin: 0 0 10px; padding: 9px 11px; border-radius: 8px;
      background: #fffbeb; font-size: 12.5px; line-height: 1.6;
    }
    .cl-hint.stale { background: #eff6ff; }
    .cl-hint mat-icon { font-size: 16px; width: 16px; height: 16px; flex: 0 0 16px; margin-top: 1px; }
    .cl-extra { width: 100%; margin-bottom: 4px; }
    .cl-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 4px; }
    .cl-actions.save-row { margin-top: 10px; }
    .cl-generating { margin-top: 8px; }
    .cl-generating p { margin: 10px 0 0; font-size: 12.5px; opacity: 0.65; }
    .cl-editor {
      width: 100%; margin-top: 10px; padding: 12px;
      font-family: inherit; font-size: 13.5px; line-height: 1.7;
      border: 1px solid rgba(15, 23, 42, 0.18); border-radius: 8px;
      resize: vertical; box-sizing: border-box;
    }
    .cl-preview {
      margin-top: 10px; padding: 14px 16px; border-radius: 8px;
      background: rgba(15, 23, 42, 0.028); font-size: 13.5px; line-height: 1.75;
      white-space: pre-wrap; word-break: break-word;
      max-height: 420px; overflow-y: auto;
    }
    .cl-empty { margin: 10px 0 0; font-size: 13px; opacity: 0.55; }
    /* ★ Simplify 对齐:黄色警告条 */
    .warn-banner {
      display: flex; align-items: flex-start; gap: 8px;
      margin: 0 0 12px; padding: 10px 12px; border-radius: 8px;
      background: #fffbeb; border: 1px solid #fffbeb;
      font-size: 12.5px; line-height: 1.6; color: #b06a00;
    }
    .warn-banner mat-icon { font-size: 16px; width: 16px; height: 16px; flex: 0 0 16px; margin-top: 1px; }
    /* ★ Simplify 对齐:朴素 textarea (非 mat-form-field) */
    .plain-area {
      width: 100%; box-sizing: border-box;
      padding: 10px 12px; font-family: inherit; font-size: 13.5px; line-height: 1.6;
      border: 1px solid rgba(15, 23, 42, 0.2); border-radius: 8px; resize: vertical;
      background: #ffffff; color: inherit;
    }
    /* 票123 C-2.2:原来 ":focus" 直接 "outline: none",键盘用户看不到焦点在哪。
       现在保留既有的 border-color 线索,并在 :focus-visible 上补回全站共享焦点环。 */
    .plain-area:focus { border-color: var(--brand); }
    .plain-area:focus-visible { outline: 2px solid var(--brand); outline-offset: 2px; }
    .plain-area::placeholder { color: rgba(15, 23, 42, 0.35); }
    .field-hint { margin: 6px 0 10px; font-size: 12px; color: rgba(15, 23, 42, 0.45); line-height: 1.5; }
    /* 票75:字段级校验提示(对齐票12 Knowledge 弹窗 .field-error 口径:红色 12px、贴字段下方) */
    .field-error { color: #dc2626; font-size: 12px; margin: -14px 0 10px 2px; }
    .field-error-plain { color: #dc2626; font-size: 12px; margin: 4px 0 8px 2px; }
    .gen-btn { margin: 2px 0 4px; }
    .gen-btn[disabled] { opacity: 0.5; }

    /* ---- 公司 Logo (Simplify:大圆角方块) ---- */
    .logo-wrap { position: relative; flex: 0 0 40px; width: 40px; height: 40px; }
    .logo-avatar {
      display: flex; align-items: center; justify-content: center;
      width: 40px; height: 40px; border-radius: 10px;
      color: #ffffff; font-size: 14px; font-weight: 700; letter-spacing: 0.3px;
      text-transform: uppercase; overflow: hidden;
    }
    .logo {
      position: absolute; inset: 0;
      width: 40px; height: 40px; border-radius: 10px; object-fit: contain;
      background: #ffffff;
    }
    .detail-logo { margin-right: 0; flex: 0 0 40px; }

    /* ---- 页签 (Simplify:极简,active 下划线) ---- */
    /* ★ Simplify 真站:5 tab */
    .tabs {
      display: flex; gap: 30px; margin: 0; padding: 0 32px;
      border-bottom: 1px solid #e2e8f0;
    }
    .tab {
      appearance: none; background: none; border: none; cursor: pointer;
      display: inline-flex; align-items: center; gap: 8px;
      padding: 14px 2px; font-size: 14px; color: #64748b;
      border-bottom: 3px solid transparent; margin-bottom: -1px;
    }
    .tab mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .tab:hover { color: #0f172a; }
    .tab.active { color: #0f172a; font-weight: 600; border-bottom-color: #0f172a; }
    .tab-placeholder {
      display: flex; flex-direction: column; align-items: center; gap: 12px;
      padding: 60px 32px; color: #64748b;
    }
    .tab-placeholder mat-icon { font-size: 48px; width: 48px; height: 48px; opacity: 0.4; }
    .tab-placeholder p { font-size: 16px; margin: 0; }

    /* ---- 沟通记录 ---- */
    .comms-head { margin-bottom: 10px; }
    .comms-empty { font-size: 13px; opacity: 0.55; margin: 4px 0 0; }
    .comm-form {
      padding: 12px; margin-bottom: 14px; border-radius: 10px;
      background: rgba(15, 23, 42, 0.025); border: 1px solid rgba(15, 23, 42, 0.08);
    }
    .comm-form mat-form-field { width: 100%; }
    .comm-form .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .comm-form .row2 mat-form-field { width: 100%; }
    .comm-form-actions { display: flex; gap: 8px; }
    .comm-list { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
    .comm-item {
      display: flex; gap: 10px; align-items: flex-start;
      padding: 10px 12px; border-radius: 10px; border: 1px solid rgba(15, 23, 42, 0.08);
    }
    .comm-type {
      flex: 0 0 auto; font-size: 11px; font-weight: 600; padding: 2px 9px; border-radius: 10px;
      background: rgba(15, 23, 42, 0.06);
    }
    .comm-type.ct-email { background: #eff6ff; color: #2563eb; }
    .comm-type.ct-call { background: #f0fdf4; color: #16a34a; }
    .comm-type.ct-interview { background: #fffbeb; color: #b06a00; }
    .comm-type.ct-message { background: #eff6ff; color: #2563eb; }
    .comm-type.ct-note { background: #e2e8f0; color: #475569; }
    .comm-body { flex: 1; min-width: 0; }
    .comm-subject { font-size: 13.5px; font-weight: 600; margin-bottom: 2px; }
    .comm-content { margin: 0; font-size: 13px; line-height: 1.6; white-space: pre-wrap; word-break: break-word; }
    .comm-meta { display: flex; flex-wrap: wrap; gap: 4px 10px; margin-top: 5px; font-size: 11.5px; opacity: 0.6; }
    .comm-actions { display: flex; flex-direction: column; gap: 2px; flex: 0 0 auto; }
    .comm-actions .danger { color: #dc2626; }

    /* ---- 自备问题 (Questions, G-07) ---- */
    /* ★ 票74:q-* 原在 tracker.component.scss,票52 拆分抽屉时漏迁 —— 封装作用域
       到不了抽屉 DOM(实测 .q-item 计算样式 display:block),长无空格文本因此无约束
       横向溢出。按 comm 先例迁回本组件,值逐字源自 tracker.component.scss;
       .q-text 另加换行约束(min-width:0 + overflow-wrap:anywhere +
       word-break:break-word,与本文件 .comm-content/.pre 口径一致)。 */
    .q-empty {
      text-align: center; padding: 32px 16px; color: rgba(15, 23, 42, 0.45);
    }
    .q-empty mat-icon { font-size: 40px; width: 40px; height: 40px; opacity: 0.4; }
    .q-list { display: flex; flex-direction: column; gap: 8px; margin-bottom: 16px; }
    .q-item {
      display: flex; align-items: flex-start; gap: 12px;
      padding: 12px 14px; border: 1px solid rgba(15, 23, 42, 0.08);
      border-radius: 8px; background: #ffffff;
    }
    .q-num {
      flex: 0 0 24px; width: 24px; height: 24px; border-radius: 50%;
      background: var(--signal-tint); color: var(--brand); font-size: 12px; font-weight: 600;
      display: flex; align-items: center; justify-content: center;
    }
    .q-text {
      flex: 1; min-width: 0; font-size: 14px; line-height: 1.5;
      overflow-wrap: anywhere; word-break: break-word;
    }
    .q-del { flex: 0 0 auto; opacity: 0.5; }
    .q-del:hover { opacity: 1; color: #dc2626; }
    .q-add-form { margin-bottom: 16px; }
    .q-add-form .full { width: 100%; }
    .q-add-actions { display: flex; gap: 8px; margin-top: 8px; }
    .q-add-btn { margin-top: 4px; }
  `]
})
export class ApplicationDetailDialogComponent implements OnInit, OnDestroy {
  readonly app = inject<Application>(MAT_DIALOG_DATA);
  private readonly jobsApi = inject(JobsApiService);
  private readonly trackerApi = inject(TrackerApi);
  private readonly prefs = inject(TrackerPreferencesService);
  private readonly i18n = inject(I18nService);
  private readonly dialog = inject(MatDialog);
  private readonly sanitizer = inject(DomSanitizer);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  tf = (key: string, params: Record<string, string | number | null | undefined>): string => this.i18n.tf(key, params);
  private readonly snack = inject(MatSnackBar);

  /**
   * 服务端全量详情(M1.5)。列表项不带轮次/历史/拒因 —— 之前弹窗直接渲染
   * 列表项,这些区块其实从未显示过。打开后拉一次详情替换数据源。
   */
  readonly full = signal<Application | null>(null);

  /** P0-2:详情加载失败标记 —— 显示"数据可能过期"警告,而不是静默用旧数据。 */
  readonly loadError = signal(false);

  /** 模板统一读这里:详情没回来前用列表项兜底。 */
  get detail(): Application { return this.full() ?? this.app; }

  /** 轮次结果中文标签(M1.5 回写后展示)。 */
  outcomeLabel(o: string): string {
    return this.i18n.t('outcome.' + o);
  }

  /**
   * 从 HttpErrorResponse 里抠出人话错误。
   * 后端的 ProblemDetails 把说明放在 description(我们的 Error 记录映射过去的)。
   */
  private readError(err: unknown): string {
    const e = err as { error?: { description?: string; detail?: string; title?: string }; message?: string };
    return e?.error?.description ?? e?.error?.detail ?? e?.error?.title
      ?? e?.message ?? this.t('tracker.opFailed');
  }

  /** 公司情报(懒加载 —— 详情弹窗打开后才查一次)。 */
  readonly company = signal<Company | null>(null);

  /** 票 150:单家 logo 刷新进行态。 */
  readonly refreshingLogo = signal(false);

  /** JD 全文是否展开(默认折叠:40000 字符铺开会淹没弹窗)。 */
  readonly jdExpanded = signal(false);

  /** 2026-10-19:About the job 是否展开(LinkedIn 风格:长描述默认折叠,点 more 展开)。 */
  readonly aboutJobExpanded = signal(false);

  /** Documents tab: 简历文件名和 PDF URL（null = 无文件） */
  readonly resumeFileName = signal<string | null>(null);
  readonly resumeFileUrl = signal<SafeResourceUrl | null>(null);
  readonly resumePdfRawUrl = signal<string | null>(null);

  /**
   * Documents tab: 有效简历来源。
   * tailored = 本投递的投递版本; base = 引用基础简历文件(不复制);
   * text = 主简历只有文本版(REQ-05 后端回落,无文件); none = 都没有。
   */
  readonly appResumeKind = signal<'base' | 'tailored' | 'text' | 'none'>('none');

  /**
   * 有效简历的提取文本(评分用)。来自后端 resume_files.TextContent ——
   * 有 tailored 时后端返回 tailored 的文本,Resume Score 按投递版本算;
   * kind=text 时为文本简历正文,Documents 直接展示全文(模板要读,故非 private)。
   */
  readonly appResumeText = signal<string | null>(null);

  /** 匹配分析结果。null = 还没算(JD 或简历缺失时保持 null)。 */
  readonly match = signal<MatchResult | null>(null);

  /** 简历全文(匹配分析的比对基准)。null = 尚未拿到。 */
  private readonly resumeText = signal<string | null>(null);

  /**
   * 本投递的有效简历:先拿元信息(kind/fileName/textContent),再拿 PDF 二进制做预览。
   * kind=tailored → 显示投递版本;kind=base → 引用基础简历(不复制);
   * kind=text → 主简历只有文本版:无 PDF 可取,直接展示 textContent 全文;
   * kind=none → 提示上传。
   */
  private loadAppResume(): void {
    this.jobsApi.getApplicationResume(this.app.id).subscribe({
      next: (r) => {
        const kind = r.kind === 'tailored' ? 'tailored'
          : r.kind === 'base' ? 'base'
          : r.kind === 'text' ? 'text' : 'none';
        this.appResumeKind.set(kind);
        this.resumeFileName.set(r.fileName ?? null);
        this.appResumeText.set(r.textContent ?? null);
        this.recomputeMatch();
        if (kind === 'tailored' || kind === 'base') {
          this.jobsApi.getApplicationResumeFile(this.app.id).subscribe({
            next: (blob) => {
              const url = URL.createObjectURL(blob);
              this.resumePdfRawUrl.set(url);
              this.resumeFileUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
            },
            error: () => { this.resumePdfRawUrl.set(null); this.resumeFileUrl.set(null); }
          });
        } else {
          this.resumePdfRawUrl.set(null);
          this.resumeFileUrl.set(null);
        }
      },
      error: () => { this.appResumeKind.set('none'); this.recomputeMatch(); }
    });
  }

  /** 上传本投递的 tailored 简历 PDF(文本前端 pdf.js 提取,一起发给后端)。 */
  async uploadTailoredResume(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!file.name.toLowerCase().endsWith('.pdf')) {
      this.snack.open(this.t('tracker.dResumeUploadFail'), this.t('common.close'), { duration: 3000 });
      return;
    }
    try {
      const text = await extractPdfText(file);
      const fd = new FormData();
      fd.append('file', file, file.name);
      fd.append('textContent', text);
      this.jobsApi.uploadApplicationResume(this.app.id, fd, this.app.version ?? null).subscribe({
        next: () => {
          this.snack.open(this.t('tracker.dResumeUploadOk'), this.t('common.close'), { duration: 2500 });
          this.loadAppResume();
        },
        error: () => this.snack.open(this.t('tracker.dResumeUploadFail'), this.t('common.close'), { duration: 3000 })
      });
    } catch {
      this.snack.open(this.t('tracker.dResumeUploadFail'), this.t('common.close'), { duration: 3000 });
    }
  }

  /** 删除 tailored 版本 —— 回落到基础简历(引用,不复制)。 */
  removeTailoredResume(): void {
    void this.confirmDlg(this.t('tracker.dResumeRemoveTailored'), true).then((ok) => {
      if (!ok) return;
      this.jobsApi.deleteApplicationResume(this.app.id).subscribe({
        next: () => this.loadAppResume(),
        error: () => this.snack.open(this.t('tracker.dResumeUploadFail'), this.t('common.close'), { duration: 3000 })
      });
    });
  }

  // ---------------------------- 求职信 ----------------------------

  /** 当前投递的求职信。null = 尚未生成(不是加载失败)。 */
  readonly cl = signal<CoverLetterDto | null>(null);

  /** 生成前的输入体检结果 —— 缺简历时按钮直接禁用,不让用户白等。 */
  readonly clReadiness = signal<CoverLetterReadinessDto | null>(null);

  /** FUN-07(票35):readiness 缺失码 → 当前语言提示(未知码不显示横幅)。 */
  missingText(rd: CoverLetterReadinessDto): string | null {
    const key = coverLetterMissingKey(rd.missingCode);
    return key ? this.t(key) : null;
  }

  /** 生成中。LLM 要几十秒,必须有明确态,否则用户会重复点。 */
  readonly clGenerating = signal(false);

  /** 是否在手动编辑态(与预览态切换)。 */
  readonly clEditing = signal(false);

  /** 额外要求(双向绑定到 textarea)。 */
  clExtra = '';

  /** 编辑中的草稿(保存前不落库 —— 用户取消就丢弃)。 */
  clDraft = '';

  constructor() {
    // 全量详情:列表项不带轮次/历史/拒因,打开后以服务端为准替换(M1.5)
    this.jobsApi.getApplication(this.app.id).subscribe({
      next: (a) => this.full.set(a),
      error: () => { this.loadError.set(true); /* P0-2:不再静默,模板显示过期警告 */ }
    });

    // 公司情报:Application 只带 companyId,要单独查
    if (this.app.companyId) {
      this.jobsApi.getCompany(this.app.companyId).subscribe({
        next: (c) => { this.company.set(c); },
        error: () => { /* 拿不到公司情报不阻断详情展示 */ }
      });
    }

    // 简历全文:从 Profile 服务取(与 AI 练习共用同一份)
    this.jobsApi.getResumeText().subscribe({
      next: (r) => {
        this.resumeText.set(r.resumeText);
        this.recomputeMatch();
      },
      error: () => { this.recomputeMatch(); }
    });

    // 本投递的有效简历(投递版本优先,没有回落基础):Documents tab 展示 + 评分基准
    this.loadAppResume();

    // 求职信 + 输入体检:详情弹窗打开时各查一次
    this.loadCoverLetter();
    this.loadReadiness();

    // 沟通记录:提前拉一次,这样概览页"沟通记录(N)"角标的数字才准
    this.loadCommunications();

    // 自备问题:读后端(含本地旧数据一次性导入)
    this.loadAppQuestions();

    // 收藏状态:与列表同一份信号(已加载则为空操作)
    this.prefs.ensureFavoritesLoaded();
  }

  // ---------------------------- 求职信方法 ----------------------------

  private loadCoverLetter(): void {
    this.jobsApi.getCoverLetter<CoverLetterDto | null>(this.app.id).subscribe({
      next: (c) => {
        this.cl.set(c);
        this.clDraft = c?.content ?? '';
      },
      error: () => { /* 读不到不阻断详情展示 */ }
    });
  }

  private loadReadiness(): void {
    this.jobsApi.getCoverLetterReadiness<CoverLetterReadinessDto>(this.app.id)
      .subscribe({
        next: (r) => this.clReadiness.set(r),
        error: () => { /* 体检失败不阻断 —— 生成时后端还会再校验一次 */ }
      });
  }

  /**
   * AI 生成求职信。
   *
   * ⚠️ 已有内容时必须确认覆盖 —— 后端默认 Overwrite=false 兜底,
   *    前端这一层确认是为了让用户明确知道"手改的内容会被冲掉"。
   */
  generateCoverLetter(): void {
    const hasContent = !!this.cl()?.content;
    void (async () => {
      if (hasContent && !(await this.confirmDlg(this.t('tracker.dClOverwrite'), false))) return;

      this.clGenerating.set(true);
      this.jobsApi.generateCoverLetter<CoverLetterDto>(this.app.id,
        { extraInstructions: this.clExtra.trim() || null, overwrite: hasContent }
      ).subscribe({
        next: (c) => {
          this.cl.set(c);
          this.clDraft = c.content;
          this.clEditing.set(false);
          this.clGenerating.set(false);
        },
        error: (err) => {
          this.clGenerating.set(false);
          // 后端的 ProblemDetails.description 已经是人话,直接透出
          this.alertError(this.readError(err));
        }
      });
    })();
  }

  saveCoverLetter(): void {
    const text = this.clDraft.trim();
    if (!text) { this.alertError(this.t('tracker.dClEmptyErr')); return; }

    this.jobsApi.saveCoverLetter<CoverLetterDto>(this.app.id, text, this.cl()?.version ?? null)
      .subscribe({
        next: (c) => { this.cl.set(c); this.clEditing.set(false); },
        error: (err) => this.alertError(this.readError(err))
      });
  }

  cancelEdit(): void {
    this.clDraft = this.cl()?.content ?? '';
    this.clEditing.set(false);
  }

  markCoverLetterFinal(): void {
    this.jobsApi.finalizeCoverLetter<CoverLetterDto>(this.app.id)
      .subscribe({
        next: (c) => this.cl.set(c),
        error: (err) => this.alertError(this.readError(err))
      });
  }

  deleteCoverLetter(): void {
    void this.confirmDlg(this.t('tracker.dClDelete'), true).then((ok) => {
      if (!ok) return;
      this.jobsApi.deleteCoverLetter(this.app.id)
        .subscribe({
          next: () => { this.cl.set(null); this.clDraft = ''; this.clEditing.set(false); },
          error: (err) => this.alertError(this.readError(err))
        });
    });
  }

  clStatusLabel(s: string): string {
    switch (s) {
      case 'Generated': return this.t('tracker.dClStatusGen');
      case 'Final': return this.t('tracker.dClStatusFinal');
      default: return this.t('tracker.dClStatusDraft');
    }
  }

  /** 是否有可用 JD（与 Overview / 高亮卡同一口径，供评分空态区分缺 JD 还是缺简历）。 */
  hasJdForScore(): boolean {
    return !!((this.detail.jdText ?? '').trim() || (this.detail.jdSummary ?? '').trim());
  }

  /** JD 全文到手或简历到手后重算 —— 两者缺一就不出分。 */
  private recomputeMatch(): void {
    // ★ F-08:JD 口径与 Overview 对齐 —— 读最新详情(detail)，JdText 缺失时回落
    //   JdSummary。旧实现只读列表项的 jdText，只有摘要的投递永远算不出分，
    //   与 Overview 里明明有 JD 自相矛盾。
    const jd = (this.detail.jdText ?? '').trim() || (this.detail.jdSummary ?? '').trim();
    // 投递版本优先:有 tailored 文本就用它,没有才用基础简历
    const resume = this.appResumeText() ?? this.resumeText();
    if (!jd || !resume) { this.match.set(null); return; }
    this.match.set(computeMatch(resume, jd));
  }

  statusLabel(s: string): string {
    return this.i18n.t('status.' + s);
  }

  statusClass(s: string): string {
    return `st-${(s || '').toLowerCase()}`;
  }

  /** 匹配分对应的颜色档(与 Simplify 一致:>=70 好,50-69 中,<50 差)。 */
  matchTier(score: number): string {
    if (score >= 70) return 'good';
    if (score >= 50) return 'fair';
    return 'poor';
  }

  matchTierLabel(score: number): string {
    if (score >= 70) return this.t('tracker.dStrongMatch');
    if (score >= 50) return this.t('tracker.dMedMatch');
    return this.t('tracker.dWeakMatch');
  }

  /** 2026-10-19:仪表盘圆弧长度(SVG dasharray)。r=80,周长≈502.65 */
  gaugeArc(score: number): string {
    const c = 2 * Math.PI * 80;
    const v = Math.max(0, Math.min(100, score)) / 100 * c;
    return `${v.toFixed(1)} ${c.toFixed(1)}`;
  }

  /** 2026-10-19:Resume Score 左侧 JD 卡片 —— 命中关键词高亮为 teal pill */
  /**
   * 票 139：单段文本的命中标记（先转义、单次遍历标记，不嵌套）。
   * 旧 highlightedJd 的两处缺陷一并修掉：(1) 逐词顺序替换会在已插入的
   * span 内再匹配短词（如 .NET 嵌进 .NET Core 的 span）；(2) 组件默认
   * Emulated 封装下 .hl-kw 规则不作用于 [innerHTML] 注入节点，标记不可见。
   * 本函数单次左→右遍历、交替子串永不重叠，故无嵌套、无标签内匹配；
   * span 同时带 class="hl-kw" 与 .hl-kw 同值内联样式——内联样式不受
   * 封装影响，浏览器内必可见（Angular 默认消毒保留这些安全声明）。
   * 标记口径：仅命中词的字面出现（大小写不敏感）；经别名才命中但显示
   * 文本无字面出现时无标记（现状口径，见定稿 A-4，不得为此发明新规则）。
   */
  highlightHtml(text: string, hit: string[]): string {
    const esc = (s: string): string =>
      s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    // 按长度降序：同一位置长词优先（如 .NET Core 优先于 .NET），去重、跳过单字
    const keys = [...new Set(hit)].filter(k => k.length >= 2)
      .sort((a, b) => b.length - a.length);
    if (keys.length === 0) return esc(text);
    const pattern = keys.map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|');
    const re = new RegExp(`(${pattern})`, 'gi');
    let out = '';
    let last = 0;
    let m: RegExpExecArray | null;
    while ((m = re.exec(text)) !== null) {
      out += esc(text.slice(last, m.index));
      out += `<span class="hl-kw" style="color:#1d4ed8;background:#eff6ff;border-radius:999px;padding:1px 6px;font-weight:600;white-space:nowrap">${esc(m[0])}</span>`;
      last = m.index + m[0].length;
    }
    out += esc(text.slice(last));
    return out;
  }

  // ---------------------------- 公司 Logo / 页签 ----------------------------

  /** 名称首字母头像(Logo 加载失败时的降级展示)。 */
  companyInitials(name?: string): string {
    const n = (name ?? '').trim();
    if (!n) return '?';
    const parts = n.split(/\s+/).filter(Boolean);
    if (parts.length >= 2) {
      return (parts[0][0] + parts[1][0]).toUpperCase();
    }
    return parts[0].slice(0, 2).toUpperCase();
  }

  /** 由名称哈希出稳定的彩色背景。 */
  avatarColor(name?: string): string {
    const n = (name ?? '?').trim() || '?';
    let h = 0;
    for (let i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) % 360;
    return `hsl(${h}, 52%, 52%)`;
  }

  /** Logo 加载失败时降级为首字母头像。 */
  readonly logoBroken = signal(false);

  /** 详情页当前页签。 */
  readonly detailTab = signal<'overview' | 'documents' | 'score' | 'email' | 'questions' | 'comms'>('overview');

  switchTab(tab: 'overview' | 'documents' | 'score' | 'email' | 'questions' | 'comms'): void {
    // comms 是旧名,统一映射到 email
    const t = tab === 'comms' ? 'email' : tab;
    this.detailTab.set(t as any);
    if (t === 'email') this.loadCommunications();
  }

  /* ★ Simplify 真站:头部操作按钮 */
  readonly dialogRef = inject(MatDialogRef<ApplicationDetailDialogComponent>);

  /* 票 147:抽屉左边缘拖拽改宽 + 窄屏规则(宽度口径见 detail-drawer-width.ts)。
     钳制只由拖拽逻辑执行:开启时即把 pane 的 min/maxWidth 内联约束清掉,
     否则 open 配置的 1280/900 会悄悄钳制 updateSize(定稿 §2.2 陷阱 a)。 */
  @ViewChild(PdfContentViewerComponent) private pdfViewer?: PdfContentViewerComponent;
  private readonly hostEl = inject(ElementRef<HTMLElement>);
  readonly narrowDrawer = signal(false);
  readonly draggingDrawer = signal(false);
  private drawerWidthPx: number | null = null;
  private dragStartX = 0;
  private dragStartWidth = 0;
  private drawerWasNarrow: boolean | null = null;
  private viewerRemeasureQueued = false;

  ngOnInit(): void {
    this.applyDrawerNarrowState();
  }

  ngOnDestroy(): void {
    // 对话框在拖动中被关闭(Esc 等)时 pointerup 不会来,须在此复原文本选中,
    // 否则 user-select: none 会残留到整个应用。
    if (this.draggingDrawer()) {
      this.draggingDrawer.set(false);
      document.body.style.userSelect = '';
    }
  }

  @HostListener('window:resize')
  onWindowResize(): void {
    this.applyDrawerNarrowState();
  }

  private paneEl(): HTMLElement | null {
    return this.hostEl.nativeElement.closest('.cdk-overlay-pane') as HTMLElement | null;
  }

  /** 窄屏(<1024px)取满宽并隐藏手柄;宽屏恢复记忆宽/66vw;跨阈值或宽屏内超范围时才改宽。 */
  private applyDrawerNarrowState(): void {
    const narrow = window.innerWidth < DRAWER_NARROW_BELOW;
    this.narrowDrawer.set(narrow);
    const crossed = this.drawerWasNarrow !== narrow;
    this.drawerWasNarrow = narrow;
    const pane = this.paneEl();
    if (pane) { pane.style.maxWidth = 'none'; pane.style.minWidth = '0px'; }
    if (narrow) {
      if (this.draggingDrawer()) {
        this.draggingDrawer.set(false);
        document.body.style.userSelect = '';
      }
      if (crossed) { this.dialogRef.updateSize('100vw'); this.drawerWidthPx = window.innerWidth; }
      return;
    }
    if (crossed) {
      const w = initialDrawerWidth(window.innerWidth);
      this.dialogRef.updateSize(w);
      this.drawerWidthPx = parseInt(w, 10);
      return;
    }
    // 宽屏内 resize:仅把当前宽钳制进新范围(不改记忆值,窗口恢复后仍可用原宽)。
    const { min, max } = drawerWidthRange(window.innerWidth);
    if (this.drawerWidthPx != null && (this.drawerWidthPx < min || this.drawerWidthPx > max)) {
      const w = Math.min(Math.max(this.drawerWidthPx, min), max);
      this.applyDrawerWidth(w);
    }
  }

  onDrawerResizeStart(e: PointerEvent): void {
    if (this.narrowDrawer()) return;
    const pane = this.paneEl();
    if (!pane) return;
    pane.style.maxWidth = 'none';
    pane.style.minWidth = '0px';
    if (this.drawerWidthPx == null) this.drawerWidthPx = Math.round(pane.getBoundingClientRect().width);
    this.dragStartX = e.clientX;
    this.dragStartWidth = this.drawerWidthPx;
    this.draggingDrawer.set(true);
    try { (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId); } catch { /* 捕获失败仍可拖动 */ }
    document.body.style.userSelect = 'none';
    e.preventDefault();
  }

  onDrawerResizeMove(e: PointerEvent): void {
    if (!this.draggingDrawer()) return;
    // 右贴边:左边缘左移(clientX 减小)=变宽。
    const { min, max } = drawerWidthRange(window.innerWidth);
    const w = Math.min(Math.max(this.dragStartWidth + (this.dragStartX - e.clientX), min), max);
    this.applyDrawerWidth(w);
    this.queueViewerRemeasure();
  }

  onDrawerResizeEnd(e: PointerEvent): void {
    if (!this.draggingDrawer()) return;
    this.draggingDrawer.set(false);
    document.body.style.userSelect = '';
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch { /* 已释放 */ }
    if (this.drawerWidthPx != null) saveDrawerWidth(this.drawerWidthPx);
    // 抽屉拖宽不触发 window resize,简历框高须显式重算(定稿 §2.2 陷阱 b)。
    this.pdfViewer?.remeasure();
  }

  onDrawerResizeKey(e: KeyboardEvent): void {
    if (this.narrowDrawer()) return;
    let delta = 0;
    if (e.key === 'ArrowLeft') delta = DRAWER_KEY_STEP;
    else if (e.key === 'ArrowRight') delta = -DRAWER_KEY_STEP;
    else return;
    e.preventDefault();
    const pane = this.paneEl();
    if (!pane) return;
    if (this.drawerWidthPx == null) this.drawerWidthPx = Math.round(pane.getBoundingClientRect().width);
    const { min, max } = drawerWidthRange(window.innerWidth);
    const w = Math.min(Math.max(this.drawerWidthPx + delta, min), max);
    this.applyDrawerWidth(w);
    saveDrawerWidth(w);
    this.pdfViewer?.remeasure();
  }

  private applyDrawerWidth(w: number): void {
    this.dialogRef.updateSize(Math.round(w) + 'px');
    this.drawerWidthPx = Math.round(w);
  }

  private queueViewerRemeasure(): void {
    if (this.viewerRemeasureQueued) return;
    this.viewerRemeasureQueued = true;
    requestAnimationFrame(() => { this.viewerRemeasureQueued = false; this.pdfViewer?.remeasure(); });
  }

  /** 票45 REQ-14:站内确认弹窗(全站 ConfirmDialog,F-02 标准)替代原生 confirm;
   *  true 才执行后续动作,取消/Esc/点遮罩一律不执行(与原生 confirm 的 false 同义)。 */
  private confirmDlg(body: string, danger: boolean): Promise<boolean> {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: danger ? this.t('common.delete') : this.t('common.confirmTitle'),
        body,
        confirmText: danger ? this.t('common.delete') : this.t('common.confirm'),
        cancelText: this.t('common.cancel'),
        danger,
      } as ConfirmDialogData,
    });
    return new Promise((resolve) => ref.afterClosed().subscribe((v) => resolve(v === true)));
  }

  /** 票45 REQ-14:错误提示替代原生 alert —— 走本类既有 snack 通道,文案逐字不变。 */
  private alertError(msg: string): void {
    this.snack.open(msg, this.t('common.close'), { duration: 3000 });
  }

  /** 票 150:单家触发 logo 刷新(LinkedIn 重提 → Website 派生)。
   *  成功取到新值时更新头部 logo 与公司记录;无来源时保持色块并提示一句,
   *  失败时同样只提示、不刷控制台、不改现有显示。 */
  refreshLogo(): void {
    if (!this.app.companyId || this.refreshingLogo()) return;
    this.refreshingLogo.set(true);
    this.jobsApi.refreshLogo(this.app.companyId).subscribe({
      next: (r) => {
        this.refreshingLogo.set(false);
        if (r.status === 'Updated' && r.logoUrl) {
          this.app.companyLogoUrl = r.logoUrl;
          this.logoBroken.set(false);
          const c = this.company();
          if (c) { this.company.set({ ...c, logoUrl: r.logoUrl }); }
          this.snack.open(this.t('tracker.logoRefreshed'), this.t('common.close'), { duration: 2500 });
        } else if (r.status === 'Cleaned') {
          this.app.companyLogoUrl = null;
          this.snack.open(this.t('tracker.logoCleaned'), this.t('common.close'), { duration: 2500 });
        } else {
          this.snack.open(this.t('tracker.logoNoSource'), this.t('common.close'), { duration: 3000 });
        }
      },
      error: (err) => {
        this.refreshingLogo.set(false);
        this.alertError(this.readError(err));
      },
    });
  }

  /** 收藏与列表共用同一路径(TrackerPreferencesService)—— 不再各读各的 localStorage。 */
  isFav(): boolean {
    return this.prefs.isFavorite(this.app.id);
  }

  toggleFav(): void {
    const adding = this.prefs.toggleFavorite(this.app.id);
    this.snack.open(this.t(adding ? 'tracker.favorited' : 'tracker.unfavorited'),
      this.t('common.close'), { duration: 2000 });
  }

  doArchive(): void {
    this.jobsApi.archive(this.app.id).subscribe({
      next: () => {
        this.snack.open(this.t('tracker.archived'), this.t('common.close'), { duration: 2000 });
        this.dialogRef.close(true);
      },
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  doDelete(): void {
    // ★ F-02:改用全站统一确认弹窗(原生 confirm 会被静默 dismiss);
    //   删除成功补一条可见反馈,不再关窗无声。
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.delete'), body: this.t('tracker.deleteConfirm'),
        confirmText: this.t('common.delete'), cancelText: this.t('common.cancel'),
        danger: true,
      } as ConfirmDialogData,
    });
    ref.afterClosed().subscribe((v) => {
      if (v !== true) return;
      this.jobsApi.deleteApplication(this.app.id, this.app.version ?? null).subscribe({
        next: () => {
          this.snack.open(this.t('tracker.deleted'), this.t('common.close'), { duration: 3000 });
          this.dialogRef.close(true);
        },
        error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
      });
    });
  }

  /* ★ Simplify 真站:状态时间线 — 固定 5 阶段,不含 Accepted/Rejected 等 */
  readonly statusOrder = ['Saved', 'Applied', 'Screen', 'Interview', 'Offer'];

  isStatusDone(s: string): boolean {
    const order = this.statusOrder;
    let currentIdx = order.indexOf(this.detail.status);
    if (currentIdx < 0) {
      // 当前状态不在 5 阶段内(如 Rejected/Accepted):按 history 取走到的最远阶段
      const hist = (this.detail as any).history as Array<{ status: string }> | undefined;
      currentIdx = -1;
      for (const h of hist ?? []) {
        const i = order.indexOf(h.status);
        if (i > currentIdx) currentIdx = i;
      }
    }
    const sIdx = order.indexOf(s);
    return sIdx >= 0 && sIdx <= currentIdx;
  }

  statusDate(s: string): string | null {
    // 从 history 里找该状态的时间
    const h = (this.detail as any).history;
    if (!h || !Array.isArray(h)) return null;
    const item = h.find((x: any) => x.status === s || x.to === s);
    if (!item) return null;
    const d = item.at || item.date || item.createdAt;
    if (!d) return null;
    try {
      const dt = new Date(d);
      return `${dt.getMonth() + 1}/${dt.getDate()}/${String(dt.getFullYear()).slice(2)}`;
    } catch { return null; }
  }

  quickChangeStatus(ev: Event): void {
    const select = ev.target as HTMLSelectElement;
    const newStatus = select.value;
    this.jobsApi.changeStatus(this.app.id, newStatus as ApplicationStatus).subscribe({
      next: () => {
        this.snack.open(this.t('tracker.statusUpdated'), this.t('common.close'), { duration: 2000 });
        this.reloadDetail();
      },
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  private reloadDetail(): void {
    this.jobsApi.getApplication(this.app.id).subscribe({
      next: (app: Application) => this.full.set(app),
      error: () => this.loadError.set(true)
    });
  }

  startEditPosting(): void {
    // TODO: 内联编辑 Job Posting 字段
    this.snack.open(this.t('common.comingSoon'), this.t('common.close'), { duration: 2000 });
  }

  // ---------------------------- 条目编辑(2026-10-08) ----------------------------
  // 详情弹窗之前是纯只读 —— Notes/JD 全文/JD 摘要都改不了。
  // 后端早就有 PUT /api/jobs/applications/{id} 和 PUT .../jd,前端只是没暴露。

  /** Notes 编辑态。 */
  readonly notesEditing = signal(false);
  notesDraft = '';
  readonly notesSaving = signal(false);

  /** JD 摘要编辑态。 */
  readonly jdSummaryEditing = signal(false);
  /** 2026-10-19:重新解析 JD（用最新后端重跑，修复旧数据）。 */
  readonly reparsing = signal(false);
  jdSummaryDraft = '';
  readonly jdSummarySaving = signal(false);

  /** 票142:公司介绍编辑态(沿用 JD 摘要编辑模式)。 */
  readonly companyDescEditing = signal(false);
  companyDescDraft = '';
  readonly companyDescSaving = signal(false);
  readonly companyDescError = signal<string | null>(null);

  /** 票142:公司情报编辑态 + 抓取态。 */
  readonly profileEditing = signal(false);
  profileDraft = '';
  readonly profileSaving = signal(false);
  readonly profileError = signal<string | null>(null);
  readonly profileFetching = signal(false);
  readonly profileFetchError = signal<string | null>(null);
  /** 本次编辑会话中抓取到的来源 URL(保存时记入 profileSourcesJson;手动编辑仍原值回传)。 */
  readonly fetchedProfileSource = signal<string | null>(null);

  /** JD 全文编辑态(走独立的 /jd 端点,不碰其他字段)。 */
  readonly jdTextEditing = signal(false);
  jdTextDraft = '';
  readonly jdTextSaving = signal(false);

  // ---------------------------- 面试问题 (G-07) ----------------------------

  /** 每个申请的自定义面试问题(COD-10a/票14:Jobs 后端为真源,按申请 id 隔离)。 */
  readonly appQuestions = signal<ApplicationQuestionDto[]>([]);
  questionDraft = '';
  readonly questionAdding = signal(false);

  // ---- 票75:字段级校验错误(后端上限为唯一口径,值与 Jobs 校验器逐一对齐) ----
  /** 自备问题 Text 上限(AddApplicationQuestionCommandValidator MaximumLength(4000))。 */
  readonly QUESTION_MAX = 4000;
  /** JD 长文本统一上限(JdTextLimits.MaxLength,Update/SetJdContent 校验器共用)。 */
  readonly JD_MAX = 200_000;
  /** 沟通记录上限(Create/UpdateCommunicationCommandValidator)。 */
  readonly COMM_CONTENT_MAX = 20_000;
  readonly COMM_SHORT_MAX = 300;

  readonly questionError = signal<string | null>(null);
  readonly jdSummaryError = signal<string | null>(null);
  readonly jdTextError = signal<string | null>(null);
  readonly commFieldErrors = signal<Partial<Record<'subject' | 'contactName' | 'contactEmail' | 'content', string>>>({});

  /** 超限文案:上限数字走 i18n tf 注入,三语在核心字典 validation.tooLong。 */
  private tooLongText(max: number): string {
    return this.tf('validation.tooLong', { max });
  }

  /**
   * 服务端字段错误 → 本地化字段文案(票12 applyServerError 同型机制,纯逻辑
   * 在 validation-errors.helpers)。该字段无服务端错误时返回 null,调用方
   * 回落原有通用处理,非校验失败的呈现不回退。
   */
  private mapFieldError(e: unknown, field: string, sentLength: number, max: number): string | null {
    const c = fieldErrorFromServer(e, field, { sentLength, max });
    if (!c) return null;
    if (c.kind === 'tooLong') return this.tooLongText(max);
    if (c.kind === 'required') return this.t('validation.required');
    return c.text;
  }

  clearCommError(field: 'subject' | 'contactName' | 'contactEmail' | 'content'): void {
    if (this.commFieldErrors()[field]) {
      const next = { ...this.commFieldErrors() };
      delete next[field];
      this.commFieldErrors.set(next);
    }
  }

  /** 打开弹窗时读后端(未导入过则先把本地旧问题一次性导入再读回,对账以读回条数为准)。 */
  private loadAppQuestions(): void {
    this.prefs.loadQuestions(this.app.id).subscribe({
      next: (qs) => this.appQuestions.set(qs),
      error: () => { /* 读不到不阻断详情展示(同求职信口径) */ }
    });
  }

  addQuestion(): void {
    const q = this.questionDraft.trim();
    if (!q) return;
    // 票75:提交前先挡超限(后端 MaximumLength(4000)),字段下直接亮含上限的本地化提示,不白跑 400
    if (textLengthError(q, this.QUESTION_MAX)) {
      this.questionError.set(this.tooLongText(this.QUESTION_MAX));
      return;
    }
    this.prefs.addQuestion(this.app.id, q).subscribe({
      next: () => {
        this.questionDraft = '';
        this.questionError.set(null);
        this.questionAdding.set(false);
        this.loadAppQuestions();
      },
      // 票75:后端 400 的 errors{Text} 映射回问题框下方;非字段级失败回落原通用 snack
      error: (e) => {
        const fieldText = this.mapFieldError(e, 'Text', q.length, this.QUESTION_MAX);
        if (fieldText) { this.questionError.set(fieldText); return; }
        this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 });
      }
    });
  }

  removeQuestion(questionId: string): void {
    this.prefs.deleteQuestion(this.app.id, questionId).subscribe({
      next: () => this.appQuestions.set(this.appQuestions().filter((x) => x.id !== questionId)),
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  startEditNotes(): void {
    this.notesDraft = this.detail.notes ?? '';
    this.notesEditing.set(true);
  }

  cancelEditNotes(): void {
    this.notesEditing.set(false);
  }

  saveNotes(): void {
    const d = this.detail;
    this.notesSaving.set(true);
    this.jobsApi.updateApplication(this.app.id, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      // FUN-02 附带:全量 PUT 下缺省即 null,会误清已有 workMode/source —— 从详情回填
      workMode: d.workMode ?? null, source: d.source ?? null,
      jdSummary: d.jdSummary,
      notes: this.notesDraft.trim() || null, priority: d.priority,
      deadline: d.deadline, appliedDate: d.appliedDate,
      version: d.version ?? null,
    }).subscribe({
      next: () => {
        this.notesSaving.set(false);
        this.notesEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.notesSaving.set(false);
        this.alertError(this.readError(err));
      }
    });
  }

  startEditJdSummary(): void {
    this.jdSummaryDraft = this.detail.jdSummary ?? '';
    this.jdSummaryEditing.set(true);
  }

  cancelEditJdSummary(): void {
    this.jdSummaryEditing.set(false);
    this.jdSummaryError.set(null);
  }

  /** 2026-10-19:用最新后端重新解析 JD，修复旧数据的噪音/去重问题。 */
  /**
   * 票 154：详情页可见小节。存量记录（sectionsJson null/非法）返回 []，模板回退 legacy；
   * 公司介绍类小节与 companyDescription 逐字重复时去重（首步定法 ①，纯渲染规则）。
   */
  get visibleSections(): JdSection[] {
    return visibleDetailSections(parseSectionsJson(this.detail.sectionsJson), this.detail.companyDescription);
  }

  /** 票189：用户令去掉的三处（先隐藏不删除，恢复只改开关一处；数据、保存逻辑、i18n 键一律保留）。 */
  readonly hideCompanyIntel = true;
  readonly hideJobLink = true;
  readonly hideJdTools = true;

  /**
   * 票189：展示用小节 = 可见小节再去掉与 JD 摘要（票 152 原文摘录）重复的前导内容。
   * Overview 与 Score 两处同用本 getter（票 139 逐节一致口径不变）。
   */
  get displaySections(): JdSection[] {
    return dedupSectionsAgainstSummary(this.jdSummaryDisplay(), this.visibleSections);
  }

  reparseJd(): void {
    const d = this.detail;
    const raw = d.jdText?.trim();
    if (!raw) {
      // 原文为空时无法重解析，提示用户
      this.snack.open(this.t('tracker.dNoJdText'), this.t('common.close'), { duration: 3000 });
      return;
    }
    this.reparsing.set(true);
    this.trackerApi.parseJd(raw, false).subscribe({
      next: (p: any) => {
        const upd: any = { version: this.detail.version ?? null };
        if (p.company) upd.company = p.company;
        if (p.role) upd.role = p.role;
        if (p.location) upd.location = p.location;
        if (p.salary) upd.salary = p.salary;
        if (p.workMode) upd.workMode = p.workMode;
        if (p.jobType) upd.jobType = p.jobType;
        if (p.jdSummary) upd.jdSummary = p.jdSummary;
        // 票 154 R-154-7：新解析带小节时只发合并后的 sectionsJson，不再发 requirements/benefits
        // （后端改从合并列表投影，保证二者一致）；新解析无小节时走今日 legacy 路径。
        if (p.sections && p.sections.length > 0) {
          const stored = parseSectionsJson(this.detail.sectionsJson) ?? [];
          upd.sectionsJson = JSON.stringify(mergeSectionsForReparse(stored, p.sections));
        } else {
          if (p.requirements) upd.requirements = p.requirements;
          if (p.benefits) upd.benefits = p.benefits;
        }
        if (p.companyDescription) upd.companyDescription = p.companyDescription;
        this.jobsApi.updateApplication(this.app.id, upd).subscribe({
          next: () => {
            this.reparsing.set(false);
            this.reloadDetail();
          },
          error: () => this.reparsing.set(false)
        });
      },
      error: () => this.reparsing.set(false)
    });
  }

  /**
   * 文本自动分段(2026-10-19):详情页 JD 摘要/公司介绍/任职要求/福利待遇展示用。
   * 1) 优先按空行分段;2) 无空行时按句子边界切成适读长度的段落。
   * 返回段落数组,模板用 @for 渲染成 <p>。
   */
  paras(text: string | undefined | null): string[] {
    if (!text) return [];
    const t = text.trim();
    if (!t) return [];
    // 先按空行(一个或多个)分段
    let parts = t.split(/\n\s*\n/).map(s => s.trim()).filter(s => s.length > 0);
    if (parts.length <= 1) {
      // 无空行:按单换行分段
      parts = t.split(/\n/).map(s => s.trim()).filter(s => s.length > 0);
    }
    if (parts.length <= 1) {
      // 仍是整块:按句子边界切,每段约 2-4 句,避免一屏大段文字
      const sentences = t.match(/[^.!?。！？]+[.!?。！？]+[""』）)]?/g) ?? [t];
      const out: string[] = [];
      let cur = '';
      for (const s of sentences) {
        const cand = cur ? cur + ' ' + s.trim() : s.trim();
        if (cand.length > 420 && cur) { out.push(cur); cur = s.trim(); }
        else cur = cand;
      }
      if (cur) out.push(cur);
      return out.length > 0 ? out : [t];
    }
    return parts;
  }

  /** 2026-10-19:详情页显示用 JD 摘要 —— 去掉已单独成节的 Qualifications / Additional Information，避免重复 */
  jdSummaryDisplay(): string {
    const d = this.detail;
    if (!d) return '';
    let t = d.jdSummary || '';
    if (!t.trim()) return t;
    // 2026-10-19:如果摘要与 requirements 实质相同(后端提取偏差),直接返回空,避免重复显示
    const req = (d.requirements || '').trim();
    if (req) {
      const reqNorm = req.replace(/[•·▪–—\-*\s]+/g, ' ').trim().toLowerCase();
      const tNorm = t.replace(/[•·▪–—\-*\s]+/g, ' ').trim().toLowerCase();
      // 摘要包含 requirements 的核心内容(前 80 字符) → 视为重复,不显示
      if (tNorm === reqNorm || tNorm.includes(reqNorm.slice(0, 80)) || reqNorm.includes(tNorm.slice(0, 80))) {
        return '';
      }
    }
    // 有单独的 requirements/benefits 节时，从摘要里切掉对应原文段
    // 注意:文本可能没有换行(大段 blob)，不用 ^ 行首锚定
    const cutFrom = (src: string, markers: string[]): string => {
      let idx = -1;
      for (const m of markers) {
        const i = src.search(new RegExp('\\b' + m + '\\b', 'i'));
        if (i >= 0 && (idx < 0 || i < idx)) idx = i;
      }
      return idx >= 0 ? src.slice(0, idx).trim() : src;
    };
    if (d.requirements?.trim()) {
      t = cutFrom(t, ['Qualifications', 'Requirements', '任职要求', '任职资格']);
    }
    if (d.benefits?.trim()) {
      t = cutFrom(t, ['Additional Information', 'Benefits', 'Compensation', '福利', '待遇']);
    }
    // 去掉原文里的 "Job Description" 小节标题(我们自己会渲染一个)
    t = t.replace(/\bJob Description\b/i, '').trim();
    return t.trim();
  }

  /** 2026-10-19:LinkedIn 风格富文本分块 —— 段落与列表分开,列表渲染为 <ul> */
  richBlocks(text: string | undefined | null): { kind: 'p' | 'ul'; items: string[] }[] {    if (!text) return [];
    const t0 = text.trim();
    if (!t0) return [];
    // 无换行的大段文本:按句子切分,避免一屏文字墙
    // 注意:保护 .NET / ASP.NET / C# 等技术缩写不被句号切断
    if (!t0.includes('\n') && t0.length > 600) {
      const PROT = '\u0000';
      let work = t0
        .replace(/\.NET\b/gi, PROT + 'DOTNET')
        .replace(/ASP\.NET\b/gi, 'ASP' + PROT + 'DOTNET')
        .replace(/\bC#/g, 'C' + PROT + 'SHARP')
        .replace(/\be\.g\./gi, 'EG' + PROT)
        .replace(/\bi\.e\./gi, 'IE' + PROT);
      const sentences = work.match(/[^.!?。！？]+[.!?。！？]+[""』）)]?/g) ?? [work];
      const restore = (s: string) => s
        .replace(new RegExp(PROT + 'DOTNET', 'g'), '.NET')
        .replace(new RegExp('ASP' + PROT + 'DOTNET', 'g'), 'ASP.NET')
        .replace(new RegExp('C' + PROT + 'SHARP', 'g'), 'C#')
        .replace(new RegExp('EG' + PROT, 'g'), 'e.g.')
        .replace(new RegExp('IE' + PROT, 'g'), 'i.e.');
      const out2: { kind: 'p' | 'ul'; items: string[] }[] = [];
      let cur2 = '';
      for (const s of sentences) {
        const rs = restore(s.trim());
        const cand = cur2 ? cur2 + ' ' + rs : rs;
        if (cand.length > 420 && cur2) { out2.push({ kind: 'p', items: [cur2] }); cur2 = rs; }
        else cur2 = cand;
      }
      if (cur2) out2.push({ kind: 'p', items: [cur2] });
      return out2;
    }
    const lines = text.split(/\n/).map(s => s.trim()).filter(s => s.length > 0);
    if (lines.length === 0) return [];
    const out: { kind: 'p' | 'ul'; items: string[] }[] = [];
    let curUl: string[] | null = null;
    const flushUl = () => { if (curUl && curUl.length > 0) out.push({ kind: 'ul', items: curUl }); curUl = null; };
    // 票189：连续单句行成列表。原帖 <ul><li> 经纯文本粘贴丢 bullet 后只剩换行短行，
    // 旧逻辑把它们当散文段落合并成一团；现连续 ≥2 行单句行（isListPhraseLine）即渲染
    // 为 <ul>，恢复原帖列表结构；单行不成列表时降级为段落，仍走后续合并（硬换行恢复）。
    // 无换行大段（>600 字 blob）无行结构可恢复，走上方句子切分，不动。
    let curList: string[] | null = null;
    const flushList = () => {
      if (!curList) return;
      if (curList.length >= 2) out.push({ kind: 'ul', items: curList });
      else out.push({ kind: 'p', items: [curList[0]] });
      curList = null;
    };
    for (const ln of lines) {
      // bullet: • · - * 或数字编号开头
      const m = ln.match(/^[•·▪–—\-*]\s*(.+)$/) || ln.match(/^\d+[.)、]\s*(.+)$/);
      if (m) {
        flushList();
        if (!curUl) curUl = [];
        curUl.push(m[1].trim());
      } else if (isListPhraseLine(ln)) {
        flushUl();
        if (!curList) curList = [];
        curList.push(ln);
      } else {
        flushUl();
        flushList();
        out.push({ kind: 'p', items: [ln] });
      }
    }
    flushUl();
    flushList();
    // 合并连续的单句段落,避免碎片化(LinkedIn 风格:长描述为段落)
    const merged: { kind: 'p' | 'ul'; items: string[] }[] = [];
    for (const b of out) {
      const last = merged[merged.length - 1];
      if (b.kind === 'p' && last && last.kind === 'p' && (last.items[0].length + b.items[0].length) < 420) {
        last.items[0] = last.items[0] + ' ' + b.items[0];
      } else {
        merged.push(b);
      }
    }
    return merged;
  }

  saveJdSummary(): void {
    const d = this.detail;
    // 票75:超限先挡(JdTextLimits.MaxLength),字段下亮含上限提示,不走通用弹框
    const sent = this.jdSummaryDraft.trim();
    if (textLengthError(sent, this.JD_MAX)) {
      this.jdSummaryError.set(this.tooLongText(this.JD_MAX));
      return;
    }
    this.jdSummarySaving.set(true);
    this.jobsApi.updateApplication(this.app.id, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      // FUN-02 附带:同 saveNotes,回填表单外的 workMode/source 防全量 PUT 误清
      workMode: d.workMode ?? null, source: d.source ?? null,
      jdSummary: this.jdSummaryDraft.trim() || null, notes: d.notes,
      priority: d.priority, deadline: d.deadline, appliedDate: d.appliedDate,
      version: d.version ?? null,
    }).subscribe({
      next: () => {
        this.jdSummarySaving.set(false);
        this.jdSummaryEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.jdSummarySaving.set(false);
        // 票75:errors{JdSummary} 落摘要框下方;其余失败回落原通用提示
        const fieldText = this.mapFieldError(err, 'JdSummary', sent.length, this.JD_MAX);
        if (fieldText) { this.jdSummaryError.set(fieldText); return; }
        this.alertError(this.readError(err));
      }
    });
  }

  /** 票 226：详情小节编辑（与 SmartAdd 编辑器同规则，见 prepareSectionsSave）。 */
  readonly sectionsEditing = signal(false);
  readonly sectionsSaving = signal(false);
  readonly sectionsError = signal<string | null>(null);
  sectionDrafts: (SectionEditDraft & { id: number })[] = [];
  private nextSectionDraftId = 1;

  startEditSections(): void {
    // 草稿必须来自存量 sectionsJson 原值：displaySections 经过摘要去重与公司介绍
    // 去重，拿它当草稿保存会把展示层滤掉的节从库里删掉。
    const stored = parseSectionsJson(this.detail.sectionsJson) ?? [];
    this.nextSectionDraftId = 1;
    this.sectionDrafts = stored.map((s) => ({
      id: this.nextSectionDraftId++,
      title: s.title, body: s.body, origin: s.origin,
      snapTitle: s.title, snapBody: s.body,
    }));
    this.sectionsError.set(null);
    this.sectionsEditing.set(true);
  }

  addSectionDraft(): void {
    this.sectionDrafts.push({
      id: this.nextSectionDraftId++,
      title: '', body: '', origin: 'manual', snapTitle: '', snapBody: '',
    });
  }

  removeSectionDraft(id: number): void {
    this.sectionDrafts = this.sectionDrafts.filter((d) => d.id !== id);
  }

  cancelEditSections(): void {
    this.sectionsEditing.set(false);
    this.sectionsError.set(null);
  }

  saveSections(): void {
    const r = prepareSectionsSave(this.sectionDrafts);
    if (r.error === 'tooLong') {
      this.sectionsError.set(this.t('tracker.saSectionTitleTooLong'));
      return;
    }
    if (r.error === 'titleRequired') {
      this.sectionsError.set(this.t('tracker.saSectionTitleRequired'));
      return;
    }
    // 后端语义：SectionsJson null = 未提供、保持原值；非 null = 明确新值。
    // 用户删空全部节时发 '[]'（后端 Canonicalize 空列表归一为 null = 清空 + 投影清零），
    // 而非发 null（那会保持旧节，违背删空意图）；本来就无存量节时发 null（无-op）。
    const hadStored = (parseSectionsJson(this.detail.sectionsJson) ?? []).length > 0;
    const sectionsJson = r.json ?? (hadStored ? '[]' : null);
    const d = this.detail;
    this.sectionsSaving.set(true);
    this.jobsApi.updateApplication(this.app.id, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      // 全量 PUT 回填（同 saveJdSummary）：表单外字段不回填会被误清。
      workMode: d.workMode ?? null, source: d.source ?? null,
      jdSummary: d.jdSummary ?? null, notes: d.notes,
      priority: d.priority, deadline: d.deadline, appliedDate: d.appliedDate,
      version: d.version ?? null,
      sectionsJson,
    }).subscribe({
      next: () => {
        this.sectionsSaving.set(false);
        this.sectionsEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.sectionsSaving.set(false);
        this.alertError(this.readError(err));
      }
    });
  }

  startEditJdText(): void {
    this.jdTextDraft = this.detail.jdText ?? '';
    this.jdTextEditing.set(true);
    // 编辑时自动展开全文
    this.jdExpanded.set(true);
  }

  // ---------------------------- 票142:公司两节编辑 + 抓取 ----------------------------

  startEditCompanyDesc(): void {
    this.companyDescDraft = this.detail.companyDescription ?? '';
    this.companyDescError.set(null);
    this.companyDescEditing.set(true);
  }

  cancelEditCompanyDesc(): void {
    this.companyDescEditing.set(false);
    this.companyDescError.set(null);
  }

  saveCompanyDesc(): void {
    const d = this.detail;
    // 票75 口径:超限先挡,字段下亮含上限提示,不走通用弹框
    const sent = this.companyDescDraft.trim();
    if (textLengthError(sent, this.JD_MAX)) {
      this.companyDescError.set(this.tooLongText(this.JD_MAX));
      return;
    }
    this.companyDescSaving.set(true);
    // 注意:R-142-2“只传 companyDescription + version”字面不可行——PUT
    // applications/{id} 的 Role 必填且其余字段无条件覆盖(见 JobAggregates
    // .UpdateDetails;唯 companyDescription 等有 null-keep 语义)。故按本对话框
    // 既有 saveJdSummary 模式回填现值,仅 companyDescription 变化,无其他字段被改写。
    this.jobsApi.updateApplication(this.app.id, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      // FUN-02 附带:同 saveNotes,回填表单外的 workMode/source 防全量 PUT 误清
      workMode: d.workMode ?? null, source: d.source ?? null,
      jdSummary: d.jdSummary, notes: d.notes,
      priority: d.priority, deadline: d.deadline, appliedDate: d.appliedDate,
      companyDescription: sent || null,
      version: d.version ?? null,
    }).subscribe({
      next: () => {
        this.companyDescSaving.set(false);
        this.companyDescEditing.set(false);
        this.reloadDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.companyDescSaving.set(false);
        this.alertError(this.readError(err));
      }
    });
  }

  startEditProfile(): void {
    const c = this.company();
    if (!c) return;
    this.profileDraft = c.profile ?? '';
    this.fetchedProfileSource.set(null);
    this.profileError.set(null);
    this.profileFetchError.set(null);
    this.profileEditing.set(true);
  }

  cancelEditProfile(): void {
    this.profileEditing.set(false);
    this.profileError.set(null);
    this.profileFetchError.set(null);
    this.fetchedProfileSource.set(null);
  }

  saveProfile(): void {
    const c = this.company();
    if (!c) return;
    const sent = this.profileDraft.trim();
    this.profileSaving.set(true);
    // R-142-3:手动编辑原值回传;经抓取填入后保存记 [sourceUrl];清空文本时来源一并清空。
    const sources = sent
      ? (this.fetchedProfileSource() ? JSON.stringify([this.fetchedProfileSource()]) : (c.profileSourcesJson ?? null))
      : null;
    this.jobsApi.setCompanyProfile(c.id, {
      profile: sent || null, profileSourcesJson: sources, version: c.version ?? null,
    }).subscribe({
      next: () => {
        this.profileSaving.set(false);
        this.profileEditing.set(false);
        this.fetchedProfileSource.set(null);
        this.reloadCompany();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.profileSaving.set(false);
        // A-5:失败时编辑态不关闭、草稿保留,走现有通用提示
        this.alertError(this.readError(err));
      }
    });
  }

  /** 刷新公司信号(情报保存成功后;与 reloadDetail 的投递刷新并列)。 */
  private reloadCompany(): void {
    const id = this.app.companyId;
    if (!id) return;
    this.jobsApi.getCompany(id).subscribe({
      next: (c) => this.company.set(c),
      error: () => { /* 刷新失败不阻断展示,旧值仍在 */ }
    });
  }

  /** 情报来源 URL 列表:profileSourcesJson 为 JSON 数组字符串;非 JSON 的单条 URL 容错为单元素。 */
  profileSources(): string[] {
    const raw = this.company()?.profileSourcesJson;
    if (!raw) return [];
    try {
      const parsed: unknown = JSON.parse(raw);
      if (Array.isArray(parsed)) return parsed.filter((x): x is string => typeof x === 'string' && !!x);
      if (typeof parsed === 'string' && parsed) return [parsed];
    } catch { /* 非 JSON 时走下方的 URL 容错 */ }
    return raw.startsWith('http') ? [raw] : [];
  }

  /**
   * 票142 R-142-11:抓取公司简介。
   * 成功 → 候选文本进编辑草稿(附来源链接),进入编辑态;未保存前不写库,刷新仍为空态。
   * 未找到 → 明确失败提示(写明试过哪几腿),字段保持空态、手动添加仍可用。
   * 已有 profile 时重新抓取 → 结果只进草稿,已保存文本在确认前一字不动。
   */
  fetchCompanyProfile(): void {
    const c = this.company();
    if (!c || this.profileFetching()) return;
    this.profileFetching.set(true);
    this.profileFetchError.set(null);
    this.jobsApi.fetchCompanyProfile(c.id).subscribe({
      next: (r) => {
        this.profileFetching.set(false);
        if (r.candidateText) {
          this.profileDraft = r.candidateText;
          this.fetchedProfileSource.set(r.sourceUrl ?? null);
          this.profileEditing.set(true);
        } else {
          const legs = (r.tried ?? []).map((leg) =>
            leg === 'website' ? this.t('tracker.dFetchLegWebsite') : this.t('tracker.dFetchLegWikipedia'));
          this.profileFetchError.set(
            `${this.t('tracker.dFetchNotFound')}（${this.t('tracker.dFetchTried')}：${legs.join('、') || '—'}）`);
        }
      },
      error: (err) => {
        this.profileFetching.set(false);
        this.profileFetchError.set(this.readError(err));
      }
    });
  }

  /** 1-click JD 导入:打开编辑 + 滚动到 Full JD 区域。 */
  scrollToJd(): void {
    setTimeout(() => {
      const el = document.querySelector('.jd-full-section');
      el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, 100);
  }

  cancelEditJdText(): void {
    this.jdTextEditing.set(false);
  }

  saveJdText(): void {
    // 票75:同 saveJdSummary 口径(该编辑入口现行模板未渲染,逻辑先对齐,防入口恢复后回潮)
    const sent = this.jdTextDraft.trim();
    if (textLengthError(sent, this.JD_MAX)) {
      this.jdTextError.set(this.tooLongText(this.JD_MAX));
      return;
    }
    this.jdTextSaving.set(true);
    this.jobsApi.saveApplicationJd(this.app.id, {
      jdText: this.jdTextDraft.trim() || null,
      jdSourceUrl: this.detail.jdSourceUrl ?? null,
      version: this.detail.version ?? null,
    }).subscribe({
      next: () => {
        this.jdTextSaving.set(false);
        this.jdTextEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.jdTextSaving.set(false);
        const fieldText = this.mapFieldError(err, 'JdText', sent.length, this.JD_MAX);
        if (fieldText) { this.jdTextError.set(fieldText); return; }
        this.alertError(this.readError(err));
      }
    });
  }

  /** 保存后重新拉详情,保证弹窗里显示的是最新数据。 */
  private refreshDetail(): void {
    this.jobsApi.getApplication(this.app.id).subscribe({
      next: (a) => this.full.set(a),
      error: () => { this.loadError.set(true); }
    });
  }

  // ---------------------------- 沟通记录 ----------------------------

  readonly comms = signal<Communication[]>([]);
  readonly commsLoading = signal(false);
  readonly commEditingId = signal<string | null>(null);
  /** 票105:新增态独立标记(同 questionAdding 先例)。新增时 commEditingId 恒为 null,
   * 旧模板只看 commEditingId 渲染表单,新增入口点了没反应;模式判定走 comm-form.helpers。 */
  readonly commAdding = signal(false);
  readonly commMode = computed(() => commFormMode(this.commEditingId(), this.commAdding()));
  readonly commTypes: CommunicationType[] = ['Email', 'Call', 'Interview', 'Message', 'Note'];

  /** 编辑中的草稿(新增态时 commEditingId 为 null)。 */
  readonly commDraft = signal<{
    type: CommunicationType; subject: string; content: string;
    contactName: string; contactEmail: string; occurredAt: string;
  }>({
    type: 'Email', subject: '', content: '', contactName: '', contactEmail: '', occurredAt: ''
  });

  private blankDraft(): {
    type: CommunicationType; subject: string; content: string;
    contactName: string; contactEmail: string; occurredAt: string;
  } {
    return {
      type: 'Email', subject: '', content: '', contactName: '', contactEmail: '',
      occurredAt: this.nowLocalInput()
    };
  }

  private loadCommunications(): void {
    this.commsLoading.set(true);
    this.trackerApi.listCommunications(this.app.id).subscribe({
      next: (list) => { this.comms.set(list); this.commsLoading.set(false); },
      error: () => { this.commsLoading.set(false); }
    });
  }

  commTypeLabel(t: CommunicationType): string {
    switch (t) {
      case 'Email': return this.t('tracker.commsTypeEmail');
      case 'Call': return this.t('tracker.commsTypeCall');
      case 'Interview': return this.t('tracker.commsTypeInterview');
      case 'Message': return this.t('tracker.commsTypeMessage');
      case 'Note': return this.t('tracker.commsTypeNote');
      default: return this.t('tracker.commsUnknown');
    }
  }

  startAddComm(): void {
    this.commEditingId.set(null);
    this.commAdding.set(true);
    this.commFieldErrors.set({});
    this.commDraft.set(this.blankDraft());
  }

  startEditComm(c: Communication): void {
    this.commAdding.set(false);
    this.commEditingId.set(c.id);
    this.commFieldErrors.set({});
    this.commDraft.set({
      type: c.type,
      subject: c.subject ?? '',
      content: c.content,
      contactName: c.contactName ?? '',
      contactEmail: c.contactEmail ?? '',
      occurredAt: this.toLocalInput(c.occurredAt)
    });
  }

  cancelComm(): void {
    this.commEditingId.set(null);
    this.commAdding.set(false);
    this.commFieldErrors.set({});
  }

  saveComm(): void {
    const d = this.commDraft();
    if (!d.content.trim()) return;
    const body = {
      type: d.type,
      subject: d.subject.trim() || null,
      content: d.content.trim(),
      contactName: d.contactName.trim() || null,
      contactEmail: d.contactEmail.trim() || null,
      occurredAt: this.fromLocalInput(d.occurredAt)
    };
    // 票75:四字段长度先挡(后端 Create/UpdateCommunication 校验器),超限字段下亮含上限提示
    const limits = {
      subject: this.COMM_SHORT_MAX, contactName: this.COMM_SHORT_MAX,
      contactEmail: this.COMM_SHORT_MAX, content: this.COMM_CONTENT_MAX
    } as const;
    const preErrors: Partial<Record<keyof typeof limits, string>> = {};
    for (const f of ['subject', 'contactName', 'contactEmail', 'content'] as const) {
      if (textLengthError(body[f], limits[f])) preErrors[f] = this.tooLongText(limits[f]);
    }
    if (Object.keys(preErrors).length > 0) { this.commFieldErrors.set(preErrors); return; }
    this.commFieldErrors.set({});
    const editing = this.commEditingId();
    const req: Observable<unknown> = commSaveChannel(editing) === 'update' && editing
      ? this.trackerApi.updateCommunication(this.app.id, editing,
          { ...body, version: this.comms().find(c => c.id === editing)?.version ?? null })
      : this.trackerApi.createCommunication(this.app.id, body);
    req.subscribe({
      next: () => {
        this.commEditingId.set(null);
        this.commAdding.set(false);
        this.loadCommunications();
        this.snack.open(this.t('tracker.commsSaved'), this.t('common.close'), { duration: 2500 });
      },
      // 票75:errors{Subject/Content/...} 逐字段落回表单;无字段级错误回落原通用提示
      error: (err) => {
        const mapped: Partial<Record<keyof typeof limits, string>> = {};
        const serverNames = { subject: 'Subject', contactName: 'ContactName', contactEmail: 'ContactEmail', content: 'Content' } as const;
        for (const f of ['subject', 'contactName', 'contactEmail', 'content'] as const) {
          const text = this.mapFieldError(err, serverNames[f], (body[f] ?? '').length, limits[f]);
          if (text) mapped[f] = text;
        }
        if (Object.keys(mapped).length > 0) { this.commFieldErrors.set(mapped); return; }
        this.alertError(this.readError(err));
      }
    });
  }

  deleteComm(c: Communication): void {
    void this.confirmDlg(this.t('tracker.commsConfirmDelete'), true).then((ok) => {
      if (!ok) return;
      this.trackerApi.deleteCommunication(this.app.id, c.id).subscribe({
        next: () => {
          this.commEditingId.set(null);
          this.commAdding.set(false);
          this.loadCommunications();
          this.snack.open(this.t('tracker.commsDeleted'), this.t('common.close'), { duration: 2500 });
        },
        error: (err) => this.alertError(this.readError(err))
      });
    });
  }

  // ---------------------------- 时间工具 ----------------------------

  /** ISO(含 offset)→ datetime-local 输入值(YYYY-MM-DDTHH:mm,浏览器本地时区)。 */
  private toLocalInput(iso?: string | null): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    const p = (n: number) => `${n}`.padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
  }

  private nowLocalInput(): string {
    return this.toLocalInput(new Date().toISOString());
  }

  /** datetime-local 输入值 → ISO。无 tz 视为浏览器本地时间,交给后端解析。 */
  private fromLocalInput(v: string): string {
    if (!v) return new Date().toISOString();
    const withSec = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(v) ? v + ':00' : v;
    const d = new Date(withSec);
    return isNaN(d.getTime()) ? new Date().toISOString() : d.toISOString();
  }
}

// ============================================================================
//  求职信 DTO(与后端 CoverLetterDto / CoverLetterReadinessDto 同字段)
//
//  ⚠️ 字段名必须逐字对齐后端的 JSON 输出(ASP.NET 默认 camelCase)。
//     这里手写而不生成,是因为接口很少变;若要改后端字段,记得同步这里。
// ============================================================================

/** 求职信。 */

export interface CoverLetterDto {
  id: string;
  applicationId: string;
  content: string;
  /** Draft | Generated | Final */
  status: string;
  generatedByModel: string | null;
  /** 生成时所用简历版本。 */
  resumeVersion: number | null;
  lastPromptHint: string | null;
  generatedAt: string | null;
  updatedAt: string | null;
  /** 简历已更新到更新版本 → 这封信可能已过期。 */
  isStale: boolean;
  currentResumeVersion: number;
  /** 票101:读时 xmin。 */
  version?: number;
}

/** 生成前的输入体检。 */
export interface CoverLetterReadinessDto {
  hasResume: boolean;
  resumeVersion: number;
  hasJdText: boolean;
  hasCompanyProfile: boolean;
  companyName: string;
  role: string;
  /** FUN-07(票35):后端结构化缺失码(MissingResume/MissingJd/MissingCompanyProfile),有值时说明输入不全。 */
  missingCode: string | null;
}
