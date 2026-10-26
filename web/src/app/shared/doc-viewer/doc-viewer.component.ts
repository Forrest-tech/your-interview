import { Component, computed, inject, input, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 通用文档阅读器(从 AI Practice 阅读卡片抽出的可复用模块)。
 *
 * 包含:标题栏 + 缩放控件(- % +) + 白底阅读卡片。
 * 缩放状态组件内自持,不污染父组件。
 *
 * 用法:
 *   <app-doc-viewer [title]="..." [content]="..." />
 *   <app-doc-viewer [title]="..." [content]="..." [editable]="true"
 *                   (contentChange)="onSave($event)" />
 */
@Component({
  selector: 'app-doc-viewer',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  templateUrl: './doc-viewer.component.html',
  styleUrl: './doc-viewer.component.scss'
})
export class DocViewerComponent {
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** 文档标题 */
  readonly title = input<string>('');
  /** 正文内容(纯文本,保留换行) */
  readonly content = input<string>('');
  /** 是否显示缩放控件 */
  readonly showZoom = input<boolean>(true);
  /** 是否可编辑(右上角铅笔按钮) */
  readonly editable = input<boolean>(false);

  /** 编辑后保存,传出新内容 */
  readonly contentChange = output<string>();

  private static readonly BASE_PX = 17;
  private static readonly MIN = 50;
  private static readonly MAX = 300;
  private static readonly STEP = 10;

  readonly zoom = signal(100);
  readonly editing = signal(false);
  readonly draft = signal('');

  readonly fontSize = computed(() =>
    Math.round((DocViewerComponent.BASE_PX * this.zoom()) / 100));

  readonly canDecrease = computed(() => this.zoom() > DocViewerComponent.MIN);
  readonly canIncrease = computed(() => this.zoom() < DocViewerComponent.MAX);

  zoomIn(): void {
    if (this.canIncrease()) this.zoom.set(this.zoom() + DocViewerComponent.STEP);
  }

  zoomOut(): void {
    if (this.canDecrease()) this.zoom.set(this.zoom() - DocViewerComponent.STEP);
  }

  startEdit(): void {
    this.draft.set(this.content());
    this.editing.set(true);
  }

  saveEdit(): void {
    this.editing.set(false);
    this.contentChange.emit(this.draft());
  }

  cancelEdit(): void {
    this.editing.set(false);
  }
}
