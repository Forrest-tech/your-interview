import { Component, OnDestroy, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 票 146：GitHub 式设置中心壳 —— 左导航文字链接 + 子路由出口。
 * 不复用 layout/subnav.service.ts 的页面级图标竖栏（折叠后只剩图标、无分组标题，
 * 与 GitHub 式文字导航冲突）。导航项为静态配置。
 * Admin 不并入本中心（用户 2026-10-25 拍板：Admin 保留在头像菜单），
 * 故本导航目前只有个人设置两节；新增节须单独立项后追加配置。
 * 票 244：旧 AI 语音设置页拆为 Voice / LLM 两页，顺序为 Voice、LLM、Mailbox。
 * 票 247：My Account 移出设置中心（顶级 /profile），本导航不再含个人资料节。
 */
interface SettingsNavItem {
  path: string;
  labelKey: string;
  icon: string;
}

const NAV_ITEMS: readonly SettingsNavItem[] = [
  { path: '/settings/voice', labelKey: 'setting.voiceTitle', icon: 'mic' },
  { path: '/settings/llm', labelKey: 'ai.pageTitle', icon: 'smart_toy' },
  // 票 240：配置邮箱页与 LLM 配置页平行（同一设置中心左导航）。
  { path: '/settings/mail', labelKey: 'mailbox.title', icon: 'mail' }
];

@Component({
  selector: 'app-settings-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule, MatTooltipModule],
  templateUrl: './settings-hub.component.html',
  styleUrl: './settings-hub.component.scss'
})
export class SettingsHubComponent implements OnDestroy {
  readonly i18n = inject(I18nService);
  readonly navItems = NAV_ITEMS;

  /** 票 181：左导航整列折叠状态，记 localStorage（默认展开）。 */
  readonly navCollapsed = signal(
    (() => {
      try {
        return typeof localStorage !== 'undefined' && localStorage.getItem('ui.navCollapsed.settingsNav') === '1';
      } catch { return false; }
    })());
  /** 票 181：窄屏（≤860px）下整列折叠不生效——沿用已有顶部横向行形态，回到宽屏时按存值恢复。 */
  readonly isNavNarrow = signal(false);
  private navMql: MediaQueryList | null = null;
  private readonly onNavMqlChange = (e: MediaQueryListEvent): void => { this.isNavNarrow.set(e.matches); };

  constructor() {
    if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
      this.navMql = window.matchMedia('(max-width: 860px)');
      this.isNavNarrow.set(this.navMql.matches);
      this.navMql.addEventListener('change', this.onNavMqlChange);
    }
  }

  ngOnDestroy(): void {
    this.navMql?.removeEventListener('change', this.onNavMqlChange);
  }

  /** 票 181：翻转左导航折叠并写回 localStorage。 */
  toggleNav(): void {
    const next = !this.navCollapsed();
    this.navCollapsed.set(next);
    try { localStorage.setItem('ui.navCollapsed.settingsNav', next ? '1' : '0'); } catch { /* ignore */ }
  }

  /** 模板用:直接暴露 t() 给模板调用。 */
  t(key: string): string {
    return this.i18n.t(key);
  }
}
