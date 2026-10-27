import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AuthService } from '../../core/auth/auth.service';
import { LogtoService } from '../../core/auth/logto.service';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 票241 Logto 回调页（路由 /auth/logto/callback，壳外，与登录页同级）。
 *
 * 流程：Logto 授权成功带 code 回来 → 验 state、code 换 token → 经
 * AuthService.completeLogtoLogin 走 /api/auth/logto/me 建会话。
 *
 * 两条绝不能进 Dashboard 的路：
 *   · 会话没建成（user 为 null，completeLogtoLogin 内部已清会话）→ 回登录页带失败提示；
 *   · 待审批账号：门已翻译 NameIdentifier 所以 /me 是 200 但 permissions 为空 ——
 *     这不是“登录成功”，清会话、回登录页带待审批提示，绝不进 Dashboard。
 * 其它失败（state 不匹配、Logto 回 error、换 token 失败）直接在本页显示错误，
 * 不跳走（此时尚未建会话，没有残留需要清）。
 */
@Component({
  selector: 'app-logto-callback',
  standalone: true,
  imports: [CommonModule, RouterLink, MatButtonModule, MatCardModule, MatProgressBarModule],
  templateUrl: './logto-callback.component.html'
})
export class LogtoCallbackComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly logto = inject(LogtoService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  readonly busy = signal(true);
  readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const q = this.route.snapshot.queryParamMap;
    if (q.get('error')) {
      this.failInline(this.t('auth.logtoFailed'));
      return;
    }
    const code = q.get('code');
    const state = q.get('state');
    if (!code || !state) {
      this.failInline(this.t('auth.logtoFailed'));
      return;
    }
    let tokens;
    try {
      tokens = await this.logto.handleCallback(code, state);
    } catch {
      this.failInline(this.t('auth.logtoFailed'));
      return;
    }
    if (!tokens.access_token) {
      this.failInline(this.t('auth.logtoFailed'));
      return;
    }
    this.auth.completeLogtoLogin(tokens.access_token, tokens.refresh_token ?? '').subscribe({
      next: (user) => {
        this.busy.set(false);
        if (!user) {
          // 会话没建成：completeLogtoLogin 已清会话，这里只负责把提示带回登录页。
          void this.router.navigate(['/login'], { queryParams: { notice: this.t('auth.logtoFailed') } });
          return;
        }
        if (!user.permissions || user.permissions.length === 0) {
          // 待审批：零权限不是登录成功，清会话、回登录页带提示，绝不进 Dashboard。
          this.auth.clearSession();
          void this.router.navigate(['/login'], { queryParams: { notice: this.t('auth.logtoPending') } });
          return;
        }
        void this.router.navigateByUrl(this.logto.consumeReturnUrl());
      },
      error: () => {
        this.busy.set(false);
        void this.router.navigate(['/login'], { queryParams: { notice: this.t('auth.logtoFailed') } });
      }
    });
  }

  private failInline(msg: string): void {
    this.busy.set(false);
    this.error.set(msg);
  }
}
