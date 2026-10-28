/**
 * 票 267：跟读行中间空闲过大（用户截图：Recordings Shadow 列表行中间空 track
 * 占了行宽大半）。根因是行内有两个 flex 生长项：hi-track flex:1 与 spacer
 * flex:1。修法只收跟读行（.shadow-item），常规行不动：track 改 140px 基宽、
 * 可缩不伸，spacer 藏掉；track 的播放进度与点击 seek 功能保留。
 * 本文件为结构契约（按票 264/265/266 结构契约先例断言源码）；行为由沙盒走查覆盖。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票 267：跟读行收紧中间空闲', () => {
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const scss = fs.readFileSync(path.join(__dirname, 'ai-practice.component.scss'), 'utf8');

  it('SCSS：跟读行 track 不再横向生长', () => {
    expect(scss).toContain('.hist-item.shadow-item .hi-track');
    expect(scss).toContain('flex: 0 1 140px');
  });

  it('SCSS：跟读行 spacer 藏掉，时长与删除钮不再被推到行尾', () => {
    expect(scss).toContain('.hist-item.shadow-item .spacer');
    expect(scss).toContain('display: none');
  });

  it('SCSS：常规行基规则不动（track 仍可生长、spacer 仍在）', () => {
    expect(scss).toContain('.hist-card .hist-item .hi-track');
    expect(scss).toContain('flex: 1 1 90px');
    expect(scss).toContain('.hist-card .hist-item .spacer { flex: 1 1 auto; min-width: 0; }');
  });

  it('模板：跟读行控件与信息一个不少（track 进度、时长、删除钮都在）', () => {
    expect(html).toContain('class="track hi-track"');
    expect(html).toContain('shadow.seekShadowLane(row, $event)');
    expect(html).toContain('class="track-fill"');
    expect(html).toContain('shadow.shadowPlayhead() / row.durationSec * 100');
    expect(html).toContain('class="hi-time"');
    expect(html).toContain('class="hi-del"');
    expect(html).toContain('class="hi-play"');
    expect(html).toContain('class="hi-idx"');
    expect(html).toContain('class="hi-meta"');
  });

  it('模板：spacer 元素保留（常规行仍用它，跟读行只由 CSS 藏掉）', () => {
    expect(html).toContain('<span class="spacer"></span>');
  });
});
