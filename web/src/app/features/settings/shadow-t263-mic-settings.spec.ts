/**
 * 票 263：麦克风设备选择从跟读面板移入 Settings → 语音设置。
 * 契约（源码级结构断言，与票 261 模板契约同风格）：
 *  - 跟读面板不再含任何设备下拉（模板 + 样式双清）；
 *  - 语音设置页含设备选择器（data-testid 锚点 + 复用键 + 新说明键）；
 *  - 录音路径一字未动：会话仍用同一 yi-mic-device 键取设备。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票 263：麦克风选择移出跟读面板', () => {
  const panelHtml = fs.readFileSync(
    path.join(__dirname, '../ai-practice/ai-practice.component.html'), 'utf8');
  const panelScss = fs.readFileSync(
    path.join(__dirname, '../ai-practice/ai-practice.component.scss'), 'utf8');
  const voiceHtml = fs.readFileSync(
    path.join(__dirname, 'voice-setting.component.html'), 'utf8');
  const voiceTs = fs.readFileSync(
    path.join(__dirname, 'voice-setting.component.ts'), 'utf8');
  const session = fs.readFileSync(
    path.join(__dirname, '../ai-practice/practice-shadow-session.ts'), 'utf8');
  const i18n = fs.readFileSync(
    path.join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');

  it('跟读面板无设备下拉（模板与样式双清）', () => {
    expect(panelHtml).not.toContain('shadow-mic-select');
    expect(panelHtml).not.toContain('shadow-mic-row');
    expect(panelScss).not.toContain('.shadow-mic-select');
    expect(panelScss).not.toContain('.shadow-mic-row {');
  });

  it('语音设置页有设备选择器（锚点 + 复用键 + 说明键）', () => {
    expect(voiceHtml).toContain('data-testid="mic-device-select"');
    expect(voiceHtml).toContain('practice.shadow.micDevice');
    expect(voiceHtml).toContain('practice.shadow.micDefault');
    expect(voiceHtml).toContain('setting.micNote');
  });

  it('语音设置页用同一存储键 + 同枚举规则（audioinput，失效 id 回落默认）', () => {
    expect(voiceTs).toContain(`localStorage.getItem('yi-mic-device')`);
    expect(voiceTs).toContain(`localStorage.setItem('yi-mic-device'`);
    expect(voiceTs).toContain('enumerateDevices');
    expect(voiceTs).toContain(`kind === 'audioinput'`);
    expect(voiceTs).toContain('setMicDevice(null)');
  });

  it('录音路径未动：会话仍读同一键、仍把所选设备传给 requestMicStream', () => {
    expect(session).toContain(`localStorage.getItem('yi-mic-device')`);
    expect(session).toContain('requestMicStream(this.micDeviceId())');
  });

  it('setting.micNote 与复用键均有 zh/en/fr', () => {
    for (const key of [
      'setting.micNote',
      'practice.shadow.micDevice',
      'practice.shadow.micDefault',
    ]) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });
});
