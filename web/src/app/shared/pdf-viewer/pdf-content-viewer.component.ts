import { Component, ElementRef, HostListener, Input, OnChanges, OnDestroy, SimpleChanges, signal, inject } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { getPdfjs } from '../../core/utils/pdf-text';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * PDF 内容查看器:把每页渲染成图片,去掉浏览器自带 PDF 工具栏,
 * 只干净显示文档内容。预览框高一页,框内滚动翻页(滚动停稳吸附到页首)。
 *
 * 票 147:缩放工具栏(默认关闭,opt-in)。详情对话框开启它,Resume 页与
 * Playbook 详情不传,两页零变化。缩放两段:即时 CSS(max-width 跟 zoom 走)
 * + ≥125% 档停手约 300ms 后高清重渲染(renderScale = 1.6 × zoom,上限 3.2),
 * 重渲染按页索引恢复阅读位置,不丢页、不闪白(旧图保留到新图就绪)。
 */
@Component({
  selector: 'app-pdf-content-viewer',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  template: `
    @if (showZoomControls) {
      <div class="pdfv-zoombar">
        <button mat-stroked-button class="pdfv-zoom-btn pdfv-zoom-out"
                (click)="zoomOut()" [disabled]="zoom() <= 0.5"
                [attr.aria-label]="t('pdfv.zoomOut')" [title]="t('pdfv.zoomOut')">
          <mat-icon>remove</mat-icon>
        </button>
        <span class="pdfv-zoom-pct">{{ zoomPct() }}</span>
        <button mat-stroked-button class="pdfv-zoom-btn pdfv-zoom-in"
                (click)="zoomIn()" [disabled]="zoom() >= 2"
                [attr.aria-label]="t('pdfv.zoomIn')" [title]="t('pdfv.zoomIn')">
          <mat-icon>add</mat-icon>
        </button>
        <button mat-stroked-button class="pdfv-zoom-fit"
                (click)="fitWidth()">{{ t('pdfv.fitWidth') }}</button>
      </div>
    }
    @if (loading()) {
      <div class="pdfv-loading">Loading…</div>
    }
    @if (error()) {
      <div class="pdfv-error">{{ error() }}</div>
    }
    <div class="pdfv-pages" [style.height.px]="frameHeight()"
         [style.align-items]="alignStart() ? 'flex-start' : 'center'">
      @for (url of pageUrls(); track $index) {
        <img class="pdfv-page" [src]="url" [alt]="'Page ' + ($index + 1)" (load)="onPageLoad()"
             [style.max-width.px]="BASE_MAX * zoom()" />
      }
    </div>
  `,
  styles: [`
    .pdfv-zoombar { display: flex; align-items: center; gap: 8px; padding: 0 0 8px; }
    .pdfv-zoombar .pdfv-zoom-btn { min-width: 0; padding: 0 8px; }
    .pdfv-zoombar .pdfv-zoom-fit { padding: 0 12px; }
    .pdfv-zoom-pct { min-width: 48px; text-align: center; font-size: 13px; font-weight: 600; color: #1f2430; }
    .pdfv-pages { display: flex; flex-direction: column; gap: 12px; align-items: center;
                  overflow-y: auto; overflow-x: auto; overscroll-behavior: contain;
                  scroll-snap-type: y mandatory; }
    /* 滚动条:2026-10-21 设计稿确认参数,纯 webkit —— 不要加 scrollbar-width/scrollbar-color,
       Chrome 会因此忽略 ::-webkit-scrollbar 样式。 */
    .pdfv-pages::-webkit-scrollbar { width: 10px; }
    .pdfv-pages::-webkit-scrollbar-track { background: #e1e4ec; border-radius: 5px; }
    .pdfv-pages::-webkit-scrollbar-thumb { background: #7d8797; border-radius: 5px; }
    .pdfv-pages::-webkit-scrollbar-thumb:hover { background: #2e3a8c; }
    .pdfv-page { width: 100%; background: #ffffff; flex: none;
                 scroll-snap-align: start;
                 box-shadow: 0 1px 4px rgba(31, 36, 48, .12); border-radius: 4px; }
    .pdfv-loading, .pdfv-error { text-align: center; padding: 24px; color: #5b6270; }
  `]
})
export class PdfContentViewerComponent implements OnChanges, OnDestroy {
  @Input() pdfUrl: string | null = null;   // blob: 或 http(s): URL
  @Input() scale = 1.6;                     // 渲染清晰度
  /** 票 147:缩放工具栏开关,默认关闭;仅详情对话框开启。 */
  @Input() showZoomControls = false;

