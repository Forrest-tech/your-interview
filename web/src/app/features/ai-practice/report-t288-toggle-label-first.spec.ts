/**
 * 票 288：开关文字在前、按钮在后 — 模板契约单测。
 *   · 用户截图：Shadow 与 One sentence 两个开关当前是“开关在左、文字在右”，
 *     要求改成“文字在左、开关在右”（Shadow [开关]、One sentence [开关]）。
 *   · 修法只用 Angular Material 标准机制 labelPosition="before"，不手写定位，
 *     不动票 268 iPhone 开关样式（46×28 胶囊、24px 圆钮、开态 #34C759）。
 *   · 范围只限练习控制行的这两个开关；应用内没有其他 mat-slide-toggle。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

const tpl = SRC('./ai-practice.component.html');

function toggleBlock(className: string): string {
  const start = tpl.indexOf(`class="${className}"`);
  expect(start).toBeGreaterThan(-1);
  const open = tpl.lastIndexOf('<mat-slide-toggle', start);
  const close = tpl.indexOf('</mat-slide-toggle>', start);
  expect(close).toBeGreaterThan(-1);
  return tpl.slice(open, close);
}

describe('票 288：开关文字在前、按钮在后', () => {
  it('跟读开关文字在前：labelPosition="before"', () => {
    expect(toggleBlock('shadow-switch')).toContain('labelPosition="before"');
  });

  it('一句话开关文字在前：labelPosition="before"', () => {
    expect(toggleBlock('sentence-switch')).toContain('labelPosition="before"');
  });

  it('两个开关的绑定一字未动：checked/change/disabled/tooltip/data-testid', () => {
    const shadow = toggleBlock('shadow-switch');
    expect(shadow).toContain('[checked]="shadow.shadowMode()"');
    expect(shadow).toContain('(change)="shadow.toggleShadowMode()"');
    expect(shadow).toContain('data-testid="shadow-toggle"');
    expect(shadow).toContain("{{ t('practice.shadow.shadowBadge') }}");
    const sentence = toggleBlock('sentence-switch');
    expect(sentence).toContain('[checked]="sentence.sentenceMode()"');
    expect(sentence).toContain('(change)="sentence.toggleSentenceMode()"');
    expect(sentence).toContain('[disabled]="editing() || recorder.recording() || shadow.shadowRecording()"');
    expect(sentence).toContain('data-testid="sentence-toggle"');
    expect(sentence).toContain("{{ t('practice.sentence.badge') }}");
  });

  it('范围守卫：模板内恰好只有这两个 mat-slide-toggle，没有第三个开关被改动', () => {
    const count = (tpl.match(/<mat-slide-toggle/g) || []).length;
    expect(count).toBe(2);
  });
});
