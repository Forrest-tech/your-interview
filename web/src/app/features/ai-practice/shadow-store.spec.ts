/**
 * 票 250：跟读存储单测（P14 孤儿口径 / P18 重键 / P26 去重）。
 * Jest node 环境无 IndexedDB，走内存退路，语义与 IDB 路径同一代码。
 */
import { describe, expect, it } from '@jest/globals';
import {
  deleteShadowTake,
  getShadowTake,
  putShadowTake,
  rekeyShadowTake,
  sweepShadowOrphans,
  visibleShadowKeys,
} from './shadow-store';
import type { ShadowTakeRecord } from './shadow-store';

function rec(key: string, patch: Partial<ShadowTakeRecord> = {}): ShadowTakeRecord {
  return {
    key,
    localTakeId: key,
    serverId: null,
    materialId: 'mat-1',
    rate: 1,
    laneShiftSec: 0,
    refDurationSec: 21,
    voiceWallSec: 22,
    refKey: 'tts_x',
    kind: 'mix',
    mixStatus: 'pending',
    pendingUpload: false,
    createdAt: 1000,
    voiceBlob: new Blob(['x'], { type: 'audio/webm' }),
    peaks: [{ t: 0, v: 10 }],
    f0Contour: null,
    mixBlob: null,
    ...patch,
  };
}

describe('shadow-store', () => {
  it('put/get 往返；重键 copy-verify-delete 后本地键消失、GUID 键在', async () => {
    await putShadowTake(rec('local-a'));
    expect((await getShadowTake('local-a'))?.localTakeId).toBe('local-a');
    expect(await rekeyShadowTake('local-a', 'guid-a')).toBe(true);
    expect(await getShadowTake('local-a')).toBeNull();
    const g = await getShadowTake('guid-a');
    expect(g?.serverId).toBe('guid-a');
    expect(g?.localTakeId).toBe('local-a');
    await deleteShadowTake('guid-a');
  });
  it('扫除只删孤儿：待提交/待上传/已上服务端行一律保留', async () => {
    await putShadowTake(rec('orphan-1'));
    await putShadowTake(rec('pend-1', { mixStatus: 'pending' }));
    await putShadowTake(rec('retry-1', { pendingUpload: true }));
    await putShadowTake(rec('guided-1', { key: 'guided-1', serverId: 'guided-1', localTakeId: 'local-g' }));
    const deleted = await sweepShadowOrphans('mat-1', new Set(['guided-1']), new Set(['pend-1']));
    expect(deleted).toEqual(['orphan-1']);
    expect(await getShadowTake('pend-1')).not.toBeNull();
    expect(await getShadowTake('retry-1')).not.toBeNull();
    expect(await getShadowTake('guided-1')).not.toBeNull();
    await deleteShadowTake('pend-1');
    await deleteShadowTake('retry-1');
    await deleteShadowTake('guided-1');
  });
  it('visibleShadowKeys：GUID 与本地并存只显 GUID', () => {
    const rows = [
      rec('local-b'),
      rec('guid-b', { serverId: 'guid-b', localTakeId: 'local-b' }),
    ];
    expect(visibleShadowKeys(rows)).toEqual(['guid-b']);
  });
  it('P29g：put 在 node 环境走内存退路，返回值如实报 false（未持久化）', async () => {
    expect(await putShadowTake(rec('mem-x'))).toBe(false);
    expect((await getShadowTake('mem-x'))?.localTakeId).toBe('mem-x');
    await deleteShadowTake('mem-x');
  });
  it('P29g：重键 decode 成功 → 通过；解码失败/时长 0 → 保留本地、GUID 键不在', async () => {
    await putShadowTake(rec('local-d'));
    expect(await rekeyShadowTake('local-d', 'guid-d', async () => 2.5)).toBe(true);
    expect(await getShadowTake('local-d')).toBeNull();
    expect(await getShadowTake('guid-d')).not.toBeNull();
    await deleteShadowTake('guid-d');

    await putShadowTake(rec('local-e'));
    expect(await rekeyShadowTake('local-e', 'guid-e', async () => {
      throw new Error('bad');
    })).toBe(false);
    expect(await getShadowTake('local-e')).not.toBeNull();
    expect(await getShadowTake('guid-e')).toBeNull();

    await putShadowTake(rec('local-f'));
    expect(await rekeyShadowTake('local-f', 'guid-f', async () => 0)).toBe(false);
    expect(await getShadowTake('local-f')).not.toBeNull();
    expect(await getShadowTake('guid-f')).toBeNull();
    await deleteShadowTake('local-e');
    await deleteShadowTake('local-f');
  });
  it('P29b：扫除永不删 dual 本地行，只删 mix 孤儿', async () => {
    await putShadowTake(rec('dual-keep', { kind: 'dual', mixStatus: 'local' }));
    await putShadowTake(rec('mix-orphan-2'));
    const deleted = await sweepShadowOrphans('mat-1', new Set(), new Set());
    expect(deleted).toEqual(['mix-orphan-2']);
    expect(await getShadowTake('dual-keep')).not.toBeNull();
    expect(await getShadowTake('mix-orphan-2')).toBeNull();
    await deleteShadowTake('dual-keep');
  });
});
