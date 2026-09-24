import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap, map, catchError, throwError, of, shareReplay, finalize } from 'rxjs';
import { ApiClient } from '../api/api-client';
import { AuthResult, AuthUser, Tokens } from '../models/api.models';

const TOKEN_KEY = 'yi.accessToken';
const REFRESH_KEY = 'yi.refreshToken';
const USER_KEY = 'yi.user';

/**
 * 认证状态。
 *
 * 用 signal 而不是 BehaviorSubject:
 *   模板里直接读 auth.user() 就能响应式更新,少一层 async pipe 和订阅管理。
 *
 * 令牌存 localStorage:
 *   这是可接受的取舍 —— 本平台是单用户自用工具,不是面向公众的银行系统。
 *   若要更严格,应改为 httpOnly cookie + 后端 CSRF 防护(需要后端配合改发 cookie)。
 *   这里的选择已写进 README 的安全说明,不藏着。
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);

  private readonly _user = signal<AuthUser | null>(readUser());
  readonly user = this._user.asReadonly();
  readonly isAuthenticated = computed(() => this._user() !== null);
  readonly displayName = computed(() => this._user()?.displayName ?? '');

  /**
   * ⚠️ 正在飞的刷新请求(共享给所有并发调用者)。
   *
   * 为什么必须是共享的:后端对刷新令牌有「复用检测」—— 同一个 refresh token
   * 若被使用第二次,后端会判定为令牌泄露,**撤销该用户全部令牌**并强制重新登录。
   * 而前端是单页应用,页面加载时可能有多个请求同时拿到 401(素材、录音、评分
   * 各自发请求)。如果每个 401 都各发一次 /api/auth/refresh,第二发就会踩中复用
   * 检测 —— 表现为「用着用着突然被弹回登录页」,而且是永久性登出。
   *
   * 所以:同一时刻只允许一个刷新在飞,其余调用者复用它。
   *
   * 另外用 shareReplay(1) 让刷新结果可以被多个订阅者拿到;finalize 保证
   * 请求结束后清空引用,下次 401 能重新发起。
   */
  private refreshInFlight$: Observable<Tokens> | null = null;

  /** 权限判断 —— 与后端 perm: 策略同源(后端 claim 里的 perm 数组)。 */
  can(permission: string): boolean {
    const u = this._user();
    if (!u) return false;
    return u.permissions?.includes(permission) ?? false;
  }

  canAny(...permissions: string[]): boolean {
    return permissions.some((p) => this.can(p));
  }

  hasRole(role: string): boolean {
    return this._user()?.roles?.includes(role) ?? false;
  }

  login(email: string, password: string): Observable<AuthResult> {
    return this.api.post<AuthResult>('/api/auth/login', { email, password }).pipe(
      tap((r) => this.persist(r)),
      catchError((e) => throwError(() => e))
    );
  }

  /**
   * Google 登录收尾(M2.1):后端回调把 token 放在 URL fragment 里带回,
   * 这里落库并拉取 /me 建立会话。失败(令牌无效)会清掉残留并返回 null。
   */
  completeExternalLogin(accessToken: string, refreshToken: string): Observable<AuthUser | null> {
    localStorage.setItem(TOKEN_KEY, accessToken);
    localStorage.setItem(REFRESH_KEY, refreshToken);
    return this.restoreSession();
  }

  /** Google 登录是否可用(后端配置了 OAuth 凭据)。匿名端点。 */
  googleLoginStatus(): Observable<{ enabled: boolean }> {
    return this.api.get<{ enabled: boolean }>('/api/auth/google/status');
  }

  register(email: string, displayName: string, password: string): Observable<AuthResult> {
    return this.api.post<AuthResult>('/api/auth/register', { email, displayName, password }).pipe(
      tap((r) => this.persist(r))
    );
  }

  /**
   * 本地登出 —— 清状态 + 跳登录页,**不发后端请求**。
   *
   * 为什么不像以前那样再调 /api/auth/logout:
   *   那个方法接收的是「当前 refresh token」。在**刷新失败**这条路径上,
   *   令牌要么已过期、要么已被轮转消费掉 —— 拿它去调 logout 会命中后端的
   *   复用检测,导致后端把该用户全部令牌一起撤销。结果是用户只是想重新登录,
   *   却被后端判为「令牌泄露」,下次登录后又被踢。这就是之前反复
   *   「点 AI practice 就弹回登录」的根因之一。
   *
   * 主动登出(用户点「退出」)走 logout() —— 那里主动撤销令牌是正确且有意义的。
   */
  clearSession(): void {
    this.clear();
  }

  /** 用户主动退出 —— 撤销后端令牌后本地登出。 */
  logout(): void {
    const token = this.refreshToken();
    // 先通知后端撤销刷新令牌 —— 失败也不影响本地登出(用户要的是立刻退出)
    // 只在确实有令牌时才发,避免发一个空 refreshToken 造成后端无谓处理。
    if (token) {
      this.api.post('/api/auth/logout', { refreshToken: token })
        .pipe(catchError(() => of(null)))
        .subscribe();
    }

    // ⚠️ 只清一次。以前这里 clear() 被调用两次(一次在 subscribe 回调里,
    //    一次在方法体末尾),虽然 removeItem 幂等不出错,但读起来像是两码事,
    //    更重要的是会触发两次 navigate(['/login'])。
    this.clear();
  }

  /** 会话恢复:刷新页面后用已有令牌拉一次 /me,顺便验证令牌还有效。 */
  restoreSession(): Observable<AuthUser | null> {
    if (!this.accessToken()) {
      // 没有令牌 —— 可能是全新的访客(停在登录页是正常的)。
      // 本地若有残留的 user 对象(令牌被删但 user 没删干净),一并清掉,
      // 否则 authGuard 会以为已登录而放行,又变成"进去才被踢"。
      if (this._user()) this.clear();
      return of(null);
    }

    return this.api.get<AuthUser>('/api/auth/me').pipe(
      tap((u) => {
        this._user.set(u);
        localStorage.setItem(USER_KEY, JSON.stringify(u));
      }),
      catchError(() => {
        // /me 失败(令牌过期且刷新也救不回来)才清会话。
        this.clear();
        return of(null);
      })
    );
  }

  /**
   * 刷新访问令牌 —— 并发安全。
   *
   * 关键:同一时刻只有一个请求真正打到后端,其余调用者复用同一个 Observable。
   * 这直接避免了后端「刷新令牌复用检测」被误触发(详见 refreshInFlight$ 注释)。
   */
  refresh(): Observable<Tokens> {
    if (this.refreshInFlight$) return this.refreshInFlight$;

    const refreshToken = this.refreshToken();
    if (!refreshToken) {
      // 没有刷新令牌 —— 不可能刷新成功,直接给出错误,不发无意义的空请求。
      return throwError(() => new Error('缺少刷新令牌'));
    }

    this.refreshInFlight$ = this.api.post<{ tokens: Tokens }>('/api/auth/refresh', {
      refreshToken
    }).pipe(
      // 后端返回 { tokens: {...} },这里用 map 拆出 tokens 而不是 as 强转 ——
      // 强转只是骗过编译器,调用方拿到的仍是包装对象,刷新后取 accessToken 会 undefined。
      map((r) => {
        localStorage.setItem(TOKEN_KEY, r.tokens.accessToken);
        localStorage.setItem(REFRESH_KEY, r.tokens.refreshToken);
        return r.tokens;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
      // finalize 无论成功失败都清空,保证下一次 401 能重新发起刷新。
      finalize(() => {
        this.refreshInFlight$ = null;
      })
    );

    return this.refreshInFlight$;
  }

  accessToken(): string | null {
    return localStorage.getItem(TOKEN_KEY);
  }

  refreshToken(): string | null {
    return localStorage.getItem(REFRESH_KEY);
  }

  private persist(r: AuthResult): void {
    localStorage.setItem(TOKEN_KEY, r.tokens.accessToken);
    localStorage.setItem(REFRESH_KEY, r.tokens.refreshToken);
    localStorage.setItem(USER_KEY, JSON.stringify(r.profile));
    this._user.set(r.profile);
  }

  private clear(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(REFRESH_KEY);
    localStorage.removeItem(USER_KEY);
    this._user.set(null);
    // 已经在登录页就不必再 navigate —— 否则登录页自身初始化时的会话恢复失败
    // 会产生一次多余跳转(丢掉 returnUrl 查询参数,用户重新登录后回不到原页面)。
    if (!this.router.url.startsWith('/login')) {
      this.router.navigate(['/login'], {
        queryParams: { returnUrl: this.router.url }
      });
    }
  }
}

function readUser(): AuthUser | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? (JSON.parse(raw) as AuthUser) : null;
  } catch {
    return null;
  }
}
