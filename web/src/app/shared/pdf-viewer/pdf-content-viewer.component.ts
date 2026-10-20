import { Component, Input, OnChanges, SimpleChanges, signal, inject } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { getPdfjs } from '../../core/utils/pdf-text';

/**
 * PDF 内容查看器:把每页渲染成图片,去掉浏览器自带 PDF 工具栏,
 * 只干净显示文档内容。支持滚动翻页。
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
    <div class="pdfv-pages">
      @for (url of pageUrls(); track $index) {
        <img class="pdfv-page" [src]="url" [alt]="'Page ' + ($index + 1)" />
      }
    </div>
  `,
  styles: [`
    .pdfv-pages { display: flex; flex-direction: column; gap: 12px; align-items: center; }
    .pdfv-page { width: 100%; max-width: 800px; background: #fff;
                 box-shadow: 0 1px 4px rgba(0,0,0,.12); border-radius: 4px; }
    .pdfv-loading, .pdfv-error { text-align: center; padding: 24px; color: #666; }
  `]
})
export class PdfContentViewerComponent implements OnChanges {
  @Input() pdfUrl: string | null = null;   // blob: 或 http(s): URL
  @Input() scale = 1.6;                     // 渲染清晰度

  private readonly sanitizer = inject(DomSanitizer);
  readonly pageUrls = signal<SafeUrl[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  async ngOnChanges(changes: SimpleChanges): Promise<void> {
    if (!changes['pdfUrl']) return;
    this.pageUrls.set([]);
    this.error.set(null);
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
