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
        path: 'forbidden',
        loadComponent: () =>
          import('./features/forbidden/forbidden.component').then((m) => m.ForbiddenComponent)
      }
    ]
  },
  { path: '**', redirectTo: 'dashboard' }
];
