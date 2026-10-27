/**
 * 票 250：播放控制器单测（P16 漂移校正 / §3 seek / P25 变速重映射）。
 * 假元素（AudioElementLike），不依赖真实 <audio>。
 */
import { describe, expect, it } from '@jest/globals';
import { ShadowPlaybackController } from './shadow-playback';
import type { AudioElementLike, PlaybackTrack } from './shadow-playback';

function fakeEl(currentTime = 0, duration = 30): AudioElementLike & { played: boolean } {
  const el: AudioElementLike & { played: boolean } = {
    currentTime,
    duration,
    playbackRate: 1,
    volume: 1,
    paused: true,
    played: false,
    play() {
      this.paused = false;
      this.played = true;
    },
    pause() {
      this.paused = true;
    },
  };
  return el;
}

function dual(rate = 1) {
  const voice = fakeEl(0, 22);
  const ref = fakeEl(0, 21);
  const tracks: PlaybackTrack[] = [
    { id: 'voice', el: voice, durationSec: 22 },
    { id: 'reference', el: ref, durationSec: 21 },
  ];
  const ctl = new ShadowPlaybackController(
    { mode: 'dual', tracks, reviewRate: rate, laneShiftSec: 0, seekOffsetSec: 0 },
  );
  return { ctl, voice, ref };
}

describe('ShadowPlaybackController dual', () => {
  it('seek：参考=T，人声=(T−seekOffset)/R 钳制', async () => {
    const { ctl, voice, ref } = dual(0.5);
    ctl.seek(10);
    expect(ref.currentTime).toBe(10);
    expect(voice.currentTime).toBeCloseTo(20, 6);
    ctl.seek(100);
    expect(ref.currentTime).toBe(21);
    expect(voice.currentTime).toBe(22);
    await ctl.play();
    ctl.pause();
  });
  it('tick：漂移 >50ms 纠回期望，≤50ms 不动', async () => {
    const { ctl, voice, ref } = dual(2);
    await ctl.play();
    // 锚点 (0,0)，人声走到 5s → 期望参考 10s；参考只到 9.9s（漂移 100ms）→ 纠。
    voice.currentTime = 5;
    ref.currentTime = 9.9;
    ctl.tick();
    expect(ref.currentTime).toBeCloseTo(10, 6);
    // 漂移 40ms → 不动。
    voice.currentTime = 5;
    ref.currentTime = 10.04;
    ctl.tick();
    expect(ref.currentTime).toBeCloseTo(10.04, 6);
    ctl.pause();
  });
  it('setRate：暂停→重映射→恢复播放态，审阅率独立', async () => {
    const { ctl, voice, ref } = dual(1);
    await ctl.play();
    ctl.seek(10);
    expect(voice.currentTime).toBeCloseTo(10, 6);
    await ctl.setRate(2);
    expect(ref.currentTime).toBeCloseTo(10, 6);
    expect(voice.currentTime).toBeCloseTo(5, 6);
    expect(ref.playbackRate).toBe(2);
    expect(voice.playbackRate).toBe(1);
    ctl.pause();
  });
  it('参考播完而人声继续：tick 不把参考拉回（轴扩展语义）', async () => {
    const { ctl, voice, ref } = dual(2);
    await ctl.play();
    voice.currentTime = 15; // 期望参考 30 > 参考轨长 21
    ref.currentTime = 21;
    // tick 只纠 50ms 以上漂移：期望被钳到轨长 21，参考已在 21 → 不动。
    ctl.tick();
    expect(ref.currentTime).toBe(21);
    ctl.pause();
  });
});

describe('ShadowPlaybackController mix', () => {
  it('单元素 seek/play，无漂移校正', async () => {
    const mix = fakeEl(0, 22);
    const ctl = new ShadowPlaybackController({
      mode: 'mix',
      tracks: [{ id: 'mix', el: mix, durationSec: 22 }],
      reviewRate: 1,
      laneShiftSec: 0,
      seekOffsetSec: 0,
    });
    ctl.seek(5);
    expect(mix.currentTime).toBe(5);
    await ctl.play();
    expect(ctl.isPlaying).toBe(true);
    ctl.tick();
    expect(mix.currentTime).toBe(5);
    ctl.pause();
  });
  it('单元素 tick 发 onTime（播放头推进），暂停时不发', async () => {
    const mix = fakeEl(0, 22);
    const seen: number[] = [];
    const ctl = new ShadowPlaybackController(
      {
        mode: 'mix',
        tracks: [{ id: 'mix', el: mix, durationSec: 22 }],
        reviewRate: 1,
        laneShiftSec: 0,
        seekOffsetSec: 0,
      },
      (t) => seen.push(t),
    );
    await ctl.play();
    mix.currentTime = 7;
    ctl.tick();
    expect(seen).toEqual([7]);
    ctl.pause();
    mix.currentTime = 9;
    ctl.tick();
    expect(seen).toEqual([7]);
  });
});
