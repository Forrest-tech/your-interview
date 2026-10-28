/**
 * 票 281：跟读行时长与常规行同款“当前 / 总长” — 模板/ helper 契约单测。
 *   · 跟读行 hi-time 不再是裸总长（旧：{{ fmtDuration(row.durationSec) }} → "21s"）；
 *   · 改为 {{ shadowPlayTime(row) }} / {{ fmtDuration(row.durationSec) }}，
 *     未播本行时 "0:00 / 21s"，播本行时跟随 shadowPlayhead；
 *   · helper 语义与常规行 playingTime 同口径：非本行返回 "0:00"，
 *     本行取 playhead（秒）下取整转 m:ss（分 + ':' + 两位秒）；
 *   · 常规行 hi-time 一字未动；meta 行、View Report、播放链均不在本票范围。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

const tpl = SRC('./ai-practice.component.html');
const ts = SRC('./ai-practice.component.ts');

// 跟读列表区：从 shadow-rows 循环起，到常规行 hi-time 止（常规行在后）。
const shadowList = (() => {
  const start = tpl.indexOf('@for (row of shadow.shadowRows()');
  const end = tpl.indexOf('{{ playingTime(r) }}', start);
  expect(start).toBeGreaterThan(-1);
  expect(end).toBeGreaterThan(start);
  return tpl.slice(start, end);
})();

const helper = (() => {
  const start = ts.indexOf('shadowPlayTime(row: ShadowRowView)');
  expect(start).toBeGreaterThan(-1);
  return ts.slice(start, ts.indexOf('\n  }', start));
})();

describe('票281：跟读行 hi-time 与常规行同款“当前 / 总长”', () => {
  it('跟读行 hi-time 绑定为 shadowPlayTime(row) / fmtDuration(row.durationSec)', () => {
    expect(shadowList).toContain(
      '<span class="hi-time">{{ shadowPlayTime(row) }} / {{ fmtDuration(row.durationSec) }}</span>',
    );
  });

  it('跟读列表内不再有裸总长 hi-time（旧式只剩 meta 行的时长，不在 hi-time 槽）', () => {
    const hiTimes = shadowList.match(/<span class="hi-time">.*?<\/span>/g) ?? [];
    expect(hiTimes).toHaveLength(1);
    expect(hiTimes[0]).not.toBe(
      '<span class="hi-time">{{ fmtDuration(row.durationSec) }}</span>',
    );
  });

  it('helper：非本行返回 0:00，本行取 playhead 下取整转 m:ss（playingTime 同口径）', () => {
    expect(helper).toContain('this.shadow.shadowPlayingId() !== row.id');
    expect(helper).toContain("return '0:00'");
    expect(helper).toContain('Math.floor(this.shadow.shadowPlayhead())');
    expect(helper).toContain("Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0')");
  });

  it('helper：playhead 与 durationSec 同单位（秒），不做任何单位换算', () => {
    expect(helper).not.toContain('* 1000');
    expect(helper).not.toContain('/ 1000');
  });

  it('常规行 hi-time 一字未动（仍为 playingTime(r) / fmtDuration(r.duration)）', () => {
    expect(tpl).toContain(
      '<span class="hi-time">{{ playingTime(r) }} / {{ fmtDuration(r.duration) }}</span>',
    );
  });

  it('跟读行 meta 行未动（280 的日期 · 时长 · 倍速仍在）', () => {
    expect(shadowList).toContain(
      "{{ fmtTime(row.createdAt) }} · {{ fmtDuration(row.durationSec) }} · {{ row.rate }}x",
    );
  });
});
