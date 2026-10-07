import { Component, inject, input, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * "I'm not a robot" human-verification checkbox (reCAPTCHA-style).
 *
 * This is a CLIENT-SIDE UI gate only: click → brief spinner → green check.
 * It does NOT provide real bot protection. For production-grade protection,
 * wire up Cloudflare Turnstile or Google reCAPTCHA (needs site keys) and
 * verify the token server-side.
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
    <div class="hc" [class.hc-verified]="verified()" [class.hc-error]="failed()"
         role="checkbox" [attr.aria-checked]="verified()"
         [attr.aria-label]="t('auth.humanCheckLabel')"
         tabindex="0" (click)="verify()" (keydown.enter)="verify()" (keydown.space)="verify(); $event.preventDefault()">
      <span class="hc-box">
        @if (checking()) {
          <mat-spinner diameter="22" strokeWidth="3"></mat-spinner>
        } @else if (verified()) {
          <mat-icon class="hc-check">check</mat-icon>
        } @else if (failed()) {
          <mat-icon class="hc-x">close</mat-icon>
        } @else {
          <span class="hc-empty"></span>
        }
      </span>
      <span class="hc-label">
        @if (failed()) {
          {{ t('auth.humanCheckRetry') }}
        } @else {
          {{ t('auth.humanCheckLabel') }}
        }
      </span>
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
      border: 1px solid rgba(0, 0, 0, 0.18);
      border-radius: 4px;
      background: #fafafa;
      cursor: pointer;
      user-select: none;
      max-width: 300px;
      transition: border-color 150ms ease, box-shadow 150ms ease;
    }
    .hc:hover { border-color: rgba(0, 0, 0, 0.32); }
    .hc:focus-visible {
      outline: 2px solid #3f51b5;
      outline-offset: 2px;
    }
    .hc-box {
      flex: 0 0 24px;
      width: 24px;
      height: 24px;
      display: flex;
      align-items: center;
      justify-content: center;
      border: 2px solid rgba(0, 0, 0, 0.4);
      border-radius: 3px;
      background: #fff;
    }
    .hc-empty { width: 100%; height: 100%; }
    .hc-check {
      color: #fff;
      background: #2e7d32;
      border-radius: 2px;
      font-size: 20px;
      width: 24px;
      height: 24px;
      display: flex;
      align-items: center;
      justify-content: center;
      margin: -2px;
    }
    .hc-x {
      color: #fff;
      background: #c62828;
      border-radius: 2px;
      font-size: 20px;
      width: 24px;
      height: 24px;
      display: flex;
      align-items: center;
      justify-content: center;
      margin: -2px;
    }
    .hc-verified .hc-box { border-color: #2e7d32; }
    .hc-error .hc-box { border-color: #c62828; }
    .hc-label {
      flex: 1;
      font-size: 14px;
      color: rgba(0, 0, 0, 0.75);
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
      color: #5f6368;
    }
    .hc-brand-text {
      font-size: 8.5px;
      letter-spacing: 0.2px;
      color: #5f6368;
      white-space: nowrap;
    }
  `]
})
export class HumanCheckComponent {
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** Two-way bound verification state. */
  readonly verified = input(false);
  readonly verifiedChange = output<boolean>();

  readonly checking = signal(false);
  readonly failed = signal(false);

  /**
   * Simulated verification: brief spinner, then pass.
   * ~5% random "failure" to mimic real challenge behavior —
   * user just clicks again to retry.
   */
  verify(): void {
    if (this.verified() || this.checking()) return;
    this.checking.set(true);
    this.failed.set(false);

    // 600–1200ms "challenge" delay, then resolve.
    const delay = 600 + Math.random() * 600;
    setTimeout(() => {
      this.checking.set(false);
      if (Math.random() < 0.05) {
        // Rare simulated failure — user retries with one click.
        this.failed.set(true);
        this.verifiedChange.emit(false);
      } else {
        this.verifiedChange.emit(true);
      }
    }, delay);
  }

  /** Parent calls this to reset (e.g. after a failed login attempt). */
  reset(): void {
    this.verifiedChange.emit(false);
    this.failed.set(false);
    this.checking.set(false);
  }
}
