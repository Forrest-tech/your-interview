import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatCardModule } from '@angular/material/card';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiClient } from '../../core/api/api-client';
import { KnowledgeDuplicateGroup, KnowledgeItem } from '../../core/models/api.models';

export interface DuplicatesDialogData {
  groups: KnowledgeDuplicateGroup[];
}

/**
 * 重复条目处理(需求 6.4.3)。
 *
 * 为什么不做"一键自动去重":
 * 两条题目看起来重复,其实可能一条是我自己的笔记、一条是机经里被问住的同一概念 ——
 * 内容该合并,但"哪条作为主体保留"只有用户知道。所以界面只做一件事:
 * 把每一组摆出来,让用户点"保留这条",其余并进来。合并策略(只补空缺、不覆盖)
 * 由后端保证,这里不假装能替用户判断。
 */
@Component({
  selector: 'app-duplicates-dialog',
  standalone: true,
  imports: [
    CommonModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatProgressBarModule, MatCardModule, MatTooltipModule
  ],
  template: `
    <h2 mat-dialog-title>
      <mat-icon>content_copy</mat-icon>
      重复条目({{ groups.length }} 组)
    </h2>

    <mat-dialog-content class="dd-body">
      @if (groups.length === 0) {
        <p class="empty">没有发现重复条目。</p>
      }

      @for (g of groups; track g.key) {
        <mat-card class="group">
          <div class="g-head">
            <span class="g-topic">{{ g.topic }}</span>
            <span class="g-count">{{ g.items.length }} 条重复</span>
          </div>

          @for (it of g.items; track it.id) {
            <div class="row">
              <div class="row-main">
                <span class="r-title">{{ it.title }}</span>
                <span class="r-meta">
                  复习 {{ it.reviewCount }} 次 · {{ it.mastery }}
                  @if (it.sourceCompanyName) { · {{ it.sourceCompanyName }} }
                </span>
                @if (it.conceptExplanation) {
                  <span class="r-has" [matTooltip]="'已有讲解正文'">
                    <mat-icon>article</mat-icon> 讲解
                  </span>
                }
                @if (it.keyPointsJson) {
                  <span class="r-has"><mat-icon>format_list_bulleted</mat-icon> 要点</span>
                }
              </div>
              <button mat-stroked-button color="primary" [disabled]="busy()"
                      (click)="keep(it, g)">
                保留这条
              </button>
            </div>
          }
        </mat-card>
      }

      @if (busy()) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
      @if (msg()) { <p class="msg">{{ msg() }}</p> }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>关闭</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dd-body { width: min(820px, 92vw); max-height: 70vh; }
    .empty { opacity: .7; padding: 12px 0; }
    .group { margin-bottom: 12px; border-radius: 8px; }
    .g-head {
      display: flex; align-items: baseline; justify-content: space-between;
      padding: 8px 12px 0; font-size: 13px;
    }
    .g-topic { font-weight: 600; }
    .g-count { opacity: .65; }
    .row {
      display: flex; align-items: center; gap: 12px;
      padding: 10px 12px; border-top: 1px solid rgba(0,0,0,0.06);
    }
    .row-main { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .r-title { font-size: 13.5px; }
    .r-meta { font-size: 12px; opacity: .6; }
    .r-has {
      display: inline-flex; align-items: center; gap: 4px;
      font-size: 11.5px; padding: 1px 6px; border-radius: 4px;
      background: rgba(63,81,181,0.10); color: #3f51b5; width: fit-content;
    }
    .r-has mat-icon { font-size: 14px; width: 14px; height: 14px; }
    .msg { font-size: 12.5px; color: #a05a00; margin: 8px 0 0; }
  `]
})
export class DuplicatesDialogComponent {
  readonly dialogRef = inject(MatDialogRef<DuplicatesDialogComponent, boolean>);
  readonly data = inject<DuplicatesDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(ApiClient);

  readonly groups = this.data?.groups ?? [];
  readonly busy = signal(false);
  readonly msg = signal<string | null>(null);

  /** 保留 keep,把同组其余条目依次并进来。 */
  keep(item: KnowledgeItem, group: KnowledgeDuplicateGroup): void {
    const others = group.items.filter((x) => x.id !== item.id);
    if (others.length === 0) return;

    const ok = confirm(
      `把同组的另外 ${others.length} 条合并进「${item.title}」?\n` +
      `保留条目的已有内容不会被覆盖,只补充它空缺的字段;被合并的条目会被移除。`);
    if (!ok) return;

    this.busy.set(true);
    this.msg.set(null);

    // 串行执行:合并会改写同一条保留记录,并发写会互相覆盖(也会撞并发令牌)。
    let done = 0;
    const step = (): void => {
      if (done >= others.length) {
        this.busy.set(false);
        this.groups.splice(this.groups.indexOf(group), 1);
        this.dialogRef.close(true);
        return;
      }
      this.api.post<void>('/api/knowledge/merge', {
        keepId: item.id,
        mergeId: others[done].id
      }).subscribe({
        next: () => { done++; step(); },
        error: (e: Error) => {
          this.busy.set(false);
          this.msg.set(`合并到第 ${done + 1} 条时失败:${e.message}`);
        }
      });
    };
    step();
  }
}
