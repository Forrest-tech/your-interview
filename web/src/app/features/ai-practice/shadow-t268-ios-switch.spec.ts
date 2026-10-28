/**
 * 票 268：跟读开关改 iPhone（iOS UISwitch）外观单测。
 * 只改样子：行为、位置、文案、tooltip、data-testid 一律不动；模板仍用
 * mat-slide-toggle，样式用 scoped ::ng-deep 覆盖 MDC 内部。
 * 真机视觉以沙盒走查截图为准（见实现报告），此处钉住结构契约。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票 268：跟读开关 iPhone 外观结构契约', () => {
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const scss = fs.readFileSync(path.join(__dirname, 'ai-practice.component.scss'), 'utf8');

  it('开关仍是 mat-slide-toggle，仍在 Previous 左侧，testid/文案/tooltip 一字不动', () => {
    expect(html).toContain('data-testid="shadow-toggle"');
    expect(html.indexOf('data-testid="shadow-toggle"')).toBeLessThan(html.indexOf('prevItem()'));
    expect(html).toContain('mat-slide-toggle');
    expect(html).toContain('class="shadow-switch"');
    expect(html).toContain('[checked]="shadow.shadowMode()"');
    expect(html).toContain('(change)="shadow.toggleShadowMode()"');
    expect(html).toContain("t('practice.shadow.toggleOn')");
    expect(html).toContain("t('practice.shadow.toggleOff')");
    expect(html).toContain("t('practice.shadow.shadowBadge')");
  });

  it('iOS 轨道：46×28 胶囊、关态浅灰、开态 #34C759', () => {
    // 票 277：一句话开关与跟读开关嵌套共用本块（父选择器并列 + ::ng-deep 内嵌），规则口径不变。
    expect(scss).toContain('.shadow-switch, .sentence-switch {');
    expect(scss).toContain('.mdc-switch__track {');
    expect(scss).toContain('width: 46px; height: 28px; border-radius: 14px;');
    expect(scss).toContain('background-color: #e9e9ea;');
    expect(scss).toContain('.mdc-switch--selected .mdc-switch__track');
    expect(scss).toContain('background-color: #34c759;');
  });

  it('iOS 圆钮：24px 白色圆钮带阴影、滑动过渡、钮上字形去掉（变量口径双写）', () => {
    // MDC 内部尺寸走 CSS 变量，直接宽高与变量双写，缺一不可。
    expect(scss).toContain('--mdc-switch-handle-width: 24px;');
    expect(scss).toContain('--mdc-switch-handle-height: 24px;');
    expect(scss).toContain('.mdc-switch__handle {');
    expect(scss).toContain('width: 24px !important; height: 24px !important; border-radius: 50%;');
    expect(scss).toContain('background-color: #fff;');
    expect(scss).toContain('box-shadow: 0 3px 8px rgba(0, 0, 0, 0.15)');
    expect(scss).toContain('.mdc-switch__handle-track');
    expect(scss).toContain('transition: transform 0.25s ease;');
    expect(scss).toContain('.mdc-switch__icons');
    expect(scss).toContain('display: none;');
  });

  it('只动这两个开关：其余开关/菜单的样式锚点未被波及', () => {
    // 覆盖限定在 .shadow-switch/.sentence-switch 之下，不存在全局 .mdc-switch 改写。
    // 票 277 嵌套共用后，作用域内规则行均为缩进的后代链；只有顶格（列 0）起首的
    // .mdc-switch 行才是全局改写，缩进行不算。
    const globalMdc = scss
      .split('\n')
      .filter((line) => line.includes('.mdc-switch') && !line.includes('.shadow-switch') && !line.includes('.sentence-switch'))
      .filter((line) => line.startsWith('.mdc-switch'));
    expect(globalMdc).toEqual([]);
  });
});
