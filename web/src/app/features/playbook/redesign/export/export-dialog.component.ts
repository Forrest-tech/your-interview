import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar } from '@angular/material/snack-bar';
import {
  ExportDoc, ExportFormat, FORMAT_EXT, generateExport, downloadBlob
} from './export-doc';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { makeV2Fmt, makeV2T } from '../v2.helpers';

export interface ExportToggle {
  key: string;
  label: string;
  locked?: boolean;
  defaultOn: boolean;
}

export interface ExportDialogData {
  /** 对话框标题,如「导出本轮实录」。 */
  title: string;
  subtitle?: string;
  /** 文件名(不带扩展名),可编辑。 */
  fileBase: string;
  toggles: ExportToggle[];
  /** 记忆键:实录 / 备考各一份选择记忆。 */
  storageKey: string;
  buildDoc: (selected: Set<string>) => ExportDoc;
}

interface ExportPrefs { selected: string[]; format: ExportFormat; }

/**
 * 统一导出对话框(2026-10-20 定稿):面试实录与备考报告共用。
 * 内容勾选 → 格式四选 → 文件名可编辑(扩展名随格式自动变)→ 仅下载到本机。
 * 时间戳不是选项:原文自带,恒显示恒导出。选择按 storageKey 记忆。
 */
@Component({
  selector: 'app-export-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      @if (data.subtitle) { <p class="sub">{{ data.subtitle }} · {{ t('pb.v2.x.subtitleSuffix') }}</p> }

      <div class="sec-title">{{ t('pb.v2.x.includes') }}</div>
      <p class="hint">{{ t('pb.v2.x.tsHint') }}</p>
      <div class="opts">
        @for (tg of data.toggles; track tg.key) {
          <mat-checkbox [checked]="selected().has(tg.key)" [disabled]="tg.locked"
                        (change)="toggle(tg.key, $event.checked)">
            {{ tg.label }}{{ tg.locked ? t('pb.v2.x.required') : '' }}
          </mat-checkbox>
        }
      </div>

      <div class="sec-title">{{ t('pb.v2.x.format') }}</div>
      <div class="fcards">
        @for (f of formats(); track f.key) {
          <button type="button" class="fcard" [class.on]="format() === f.key"
                  (click)="format.set(f.key)">
            <div class="fn">{{ f.label }}</div>
            <div class="fd">{{ f.ext }}</div>
          </button>
        }
      </div>

      <div class="sec-title">{{ t('pb.v2.x.fileName') }}</div>
      <div class="fname-row">
        <input class="fname-input" [ngModel]="fileBase()" (ngModelChange)="fileBase.set($event)">
        <span class="ext">{{ FORMAT_EXT[format()] }}</span>
      </div>
      @if (error()) { <p class="err">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary" [disabled]="busy()" (click)="doExport()">
        {{ busy() ? t('pb.v2.x.generating') : t('pb.v2.x.exportDownload') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .sub { color: var(--yi-text-muted, #475569); font-size: 13px; margin: 0 0 4px; }
    .sec-title { font-size: 13px; font-weight: 700; color: var(--yi-text-muted, #475569); margin: 14px 0 6px; }
    .hint { font-size: 13px; color: var(--yi-text-muted, #475569); margin: 0 0 4px; }
    .opts { display: flex; gap: 6px 16px; flex-wrap: wrap; }
    .fcards { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
    .fcard { border: 1px solid var(--yi-border, #e2e8f0); border-radius: 10px; padding: 10px 6px;
             text-align: center; background: transparent; color: inherit; cursor: pointer; }
    .fcard.on { border-color: var(--yi-accent, #2563eb); outline: 1.5px solid var(--yi-accent, #2563eb); }
    .fcard .fn { font-size: 14px; font-weight: 700; }
    .fcard .fd { font-size: 12px; color: var(--yi-text-muted, #475569); margin-top: 2px; }
    .fname-row { display: flex; align-items: center; gap: 8px; }
    .fname-input { flex: 1; border: 1px solid var(--yi-border, #e2e8f0); border-radius: 8px;
                   padding: 8px 11px; font-size: 14px; background: transparent; color: inherit; }
    .ext { font-size: 14px; font-weight: 700; color: var(--yi-text-muted, #475569); }
    .err { color: #dc2626; font-size: 13px; }
  `]
})
export class ExportDialogComponent {
  readonly data = inject(MAT_DIALOG_DATA) as ExportDialogData;
  private readonly ref = inject(MatDialogRef<ExportDialogComponent>);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly FORMAT_EXT = FORMAT_EXT;
  readonly formats = computed<{ key: ExportFormat; label: string; ext: string }[]>(() => [
    { key: 'pdf', label: 'PDF', ext: '.pdf' },
    { key: 'docx', label: 'Word', ext: '.docx' },
    { key: 'jpg', label: this.t('pb.v2.x.fmtImage'), ext: this.t('pb.v2.x.fmtJpgExt') },
    { key: 'md', label: 'Markdown', ext: '.md' }
  ]);

  readonly selected = signal<Set<string>>(new Set());
  readonly format = signal<ExportFormat>('pdf');
  readonly fileBase = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    const prefs = this.loadPrefs();
    const initial = new Set<string>();
    for (const tg of this.data.toggles) {
      if (tg.locked) { initial.add(tg.key); continue; }
      if (prefs) { if (prefs.selected.includes(tg.key)) initial.add(tg.key); }
      else if (tg.defaultOn) initial.add(tg.key);
    }
    this.selected.set(initial);
    if (prefs) this.format.set(prefs.format);
    this.fileBase.set(this.data.fileBase);
  }

  private prefsKey(): string { return `yi.exportPrefs.${this.data.storageKey}`; }

  private loadPrefs(): ExportPrefs | null {
    try {
      const raw = localStorage.getItem(this.prefsKey());
      return raw ? JSON.parse(raw) as ExportPrefs : null;
    } catch { return null; }
  }

  toggle(key: string, on: boolean): void {
    const next = new Set(this.selected());
    if (on) next.add(key); else next.delete(key);
    this.selected.set(next);
  }

  async doExport(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      try {
        localStorage.setItem(this.prefsKey(), JSON.stringify({
          selected: [...this.selected()], format: this.format()
        } satisfies ExportPrefs));
      } catch { /* 记忆失败不阻塞导出 */ }
      const doc = this.data.buildDoc(this.selected());
      const blob = await generateExport(this.format(), doc);
      const name = (this.fileBase().trim() || this.data.fileBase) + FORMAT_EXT[this.format()];
      downloadBlob(blob, name);
      this.snack.open(this.fmt('pb.v2.x.exported', { name }), this.t('common.close'), { duration: 3000 });
      this.ref.close(true);
    } catch (e) {
      this.error.set(this.fmt('pb.v2.x.exportFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.x.unknownError') }));
    } finally {
      this.busy.set(false);
    }
  }
}
