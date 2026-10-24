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
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { DragDropModule, CdkDragDrop } from '@angular/cdk/drag-drop';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { MatDividerModule } from '@angular/material/divider';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { catchError, concat, forkJoin, of, type Observable } from 'rxjs';
import { JobsApiService } from '../../core/api/jobs-api.service';
import { InterviewsApiService } from '../../core/api/interviews-api.service';
import { KnowledgeApiService } from '../../core/api/knowledge-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { SubnavService } from '../../layout/subnav.service';
import { TrackerApi } from '../../core/api/tracker-api.service';
import { TrackerPreferencesService } from '../../core/api/tracker-preferences.service';
import { Application, ApplicationStatus, Company, TrackerStats } from '../../core/models/api.models';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { InputDialogComponent, InputDialogData } from '../../shared/input-dialog/input-dialog.component';
import { parseWeeklyGoalInput, clampWeeklyGoal } from './tracker-goal.helpers';
import { AnswerLibraryDialogComponent } from './answer-library-dialog.component';
import { SmartAddDialogComponent } from './smart-add-dialog.component';
import { STATUS_ORDER, STATUS_PATH_FROM_SAVED, ApplicationForm } from './tracker-form.helpers';
import { ApplicationDialogComponent } from './application-dialog.component';
import { ApplicationDetailDialogComponent } from './application-detail-dialog.component';
import { PrepPlanDialogComponent } from './prep-plan-dialog.component';
import {
  favCountsByStatus, favoriteItems, itemsAtStatus, nextFlowStageSelection
} from './tracker-flow-favorites.helpers';

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
    MatSelectModule, MatMenuModule, MatButtonToggleModule, MatCheckboxModule,
    DragDropModule
  ],
  templateUrl: './tracker.component.html',
  styleUrl: './tracker.component.scss'
})
export class TrackerComponent implements OnInit, OnDestroy, AfterViewInit {
  private readonly jobsApi = inject(JobsApiService);
  private readonly interviewsApi = inject(InterviewsApiService);
  private readonly knowledgeApi = inject(KnowledgeApiService);
  private readonly trackerApi = inject(TrackerApi);
  private readonly prefs = inject(TrackerPreferencesService);
  private readonly router = inject(Router);
  private readonly subnav = inject(SubnavService);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  tf = (key: string, params: Record<string, string | number | null | undefined>): string => this.i18n.tf(key, params);

  /** 看板/列表/图表视图切换(Simplify 模式)。默认 list,刷新后从 localStorage 恢复。 */
  readonly viewMode = signal<'board' | 'list' | 'chart'>(
    ((): 'board' | 'list' | 'chart' => {
      try {
        const v = localStorage.getItem('yi-tracker-view');
        return v === 'board' ? 'board' : v === 'chart' ? 'chart' : 'list';
      } catch { return 'list'; }
    })()
  );

