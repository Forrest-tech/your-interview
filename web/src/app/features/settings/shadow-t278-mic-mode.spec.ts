/**
 * 票 278：跟读麦克风模式（耳机 / 扬声器）—— 独立评分胜出 A 的实装契约。
 *  - 耳机模式 → echoCancellation:false；扬声器（默认）→ true；降噪两模式恒关；
 *  - 模式存 yi-mic-mode（只存耳机，缺省 = 扬声器）；非法值回落扬声器；
 *  - 会话每次授权时现读模式（下一条即生效）；约束对象按次构造，
 *    已授权的流不受后切影响；
 *  - 开关在 Settings Voice 页、紧挨设备选择器，带利弊说明；
 *  - 常规录音流（recorder.service）一字未动。
 * 真机麦克风采集不在沙盒验证范围内（见实现报告的诚实缺口）。
 */
import { describe, expect, it, jest } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import {
  SHADOW_MIC_MODE_KEY,
  loadShadowMicMode,
  requestMicStream,
} from '../ai-practice/shadow-capture';

describe('票 278：麦克风模式决定回声消除', () => {
  // 本工程 Jest 用 node 环境（无 window）：直接在 globalThis 上挂 navigator。
  const mockMedia = (impl: (c: MediaStreamConstraints) => Promise<MediaStream>) => {
    const mediaDevices = { getUserMedia: jest.fn(impl) };
    (globalThis as unknown as { navigator: unknown }).navigator = { mediaDevices };
    return mediaDevices;
  };
  const fakeStream = {} as MediaStream;
  const audioOf = (md: ReturnType<typeof mockMedia>, i = 0) =>
    (md.getUserMedia.mock.calls[i][0] as { audio: Record<string, unknown> }).audio;

  it('耳机模式 → echoCancellation:false，noiseSuppression 恒 false', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream(null, 'earphones');
    expect(audioOf(md)['echoCancellation']).toBe(false);
    expect(audioOf(md)['noiseSuppression']).toBe(false);
  });

  it('耳机模式 + 指定设备 → 同样关回声消除，且 deviceId 照常 exact', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream('dev-1', 'earphones');
    expect(audioOf(md)['echoCancellation']).toBe(false);
    expect(audioOf(md)['deviceId']).toEqual({ exact: 'dev-1' });
  });

  it('扬声器模式 → echoCancellation:true（261 原行为）', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream(null, 'speaker');
    expect(audioOf(md)['echoCancellation']).toBe(true);
    expect(audioOf(md)['noiseSuppression']).toBe(false);
  });

  it('不传模式 → 默认扬声器（老调用方一字不动仍走原行为）', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream('dev-1');
    expect(audioOf(md)['echoCancellation']).toBe(true);
  });

  it('耳机模式下 OverconstrainedError 仍回落默认设备、且回落请求仍关回声消除', async () => {
    const err = new Error('gone');
    err.name = 'OverconstrainedError';
    const md = mockMedia(async (c) => {
      const audio = c.audio as Record<string, unknown>;
      if (audio['deviceId']) throw err;
      return fakeStream;
    });
    await requestMicStream('stale-dev', 'earphones');
    expect(md.getUserMedia).toHaveBeenCalledTimes(2);
    expect(audioOf(md, 1)['echoCancellation']).toBe(false);
    expect(audioOf(md, 1)['deviceId']).toBeUndefined();
  });

  it('约束对象按次构造：先耳机后扬声器，两次捕获的约束互不影响（录中切模式不动已授权流）', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream(null, 'earphones');
    await requestMicStream(null, 'speaker');
    expect(audioOf(md, 0)['echoCancellation']).toBe(false);
    expect(audioOf(md, 1)['echoCancellation']).toBe(true);
  });
});

