import { Component, OnInit, OnDestroy, AfterViewInit, computed, inject, signal, viewChild, ElementRef } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { DragDropModule, CdkDragDrop } from '@angular/cdk/drag-drop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Router } from '@angular/router';
import { MatDividerModule } from '@angular/material/divider';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { catchError, concat, forkJoin, of, type Observable } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { SubnavService } from '../../layout/subnav.service';
import { TrackerApi } from '../../core/api/tracker-api.service';
import { Application, ApplicationStatus, AnswerTemplate, Communication, CommunicationType, Company, Paged, TrackerStats } from '../../core/models/api.models';
import { AnswerLibraryDialogComponent } from './answer-library-dialog.component';
import { SmartAddDialogComponent } from './smart-add-dialog.component';

/** 状态下拉的选项 —— 顺序即漏斗顺序,下拉里也按流程排,避免用户找"面试中"要找半天。 */
const STATUS_ORDER: ApplicationStatus[] = [
  'Saved', 'Applied', 'Screen', 'Interview', 'Offer', 'Accepted', 'Rejected', 'Ghosted', 'Paused', 'Withdrawn'
];

/**
 * 新建对话框允许直接设置的初始状态(只能是从 Saved 合法流转出去的那几个;
 * 其余状态(面试中/Offer 等)需要走正式的状态流转,不能在新建时越级设置)。
 */
const CREATE_STATUS_OPTIONS: ApplicationStatus[] = ['Saved', 'Applied', 'Screen', 'Interview', 'Paused', 'Rejected'];

/** 从 Saved 走到目标状态的合法路径(后端状态机不允许越级)。 */
const STATUS_PATH_FROM_SAVED: Record<string, ApplicationStatus[]> = {
  'Applied': ['Applied'],
  'Screen': ['Applied', 'Screen'],
  'Interview': ['Applied', 'Screen', 'Interview'],
  'Paused': ['Paused'],
  'Rejected': ['Rejected'],
};

/**
 * 投递表单的可编辑形状。
 * 与 Application 分开定义:新建时表单没有 id/createdAt 这类服务端字段,
 * 硬套 Application 会到处写 undefined,不如用一个贴合表单的接口。
 */
export interface ApplicationForm {
  companyName: string;
  role: string;
  location: string;
  salary: string;
  status: ApplicationStatus;
  priority: string;
  appliedDate: string;
  link: string;
  notes: string;
  needsConnectFirst: boolean;
  /** 对应后端 OutreachMessage(外联留言,如"已发 LinkedIn 私信")。 */
  outreachMessage: string;
  /** ★ #6:投递的文档版本 (如 "Forrest_Lin_Resume_Corpay.pdf")。存 notes 前缀。 */
  sentDocuments: string;
  /** 职位描述 JD,供关键词提取和备战计划使用。 */
  jdText: string;
}

function emptyForm(): ApplicationForm {
  return {
    companyName: '', role: '', location: '', salary: '',
    status: 'Applied', priority: 'Medium', appliedDate: todayIso(), link: '',
    notes: '', needsConnectFirst: false, outreachMessage: '', sentDocuments: '',
    jdText: ''
  };
}

function todayIso(): string {
  const d = new Date();
  const m = `${d.getMonth() + 1}`.padStart(2, '0');
  const day = `${d.getDate()}`.padStart(2, '0');
  return `${d.getFullYear()}-${m}-${day}`;
}

/**
 * 新建/编辑投递的弹窗(内联模板)。
 *
 * 为什么内联而不是单独文件:这个表单只在 tracker 用一次,
 * 拆成独立组件要额外传 MAT_DIALOG_DATA + 回传结果,收益不大;
 * 超过两处复用再拆。
 */
@Component({
  selector: 'app-application-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatButtonModule, MatIconModule, MatSlideToggleModule, MatDividerModule
  ],
  template: `
    <h2 mat-dialog-title>{{ isEdit ? t('tracker.dlgEdit') : t('tracker.dlgNew') }}</h2>

    <mat-dialog-content class="dlg-body">
      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fCompany') }}</mat-label>
          <input matInput name="companyName" [(ngModel)]="form.companyName" required>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fRole') }}</mat-label>
          <input matInput name="role" [(ngModel)]="form.role" required>
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fLocation') }}</mat-label>
          <input matInput name="location" [(ngModel)]="form.location" [placeholder]="t('tracker.fLocationPh')">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fSalary') }}</mat-label>
          <input matInput name="salary" [(ngModel)]="form.salary" [placeholder]="t('tracker.fSalaryPh')">
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fStatus') }}</mat-label>
          <mat-select name="status" [(ngModel)]="form.status">
            @for (s of statusOptions; track s) {
              <mat-option [value]="s">{{ label(s) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fPriority') }}</mat-label>
          <mat-select name="priority" [(ngModel)]="form.priority">
            @for (p of priorities; track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fAppliedDate') }}</mat-label>
          <input matInput type="date" name="appliedDate" [(ngModel)]="form.appliedDate">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fOutreach') }}</mat-label>
          <input matInput name="outreachMessage" [(ngModel)]="form.outreachMessage"
                 [placeholder]="t('tracker.fOutreachPh')">
        </mat-form-field>
      </div>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fLink') }}</mat-label>
        <input matInput name="link" [(ngModel)]="form.link" placeholder="https://...">
      </mat-form-field>

      <!-- ★ #6:投递文档版本 -->
      <mat-form-field appearance="outline" class="full">
        <mat-label>投递文档 (如 Forrest_Lin_Resume_Corpay.pdf)</mat-label>
        <input matInput name="sentDocuments" [(ngModel)]="form.sentDocuments"
               placeholder="记录本次投递用的简历/求职信文件名">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fNotes') }}</mat-label>
        <textarea matInput name="notes" rows="4" [(ngModel)]="form.notes"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>职位描述 (JD)</mat-label>
        <textarea matInput name="jdText" rows="6" [(ngModel)]="form.jdText"
                  placeholder="粘贴完整 JD,用于关键词提取和备战计划"></textarea>
      </mat-form-field>

      <div class="qa-row">
        <span class="qa-hint">{{ t('tracker.insertAnswer') }}</span>
        <button mat-stroked-button type="button" (click)="insertAnswer()">
          <mat-icon>library_books</mat-icon>{{ t('tracker.insertAnswer') }}
        </button>
      </div>

      <mat-divider></mat-divider>

      <mat-slide-toggle name="needsConnectFirst" [(ngModel)]="form.needsConnectFirst">
        {{ t('tracker.fConnectFirst') }}
      </mat-slide-toggle>

      @if (error()) {
        <div class="err">
          <mat-icon>error_outline</mat-icon> {{ error() }}
        </div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary" [disabled]="!canSave()"
              (click)="save()">
        {{ isEdit ? t('common.save') : t('common.create') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dlg-body { min-width: 520px; max-width: 640px; }
    @media (max-width: 640px) { .dlg-body { min-width: auto; } }
    .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    mat-form-field { width: 100%; }
    .full { width: 100%; }
    mat-divider { margin: 6px 0 14px; }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 12px; padding: 8px 12px; border-radius: 6px;
      background: #fdecea; color: #b3261e; font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .qa-row {
      display: flex; align-items: center; justify-content: space-between; gap: 10px;
      padding: 10px 12px; margin-bottom: 4px; border-radius: 8px;
      background: rgba(63, 81, 181, 0.06); border: 1px solid rgba(63, 81, 181, 0.16);
    }
    .qa-row .qa-hint { font-size: 12.5px; opacity: 0.7; }
    .qa-row mat-icon { font-size: 18px; width: 18px; height: 18px; margin-right: 4px; vertical-align: middle; }
  `]
})
export class ApplicationDialogComponent {
  readonly dialogRef = inject(MatDialogRef<ApplicationDialogComponent, ApplicationForm | null>);
  /** 传入 null 表示新建;传入 Application 表示编辑。 */
  readonly existing = inject<Application | null>(MAT_DIALOG_DATA);
  private readonly dialog = inject(MatDialog);
  private readonly trackerApi = inject(TrackerApi);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** 新建时只允许从 Saved 合法流转出去的状态;编辑时沿用全部顺序。 */
  readonly statusOptions: ApplicationStatus[] = this.existing ? STATUS_ORDER : CREATE_STATUS_OPTIONS;
  readonly priorities = ['High', 'Medium', 'Low'];

  readonly error = signal<string | null>(null);

  form: ApplicationForm = this.existing ? fromApplication(this.existing) : emptyForm();

  get isEdit(): boolean {
    return this.existing !== null;
  }

  label(s: ApplicationStatus): string {
    return this.i18n.t('status.' + s);
  }

  canSave(): boolean {
    return this.form.companyName.trim().length > 0 && this.form.role.trim().length > 0;
  }

  /**
   * 从问答库挑一条标准回答,插入到备注末尾(留一个空行分隔)。
   * 选完即关弹窗,不阻断主表单填写。
   */
  insertAnswer(): void {
    const ref = this.dialog.open(AnswerLibraryDialogComponent, {
      data: { pick: true as const },
      maxWidth: '720px'
    });
    ref.afterClosed().subscribe((tpl?: AnswerTemplate | null) => {
      if (!tpl) return;
      const block = `【${tpl.question}】\n${tpl.answer}`;
      const cur = this.form.notes?.trim() ?? '';
      this.form.notes = cur ? `${cur}\n\n${block}` : block;
    });
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set(this.t('tracker.required'));
      return;
    }
    // 空字符串在这里统一转 clean,避免后端存下一堆 "" 的脏字段
    this.dialogRef.close(trimForm(this.form));
  }
}

/**
 * 投递看板。
 *
 * 布局选"按状态分组的看板"而不是表格:
 * 求职时最常问的是"我卡在哪一步、哪几家在面试",分组视图一眼看出分布;
 * 表格更适合逐行比对字段,而这里字段比对不是主任务。
 * 分组仍然是懒渲染的列表(不是拖拽 Kanban),因为拖拽要引入 CDK DragDrop 且
 * 状态流转有服务端历史记录,靠拖拽改状态会绕过 history 语义。
 */
@Component({
  selector: 'app-tracker',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatIconModule, MatButtonModule,
    MatChipsModule, MatFormFieldModule, MatInputModule, MatProgressBarModule,
    MatDialogModule, MatTooltipModule, MatSnackBarModule, MatDividerModule,
    MatSelectModule, MatMenuModule, MatButtonToggleModule, DragDropModule
  ],
  templateUrl: './tracker.component.html',
  styleUrl: './tracker.component.scss'
})
export class TrackerComponent implements OnInit, OnDestroy, AfterViewInit {
  private readonly api = inject(ApiClient);
  private readonly trackerApi = inject(TrackerApi);
  private readonly router = inject(Router);
  private readonly subnav = inject(SubnavService);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  tf = (key: string, params: Record<string, string | number | null | undefined>): string => this.i18n.tf(key, params);

  /** 看板/列表视图切换(Simplify 模式)。默认 list,刷新后从 localStorage 恢复。 */
  readonly viewMode = signal<'board' | 'list'>(
    ((): 'board' | 'list' => {
      try {
        const v = localStorage.getItem('yi-tracker-view');
        return v === 'board' ? 'board' : 'list';
      } catch { return 'list'; }
    })()
  );

  setViewMode(m: 'board' | 'list'): void {
    this.viewMode.set(m);
    try { localStorage.setItem('yi-tracker-view', m); } catch { /* 忽略 */ }
  }

  /** 隐藏的列(用户点了列头的眼睛图标)。 */
  private readonly hiddenCols = signal<Set<ApplicationStatus>>(new Set());
  isColumnHidden(s: ApplicationStatus): boolean { return this.hiddenCols().has(s); }
  hideColumn(s: ApplicationStatus): void {
    const next = new Set(this.hiddenCols());
    next.add(s);
    this.hiddenCols.set(next);
    setTimeout(() => this.onBoardScroll(), 50);
  }
  showColumn(s: ApplicationStatus): void {
    const next = new Set(this.hiddenCols());
    next.delete(s);
    this.hiddenCols.set(next);
    setTimeout(() => this.onBoardScroll(), 50);
  }
  /** 列配置菜单用:所有状态 + 是否可见。 */
  readonly columnVisibility = computed(() =>
    this.statusOrder.map(s => ({
      status: s,
      label: this.i18n.t('status.' + s),
      visible: !this.hiddenCols().has(s)
    }))
  );

