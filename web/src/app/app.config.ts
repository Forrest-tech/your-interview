import { ApplicationConfig, provideZoneChangeDetection, isDevMode } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideStore } from '@ngrx/store';
import { provideEffects } from '@ngrx/effects';
import { provideStoreDevtools } from '@ngrx/store-devtools';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    // withComponentInputBinding:路由参数可直接绑到组件 @Input,省掉一堆 ActivatedRoute 订阅
    provideRouter(routes, withComponentInputBinding()),
    provideZoneChangeDetection({ eventCoalescing: true }),

    // 动画改为 async 加载:首屏不必等动画包,体积小一截
    provideAnimationsAsync(),

    provideHttpClient(withInterceptors([authInterceptor])),

    // NgRx 先只装 store + effects 骨架 —— 目前各模块用服务 + signal 已够;
    // 等状态跨模块共享变复杂时再逐个加 feature slice,不做过度设计。
    provideStore({}),
    provideEffects([]),
    provideStoreDevtools({ maxAge: 25, logOnly: !isDevMode() })
  ]
};
