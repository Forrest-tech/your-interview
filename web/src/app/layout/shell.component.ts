import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive, RouterOutlet, Router } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatDividerModule } from '@angular/material/divider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AuthService } from '../core/auth/auth.service';

interface NavItem {
  path: string;
  label: string;
  labelEn: string;
  icon: string;
  permission?: string;
}

/**
 * 主框架:顶栏 + 侧边导航 + 内容区。
 *
 * 导航结构参考了 ynwac / simplify / hellointerview 的共同做法:
 * 顶部一条细工具条(品牌 + 账户),左侧是模块列表,内容区留白充足。
 * 这样模块数量增长时不用改结构,只加一项。
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [
    CommonModule, RouterOutlet, RouterLink, RouterLinkActive,
    MatToolbarModule, MatButtonModule, MatIconModule, MatMenuModule,
    MatDividerModule, MatTooltipModule
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss'
})
export class ShellComponent {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly collapsed = signal(false);

  /** 全部模块。permission 为空 = 登录即可见。 */
  private readonly allNav: NavItem[] = [
    { path: '/dashboard',    label: '总览',      labelEn: 'Dashboard',  icon: 'insights' },
    { path: '/tracker',      label: '投递跟踪',   labelEn: 'Tracker',    icon: 'assignment' },
    { path: '/playbook',     label: '实战机经',   labelEn: 'Playbook',   icon: 'menu_book' },
    { path: '/tech-stack',   label: '技术栈',     labelEn: 'Tech Stack', icon: 'school' },
    { path: '/mock',         label: 'AI 实战模拟', labelEn: 'AI Mock',   icon: 'record_voice_over' },
    { path: '/analytics',    label: '数据分析',   labelEn: 'Analytics',  icon: 'query_stats' },
    {
      path: '/admin', label: '管理后台', labelEn: 'Admin', icon: 'admin_panel_settings',
      permission: 'admin.users.read'
    }
  ];

  /** 按权限过滤后的导航项 —— 没权限的模块根本不显示,不靠点了才报 403。 */
  readonly nav = computed(() =>
    this.allNav.filter((i) => !i.permission || this.auth.can(i.permission))
  );

  toggleSidebar(): void {
    this.collapsed.update((v) => !v);
  }

  logout(): void {
    this.auth.logout();
  }

  goProfile(): void {
    this.router.navigate(['/profile']);
  }
}
