import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { I18nService } from '../../core/i18n/i18n.service';
import { ApiClient } from '../../core/api/api-client';
import { extractPdfText } from '../../core/utils/pdf-text';
import { PdfContentViewerComponent } from '../../shared/pdf-viewer/pdf-content-viewer.component';
import { resumeSaveBlock } from './resume.helpers';

/**
 * 简历配置页(独立页面,不再放在 profile/account 里)。
 *
 * 简历正文的真源是 Jobs API(/api/jobs/resume-text);localStorage 仅作本机草稿缓存,
 * 备战材料生成、求职信生成时从真源读入自动带入。
 * 支持 txt/md 直接读取、PDF 自动提取文字(pdf.js)、Word 复制粘贴。
 *
 * 数据键与旧版 profile 页完全一致(yi-my-resume / yi-my-resume-meta),
 * 老用户已存的简历无缝沿用,不丢数据。
 */
@Component({
  selector: 'app-resume',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatIconModule,
    MatButtonModule, MatFormFieldModule, MatInputModule, PdfContentViewerComponent
  ],
  templateUrl: './resume.component.html',
  styleUrl: './resume.component.scss'
})
export class ResumeComponent implements OnInit {
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  private readonly api = inject(ApiClient);

  readonly t = (key: string): string => this.i18n.t(key);

  /** 简历草稿(文本域绑定)。 */
  resumeDraft = '';
  resumeFileName = signal('');
  /** 待上传的 PDF 文件(选完文件、点保存后才真正上传)。 */
  private pendingPdf: File | null = null;
  /** PDF 预览 URL(blob)。null = 后端没有存 PDF。 */
  readonly resumePdfUrl = signal<SafeResourceUrl | null>(null);
  readonly resumePdfRawUrl = signal<string | null>(null);
  /** 有已存简历时默认展示,点 Edit 才进入上传/替换;无简历时默认编辑。 */
  readonly editing = signal(true);

  /** 是否有已存简历(PDF 或文本)。 */
  private hasSavedResume(): boolean {
    return !!this.resumePdfRawUrl() || !!this.savedResumePreview().trim();
  }

  /** 根据是否有已存简历同步编辑态。 */
  private syncEditing(): void {
    this.editing.set(!this.hasSavedResume());
  }
  savedResumePreview = signal('');
  savedResumeDate = signal('');
  private readonly resumeKey = 'yi-my-resume';
  private readonly resumeMetaKey = 'yi-my-resume-meta';
  private readonly sanitizer = inject(DomSanitizer);

  ngOnInit(): void {
    this.refreshResumePreview();
    this.loadPdfPreview();
    // 有已存简历时,把全文载入编辑框,方便直接改。
    try {
      const text = localStorage.getItem(this.resumeKey) ?? '';
      if (text.trim()) {
        this.resumeDraft = text;
      } else {
        // localStorage 为空时,从后端拉(用户可能在别处存过)
        this.api.get<{ resumeText: string | null }>('/api/jobs/resume-text').subscribe({
          next: (r) => {
            if (r.resumeText?.trim()) {
              this.resumeDraft = r.resumeText;
              try { localStorage.setItem(this.resumeKey, r.resumeText); } catch { /* 忽略 */ }
              this.refreshResumePreview();
            }
          },
          error: () => { /* 后端没简历就保持空,不阻断 */ }
        });
      }
    } catch { /* 忽略 */ }
  }

