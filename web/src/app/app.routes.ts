import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from './core/auth/auth.guard';
import { ShellComponent } from './layout/shell.component';

/**
 * 路由采用"布局父路由 + 懒加载子路由":
 * 布局只初始化一次,切换模块不重建侧边栏;
 * 子模块按需加载,首屏体积小。
 */
export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () =>
      import('./features/auth/login.component').then((m) => m.LoginComponent)
  },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./features/dashboard/dashboard.component').then((m) => m.DashboardComponent)
      },
      {
        path: 'tracker',
        loadComponent: () =>
          import('./features/tracker/tracker.component').then((m) => m.TrackerComponent)
      },
      {
        path: 'playbook',
        loadComponent: () =>
          import('./features/playbook/playbook.component').then((m) => m.PlaybookComponent)
      },
      {
        path: 'playbook/:id',
        loadComponent: () =>
          import('./features/playbook/playbook-detail.component').then((m) => m.PlaybookDetailComponent)
      },
      {
        path: 'tech-stack',
        loadComponent: () =>
          import('./features/techstack/techstack.component').then((m) => m.TechStackComponent)
      },
      {
        path: 'practice',
        loadComponent: () =>
          import('./features/ai-practice/ai-practice.component').then((m) => m.AiPracticeComponent)
      },
      {
        path: 'mock',
        loadComponent: () =>
          import('./features/mock/mock.component').then((m) => m.MockComponent)
      },
      {
        path: 'mock/:id',
        loadComponent: () =>
          import('./features/mock/mock-session.component').then((m) => m.MockSessionComponent)
      },
      {
        path: 'analytics',
        loadComponent: () =>
          import('./features/analytics/analytics.component').then((m) => m.AnalyticsComponent)
      },
      {
        path: 'admin',
        canActivate: [permissionGuard('admin.users.read')],
        loadComponent: () =>
          import('./features/admin/admin.component').then((m) => m.AdminComponent)
      },
      {
        path: 'profile',
        loadComponent: () =>
          import('./features/profile/profile.component').then((m) => m.ProfileComponent)
      },
      {
        // AI / Azure 语音设置 —— /practice 的「TTS 引擎 → 前往配置 ↗」落点。
        // 2026-09-15 第六轮新增:此前该路由不存在,点过去会 404。
        path: 'account/ai-setting',
        loadComponent: () =>
          import('./features/ai-setting/ai-setting.component').then((m) => m.AiSettingComponent)
      },
      {
        // 条款 / 隐私 / 版权 —— 页脚三个链接的落点。
        // 2026-09-15 第七轮新增:此前三条都走 `**` 重定向回 dashboard(点不动)。
        // 三个路径共用一个组件,靠 URL 分支内容。
        path: 'terms',
        loadComponent: () =>
          import('./features/legal/legal.component').then((m) => m.LegalComponent)
      },
      {
        path: 'privacy',
        loadComponent: () =>
          import('./features/legal/legal.component').then((m) => m.LegalComponent)
      },
      {
        path: 'copyright',
        loadComponent: () =>
          import('./features/legal/legal.component').then((m) => m.LegalComponent)
      },
      {
        path: 'forbidden',
        loadComponent: () =>
          import('./features/forbidden/forbidden.component').then((m) => m.ForbiddenComponent)
      }
    ]
  },
  { path: '**', redirectTo: 'dashboard' }
];
