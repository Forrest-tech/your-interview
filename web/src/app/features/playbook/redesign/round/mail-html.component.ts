import {
  Component, ElementRef, EventEmitter, OnDestroy, OnInit, Output, ViewChild,
  inject, input, signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient, HttpResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import DOMPurify from 'dompurify';
import { I18nService } from '../../../../core/i18n/i18n.service';
import {
  buildMailSrcdoc, isMailHtmlEmpty, sanitizeMailHtml,
} from './mail-html.sanitize';

/**
 * 票 309 §8：邮件 HTML 渲染（Materials tab 内每封展开卡专用；comm-log 不渲染正文，不用它）。
 * 只有本组件渲染 HtmlBody，且永远先过 DOMPurify（静态检查见 tools/check-mail-html-bypass.mjs）。
 *
 * - 懒渲染：卡片展开时组件创建才取 /html（默认拦截远端图）；收起即销毁，订阅随之取消。
 * - 单一失败路径：/html 取信失败（404/500/网络/超时皆同）或消毒为空 → 发 noHtml，
 *   父级渲染既有纯文本路径（时间线分割或 <pre> 回落），此处绝不留空 iframe 与错误态。
 * - iframe：sandbox 字符串精确不变（无 allow-scripts 等），内容经直接 DOM
 *   setAttribute('srcdoc', …) 写入——不走 Angular 属性绑定，故 Angular 内置
 *   消毒器不碰邮件 HTML，本路径也不需要任何信任旁路。
 * - 高度：load 时读帧内 scrollHeight，min(…, 1600px)，超高部分帧内滚动。
 * - Load images：仅当 /html 响应带 X-Images-Blocked: 1 时出现；逐封显式 opt-in，
 *   不跨封/跨会话持久；重取 ?images=1 后仍走同一 DOMPurify + iframe 路径。
 */
@Component({
  selector: 'app-mail-html',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (showToggle()) {
      <button type="button" class="mail-html-toggle" (click)="loadImages()">
        {{ i18n.t('mailbox.loadImages') }}
      </button>
    }
    @if (hasHtml()) {
      <!-- sandbox 必须是静态字面量：Angular NG0910 禁止以绑定形式设 iframe 的
           sandbox（含 [attr.sandbox]）。字面值与 mail-html.sanitize.ts 的
           MAIL_HTML_SANDBOX 常量逐字相同；真实值由 Playwright 测试 20 在浏览器中
           断言（Jest 测试 18 断言常量本身），改一处必须改两处。 -->
      <iframe #frame
        sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
        referrerpolicy="no-referrer"
        class="mail-html-frame"
        (load)="onFrameLoad()"></iframe>
    }
  `,
  styles: [`
    .mail-html-toggle {
      margin: 0 0 8px;
      padding: 4px 10px;
      border: 1px solid var(--yi-border, #e2e8f0);
      border-radius: 999px;
      background: transparent;
      cursor: pointer;
      font-size: 12px;
    }
    .mail-html-frame { width: 100%; border: 0; display: block; overflow: auto; }
  `],
})
export class MailHtmlComponent implements OnInit, OnDestroy {
  readonly assetId = input.required<string>();
  @Output() noHtml = new EventEmitter<void>();

  protected readonly i18n = inject(I18nService);
  private readonly http = inject(HttpClient);

  protected readonly hasHtml = signal(false);
  protected readonly showToggle = signal(false);

  private sub: Subscription | null = null;
  private frameEl: HTMLIFrameElement | null = null;
  private pendingSrcdoc: string | null = null;

  @ViewChild('frame') set frame(el: ElementRef<HTMLIFrameElement> | undefined) {
    this.frameEl = el?.nativeElement ?? null;
    if (this.frameEl && this.pendingSrcdoc !== null) {
      this.frameEl.setAttribute('srcdoc', this.pendingSrcdoc);
      this.pendingSrcdoc = null;
    }
  }

  ngOnInit(): void {
    this.fetchHtml(false);
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
    this.sub = null;
  }

  /** Load images：显式 opt-in，重取 ?images=1（仍走同一消毒 + iframe 路径）。 */
  protected loadImages(): void {
    this.fetchHtml(true);
  }

  private fetchHtml(imagesAllowed: boolean): void {
    this.sub?.unsubscribe();
    const url = `/api/interviews/mailbox/assets/${this.assetId()}/html` +
      (imagesAllowed ? '?images=1' : '');
    this.sub = this.http
      .get(url, { observe: 'response', responseType: 'text' })
      .subscribe({
        next: (resp: HttpResponse<string>) => {
          const clean = sanitizeMailHtml(resp.body ?? '', DOMPurify);
          if (isMailHtmlEmpty(clean)) {
            this.fail();
            return;
          }
          this.render(clean, resp.headers.get('X-Images-Blocked') === '1');
        },
        // 单一失败路径：任何失败（HTTP 状态或取信拒绝）都走同一 noHtml，
        // 不分状态码分支，不留空 iframe，不以错误态替代正文。
        error: () => this.fail(),
      });
  }

  private render(cleanHtml: string, imagesBlocked: boolean): void {
    this.hasHtml.set(true);
    this.showToggle.set(imagesBlocked);
    const srcdoc = buildMailSrcdoc(cleanHtml);
    if (this.frameEl) this.frameEl.setAttribute('srcdoc', srcdoc);
    else this.pendingSrcdoc = srcdoc;
  }

  private fail(): void {
    this.hasHtml.set(false);
    this.showToggle.set(false);
    this.noHtml.emit();
  }

  /** iframe load 后量高：帧内 scrollHeight，封顶 1600px，超高帧内滚动。 */
  protected onFrameLoad(): void {
    const frame = this.frameEl;
    if (!frame) return;
    const doc = frame.contentDocument;
    if (!doc) return;
    const h = Math.max(
      doc.body?.scrollHeight ?? 0,
      doc.documentElement?.scrollHeight ?? 0,
    );
    if (h > 0) frame.style.height = Math.min(h, 1600) + 'px';
  }
}