  /** 从后端拉 PDF 预览(有才显示)。 */
  private loadPdfPreview(): void {
    // 元信息:后端是文件名真源(换浏览器/清缓存后 localStorage 没了,这里还能显示)
    this.api.get<{ fileName: string }>('/api/jobs/resume-file/meta').subscribe({
      next: (m) => { if (m.fileName) this.resumeFileName.set(m.fileName); },
      error: () => { /* 没有 PDF 就不显示 */ }
    });
    this.api.getBlob('/api/jobs/resume-file').subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        this.resumePdfRawUrl.set(url);
        this.resumePdfUrl.set(this.sanitizer.bypassSecurityTrustResourceUrl(url));
        this.syncEditing();
      },
      error: () => { this.resumePdfRawUrl.set(null); this.resumePdfUrl.set(null); /* 没有 PDF 就不显示,不阻断 */ }
    });
  }

  saveResume(): void {
    // 空存前端拦截:后端对空文本必回 400(Resume.Empty),先用人话拦下,
    // 不发注定失败的请求;有待上传 PDF 时放行(文件路径不靠文本守卫)。
    if (resumeSaveBlock(this.resumeDraft, !!this.pendingPdf) === 'empty-text') {
      this.snack.open(this.t('profile.resumeEmptyHint'), this.t('common.close'), { duration: 4000 });
      return;
    }
    const text = this.resumeDraft;
    try {
      localStorage.setItem(this.resumeKey, text);
      const meta = JSON.stringify({
        fileName: this.resumeFileName() || '',
        savedAt: new Date().toISOString()
      });
      localStorage.setItem(this.resumeMetaKey, meta);
      this.refreshResumePreview();
    } catch {
      this.snack.open(this.t('profile.resumeSaveFailed'), this.t('common.close'), { duration: 3000 });
      return;
    }
    // 同步到后端 —— 求职信生成、简历匹配都从这里读
    this.api.put('/api/jobs/resume-text', { resumeText: text }).subscribe({
      next: () => {
        this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2500 });
        this.editing.set(false);
      },
      error: () => {
        // 后端同步失败:localStorage 已存,提示用户但不算失败
        this.snack.open(this.t('profile.resumeSavedLocalOnly'), this.t('common.close'), { duration: 4000 });
      }
    });
    // 有待上传的 PDF:一起存到后端(Documents tab 预览 + 去重引用都从这里读)
    if (this.pendingPdf) {
      const fd = new FormData();
      fd.append('file', this.pendingPdf, this.pendingPdf.name);
      fd.append('textContent', text);
      this.api.postForm('/api/jobs/resume-file', fd).subscribe({
        next: () => {
          this.pendingPdf = null;
          this.loadPdfPreview();
        },
        error: () => {
          this.snack.open(this.t('profile.resumePdfSaveFailed'), this.t('common.close'), { duration: 4000 });
        }
      });
    }
  }

  clearResume(): void {
    try {
      localStorage.removeItem(this.resumeKey);
      localStorage.removeItem(this.resumeMetaKey);
    } catch { /* 忽略 */ }
    this.resumeDraft = '';
    this.resumeFileName.set('');
    this.pendingPdf = null;
    this.resumePdfRawUrl.set(null);
    this.resumePdfUrl.set(null);
    this.refreshResumePreview();
    // 同步清空后端
    this.api.put('/api/jobs/resume-text', { resumeText: '' }).subscribe({
      next: () => this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2000 }),
      error: () => this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2000 })
    });
    // 后端 PDF 一起删
    this.api.delete('/api/jobs/resume-file').subscribe({ error: () => { /* 忽略 */ } });
  }

  private refreshResumePreview(): void {
    try {
      const text = localStorage.getItem(this.resumeKey) ?? '';
      const metaRaw = localStorage.getItem(this.resumeMetaKey);
      const meta = metaRaw ? JSON.parse(metaRaw) : {};
      if (text.trim()) {
        // 只作"有已存文本简历"的状态标志用(编辑态/卡片头显隐),不再截取摘录展示。
        this.savedResumePreview.set(text);
        this.savedResumeDate.set(meta.savedAt ? new Date(meta.savedAt).toLocaleDateString() : '');
        if (meta.fileName) this.resumeFileName.set(meta.fileName);
      } else {
        this.savedResumePreview.set('');
        this.savedResumeDate.set('');
      }
    } catch {
      this.savedResumePreview.set('');
    }
    this.syncEditing();
  }

  /** 供备战材料对话框调用:取存好的简历。 */
  static getSavedResume(): string {
    try {
      return localStorage.getItem('yi-my-resume') ?? '';
    } catch {
      return '';
    }
  }

  /** 上传简历文件:txt/md 直接读,PDF 用 pdf.js 提取文字。 */
  async onResumeFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.resumeFileName.set(file.name);
    // PDF 留到点保存时一起上传(和文本保持一致)
    this.pendingPdf = file.name.toLowerCase().endsWith('.pdf') ? file : null;

    try {
      const name = file.name.toLowerCase();
      if (name.endsWith('.txt') || name.endsWith('.md')) {
        this.resumeDraft = await file.text();
        this.snack.open(this.t('profile.resumeExtracted'), this.t('common.close'), { duration: 2500 });
      } else if (name.endsWith('.pdf')) {
        this.snack.open(this.t('profile.resumeExtracting'), this.t('common.close'), { duration: 2000 });
        const text = await this.extractPdfText(file);
        if (text.trim()) {
          this.resumeDraft = text;
          this.snack.open(this.t('profile.resumeExtracted'), this.t('common.close'), { duration: 2500 });
        } else {
          this.snack.open(this.t('profile.resumeExtractFailed'), this.t('common.close'), { duration: 4000 });
        }
      } else {
        // docx 等:提示复制粘贴
        this.snack.open(this.t('profile.resumeDocxHint'), this.t('common.close'), { duration: 5000 });
      }
    } catch {
      this.snack.open(this.t('profile.resumeExtractFailed'), this.t('common.close'), { duration: 4000 });
    }
    input.value = '';
  }

  private async extractPdfText(file: File): Promise<string> {
    return extractPdfText(file);
  }
}
