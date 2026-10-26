import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { I18nService } from '../../../../core/i18n/i18n.service';

/**
 * 票 225：邮件截图弹窗（推翻 210 的卡片内 inline 展开选择，用户新指令）。
 * 只负责展示与缩放：弹窗内大图 + −/百分比/＋ 缩放；不做拖拽平移、旋转、下载。
 * 缩放是弹窗本地状态，与 qa-zoom 的全局字号服务无关——qa-zoom 不适用：
 * 它读写共享 QaZoomService（localStorage 字号偏好），复用到图片上会误改
 * 全局字号；故弹窗内做同款 36×36 无框 −/+ + 百分比显示（211 样式）。
 * 194 铁律：百分比数字恒等于实际缩放比例——标签与图片宽度同取 scale 信号。
 */

export interface EmailShotDialogData {
  /** 截图 blob 对象 URL（调用方建，关闭后由调用方 revoke）。 */
  url: string;
}

export const SHOT_SCALE_MIN = 50;
export const SHOT_SCALE_MAX = 300;
export const SHOT_SCALE_STEP = 25;
export const SHOT_SCALE_DEFAULT = 100;

/** 缩放钳制：50%～300%。 */
export function clampShotScale(v: number): number {
  if (!Number.isFinite(v)) return SHOT_SCALE_DEFAULT;
  return Math.min(SHOT_SCALE_MAX, Math.max(SHOT_SCALE_MIN, Math.round(v)));
}

/** 单步缩放：dir 为 1 放大、-1 缩小，步进 25%，到界停住。 */
export function stepShotScale(v: number, dir: 1 | -1): number {
  return clampShotScale(clampShotScale(v) + dir * SHOT_SCALE_STEP);
}

/**
 * 图片实际渲染宽度（px）：自然宽度 × 缩放比例；自然宽度未知时返回 null
 * （不设宽度样式，图片按自然尺寸显示，百分比仍为默认值 100%，与实际一致）。
 */
export function shotDisplayWidth(naturalWidth: number | null, scale: number): number | null {
  if (naturalWidth === null || naturalWidth <= 0) return null;
  return Math.round(naturalWidth * clampShotScale(scale) / 100);
}

@Component({
  selector: 'app-email-shot-dialog',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="shot-dlg">
      <div class="shot-dlg-head">
        <span class="shot-dlg-titles">
          <span class="shot-dlg-title">{{ i18n.t('pb.v2.m.shotTitle') }}</span>
          <span class="shot-dlg-sub">{{ i18n.t('pb.v2.m.shotSub') }}</span>
        </span>
        <button type=\"button\" class=\"shot-dlg-close\" (click)=\"ref.close()\"
                [title]=\"i18n.t('common.close')\" [attr.aria-label]=\"i18n.t('common.close')\">×</button>
      </div>
      <div class=\"shot-dlg-zoom\" role=\"group\" [attr.aria-label]=\"i18n.t('pb.v2.m.shotZoom')\">
        <button type=\"button\" class=\"zbtn\" (click)=\"zoomOut()\"
                [disabled]=\"scale() <= 50\" [title]=\"i18n.t('zoom.decrease')\">−</button>
        <span class=\"zpct\">{{ scale() }}%</span>
        <button type=\"button\" class=\"zbtn\" (click)=\"zoomIn()\"
                [disabled]=\"scale() >= 300\" [title]=\"i18n.t('zoom.increase')\">＋</button>
      </div>
      <div class=\"shot-dlg-scroll\">
        <img [src]=\"data.url\" [alt]=\"i18n.t('pb.v2.m.imgAltShot')\"
             [style.width.px]=\"displayWidth()\" (load)=\"onImgLoad($event)\">
      </div>
    </div>
  `,
  styles: [`
    .shot-dlg { display: flex; flex-direction: column; max-height: 92vh; }
    .shot-dlg-head { display: flex; gap: 8px; align-items: center; padding: 12px 16px 4px; }
    .shot-dlg-titles { display: flex; flex-direction: column; flex: 1; min-width: 0; }
    .shot-dlg-title { font-size: 14px; font-weight: 600; }
    .shot-dlg-sub { font-size: 13px; color: var(--yi-text-muted, #475569); }
    .shot-dlg-close { display: inline-flex; align-items: center; justify-content: center;
                      width: 36px; height: 36px; border: none; background: transparent;
                      color: var(--yi-text-muted, #475569); font-size: 22px; line-height: 1;
                      cursor: pointer; flex-shrink: 0; }
    .shot-dlg-close:hover { color: var(--yi-text, #0f172a); }
    .shot-dlg-zoom { display: inline-flex; align-items: center; gap: 5px; padding: 4px 16px 8px; }
    .zbtn { display: inline-flex; align-items: center; justify-content: center;
            width: 36px; height: 36px; border: none; background: transparent;
            color: var(--yi-text-muted, #475569); font-size: 19px; line-height: 1; cursor: pointer;
            transition: color .15s ease; }
    .zbtn:hover { background: transparent; border: none; color: var(--yi-text, #0f172a); }
    .zbtn:active { background: transparent; border: none; }
    .zbtn:disabled { opacity: .4; cursor: default; background: transparent; border: none; }
    .zpct { min-width: 52px; text-align: center; font-size: 12px; font-weight: 700;
            border: 1px solid var(--yi-border, #e2e8f0); border-radius: 7px; padding: 4px 4px; }
    .shot-dlg-scroll { overflow: auto; padding: 0 16px 16px; }
    .shot-dlg-scroll img { display: block; max-width: none; }
  `],
})
export class EmailShotDialogComponent {
  readonly ref = inject(MatDialogRef<EmailShotDialogComponent>);
  readonly data = inject<EmailShotDialogData>(MAT_DIALOG_DATA);
  readonly i18n = inject(I18nService);
  readonly scale = signal(SHOT_SCALE_DEFAULT);
  private readonly naturalWidth = signal<number | null>(null);

  /** 与百分比标签同源：图片宽度恒由 scale 信号算出。 */
  readonly displayWidth = () => shotDisplayWidth(this.naturalWidth(), this.scale());

  zoomIn(): void {
    this.scale.set(stepShotScale(this.scale(), 1));
  }

  zoomOut(): void {
    this.scale.set(stepShotScale(this.scale(), -1));
  }

  onImgLoad(ev: Event): void {
    const w = (ev.target as HTMLImageElement | null)?.naturalWidth ?? 0;
    this.naturalWidth.set(w > 0 ? w : null);
  }
}