  private readonly sanitizer = inject(DomSanitizer);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly i18n = inject(I18nService);
  readonly pageUrls = signal<SafeUrl[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** 预览框高度 = 第一页实际显示高度;量测前为 null(自然高度,不影响加载态)。 */
  readonly frameHeight = signal<number | null>(null);
  /** 票 147:缩放系数,1 = 100%(今天行为);步进 0.25,范围 [0.5, 2]。 */
  readonly zoom = signal(1);
  /** 票 147:页宽超出容器时左对齐(否则居中页左右溢出、左侧滚不到)。 */
  readonly alignStart = signal(false);

  /** 100% 档的页宽上限(px);缩放后上限 = BASE_MAX × zoom。 */
  readonly BASE_MAX = 800;

  /** 当前渲染倍率(初始 = scale 输入);缩放重渲染时改写。 */
  private renderScale = 1.6;
  private zoomTimer: ReturnType<typeof setTimeout> | null = null;
  /** 渲染代次:快速连点缩放时旧重渲染不得覆盖新图(竞态守卫)。 */
  private renderSeq = 0;

  t = (key: string): string => this.i18n.t(key);

  zoomPct(): string {
    return Math.round(this.zoom() * 100) + '%';
  }

  setZoom(z: number): void {
    const clamped = Math.min(2, Math.max(0.5, Math.round(z * 4) / 4));
    if (clamped === this.zoom()) return;
    this.zoom.set(clamped);
    this.refreshLayout();
    // ≥125% 档停手约 300ms 后高清重渲染;回落 <125% 时降回基础倍率。
    if (this.zoomTimer) clearTimeout(this.zoomTimer);
    this.zoomTimer = setTimeout(() => void this.maybeRerender(), 300);
  }

  zoomIn(): void { this.setZoom(this.zoom() + 0.25); }
  zoomOut(): void { this.setZoom(this.zoom() - 0.25); }
  /** 适应宽度 = 回到 100%(今天显示效果)。 */
  fitWidth(): void { this.setZoom(1); }

  /** 票 147:供抽屉拖拽结束时显式调用——抽屉改宽不触发 window resize,框高须手动跟上。 */
  remeasure(): void {
    this.refreshLayout();
  }

  ngOnDestroy(): void {
    if (this.zoomTimer) clearTimeout(this.zoomTimer);
  }

  /** 以首图当前显示高度为准设定框高;窗口缩放后图片宽度变、高度随之变,重算。 */
  private measureFirstPage(): void {
    const img = this.host.nativeElement.querySelector('.pdfv-page') as HTMLImageElement | null;
    if (img && img.clientHeight > 0) this.frameHeight.set(img.clientHeight);
  }

  /** 页宽超出容器时左对齐,未超出时居中(保持今天外观)。 */
  private updateAlign(): void {
    const pagesEl = this.host.nativeElement.querySelector('.pdfv-pages') as HTMLElement | null;
    if (!pagesEl) return;
    this.alignStart.set(this.BASE_MAX * this.zoom() > pagesEl.clientWidth);
  }

  private refreshLayout(): void {
    requestAnimationFrame(() => { this.measureFirstPage(); this.updateAlign(); });
  }

  onPageLoad(): void {
    this.measureFirstPage();
    this.updateAlign();
  }

  @HostListener('window:resize')
  onResize(): void {
    this.refreshLayout();
  }

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!changes['pdfUrl']) return;
    if (this.zoomTimer) clearTimeout(this.zoomTimer);
    this.renderSeq++;
    this.pageUrls.set([]);
    this.error.set(null);
    this.frameHeight.set(null);
    this.zoom.set(1);
    this.renderScale = this.scale;
    const pagesEl = this.host.nativeElement.querySelector('.pdfv-pages') as HTMLElement | null;
    if (pagesEl) pagesEl.scrollTop = 0;
    if (!this.pdfUrl) return;
    await this.renderPdf();
  }

  /** 渲染当前文档(新文档与缩放重渲染共用);旧图保留到新图就绪,不闪白。
   *  代次守卫:被更新渲染超车时不写图、不写错、不清 loading(新渲染负责收尾)。 */
  private async renderPdf(seq = this.renderSeq): Promise<void> {
    if (!this.pdfUrl) return;
    this.loading.set(true);
    try {
      const pdfjs = getPdfjs();
      const data = await (await fetch(this.pdfUrl)).arrayBuffer();
      const pdf = await pdfjs.getDocument({ data }).promise;
      const urls: SafeUrl[] = [];
      for (let i = 1; i <= pdf.numPages; i++) {
        const page = await pdf.getPage(i);
        const viewport = page.getViewport({ scale: this.renderScale });
        const canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        await page.render({ canvasContext: canvas.getContext('2d')!, viewport }).promise;
        const blob = await new Promise<Blob | null>(res => canvas.toBlob(res, 'image/png'));
        if (blob) urls.push(this.sanitizer.bypassSecurityTrustUrl(URL.createObjectURL(blob)));
      }
      if (seq === this.renderSeq) this.pageUrls.set(urls);
    } catch (e: any) {
      if (seq === this.renderSeq) this.error.set(e?.message || 'Failed to render PDF');
    } finally {
      if (seq === this.renderSeq) this.loading.set(false);
    }
  }

  /** 缩放停手后的高清重渲染:目标倍率不变则跳过;重渲染后按页索引恢复位置。 */
  private async maybeRerender(): Promise<void> {
    const target = this.zoom() >= 1.25 ? Math.min(this.scale * this.zoom(), 3.2) : this.scale;
    if (target === this.renderScale || !this.pdfUrl) return;
    const pagesEl = this.host.nativeElement.querySelector('.pdfv-pages') as HTMLElement | null;
    const imgs = this.host.nativeElement.querySelectorAll('.pdfv-page');
    let idx = 0;
    if (pagesEl && imgs.length > 0) {
      const top = pagesEl.scrollTop;
      for (let i = 0; i < imgs.length; i++) {
        const el = imgs[i] as HTMLElement;
        if (el.offsetTop + el.clientHeight > top + 10) { idx = i; break; }
        idx = i;
      }
    }
    this.renderScale = target;
    const seq = ++this.renderSeq;
    await this.renderPdf(seq);
    if (seq !== this.renderSeq) return; // 已被更新的缩放超车,不恢复旧位置
    this.restoreScrollToPage(idx);
  }

  /** 等目标页图铺好后再滚到其页首(滚动吸附点就是页首,不会再弹走)。 */
  private restoreScrollToPage(idx: number): void {
    let frames = 0;
    const tick = (): void => {
      frames++;
      const pagesEl = this.host.nativeElement.querySelector('.pdfv-pages') as HTMLElement | null;
      const imgs = this.host.nativeElement.querySelectorAll('.pdfv-page');
      const el = imgs[idx] as HTMLElement | undefined;
      if (pagesEl && el && el.clientHeight > 0) {
        pagesEl.scrollTop = el.offsetTop;
        return;
      }
      if (frames < 60) requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  }
}
