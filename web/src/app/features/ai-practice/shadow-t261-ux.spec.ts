/**
 * 票 261：跟读练习 UX 重构（用户截图 7 点）单测。
 * 覆盖纯函数（编号/排序/线 points/麦克风请求）+ 模板与样式的结构契约
 * （开关位置、菜单化倍速、无展开 pills、无柱形、无旧删除直通）。
 * 真机麦克风采集不在沙盒验证范围内（见实现报告的诚实缺口）。
 */
import { describe, expect, it, jest } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import {
  assignShadowNumbers,
  orderNewestFirst,
  serverOnlyShadowRows,
} from './practice-shadow-session';
import { peaksToLinePoints } from './shadow-waveform.helpers';
import {
  valuesToLinePoints as waveLinePoints,
  valuesToMirroredLinePoints,
} from './waveform.helpers';
import { requestMicStream } from './shadow-capture';

describe('票 261 §1：编号时间先后、显示最新在上', () => {
  it('assignShadowNumbers：最早 #1，与输入顺序无关', () => {
    const rows = [
      { id: 'c', createdAt: 300 },
      { id: 'a', createdAt: 100 },
      { id: 'b', createdAt: 200 },
    ];
    const map = assignShadowNumbers(rows);
    expect(map.get('a')).toBe(1);
    expect(map.get('b')).toBe(2);
    expect(map.get('c')).toBe(3);
  });

  it('orderNewestFirst：最新在上，且不改原数组', () => {
    const rows = [
      { id: 'a', createdAt: 100 },
      { id: 'b', createdAt: 300 },
      { id: 'c', createdAt: 200 },
    ];
    expect(orderNewestFirst(rows).map((r) => r.id)).toEqual(['b', 'c', 'a']);
    expect(rows.map((r) => r.id)).toEqual(['a', 'b', 'c']);
  });

  it('serverOnlyShadowRows：行带 no 占位 0（shadowRows computed 统一编号）', () => {
    const rec = {
      id: 'r1',
      isShadow: true,
      createdAt: 1000,
      duration: 5,
      takeRate: 1.5,
    } as never;
    const rows = serverOnlyShadowRows([rec], new Set<string>());
    expect(rows).toHaveLength(1);
    expect(rows[0].no).toBe(0);
  });
});

describe('票 261 §6：波形线 points', () => {
  it('peaksToLinePoints：已知峰值 → 已知 points（柱顶 y = 100 - v）', () => {
    const peaks = [
      { t: 0, v: 0 },
      { t: 1, v: 50 },
      { t: 2, v: 100 },
    ];
    expect(peaksToLinePoints(peaks, 2)).toBe('0.0,100.0 500.0,50.0 1000.0,0.0');
  });

  it('peaksToLinePoints：v 越界钳制、t 越界钳制', () => {
    const peaks = [
      { t: -5, v: -20 },
      { t: 99, v: 150 },
    ];
    expect(peaksToLinePoints(peaks, 2)).toBe('0.0,100.0 1000.0,0.0');
  });

  it('peaksToLinePoints：少于 2 点或轴长非法 → 空串（不画线）', () => {
    expect(peaksToLinePoints([], 2)).toBe('');
    expect(peaksToLinePoints([{ t: 0, v: 50 }], 2)).toBe('');
    expect(peaksToLinePoints([{ t: 0, v: 50 }, { t: 1, v: 60 }], 0)).toBe('');
  });

  it('valuesToMirroredLinePoints：中线对称（0~100 尺度）', () => {
    const [upper, lower] = valuesToMirroredLinePoints([0, 50, 100]);
    expect(upper).toBe('0.0,50.0 500.0,25.0 1000.0,0.0');
    expect(lower).toBe('0.0,50.0 500.0,75.0 1000.0,100.0');
  });

  it('valuesToMirroredLinePoints：已播部分 x 与全长同标尺（只截前 count 个点）', () => {
    const [full] = valuesToMirroredLinePoints([0, 50, 100, 80]);
    const [played] = valuesToMirroredLinePoints([0, 50, 100, 80], 100, 2);
    expect(full).toBe('0.0,50.0 333.3,25.0 666.7,0.0 1000.0,10.0');
    expect(played).toBe('0.0,50.0 333.3,25.0');
  });

  it('waveform.helpers valuesToLinePoints：0~1 尺度（实时音量线）', () => {
    expect(waveLinePoints([0, 0.5, 1], 1)).toBe('0.0,100.0 500.0,50.0 1000.0,0.0');
  });
});

