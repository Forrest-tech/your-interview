import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap, map, catchError, throwError, of } from 'rxjs';
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

  register(email: string, displayName: string, password: string): Observable<AuthResult> {
    return this.api.post<AuthResult>('/api/auth/register', { email, displayName, password }).pipe(
      tap((r) => this.persist(r))
    );
  }

  logout(): void {
    // 先通知后端撤销刷新令牌 —— 失败也不影响本地登出(用户要的是立刻退出)
    this.api.post('/api/auth/logout', { refreshToken: this.refreshToken() })
      .pipe(catchError(() => of(null)))
      .subscribe(() => this.clear());

    this.clear();
  }

  /** 会话恢复:刷新页面后用已有令牌拉一次 /me,顺便验证令牌还有效。 */
  restoreSession(): Observable<AuthUser | null> {
    if (!this.accessToken()) return of(null);

    return this.api.get<AuthUser>('/api/auth/me').pipe(
      tap((u) => {
        this._user.set(u);
        localStorage.setItem(USER_KEY, JSON.stringify(u));
      }),
      catchError(() => {
        this.clear();
        return of(null);
      })
    );
  }

  refresh(): Observable<Tokens> {
    // 后端返回 { tokens: {...} },这里用 map 拆出 tokens 而不是 as 强转 ——
    // 强转只是骗过编译器,调用方拿到的仍是包装对象,刷新后取 accessToken 会 undefined。
    return this.api.post<{ tokens: Tokens }>('/api/auth/refresh', {
      refreshToken: this.refreshToken()
    }).pipe(
      map((r) => {
        localStorage.setItem(TOKEN_KEY, r.tokens.accessToken);
        localStorage.setItem(REFRESH_KEY, r.tokens.refreshToken);
        return r.tokens;
      }),
      catchError((e) => {
        this.clear();
        return throwError(() => e);
      })
    );
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
    this.router.navigate(['/login']);
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
