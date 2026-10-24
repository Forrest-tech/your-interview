import { Component, inject, model, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * "I'm not a robot" human-verification checkbox (reCAPTCHA-style).
 *
 * CLIENT-SIDE UI gate only: click → brief spinner → green check.
 * Deterministic: no random failures, no parent round-trip timing issues
 * (uses model() two-way binding so state is local and immediate).
 *
 * Usage:
 *   <app-human-check [(verified)]="humanOk" />
 * The parent disables submit until `verified` is true.
 */
@Component({
  selector: 'app-human-check',
  standalone: true,
  imports: [CommonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    <div class="hc" [class.hc-verified]="verified()"
         role="checkbox" [attr.aria-checked]="verified()"
         [attr.aria-label]="t('auth.humanCheckLabel')"
         tabindex="0" (click)="verify()" (keydown.enter)="verify()" (keydown.space)="verify(); $event.preventDefault()">
      <span class="hc-box">
        @if (checking()) {
          <mat-spinner diameter="22" strokeWidth="3"></mat-spinner>
        } @else if (verified()) {
          <mat-icon class="hc-check">check</mat-icon>
        } @else {
          <span class="hc-empty"></span>
        }
      </span>
      <span class="hc-label">{{ t('auth.humanCheckLabel') }}</span>
      <span class="hc-brand">
        <mat-icon class="hc-shield">shield</mat-icon>
        <span class="hc-brand-text">{{ t('auth.humanCheckBrand') }}</span>
      </span>
    </div>
  `,
  styles: [`
    .hc {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      border: 1px solid rgba(31, 36, 48, 0.18);
      border-radius: 6px;
      background: #f5f6f8;
      cursor: pointer;
      user-select: none;
      max-width: 300px;
      transition: border-color 150ms ease, box-shadow 150ms ease;
    }
    .hc:hover { border-color: rgba(31, 36, 48, 0.32); }
    .hc:focus-visible {
      outline: 2px solid #2e3a8c;
      outline-offset: 2px;
    }
    .hc-box {
      flex: 0 0 24px;
      width: 24px;
      height: 24px;
      display: flex;
      align-items: center;
      justify-content: center;
      border: 2px solid rgba(31, 36, 48, 0.4);
      border-radius: 6px;
      background: #ffffff;
    }
    .hc-empty { width: 100%; height: 100%; }
    .hc-check {
      color: #ffffff;
      background: #1b7f4b;
      border-radius: 6px;
      font-size: 20px;
      width: 24px;
      height: 24px;
      display: flex;
      align-items: center;
      justify-content: center;
      margin: -2px;
    }
    .hc-verified .hc-box { border-color: #1b7f4b; }
    .hc-label {
      flex: 1;
      font-size: 14px;
      color: rgba(31, 36, 48, 0.75);
    }
    .hc-brand {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 1px;
      opacity: 0.55;
    }
    .hc-shield {
      font-size: 22px;
      width: 22px;
      height: 22px;
      color: #5b6270;
    }
    .hc-brand-text {
      font-size: 8.5px;
      letter-spacing: 0.2px;
      color: #5b6270;
      white-space: nowrap;
    }
  `]
})
export class HumanCheckComponent {
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** Two-way bound verification state (model = local, immediate, no parent round-trip). */
  readonly verified = model(false);

  readonly checking = signal(false);

  /**
   * Click → short spinner (400ms, just for affordance) → verified.
   * Deterministic: always succeeds. Double-clicks while spinning are ignored.
   */
  verify(): void {
    if (this.verified() || this.checking()) return;
    this.checking.set(true);
    setTimeout(() => {
      this.checking.set(false);
      this.verified.set(true);
    }, 400);
  }

  /** Parent calls this to reset (e.g. after a failed login attempt). */
  reset(): void {
    this.verified.set(false);
    this.checking.set(false);
  }
}