describe('票 261 §2：requestMicStream 设备回落', () => {
  // 本工程 Jest 用 node 环境（无 window）：直接在 globalThis 上挂 navigator。
  const mockMedia = (impl: (c: MediaStreamConstraints) => Promise<MediaStream>) => {
    const mediaDevices = { getUserMedia: jest.fn(impl) };
    (globalThis as unknown as { navigator: unknown }).navigator = { mediaDevices };
    return mediaDevices;
  };
  const fakeStream = {} as MediaStream;

  it('指定 deviceId → 以 exact 约束请求', async () => {
    const md = mockMedia(async () => fakeStream);
    const stream = await requestMicStream('dev-1');
    expect(stream).toBe(fakeStream);
    const c = md.getUserMedia.mock.calls[0][0] as { audio: Record<string, unknown> };
    expect(c.audio['echoCancellation']).toBe(true);
    expect(c.audio['noiseSuppression']).toBe(false);
    expect(c.audio['deviceId']).toEqual({ exact: 'dev-1' });
  });

  it('OverconstrainedError → 回落默认设备（不带 deviceId 再请求一次）', async () => {
    const err = new Error('gone');
    err.name = 'OverconstrainedError';
    const md = mockMedia(async (c) => {
      const audio = c.audio as Record<string, unknown>;
      if (audio['deviceId']) throw err;
      return fakeStream;
    });
    const stream = await requestMicStream('stale-dev');
    expect(stream).toBe(fakeStream);
    expect(md.getUserMedia).toHaveBeenCalledTimes(2);
    const second = md.getUserMedia.mock.calls[1][0] as { audio: Record<string, unknown> };
    expect(second.audio['deviceId']).toBeUndefined();
  });

  it('其它错误（NotAllowedError）原样上抛，不回落', async () => {
    const err = new Error('denied');
    err.name = 'NotAllowedError';
    const md = mockMedia(async () => {
      throw err;
    });
    await expect(requestMicStream('dev-1')).rejects.toBe(err);
    expect(md.getUserMedia).toHaveBeenCalledTimes(1);
  });

  it('null → 默认设备（不带 deviceId）', async () => {
    const md = mockMedia(async () => fakeStream);
    await requestMicStream(null);
    const c = md.getUserMedia.mock.calls[0][0] as { audio: Record<string, unknown> };
    expect(c.audio['deviceId']).toBeUndefined();
  });
});

describe('票 261：i18n 新键三语齐全', () => {
  it('micDevice / micDefault / noVoice / liveMeterUnavailable 均有 zh/en/fr', () => {
    const i18n = fs.readFileSync(
      path.join(__dirname, '../../core/i18n/i18n.service.ts'),
      'utf8',
    );
    for (const key of [
      'practice.shadow.micDevice',
      'practice.shadow.micDefault',
      'practice.shadow.noVoice',
      'practice.shadow.liveMeterUnavailable',
    ]) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });
});

describe('票 261：模板与样式结构契约', () => {
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const scss = fs.readFileSync(path.join(__dirname, 'ai-practice.component.scss'), 'utf8');

  it('§4：跟读开关在 Previous 左侧（data-testid 在 prevItem 之前出现）', () => {
    expect(html).toContain('data-testid="shadow-toggle"');
    expect(html.indexOf('data-testid="shadow-toggle"')).toBeLessThan(html.indexOf('prevItem()'));
    expect(html).toContain('mat-slide-toggle');
  });

  it('§3（票 265 修订）：跟读倍速行已删除，倍速只用示范条 x1；回放菜单仍在，跟读面板无开始/停止键', () => {
    // 录制倍速行彻底删除（不是改回 pills，也不是换菜单样式）。
    expect(html).not.toContain('shadow-rate-row');
    expect(html).not.toContain('shadowRateMenu');
    expect(html).not.toContain('shadow.setShadowRate');
    // 回放菜单是另一行，刻意保留：仍用菜单键，仍用同一套 class。
    expect(html).toContain('shadowReviewMenu');
    expect(html).toContain('shadow-rate-menu-btn');
    expect(html).not.toContain('shadow-rate-btn');
    expect(html).not.toContain('data-testid="shadow-start"');
    expect(html).not.toContain('data-testid="shadow-stop"');
    expect(scss).not.toContain('.shadow-rate-btn {');
    expect(scss).not.toContain('.shadow-start-btn');
  });

  it('§5：绿色录音键在跟读模式下也作停止键（模板条件含 shadowRecording）', () => {
    expect(html).toContain('recorder.recording() || shadow.shadowRecording()');
  });

  it('§6：波形用 polyline（无柱形 span），跟读/报告/实时三路齐全', () => {
    expect(html).toContain('shadow-line');
    expect(html).toContain('ws-line');
    expect(html).toContain('live-line');
    expect(html).not.toContain('shadow-bar');
    expect(html).not.toContain('ws-bar');
    expect(html).not.toContain('lw-bar');
    expect(scss).not.toContain('.shadow-bar {');
    expect(scss).not.toContain('.ws-bar {');
    expect(scss).not.toContain('.lw-bar {');
  });

  it('§1：跟读列表用 row.no、常规列表用 orderedNonShadowRecordings', () => {
    expect(html).toContain('#{{ row.no }}');
    expect(html).toContain('orderedNonShadowRecordings()');
  });

  it('§2（票 263：已移出面板）：跟读面板无麦克风设备选择', () => {
    expect(html).not.toContain('shadow-mic-select');
    expect(html).not.toContain('shadow-mic-row');
  });

  it('§7：本地行删除与待提交丢弃走确认（session 源码含 confirmDialog 门）', () => {
    const session = fs.readFileSync(path.join(__dirname, 'practice-shadow-session.ts'), 'utf8');
    const removeBlock = session.slice(session.indexOf('async removeShadowRow'));
    expect(removeBlock.slice(0, 1200)).toContain('confirmDialog');
    const discardBlock = session.slice(session.indexOf('async discardShadowPending'));
    expect(discardBlock.slice(0, 1200)).toContain('confirmDialog');
  });
});
