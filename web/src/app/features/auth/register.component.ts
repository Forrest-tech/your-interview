import { Component, OnInit, inject, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { LogtoService } from '../../core/auth/logto.service';
import { HumanCheckComponent } from './human-check.component';

/**
 * ★ ML-FIX-UI(W-UI-5):注册页 —— 后端 /api/auth/register 早已是
 * [AllowAnonymous](默认 User 角色),前端 auth.register() 也早已存在,
 * 只是从来没有 UI 入口与路由。本页补上这最后一环,与登录页同款
 * 人机验证门槛(防批量注册);成功后 auth.register() 已落会话,直接进 Dashboard。
 */
@Component({
  selector: 'app-register',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink, MatFormFieldModule,
    MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule,
    HumanCheckComponent
  ],
  templateUrl: './register.component.html',
  styleUrl: './register.component.scss'
})
export class RegisterComponent implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly i18n = inject(I18nService);
  private readonly logto = inject(LogtoService);
  t = (key: string): string => this.i18n.t(key);

  ngOnInit(): void {
    // 票241 灰度期：注册统一走 Logto hosted 注册页（审批流的唯一入口），
    // 旧表单只在双轨关闭（logto.enabled=false）时保留；灰度期服务端对旧
    // /api/auth/register 直接回 410，旧表单即使留下也提交不出去。
    if (this.logto.isEnabled()) void this.logto.beginLogin('register');
  }

  email = '';
  displayName = '';
  password = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly hidePassword = signal(true);
  humanOk = false;
  private readonly humanCheck = viewChild(HumanCheckComponent);

  submit(): void {
    if (!this.email || !this.displayName || !this.password || !this.humanOk || this.busy()) return;

    this.busy.set(true);
    this.error.set(null);

    this.auth.register(this.email.trim(), this.displayName.trim(), this.password).subscribe({
      next: () => {
        this.busy.set(false);
        this.router.navigateByUrl('/dashboard');
      },
      error: (e: Error) => {
        this.busy.set(false);
        this.humanOk = false;
        this.humanCheck()?.reset();
        this.error.set(this.friendlyError(e));
      }
    });
  }

  /** 与登录页同款:网络/后端不通给中性提示,其余透传后端原因(409 邮箱已注册等)。 */
  private friendlyError(e: Error): string {
    const msg = (e?.message ?? '').toLowerCase();
    if (!msg || msg.includes('cannot post') || msg.includes('<!doctype') || msg.includes('failed to fetch')) {
      return this.t('auth.errServerUnreachable');
    }
    return e.message || this.t('auth.registerFailed');
  }
}
