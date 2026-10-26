import {
  Component, EventEmitter, Inject, Input, Output, inject
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { Signal } from '@angular/core';

import { DeletedMaterialDto } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  TrashItemView, TrashLoadState, trashItemView, shouldShowRestoreAll,
  shouldShowTrashEmpty, shouldShowTrashError, TRASH_DATE_FORMAT
} from './material-trash.helpers';

/**
 * ★ 票124:回收站对话框的数据契约。
 *
 * ★ 契约 C2:清单与状态由 PracticeMaterialTree 持有(树的状态域),
 *   对话框只读信号、只抛回调 —— 不持有第二份树状态,也不裸调 ApiClient。
 */
export interface MaterialTrashDialogData {
  /** 回收站清单(顶层已删节点)。 */
  items: Signal<DeletedMaterialDto[]>;
  /** 加载态:loading / loaded / failed。 */
  state: Signal<TrashLoadState>;
  /** 正在恢复中的素材 id(null = 无进行中的恢复)。 */
  restoringId: Signal<string | null>;
  /** 恢复某一项。 */
  onRestore: (id: string) => void;
  /** 全部恢复。 */
  onRestoreAll: () => void;
  /** 重新加载清单(失败态的重试)。 */
  onRetry: () => void;
}

/**
 * ★ 票124:素材回收站对话框。
 *
 * 形态镜像同目录 material-transfer-dialog(全站弹窗语言:白卡片、圆角 14、
 * 标题栏「图标 + 标题 + 右侧关闭」),文案镜像 Tech Stack 已上线的回收站
 * (ts.trash.*,V20/V21)。
 *
 * ★ 纪律:加载失败**绝不渲染成空态**(事故教训,规格 §8.2)——
 *   empty 只在"请求成功且确实为空"时出现;失败态另有文案 + 重试。
 */
@Component({
  selector: 'app-material-trash-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule,
    MatTooltipModule, MatProgressSpinnerModule, MatDialogModule
  ],
  templateUrl: './material-trash-dialog.component.html',
  styleUrl: './material-trash-dialog.component.scss'
})
export class MaterialTrashDialogComponent {
  private readonly i18n = inject(I18nService);
  private readonly data = inject<MaterialTrashDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<MaterialTrashDialogComponent>>(MatDialogRef);

  t = (key: string): string => this.i18n.t(key);

  /** 插值({count} 之类)。i18n.tf 是多占位符版本。 */
  tf = (key: string, params: Record<string, string | number>): string => this.i18n.tf(key, params);

  // ---------- 受控输出 ----------

  /** 用户点了某一项的「恢复」。 */
  @Output() restoreItem = new EventEmitter<string>();

  /** 用户点了「全部恢复」。 */
  @Output() restoreAll = new EventEmitter<void>();

  /** 用户点了关闭。 */
  @Output() closed = new EventEmitter<void>();

  /** 用户点了「重试」(加载失败态)。 */
  @Output() retry = new EventEmitter<void>();

  // ---------- 受控输入(来自 MAT_DIALOG_DATA 的信号) ----------

  /** 回收站清单(顶层已删节点)。 */
  get items(): DeletedMaterialDto[] { return this.data.items() ?? []; }

  /** 加载态:loading / loaded / failed。 */
  get state(): TrashLoadState { return this.data.state(); }

  /** 正在恢复中的素材 id(null = 无进行中的恢复)。 */
  get restoringId(): string | null { return this.data.restoringId(); }

  /** 顶层项总数(标题行显示)。 */
  get total(): number { return this.items.length; }

  /** 「全部恢复」按钮是否显示(顶层项 ≥2 时才显示 —— 规格 §8.2)。 */
  get showRestoreAll(): boolean { return shouldShowRestoreAll(this.total); }

  /** 空态是否显示(仅"加载成功 + 确实为空")。 */
  get showEmpty(): boolean { return shouldShowTrashEmpty(this.state, this.items); }

  /** 加载失败态是否显示。 */
  get showError(): boolean { return shouldShowTrashError(this.state); }

  /** 加载中是否显示。 */
  get showLoading(): boolean { return this.state === 'loading'; }

  /**
   * 清单项 → 字段视图(字段与显隐条件由 helpers 统一判定,模板只渲染)。
   */
  view(item: DeletedMaterialDto): TrashItemView {
    return trashItemView(item, (iso) => this.formatTime(iso), (k, p) => this.tf(k, p));
  }

  /** 该项是否正在恢复(用于置灰按钮)。 */
  isRestoring(id: string): boolean { return this.restoringId === id; }

  /** 「全部恢复」是否正在进行。 */
  get restoringAll(): boolean { return this.restoringId === '__all__'; }

  // ---------- 交互 ----------

  /** 恢复某一项:交回宿主(契约 C2:动作由树的状态域发起)。 */
  onRestore(id: string): void {
    this.restoreItem.emit(id);
    this.data.onRestore(id);
  }

  /** 全部恢复:交回宿主。 */
  onRestoreAll(): void {
    this.restoreAll.emit();
    this.data.onRestoreAll();
  }

  /** 重新加载清单。 */
  onRetry(): void {
    this.retry.emit();
    this.data.onRetry();
  }

  /** 关闭对话框。 */
  onClose(): void {
    this.closed.emit();
    this.dialogRef.close();
  }

  /**
   * 删除时间的本地格式:规格 §8.2 逐字要求 `yyyy-MM-dd HH:mm`。
   * 手写而不引 DatePipe —— DatePipe 的 locale 变体会随语言设置变出
   * "10/24/2026, 4:53 PM" 之类,与规格逐字口径不符。
   */
  private formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    const pad = (n: number): string => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} `
         + `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  /** 供 spec 断言:格式常量与实现同源。 */
  readonly dateFormat = TRASH_DATE_FORMAT;
}
