/**
 * 票 258 S1：跨设备跟读标识 — 分类规则纯函数单测。
 * 规则（S3-PLAN §2.2，唯一口径）：行是跟读行 IFF 服务端 IsShadow 为 true
 * OR 本地 shadow-store 有链接；本地链接优先（服务端 false 但本地有链接
 * 仍是跟读行）；两者皆无 = 非跟读行。
 * 另断言有意的行为变化：另一设备上的 1x 跟读行（服务端标识、无本地链接）
 * 进跟读列表、不进普通列表；且任何行不会从两列表同时消失。
 */
import { describe, expect, it } from '@jest/globals';
import {
  isShadowRow,
  serverOnlyShadowRows,
} from './practice-shadow-session';

describe('isShadowRow（票258 S1 分类规则）', () => {
  it('服务端标识 true、无本地链接 → 跟读行（另一设备 1x 行）', () => {
    expect(isShadowRow(true, false)).toBe(true);
  });

  it('服务端标识 false、有本地链接 → 跟读行（本地优先，覆盖过渡期历史行）', () => {
    expect(isShadowRow(false, true)).toBe(true);
  });

  it('服务端标识 true、有本地链接 → 跟读行', () => {
    expect(isShadowRow(true, true)).toBe(true);
  });

  it('两者皆无 → 非跟读行', () => {
    expect(isShadowRow(false, false)).toBe(false);
  });

  it('服务端标识缺省（历史行/旧响应）→ 按 false 处理', () => {
    expect(isShadowRow(undefined, false)).toBe(false);
    expect(isShadowRow(undefined, true)).toBe(true);
  });
});

describe('serverOnlyShadowRows（票258 S1 另一设备行）', () => {
  const rec = (id: string, isShadow?: boolean) => ({
    id,
    createdAt: 1000,
    duration: 12.5,
    isShadow,
  });

  it('服务端标识、无本地链接 → 一行 mix/done/已上传，localTakeId 为空', () => {
    const rows = serverOnlyShadowRows([rec('srv-1', true)], new Set());
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      id: 'srv-1',
      key: 'srv-1',
      localTakeId: '',
      serverId: 'srv-1',
      durationSec: 12.5,
      rate: 1,
      kind: 'mix',
      mixStatus: 'done',
      uploaded: true,
    });
  });

  it('已链接的行（key/localTakeId/serverId 任一命中）不重复进列', () => {
    const linked = new Set(['srv-1']);
    expect(serverOnlyShadowRows([rec('srv-1', true)], linked)).toHaveLength(0);
  });

  it('非跟读行（标识 false/缺省）不进跟读列表', () => {
    const rows = serverOnlyShadowRows(
      [rec('a', false), rec('b'), rec('c', true)],
      new Set(['c']),
    );
    expect(rows).toHaveLength(0);
  });

  it('混合列表：仅未链接的服务端跟读行进列，其余行仍归普通列表', () => {
    const recordings = [rec('srv-1', true), rec('srv-2', true), rec('plain', false)];
    const linked = new Set(['srv-2']);
    const shadow = serverOnlyShadowRows(recordings, linked);
    expect(shadow.map((r) => r.id)).toEqual(['srv-1']);
    // 普通列表口径 = NOT isShadowRow：srv-1 已进跟读列，plain 留普通列，
    // srv-2 有本地链接故也是跟读列——三行各有归属，无一行从两列表同时消失。
    const normal = recordings.filter((r) => !isShadowRow(r.isShadow, linked.has(r.id)));
    expect(normal.map((r) => r.id)).toEqual(['plain']);
    const all = new Set([...shadow.map((r) => r.id), ...normal.map((r) => r.id), 'srv-2']);
    expect(all.size).toBe(3);
  });
});