describe('票 278：模式持久化（yi-mic-mode）', () => {
  const stubStorage = (initial: Record<string, string>) => {
    const store = new Map(Object.entries(initial));
    (globalThis as unknown as { localStorage: unknown }).localStorage = {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => {
        store.set(k, v);
      },
      removeItem: (k: string) => {
        store.delete(k);
      },
    };
  };

  it('存储键 = yi-mic-mode', () => {
    expect(SHADOW_MIC_MODE_KEY).toBe('yi-mic-mode');
  });

  it('存 earphones → 读出 earphones；缺省 / 非法值 → 回落 speaker', () => {
    stubStorage({ 'yi-mic-mode': 'earphones' });
    expect(loadShadowMicMode()).toBe('earphones');
    stubStorage({});
    expect(loadShadowMicMode()).toBe('speaker');
    stubStorage({ 'yi-mic-mode': 'junk' });
    expect(loadShadowMicMode()).toBe('speaker');
  });
});

describe('票 278：开关位置与接线（结构契约）', () => {
  const voiceHtml = fs.readFileSync(
    path.join(__dirname, 'voice-setting.component.html'), 'utf8');
  const voiceTs = fs.readFileSync(
    path.join(__dirname, 'voice-setting.component.ts'), 'utf8');
  const session = fs.readFileSync(
    path.join(__dirname, '../ai-practice/practice-shadow-session.ts'), 'utf8');
  const recorder = fs.readFileSync(
    path.join(__dirname, '../../core/recorder/recorder.service.ts'), 'utf8');
  const i18n = fs.readFileSync(
    path.join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');

  it('语音设置页有模式选择器（锚点 + 两模式 + 说明键），且紧挨设备选择器之后', () => {
    expect(voiceHtml).toContain('data-testid="mic-mode-select"');
    expect(voiceHtml).toContain('setting.micMode');
    expect(voiceHtml).toContain('setting.micModeSpeaker');
    expect(voiceHtml).toContain('setting.micModeEarphones');
    expect(voiceHtml).toContain('setting.micModeNote');
    expect(voiceHtml).toContain('value="speaker"');
    expect(voiceHtml).toContain('value="earphones"');
    const deviceAt = voiceHtml.indexOf('mic-device-select');
    const modeAt = voiceHtml.indexOf('mic-mode-select');
    const noteAt = voiceHtml.indexOf('setting.micModeNote');
    expect(deviceAt).toBeGreaterThan(-1);
    expect(modeAt).toBeGreaterThan(deviceAt);
    expect(noteAt).toBeGreaterThan(modeAt);
  });

  it('语音设置页用同一存储键（耳机存键、扬声器清键）+ 恢复时现读', () => {
    expect(voiceTs).toContain('SHADOW_MIC_MODE_KEY');
    expect(voiceTs).toContain('setMicMode');
    expect(voiceTs).toContain('loadShadowMicMode()');
    expect(voiceTs).toContain(`localStorage.setItem(SHADOW_MIC_MODE_KEY, 'earphones')`);
    expect(voiceTs).toContain('localStorage.removeItem(SHADOW_MIC_MODE_KEY)');
  });

  it('会话每次授权时现读模式（下一条即生效；非缓存信号）', () => {
    expect(session).toContain('loadShadowMicMode');
    // 票 284：模式在 take 开始时读一次存入 take 局部量（非缓存信号，
    // 下一条重读），授权约束、录制端增益、参考声压低三处共用同一值；
    // 278 的“每次授权现读”口径未变，只是断言跟随新写法。
    expect(session).toContain('const micMode = loadShadowMicMode()');
    expect(session).toContain('requestMicStream(this.micDeviceId(), micMode)');
  });

  it('常规录音流一字未动（不含模式键与模式读取）', () => {
    expect(recorder).not.toContain('yi-mic-mode');
    expect(recorder).not.toContain('loadShadowMicMode');
    expect(recorder).not.toContain('ShadowMicMode');
  });

  it('四个新键均有 zh/en/fr，且说明写明利弊（耳机干净但须真戴耳机、扬声器防串音）', () => {
    for (const key of [
      'setting.micMode',
      'setting.micModeSpeaker',
      'setting.micModeEarphones',
      'setting.micModeNote',
    ]) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
    const note = i18n.split('\n').find((l) => l.includes(`'setting.micModeNote'`))!;
    expect(note).toContain('耳机');
    expect(note).toContain('扬声器');
    expect(note).toContain('earphones');
    expect(note).toContain('Speaker');
  });
});
