import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MAT_DIALOG_DATA } from '@angular/material/dialog';

/** ★ 备战计划对话框 (替代原生 confirm)。 */
@Component({
  selector: 'app-prep-plan-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>🎯 面试备战计划</h2>
    <mat-dialog-content>
      <p><b>{{ data.companyName }}</b> — {{ data.role }}</p>
      <p>技术关键词 (从 JD 提取):</p>
      <ul>
        @for (k of data.keywords; track k) { <li>{{ k }}</li> }
        @if (data.keywords.length === 0) { <li><i>未识别到关键词</i></li> }
      </ul>
      <p>建议准备:</p>
      <ol>
        <li>针对每个关键词准备 1 个项目实例</li>
        <li>在 Tech Stack 里复习相关条目</li>
        <li>用 AI Practice 做一次模拟问答</li>
      </ol>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>稍后</button>
      <button mat-flat-button color="primary" [mat-dialog-close]="true">去 Playbook 准备</button>
    </mat-dialog-actions>
  `
})
export class PrepPlanDialogComponent {
  readonly data = inject(MAT_DIALOG_DATA) as { companyName: string; role: string; keywords: string[] };
}