  /** 列表视图的 pipeline stepper:5 个阶段。 */
  readonly pipelineStages: ApplicationStatus[] =
    ['Saved', 'Applied', 'Screen', 'Interview', 'Offer'] as ApplicationStatus[];

  /** 某申请在某阶段的到达日期(从 history 里找),没到过返回 null。 */
  stageDate(app: Application, stage: ApplicationStatus): string | null {
    const h = app.history?.find(x => x.status === stage);
    if (!h) return null;
    // 只取日期部分 MM-DD
    const d = h.changedAt?.slice(0, 10);
    return d ? d.slice(5) : null;
  }

  /** 某阶段是否已到达(用于 stepper 高亮)。 */
  stageReached(app: Application, stage: ApplicationStatus): boolean {
    return !!app.history?.some(x => x.status === stage) ||
      this.pipelineStages.indexOf(app.status as ApplicationStatus) >=
      this.pipelineStages.indexOf(stage);
  }

  /** 投递后等待天数(用于跟进提醒)。 */
  daysWaiting(app: Application): number | null {
    if (!app.appliedDate) return null;
    const ms = Date.now() - new Date(app.appliedDate).getTime();
    return Math.max(0, Math.floor(ms / 86400000));
  }

  /** 公司 Logo:优先用后端解析的,fallback 用 Clearbit 按域名猜,再失败显示首字母。 */
  logoUrl(app: Application): string | null {
    if (app.companyLogoUrl) return app.companyLogoUrl;
    const domain = this.guessDomain(app.companyName);
    return domain ? `https://logo.clearbit.com/${domain}` : null;
  }

  /** 常见公司名 → 域名(猜错也没关系,加载失败会自动 fallback 到首字母)。 */
  private guessDomain(name: string): string | null {
    const n = (name || '').toLowerCase().trim();
    const map: Record<string, string> = {
      'td bank': 'td.com',
      'td securities': 'td.com',
      'thoughtstorm': 'thoughtstorm.ca',
      'descartes': 'descartes.com',
      'questrade': 'questrade.com',
      'corpay': 'corpay.com',
      'mohawk medbuy': 'mohawkmedbuy.ca',
      'rbc': 'rbc.com',
      'cibc': 'cibc.com',
      'scotiabank': 'scotiabank.com',
      'bmo': 'bmo.com',
    };
    for (const [k, v] of Object.entries(map)) {
      if (n.includes(k)) return v;
    }
    // 通用猜测:去掉空格+特殊字符+.com
    const clean = n.replace(/[^a-z0-9]/g, '');
    return clean.length >= 3 ? `${clean}.com` : null;
  }

  /** 是否需要跟进:到了 nextFollowUpAt,或早期阶段投递超过 14 天无进展。 */
  needsFollowUp(app: Application): boolean {
    if (['Offer', 'Accepted', 'Rejected', 'Withdrawn', 'Ghosted'].includes(app.status)) return false;
    if (app.nextFollowUpAt && new Date(app.nextFollowUpAt).getTime() <= Date.now()) return true;
    // 14天规则只适用于早期阶段(Saved/Applied/Screen)——Interview+ 已有明确进展
    if (!['Saved', 'Applied', 'Screen'].includes(app.status)) return false;
    const d = this.daysWaiting(app);
    return d !== null && d >= 14;
  }

  /**
   * 卡片/列表行上的快速改状态(Simplify 模式):乐观更新本地,失败回滚。
   * 后端 PUT 要求全量 payload,这里用当前对象的值拼。
   */
  quickChangeStatus(app: Application, status: ApplicationStatus): void {
    if (app.status === status) return;
    const prev = app.status;
    // 乐观更新
    app.status = status;
    this.items.set([...this.items()]);

    // 状态流转走专门的 /status 接口(后端状态机校验合法流转),
    // 不能塞进 PUT body —— UpdateApplicationBody 没有 Status 字段,会被静默忽略。
    this.api.post<void>(`/api/jobs/applications/${app.id}/status`, { status }).subscribe({
      next: () => {
        this.loadStats();
        // ★ #2:卡片进入 Interview → 从 JD 自动生成备战计划
        if (status === 'Interview') this.maybeGeneratePrepPlan(app);
      },
      error: () => {
        // 回滚
        app.status = prev;
        this.items.set([...this.items()]);
        this.snack.open(this.t('tracker.statusChangeFailed'), this.t('common.close'), { duration: 3000 });
      }
    });
  }

  /** ★ #2:从 JD 提取技术关键词,生成面试备战清单。 */
  private static readonly TECH_KEYWORDS = [
    'C#', '.NET', 'ASP.NET', 'Entity Framework', 'EF Core', 'WPF', 'Blazor',
    'Java', 'Spring', 'Spring Boot', 'Python', 'Django', 'Flask',
    'JavaScript', 'TypeScript', 'React', 'Angular', 'Vue', 'Node.js',
    'SQL', 'SQL Server', 'PostgreSQL', 'MySQL', 'MongoDB', 'Redis',
    'Azure', 'AWS', 'Docker', 'Kubernetes', 'Microservices', 'REST', 'GraphQL',
    'Git', 'CI/CD', 'Agile', 'Scrum', 'Design Patterns', 'System Design'
  ];

  private maybeGeneratePrepPlan(app: Application): void {
    const jd = app.jdText || app.jdSummary || '';
    const found = jd.trim() ? TrackerComponent.TECH_KEYWORDS.filter(k =>
      jd.toLowerCase().includes(k.toLowerCase())) : [];
    // 用 Material Dialog 替代原生 confirm
    const dialogRef = this.dialog.open(PrepPlanDialogComponent, {
      width: '480px',
      data: { companyName: app.companyName, role: app.role, keywords: found }
    });
    dialogRef.afterClosed().subscribe(go => {
      if (go) {
        // ★ F-B1: 直接创建 Playbook 条目并预填,不让用户重输
        this.api.post<{ id: string }>('/api/interviews', {
          companyName: app.companyName,
          role: app.role,
          jdText: app.jdText || null,
        }).subscribe({
          next: (r) => {
            this.router.navigate(['/playbook', r.id], { queryParams: { tab: 'rounds' } });
          },
          error: () => {
            // 创建失败则回退到老行为:按公司过滤
            this.router.navigate(['/playbook'], { queryParams: { company: app.companyName } });
          }
        });
      }
    });
  }

/**
 * 看板拖拽(Simplify 对等功能):卡片拖到另一列 = 改状态。
   * 复用 quickChangeStatus 的乐观更新 + 失败回滚语义。
   */
  onCardDrop(event: CdkDragDrop<Application[]>, targetStatus: ApplicationStatus): void {
    const app = event.item.data as Application;
    if (!app || app.status === targetStatus) return;
    this.quickChangeStatus(app, targetStatus);
  }

