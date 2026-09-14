import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/** 新增条目的表单模型(仅本对话框内部使用)。 */
export interface KnowledgeForm {
  title: string;
  topic: string;
  question: string;
  conceptExplanation: string;
  keyPoints: string;
  commonMistakes: string;
  difficulty: number;
  importance: number;
}

/**
 * 新增技术栈条目的对话框。
 *
 * 关于"要点 / 常见误区"两个输入框:后端存的是 JSON 数组,
 * 但让用户手写 JSON 是反人类的 —— 这里用"一行一条"的纯文本,
 * 提交时再转成 JSON 数组。用户写起来是列表,机器拿到的是结构化数据。
 */
@Component({
  selector: 'app-knowledge-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule,
    MatInputModule, MatSelectModule, MatButtonModule, MatIconModule
  ],
  template: `
    <h2 mat-dialog-title>新增技术栈条目</h2>

    <mat-dialog-content class="dlg">
      <mat-form-field appearance="outline" class="full">
        <mat-label>标题 *</mat-label>
        <input matInput name="title" [(ngModel)]="form.title" required
               placeholder="如 Clean Architecture / 依赖注入">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>分类 *</mat-label>
        <input matInput name="topic" [(ngModel)]="form.topic" required
               placeholder="如 架构 / C#/.NET / 云原生">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>面试题干</mat-label>
        <textarea matInput name="question" [(ngModel)]="form.question" rows="2"
                  placeholder="面试官会怎么问这个概念"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>概念讲解</mat-label>
        <textarea matInput name="conceptExplanation" [(ngModel)]="form.conceptExplanation" rows="5"
                  placeholder="原理、机制、为什么这样设计"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>关键要点(一行一条)</mat-label>
        <textarea matInput name="keyPoints" [(ngModel)]="form.keyPoints" rows="4"
                  placeholder="答题必须覆盖的点,每行一条"></textarea>
        <mat-hint>提交时会自动转成结构化列表</mat-hint>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>常见误区(一行一条)</mat-label>
        <textarea matInput name="commonMistakes" [(ngModel)]="form.commonMistakes" rows="3"
                  placeholder="容易被追问打穿的地方,每行一条"></textarea>
      </mat-form-field>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>难度 1-5</mat-label>
          <input matInput type="number" name="difficulty" min="1" max="5"
                 [(ngModel)]="form.difficulty">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>重要度 1-5</mat-label>
          <input matInput type="number" name="importance" min="1" max="5"
                 [(ngModel)]="form.importance">
        </mat-form-field>
      </div>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>取消</button>
      <button mat-raised-button color="primary" [disabled]="!canSubmit()" (click)="save()">
        保存
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dlg { min-width: 460px; max-width: 560px; }
    .full { width: 100%; }
    .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    @media (max-width: 560px) { .dlg { min-width: auto; } .row2 { grid-template-columns: 1fr; } }
    h2[mat-dialog-title] { display: flex; align-items: center; gap: 6px; }
  `]
})
export class KnowledgeDialogComponent {
  private readonly ref = inject(MatDialogRef<KnowledgeDialogComponent>);

  form: KnowledgeForm = {
    title: '', topic: '', question: '', conceptExplanation: '',
    keyPoints: '', commonMistakes: '', difficulty: 3, importance: 3
  };

  /** 标题和分类是后端必填项,前端先挡一道,避免白跑一次 400。 */
  canSubmit(): boolean {
    return this.form.title.trim().length > 0 && this.form.topic.trim().length > 0;
  }

  /** 把"一行一条"的文本转成后端要的 JSON 数组字符串;空则给 null 而不是 "[]"。 */
  private toJsonList(text: string): string | null {
    const lines = text.split('\n').map((x) => x.trim()).filter((x) => x.length > 0);
    return lines.length > 0 ? JSON.stringify(lines) : null;
  }

  save(): void {
    if (!this.canSubmit()) return;
    const payload = {
      title: this.form.title.trim(),
      topic: this.form.topic.trim(),
      question: this.form.question.trim() || null,
      conceptExplanation: this.form.conceptExplanation.trim() || null,
      keyPointsJson: this.toJsonList(this.form.keyPoints),
      commonMistakesJson: this.toJsonList(this.form.commonMistakes),
      difficulty: Number(this.form.difficulty) || 3,
      importance: Number(this.form.importance) || 3,
      source: 'Personal'
    };
    this.ref.close(payload);
  }
}
