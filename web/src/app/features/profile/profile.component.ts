import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatSelectModule } from '@angular/material/select';
import { ApiClient } from '../../core/api/api-client';
import { PracticeApi, StorageSettingDto } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { AuthService } from '../../core/auth/auth.service';
import { AuthUser } from '../../core/models/api.models';

/** 一个权限分组 —— 权限字符串本身形如 "admin.users.read",按前缀分组比平铺一长串更好读。 */
interface PermissionGroup {
  prefix: string;
  label: string;
  permissions: string[];
}

/** 时区选项(IANA 名)。城市双语标注,不限于此列表 —— 已存值不在列表时动态补进。 */
interface TzOption { id: string; label: string; }

/**
 * 个人中心(M2.2)。
 *
 * 从只读资料页升级而来:显示名称编辑、界面语言/时区偏好(存到账号)、
 * 修改密码(成功后踢全部设备)、账号信息(注册/最近登录,按时区显示)。
 *
 * 数据来源刻意以 GET /api/auth/me 为准而不是直接读本地缓存的 AuthUser:
 * 本地缓存可能是几天前登录时写下的,权限被管理员调整后并不知情;
 * 每次进页面重新拉一次,保证"我看到的就是后端当前认的"。
 * displayName 等展示字段在接口失败时回退到 AuthService 的缓存,避免整页空白。
 */
