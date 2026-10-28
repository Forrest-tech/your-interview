/**
 * 票 275：报告波形改单条线（用户选定的样式 1：单条下沿锚定包络线，跟读面板同款）。
 *
 * 断言口径（模板/源码文本级，与本目录既有 spec 同款做法）：
 * - 报告 .ws-line svg 内恰好 2 条 polyline：1 条基线 + 1 条 .played 覆盖层，
 *   不再是 261 的 4 条（上下两条 + 两条覆盖层）；
 * - 组件的 waveLinePoints / waveLinePlayedPoints 返回单串（valuesToLinePoints），
 *   组件内不再引用 valuesToMirroredLinePoints；
 * - 映射本身仍是下沿锚定（valuesToLinePoints：y = 100 − v），由既有
 *   shadow-t261-ux.spec.ts 的 valuesToLinePoints 用例覆盖，此处不重复。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票275：报告波形单条线', () => {
  const html = fs.readFileSync(
    path.join(__dirname, 'ai-practice.component.html'),
    'utf8',
  );
  const ts = fs.readFileSync(
    path.join(__dirname, 'ai-practice.component.ts'),
    'utf8',
  );

  function wsSvgBlock(): string {
    const start = html.indexOf('<svg class="ws-line"');
    expect(start).toBeGreaterThan(-1);
    const end = html.indexOf('</svg>', start);
    expect(end).toBeGreaterThan(start);
    return html.slice(start, end);
  }

  it('ws-line svg 内恰好 2 条 polyline：1 条基线 + 1 条 played 覆盖层', () => {
    const block = wsSvgBlock();
    const polylines = block.match(/<polyline/g) ?? [];
    expect(polylines.length).toBe(2);
    expect(block).toContain('[attr.points]="waveLinePoints()"');
    expect(block).toContain('class="played" [attr.points]="waveLinePlayedPoints()"');
    expect(block).not.toContain('waveLinePoints()[0]');
    expect(block).not.toContain('waveLinePoints()[1]');
    expect(block).not.toContain('waveLinePlayedPoints()[0]');
    expect(block).not.toContain('waveLinePlayedPoints()[1]');
  });

  it('组件波形方法返回单串 valuesToLinePoints，不再引用中线对称函数', () => {
    expect(ts).toContain('waveLinePoints(): string {');
    expect(ts).toContain('return valuesToLinePoints(this.grading.waveBars());');
    expect(ts).toContain('waveLinePlayedPoints(): string {');
    expect(ts).toContain('return valuesToLinePoints(bars, 100, played);');
    expect(ts).not.toContain('valuesToMirroredLinePoints');
  });

  it('条带容器、tooltip、指标行、加载/失败态一字不动', () => {
    expect(html).toContain('class="wave-strip"');
    expect(html).toContain(`[matTooltip]="t('practice.recPlay')"`);
    expect(html).toContain('data-testid="wave-metrics"');
    expect(html).toContain(`waveStatus() === 'loading'`);
    expect(html).toContain(`waveStatus() === 'failed'`);
  });
});
