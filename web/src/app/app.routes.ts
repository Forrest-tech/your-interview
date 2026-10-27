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
    // ★ ML-FIX-UI(W-UI-5):注册入口 —— 后端 /api/auth/register 早已开放,
    // 前端此前无路由无链接,账号只能经 API/管理员创建。
    path: 'register',
    loadComponent: () =>
      import('./features/auth/register.component').then((m) => m.RegisterComponent)
  },
  {
    // 票241：Logto 授权回调（壳外，与登录页同级；回调页内建会话或回登录页）。
    path: 'auth/logto/callback',
    loadComponent: () =>
      import('./features/auth/logto-callback.component').then((m) => m.LogtoCallbackComponent)
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
        path: 'playbook/calendar',
        loadComponent: () =>
          import('./features/playbook/playbook-calendar.component').then((m) => m.PlaybookCalendarComponent)
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
        // 票 146：GitHub 式设置中心 —— /settings 父路由 + 两节子路由。
        // 旧 URL（/profile、/account/ai-setting）全部重定向到新 URL，任何旧入口不 404。
        path: 'settings',
        loadComponent: () =>
          import('./features/settings/settings-hub.component').then((m) => m.SettingsHubComponent),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'profile' },
          {
            path: 'profile',
            loadComponent: () =>
              import('./features/profile/profile.component').then((m) => m.ProfileComponent)
          },
          {
            path: 'ai',
            loadComponent: () =>
              import('./features/ai-setting/ai-setting.component').then((m) => m.AiSettingComponent)
          },
          {
            // 票 240：配置邮箱页，与 API key 配置页平行。
            path: 'mail',
            loadComponent: () =>
              import('./features/settings/mailbox-setting.component').then((m) => m.MailboxSettingComponent)
          }
        ]
      },
      { path: 'profile', redirectTo: 'settings/profile' },
      { path: 'account/ai-setting', redirectTo: 'settings/ai' },
      {
        path: 'resume',
        loadComponent: () =>
          import('./features/resume/resume.component').then((m) => m.ResumeComponent)
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
