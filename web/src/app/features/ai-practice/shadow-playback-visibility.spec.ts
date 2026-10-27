/**
 * 票 250 P29f：后台双轨播放规则单测（假元素 + 假文档）。
 * - hidden → 仅 dual 暂停；visible → 按 seek 公式重映射后恢复。
 * - mix 不受影响；hidden 期间手动暂停 → 取消自动恢复。
 */
import { describe, expect, it } from '@jest/globals';
import { ShadowPlaybackController } from './shadow-playback';
import type { AudioElementLike, DocumentLike, PlaybackTrack } from './shadow-playback';

function fakeEl(currentTime = 0, duration = 30): AudioElementLike {
  return {
    currentTime,
    duration,
    playbackRate: 1,
    volume: 1,
    paused: true,
    play() {
      this.paused = false;
    },
    pause() {
      this.paused = true;
    },
  };
}

class FakeDoc implements DocumentLike {
  hidden = false;
  private listeners = new Set<() => void>();
  addEventListener(_type: string, fn: () => void): void {
    this.listeners.add(fn);
  }
  removeEventListener(_type: string, fn: () => void): void {
    this.listeners.delete(fn);
  }
  emit(): void {
    for (const fn of [...this.listeners]) fn();
  }
}

function dualCtl(doc: FakeDoc, rate = 1) {
  const voice = fakeEl(0, 22);
  const ref = fakeEl(0, 21);
  const tracks: PlaybackTrack[] = [
    { id: 'voice', el: voice, durationSec: 22 },
    { id: 'reference', el: ref, durationSec: 21 },
  ];
  const ctl = new ShadowPlaybackController(
    { mode: 'dual', tracks, reviewRate: rate, laneShiftSec: 0, seekOffsetSec: 0 },
    undefined,
    doc,
  );
  return { ctl, voice, ref };
}

describe('ShadowPlaybackController visibility (P29f)', () => {
  it('dual：hidden 暂停两轨；visible 按 seek 重映射后恢复播放', async () => {
    const doc = new FakeDoc();
    const { ctl, voice, ref } = dualCtl(doc, 1);
    await ctl.play();
    voice.currentTime = 4;
    ref.currentTime = 4;
    doc.hidden = true;
    doc.emit();
    expect(ctl.isPlaying).toBe(false);
    expect(voice.paused).toBe(true);
    expect(ref.paused).toBe(true);
    doc.hidden = false;
    doc.emit();
    // visible 恢复是异步 play：等 microtask 落定后再断言播放态。
    await new Promise((r) => setTimeout(r, 0));
    expect(ref.currentTime).toBeCloseTo(4, 9);
    expect(voice.currentTime).toBeCloseTo(4, 9);
    expect(ctl.isPlaying).toBe(true);
    expect(voice.paused).toBe(false);
    expect(ref.paused).toBe(false);
    ctl.destroy();
  });

  it('mix：hidden 不暂停（P29f 仅 dual 适用）', async () => {
    const doc = new FakeDoc();
    const mix = fakeEl(0, 30);
    const ctl = new ShadowPlaybackController(
      { mode: 'mix', tracks: [{ id: 'mix', el: mix, durationSec: 30 }], reviewRate: 1, laneShiftSec: 0, seekOffsetSec: 0 },
      undefined,
      doc,
    );
    await ctl.play();
    doc.hidden = true;
    doc.emit();
    expect(ctl.isPlaying).toBe(true);
    expect(mix.paused).toBe(false);
    ctl.destroy();
  });

  it('hidden 期间手动暂停 → visible 不自动恢复', async () => {
    const doc = new FakeDoc();
    const { ctl, voice, ref } = dualCtl(doc, 1);
    await ctl.play();
    doc.hidden = true;
    doc.emit();
    expect(ctl.isPlaying).toBe(false);
    ctl.pause(); // 用户在后台手动暂停
    doc.hidden = false;
    doc.emit();
    expect(ctl.isPlaying).toBe(false);
    expect(voice.paused).toBe(true);
    expect(ref.paused).toBe(true);
    ctl.destroy();
  });
});
