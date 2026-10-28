/**
 * 票 264：跟读模式下参考音播完自动停（用户令“当录音播放完，这个自动停止”）。
 * 覆盖纯函数门（shouldAutoStopOnRefEnd）+ session 源码结构契约（onend 接线、
 * 暂停不停、stopShadow 自带双停门）。真机麦克风与真参考音不在沙盒验证范围
 * 内（见实现报告的诚实缺口）；走查用桩参考音 + 桩麦流。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import { shouldAutoStopOnRefEnd } from './practice-shadow-session';

describe('票 264：shouldAutoStopOnRefEnd 门矩阵', () => {
  it('在录音 + 未在停 + 当前 howl → 停', () => {
    expect(shouldAutoStopOnRefEnd(true, false, true)).toBe(true);
  });

  it('已手动停（未在录音）→ no-op，绝不双停', () => {
    expect(shouldAutoStopOnRefEnd(false, false, true)).toBe(false);
  });

  it('正在停（stopShadow 已进入）→ no-op，绝不双停', () => {
    expect(shouldAutoStopOnRefEnd(true, true, true)).toBe(false);
  });

  it('旧 howl 的迟到 end 事件（新 take 已重建 refHowl）→ 不误停新录音', () => {
    expect(shouldAutoStopOnRefEnd(true, false, false)).toBe(false);
  });

  it('全否 → 不停', () => {
    expect(shouldAutoStopOnRefEnd(false, true, false)).toBe(false);
  });
});

describe('票 264：session 源码结构契约', () => {
  const session = fs.readFileSync(
    path.join(__dirname, 'practice-shadow-session.ts'),
    'utf8',
  );
  const onendBlock = session.slice(session.indexOf('onend: () => {'), session.indexOf('onend: () => {') + 900);

  it('onend 经门控调现有 stopShadow（不新造停止路径），旧“播完不停”注记已除', () => {
    expect(onendBlock).toContain('shouldAutoStopOnRefEnd');
    expect(onendBlock).toContain('this.stopShadow()');
    expect(onendBlock).toContain('this.refHowl === howl');
    expect(session).not.toContain('录音不自动停');
  });

  it('暂停不停：session 源码无 onpause→stop 接线', () => {
    expect(session).not.toContain('onpause');
  });

  it('stopShadow 自带双停门（第二道防线）', () => {
    const stopBlock = session.slice(session.indexOf('async stopShadow()'));
    expect(stopBlock.slice(0, 300)).toContain(
      'if (!this.shadowRecording() || this.stopping) return;',
    );
  });

  it('常规评分录音不动：onend 接线只在跟读 startShadow 的 Howl 内', () => {
    // startShadow 内唯一 onend；常规 grading 的 ended 处理在 component 内，
    // 与本票无关（component 源码无 shouldAutoStopOnRefEnd 引用）。
    const component = fs.readFileSync(
      path.join(__dirname, 'ai-practice.component.ts'),
      'utf8',
    );
    expect(component).not.toContain('shouldAutoStopOnRefEnd');
    expect(session.match(/onend: \(\) => \{/g)).toHaveLength(1);
  });
});