@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink, MatCardModule, MatIconModule,
    MatButtonModule, MatChipsModule, MatProgressBarModule, MatTooltipModule,
    MatFormFieldModule, MatInputModule, MatSelectModule
  ],
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.scss'
})
export class ProfileComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** 录音存储目录的接口在 Assessment 服务上(不是 Identity),所以走练习页那套 API 层。 */
  private readonly practice = inject(PracticeApi);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  t = (key: string): string => this.i18n.t(key);
  /** 带占位符的词条(模板只能访问公开成员,所以包一层)。 */
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  /** 语言选项(i18n 是私有依赖,模板经此公开字段访问)。 */
  readonly langOptions = this.i18n.options;

  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly me = signal<AuthUser | null>(null);

  /** 接口失败时用本地缓存兜底 —— 用户至少还能看到自己是谁。 */
  readonly user = computed<AuthUser | null>(() => this.me() ?? this.auth.user());

  /**
   * 是否处于"接口失败"态。三态判定统一走这几个 computed,
   * 避免模板里散落 error() && !loading() 之类的组合条件写错。
   */
  readonly hasError = computed(() => !this.loading() && this.error() !== null);

  /** 接口失败且连本地缓存都没有 —— 无任何可展示数据,只能引导重新登录。 */
  readonly hasNothingToShow = computed(() => this.hasError() && this.user() === null);

  /**
   * 是否展示"正在用缓存兜底"的降级提示。
   * 失败 + 有缓存 = 页面还能看,但要明确告知不是最新的。
   * 失败 + 无缓存 = 走 hasNothingToShow 的兜底块,不再叠加这个提示。
   */
  readonly usedFallback = computed(() => this.hasError() && this.user() !== null);

  /** 成功加载且确实没有权限 —— 真正的空态,与"加载失败"不是一回事。 */
  readonly isEmptyPermissions = computed(
    () => !this.loading() && !this.hasError() && this.permissionCount() === 0
  );

  /** 成功加载且拿到了账号 —— 才渲染身份卡与权限清单。 */
  readonly hasUser = computed(() => !this.loading() && this.user() !== null);

  /** 管理员强制改密 → 顶部警示条。 */
  readonly mustChange = computed(() => this.user()?.mustChangePassword === true);

  readonly displayName = computed(() =>
    this.user()?.displayName || this.auth.displayName() || this.t('prof.unnamedUser')
  );

  readonly initials = computed(() => {
    const name = this.displayName().trim();
    const source = name.length > 0 ? name : (this.user()?.email ?? '?');
    return source.slice(0, 1).toUpperCase();
  });

  readonly avatarUrl = computed(() => this.user()?.avatarUrl || '');

  readonly permissionCount = computed(() => this.user()?.permissions?.length ?? 0);

  // ---------- 显示名称编辑 ----------

  readonly editingName = signal(false);
  readonly savingName = signal(false);
  nameDraft = '';

  // ---------- 偏好 ----------

  readonly savingPrefs = signal(false);
  langDraft = 'zh';
  tzDraft = 'America/Toronto';

  /** 我的简历(localStorage,备战材料生成时自动带入)。 */
  resumeDraft = '';
  resumeFileName = signal('');
  private readonly resumeKey = 'yi-my-resume';

  saveResume(): void {
    try {
      localStorage.setItem(this.resumeKey, this.resumeDraft);
      this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2500 });
    } catch {
      this.snack.open(this.t('profile.resumeSaveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  /** 供备战材料对话框调用:取存好的简历。 */
  static getSavedResume(): string {
    try {
      return localStorage.getItem('yi-my-resume') ?? '';
    } catch {
      return '';
    }
  }

  /** 上传简历文件:txt/md 直接读,PDF 用 pdf.js 提取文字。 */
  async onResumeFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.resumeFileName.set(file.name);

    try {
      const name = file.name.toLowerCase();
      if (name.endsWith('.txt') || name.endsWith('.md')) {
        this.resumeDraft = await file.text();
        this.snack.open(this.t('profile.resumeExtracted'), this.t('common.close'), { duration: 2500 });
      } else if (name.endsWith('.pdf')) {
        this.snack.open(this.t('profile.resumeExtracting'), this.t('common.close'), { duration: 2000 });
        const text = await this.extractPdfText(file);
        if (text.trim()) {
          this.resumeDraft = text;
          this.snack.open(this.t('profile.resumeExtracted'), this.t('common.close'), { duration: 2500 });
        } else {
          this.snack.open(this.t('profile.resumeExtractFailed'), this.t('common.close'), { duration: 4000 });
        }
      } else {
        // docx 等:提示复制粘贴
        this.snack.open(this.t('profile.resumeDocxHint'), this.t('common.close'), { duration: 5000 });
      }
    } catch {
      this.snack.open(this.t('profile.resumeExtractFailed'), this.t('common.close'), { duration: 4000 });
    }
    input.value = '';
  }

  private async extractPdfText(file: File): Promise<string> {
    const pdfjs = await import('pdfjs-dist');
    const buf = await file.arrayBuffer();
    const pdf = await (pdfjs as any).getDocument({ data: buf }).promise;
    const parts: string[] = [];
    for (let i = 1; i <= pdf.numPages; i++) {
      const page = await pdf.getPage(i);
      const content = await page.getTextContent();
      const line = (content.items as any[]).map((it: any) => it.str ?? '').join(' ');
      parts.push(line);
    }
    return parts.join('\n');
  }

  /** 常用时区。用户已存的值不在列表里时(历史数据/手动改库)动态补一项,不丢值。 */
  readonly tzOptions = computed<TzOption[]>(() => {
    const stored = this.user()?.timeZone;
    const base: TzOption[] = [
      { id: 'America/Toronto', label: this.t('prof.tzToronto') },
      { id: 'America/Vancouver', label: this.t('prof.tzVancouver') },
      { id: 'America/New_York', label: this.t('prof.tzNewYork') },
      { id: 'America/Los_Angeles', label: this.t('prof.tzLosAngeles') },
      { id: 'Europe/London', label: this.t('prof.tzLondon') },
      { id: 'Europe/Paris', label: this.t('prof.tzParis') },
      { id: 'Asia/Shanghai', label: this.t('prof.tzShanghai') },
      { id: 'Asia/Hong_Kong', label: this.t('prof.tzHongKong') },
      { id: 'Asia/Tokyo', label: this.t('prof.tzTokyo') },
      { id: 'UTC', label: 'UTC' }
    ];
    if (stored && !base.some((o) => o.id === stored)) {
      base.unshift({ id: stored, label: stored });
    }
    return base;
  });

  // ---------- 本机录音存储目录(★ 2026-09-27 Forrest) ----------

  /** 服务端判定的真实状态 —— 界面说的话必须和落盘位置一致,不允许自己猜。 */
  readonly storageStatus = signal<StorageSettingDto | null>(null);
  readonly storageBusy = signal(false);
  readonly storageMsg = signal<string | null>(null);

  /**
   * 预设位置 —— **选**而不是填(浏览器拿不到 Mac 的系统文件夹选择框,
   * 预设下拉是最接近"选择路径"的方式;特殊位置仍可走"自定义")。
   * `~` 由服务端展开:compose 会把 Mac 的家目录经 HOST_HOME 注入容器。
   */
  readonly storagePresetDocs = '~/Documents/your-interview/recordings';
  readonly storagePresetRepo = '~/dev/recordings';
  /** 下拉里"自定义路径"的哨兵值。 */
  readonly storageCustom = '__custom__';
  /** 下拉选中值(预设路径 或 __custom__)。默认即用户要求的 Documents 方案。 */
  storageSelect = '~/Documents/your-interview/recordings';
  /** 选择"自定义路径"时出现的输入框。 */
  storageDraft = '';

  readonly storageStatusKind = computed<'ok' | 'warn' | 'muted'>(() => {
    const s = this.storageStatus()?.status;
    if (s === 'CustomActive') return 'ok';
    if (s === 'Default') return 'muted';
    return 'warn';
  });

  readonly storageStatusIcon = computed(() => {
    const s = this.storageStatus()?.status;
    if (s === 'CustomActive') return 'check_circle';
    if (s === 'Default') return 'info';
    return 'warning_amber';
  });

  readonly storageStatusText = computed(() => {
    const st = this.storageStatus();
    if (!st) return this.t('profile.storageUnknown');
    let text: string;
    switch (st.status) {
      case 'CustomActive':
        text = this.tn('profile.storageActive', st.desiredPath ?? st.effectiveRoot);
        break;
      case 'CustomPendingMount':
        text = this.t('profile.storagePending');
        break;
      case 'CustomUnusable':
        text = this.t('profile.storageUnusable');
        break;
      default:
        text = this.tn('profile.storageDefault', st.hostDirectory ?? st.effectiveRoot);
    }
    return st.message ? `${text}（${st.message}）` : text;
  });

  /** 已有多少录音文件(从生效目录里数出来的,不是估计值)。 */
  readonly storageFilesText = computed(() => {
    const st = this.storageStatus();
    if (!st) return '';
    const mb = st.totalBytes / 1024 / 1024;
    // 空目录就是 0,不编一个"1 KB"出来 —— 数字必须和实际一致
    const size = st.totalBytes <= 0
      ? '0 KB'
      : (mb >= 1 ? `${mb.toFixed(1)} MB` : `${Math.max(1, Math.round(st.totalBytes / 1024))} KB`);
    return `${st.fileCount} · ${size}`;
  });

  /** 需要挂载时给的可直接复制的配置片段(改 .env + 重建容器)。优先用后端展开后的绝对路径。 */
  readonly storageMountSnippet = computed(() => {
    const path = this.storageStatus()?.desiredPath
      || this.storageDraft.trim()
      || this.storagePresetDocs;
    const head = path.startsWith('~')
      // Docker 不会把 ~ 展开成你 Mac 上的家目录 —— 必须写完整路径,否则挂载会失败
      ? [this.t('prof.mountPathComment'), '']
      : [];
    return [
      ...head,
      this.t('prof.mountEnvComment'),
      `RECORDINGS_HOST_DIR=${path}`,
      '',
      this.t('prof.mountReloadComment'),
      'docker compose up -d --force-recreate assessment'
    ].join('\n');
  });

  // ---------- 修改密码 ----------

  readonly pwdBusy = signal(false);
  /** 表单级错误(强度/两次不一致) —— 与页面级加载错误分开,互不覆盖。 */
  readonly pwdError = signal<string | null>(null);
  currentPwd = '';
  newPwd = '';
  confirmPwd = '';

  /**
   * 权限分组:按第一个 "." 之前的前缀切分。
   * 无前缀的散装权限统一归到"其他",不让它们消失。
   */
  readonly permissionGroups = computed<PermissionGroup[]>(() => {
    const list = this.user()?.permissions ?? [];
    const map = new Map<string, string[]>();

    for (const p of list) {
      const dot = p.indexOf('.');
      const prefix = dot > 0 ? p.slice(0, dot) : 'other';
      const bucket = map.get(prefix);
      if (bucket) bucket.push(p);
      else map.set(prefix, [p]);
    }

    return [...map.entries()]
      .map(([prefix, permissions]) => ({
        prefix,
        label: this.groupLabel(prefix),
        permissions: permissions.slice().sort()
      }))
      .sort((a, b) => a.prefix.localeCompare(b.prefix));
  });

  ngOnInit(): void {
    this.load();
    try {
      this.resumeDraft = localStorage.getItem(this.resumeKey) ?? '';
    } catch { /* 忽略 */ }
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<AuthUser>('/api/auth/me').subscribe({
      next: (u) => {
        this.me.set(u);
        this.syncDrafts(u);
        this.loading.set(false);
        // 存储目录是**另一个服务**的数据,单独拉:它失败不影响整页渲染。
        this.loadStorage();
      },
      // 失败不整页报错:本地缓存的 user 仍可展示,只在顶部提示"不是最新的"
      error: (e: Error) => {
        this.error.set(e.message || this.t('prof.errNetwork'));
        this.loading.set(false);
      }
    });
  }

  /** 表单初值随 /me 刷新(编辑中不打扰 —— 草稿只在非编辑态同步)。 */
  private syncDrafts(u: AuthUser): void {
    if (!this.editingName()) this.nameDraft = u.displayName;
    this.langDraft = u.preferredLanguage || this.i18n.lang();
    this.tzDraft = u.timeZone || 'America/Toronto';
  }

  // ---------- 显示名称 ----------

  startEditName(): void {
    this.nameDraft = this.user()?.displayName ?? '';
    this.editingName.set(true);
  }

  cancelEditName(): void {
    this.editingName.set(false);
  }

  saveName(): void {
    const name = this.nameDraft.trim();
    if (!name || this.savingName()) return;
    this.savingName.set(true);
    this.auth.saveProfile({ displayName: name }).subscribe({
      next: (u) => {
        this.me.set(u);
        this.savingName.set(false);
        this.editingName.set(false);
      },
      error: () => this.savingName.set(false)
    });
  }

  // ---------- 偏好 ----------

  savePrefs(): void {
    if (this.savingPrefs()) return;
    this.savingPrefs.set(true);
    this.auth.saveProfile({ preferredLanguage: this.langDraft, timeZone: this.tzDraft })
      .subscribe({
        next: (u) => {
          this.me.set(u);
          this.savingPrefs.set(false);
        },
        error: () => this.savingPrefs.set(false)
      });
  }

  // ---------- 本机录音存储目录 ----------

  private loadStorage(): void {
    this.practice.getStorageSettings().subscribe({
      next: (s) => {
        this.storageStatus.set(s);
        this.applyDesiredPath(s.desiredPath);
      },
      // 读不到就当"未自定义":页面其余部分照常工作,不因此整页报错
      error: () => this.storageStatus.set(null)
    });
  }

  /** 后端存的是展开后的绝对路径;预设命中就回到下拉项,否则落到"自定义"。 */
  private applyDesiredPath(desired: string | null): void {
    if (!desired) {
      this.storageSelect = this.storagePresetDocs;
      this.storageDraft = '';
      return;
    }
    if (desired === this.storagePresetDocs || desired === this.storagePresetRepo) {
      this.storageSelect = desired;
      this.storageDraft = '';
    } else {
      this.storageSelect = this.storageCustom;
      this.storageDraft = desired;
    }
  }

  /**
   * 保存所选路径。
   * 后端会真去探测这个目录能不能写;不能写也会把路径存下来,
   * 但状态如实标成"待挂载",并回显需要的挂载配置 —— 不会假装成功。
   */
  saveStoragePath(): void {
    if (this.storageBusy()) return;
    this.storageBusy.set(true);
    this.storageMsg.set(null);

    const raw = (this.storageSelect === this.storageCustom
      ? this.storageDraft
      : this.storageSelect).trim();

    this.practice.saveStorageSettings(raw.length > 0 ? raw : null).subscribe({
      next: (s) => {
        this.storageStatus.set(s);
        this.applyDesiredPath(s.desiredPath);
        this.storageBusy.set(false);
        this.storageMsg.set(this.t('profile.storageSaved'));
      },
      error: (e: Error) => {
        this.storageBusy.set(false);
        this.storageMsg.set(e.message || this.t('profile.storageSaveFail'));
      }
    });
  }

  /** 把已有录音搬到新目录(相对路径不变,所以数据库记录不用动)。 */
  migrateStorageFiles(): void {
    if (this.storageBusy()) return;
    this.storageBusy.set(true);
    this.storageMsg.set(null);

    this.practice.migrateStorage().subscribe({
      next: (r) => {
        this.storageBusy.set(false);
        this.storageMsg.set(this.tn('profile.storageMigrated', r.moved));
        this.loadStorage();
      },
      error: (e: Error) => {
        this.storageBusy.set(false);
        this.storageMsg.set(e.message || this.t('profile.storageMigrateFail'));
      }
    });
  }

  copyMountSnippet(): void {
    const text = this.storageMountSnippet();
    navigator.clipboard?.writeText(text).then(
      () => this.storageMsg.set(this.t('profile.storageCopied')),
      () => this.storageMsg.set(text)
    );
  }

  // ---------- 修改密码 ----------

  /** 与后端 ChangePasswordCommandValidator 同一套强度规则,前端先挡一道。 */
  private pwdStrong(p: string): boolean {
    return p.length >= 12
      && /[A-Z]/.test(p) && /[a-z]/.test(p)
      && /[0-9]/.test(p) && /[^a-zA-Z0-9]/.test(p);
  }

  changePassword(): void {
    if (this.pwdBusy()) return;
    this.pwdError.set(null);

    if (!this.currentPwd) {
      this.pwdError.set(this.t('profile.currentPwd'));
      return;
    }
    if (!this.pwdStrong(this.newPwd)) {
      this.pwdError.set(this.t('profile.pwdWeak'));
      return;
    }
    if (this.newPwd === this.currentPwd) {
      this.pwdError.set(this.t('profile.pwdSame'));
      return;
    }
    if (this.newPwd !== this.confirmPwd) {
      this.pwdError.set(this.t('profile.pwdMismatch'));
      return;
    }

    this.pwdBusy.set(true);
    this.api.post('/api/auth/change-password', {
      currentPassword: this.currentPwd,
      newPassword: this.newPwd
    }).subscribe({
      next: () => {
        // 后端已撤销全部刷新令牌(踢掉所有设备)—— 本地立即清会话,
        // 引导重新登录。比等 access token 自然过期(期间还会被当成已登录)
        // 再莫名被踢,诚实且清晰得多。
        this.auth.clearSession();
        this.router.navigate(['/login'], {
          queryParams: { notice: this.t('profile.pwdChanged'), returnUrl: '/profile' }
        });
      },
      error: (e: Error) => {
        // 后端拒绝:当前密码不正确 / 强度不达标 —— 原样呈现给用户
        this.pwdError.set(e.message || this.t('profile.pwdWeak'));
        this.pwdBusy.set(false);
      }
    });
  }

  // ---------- 展示 ----------

  /** 按账号所选时区格式化时间(IANA 时区 + 当前界面语言)。 */
  formatInZone(iso?: string | null): string {
    if (!iso) return '—';
    const tz = this.user()?.timeZone || 'America/Toronto';
    try {
      return new Intl.DateTimeFormat(this.i18n.locale(), {
        timeZone: tz,
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZoneName: 'short'
      }).format(new Date(iso));
    } catch {
      // 时区串非法(脏数据)→ 退回浏览器本地时区,绝不让日期渲染炸掉整页
      return new Date(iso).toLocaleString(this.i18n.locale());
    }
  }

  /** 权限前缀 → 中文分组名。目的只是让分组有个人话标题,未收录的前缀直接显示原文。 */
  private groupLabel(prefix: string): string {
    const keyMap: Record<string, string> = {
      admin: 'prof.permGroupAdmin',
      jobs: 'prof.permGroupJobs',
      interviews: 'prof.permGroupInterviews',
      knowledge: 'prof.permGroupKnowledge',
      mock: 'prof.permGroupMock',
      analytics: 'prof.permGroupAnalytics',
      profile: 'prof.permGroupProfile',
      other: 'prof.permGroupOther'
    };
    const key = keyMap[prefix];
    return key ? this.t(key) : prefix;
  }
}
