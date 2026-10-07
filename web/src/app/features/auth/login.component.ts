import { Component, inject, OnInit, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { HumanCheckComponent } from './human-check.component';

/** Google 登录状态(是否配置了凭据)。 */
interface GoogleLoginStatus {
  enabled: boolean;
}

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatFormFieldModule,
    MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule,
    HumanCheckComponent
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss'
})
export class LoginComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  email = '';
  password = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  /** 中性提示(区别于红色报错):如改密成功后"请重新登录"。 */
  readonly notice = signal<string | null>(null);
  readonly hidePassword = signal(true);
  /** "I'm not a robot" 验证状态 —— 通过后才允许提交。 */
  humanOk = false;
  private readonly humanCheck = viewChild(HumanCheckComponent);

  /** Google 登录按钮是否展示(后端配置了凭据才展示)。 */
  readonly googleEnabled = signal(false);
  /** Google 回程是否正在建立会话(拉 /me 期间)。 */
  readonly googleBusy = signal(false);

  private returnUrl = '/dashboard';

  ngOnInit(): void {
    // returnUrl:登录后要回的页面(路由守卫跳转时带过来的)
    const q = this.route.snapshot.queryParamMap;
    const r = q.get('returnUrl');
    if (r && r.startsWith('/') && !r.startsWith('//')) this.returnUrl = r;

    // Google 流程失败时后端重定向回来带 googleError
    const gErr = q.get('googleError');
    if (gErr) this.error.set(gErr);

    // 中性提示(如修改密码成功后被引导回来:"密码已修改,请重新登录")
    const n = q.get('notice');
    if (n) this.notice.set(n);

    // Google 回调把 token 放在 URL fragment(#access_token=…)带回来
    this.consumeGoogleHash();

    // 后端配了 Google 凭据才显示按钮(端点匿名可查)
    this.auth.googleLoginStatus().subscribe({
      next: (s) => this.googleEnabled.set(!!(s as GoogleLoginStatus)?.enabled),
      error: () => this.googleEnabled.set(false)
    });
  }

  submit(): void {
    if (!this.email || !this.password || !this.humanOk || this.busy()) return;

    this.busy.set(true);
    this.error.set(null);

    this.auth.login(this.email.trim(), this.password).subscribe({
      next: () => {
        this.busy.set(false);
        this.router.navigateByUrl(this.returnUrl);
      },
      error: (e: Error) => {
        this.busy.set(false);
        // 验证重置:登录失败后要求重新过人机验证,防止暴力破解。
        this.humanOk = false;
        this.humanCheck()?.reset();
        this.error.set(this.friendlyError(e));
      }
    });
  }

  /**
   * 把后端/网络的原始错误翻译成用户能看懂的话。
   * 之前直接显示 "Cannot POST /api/auth/login" 的 HTML,用户完全看不懂。
   */
  private friendlyError(e: Error): string {
    const msg = (e?.message ?? '').toLowerCase();
    // 后端没起 / 网络不通:fetch 失败或返回 HTML 错误页
    if (!msg || msg.includes('cannot post') || msg.includes('<!doctype') ||
        msg.includes('failed to fetch') || msg.includes('networkerror') ||
        msg.includes('load failed')) {
      return this.t('auth.errServerUnreachable');
    }
    // 401 等:后端故意不区分"邮箱不存在"和"密码错误"(防枚举攻击)
    return e.message || this.t('auth.loginFailed');
  }

  /** 跳到后端 authorize 端点 → 302 到 Google 授权页。 */
  loginWithGoogle(): void {
    if (this.googleBusy()) return;
    window.location.href =
      '/api/auth/google/authorize?returnUrl=' + encodeURIComponent(this.returnUrl);
  }

  /**
   * 解析并消费 Google 回调 fragment:
   *   /login#access_token=…&refresh_token=…&returnUrl=…
   * fragment 不会进服务器日志;消费后立刻从地址栏清掉,
   * 避免刷新/分享链接时把 token 带在 URL 里。
   */
  private consumeGoogleHash(): void {
    const hash = window.location.hash;
    if (!hash || hash.length < 2) return;

    const params = new URLSearchParams(hash.slice(1));
    const access = params.get('access_token');
    const refresh = params.get('refresh_token');

    // 清掉 fragment(先清再异步建会话,防止 token 残留在地址栏)
    history.replaceState(null, '', window.location.pathname + window.location.search);

    if (!access || !refresh) return;   // 不是 Google 回程的 fragment,忽略

    const r = params.get('returnUrl');
    if (r && r.startsWith('/') && !r.startsWith('//')) this.returnUrl = r;

    this.googleBusy.set(true);
    this.auth.completeExternalLogin(access, refresh).subscribe({
      next: (user) => {
        this.googleBusy.set(false);
        if (user) {
          this.router.navigateByUrl(this.returnUrl);
        } else {
          this.error.set(this.t('auth.googleSessionReturnedFail'));
        }
      },
      error: () => {
        this.googleBusy.set(false);
        this.error.set(this.t('auth.googleSessionFail'));
      }
    });
  }
}
