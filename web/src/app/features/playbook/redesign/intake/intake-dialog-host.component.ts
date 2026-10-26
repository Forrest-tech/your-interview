import { AfterViewInit, Component, effect, inject, signal, viewChild } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { InterviewDetail } from '../../../../core/models/api.models';
import { IntakePanelComponent, IntakeType } from './intake-panel.component';

/**
 * 票 199：+ Add material 面板的弹窗宿主（薄胶水层，不含面板逻辑）。
 * 面板本身（intake-panel）保持原样：它以 open 信号控制自身显隐，
 * X / Cancel / 保存成功时都会把 open 置 false；宿主把 open→false
 * 映射为对话框关闭，把 saved / prepGenerate 输出映射为对话框结果。
 * 注意时序：finishSave 先置 open=false、再同步 emit saved/prepGenerate，
 * 而 signal effect 在 emit 之后才跑；为防 effect 抢先以空结果关窗，
 * saved/prepGenerate 处理里先立 closed 旗，effect 见旗即停。
 */
export type IntakeDialogMode =
  | { kind: 'panel'; type: IntakeType; roundId?: string; presetNew?: boolean }
  | { kind: 'prepInput'; purpose: 'experiences' | 'resume'; roundId: string; text: string; assetId?: string };

export interface IntakeDialogData {
  entry: InterviewDetail;
  mode: IntakeDialogMode;
}

export interface IntakeDialogResult {
  saved?: true;
  prepGenerate?: { roundId: string };
}

@Component({
  selector: 'app-intake-dialog-host',
  standalone: true,
  imports: [IntakePanelComponent],
  template: `
    <app-intake-panel [entry]="data.entry" (saved)="onSaved()" (prepGenerate)="onPrepGenerate($event)" />
  `,
  styles: [`
    /* 弹窗内容区内部滚动：长解析预览不得把弹窗顶出视口（外层 pane 另有 maxHeight 92vh）。 */
    :host { display: block; max-height: 92vh; overflow-y: auto; }
  `],
})
export class IntakeDialogHostComponent implements AfterViewInit {
  private readonly ref = inject(MatDialogRef<IntakeDialogHostComponent, IntakeDialogResult>);
  readonly data = inject<IntakeDialogData>(MAT_DIALOG_DATA);
  private readonly panel = viewChild(IntakePanelComponent);
  private readonly started = signal(false);
  private readonly closed = signal(false);

  constructor() {
    effect(() => {
      const p = this.panel();
      if (p && this.started() && !this.closed() && !p.open()) this.ref.close();
    });
  }

  ngAfterViewInit(): void {
    const p = this.panel();
    if (!p) return;
    const m = this.data.mode;
    if (m.kind === 'panel') p.openPanel(m.type, m.roundId, m.presetNew ?? false);
    else p.openPrepInput(m.purpose, m.roundId, m.text, m.assetId);
    this.started.set(true);
  }

  onSaved(): void {
    this.closed.set(true);
    this.ref.close({ saved: true });
  }

  onPrepGenerate(ev: { roundId: string }): void {
    this.closed.set(true);
    this.ref.close({ prepGenerate: ev });
  }
}
