import { ApplicationConfig, provideZoneChangeDetection, isDevMode, inject, APP_INITIALIZER } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideStore } from '@ngrx/store';
import { provideEffects } from '@ngrx/effects';
import { provideStoreDevtools } from '@ngrx/store-devtools';
import { firstValueFrom } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { AuthService } from './core/auth/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    // withComponentInputBinding:路由参数可直接绑到组件 @Input,省掉一堆 ActivatedRoute 订阅
    provideRouter(routes, withComponentInputBinding()),
    provideZoneChangeDetection({ eventCoalescing: true }),

    // 动画改为 async 加载:首屏不必等动画包,体积小一截
    provideAnimationsAsync(),

    provideHttpClient(withInterceptors([authInterceptor])),

    /**
     * 启动时恢复会话(2026-09-16 新增)。
     *
     * 为什么必须有这一步:
     *   以前只有 authGuard 看 localStorage 里的 user 就放行,从不验证令牌是否
     *   还有效。结果:localStorage 里残留一份已过期的会话时,用户能顺利进入
     *   /practice,页面随即发 API 请求 → 后端 401 → 旧拦截器走"刷新失败 →
     *   logout()"这条自伤路径 → 被弹回登录页。这就是反复出现的
     *   "点 AI practice 就退回登录窗口"。
     *
     * 现在:启动时先拉一次 /api/auth/me。
     *   - 令牌有效 → 用户信息刷新,正常进入。
     *   - 令牌过期但 refresh token 有效 → 拦截器静默刷新后重放,用户无感。
     *   - 两者都失效 → 干净地清掉本地会话并停在登录页(而不是进去再被踢)。
     *
     * ⚠️ Angular 17 用 APP_INITIALIZER 注入令牌(provideAppInitializer 是
     *    Angular 19+ 才有的 API,本项目装的是 17.3)。工厂返回的 Promise 会被
     *   路由器等待,所以不会出现"页面先发请求、再收到 401"的竞态。
     */
    {
      provide: APP_INITIALIZER,
      multi: true,
      useFactory: () => {
        const auth = inject(AuthService);
        return () => firstValueFrom(auth.restoreSession());
      }
    },

    // NgRx 先只装 store + effects 骨架 —— 目前各模块用服务 + signal 已够;
    // 等状态跨模块共享变复杂时再逐个加 feature slice,不做过度设计。
    provideStore({}),
    provideEffects([]),
    provideStoreDevtools({ maxAge: 25, logOnly: !isDevMode() })
  ]
};
