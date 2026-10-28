/**
 * 票 280：跟读行与常规 Take 行同版式 + View Report 开关（用户点选方案）。
 * 覆盖（模板源码断言，Node 环境无 DOM，见票 269 先例）：
 * - 跟读行 hi-meta 含日期时间（fmtTime 口径，与常规行一致）+ 时长 + 倍速；
 * - 跟读行有 .hi-view 开关：View Report/Collapse 文案 + 上下 chevron +
 *   aria-expanded + stopPropagation + 调 expandShadowRow（只开关展开区）；
 * - 跟读行无评分入口、无评分数字：无 hi-score、无 gradeRecording、无 hi-badge；
 * - expandShadowRow 本身不起播（方法体内无 .play( 调用），故 View Report
 *   开关展开时不放音；常规行 hi-score / hi-view 原样未动。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

const SRC = __dirname;
const read = (f: string): string => fs.readFileSync(path.join(SRC, f), 'utf8');

function shadowSection(): string {
  const html = read('ai-practice.component.html');
  const start = html.indexOf('跟读列表');
  const end = html.indexOf('@else if (!shadow.nonShadowRecordings', start);
  expect(start).toBeGreaterThan(-1);
  expect(end).toBeGreaterThan(start);
  return html.slice(start, end);
}

function normalSection(): string {
  const html = read('ai-practice.component.html');
  const start = html.indexOf('@else if (!shadow.nonShadowRecordings');
  expect(start).toBeGreaterThan(-1);
  return html.slice(start);
}

describe('票 280：跟读行版式与 View Report 开关', () => {
  it('跟读行 hi-meta：日期时间 + 时长 + 倍速（日期口径与常规行一致）', () => {
    const sec = shadowSection();
    expect(sec).toContain('fmtTime(row.createdAt)');
    expect(sec).toContain('fmtDuration(row.durationSec)');
    expect(sec).toContain('{{ row.rate }}x');
  });

  it('跟读行有 .hi-view 开关：View Report/Collapse + chevron + aria-expanded + stopPropagation', () => {
    const sec = shadowSection();
    expect(sec).toContain('class="hi-view"');
    expect(sec).toContain("t('practice.viewReport')");
    expect(sec).toContain("t('practice.collapseReport')");
    expect(sec).toContain("'expand_less'");
    expect(sec).toContain("'expand_more'");
    expect(sec).toContain('aria-expanded');
    expect(sec).toContain('data-testid="shadow-view-report"');
    expect(sec).toContain('$event.stopPropagation(); shadow.expandShadowRow(row)');
  });

  it('跟读行无评分入口、无评分数字', () => {
    const sec = shadowSection();
    expect(sec).not.toContain('hi-score');
    expect(sec).not.toContain('gradeRecording');
    expect(sec).not.toContain('hi-badge');
    expect(sec).not.toContain('runAiScoring');
  });

  it('expandShadowRow 本身不起播：方法体内无 .play( 调用', () => {
    const src = read('practice-shadow-session.ts');
    const start = src.indexOf('async expandShadowRow');
    const end = src.indexOf('async toggleShadowPlay', start);
    expect(start).toBeGreaterThan(-1);
    expect(end).toBeGreaterThan(start);
    expect(src.slice(start, end)).not.toContain('.play(');
  });

  it('常规行原样未动：仍有评分钮与 View Report 开关', () => {
    const sec = normalSection();
    expect(sec).toContain('hi-score');
    expect(sec).toContain('gradeRecording(r.id)');
    expect(sec).toContain('class="hi-view"');
    expect(sec).toContain("t('practice.viewReport')");
    expect(sec).toContain("t('practice.collapseReport')");
  });

  it('跟读行行首播放钮接线未动：仍调 toggleShadowPlay（非 View Report 路径）', () => {
    const sec = shadowSection();
    expect(sec).toContain('shadow.toggleShadowPlay(row)');
  });
});
