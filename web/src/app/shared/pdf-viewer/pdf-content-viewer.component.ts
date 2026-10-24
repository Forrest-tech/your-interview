import { Component, ElementRef, HostListener, Input, OnChanges, SimpleChanges, signal, inject } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { getPdfjs } from '../../core/utils/pdf-text';

/**
 * PDF 内容查看器:把每页渲染成图片,去掉浏览器自带 PDF 工具栏,
 * 只干净显示文档内容。预览框高一页,框内滚动翻页(滚动停稳吸附到页首)。
 */
@Component({
  selector: 'app-pdf-content-viewer',
  standalone: true,
  template: `
    @if (loading()) {
      <div class="pdfv-loading">Loading…</div>
    }
    @if (error()) {
      <div class="pdfv-error">{{ error() }}</div>
    }
    <div class="pdfv-pages" [style.height.px]="frameHeight()">
      @for (url of pageUrls(); track $index) {
        <img class="pdfv-page" [src]="url" [alt]="'Page ' + ($index + 1)" (load)="onPageLoad()" />
      }
    </div>
  `,
  styles: [`
    .pdfv-pages { display: flex; flex-direction: column; gap: 12px; align-items: center;
                  overflow-y: auto; overscroll-behavior: contain;
                  scroll-snap-type: y mandatory; }
    /* 滚动条:2026-10-21 设计稿确认参数,纯 webkit —— 不要加 scrollbar-width/scrollbar-color,
       Chrome 会因此忽略 ::-webkit-scrollbar 样式。 */
    .pdfv-pages::-webkit-scrollbar { width: 10px; }
    .pdfv-pages::-webkit-scrollbar-track { background: #e1e4ec; border-radius: 5px; }
    .pdfv-pages::-webkit-scrollbar-thumb { background: #7d8797; border-radius: 5px; }
    .pdfv-pages::-webkit-scrollbar-thumb:hover { background: #2e3a8c; }
    .pdfv-page { width: 100%; max-width: 800px; background: #ffffff; flex: none;
                 scroll-snap-align: start;
                 box-shadow: 0 1px 4px rgba(31, 36, 48, .12); border-radius: 4px; }
    .pdfv-loading, .pdfv-error { text-align: center; padding: 24px; color: #5b6270; }
  `]
})
export class PdfContentViewerComponent implements OnChanges {
  @Input() pdfUrl: string | null = null;   // blob: 或 http(s): URL
  @Input() scale = 1.6;                     // 渲染清晰度

  private readonly sanitizer = inject(DomSanitizer);
  private readonly host = inject(ElementRef<HTMLElement>);
  readonly pageUrls = signal<SafeUrl[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** 预览框高度 = 第一页实际显示高度;量测前为 null(自然高度,不影响加载态)。 */
  readonly frameHeight = signal<number | null>(null);

  /** 以首图当前显示高度为准设定框高;窗口缩放后图片宽度变、高度随之变,重算。 */
  private measureFirstPage(): void {
    const img = this.host.nativeElement.querySelector('.pdfv-page') as HTMLImageElement | null;
    if (img && img.clientHeight > 0) this.frameHeight.set(img.clientHeight);
  }

  onPageLoad(): void {
    this.measureFirstPage();
  }

  @HostListener('window:resize')
  onResize(): void {
    requestAnimationFrame(() => this.measureFirstPage());
  }

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!changes['pdfUrl']) return;
    this.pageUrls.set([]);
    this.error.set(null);
    this.frameHeight.set(null);
    const pagesEl = this.host.nativeElement.querySelector('.pdfv-pages') as HTMLElement | null;
    if (pagesEl) pagesEl.scrollTop = 0;
    if (!this.pdfUrl) return;
    this.loading.set(true);
    try {
      const pdfjs = getPdfjs();
      const data = await (await fetch(this.pdfUrl)).arrayBuffer();
      const pdf = await pdfjs.getDocument({ data }).promise;
      const urls: SafeUrl[] = [];
      for (let i = 1; i <= pdf.numPages; i++) {
        const page = await pdf.getPage(i);
        const viewport = page.getViewport({ scale: this.scale });
        const canvas = document.createElement('canvas');
        canvas.width = viewport.width;
        canvas.height = viewport.height;
        await page.render({ canvasContext: canvas.getContext('2d')!, viewport }).promise;
        const blob = await new Promise<Blob | null>(res => canvas.toBlob(res, 'image/png'));
        if (blob) urls.push(this.sanitizer.bypassSecurityTrustUrl(URL.createObjectURL(blob)));
      }
      this.pageUrls.set(urls);
    } catch (e: any) {
      this.error.set(e?.message || 'Failed to render PDF');
    } finally {
      this.loading.set(false);
    }
  }
}
