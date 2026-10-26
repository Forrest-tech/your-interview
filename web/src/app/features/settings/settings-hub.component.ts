import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 票 146：GitHub 式设置中心壳 —— 左导航文字链接 + 子路由出口。
 * 不复用 layout/subnav.service.ts 的页面级图标竖栏（折叠后只剩图标、无分组标题，
 * 与 GitHub 式文字导航冲突）。导航项为静态配置。
 * Admin 不并入本中心（用户 2026-10-25 拍板：Admin 保留在头像菜单），
 * 故本导航目前只有个人设置两节；新增节须单独立项后追加配置。
 */
interface SettingsNavItem {
  path: string;
  labelKey: string;
  icon: string;
}

const NAV_ITEMS: readonly SettingsNavItem[] = [
  { path: '/settings/profile', labelKey: 'account.profile', icon: 'person' },
  { path: '/settings/ai', labelKey: 'setting.title', icon: 'smart_toy' }
];

@Component({
  selector: 'app-settings-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, MatIconModule],
  templateUrl: './settings-hub.component.html',
  styleUrl: './settings-hub.component.scss'
})
export class SettingsHubComponent {
  readonly i18n = inject(I18nService);
  readonly navItems = NAV_ITEMS;

  /** 模板用:直接暴露 t() 给模板调用。 */
  t(key: string): string {
    return this.i18n.t(key);
  }
}