  /** 导出 CSV(Simplify 对等功能):当前筛选下的所有投递。 */
  exportCsv(): void {
    const rows = this.items();
    if (rows.length === 0) {
      this.snack.open(this.t('tracker.exportEmpty'), this.t('common.close'), { duration: 2500 });
      return;
    }    const header = ['Company', 'Role', 'Location', 'Status', 'Priority', 'Applied Date', 'Salary', 'Link', 'Notes'];
    const esc = (v: unknown): string => {
      const s = String(v ?? '');
      return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
    };
    const lines = [header.join(',')];
    for (const a of rows) {
      lines.push([
        esc(a.companyName), esc(a.role), esc(a.location),
        esc(this.i18n.t('status.' + a.status)), esc(a.priority),
        esc(a.appliedDate), esc(a.salary), esc(a.link),
        esc((a.notes ?? '').replace(/\n/g, ' '))
      ].join(','));
    }
    // BOM 让 Excel 正确识别 UTF-8 中文
    const blob = new Blob(['\uFEFF' + lines.join('\n')], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `applications-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    URL.revokeObjectURL(url);
  }

  /** ★ #19:全量备份 (JSON:申请 + 知识库 + STAR 故事 + 复盘)。 */
  async exportBackup(): Promise<void> {
    const data: Record<string, unknown> = {
      exportedAt: new Date().toISOString(),
      version: 1
    };
    // 申请
    try {
      const apps = await this.api.get<Paged<Application>>('/api/jobs/applications', {
        page: 1, pageSize: 1000
      }).toPromise();
      data['applications'] = apps?.items ?? [];
    } catch { data['applications'] = []; }
    // 知识库
    try {
      const kb = await this.api.get<Paged<any>>('/api/knowledge', {
        page: 1, pageSize: 1000
      }).toPromise();
      data['knowledge'] = kb?.items ?? [];
    } catch { data['knowledge'] = []; }
    // STAR 故事 (localStorage)
    try {
      data['starStories'] = JSON.parse(localStorage.getItem('pb.starStories') || '[]');
    } catch { data['starStories'] = []; }
    // 复盘 (localStorage,所有 pb.debrief.* key)
    const debriefs: Record<string, unknown> = {};
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (k?.startsWith('pb.debrief.')) {
        try { debriefs[k] = JSON.parse(localStorage.getItem(k) || 'null'); } catch { /* skip */ }
      }
    }
    data['debriefs'] = debriefs;
    // 粘贴的备战材料 (localStorage,所有 pb-pasted-* key)
    const pasted: Record<string, unknown> = {};
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (k?.startsWith('pb-pasted-')) {
        try { pasted[k] = localStorage.getItem(k); } catch { /* skip */ }
      }
    }
    data['pastedMaterial'] = pasted;

    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `your-interview-backup-${new Date().toISOString().slice(0, 10)}.json`;
    link.click();
    URL.revokeObjectURL(url);
    this.snack.open('备份已下载', '关闭', { duration: 2500 });
  }

  /** ★ 从备份恢复 localStorage 数据 (STAR/复盘/粘贴材料)。 */
  importBackup(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      try {
        const data = JSON.parse(reader.result as string);
        let count = 0;
        if (Array.isArray(data['starStories'])) {
          localStorage.setItem('pb.starStories', JSON.stringify(data['starStories']));
          count++;
        }
        const debriefs = data['debriefs'] as Record<string, unknown> | undefined;
        if (debriefs) {
          for (const [k, v] of Object.entries(debriefs)) {
            localStorage.setItem(k, JSON.stringify(v));
            count++;
          }
        }
        const pasted = data['pastedMaterial'] as Record<string, string> | undefined;
        if (pasted) {
          for (const [k, v] of Object.entries(pasted)) {
            localStorage.setItem(k, v);
            count++;
          }
        }
        this.snack.open(`已恢复 ${count} 项本地数据`, '关闭', { duration: 3000 });
      } catch {
        this.snack.open('备份文件解析失败', '关闭', { duration: 3000 });
      }
      input.value = '';
    };
    reader.readAsText(file);
  }

/** 导入 CSV(Simplify 对等功能):与 exportCsv 格式互通,逐行创建投递。 */
  importCsv(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = () => {
      try {
        const text = String(reader.result ?? '').replace(/^\uFEFF/, '');
        const rows = this.parseCsvRows(text);
        if (rows.length < 2) {
          this.snack.open(this.t('tracker.importEmpty'), this.t('common.close'), { duration: 3000 });
          return;
        }
        // 第一行是表头,跳过。列顺序与 exportCsv 一致:
        // Company, Role, Location, Status, Priority, Applied Date, Salary, Link, Notes
        const dataRows = rows.slice(1).filter(r => r.some(c => c.trim()));
        let created = 0, failed = 0;
        const total = dataRows.length;
        if (total === 0) {
          this.snack.open(this.t('tracker.importEmpty'), this.t('common.close'), { duration: 3000 });
          return;
        }
        for (const r of dataRows) {
          const body = {
            companyName: (r[0] ?? '').trim(),
            role: (r[1] ?? '').trim(),
            location: (r[2] ?? '').trim() || null,
            status: this.parseStatusLabel((r[3] ?? '').trim()),
            priority: (r[4] ?? '').trim() || 'Medium',
            appliedDate: (r[5] ?? '').trim() || null,
            salary: (r[6] ?? '').trim() || null,
            link: (r[7] ?? '').trim() || null,
            notes: (r[8] ?? '').trim() || null,
          };
          if (!body.companyName || !body.role) { failed++; this.checkImportDone(++created + failed, total, created, failed); continue; }
          this.api.post('/api/jobs/applications', body).subscribe({
            next: () => { created++; this.checkImportDone(created + failed, total, created, failed); },
            error: () => { failed++; this.checkImportDone(created + failed, total, created, failed); }
          });
        }
      } catch {
        this.snack.open(this.t('tracker.importFailed'), this.t('common.close'), { duration: 4000 });
      }
      input.value = '';
    };
    reader.readAsText(file, 'utf-8');
  }

  private checkImportDone(done: number, total: number, created: number, failed: number): void {
    if (done >= total) {
      this.reload();
      const msg = this.tf('tracker.importDone', { c: created, f: failed });
      this.snack.open(msg, this.t('common.close'), { duration: 4000 });
    }
  }

  /**
   * CSV 状态列回填:导出时写的是本地化标签(如"已投递"/"Applied"),
   * 导入时反查回状态 key。认不出就回退 'Saved',不让整行失败。
   */
  private parseStatusLabel(label: string): string {
    if (!label) return 'Saved';
    const keys = ['Saved', 'Applied', 'Screen', 'Interview', 'Offer', 'Rejected', 'Paused', 'Withdrawn', 'Accepted', 'Ghosted'];
    // 直接是 key(如英文 CSV 手工编辑)就直接用
    if (keys.includes(label)) return label;
    // 反查本地化标签
    for (const k of keys) {
      if (this.i18n.t('status.' + k) === label) return k;
    }
    return 'Saved';
  }

  /** 最小 CSV 解析器:处理引号转义(""),逗号分隔。 */  private parseCsvRows(text: string): string[][] {
    const rows: string[][] = [];
    let row: string[] = [], field = '', inQuotes = false;
    for (let i = 0; i < text.length; i++) {
      const c = text[i];
      if (inQuotes) {
        if (c === '"') {
          if (text[i + 1] === '"') { field += '"'; i++; }
          else inQuotes = false;
        } else field += c;
      } else if (c === '"') inQuotes = true;
      else if (c === ',') { row.push(field); field = ''; }
      else if (c === '\n' || c === '\r') {
        if (c === '\r' && text[i + 1] === '\n') i++;
        row.push(field); field = '';
        if (row.some(x => x.trim())) rows.push(row);
        row = [];
      } else field += c;
    }
    if (field || row.length > 0) { row.push(field); rows.push(row); }
    return rows;
  }

/** 备战:从投递一键创建 Playbook 面试条目,JD/公司/职位/投递关联自动带入。 */  prepareInterview(app: Application, event: Event): void {
    event.stopPropagation();
    // 直接传 companyId,避免后端按名复用时找不到而生成随机 ID 导致孤儿条目。
    this.api.post<{ id: string }>('/api/interviews', {
      companyId: app.companyId,
      companyName: app.companyName,
      role: app.role,
      jobApplicationId: app.id,
      location: app.location || null,
      jdText: app.jdText || null,
      jdSummary: app.jdSummary || null,
      notes: `从 Tracker 一键创建 (${new Date().toISOString().slice(0, 10)})`
    }).subscribe({
      next: (r) => {
        this.notify(this.t('tracker.prepCreated'));
        this.router.navigate(['/playbook', r.id]);
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** 看板横向滚动:macOS 默认隐藏滚动条,显式左右按钮保证可发现。 */
  private readonly boardEl = viewChild<ElementRef<HTMLElement>>('board');
  private readonly canLeft = signal(false);
  private readonly canRight = signal(false);
  canScrollLeft = this.canLeft.asReadonly();
  canScrollRight = this.canRight.asReadonly();

  scrollBoard(dir: 1 | -1): void {
    const el = this.boardEl()?.nativeElement;
    if (!el) return;
    // 一次滚一列半(列宽 250-300 + 间距 12)
    el.scrollBy({ left: dir * 450, behavior: 'smooth' });
  }

  /** 滚动时更新按钮显隐:到左头隐藏左按钮,到右头隐藏右按钮。 */
  onBoardScroll(): void {
    const el = this.boardEl()?.nativeElement;
    if (!el) return;
    const max = el.scrollWidth - el.clientWidth;
    this.canLeft.set(el.scrollLeft > 8);
    this.canRight.set(el.scrollLeft < max - 8);
  }

  /** 名称首字母头像(看板卡 + 详情页共用)。 */
  companyInitials(name?: string): string {
    const n = (name ?? '').trim();
    if (!n) return '?';
    const parts = n.split(/\s+/).filter(Boolean);
    if (parts.length >= 2) {
      return (parts[0][0] + parts[1][0]).toUpperCase();
    }
    return parts[0].slice(0, 2).toUpperCase();
  }

  /** 由名称哈希出稳定的彩色背景,让降级头像不单调。 */
  avatarColor(name?: string): string {
    const n = (name ?? '?').trim() || '?';
    let h = 0;
    for (let i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) % 360;
    return `hsl(${h}, 52%, 52%)`;
  }

  /** 看板卡 Logo 加载失败的 id 集合(每卡独立降级,不互相影响)。 */
  private readonly brokenLogos = signal<Set<string>>(new Set());
  logoBrokenCard(id: string): boolean { return this.brokenLogos().has(id); }
  markLogoBroken(id: string): void {
    const next = new Set(this.brokenLogos());
    next.add(id);
    this.brokenLogos.set(next);
  }

  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly statusOrder = STATUS_ORDER;
  readonly statusOptions: ApplicationStatus[] = STATUS_ORDER;

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly items = signal<Application[]>([]);
  /** ★ 收藏 (localStorage):id 集合。 */
  readonly favorites = signal<Set<string>>(new Set(
    JSON.parse(localStorage.getItem('yi-tracker-favorites') || '[]')
  ));

  toggleFavorite(id: string): void {
    const f = new Set(this.favorites());
    if (f.has(id)) f.delete(id); else f.add(id);
    this.favorites.set(f);
    try { localStorage.setItem('yi-tracker-favorites', JSON.stringify([...f])); } catch { /* 忽略 */ }
  }

  isFavorite(id: string): boolean {
    return this.favorites().has(id);
  }
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(50);
  readonly totalPages = signal(1);

  /** 筛选条件走服务端 —— 数据量大时前端过滤会漏数据,所以 search/status 都进 query。 */
  readonly search = signal('');
  readonly statusFilter = signal<ApplicationStatus | null>(
    ((): ApplicationStatus | null => {
      try {
        return (localStorage.getItem('yi-tracker-status') as ApplicationStatus) || null;
      } catch { return null; }
    })()
  );
  /** ★ 只看收藏。 */
  readonly favOnly = signal(false);
  /** ★ Active/Archived 标签页 (Simplify 对等)。 */
  readonly showArchived = signal(false);
  /** 排序键直接对齐后端 sortBy(updated/applied/appliedAsc/company/deadline)。 */
  readonly sort = signal<string>(
    ((): string => {
      try {
        return localStorage.getItem('yi-tracker-sort') || 'updated';
      } catch { return 'updated'; }
    })()
  );

  setStatusFilter(s: ApplicationStatus | null): void {
    this.statusFilter.set(s);
    try {
      if (s) localStorage.setItem('yi-tracker-status', s);
      else localStorage.removeItem('yi-tracker-status');
    } catch { /* 忽略 */ }
    this.page.set(1);
    this.reload();
  }

  setSort(s: string): void {
    this.sort.set(s);
    try { localStorage.setItem('yi-tracker-sort', s); } catch { /* 忽略 */ }
    this.page.set(1);
    this.reload();
  }

  readonly stats = signal<TrackerStats | null>(null);

  /** 看板列:按固定状态顺序分组,空组仍保留占位符,好让用户看到"这一列现在是空的"。 */
  readonly columns = computed(() => {
    this.i18n.lang(); // 建立对语言 signal 的依赖,切换语言时重算列标签
    const all = this.displayItems();
    const only = this.statusFilter();
    const hidden = this.hiddenCols();
    const favs = this.favorites();
    const favOnly = this.favOnly();
    const statuses = (only ? [only] : this.statusOrder).filter(s => !hidden.has(s));

    return statuses.map((status) => ({
      status,
      label: this.i18n.t('status.' + status),
      items: all.filter((a) => a.status === status && (!favOnly || favs.has(a.id)))
    }));
  });

  readonly statTotal = computed(() => this.stats()?.total ?? this.total());
  readonly statActive = computed(() => this.stats()?.activeCount ?? 0);
  readonly statInterview = computed(() => this.stats()?.interviewCount ?? 0);
  readonly statOffer = computed(() => this.stats()?.offerCount ?? 0);

  // ------------------------------ Simplify 对标:stale 检测 ------------------------------
  /** 超过多少天无更新算 stale(Simplify 模式:早期阶段 14 天无进展标黄)。 */
  private static readonly STALE_DAYS = 14;
  private static readonly STALE_STATUSES: ApplicationStatus[] = ['Applied', 'Screen', 'Interview'];

  /** 单个投递是否 stale:活跃阶段 + updatedAt 超过 14 天。 */
  isStale(a: Application): boolean {
    if (!TrackerComponent.STALE_STATUSES.includes(a.status)) return false;
    const u = a.updatedAt ? new Date(a.updatedAt).getTime() : 0;
    if (!u) return false;
    return Date.now() - u > TrackerComponent.STALE_DAYS * 86400_000;
  }

  /** 当前列表里的 stale 投递(算一次,模板里复用,避免每行重复算)。 */
  readonly staleIds = computed(() => {
    const s = new Set<string>();
    for (const a of this.items()) if (this.isStale(a)) s.add(a.id);
    return s;
  });
  readonly staleCount = computed(() => this.staleIds().size);
  readonly showStaleOnly = signal(false);
  toggleStaleOnly(): void { this.showStaleOnly.set(!this.showStaleOnly()); }

  /** 展示用列表:stale 筛选打开时只显示 stale(客户端过滤,不打后端)。 */
  readonly displayItems = computed(() => {
    const all = this.items();
    return this.showStaleOnly() ? all.filter(a => this.staleIds().has(a.id)) : all;
  });

  /** 点击每周目标卡片:用原生 prompt 改数字(简单直接,比再开一个 dialog 轻)。 */
  promptGoal(): void {
    const cur = this.weeklyGoal();
    const raw = window.prompt(this.i18n.t('tracker.goalPrompt'), String(cur));
    if (raw === null) return;
    const n = parseInt(raw, 10);
    if (Number.isFinite(n) && n > 0) this.setWeeklyGoal(n);
  }

  // ------------------------------ Simplify 对标:每周目标 ------------------------------
  private static readonly GOAL_KEY = 'yi-tracker-weekly-goal';
  /** 每周投递目标,默认 5(Simplify 默认值)。 */
  readonly weeklyGoal = signal<number>(TrackerComponent.readGoal());
  private static readGoal(): number {
    try {
      const v = parseInt(localStorage.getItem(TrackerComponent.GOAL_KEY) ?? '5', 10);
      return Number.isFinite(v) && v > 0 && v <= 100 ? v : 5;
    } catch { return 5; }
  }
  setWeeklyGoal(n: number): void {
    const v = Math.max(1, Math.min(100, Math.round(n) || 5));
    this.weeklyGoal.set(v);
    try { localStorage.setItem(TrackerComponent.GOAL_KEY, String(v)); } catch { /* 忽略 */ }
  }

  /** 本周一 00:00(本地)。 */
  private static weekStart(): Date {
    const d = new Date();
    d.setHours(0, 0, 0, 0);
    const dow = (d.getDay() + 6) % 7; // 周一=0
    d.setDate(d.getDate() - dow);
    return d;
  }
  /** 本周已投递数(全量):items() 只是当前页的分页切片,翻页/筛选后会算错。
   *  这里按 200/页(后端 pageSize 上限)逐页拉全量,再按 appliedDate 计数。
   *  "本周"定义与旧逻辑一致:本地时间周一 00:00 起(含今天)。 */
  readonly weeklyAppliedCount = signal(0);

  private loadWeeklyApplied(): void {
    const start = TrackerComponent.weekStart().getTime();
    const pageSize = 200;
    const fetchPage = (page: number, acc: Application[]): void => {
      this.api.get<Paged<Application>>('/api/jobs/applications', {
        page, pageSize, sortBy: 'updated'
      }).subscribe({
        next: (r) => {
          const all = acc.concat(r.items ?? []);
          if (page < (r.totalPages ?? 1)) {
            fetchPage(page + 1, all);
          } else {
            let n = 0;
            for (const a of all) {
              const ad = a.appliedDate ? new Date(a.appliedDate).getTime() : 0;
              if (ad >= start) n++;
            }
            this.weeklyAppliedCount.set(n);
          }
        },
        error: () => { /* 统计失败就保留旧值,列表本身不受影响 */ }
      });
    };
    fetchPage(1, []);
  }

  /** ★ #18:幽灵跟进提醒 (Applied 状态 21 天无更新)。 */
  readonly ghostingApps = signal<Application[]>([]);
  loadGhosting(): void {
    this.api.get<Paged<Application>>('/api/jobs/applications', {
      page: 1, pageSize: 200, status: 'Applied'
    }).subscribe({
      next: (r) => {
        const now = Date.now();
        const stale = (r.items ?? []).filter(a => {
          const updated = a.updatedAt ? new Date(a.updatedAt).getTime() : 0;
          const days = (now - updated) / 86400000;
          return days >= 21;
        });
        this.ghostingApps.set(stale);
      },
      error: () => { /* 失败就空着,不打扰主流程 */ }
    });
  }

  /** 本周已投递数:appliedDate 落在本周一之后(含今天)。全量统计,见 loadWeeklyApplied。 */
  readonly weeklyApplied = computed(() => this.weeklyAppliedCount());
  readonly weeklyPct = computed(() => {
    const g = this.weeklyGoal();
    return g > 0 ? Math.min(100, Math.round((this.weeklyApplied() / g) * 100)) : 0;
  });

  // ------------------------------ Simplify 对标:转化漏斗 ------------------------------
  readonly funnel = computed(() => {
    const by = this.stats()?.byStatus ?? {};
    const stages: ApplicationStatus[] = ['Applied', 'Screen', 'Interview', 'Offer', 'Accepted'];
    const counts = stages.map(s => by[s] ?? 0);
    const max = Math.max(1, ...counts);
    return stages.map((s, i) => ({
      status: s,
      label: this.i18n.t('status.' + s),
      count: counts[i],
      // 相对上一阶段的转化率(第一阶段恒 100%)
      rate: i === 0 ? 100 : (counts[i - 1] > 0 ? Math.round((counts[i] / counts[i - 1]) * 100) : 0),
      widthPct: Math.round((counts[i] / max) * 100)
    }));
  });
  /** 漏斗是否有数据(模板里不能写箭头函数,用 computed 包一层)。 */
  readonly hasFunnelData = computed(() => this.funnel().some(f => f.count > 0));

  ngOnInit(): void {
    this.reload();
    this.loadGhosting(); // ★ #18
    // ★ 子导航:有实际功能 —— Status 切看板, List 切列表, Overview 滚到指标区
    this.subnav.register('/tracker', [
      {
        key: 'tracker:status',
        labelKey: 'sub.status',
        icon: 'view_kanban',
        active: () => this.viewMode() === 'board',
        action: () => this.setViewMode('board')
      },
      {
        key: 'tracker:list',
        labelKey: 'sub.list',
        icon: 'list',
        active: () => this.viewMode() === 'list',
        action: () => this.setViewMode('list')
      },
      {
        key: 'tracker:overview',
        labelKey: 'sub.overview',
        icon: 'insights',
        action: () => {
          const el = document.getElementById('overview');
          if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }
      }
    ]);
  }

  ngOnDestroy(): void {
    this.subnav.unregister('/tracker');
  }

  ngAfterViewInit(): void {
    // 初次渲染后计算滚动按钮显隐(列数>可视区时才显示右按钮)
    setTimeout(() => this.onBoardScroll(), 100);
  }

  // ------------------------------ 数据加载 ------------------------------

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<Paged<Application>>('/api/jobs/applications', {
      page: this.page(),
      pageSize: this.pageSize(),
      status: this.statusFilter(),
      search: this.search().trim(),
      sortBy: this.sort(),
      archived: this.showArchived()
    }).subscribe({
      next: (r) => {
        this.items.set(r.items ?? []);
        this.total.set(r.total ?? 0);
        this.totalPages.set(r.totalPages ?? 1);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.items.set([]);
        this.loading.set(false);
      }
    });
  }

  /** 统计是"锦上添花",失败就静默降级 —— 列表本身还能用。 */
  loadStats(): void {
    this.api.get<TrackerStats>('/api/jobs/stats')
      .pipe(catchError(() => of(null)))
      .subscribe((s) => this.stats.set(s));
  }

  reload(): void {
    this.load();
    this.loadStats();
    this.loadWeeklyApplied();
  }

  // ------------------------------ 筛选交互 ------------------------------

  onSearchInput(value: string): void {
    this.search.set(value);
    this.page.set(1);
  }

  onSearchEnter(): void {
    this.page.set(1);
    this.load();
  }

  toggleStatusFilter(s: ApplicationStatus): void {
    this.setStatusFilter(this.statusFilter() === s ? null : s);
  }

  onSortChange(v: string): void {
    this.setSort(v);
  }

  clearFilters(): void {
    this.search.set('');
    this.statusFilter.set(null);
    this.showStaleOnly.set(false);
    this.sort.set('updated');
    try {
      localStorage.removeItem('yi-tracker-status');
      localStorage.setItem('yi-tracker-sort', 'updated');
    } catch { /* 忽略 */ }
    this.page.set(1);
    this.reload();
  }

  goPage(p: number): void {
    if (p < 1 || p > this.totalPages() || p === this.page()) return;
    this.page.set(p);
    this.load();
  }

  // ------------------------------ 增删改查 ------------------------------

  openCreate(): void {
    this.openDialog(null);
  }

  openEdit(app: Application, ev?: Event): void {
    ev?.stopPropagation();   // 卡片的 click 是"打开详情",编辑按钮不该同时触发详情
    this.openDialog(app);
  }

  private openDialog(app: Application | null): void {
    const ref = this.dialog.open(ApplicationDialogComponent, {
      data: app,
      autoFocus: 'first-tabbable',
      restoreFocus: true
    });

    ref.afterClosed().subscribe((form?: ApplicationForm | null) => {
      if (!form) return;
      if (app) this.update(app.id, form);
      else this.create(form);
    });
  }

  private create(form: ApplicationForm): void {
    // 先按公司名解析/创建公司(幂等),拿到 companyId 再建投递 —— 否则后端会因缺 CompanyId 拒收。
    this.trackerApi.resolveCompany(form.companyName.trim()).subscribe({
      next: (c) => {
        this.api.post<Application>('/api/jobs/applications', {
          companyId: c.id,
          role: form.role,
          location: form.location || null,
          link: form.link || null,
          salary: form.salary || null,
          workMode: null,
          source: null,
          jdSummary: null,
          priority: form.priority || null,
          appliedDate: form.appliedDate || null
        }).subscribe({
          next: (app) => this.afterCreate(app, form),
          error: (e: Error) => this.notify(e.message, true)
        });
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** 建完投递后,补齐初始状态(走合法路径逐步流转)/备注/外联留言,再刷新看板。 */
  private afterCreate(app: Application, form: ApplicationForm): void {
    const tasks: Observable<unknown>[] = [];
    const path = STATUS_PATH_FROM_SAVED[form.status] ?? [];
    if (path.length > 0) {
      // 按路径逐步流转(如 Saved→Applied→Screen→Interview),每步都是后端允许的合法流转
      const steps$: Observable<unknown>[] = path.map((s) =>
        this.api.post<void>(`/api/jobs/applications/${app.id}/status`,
          { status: s, note: this.t('tracker.historyCreated') }).pipe(catchError(() => of(null)))
      );
      tasks.push(concat(...steps$));
    }
    if (form.notes) {
      tasks.push(this.updateAppPayload(app.id, form).pipe(catchError(() => of(null))));
    }
    if (form.needsConnectFirst || form.outreachMessage) {
      tasks.push(this.api.put<void>(`/api/jobs/applications/${app.id}/outreach`,
        { posterName: null, needsConnectFirst: form.needsConnectFirst, message: form.outreachMessage || null })
        .pipe(catchError(() => of(null))));
    }

    const done = () => { this.notify(this.t('tracker.created')); this.reload(); };
    if (tasks.length === 0) { done(); return; }
    forkJoin(tasks).subscribe({ next: done, error: done });
  }

  private update(id: string, form: ApplicationForm): void {
    const tasks: Observable<unknown>[] = [
      this.updateAppPayload(id, form).pipe(catchError((e: Error) => { this.notify(e.message, true); return of(null); }))
    ];
    if (form.needsConnectFirst || form.outreachMessage) {
      tasks.push(this.api.put<void>(`/api/jobs/applications/${id}/outreach`,
        { posterName: null, needsConnectFirst: form.needsConnectFirst, message: form.outreachMessage || null })
        .pipe(catchError(() => of(null))));
    }
    const done = () => { this.notify(this.t('tracker.saved')); this.reload(); };
    forkJoin(tasks).subscribe({ next: done, error: done });
  }

  /** 把表单字段拼成 UpdateApplication 所需的完整负载(后端要求全量)。 */
  private updateAppPayload(id: string, form: ApplicationForm): Observable<unknown> {
    // ★ #6:文档版本存入 notes 前缀 [DOCS:...]
    const notes = form.sentDocuments?.trim()
      ? `[DOCS:${form.sentDocuments.trim()}] ${form.notes || ''}`.trim()
      : (form.notes || null);
    return this.api.put<void>(`/api/jobs/applications/${id}`, {
      role: form.role,
      location: form.location || null,
      link: form.link || null,
      salary: form.salary || null,
      workMode: null,
      source: null,
      jdSummary: null,
      notes: notes,
      priority: form.priority || null,
      appliedDate: form.appliedDate || null,
      deadline: null
    });
  }

  /**
   * 智能粘贴入口(需求 6.2.2 / 6.2.3):先解析、再让用户改、最后才入库。
   * 弹窗自己完成写入,这里只负责刷新与提示。
   */
  openSmartAdd(app?: Application): void {
    const ref = this.dialog.open(SmartAddDialogComponent, {
      data: { applications: this.items(), applicationId: app?.id ?? null },
      maxWidth: '860px',
      autoFocus: false,
      restoreFocus: true
    });
    ref.afterClosed().subscribe((r) => {
      if (!r?.ok) return;
      this.notify(r.message);
      this.reload();
    });
  }

  /** 打开申请问答库(管理态)。 */
  openAnswerLibrary(): void {
    this.dialog.open(AnswerLibraryDialogComponent, { data: { pick: false as const }, maxWidth: '760px' });
  }

  /** 删除必须二次确认 —— 误删一条投递记录要重新回忆时间线,代价不对称。 */
  remove(app: Application, ev?: Event): void {
    ev?.stopPropagation();
    const ok = confirm(this.tf('tracker.confirmDelete', { a: `${app.companyName} · ${app.role}` }));
    if (!ok) return;

    this.api.delete<void>(`/api/jobs/applications/${app.id}`).subscribe({
      next: () => {
        this.notify(this.t('tracker.deleted'));
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** ★ 归档 (Simplify 对等)。 */
  archive(app: Application, ev?: Event): void {
    ev?.stopPropagation();
    this.api.post<void>(`/api/jobs/applications/${app.id}/archive`, {}).subscribe({
      next: () => {
        this.notify(this.t('tracker.archived'));
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** ★ 取消归档。 */
  unarchive(app: Application, ev?: Event): void {
    ev?.stopPropagation();
    this.api.post<void>(`/api/jobs/applications/${app.id}/unarchive`, {}).subscribe({
      next: () => {
        this.notify(this.t('tracker.unarchived'));
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  openDetail(app: Application): void {
    // Simplify 模式:右侧抽屉(不是居中弹窗),看板在左侧保持可见。
    // 宽度对齐 Simplify 真站: 66vw (max 1280px)
    this.dialog.open(ApplicationDetailDialogComponent, {
      data: app,
      width: '66vw',
      maxWidth: '1280px',
      minWidth: '900px',
      height: '100vh',
      maxHeight: '100vh',
      position: { right: '0', top: '0' },
      panelClass: 'detail-drawer-panel',
      autoFocus: false
    });
  }

  private notify(msg: string, isError = false): void {
    this.snack.open(msg, this.t('common.close'), {
      duration: isError ? 5000 : 2500,
      horizontalPosition: 'center',
      verticalPosition: 'bottom',
      panelClass: isError ? 'snack-error' : undefined
    });
  }

  // ------------------------------ 展示辅助 ------------------------------

  /** 状态 → CSS 类名,用来上色。颜色是该页唯一"扫一眼就能读数"的通道。 */
  statusClass(s: string): string {
    return `st-${(s || '').toLowerCase()}`;
  }

  statusLabel(s: string): string {
    return this.i18n.t('status.' + s);
  }

  priorityClass(p?: string): string {
    return `pri-${(p ?? 'medium').toLowerCase()}`;
  }

  trackById(_i: number, a: Application): string {
    return a.id;
  }
}

/** Application → 表单形状。缺省值就地补,让编辑弹窗不出现 undefined。 */
function fromApplication(a: Application): ApplicationForm {
  const notes = a.notes ?? '';
  // ★ #6:从 notes 提取 [DOCS:...] 标记的文档版本
  const m = notes.match(/^\[DOCS:([^\]]+)\]\s*/);
  return {
    companyName: a.companyName ?? '',
    role: a.role ?? '',
    location: a.location ?? '',
    salary: a.salary ?? '',
    status: a.status ?? 'Applied',
    priority: a.priority ?? 'Medium',
    appliedDate: (a.appliedDate ?? '').slice(0, 10) || todayIso(),
    link: a.link ?? '',
    notes: m ? notes.slice(m[0].length) : notes,
    needsConnectFirst: a.needsConnectFirst ?? false,
    outreachMessage: a.outreachMessage ?? '',
    sentDocuments: m ? m[1] : '',
    jdText: a.jdText ?? ''
  };
}

function trimForm(f: ApplicationForm): ApplicationForm {
  return {
    ...f,
    companyName: f.companyName.trim(),
    role: f.role.trim(),
    location: f.location.trim(),
    salary: f.salary.trim(),
    link: f.link.trim(),
    notes: f.notes.trim(),
    outreachMessage: f.outreachMessage.trim()
  };
}

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
    MatFormFieldModule, MatInputModule, MatSelectModule, MatTooltipModule
  ],
  template: `
    <h2 mat-dialog-title class="detail-header">
      <span class="logo-wrap detail-logo">
        <span class="logo-avatar" [style.background]="avatarColor(app.companyName)">{{ companyInitials(app.companyName) }}</span>
        @if (app.companyLogoUrl && !logoBroken()) {
          <img class="logo" [src]="app.companyLogoUrl" (error)="logoBroken.set(true)">
        }
      </span>
      <span class="title-wrap">
        <span class="main-title">{{ app.role }}</span>
        <span class="sub-title">{{ app.companyName }}</span>
      </span>
      <span class="header-actions">
        <button class="hdr-btn" (click)="toggleFav()" [matTooltip]="t('tracker.favorite')">
          <mat-icon>{{ isFav() ? 'favorite' : 'favorite_border' }}</mat-icon>
          <span>{{ t('tracker.favorite') }}</span>
        </button>
        <button class="hdr-btn" (click)="doArchive()" [matTooltip]="t('tracker.archive')">
          <mat-icon>archive</mat-icon>
          <span>{{ t('tracker.archive') }}</span>
        </button>
        <button class="hdr-btn danger" (click)="doDelete()" [matTooltip]="t('common.delete')">
          <mat-icon>delete</mat-icon>
          <span>{{ t('common.delete') }}</span>
        </button>
        <button class="hdr-close" mat-dialog-close [matTooltip]="t('common.close')">
          <mat-icon>close</mat-icon>
        </button>
      </span>
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
        <button mat-icon-button class="edit-btn" (click)="startEditPosting()" [matTooltip]="t('common.edit')">
          <mat-icon>edit</mat-icon>
        </button>
      </div>

      <!-- 字段网格: Location, Salary / Job type, URL -->
      <div class="fields">
        <div class="field"><span class="flabel">{{ t('tracker.dLocation') }}</span><span class="fvalue">{{ detail.location || '—' }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.dSalary') }}</span><span class="fvalue">{{ detail.salary || '—' }}</span></div>
        <div class="field"><span class="flabel">{{ t('tracker.dJobType') }}</span><span class="fvalue">{{ detail.jobType || '—' }}</span></div>
        <div class="field"><span class="flabel">URL</span>
          <span class="fvalue">
            @if (detail.jobUrl) {
              <a [href]="detail.jobUrl" target="_blank" class="teal-link">{{ t('tracker.dViewPosting') }} <mat-icon>open_in_new</mat-icon></a>
            } @else { — }
          </span>
        </div>
      </div>

      <!-- JD 正文 -->
      <div class="jd-body">
        @if (detail.jdSummary) {
          <p>{{ detail.jdSummary }}</p>
        }
        @if (detail.jdText) {
          <p class="pre">{{ detail.jdText }}</p>
        }
        @if (!detail.jdSummary && !detail.jdText) {
          <p class="dim">{{ t('tracker.dNoJd') }}</p>
        }
      </div>

      <mat-divider></mat-divider>
      <section>
        <div class="sec-head">
          <span>{{ t('tracker.dJdSummary') }}</span>
          <button mat-icon-button (click)="jdSummaryEditing() ? cancelEditJdSummary() : startEditJdSummary()"
                  [matTooltip]="t('common.edit')" class="edit-btn">
            <mat-icon>{{ jdSummaryEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        @if (jdSummaryEditing()) {
          <textarea class="plain-area" rows="8" [(ngModel)]="jdSummaryDraft"
                    [placeholder]="t('tracker.dJdSummary')"></textarea>
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveJdSummary()"
                    [disabled]="jdSummarySaving()">
              @if (jdSummarySaving()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditJdSummary()">{{ t('common.cancel') }}</button>
          </div>
        } @else {
          <p class="pre">{{ detail.jdSummary || '—' }}</p>
        }
      </section>

      <!-- 匹配分析(纯关键词,不调 AI)-->
      @if (match(); as m) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dMatchAnalysis') }}</span></div>

          <div class="match-head">
            <div class="score" [class]="matchTier(m.score)">
              <span class="score-num">{{ m.score }}</span>
              <span class="score-unit">{{ t('tracker.dScoreUnit') }}</span>
            </div>
            <div class="score-note">
              <strong [class]="matchTier(m.score)">{{ matchTierLabel(m.score) }}</strong>
              <span>{{ tf('tracker.dHitKw', { n: m.hit.length, m: m.total }) }}</span>
            </div>
          </div>

          @if (m.missing.length > 0) {
            <div class="kw-group">
              <span class="kw-title">{{ tn('tracker.dMissingKw', m.missing.length) }}</span>
              <div class="kw-list">
                @for (k of m.missing; track k) {
                  <span class="kw miss">{{ k }}</span>
                }
              </div>
            </div>
          }

          @if (m.hit.length > 0) {
            <div class="kw-group">
              <span class="kw-title">{{ tn('tracker.dHitKwTitle', m.hit.length) }}</span>
              <div class="kw-list">
                @for (k of m.hit; track k) {
                  <span class="kw ok">{{ k }}</span>
                }
              </div>
            </div>
          }

          @if (m.softSkills.length > 0) {
            <p class="soft-note">
              {{ t('tracker.dSoftSkills') }}{{ m.softSkills.join('、') }}
            </p>
          }
        </section>
      }

      <!-- 求职信(Cover Letter) ★ Simplify 视觉 -->
      <mat-divider></mat-divider>
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
          @if (rd.missingHint) {
            <div class="warn-banner">
              <mat-icon>warning</mat-icon>
              <span>{{ rd.missingHint }}</span>
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
              [disabled]="!!clReadiness()?.missingHint">
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

      <!-- JD 全文(默认折叠:可达 40000 字符)—— 2026-10-08:加编辑 -->
      <mat-divider></mat-divider>
      <section>
        <div class="sec-head fold-head">
          <span (click)="jdTextEditing() ? null : jdExpanded.set(!jdExpanded())" style="display:flex;align-items:center;cursor:pointer;gap:6px;">
            <mat-icon>{{ jdExpanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
            <span>{{ t('tracker.dJdFull') }}</span>
            @if (detail.jdText) {
              <span class="fold-len">{{ tn('tracker.dJdChars', detail.jdText.length) }}</span>
            }
          </span>
          <button mat-icon-button
                  (click)="$event.stopPropagation(); jdTextEditing() ? cancelEditJdText() : startEditJdText()"
                  [matTooltip]="t('common.edit')" class="edit-btn">
            <mat-icon>{{ jdTextEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>

        @if (app.jdSourceUrl) {
          <a [href]="app.jdSourceUrl" target="_blank" rel="noopener noreferrer" class="src-link">
            <mat-icon>link</mat-icon>{{ t('tracker.dViewPosting') }}
          </a>
        }

        @if (jdTextEditing()) {
          <textarea class="plain-area" rows="16" [(ngModel)]="jdTextDraft"
                    [placeholder]="t('tracker.dJdFull')"></textarea>
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveJdText()"
                    [disabled]="jdTextSaving()">
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditJdText()">{{ t('common.cancel') }}</button>
          </div>
        } @else if (detail.jdText) {
          @if (jdExpanded()) {
            <div class="jd-full">
              <p class="pre">{{ detail.jdText }}</p>
            </div>
          } @else {
            <p class="pre jd-peek">{{ detail.jdText?.slice(0, 220) }}…</p>
          }
        } @else {
          <p class="cl-empty">{{ t('tracker.dJdEmpty') }}</p>
        }
      </section>

      <!-- 公司情报 -->
      @if (company()?.profile) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dCompanyIntel') }} · {{ company()?.name }}</span></div>
          <p class="pre">{{ company()?.profile }}</p>
          @if (company()?.website) {
            <a [href]="company()!.website!" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ company()!.website }}
            </a>
          }
        </section>
      }

      <mat-divider></mat-divider>
      <section>
        <div class="sec-head">
          <span>{{ t('tracker.dNotes') }}</span>
          <button mat-icon-button (click)="notesEditing() ? cancelEditNotes() : startEditNotes()"
                  [matTooltip]="t('common.edit')" class="edit-btn">
            <mat-icon>{{ notesEditing() ? 'close' : 'edit' }}</mat-icon>
          </button>
        </div>
        @if (notesEditing()) {
          <textarea class="plain-area" rows="8" [(ngModel)]="notesDraft"
                    [placeholder]="t('tracker.dNotes')"></textarea>
          <div class="cl-actions save-row">
            <button mat-flat-button color="primary" (click)="saveNotes()"
                    [disabled]="notesSaving()">
              {{ t('common.save') }}
            </button>
            <button mat-button (click)="cancelEditNotes()">{{ t('common.cancel') }}</button>
          </div>
        } @else {
          <p class="pre">{{ detail.notes || '—' }}</p>
        }
      </section>

      @if ((detail.history?.length ?? 0) > 0) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dHistory') }}</span></div>
          <ol class="history">
            @for (h of detail.history ?? []; track h.changedAt + h.status) {
              <li>
                <span class="to">{{ statusLabel(h.status) }}</span>
                <time>{{ h.changedAt | date: 'yyyy-MM-dd HH:mm' }}</time>
                @if (h.note) { <em class="note">{{ h.note }}</em> }
              </li>
            }
          </ol>
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

      @if (app.link) {
        <mat-divider></mat-divider>
        <section>
          <div class="sec-head"><span>{{ t('tracker.dJobLink') }}</span></div>
          <a [href]="app.link" target="_blank" rel="noopener noreferrer" class="link">
            {{ app.link }}
          </a>
        </section>
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

        <!-- Notes 卡片 (简化版,完整编辑在下方) -->
        <div class="notes-card">
          <h3>{{ t('tracker.dNotes') }}</h3>
          <p class="pre">{{ detail.notes || '—' }}</p>
        </div>
      </div><!-- /.side-col -->
      </div><!-- /.two-col -->
      }

      @else if (detailTab() === 'documents') {
        <div class="tab-placeholder">
          <mat-icon>folder</mat-icon>
          <p>{{ t('tracker.dDocuments') }}</p>
        </div>
      }

      @else if (detailTab() === 'score') {
        <div class="tab-placeholder">
          <mat-icon>assessment</mat-icon>
          <p>{{ t('tracker.dResumeScore') }}</p>
        </div>
      }

      @else if (detailTab() === 'questions') {
        <div class="tab-placeholder">
          <mat-icon>help</mat-icon>
          <p>{{ t('tracker.dQuestions') }}</p>
        </div>
      }

      @else {
        <!-- Email/Communications tab -->
        <!-- 沟通记录:按发生时间倒序的时间线 + 增删改 -->
        <div class="comms">
          <div class="comms-head">
            <button mat-flat-button color="primary" (click)="startAddComm()">
              <mat-icon>add</mat-icon>{{ t('tracker.commsAdd') }}
            </button>
          </div>

          @if (commsLoading()) {
            <mat-progress-bar mode="indeterminate"></mat-progress-bar>
          }

          @if (commEditingId(); as editing) {
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
                <input matInput [(ngModel)]="commDraft().subject" [placeholder]="t('tracker.dCommSubjectPh')">
              </mat-form-field>
              <div class="row2">
                <mat-form-field appearance="outline">
                  <mat-label>{{ t('tracker.commsContact') }}</mat-label>
                  <input matInput [(ngModel)]="commDraft().contactName" [placeholder]="t('tracker.dCommContactPh')">
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Email</mat-label>
                  <input matInput [(ngModel)]="commDraft().contactEmail" placeholder="name@company.com">
                </mat-form-field>
              </div>
              <mat-form-field appearance="outline" class="full">
                <mat-label>{{ t('tracker.commsContent') }}</mat-label>
                <textarea matInput rows="3" [(ngModel)]="commDraft().content"></textarea>
              </mat-form-field>
              <div class="comm-form-actions">
                <button mat-flat-button color="primary" [disabled]="!commDraft().content.trim()"
                        (click)="saveComm()">{{ editing ? t('common.save') : t('tracker.commsAdd') }}</button>
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
                  <button mat-icon-button [matTooltip]="t('common.edit')" (click)="startEditComm(c)">
                    <mat-icon>edit</mat-icon>
                  </button>
                  <button mat-icon-button class="danger" [matTooltip]="t('common.delete')"
                          (click)="deleteComm(c)">
                    <mat-icon>delete_outline</mat-icon>
                  </button>
                </div>
              </li>
            }
          </ul>
        </div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.close') }}</button>
    </mat-dialog-actions>
  `,
  styles: [`
    /* ★ Simplify 对齐:头部标题堆叠 */
    /* ★ Simplify 真站:头部 */
    h2[mat-dialog-title].detail-header {
      display: flex; align-items: flex-start; gap: 16px;
      margin: 0; padding: 24px 32px 20px;
      border-bottom: 1px solid #ECEEF1;
    }
    h2 .title-wrap { display: flex; flex-direction: column; gap: 4px; min-width: 0; flex: 1; }
    h2 .main-title { font-size: 20px; font-weight: 700; line-height: 1.3; color: #1A1D23; }
    h2 .sub-title { font-size: 15px; font-weight: 400; color: #6E7681; }
    .detail-header .logo-wrap { flex: 0 0 62px; width: 62px; height: 62px; }
    .detail-header .logo-avatar { width: 62px; height: 62px; border-radius: 12px; font-size: 22px; }
    .detail-header .logo { width: 62px; height: 62px; border-radius: 12px; }
    .header-actions { display: flex; align-items: center; gap: 10px; margin-left: auto; }
    .hdr-btn {
      display: inline-flex; align-items: center; gap: 8px;
      height: 38px; padding: 0 16px;
      background: #fff; border: 1px solid #E3E6EA; border-radius: 8px;
      font-size: 14px; color: #3D4451; cursor: pointer;
    }
    .hdr-btn mat-icon { font-size: 18px; width: 18px; height: 18px; color: #6E7681; }
    .hdr-btn:hover { background: #F7F8FA; }
    .hdr-btn.danger { border-color: #EF4444; color: #EF4444; }
    .hdr-btn.danger mat-icon { color: #EF4444; }
    .hdr-close {
      display: inline-flex; align-items: center; justify-content: center;
      width: 38px; height: 38px; border: none; background: none; cursor: pointer;
      color: #3D4451; border-radius: 8px;
    }
    .hdr-close:hover { background: #F7F8FA; }
    .detail-body { padding: 0; }
    /* ★ Simplify 真站:双栏布局 */
    .two-col { display: flex; min-height: 400px; }
    .main-col { flex: 1; min-width: 0; padding: 24px 32px; }
    .side-col {
      flex: 0 0 360px; width: 360px;
      padding: 24px 32px 24px 0;
      border-left: 1px solid #ECEEF1;
      margin-left: 32px; padding-left: 32px;
    }
    .status-card {
      background: #fff; border: 1px solid #ECEEF1; border-radius: 12px;
      padding: 24px; margin-bottom: 24px;
    }
    .status-card h3 { font-size: 16px; font-weight: 700; color: #1A1D23; margin: 0 0 16px; }
    .status-select {
      width: 100%; height: 40px; padding: 0 16px;
      border: 1px solid #E3E6EA; border-radius: 8px;
      font-size: 14px; color: #1F2937; background: #fff;
      margin-bottom: 20px; cursor: pointer;
    }
    .timeline { position: relative; padding-left: 28px; }
    .timeline::before {
      content: ''; position: absolute; left: 9px; top: 8px; bottom: 8px;
      width: 2px; background: #E3E6EA;
    }
    .tl-item { position: relative; padding: 0 0 20px; }
    .tl-item:last-child { padding-bottom: 0; }
    .tl-dot {
      position: absolute; left: -28px; top: 2px;
      width: 20px; height: 20px; border-radius: 50%;
      border: 2px solid #D4D9E0; background: #fff;
    }
    .tl-item.done .tl-dot { border-color: #2AAED3; }
    .tl-item.done .tl-dot::after {
      content: ''; position: absolute; left: 50%; top: 50%;
      width: 8px; height: 8px; border-radius: 50%; background: #2AAED3;
      transform: translate(-50%, -50%);
    }
    .tl-name { font-size: 14px; font-weight: 600; color: #1F2937; }
    .tl-item:not(.done) .tl-name { font-weight: 500; color: #6E7681; }
    .tl-date { font-size: 13px; color: #8A94A6; margin-left: 8px; }
    .tl-edit { margin-left: 8px; color: #2AAED3; cursor: pointer; font-size: 14px; }
    .notes-card {
      background: #fff; border: 1px solid #ECEEF1; border-radius: 12px;
      padding: 24px;
    }
    .notes-card h3 { font-size: 16px; font-weight: 700; color: #1A1D23; margin: 0 0 16px; }
    .top { margin-bottom: 14px; }
    .chip { font-size: 12px; }
    .rejection { margin: 6px 0 0; font-size: 13px; color: #c62828; }
    /* ★ Simplify 对齐:状态 pill (灰色圆角) */
    .status-pill {
      display: inline-block; padding: 6px 16px; border-radius: 16px;
      background: rgba(0,0,0,0.06); font-size: 13px; font-weight: 500;
      margin-right: 8px; margin-bottom: 4px;
    }
    .status-pill.warn { background: #fff3e0; color: #e65100; }
    /* ★ Simplify 对齐:字段两列,标签小灰字在上 */
    .fields {
      display: grid; grid-template-columns: 1fr 1fr; gap: 16px 24px;
      margin: 0 0 24px;
    }
    .field { display: flex; flex-direction: column; gap: 4px; }
    .flabel { font-size: 12px; color: rgba(0,0,0,0.45); letter-spacing: 0.2px; }
    .fvalue { font-size: 14px; line-height: 1.5; }
    .kw-gap { margin: 0 0 20px; }
    .kw-gap .flabel { display: block; margin-bottom: 8px; }
    /* ---- Simplify 视觉:section 统一 ---- */
    section { margin: 0; padding: 18px 0; }
    section + section, mat-divider + section { border-top: 1px solid rgba(0,0,0,0.08); }
    mat-divider { display: none; }
    .sec-head {
      display: flex; align-items: center; gap: 6px;
      margin: 0 0 16px; font-size: 17px; font-weight: 700;
      color: #1A1D23; letter-spacing: 0;
    }
    .sec-head .edit-btn { margin-left: auto; }
    .teal-link {
      color: #0E9FCC; font-weight: 500; text-decoration: none;
      display: inline-flex; align-items: center; gap: 6px;
    }
    .teal-link mat-icon { font-size: 14px; width: 14px; height: 14px; }
    .teal-link:hover { text-decoration: underline; }
    .jd-body { margin-top: 24px; }
    .jd-body p { font-size: 14px; color: #3D4451; line-height: 1.57; margin: 0 0 14px; }
    .jd-body .dim { color: #8A94A6; }
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
      border-radius: 10px; background: rgba(0, 0, 0, 0.06); font-size: 11.5px;
    }
    .tag.outcome-pass { background: #e8f5e9; color: #2e7d32; }
    .tag.outcome-fail { background: #ffebee; color: #c62828; }
    .link { font-size: 13px; word-break: break-all; color: #303f9f; }
    /* ---- 匹配分析 ---- */
    .match-head { display: flex; align-items: center; gap: 14px; margin-bottom: 14px; }
    .score {
      display: flex; align-items: baseline; gap: 2px;
      padding: 6px 14px; border-radius: 10px; font-weight: 600;
    }
    .score-num { font-size: 26px; line-height: 1; }
    .score-unit { font-size: 12px; }
    .score.good { background: #e8f5e9; color: #2e7d32; }
    .score.fair { background: #fff8e1; color: #ef6c00; }
    .score.poor { background: #ffebee; color: #c62828; }
    .score-note { display: flex; flex-direction: column; gap: 3px; font-size: 12.5px; }
    .score-note strong.good { color: #2e7d32; }
    .score-note strong.fair { color: #ef6c00; }
    .score-note strong.poor { color: #c62828; }
    .score-note span { opacity: 0.6; }
    .kw-group { margin-bottom: 12px; }
    .kw-title { display: block; font-size: 11.5px; opacity: 0.55; margin-bottom: 6px; }
    .kw-list { display: flex; flex-wrap: wrap; gap: 6px; }
    .kw {
      padding: 2px 9px; border-radius: 11px; font-size: 12px;
      border: 1px solid transparent;
    }
    .kw.miss { background: #ffebee; color: #c62828; border-color: #ffcdd2; }
    .kw.ok { background: #e8f5e9; color: #2e7d32; border-color: #c8e6c9; }
    .soft-note { margin: 8px 0 0; font-size: 12px; opacity: 0.55; line-height: 1.6; }
    /* ---- JD 全文折叠 ---- */
    .fold-head { cursor: default; }
    .fold-head > span:first-child { display: flex; align-items: center; gap: 6px; }
    .fold-head:hover { opacity: 1; }
    .fold-head mat-icon { font-size: 18px; width: 18px; height: 18px; color: rgba(0,0,0,0.45); }
    .fold-len { font-size: 11.5px; opacity: 0.45; font-weight: 400; }
    /* Simplify:标题行右侧的小编辑图标 */
    .edit-btn { width: 26px; height: 26px; line-height: 26px; margin-left: auto; opacity: 0.45; }
    .edit-btn:hover { opacity: 1; }
    .edit-btn mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .jd-full { max-height: 340px; overflow-y: auto; padding-right: 6px; }
    .jd-peek { opacity: 0.65; }
    .src-link {
      display: inline-flex; align-items: center; gap: 4px;
      font-size: 12.5px; color: #303f9f; margin-bottom: 8px;
    }
    .src-link mat-icon { font-size: 15px; width: 15px; height: 15px; }
    /* ---- 求职信 ---- */
    .cl-status {
      margin-left: 6px; padding: 1px 8px; border-radius: 10px;
      font-size: 11px; font-weight: 500;
    }
    .cl-status.cl-draft { background: rgba(0,0,0,0.07); }
    .cl-status.cl-generated { background: #e3f2fd; color: #1565c0; }
    .cl-status.cl-final { background: #e8f5e9; color: #2e7d32; }
    .cl-hint {
      display: flex; align-items: flex-start; gap: 6px;
      margin: 0 0 10px; padding: 9px 11px; border-radius: 8px;
      background: #fff8e1; font-size: 12.5px; line-height: 1.6;
    }
    .cl-hint.stale { background: #f3e5f5; }
    .cl-hint mat-icon { font-size: 16px; width: 16px; height: 16px; flex: 0 0 16px; margin-top: 1px; }
    .cl-extra { width: 100%; margin-bottom: 4px; }
    .cl-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-top: 4px; }
    .cl-actions.save-row { margin-top: 10px; }
    .cl-generating { margin-top: 8px; }
    .cl-generating p { margin: 10px 0 0; font-size: 12.5px; opacity: 0.65; }
    .cl-editor {
      width: 100%; margin-top: 10px; padding: 12px;
      font-family: inherit; font-size: 13.5px; line-height: 1.7;
      border: 1px solid rgba(0,0,0,0.18); border-radius: 8px;
      resize: vertical; box-sizing: border-box;
    }
    .cl-preview {
      margin-top: 10px; padding: 14px 16px; border-radius: 8px;
      background: rgba(0,0,0,0.028); font-size: 13.5px; line-height: 1.75;
      white-space: pre-wrap; word-break: break-word;
      max-height: 420px; overflow-y: auto;
    }
    .cl-empty { margin: 10px 0 0; font-size: 13px; opacity: 0.55; }
    /* ★ Simplify 对齐:黄色警告条 */
    .warn-banner {
      display: flex; align-items: flex-start; gap: 8px;
      margin: 0 0 12px; padding: 10px 12px; border-radius: 8px;
      background: #fef9e7; border: 1px solid #f9e79f;
      font-size: 12.5px; line-height: 1.6; color: #7d6608;
    }
    .warn-banner mat-icon { font-size: 16px; width: 16px; height: 16px; flex: 0 0 16px; margin-top: 1px; }
    /* ★ Simplify 对齐:朴素 textarea (非 mat-form-field) */
    .plain-area {
      width: 100%; box-sizing: border-box;
      padding: 10px 12px; font-family: inherit; font-size: 13.5px; line-height: 1.6;
      border: 1px solid rgba(0,0,0,0.2); border-radius: 8px; resize: vertical;
      background: #fff; color: inherit;
    }
    .plain-area:focus { outline: none; border-color: #1a73e8; }
    .plain-area::placeholder { color: rgba(0,0,0,0.35); }
    .field-hint { margin: 6px 0 10px; font-size: 12px; color: rgba(0,0,0,0.45); line-height: 1.5; }
    .gen-btn { margin: 2px 0 4px; }
    .gen-btn[disabled] { opacity: 0.5; }

    /* ---- 公司 Logo (Simplify:大圆角方块) ---- */
    .logo-wrap { position: relative; flex: 0 0 40px; width: 40px; height: 40px; }
    .logo-avatar {
      display: flex; align-items: center; justify-content: center;
      width: 40px; height: 40px; border-radius: 10px;
      color: #fff; font-size: 14px; font-weight: 700; letter-spacing: 0.3px;
      text-transform: uppercase; overflow: hidden;
    }
    .logo {
      position: absolute; inset: 0;
      width: 40px; height: 40px; border-radius: 10px; object-fit: contain;
      background: #fff;
    }
    .detail-logo { margin-right: 0; flex: 0 0 40px; }

    /* ---- 页签 (Simplify:极简,active 下划线) ---- */
    /* ★ Simplify 真站:5 tab */
    .tabs {
      display: flex; gap: 30px; margin: 0; padding: 0 32px;
      border-bottom: 1px solid #ECEEF1;
    }
    .tab {
      appearance: none; background: none; border: none; cursor: pointer;
      display: inline-flex; align-items: center; gap: 8px;
      padding: 14px 2px; font-size: 14px; color: #8A94A6;
      border-bottom: 3px solid transparent; margin-bottom: -1px;
    }
    .tab mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .tab:hover { color: #3D4451; }
    .tab.active { color: #1A1D23; font-weight: 600; border-bottom-color: #1A1D23; }
    .tab-placeholder {
      display: flex; flex-direction: column; align-items: center; gap: 12px;
      padding: 60px 32px; color: #8A94A6;
    }
    .tab-placeholder mat-icon { font-size: 48px; width: 48px; height: 48px; opacity: 0.4; }
    .tab-placeholder p { font-size: 16px; margin: 0; }

    /* ---- 沟通记录 ---- */
    .comms-head { margin-bottom: 10px; }
    .comms-empty { font-size: 13px; opacity: 0.55; margin: 4px 0 0; }
    .comm-form {
      padding: 12px; margin-bottom: 14px; border-radius: 10px;
      background: rgba(0,0,0,0.025); border: 1px solid rgba(0,0,0,0.08);
    }
    .comm-form mat-form-field { width: 100%; }
    .comm-form .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .comm-form .row2 mat-form-field { width: 100%; }
    .comm-form-actions { display: flex; gap: 8px; }
    .comm-list { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
    .comm-item {
      display: flex; gap: 10px; align-items: flex-start;
      padding: 10px 12px; border-radius: 10px; border: 1px solid rgba(0,0,0,0.08);
    }
    .comm-type {
      flex: 0 0 auto; font-size: 11px; font-weight: 600; padding: 2px 9px; border-radius: 10px;
      background: rgba(0,0,0,0.06);
    }
    .comm-type.ct-email { background: #e3f2fd; color: #1565c0; }
    .comm-type.ct-call { background: #e8f5e9; color: #2e7d32; }
    .comm-type.ct-interview { background: #fff3e0; color: #ef6c00; }
    .comm-type.ct-message { background: #f3e5f5; color: #7b1fa2; }
    .comm-type.ct-note { background: #eceff1; color: #455a64; }
    .comm-body { flex: 1; min-width: 0; }
    .comm-subject { font-size: 13.5px; font-weight: 600; margin-bottom: 2px; }
    .comm-content { margin: 0; font-size: 13px; line-height: 1.6; white-space: pre-wrap; word-break: break-word; }
    .comm-meta { display: flex; flex-wrap: wrap; gap: 4px 10px; margin-top: 5px; font-size: 11.5px; opacity: 0.6; }
    .comm-actions { display: flex; flex-direction: column; gap: 2px; flex: 0 0 auto; }
    .comm-actions .danger { color: #c62828; }
  `]
})
export class ApplicationDetailDialogComponent {
  readonly app = inject<Application>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);
  private readonly trackerApi = inject(TrackerApi);
  private readonly i18n = inject(I18nService);
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

  /** JD 全文是否展开(默认折叠:40000 字符铺开会淹没弹窗)。 */
  readonly jdExpanded = signal(false);

  /** 匹配分析结果。null = 还没算(JD 或简历缺失时保持 null)。 */
  readonly match = signal<MatchResult | null>(null);

  /** 简历全文(匹配分析的比对基准)。null = 尚未拿到。 */
  private readonly resumeText = signal<string | null>(null);

  // ---------------------------- 求职信 ----------------------------

  /** 当前投递的求职信。null = 尚未生成(不是加载失败)。 */
  readonly cl = signal<CoverLetterDto | null>(null);

  /** 生成前的输入体检结果 —— 缺简历时按钮直接禁用,不让用户白等。 */
  readonly clReadiness = signal<CoverLetterReadinessDto | null>(null);

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
    this.api.get<Application>(`/api/jobs/applications/${this.app.id}`).subscribe({
      next: (a) => this.full.set(a),
      error: () => { this.loadError.set(true); /* P0-2:不再静默,模板显示过期警告 */ }
    });

    // 公司情报:Application 只带 companyId,要单独查
    if (this.app.companyId) {
      this.api.get<Company>(`/api/jobs/companies/${this.app.companyId}`).subscribe({
        next: (c) => this.company.set(c),
        error: () => { /* 拿不到公司情报不阻断详情展示 */ }
      });
    }

    // 简历全文:从 Profile 服务取(与 AI 练习共用同一份)
    this.api.get<{ resumeText: string | null }>('/api/jobs/resume-text').subscribe({
      next: (r) => {
        this.resumeText.set(r.resumeText);
        this.recomputeMatch();
      },
      error: () => { this.recomputeMatch(); }
    });

    // 求职信 + 输入体检:详情弹窗打开时各查一次
    this.loadCoverLetter();
    this.loadReadiness();

    // 沟通记录:提前拉一次,这样概览页"沟通记录(N)"角标的数字才准
    this.loadCommunications();
  }

  // ---------------------------- 求职信方法 ----------------------------

  private loadCoverLetter(): void {
    this.api.get<CoverLetterDto | null>(`/api/jobs/applications/${this.app.id}/cover-letter`).subscribe({
      next: (c) => {
        this.cl.set(c);
        this.clDraft = c?.content ?? '';
      },
      error: () => { /* 读不到不阻断详情展示 */ }
    });
  }

  private loadReadiness(): void {
    this.api.get<CoverLetterReadinessDto>(`/api/jobs/applications/${this.app.id}/cover-letter/readiness`)
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
    if (hasContent && !confirm(this.t('tracker.dClOverwrite'))) return;

    this.clGenerating.set(true);
    this.api.post<CoverLetterDto>(
      `/api/jobs/applications/${this.app.id}/cover-letter/generate`,
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
        alert(this.readError(err));
      }
    });
  }

  saveCoverLetter(): void {
    const text = this.clDraft.trim();
    if (!text) { alert(this.t('tracker.dClEmptyErr')); return; }

    this.api.put<CoverLetterDto>(`/api/jobs/applications/${this.app.id}/cover-letter`, { content: text })
      .subscribe({
        next: (c) => { this.cl.set(c); this.clEditing.set(false); },
        error: (err) => alert(this.readError(err))
      });
  }

  cancelEdit(): void {
    this.clDraft = this.cl()?.content ?? '';
    this.clEditing.set(false);
  }

  markCoverLetterFinal(): void {
    this.api.post<CoverLetterDto>(`/api/jobs/applications/${this.app.id}/cover-letter/final`)
      .subscribe({
        next: (c) => this.cl.set(c),
        error: (err) => alert(this.readError(err))
      });
  }

  deleteCoverLetter(): void {
    if (!confirm(this.t('tracker.dClDelete'))) return;
    this.api.delete<void>(`/api/jobs/applications/${this.app.id}/cover-letter`)
      .subscribe({
        next: () => { this.cl.set(null); this.clDraft = ''; this.clEditing.set(false); },
        error: (err) => alert(this.readError(err))
      });
  }

  clStatusLabel(s: string): string {
    switch (s) {
      case 'Generated': return this.t('tracker.dClStatusGen');
      case 'Final': return this.t('tracker.dClStatusFinal');
      default: return this.t('tracker.dClStatusDraft');
    }
  }

  /** JD 全文到手或简历到手后重算 —— 两者缺一就不出分。 */
  private recomputeMatch(): void {
    const jd = this.app.jdText;
    const resume = this.resumeText();
    if (!jd || !resume) return;
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
  private readonly favKey = 'yi-tracker-favorites';

  isFav(): boolean {
    try {
      const arr: string[] = JSON.parse(localStorage.getItem(this.favKey) ?? '[]');
      return arr.includes(this.app.id);
    } catch { return false; }
  }

  toggleFav(): void {
    try {
      const arr: string[] = JSON.parse(localStorage.getItem(this.favKey) ?? '[]');
      const i = arr.indexOf(this.app.id);
      if (i >= 0) arr.splice(i, 1); else arr.push(this.app.id);
      localStorage.setItem(this.favKey, JSON.stringify(arr));
      this.snack.open(this.t(i >= 0 ? 'tracker.unfavorited' : 'tracker.favorited'),
        this.t('common.close'), { duration: 2000 });
    } catch { /* ignore */ }
  }

  doArchive(): void {
    this.api.post<void>(`/api/jobs/applications/${this.app.id}/archive`, {}).subscribe({
      next: () => {
        this.snack.open(this.t('tracker.archived'), this.t('common.close'), { duration: 2000 });
        this.dialogRef.close(true);
      },
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  doDelete(): void {
    if (!confirm(this.t('tracker.deleteConfirm'))) return;
    this.api.delete<void>(`/api/jobs/applications/${this.app.id}`).subscribe({
      next: () => this.dialogRef.close(true),
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  /* ★ Simplify 真站:状态时间线 */
  readonly statusOrder = ['Saved', 'Applied', 'Screen', 'Interview', 'Offer'];

  isStatusDone(s: string): boolean {
    const order = this.statusOrder;
    const currentIdx = order.indexOf(this.detail.status);
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
    this.api.put<void>(`/api/jobs/applications/${this.app.id}/status`,
      { status: newStatus }).subscribe({
      next: () => {
        this.snack.open(this.t('tracker.statusUpdated'), this.t('common.close'), { duration: 2000 });
        this.reloadDetail();
      },
      error: (e) => this.snack.open(this.readError(e), this.t('common.close'), { duration: 3000 })
    });
  }

  private reloadDetail(): void {
    this.api.get<Application>(`/api/jobs/applications/${this.app.id}`).subscribe({
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
  jdSummaryDraft = '';
  readonly jdSummarySaving = signal(false);

  /** JD 全文编辑态(走独立的 /jd 端点,不碰其他字段)。 */
  readonly jdTextEditing = signal(false);
  jdTextDraft = '';
  readonly jdTextSaving = signal(false);

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
    this.api.put(`/api/jobs/applications/${this.app.id}`, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      jdSummary: d.jdSummary,
      notes: this.notesDraft.trim() || null, priority: d.priority,
      deadline: d.deadline, appliedDate: d.appliedDate,
    }).subscribe({
      next: () => {
        this.notesSaving.set(false);
        this.notesEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.notesSaving.set(false);
        alert(this.readError(err));
      }
    });
  }

  startEditJdSummary(): void {
    this.jdSummaryDraft = this.detail.jdSummary ?? '';
    this.jdSummaryEditing.set(true);
  }

  cancelEditJdSummary(): void {
    this.jdSummaryEditing.set(false);
  }

  saveJdSummary(): void {
    const d = this.detail;
    this.jdSummarySaving.set(true);
    this.api.put(`/api/jobs/applications/${this.app.id}`, {
      role: d.role, location: d.location, link: d.link, salary: d.salary,
      jdSummary: this.jdSummaryDraft.trim() || null, notes: d.notes,
      priority: d.priority, deadline: d.deadline, appliedDate: d.appliedDate,
    }).subscribe({
      next: () => {
        this.jdSummarySaving.set(false);
        this.jdSummaryEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.jdSummarySaving.set(false);
        alert(this.readError(err));
      }
    });
  }

  startEditJdText(): void {
    this.jdTextDraft = this.detail.jdText ?? '';
    this.jdTextEditing.set(true);
    // 编辑时自动展开全文
    this.jdExpanded.set(true);
  }

  cancelEditJdText(): void {
    this.jdTextEditing.set(false);
  }

  saveJdText(): void {
    this.jdTextSaving.set(true);
    this.api.put(`/api/jobs/applications/${this.app.id}/jd`, {
      jdText: this.jdTextDraft.trim() || null,
      jdSourceUrl: this.detail.jdSourceUrl ?? null,
    }).subscribe({
      next: () => {
        this.jdTextSaving.set(false);
        this.jdTextEditing.set(false);
        this.refreshDetail();
        this.snack.open(this.t('common.saved'), undefined, { duration: 2000 });
      },
      error: (err) => {
        this.jdTextSaving.set(false);
        alert(this.readError(err));
      }
    });
  }

  /** 保存后重新拉详情,保证弹窗里显示的是最新数据。 */
  private refreshDetail(): void {
    this.api.get<Application>(`/api/jobs/applications/${this.app.id}`).subscribe({
      next: (a) => this.full.set(a),
      error: () => { this.loadError.set(true); }
    });
  }

  // ---------------------------- 沟通记录 ----------------------------

  readonly comms = signal<Communication[]>([]);
  readonly commsLoading = signal(false);
  readonly commEditingId = signal<string | null>(null);
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
    this.commDraft.set(this.blankDraft());
  }

  startEditComm(c: Communication): void {
    this.commEditingId.set(c.id);
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
    const editing = this.commEditingId();
    const req: Observable<unknown> = editing
      ? this.trackerApi.updateCommunication(this.app.id, editing, body)
      : this.trackerApi.createCommunication(this.app.id, body);
    req.subscribe({
      next: () => {
        this.commEditingId.set(null);
        this.loadCommunications();
        this.snack.open(this.t('tracker.commsSaved'), this.t('common.close'), { duration: 2500 });
      },
      error: (err) => alert(this.readError(err))
    });
  }

  deleteComm(c: Communication): void {
    if (!confirm(this.t('tracker.commsConfirmDelete'))) return;
    this.trackerApi.deleteCommunication(this.app.id, c.id).subscribe({
      next: () => {
        this.commEditingId.set(null);
        this.loadCommunications();
        this.snack.open(this.t('tracker.commsDeleted'), this.t('common.close'), { duration: 2500 });
      },
      error: (err) => alert(this.readError(err))
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
//  关键词匹配分析(纯算法,不调 AI)
//  Forrest 2026-09-18:只算技术词;软素质词单列不计分。
// ============================================================================

/** 匹配分析结果。 */
export interface MatchResult {
  /** 0-100 的技术词命中率 —— 这是主分数。 */
  score: number;
  /** JD 里出现且简历也有的技术词。 */
  hit: string[];
  /** JD 里出现但简历没有的技术词 —— 即 Simplify 的 "Missing Keywords"。 */
  missing: string[];
  /** JD 里识别到的技术词总数(分数的分母)。 */
  total: number;
  /** 顺带识别出的软素质词 —— 单列展示,不进分数。 */
  softSkills: string[];
}

/**
 * 技术关键词词典。
 * ⚠️ 只收"能在简历里当技能写"的词 —— 判断标准:
 *    招聘方能拿它做筛选条件,候选人能拿它做技能声明。
 *    收词原则:宁可少收,不可乱收(乱收会让分母虚高、分数虚低)。
 */
const TECH_TERMS: readonly string[] = [
  // 语言
  'C#', 'C\+\+', 'Java', 'Python', 'JavaScript', 'TypeScript', 'SQL', 'T-SQL', 'TSQL',
  'Go', 'Rust', 'Ruby', 'PHP', 'Kotlin', 'Swift', 'Scala', 'Perl', 'Bash', 'PowerShell',
  'HTML', 'CSS', 'SCSS', 'SASS', 'XAML', 'JSON', 'XML', 'YAML',
  // 后端 / 框架
  '.NET', '.NET Core', 'ASP.NET', 'Web API', 'EF Core', 'Entity Framework', 'WCF', 'WPF',
  'Node.js', 'Express', 'Spring', 'Django', 'Flask', 'FastAPI', 'gRPC', 'REST', 'RESTful',
  'GraphQL', 'WebSockets', 'SignalR', 'Prism', 'MVVM', 'MVC',
  // 前端
  'Angular', 'React', 'Vue', 'Redux', 'RxJS', 'NgRx', 'jQuery', 'Tailwind', 'Bootstrap',
  'Material', 'Webpack', 'Vite', 'ES6', 'SASS',
  // 数据库 / 缓存
  'SQL Server', 'PostgreSQL', 'MySQL', 'Oracle', 'MongoDB', 'DynamoDB', 'Redis', 'Memcached',
  'Elasticsearch', 'Cassandra', 'SQLite', 'Cosmos DB', 'BigQuery', 'Snowflake', 'Redshift',
  // 云 / 基础设施
  'Azure', 'AWS', 'GCP', 'Google Cloud', 'Compute Engine', 'App Services', 'Service Bus',
  'S3', 'EC2', 'ECS', 'Fargate', 'Lambda', 'Kubernetes', 'Docker', 'Terraform', 'Helm',
  'PaaS', 'IaaS', 'SaaS', 'Serverless',
  // 架构 / 方法
  'Microservices', 'Clean Architecture', 'DDD', 'Domain-Driven Design', 'SOLID', 'CQRS',
  'Event Sourcing', 'Strangler', 'Design Patterns', 'Distributed', 'High Availability',
  'Scalability', 'Load Balancing', 'Caching', 'Message Queue', 'Kafka', 'RabbitMQ',
  // 测试 / 质量
  'Unit Testing', 'TDD', 'xUnit', 'NUnit', 'MSTest', 'Jest', 'Jasmine', 'Karma', 'Cypress',
  'Playwright', 'Selenium', 'SonarQube', 'Code Review', 'Integration Testing',
  // DevOps
  'CI/CD', 'GitHub Actions', 'Jenkins', 'TeamCity', 'Azure DevOps', 'GitLab', 'Git',
  'Observability', 'Prometheus', 'Grafana', 'Datadog', 'New Relic', 'Splunk',
  // 安全 / 合规
  'OWASP', 'Security', 'OAuth', 'JWT', 'SSO', 'SAML', 'FedRAMP', 'FIPS',
  // 其他
  'Linux', 'Unix', 'Multithreading', 'Asynchronous', 'Object-Oriented', 'OOP',
  'Agile', 'Scrum', 'Kanban', 'Jira', 'Confluence', 'AI', 'Machine Learning', 'LLM',
  'Web Application', 'Web Services', 'Full-Stack', 'Full Stack', 'Front-End', 'Back-End'
];

/**
 * 软素质词 —— 单独识别、单独展示,但**不进分数**。
 * 理由:简历里不会把"团队合作"当技能写,算进分母会让所有人得分虚低。
 */
const SOFT_TERMS: readonly string[] = [
  'team player', 'employee engagement', 'communication skills', 'self-starter',
  'problem-solving', 'analytical', 'attention to detail', 'collaboration',
  'fast-paced', 'entrepreneurial', 'work well under pressure', 'written and verbal',
  'interpersonal', 'time management', 'adaptable', 'proactive', 'mentoring',
  'stakeholder', 'leadership',
  '团队合作', '沟通能力', '抗压', '责任心', '学习能力'
];

/** 归一化:小写 + 折叠空白,用于跨大小写、跨连字符比对。 */
function norm(t: string): string {
  return t.toLowerCase().replace(/\s+/g, ' ').trim();
}

/**
 * 常见别名归一 —— 让 "ASP.NET" 和 "ASP .NET"、"C Sharp" 与 "C#"
 * 这类写法差异不会把命中判成未命中。
 */
function aliasesOf(term: string): string[] {
  const n = norm(term);
  const out = [n];
  if (n === 'c#') out.push('c sharp', 'csharp');
  if (n === '.net' || n === '.net core') out.push('.net', '.net core', 'dotnet', 'asp.net');
  if (n === 'asp.net') out.push('asp.net', 'aspnet', 'asp .net');
  if (n === 'rest' || n === 'restful') out.push('rest', 'restful');
  if (n === 'ci/cd') out.push('ci/cd', 'ci cd', 'continuous integration');
  if (n === 'full-stack' || n === 'full stack') out.push('full-stack', 'full stack', 'fullstack');
  if (n === 'gcp' || n === 'google cloud') out.push('gcp', 'google cloud');
  if (n === 'dd' + 'd') out.push('ddd', 'domain-driven design', 'domain driven design');
  if (n === 'unit testing') out.push('unit test', 'unit testing', 'unit tests');
  if (n === 't-sql' || n === 'tsql') out.push('t-sql', 'tsql', 't sql');
  return Array.from(new Set(out));
}

/** 该词是否在文本里出现(大小写不敏感 + 别名)。 */
function occursIn(text: string, term: string): boolean {
  const hay = norm(text);
  return aliasesOf(term).some((a) => hay.includes(a));
}

/**
 * 计算简历与 JD 的匹配度。
 *
 * @param resume 简历全文
 * @param jd     JD 全文
 */
export function computeMatch(resume: string, jd: string): MatchResult {
  // 只把"JD 里真的出现过"的技术词算进分母 ——
  // 词典有几百个词,全算分母会变成"词典命中率",毫无意义。
  const jdTech = TECH_TERMS.filter((t) => occursIn(jd, t));

  const hit: string[] = [];
  const missing: string[] = [];
  for (const t of jdTech) {
    if (occursIn(resume, t)) hit.push(t);
    else missing.push(t);
  }

  const total = jdTech.length;
  const score = total === 0 ? 0 : Math.round((hit.length / total) * 100);

  const softSkills = SOFT_TERMS.filter((t) => occursIn(jd, t));

  return { score, hit, missing, total, softSkills };
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
}

/** 生成前的输入体检。 */
export interface CoverLetterReadinessDto {
  hasResume: boolean;
  resumeVersion: number;
  hasJdText: boolean;
  hasCompanyProfile: boolean;
  companyName: string;
  role: string;
  /** 有值时说明输入不全(简历缺失是硬阻断,JD/公司情报是建议补)。 */
  missingHint: string | null;
}



/** ★ 备战计划对话框 (替代原生 confirm)。 */
@Component({
  selector: 'app-prep-plan-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>🎯 面试备战计划</h2>
    <mat-dialog-content>
      <p><b>{{ data.companyName }}</b> — {{ data.role }}</p>
      <p>技术关键词 (从 JD 提取):</p>
      <ul>
        @for (k of data.keywords; track k) { <li>{{ k }}</li> }
        @if (data.keywords.length === 0) { <li><i>未识别到关键词</i></li> }
      </ul>
      <p>建议准备:</p>
      <ol>
        <li>针对每个关键词准备 1 个项目实例</li>
        <li>在 Tech Stack 里复习相关条目</li>
        <li>用 AI Practice 做一次模拟问答</li>
      </ol>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>稍后</button>
      <button mat-flat-button color="primary" [mat-dialog-close]="true">去 Playbook 准备</button>
    </mat-dialog-actions>
  `
})
export class PrepPlanDialogComponent {
  readonly data = inject(MAT_DIALOG_DATA) as { companyName: string; role: string; keywords: string[] };
}
