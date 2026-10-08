import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { I18nService } from '../../core/i18n/i18n.service';
import { ApiClient } from '../../core/api/api-client';

/**
 * 简历配置页(独立页面,不再放在 profile/account 里)。
 *
 * 存一份简历文本到 localStorage,备战材料生成、求职信生成时自动带入。
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
    MatButtonModule, MatFormFieldModule, MatInputModule
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
  savedResumePreview = signal('');
  savedResumeDate = signal('');
  private readonly resumeKey = 'yi-my-resume';
  private readonly resumeMetaKey = 'yi-my-resume-meta';

  ngOnInit(): void {
    this.refreshResumePreview();
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

  saveResume(): void {
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
      },
      error: () => {
        // 后端同步失败:localStorage 已存,提示用户但不算失败
        this.snack.open(this.t('profile.resumeSavedLocalOnly'), this.t('common.close'), { duration: 4000 });
      }
    });
  }

  clearResume(): void {
    try {
      localStorage.removeItem(this.resumeKey);
      localStorage.removeItem(this.resumeMetaKey);
    } catch { /* 忽略 */ }
    this.resumeDraft = '';
    this.resumeFileName.set('');
    this.refreshResumePreview();
    // 同步清空后端
    this.api.put('/api/jobs/resume-text', { resumeText: '' }).subscribe({
      next: () => this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2000 }),
      error: () => this.snack.open(this.t('profile.resumeSaved'), this.t('common.close'), { duration: 2000 })
    });
  }

  private refreshResumePreview(): void {
    try {
      const text = localStorage.getItem(this.resumeKey) ?? '';
      const metaRaw = localStorage.getItem(this.resumeMetaKey);
      const meta = metaRaw ? JSON.parse(metaRaw) : {};
      if (text.trim()) {
        this.savedResumePreview.set(text.slice(0, 300) + (text.length > 300 ? '...' : ''));
        this.savedResumeDate.set(meta.savedAt ? new Date(meta.savedAt).toLocaleDateString() : '');
        if (meta.fileName) this.resumeFileName.set(meta.fileName);
      } else {
        this.savedResumePreview.set('');
        this.savedResumeDate.set('');
      }
    } catch {
      this.savedResumePreview.set('');
    }
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
    const pdfjs = await import('pdfjs-dist');
    // pdf.js v4+ 需要 worker:用本地打包的 assets,比 CDN 可靠
    const pdfjsAny = pdfjs as any;
    if (pdfjsAny.GlobalWorkerOptions && !pdfjsAny.GlobalWorkerOptions.workerSrc) {
      pdfjsAny.GlobalWorkerOptions.workerSrc = 'assets/pdf.worker.min.mjs';
    }
    const buf = await file.arrayBuffer();
    const pdf = await pdfjsAny.getDocument({ data: buf }).promise;
    const parts: string[] = [];
    for (let i = 1; i <= pdf.numPages; i++) {
      const page = await pdf.getPage(i);
      const content = await page.getTextContent();
      const line = (content.items as any[]).map((it: any) => it.str ?? '').join(' ');
      parts.push(line);
    }
    return parts.join('\n');
  }
}
