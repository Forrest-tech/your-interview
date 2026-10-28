/**
 * 票 265：跟读倍速行删除，倍速只用示范条 x1。
 * 用户定案（点选）：Shadowing rate 那行去掉，倍速只用播放条上的 x1。
 * 注意：此前同一票号曾有“改回旧 pills”方向，用户纠正后叫停、未落地；
 * 本票方向是删除行 + 录制率跟示范条 x1，不是 pills 回归。
 * 本文件为结构契约：session 构造需完整组件 host，既有 spec 均未构造 session，
 * 故按票 264 结构契约先例断言源码接线；行为由沙盒走查覆盖。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票 265：跟读倍速行删除，倍速只用示范条 x1', () => {
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const scss = fs.readFileSync(path.join(__dirname, 'ai-practice.component.scss'), 'utf8');
  const session = fs.readFileSync(path.join(__dirname, 'practice-shadow-session.ts'), 'utf8');
  const i18n = fs.readFileSync(path.join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');

  it('模板：跟读倍速行与录制倍速菜单彻底删除', () => {
    expect(html).not.toContain('shadow-rate-row');
    expect(html).not.toContain('shadowRateMenu');
    expect(html).not.toContain('shadow.setShadowRate');
    expect(html).not.toContain('practice.shadow.rate');
  });

  it('模板：示范条 x1 与回放菜单原样保留', () => {
    expect(html).toContain('speed-badge');
    expect(html).toContain('rateMenu');
    expect(html).toContain('shadowReviewMenu');
    expect(html).toContain('shadow.shadowReviewRate()');
  });

  it('session：录制率信号与 setter 删除，采集率与 take 率均跟示范条 x1', () => {
    expect(session).not.toContain('shadowRate = signal');
    expect(session).not.toContain('setShadowRate');
    expect(session).not.toContain('this.shadowRate()');
    expect(session).toContain('this.takeRate = this.host.tts.rate()');
    // 采集率与 take 率同源：host.tts.rate() 恰出现两次（采集参数 + takeRate）。
    expect(session.match(/this\.host\.tts\.rate\(\)/g)?.length).toBe(2);
  });

  it('session：倍速率表保留（回放菜单仍迭代它）', () => {
    expect(session).toContain('readonly shadowRates = [0.5, 0.75, 1, 1.25, 1.5, 2]');
  });

  it('i18n：删行后无孤儿键，回放倍速键仍在', () => {
    expect(i18n).not.toContain("'practice.shadow.rate'");
    expect(i18n).not.toContain("'practice.shadow.recording'");
    expect(i18n).toContain("'practice.shadow.reviewRate'");
  });

  it('样式：菜单键 class 保留（回放菜单仍用它），无 pills 回归', () => {
    expect(scss).toContain('.shadow-rate-menu-btn');
    expect(scss).not.toContain('.shadow-rate-btn {');
  });
});
