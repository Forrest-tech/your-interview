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
import { I18nService } from '../../core/i18n/i18n.service';
import { Application, ApplicationStatus, Company, Paged, TrackerStats } from '../../core/models/api.models';

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
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number): string => this.i18n.tn(key, n);

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
    MatChipsModule, MatDividerModule, FormsModule, MatProgressBarModule,
    MatFormFieldModule, MatInputModule
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

      <!-- 匹配分析(纯关键词,不调 AI)-->
      @if (match(); as m) {
        <mat-divider></mat-divider>
        <section>
          <h4>简历匹配分析</h4>

          <div class="match-head">
            <div class="score" [class]="matchTier(m.score)">
              <span class="score-num">{{ m.score }}</span>
              <span class="score-unit">分</span>
            </div>
            <div class="score-note">
              <strong [class]="matchTier(m.score)">{{ matchTierLabel(m.score) }}</strong>
              <span>命中 {{ m.hit.length }} / {{ m.total }} 个 JD 技术关键词</span>
            </div>
          </div>

          @if (m.missing.length > 0) {
            <div class="kw-group">
              <span class="kw-title">缺少的关键词 ({{ m.missing.length }})</span>
              <div class="kw-list">
                @for (k of m.missing; track k) {
                  <span class="kw miss">{{ k }}</span>
                }
              </div>
            </div>
          }

          @if (m.hit.length > 0) {
            <div class="kw-group">
              <span class="kw-title">已命中 ({{ m.hit.length }})</span>
              <div class="kw-list">
                @for (k of m.hit; track k) {
                  <span class="kw ok">{{ k }}</span>
                }
              </div>
            </div>
          }

          @if (m.softSkills.length > 0) {
            <p class="soft-note">
              另提及软素质要求(不计入分数):{{ m.softSkills.join('、') }}
            </p>
          }
        </section>
      }

      <!-- 求职信(Cover Letter)-->
      <mat-divider></mat-divider>
      <section class="cl-section">
        <h4 class="fold-head">
          <mat-icon>description</mat-icon>
          <span>求职信</span>
          @if (cl(); as c) {
            <span class="cl-status" [class]="'cl-' + c.status.toLowerCase()">{{ clStatusLabel(c.status) }}</span>
          }
          <span class="fold-len">
            @if (cl(); as c) { {{ c.content.length }} 字符 }
          </span>
        </h4>

        <!-- 输入体检提示(缺简历/缺 JD/缺公司情报)-->
        @if (clReadiness(); as rd) {
          @if (rd.missingHint) {
            <p class="cl-hint">
              <mat-icon>info</mat-icon>
              <span>{{ rd.missingHint }}</span>
            </p>
          }
          @if (cl()?.isStale) {
            <p class="cl-hint stale">
              <mat-icon>history</mat-icon>
              <span>这封信基于简历 v{{ cl()?.resumeVersion }} 生成,当前简历已是 v{{ rd.resumeVersion }} —— 建议重新生成。</span>
            </p>
          }
        }

        <!-- 生成参数 -->
        @if (!clGenerating()) {
          <mat-form-field appearance="outline" class="cl-extra">
            <mat-label>额外要求(可选)</mat-label>
            <textarea matInput rows="2" [(ngModel)]="clExtra"
              placeholder="例:强调我在 Citigroup 的低延迟交易经验,语气务实一些"></textarea>
            <mat-hint>AI 会读你的简历 + JD 全文 + 公司情报,再叠加这里的补充。</mat-hint>
          </mat-form-field>
        }

        <!-- 生成按钮 / 生成中 -->
        @if (clGenerating()) {
          <div class="cl-generating">
            <mat-progress-bar mode="indeterminate"></mat-progress-bar>
            <p>正在生成…通常 20-60 秒,请勿关闭窗口。</p>
          </div>
        } @else {
          <div class="cl-actions">
            <button mat-flat-button color="primary" (click)="generateCoverLetter()"
              [disabled]="!!clReadiness()?.missingHint">
              <mat-icon>auto_awesome</mat-icon>
              {{ (cl()?.content ? '重新生成' : 'AI 生成') }}
            </button>
            @if (cl()?.content) {
              <button mat-button (click)="clEditing.set(!clEditing())">
                <mat-icon>{{ clEditing() ? 'visibility' : 'edit' }}</mat-icon>
                {{ clEditing() ? '预览' : '手动编辑' }}
              </button>
              @if (cl()?.status !== 'Final') {
                <button mat-button (click)="markCoverLetterFinal()">
                  <mat-icon>check_circle</mat-icon>标记为已确认
                </button>
              }
              <button mat-button (click)="deleteCoverLetter()">
                <mat-icon>delete_outline</mat-icon>删除
              </button>
            }
          </div>
        }

        <!-- 正文:编辑态 textarea / 预览态只读 -->
        @if (cl()?.content; as content) {
          @if (clEditing()) {
            <textarea matInput class="cl-editor" rows="16" [(ngModel)]="clDraft"></textarea>
            <div class="cl-actions save-row">
              <button mat-flat-button color="primary" (click)="saveCoverLetter()">保存</button>
              <button mat-button (click)="cancelEdit()">取消</button>
            </div>
          } @else {
            <div class="cl-preview">{{ content }}</div>
          }
        } @else if (!clGenerating()) {
          <p class="cl-empty">还没有求职信。点上面的按钮,基于你的简历与该岗位 JD 生成一封。</p>
        }
      </section>

      <!-- JD 全文(默认折叠:可达 40000 字符)-->
      @if (app.jdText) {
        <mat-divider></mat-divider>
        <section>
          <h4 class="fold-head" (click)="jdExpanded.set(!jdExpanded())">
            <mat-icon>{{ jdExpanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
            <span>JD 全文</span>
            <span class="fold-len">{{ app.jdText.length }} 字符</span>
          </h4>

          @if (app.jdSourceUrl) {
            <a [href]="app.jdSourceUrl" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>link</mat-icon>查看原始发布页
            </a>
          }

          @if (jdExpanded()) {
            <div class="jd-full">
              <p class="pre">{{ app.jdText }}</p>
            </div>
          } @else {
            <p class="pre jd-peek">{{ app.jdText.slice(0, 220) }}…</p>
          }
        </section>
      }

      <!-- 公司情报 -->
      @if (company()?.profile) {
        <mat-divider></mat-divider>
        <section>
          <h4>公司情报 · {{ company()?.name }}</h4>
          <p class="pre">{{ company()?.profile }}</p>
          @if (company()?.website) {
            <a [href]="company()!.website!" target="_blank" rel="noopener noreferrer" class="src-link">
              <mat-icon>public</mat-icon>{{ company()!.website }}
            </a>
          }
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
    .fold-head {
      display: flex; align-items: center; gap: 6px;
      cursor: pointer; user-select: none; margin-bottom: 8px;
    }
    .fold-head:hover { opacity: 0.75; }
    .fold-head mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .fold-len { margin-left: auto; font-size: 11.5px; opacity: 0.45; font-weight: 400; }
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
  `]
})
export class ApplicationDetailDialogComponent {
  readonly app = inject<Application>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);

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
    if (hasContent && !confirm('这会用新生成的内容覆盖当前求职信(包括你手改的部分)。继续?')) return;

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
        alert(readError(err));
      }
    });
  }

  saveCoverLetter(): void {
    const text = this.clDraft.trim();
    if (!text) { alert('求职信内容不能为空。'); return; }

    this.api.put<CoverLetterDto>(`/api/jobs/applications/${this.app.id}/cover-letter`, { content: text })
      .subscribe({
        next: (c) => { this.cl.set(c); this.clEditing.set(false); },
        error: (err) => alert(readError(err))
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
        error: (err) => alert(readError(err))
      });
  }

  deleteCoverLetter(): void {
    if (!confirm('删除这封求职信?此操作不可撤销。')) return;
    this.api.delete<void>(`/api/jobs/applications/${this.app.id}/cover-letter`)
      .subscribe({
        next: () => { this.cl.set(null); this.clDraft = ''; this.clEditing.set(false); },
        error: (err) => alert(readError(err))
      });
  }

  clStatusLabel(s: string): string {
    switch (s) {
      case 'Generated': return 'AI 生成';
      case 'Final': return '已确认';
      default: return '草稿';
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
    return STATUS_LABELS[s as ApplicationStatus] ?? s;
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
    if (score >= 70) return '强匹配';
    if (score >= 50) return '一般匹配';
    return '弱匹配';
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

/**
 * 从 HttpErrorResponse 里抠出人话错误。
 * 后端的 ProblemDetails 把说明放在 description(我们的 Error 记录映射过去的)。
 */
function readError(err: unknown): string {
  const e = err as { error?: { description?: string; detail?: string; title?: string }; message?: string };
  return e?.error?.description ?? e?.error?.detail ?? e?.error?.title
    ?? e?.message ?? '操作失败,请重试。';
}
