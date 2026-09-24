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
import { MatSelectModule } from '@angular/material/select';
import { ApiClient } from '../../core/api/api-client';
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
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  private readonly router = inject(Router);
  t = (key: string): string => this.i18n.t(key);
  /** 带占位符的词条(模板只能访问公开成员,所以包一层)。 */
  tn = (key: string, n: string | number): string => this.i18n.tn(key, n);
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
    this.user()?.displayName || this.auth.displayName() || '未命名用户'
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

  /** 常用时区。用户已存的值不在列表里时(历史数据/手动改库)动态补一项,不丢值。 */
  readonly tzOptions = computed<TzOption[]>(() => {
    const stored = this.user()?.timeZone;
    const base: TzOption[] = [
      { id: 'America/Toronto', label: '多伦多 Toronto' },
      { id: 'America/Vancouver', label: '温哥华 Vancouver' },
      { id: 'America/New_York', label: '纽约 New York' },
      { id: 'America/Los_Angeles', label: '洛杉矶 Los Angeles' },
      { id: 'Europe/London', label: '伦敦 London' },
      { id: 'Europe/Paris', label: '巴黎 Paris' },
      { id: 'Asia/Shanghai', label: '上海 Shanghai' },
      { id: 'Asia/Hong_Kong', label: '香港 Hong Kong' },
      { id: 'Asia/Tokyo', label: '东京 Tokyo' },
      { id: 'UTC', label: 'UTC' }
    ];
    if (stored && !base.some((o) => o.id === stored)) {
      base.unshift({ id: stored, label: stored });
    }
    return base;
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
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<AuthUser>('/api/auth/me').subscribe({
      next: (u) => {
        this.me.set(u);
        this.syncDrafts(u);
        this.loading.set(false);
      },
      // 失败不整页报错:本地缓存的 user 仍可展示,只在顶部提示"不是最新的"
      error: (e: Error) => {
        this.error.set(e.message || '网络异常,请稍后重试');
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
    const known: Record<string, string> = {
      admin: '管理后台',
      jobs: '投递跟踪',
      interviews: '实战机经',
      knowledge: '技术栈',
      mock: 'AI 模拟',
      analytics: '数据分析',
      profile: '个人资料',
      other: '其他权限'
    };
    return known[prefix] ?? prefix;
  }
}
