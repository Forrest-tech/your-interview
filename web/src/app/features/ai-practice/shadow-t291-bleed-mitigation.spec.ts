/**
 * 票 291 A 缓解包（独立评分胜出 8.3；B 3.3、C 4.5 封顶、D 2.0 封顶）：
 * 扬声器模式关 AGC + 参考声压低 0.3 + AEC 状态常显。
 * 结构契约（源码断言，按 264/265/266 先例）；声学效果（串音削减量）
 * 无法在沙盒复现（无真实扬声器回声），真机实录验证。
 * 约束：耳机模式（278 路径）一字不动；AEC 警告原行为保留。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import { SHADOW_REF_DUCK_FACTOR, duckedVolume } from './shadow-voice-gain';

describe('票 291 A：扬声器串音缓解包', () => {
  const capture = fs.readFileSync(path.join(__dirname, 'shadow-capture.ts'), 'utf8');
  const session = fs.readFileSync(path.join(__dirname, 'practice-shadow-session.ts'), 'utf8');
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const i18n = fs.readFileSync(
    path.join(__dirname, '../../core/i18n/i18n.service.ts'),
    'utf8',
  );

  it('AGC：扬声器模式显式关 autoGainControl（耳机分支不指定）', () => {
    expect(capture).toContain("micMode === 'speaker' ? { autoGainControl: false } : {}");
  });

  it('压低系数 0.3（票 284 的 0.6 下调，仍非静音）', () => {
    expect(SHADOW_REF_DUCK_FACTOR).toBe(0.3);
    expect(duckedVolume(1)).toBeCloseTo(0.3);
  });

  it('AEC 状态信号与面板常显行（警告原行为不动）', () => {
    expect(session).toContain('shadowAecState');
    expect(session).toContain("signal<'unknown' | 'applied' | 'not-applied'>");
    expect(session).toContain('this.shadowAecWarning.set(aecApplied === false)');
    expect(html).toContain('data-testid="shadow-aec-state"');
    expect(html).toContain('practice.shadow.aecOn');
    expect(html).toContain('practice.shadow.aecOff');
    expect(html).toContain('practice.shadow.aecUnknown');
    expect(html).toContain('practice.shadow.aecWarning');
  });

  it('i18n 三键三语齐全', () => {
    for (const key of [
      'practice.shadow.aecOn',
      'practice.shadow.aecOff',
      'practice.shadow.aecUnknown',
    ]) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });
});