  setViewMode(m: 'board' | 'list' | 'chart'): void {
    this.viewMode.set(m);
    // ★ 票73:切走流程视图后条带恢复默认态(再点节点选中口径见 selectFlowStage);
    // 进入流程视图时刷新全量缓存(徽标/条带的全量口径)。
    if (m !== 'chart') this.flowStage.set(null);
    if (m === 'chart') this.loadFlowAll();
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
    // 2026-10-19:去掉 clearbit 外部 logo，直接用首字母头像（避免控制台报错）
    return app.companyLogoUrl || null;
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
    this.jobsApi.changeStatus(app.id, status).subscribe({
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
        // FUN-03:必须带 jobApplicationId —— 后端按它幂等,已有条目时返回既有 id,
        // 前端直接跳既有条目;此前这条路径没带,每次改状态到 Interview 都新建第二条。
        this.interviewsApi.createEntry<{ id: string }>({
          companyName: app.companyName,
          role: app.role,
          jobApplicationId: app.id,
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
      const apps = await this.jobsApi.getApplications<Application>({
        page: 1, pageSize: 1000
      }).toPromise();
      data['applications'] = apps?.items ?? [];
    } catch { data['applications'] = []; }
    // 知识库
    try {
      const kb = await this.knowledgeApi.getItems({
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
    this.snack.open(this.t('tracker.backupDownloaded'), this.t('common.close'), { duration: 2500 });
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
        this.snack.open(this.tn('tracker.backupRestored', count), this.t('common.close'), { duration: 3000 });
      } catch {
        this.snack.open(this.t('tracker.backupParseFailed'), this.t('common.close'), { duration: 3000 });
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
          this.jobsApi.createApplication(body).subscribe({
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
    this.interviewsApi.createEntry<{ id: string }>({
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
  /** ★ 收藏(COD-10a/票14:后端为真源,与详情弹窗共用同一份信号 —— 单一读写路径)。 */
  readonly favorites = this.prefs.favorites;

  toggleFavorite(id: string): void {
    this.prefs.toggleFavorite(id);
  }

  isFavorite(id: string): boolean {
    return this.prefs.isFavorite(id);
  }

  /** ★ 批量操作:选中的申请 id 集合。 */
  readonly selected = signal<Set<string>>(new Set());
  readonly selectedCount = computed(() => this.selected().size);

  /** ★ 选择模式:默认关闭,卡片不显示勾选框;点工具栏"选择"进入。 */
  readonly selectMode = signal(false);

  toggleSelectMode(): void {
    const next = !this.selectMode();
    this.selectMode.set(next);
    if (!next) this.clearSelection();
  }

  toggleSelect(id: string, ev?: Event | any): void {
    // MatCheckboxChange 没有 stopPropagation,直接忽略
    try { (ev as Event)?.stopPropagation?.(); } catch {}
    const next = new Set(this.selected());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.selected.set(next);
  }

  isSelected(id: string): boolean {
    return this.selected().has(id);
  }

  clearSelection(): void {
    this.selected.set(new Set());
  }

  /** 全选当前页。 */
  selectAllVisible(): void {
    const next = new Set(this.selected());
    for (const a of this.displayItems()) next.add(a.id);
    this.selected.set(next);
  }

  /** 批量改状态:逐个调 /status(走状态机校验),失败的单独 toast。 */
  bulkSetStatus(status: ApplicationStatus): void {
    const ids = [...this.selected()];
    if (ids.length === 0) return;
    let done = 0, failed = 0;
    for (const id of ids) {
      this.jobsApi.changeStatus(id, status).subscribe({
        next: () => {
          done++;
          if (done + failed === ids.length) this.finishBulk(done, failed, 'status');
        },
        error: () => {
          failed++;
          if (done + failed === ids.length) this.finishBulk(done, failed, 'status');
        }
      });
    }
  }

  /** 批量归档。 */
  bulkArchive(): void {
    const ids = [...this.selected()];
    if (ids.length === 0) return;
    let done = 0, failed = 0;
    for (const id of ids) {
      this.jobsApi.archive(id).subscribe({
        next: () => {
          done++;
          if (done + failed === ids.length) this.finishBulk(done, failed, 'archive');
        },
        error: () => {
          failed++;
          if (done + failed === ids.length) this.finishBulk(done, failed, 'archive');
        }
      });
    }
  }

  /** ★ F-02:删除确认统一走全站确认弹窗 —— 原生 confirm 在无弹窗处理的环境
   *  (自动化测试/某些内嵌浏览器)会被静默 dismiss,删除看起来"点了没反应"。 */
  private confirmDelete(body: string): Promise<boolean> {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.delete'), body,
        confirmText: this.t('common.delete'), cancelText: this.t('common.cancel'),
        danger: true,
      } as ConfirmDialogData,
    });
    return new Promise((resolve) => ref.afterClosed().subscribe((v) => resolve(v === true)));
  }

  /** 批量删除:二次确认。 */
  bulkDelete(): void {
    const n = this.selected().size;
    if (n === 0) return;
    void this.confirmDelete(this.tf('tracker.confirmBulkDelete', { n })).then((ok) => {
      if (!ok) return;
      const ids = [...this.selected()];
      let done = 0, failed = 0;
      for (const id of ids) {
        this.jobsApi.deleteApplication(id,
          this.items().find((a) => a.id === id)?.version ?? null).subscribe({
          next: () => {
            done++;
            if (done + failed === ids.length) this.finishBulk(done, failed, 'delete');
          },
          error: () => {
            failed++;
            if (done + failed === ids.length) this.finishBulk(done, failed, 'delete');
          }
        });
      }
    });
  }

  private finishBulk(done: number, failed: number, _op: string): void {
    this.clearSelection();
    this.selectMode.set(false);
    this.reload();
    if (failed > 0) {
      this.notify(this.tf('tracker.bulkPartial', { done, failed }), true);
    } else {
      this.notify(this.tn('tracker.bulkDone', done));
    }
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
  readonly statActive = computed(() => this.stats()?.active ?? 0);
  readonly statInterview = computed(() => this.stats()?.inInterview ?? 0);
  readonly statOffer = computed(() => this.stats()?.offers ?? 0);

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

  /** 点击每周目标卡片:站内输入弹窗改数字(票45 REQ-14,替代原生 prompt ——
   *  与 F-02 同理,原生 prompt 在无弹窗处理的环境会被静默 dismiss)。
   *  解析/校验口径逐字不变:取消/空/非数字/≤0 静默不改,>100 由 setWeeklyGoal 钳到 100。 */
  promptGoal(): void {
    const ref = this.dialog.open(InputDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('tracker.goalTitle'), label: this.i18n.t('tracker.goalPrompt'),
        initialValue: String(this.weeklyGoal()),
        confirmText: this.t('common.save'), cancelText: this.t('common.cancel'),
      } as InputDialogData,
    });
    ref.afterClosed().subscribe((raw) => {
      const n = parseWeeklyGoalInput(raw);
      if (n !== null) this.setWeeklyGoal(n);
    });
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
    const v = clampWeeklyGoal(n);
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
      this.jobsApi.getApplications<Application>({
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
  /** 2026-10-19:用户点"不再提示"后永久隐藏 ghosting 横幅（localStorage）。 */
  readonly ghostingDismissed = signal(localStorage.getItem('yi.ghostingDismissed') === '1');
  dismissGhostingForever(): void {
    localStorage.setItem('yi.ghostingDismissed', '1');
    this.ghostingDismissed.set(true);
  }
  loadGhosting(): void {
    this.jobsApi.getApplications<Application>({
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
  /** ★ G-06: Sankey 流程图数据 —— 各阶段节点 + 流向边的宽度。 */
  readonly flowChart = computed(() => {
    const by = this.stats()?.byStatus ?? {};
    // 主流程:Saved → Applied → Screen → Interview → Offer → Accepted
    const stages: ApplicationStatus[] = ['Saved', 'Applied', 'Screen', 'Interview', 'Offer', 'Accepted'];
    const counts = stages.map(s => by[s] ?? 0);
    const max = Math.max(1, ...counts);
    const rejected = by['Rejected'] ?? 0;
    const ghosted = by['Ghosted'] ?? 0;
    return {
      nodes: stages.map((s, i) => ({
        status: s,
        label: this.i18n.t('status.' + s),
        count: counts[i],
        // 节点高度按数量比例,最小 24px
        height: Math.max(24, Math.round((counts[i] / max) * 120)),
        x: i * 160,
      })),
      // 边:从上一阶段流向下一阶段,宽度按目标阶段数量
      edges: stages.slice(1).map((s, i) => ({
        from: i, to: i + 1,
        count: counts[i + 1],
        width: Math.max(2, Math.round((counts[i + 1] / max) * 40)),
      })),
      rejected, ghosted,
      total: counts.reduce((a, b) => a + b, 0) + rejected + ghosted,
    };
  });

  // ------------------------------ 票73:流程视图收藏 ------------------------------
  /**
   * 流程图是聚合图(stats 口径),徽标与条带需要投递全量:items() 只是分页
   * 切片(同 weeklyAppliedCount 的教训),故进流程视图时逐页拉全量缓存。
   * 收藏真源仍只有 prefs.favorites 一份信号 —— 切换后徽标/条带经下面的
   * computed 即时同步,缓存本身不随切换刷新(状态变更不在流程视图发生)。
   */
  readonly flowAllItems = signal<Application[]>([]);
  /** 条带选中的阶段;null = 默认条带「收藏的投递」。 */
  readonly flowStage = signal<ApplicationStatus | null>(null);
  readonly flowFavCounts = computed(() =>
    favCountsByStatus(this.flowAllItems(), this.prefs.favorites()));
  readonly flowStripItems = computed(() => {
    const stage = this.flowStage();
    return stage
      ? itemsAtStatus(this.flowAllItems(), stage)
      : favoriteItems(this.flowAllItems(), this.prefs.favorites());
  });

  flowFavCount(status: ApplicationStatus): number {
    return this.flowFavCounts().get(status) ?? 0;
  }

  /** 点阶段节点:选中 ↔ 取消(回默认条带)↔ 切换阶段。 */
  selectFlowStage(status: ApplicationStatus): void {
    this.flowStage.update(cur => nextFlowStageSelection(cur, status) as ApplicationStatus | null);
  }

  private loadFlowAll(): void {
    const pageSize = 200;
    const fetchPage = (page: number, acc: Application[]): void => {
      this.jobsApi.getApplications<Application>({
        page, pageSize, sortBy: 'updated', archived: false
      }).subscribe({
        next: (r) => {
          const all = acc.concat(r.items ?? []);
          if (page < (r.totalPages ?? 1)) fetchPage(page + 1, all);
          else this.flowAllItems.set(all);
        },
        error: () => { /* 失败保留旧缓存,主流程不受影响 */ }
      });
    };
    fetchPage(1, []);
  }

  ngOnInit(): void {
    this.reload();
    this.prefs.ensureFavoritesLoaded(); // ★ 票14:收藏读后端 + 本地旧数据一次性导入
    if (this.viewMode() === 'chart') this.loadFlowAll(); // ★ 票73:刷新后直落流程视图时也要全量缓存
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

    this.jobsApi.getApplications<Application>({
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
    (this.jobsApi.getStats() as unknown as Observable<TrackerStats>)
      .pipe(catchError(() => of(null)))
      .subscribe((s) => this.stats.set(s));
  }

  reload(): void {
    this.load();
    this.loadStats();
    this.loadWeeklyApplied();
  }

  // ------------------------------ 筛选交互 ------------------------------

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  onSearchInput(value: string): void {
    this.search.set(value);
    this.page.set(1);
    // ★ F-05:输入即筛选(300ms 防抖)—— 旧实现只更新信号、从不发请求,
    //   用户打完整公司名列表也纹丝不动,只能靠回车(且焦点不在框内就无效)。
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.load(), 300);
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

  /** 当前是否有搜索/筛选生效（空态文案区分"真没数据"与"被筛掉了"）。 */
  filtersActive(): boolean {
    return !!this.search().trim() || !!this.statusFilter() || this.showStaleOnly() || this.favOnly();
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
      if (app) this.update(app, form);
      else this.create(form);
    });
  }

  /** ★ #6:文档版本存入 notes 前缀 [DOCS:...] —— 建单与编辑共用同一口径。 */
  private composeNotes(form: ApplicationForm): string | null {
    return form.sentDocuments?.trim()
      ? `[DOCS:${form.sentDocuments.trim()}] ${form.notes || ''}`.trim()
      : (form.notes || null);
  }

  private create(form: ApplicationForm): void {
    // 先按公司名解析/创建公司(幂等),拿到 companyId 再建投递 —— 否则后端会因缺 CompanyId 拒收。
    this.trackerApi.resolveCompany(form.companyName.trim()).subscribe({
      next: (c) => {
        this.jobsApi.createApplication<Application>({
          companyId: c.id,
          role: form.role,
          location: form.location || null,
          link: form.link || null,
          salary: form.salary || null,
          workMode: null,
          source: null,
          jdSummary: null,
          priority: form.priority || null,
          appliedDate: form.appliedDate || null,
          // FUN-02:手动建单填的 JD 与备注随建单负载一并提交 —— 此前根本不发,
          // 保存后抽屉恒显示 "No JD yet"、API jdText/notes 为空。
          jdText: form.jdText?.trim() || null,
          notes: this.composeNotes(form)
        }).subscribe({
          next: (app) => this.afterCreate(app, form),
          error: (e: Error) => this.notify(e.message, true)
        });
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  /** 建完投递后,补齐初始状态(走合法路径逐步流转)/外联留言,再刷新看板。
   *  备注与 JD 已随 create 负载落库 (FUN-02),不再补发全量 PUT —— 旧做法只在
   *  form.notes 非空时才发,且新建返回对象只有 id、全量 PUT 反而可能误清字段。 */
  private afterCreate(app: Application, form: ApplicationForm): void {
    const tasks: Observable<unknown>[] = [];
    const path = STATUS_PATH_FROM_SAVED[form.status] ?? [];
    if (path.length > 0) {
      // 按路径逐步流转(如 Saved→Applied→Screen→Interview),每步都是后端允许的合法流转
      const steps$: Observable<unknown>[] = path.map((s) =>
        this.jobsApi.changeStatus(app.id, s, this.t('tracker.historyCreated')).pipe(catchError(() => of(null)))
      );
      tasks.push(concat(...steps$));
    }
    if (form.needsConnectFirst || form.outreachMessage) {
      tasks.push(this.jobsApi.updateOutreach(app.id,
        { posterName: null, needsConnectFirst: form.needsConnectFirst, message: form.outreachMessage || null })
        .pipe(catchError(() => of(null))));
    }

    const done = () => { this.notify(this.t('tracker.created')); this.reload(); };
    if (tasks.length === 0) { done(); return; }
    forkJoin(tasks).subscribe({ next: done, error: done });
  }

  private update(existing: Application, form: ApplicationForm): void {
    const id = existing.id;
    const tasks: Observable<unknown>[] = [
      this.updateAppPayload(existing, form).pipe(catchError((e: Error) => { this.notify(e.message, true); return of(null); }))
    ];
    if (form.needsConnectFirst || form.outreachMessage) {
      tasks.push(this.jobsApi.updateOutreach(id,
        { posterName: null, needsConnectFirst: form.needsConnectFirst, message: form.outreachMessage || null })
        .pipe(catchError(() => of(null))));
    }
    const done = () => { this.notify(this.t('tracker.saved')); this.reload(); };
    forkJoin(tasks).subscribe({ next: done, error: done });
  }

  /**
   * 把表单字段拼成 UpdateApplication 所需的完整负载(后端要求全量)。
   * FUN-02:表单有的字段带真实值 (含 jdText —— 此前根本不发,编辑填的 JD 静默丢失);
   * 表单没有的字段 (workMode/source/jdSummary/deadline 及详情长字段) 从现有投递
   * 回填 —— 硬编码 null 在全量 PUT 下会把已有值误清。
   */
  private updateAppPayload(existing: Application, form: ApplicationForm): Observable<unknown> {
    return this.jobsApi.updateApplication(existing.id, {
      role: form.role,
      location: form.location || null,
      link: form.link || null,
      salary: form.salary || null,
      workMode: existing.workMode ?? null,
      source: existing.source ?? null,
      jdSummary: existing.jdSummary ?? null,
      notes: this.composeNotes(form),
      priority: form.priority || null,
      appliedDate: form.appliedDate || null,
      deadline: existing.deadline ?? null,
      // 始终发送 (空串 = 用户清空 JD,后端归一为 null;不发则后端保持原值)
      jdText: form.jdText?.trim() ?? '',
      companyDescription: existing.companyDescription ?? null,
      workAuthorization: existing.workAuthorization ?? null,
      requirements: existing.requirements ?? null,
      benefits: existing.benefits ?? null,
      version: existing.version ?? null
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
    void this.confirmDelete(this.tf('tracker.confirmDelete', { a: this.tf('tracker.confirmDeleteApp', { company: app.companyName, role: app.role }) })).then((ok) => {
      if (!ok) return;
      this.jobsApi.deleteApplication(app.id, app.version ?? null).subscribe({
        next: () => {
          this.notify(this.t('tracker.deleted'));
          this.reload();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  /** ★ 归档 (Simplify 对等)。 */
  archive(app: Application, ev?: Event): void {
    ev?.stopPropagation();
    this.jobsApi.archive(app.id).subscribe({
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
    this.jobsApi.unarchive(app.id).subscribe({
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
    // 高度由 CSS .detail-drawer-panel 控制为 100vh
    const ref = this.dialog.open(ApplicationDetailDialogComponent, {
      data: app,
      width: '66vw',
      maxWidth: '1280px',
      minWidth: '900px',
      position: { right: '0', top: '0' },
      panelClass: 'detail-drawer-panel',
      autoFocus: false
    });
    // 删除后关闭抽屉时刷新列表
    ref.afterClosed().subscribe((deleted) => {
      if (deleted) this.reload();
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
