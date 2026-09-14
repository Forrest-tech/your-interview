import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { BehaviorSubject, catchError, filter, switchMap, take, throwError } from 'rxjs';
import { AuthService } from '../auth/auth.service';

/**
 * 统一挂 Bearer 令牌 + 401 自动刷新并重放请求。
 *
 * 为什么要"刷新并重放"而不是直接踢回登录页:
 *   访问令牌是短命的(默认 1 小时)。用户正在填 JD 或录答案时被踢出去,
 *   丢掉的是几十分钟的输入。静默刷新后重放,用户完全无感。
 *
 * 并发安全:多个请求同时 401 时,只发起一次刷新,其余请求排队等结果 ——
 * 否则会同时用同一个 refresh token 刷新,触发后端的"复用检测"把令牌全撤销。
 */
let refreshing = false;
const refreshed$ = new BehaviorSubject<string | null>(null);

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.accessToken();

  const authorized = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(authorized).pipe(
    catchError((err: unknown) => {
      const e = err as HttpErrorResponse;

      // 只处理 401,且不能是刷新请求自身(否则会无限递归)
      const isRefreshCall = req.url.includes('/api/auth/refresh');
      const isLoginCall = req.url.includes('/api/auth/login');
      if (e.status !== 401 || isRefreshCall || isLoginCall) {
        return throwError(() => err);
      }

      if (refreshing) {
        // 已有刷新在飞:等它完成后再重放本请求
        return refreshed$.pipe(
          filter((t): t is string => t !== null),
          take(1),
          switchMap((fresh) => next(
            req.clone({ setHeaders: { Authorization: `Bearer ${fresh}` } })
          ))
        );
      }

      refreshing = true;
      refreshed$.next(null);

      return auth.refresh().pipe(
        switchMap((tokens) => {
          refreshing = false;
          refreshed$.next(tokens.accessToken);
          return next(req.clone({ setHeaders: { Authorization: `Bearer ${tokens.accessToken}` } }));
        }),
        catchError((refreshErr) => {
          refreshing = false;
          refreshed$.next(null);
          // 刷新也失败 → 会话彻底失效,清除本地状态
          auth.logout();
          return throwError(() => refreshErr);
        })
      );
    })
  );
};
