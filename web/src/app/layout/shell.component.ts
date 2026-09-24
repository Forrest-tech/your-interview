import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLinkActive, RouterLink, RouterOutlet } from '@angular/router';
import { catchError, of } from 'rxjs';
import { filter, map } from 'rxjs/operators';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { MatDividerModule } from '@angular/material/divider';
import { AuthService } from '../core/auth/auth.service';
import { I18nService, Lang } from '../core/i18n/i18n.service';
import { SubnavItem, SubnavService } from './subnav.service';

export interface NavItem {
  path: string;
  labelKey: string;
  icon: string;
  permission?: string;
}

/**
 * 应用外壳:顶部品牌 + 横向主导航 + 语言切换 + 账户。
 * 左侧竖栏只承载"当前页面自己的功能"(页面级子导航),不再是全局导航。
 *
 * 2026-09-15 调整(Forrest 要求):品牌位从纯文字改几何图标 + 词标;
 * 账户区不再显示 "System Administrator" 文字,只留头像图标 + 下拉。
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink, RouterLinkActive, RouterOutlet,
    MatToolbarModule, MatIconModule, MatButtonModule, MatTooltipModule,
    MatMenuModule, MatDividerModule
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss'
})
export class ShellComponent {
  private readonly router = inject(Router);
  private readonly subnavSvc = inject(SubnavService);
  readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  /** 左侧栏折叠状态。 */
  readonly collapsed = signal(false);

  private readonly allNav: NavItem[] = [
    { path: '/dashboard',  labelKey: 'nav.dashboard',  icon: 'insights' },
    { path: '/tracker',    labelKey: 'nav.tracker',    icon: 'assignment' },
    { path: '/playbook',   labelKey: 'nav.playbook',   icon: 'menu_book' },
    { path: '/tech-stack', labelKey: 'nav.techstack',  icon: 'school' },
    { path: '/practice',   labelKey: 'nav.practice',   icon: 'model_training' },
    { path: '/mock',       labelKey: 'nav.mock',       icon: 'record_voice_over' },
    { path: '/analytics',  labelKey: 'nav.analytics',  icon: 'query_stats' },
  ];

  /** 按权限过滤后的导航项 —— 没权限的模块根本不显示,不靠点了才报 403。 */
  readonly nav = computed(() =>
    this.allNav.filter((i) => !i.permission || this.auth.can(i.permission))
  );

  /** 当前 URL,用于解析左侧子导航。 */
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects)
    ),
    { initialValue: this.router.url }
  );

  /** 左侧子导航:由当前路由决定,页面自己也可以注册。 */
  readonly subnav = computed<SubnavItem[]>(() => this.subnavSvc.resolve(this.url()));

  /**
   * ★ 第三十一轮(Forrest):/practice 页隐藏全局页脚。
   *
   * 为什么用路由判断而不是在组件里改 DOM:
   *   页脚属于 shell 布局(App shell),页面组件无权也不应该去操控它。
   *   由 shell 根据当前 URL 决定是否渲染,是唯一不会留下残余状态的正确做法
   *   —— 组件里手动隐藏会在离开页面时忘记恢复(经典错误)。
   *
   * 用 startsWith 而不是 === :/practice 及其子路径(如 /practice?x=1 已由
   * urlAfterRedirects 去掉 query)统一对待,避免日后加子路由时页脚又冒出来。
   */
  readonly showFooter = computed(() => !this.url().startsWith('/practice'));

  /** 头像占位字母。 */
  readonly initial = computed(() => {
    const n = this.auth.displayName() || '?';
    return n.trim().charAt(0).toUpperCase();
  });

  /** 头像图片地址(Google 登录用户);空则顶栏继续用首字母。 */
  readonly avatarUrl = computed(() => this.auth.user()?.avatarUrl || '');

  /** 账户菜单里的角色文案(替代原来常驻顶栏的 "System Administrator")。 */
  readonly roleText = computed(() => {
    const roles = this.auth.user()?.roles ?? [];
    if (!roles.length) return '';
    return roles
      .map((r) => r.replace(/[_.]/g, ' '))
      .join(' · ');
  });

  t(key: string): string {
    return this.i18n.t(key);
  }

  /**
   * 切语言(M2.2):本地立即生效,同时把选择记到账号 ——
   * 下次登录(包括换设备/换浏览器)自动恢复。保存失败不打扰用户:
   * 语言切换本身已经成功了,只是"记住"这一步没成,静默即可。
   */
  switchLang(code: Lang): void {
    this.i18n.setLang(code);
    this.auth.saveProfile({ preferredLanguage: code })
      .pipe(catchError(() => of(null)))
      .subscribe();
  }

  toggleSidebar(): void {
    this.collapsed.set(!this.collapsed());
  }

  goProfile(): void {
    this.router.navigate(['/profile']);
  }

  goAdmin(): void {
    this.router.navigate(['/admin']);
  }

  logout(): void {
    this.auth.logout();
  }
}
