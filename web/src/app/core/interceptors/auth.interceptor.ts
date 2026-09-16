import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../auth/auth.service';

/**
 * 统一挂 Bearer 令牌 + 401 自动刷新并重放请求。
 *
 * 为什么要"刷新并重放"而不是直接踢回登录页:
 *   访问令牌是短命的(默认 1 小时)。用户正在填 JD 或录答案时被踢出去,
 *   丢掉的是几十分钟的输入。静默刷新后重放,用户完全无感。
 *
 * 并发安全(2026-09-16 重写):
 *   旧实现用模块级的 `refreshing` 布尔 + BehaviorSubject 手工排队,有两个隐患:
 *     1. 排队期间若刷新失败,BehaviorSubject 停在 null,后续等待者靠 filter 永远
 *        等不到值 —— 请求悬挂,用户界面卡住;
 *     2. 更致命的是"刷新失败 → auth.logout()"这条路径:logout() 会拿当前
 *        refresh token 再打一次 /api/auth/logout,而该令牌此刻已被消费/轮转,
 *        后端「刷新令牌复用检测」会判定令牌泄露,**撤销该用户全部令牌**。
 *        用户被弹回登录页,重新登录后还可能再被踢 —— 即反复出现的
 *        "点 AI practice 就退回登录窗口"。
 *
 *   现在把"同一时刻只刷新一次"的责任交给 AuthService.refresh()(内部用
 *   shareReplay 共享同一个在飞请求),拦截器只负责:401 → 刷新 → 重放;
 *   刷新失败 → clearSession()(纯本地清理,绝不再发请求)。
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.accessToken();

  const authorized = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(authorized).pipe(
    catchError((err: unknown) => {
      const e = err as HttpErrorResponse;

      // 这些请求自身不参与"401 → 刷新"逻辑,否则会递归 / 自伤:
      //   - /refresh:刷新请求自己 401,再触发刷新 = 无限循环
      //   - /login、/register:登录凭证错误是正常的 401,不是会话过期
      //     (旧实现漏了 register,导致注册时密码不合规会被弹去登录页)
      //   - /logout:登出即意味着放弃会话,不需要刷新
      const skip =
        req.url.includes('/api/auth/refresh') ||
        req.url.includes('/api/auth/login') ||
        req.url.includes('/api/auth/register') ||
        req.url.includes('/api/auth/logout');

      if (e.status !== 401 || skip) {
        return throwError(() => err);
      }

      return auth.refresh().pipe(
        switchMap((tokens) =>
          next(req.clone({ setHeaders: { Authorization: `Bearer ${tokens.accessToken}` } }))
        ),
        catchError((refreshErr) => {
          // 刷新也失败 → 会话彻底失效。⚠️ 只做本地清理,绝不再调 logout():
          // 此刻的 refresh token 已被消费,再发一次会触发后端复用检测并
          // 撤销该用户所有令牌(就是反复被踢回登录页的根因)。
          auth.clearSession();
          return throwError(() => refreshErr);
        })
      );
    })
  );
};
