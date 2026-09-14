import { Component, OnInit, computed, inject, signal } from '@angular/core';
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
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatDividerModule } from '@angular/material/divider';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { Application, ApplicationStatus, Paged, TrackerStats } from '../../core/models/api.models';

/** 状态下拉的选项 —— 顺序即漏斗顺序,下拉里也按流程排,避免用户找"面试中"要找半天。 */
const STATUS_ORDER: ApplicationStatus[] = [
  'Saved', 'Applied', 'Screen', 'Interview', 'Offer', 'Rejected', 'Paused', 'Withdrawn'
];

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
  outreachStatus: string;
}

function emptyForm(): ApplicationForm {
  return {
    companyName: '', role: '', location: '', salary: '',
    status: 'Applied', priority: 'Medium', appliedDate: todayIso(), link: '',
    notes: '', needsConnectFirst: false, outreachStatus: ''
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
    <h2 mat-dialog-title>{{ isEdit ? '编辑投递' : '新建投递' }}</h2>

    <mat-dialog-content class="dlg-body">
      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>公司名称</mat-label>
          <input matInput name="companyName" [(ngModel)]="form.companyName" required>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>职位</mat-label>
          <input matInput name="role" [(ngModel)]="form.role" required>
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>地点</mat-label>
          <input matInput name="location" [(ngModel)]="form.location" placeholder="如 Toronto / Remote">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>薪资范围</mat-label>
          <input matInput name="salary" [(ngModel)]="form.salary" placeholder="如 90k-110k CAD">
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>状态</mat-label>
          <mat-select name="status" [(ngModel)]="form.status">
            @for (s of statusOptions; track s) {
              <mat-option [value]="s">{{ label(s) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>优先级</mat-label>
          <mat-select name="priority" [(ngModel)]="form.priority">
            @for (p of priorities; track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>投递日期</mat-label>
          <input matInput type="date" name="appliedDate" [(ngModel)]="form.appliedDate">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>外联状态</mat-label>
          <input matInput name="outreachStatus" [(ngModel)]="form.outreachStatus"
                 placeholder="如 已发 LinkedIn 私信">
        </mat-form-field>
      </div>

      <mat-form-field appearance="outline" class="full">
        <mat-label>岗位链接</mat-label>
        <input matInput name="link" [(ngModel)]="form.link" placeholder="https://...">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>备注</mat-label>
        <textarea matInput name="notes" rows="3" [(ngModel)]="form.notes"></textarea>
      </mat-form-field>

      <mat-divider></mat-divider>

      <mat-slide-toggle name="needsConnectFirst" [(ngModel)]="form.needsConnectFirst">
        需要先建立人脉(如先 Connect 再内推)
      </mat-slide-toggle>

      @if (error()) {
        <div class="err">
          <mat-icon>error_outline</mat-icon> {{ error() }}
        </div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>取消</button>
      <button mat-raised-button color="primary" [disabled]="!canSave()"
              (click)="save()">
        {{ isEdit ? '保存' : '创建' }}
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
  `]
})
export class ApplicationDialogComponent {
  readonly dialogRef = inject(MatDialogRef<ApplicationDialogComponent, ApplicationForm | null>);
  /** 传入 null 表示新建;传入 Application 表示编辑。 */
  readonly existing = inject<Application | null>(MAT_DIALOG_DATA);

  readonly statusOptions = STATUS_ORDER;
  readonly priorities = ['High', 'Medium', 'Low'];

  readonly error = signal<string | null>(null);

  form: ApplicationForm = this.existing ? fromApplication(this.existing) : emptyForm();

  get isEdit(): boolean {
    return this.existing !== null;
  }

  label(s: ApplicationStatus): string {
    return STATUS_LABELS[s] ?? s;
  }

  canSave(): boolean {
    return this.form.companyName.trim().length > 0 && this.form.role.trim().length > 0;
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set('公司名称与职位为必填项');
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
    MatSelectModule
  ],
  templateUrl: './tracker.component.html',
  styleUrl: './tracker.component.scss'
})
export class TrackerComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly statusOrder = STATUS_ORDER;
  readonly statusOptions: ApplicationStatus[] = STATUS_ORDER;

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly items = signal<Application[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(50);
  readonly totalPages = signal(1);

  /** 筛选条件走服务端 —— 数据量大时前端过滤会漏数据,所以 search/status 都进 query。 */
  readonly search = signal('');
  readonly statusFilter = signal<ApplicationStatus | null>(null);
  readonly sort = signal<string>('updatedDesc');

  readonly stats = signal<TrackerStats | null>(null);

  /** 看板列:按固定状态顺序分组,空组仍保留占位符,好让用户看到"这一列现在是空的"。 */
  readonly columns = computed(() => {
    const all = this.items();
    const only = this.statusFilter();
    const statuses = only ? [only] : this.statusOrder;

    return statuses.map((status) => ({
      status,
      label: STATUS_LABELS[status] ?? status,
      items: all.filter((a) => a.status === status)
    }));
  });

  readonly statTotal = computed(() => this.stats()?.total ?? this.total());
  readonly statActive = computed(() => this.stats()?.activeCount ?? 0);
  readonly statInterview = computed(() => this.stats()?.interviewCount ?? 0);
  readonly statOffer = computed(() => this.stats()?.offerCount ?? 0);

  ngOnInit(): void {
    this.load();
    this.loadStats();
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
      sort: this.sort()
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
    this.statusFilter.set(this.statusFilter() === s ? null : s);
    this.page.set(1);
    this.load();
  }

  onSortChange(v: string): void {
    this.sort.set(v);
    this.page.set(1);
    this.load();
  }

  clearFilters(): void {
    this.search.set('');
    this.statusFilter.set(null);
    this.sort.set('updatedDesc');
    this.page.set(1);
    this.load();
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
    this.api.post<Application>('/api/jobs/applications', form).subscribe({
      next: () => {
        this.notify('已创建投递记录');
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  private update(id: string, form: ApplicationForm): void {
    this.api.put<Application>(`/api/jobs/applications/${id}`, form).subscribe({
      next: () => {
        this.notify('已保存修改');
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** 删除必须二次确认 —— 误删一条投递记录要重新回忆时间线,代价不对称。 */
  remove(app: Application, ev?: Event): void {
    ev?.stopPropagation();
    const ok = confirm(`确认删除「${app.companyName} · ${app.role}」这条投递记录?此操作不可撤销。`);
    if (!ok) return;

    this.api.delete<void>(`/api/jobs/applications/${app.id}`).subscribe({
      next: () => {
        this.notify('已删除');
        this.reload();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  openDetail(app: Application): void {
    this.dialog.open(ApplicationDetailDialogComponent, {
      data: app,
      maxWidth: '680px',
      autoFocus: false
    });
  }

  private notify(msg: string, isError = false): void {
    this.snack.open(msg, '关闭', {
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
    return STATUS_LABELS[s as ApplicationStatus] ?? s;
  }

  priorityClass(p?: string): string {
    return `pri-${(p ?? 'medium').toLowerCase()}`;
  }

  trackById(_i: number, a: Application): string {
    return a.id;
  }
}

/** 状态中文映射 —— 全局唯一一份,避免各处拼错。 */
const STATUS_LABELS: Record<ApplicationStatus, string> = {
  Saved: '已收藏',
  Applied: '已投递',
  Screen: '初筛',
  Interview: '面试中',
  Offer: 'Offer',
  Rejected: '已拒',
  Paused: '暂停',
  Withdrawn: '已撤回'
};

/** Application → 表单形状。缺省值就地补,让编辑弹窗不出现 undefined。 */
function fromApplication(a: Application): ApplicationForm {
  return {
    companyName: a.companyName ?? '',
    role: a.role ?? '',
    location: a.location ?? '',
    salary: a.salary ?? '',
    status: a.status ?? 'Applied',
    priority: a.priority ?? 'Medium',
    appliedDate: (a.appliedDate ?? '').slice(0, 10) || todayIso(),
    link: a.link ?? '',
    notes: a.notes ?? '',
    needsConnectFirst: a.needsConnectFirst ?? false,
    outreachStatus: a.outreachStatus ?? ''
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
    outreachStatus: f.outreachStatus.trim()
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
    MatChipsModule, MatDividerModule
  ],
  template: `
    <h2 mat-dialog-title>
      {{ app.companyName }}
      <span class="role">{{ app.role }}</span>
    </h2>

    <mat-dialog-content class="detail-body">
      <div class="top">
        <mat-chip-set>
          <mat-chip [class]="'chip ' + statusClass(app.status)">{{ statusLabel(app.status) }}</mat-chip>
          @if (app.priority) {
            <mat-chip>{{ app.priority }} 优先级</mat-chip>
          }
          @if (app.needsConnectFirst) {
            <mat-chip highlighted>需先建立人脉</mat-chip>
          }
        </mat-chip-set>
      </div>

      <dl class="fields">
        <div><dt>地点</dt><dd>{{ app.location || '—' }}</dd></div>
        <div><dt>薪资</dt><dd>{{ app.salary || '—' }}</dd></div>
        <div><dt>投递日期</dt><dd>{{ app.appliedDate || '—' }}</dd></div>
        <div><dt>外联状态</dt><dd>{{ app.outreachStatus || '—' }}</dd></div>
        <div><dt>简历匹配度</dt>
          <dd>{{ app.resumeScore != null ? app.resumeScore + ' 分' : '—' }}</dd></div>
        <div><dt>预估通过率</dt>
          <dd>{{ app.passRateEstimate != null ? app.passRateEstimate + '%' : '—' }}</dd></div>
        <div><dt>创建时间</dt><dd>{{ app.createdAt | date: 'yyyy-MM-dd HH:mm' }}</dd></div>
        <div><dt>最近更新</dt>
          <dd>{{ app.updatedAt ? (app.updatedAt | date: 'yyyy-MM-dd HH:mm') : '—' }}</dd></div>
      </dl>

      @if (app.jdSummary) {
        <mat-divider></mat-divider>
        <section>
          <h4>JD 摘要</h4>
          <p class="pre">{{ app.jdSummary }}</p>
        </section>
      }

      @if (app.notes) {
        <mat-divider></mat-divider>
        <section>
          <h4>备注</h4>
          <p class="pre">{{ app.notes }}</p>
        </section>
      }

      @if ((app.history?.length ?? 0) > 0) {
        <mat-divider></mat-divider>
        <section>
          <h4>状态流转</h4>
          <ol class="history">
            @for (h of app.history ?? []; track h.at + h.to) {
              <li>
                <span class="from">{{ statusLabel(h.from) }}</span>
                <mat-icon>arrow_forward</mat-icon>
                <span class="to">{{ statusLabel(h.to) }}</span>
                <time>{{ h.at | date: 'yyyy-MM-dd HH:mm' }}</time>
                @if (h.note) { <em class="note">{{ h.note }}</em> }
              </li>
            }
          </ol>
        </section>
      }

      @if ((app.rounds?.length ?? 0) > 0) {
        <mat-divider></mat-divider>
        <section>
          <h4>面试轮次</h4>
          <ul class="rounds">
            @for (r of app.rounds ?? []; track r.id) {
              <li>
                <strong>第 {{ r.roundNo }} 轮</strong>
                @if (r.scheduledAt) { <span>{{ r.scheduledAt | date: 'yyyy-MM-dd HH:mm' }}</span> }
                @if (r.format) { <span class="tag">{{ r.format }}</span> }
                @if (r.outcome) { <span class="tag">{{ r.outcome }}</span> }
                @if (r.notes) { <p class="pre">{{ r.notes }}</p> }
              </li>
            }
          </ul>
        </section>
      }

      @if (app.link) {
        <mat-divider></mat-divider>
        <section>
          <h4>岗位链接</h4>
          <a [href]="app.link" target="_blank" rel="noopener noreferrer" class="link">
            {{ app.link }}
          </a>
        </section>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>关闭</button>
    </mat-dialog-actions>
  `,
  styles: [`
    h2 { display: flex; align-items: baseline; gap: 10px; }
    .role { font-size: 14px; font-weight: 400; opacity: 0.6; }
    .detail-body { min-width: 460px; max-width: 620px; }
    @media (max-width: 620px) { .detail-body { min-width: auto; } }
    .top { margin-bottom: 14px; }
    .chip { font-size: 12px; }
    .fields {
      display: grid; grid-template-columns: 1fr 1fr; gap: 10px 18px;
      margin: 0 0 16px;
    }
    .fields > div { display: flex; flex-direction: column; gap: 2px; }
    dt { font-size: 11.5px; letter-spacing: 0.3px; opacity: 0.5; }
    dd { margin: 0; font-size: 13.5px; }
    section { margin-top: 16px; }
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
    .link { font-size: 13px; word-break: break-all; color: #303f9f; }
  `]
})
export class ApplicationDetailDialogComponent {
  readonly app = inject<Application>(MAT_DIALOG_DATA);

  statusLabel(s: string): string {
    return STATUS_LABELS[s as ApplicationStatus] ?? s;
  }

  statusClass(s: string): string {
    return `st-${(s || '').toLowerCase()}`;
  }
}
