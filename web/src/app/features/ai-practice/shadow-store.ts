/**
 * 票 250：跟读本地存储（IndexedDB，零后端改动）。
 *
 * 口径（PLAN-PATCHED4 §4，含 P29）：
 * - 一条 take 一条记录，键 = 本地 take id（采集时创建）；上传成功后
 *   copy-then-verify-then-delete 重键为服务端 GUID（P18），绝不
 *   delete-then-write；校验失败保留本地记录 + 重试。
 * - 待提交记录在 Stop 时即持久化（P14），不是内存态：导航离开的 take
 *   永远不是孤儿。putShadowTake 返回是否真正落进 IndexedDB；
 *   落进内存退路 = 写失败（P29g），调用方（Stop）必须显式报错、
 *   保留内存态审阅并提供 retry-write/Discard，绝不静默。
 * - 孤儿（P14 绑定口径）：同时满足无服务端行、无持久待提交记录、
 *   无待上传/重试项；扫除只在跟读列表加载与行删除时跑，只删孤儿，
 *   怀疑时保留。isShadowOrphan 的纯判定见 shadow-waveform.helpers。
 *   例外（P29b）：dual 本地行是用户可见的已保存行，只由显式行删除
 *   删除，扫除永不删 dual 行。
 * - Discard（P18）：删该 take 的全部本地产物（voice blob、peaks、
 *   F0 轮廓、缓存混音、待提交记录）—— 本记录即全部产物，无旁表。
 * - IndexedDB 不可用时（隐私模式 / Jest node 环境）退回内存 Map，
 *   语义相同（copy-verify-delete、孤儿判定、去重均走同一代码），
 *   但 put 的返回值如实报 false（未持久化）。
 * - 重键校验（P29g）：decode-based —— 回读须满足 blob size > 0
 *   且 peaks 在且 voice blob 可解码（decodeAudioData 成功、时长 > 0）。
 *   解码失败 = 校验失败，保留本地记录 + 重试，绝不删本地键。
 */
import { dedupeShadowKeys, isShadowOrphan } from './shadow-waveform.helpers';
import type { ShadowPeak } from './shadow-waveform.helpers';

export type ShadowTakeKind = 'mix' | 'dual';
export type ShadowMixStatus =
  | 'idle' | 'pending' | 'rendering' | 'ready' | 'failed' | 'uploading' | 'done' | 'local';

export interface ShadowTakeRecord {
  /** 当前键：本地 take id，或重键后的服务端 GUID。 */
  key: string;
  /** 采集时创建的本地 take id（重键后仍保留，用于去重关联）。 */
  localTakeId: string;
  /** 服务端 GUID（未上传为 null）。 */
  serverId: string | null;
  materialId: string;
  /** 跟读倍速（take 率；审阅率是独立的，不写回此处）。 */
  rate: number;
  /** 墙钟 laneShift（秒）= recorderStart − referenceStart。 */
  laneShiftSec: number;
  /** 参考音轨时长（秒，1x）。 */
  refDurationSec: number;
  /** 活声墙钟时长（秒）：行时长 jointDuration 的输入之一。 */
  voiceWallSec: number;
  /** 参考缓存键（TTS 指纹），用于取回参考音频/峰值。 */
  refKey: string;
  kind: ShadowTakeKind;
  mixStatus: ShadowMixStatus;
  /** 待上传/重试项是否存在（孤儿判定的第三条件）。 */
  pendingUpload: boolean;
  createdAt: number;
  voiceBlob: Blob | null;
  /** 活声峰值（t = 参考音轨时间，v = 0~100）。 */
  peaks: ShadowPeak[];
  /** 事后活声 F0 轮廓（P25：算一次后缓存于此，不再每渲染重算）。 */
  f0Contour: (number | null)[] | null;
  /** 1x 混音缓存（上传前持有；上传后仍保留引用直至重键完成）。 */
  mixBlob: Blob | null;
  /** 跟读原文（P29h：缓存缺失时重请求 TTS 用；无则无法重生成）。 */
  text?: string;
}

const DB_NAME = 'your-interview-shadow';
const STORE = 'takes';
const DB_VERSION = 1;

/** 内存退路：与 IndexedDB 同语义的 Map（见文件头注）。 */
const memFallback = new Map<string, ShadowTakeRecord>();

function idbAvailable(): boolean {
  try {
    return typeof indexedDB !== 'undefined' && !!indexedDB.open;
  } catch {
    return false;
  }
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) {
        db.createObjectStore(STORE, { keyPath: 'key' });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function idb<T>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest): Promise<T> {
  const db = await openDb();
  return new Promise<T>((resolve, reject) => {
    const tx = db.transaction(STORE, mode);
    const req = fn(tx.objectStore(STORE));
    req.onsuccess = () => resolve(req.result as T);
    req.onerror = () => reject(req.error);
    tx.oncomplete = () => db.close();
    tx.onerror = () => reject(tx.error);
  });
}

/**
 * 写记录。返回 true = 真正落进 IndexedDB；false = 只进了内存退路
 * （IDB 不可用或写失败）—— 调用方按 P29g 把 false 当写失败处理。
 */
export async function putShadowTake(rec: ShadowTakeRecord): Promise<boolean> {
  if (!idbAvailable()) {
    memFallback.set(rec.key, rec);
    return false;
  }
  try {
    await idb<IDBValidKey>('readwrite', (s) => s.put(rec));
    return true;
  } catch {
    memFallback.set(rec.key, rec);
    return false;
  }
}

export async function getShadowTake(key: string): Promise<ShadowTakeRecord | null> {
  if (!idbAvailable()) return memFallback.get(key) ?? null;
  try {
    const row = await idb<ShadowTakeRecord | undefined>('readonly', (s) => s.get(key));
    return row ?? null;
  } catch {
    return memFallback.get(key) ?? null;
  }
}

export async function getShadowTakesForMaterial(materialId: string): Promise<ShadowTakeRecord[]> {
  const all: ShadowTakeRecord[] = [];
  if (idbAvailable()) {
    try {
      const rows = await idb<ShadowTakeRecord[]>('readonly', (s) => s.getAll());
      all.push(...(rows || []));
    } catch {
      /* 回落内存 */
    }
  }
  for (const r of memFallback.values()) {
    if (!all.some((x) => x.key === r.key)) all.push(r);
  }
  return all
    .filter((r) => r.materialId === materialId)
    .sort((a, b) => a.createdAt - b.createdAt);
}

/** Discard：删该 take 的全部本地产物（本记录即全部产物）。 */
export async function deleteShadowTake(key: string): Promise<void> {
  memFallback.delete(key);
  if (!idbAvailable()) return;
  try {
    await idb<undefined>('readwrite', (s) => s.delete(key));
  } catch {
    /* 内存已删，IDB 失败不阻塞 Discard 的用户意图 */
  }
}

/**
 * 重键（P18 copy-then-verify-then-delete + P29g decode-based 校验）：
 * 写 GUID 记录 → 回读校验（blob size > 0 且 peaks 在；若传入 decode，
 * 还须 voice blob 解码成功且时长 > 0）→ 删本地记录。校验失败返回
 * false 并保留本地记录 + 重试（未验证的 GUID 记录删掉，本地键恢复）。
 * 生产调用方（session）恒传 decodeAudio 派生的 decode；不传时退化为
 * size + peaks 校验（仅用于无解码环境的单测默认路径）。
 */
export async function rekeyShadowTake(
  localKey: string,
  serverId: string,
  decode?: (blob: Blob) => Promise<number>,
): Promise<boolean> {
  const src = await getShadowTake(localKey);
  if (!src) return false;
  const next: ShadowTakeRecord = { ...src, key: serverId, serverId, pendingUpload: false };
  await putShadowTake(next);
  const back = await getShadowTake(serverId);
  let decodeOk = true;
  if (back?.voiceBlob && decode) {
    try {
      decodeOk = (await decode(back.voiceBlob)) > 0;
    } catch {
      decodeOk = false;
    }
  }
  if (!back || (src.voiceBlob && (!back.voiceBlob || back.voiceBlob.size === 0)) || !back.peaks || !decodeOk) {
    // 校验失败：删掉未验证的 GUID 记录，保留本地记录 + 重试。
    await deleteShadowTake(serverId);
    // 注意：deleteShadowTake 删了 GUID 键；本地键需恢复（若同库被覆盖则重写）。
    await putShadowTake(src);
    return false;
  }
  if (serverId !== localKey) await deleteShadowTake(localKey);
  return true;
}

/**
 * 扫除（P14）：只删孤儿。serverIds = 当前服务端录音行 id 集合；
 * pendingKeys = 持久待提交 / 待上传重试的键集合（调用方传入，含本地键）。
 * 返回被删的键。GUID 与本地并存时去重只显 GUID（P26，显示层用
 * dedupeShadowKeys；此处不删本地副本——重键流程负责删，扫除不越权）。
 * P29b：dual 行是用户可见的已保存本地行，扫除永不删，只由显式行删除删。
 * 票 266：本地-only 行（serverId 为空）一律是用户数据，扫除永不删——
 * 跟读只留本机后，本地行就是正本，不能因服务端没有对应行而被清掉。
 */
export async function sweepShadowOrphans(
  materialId: string,
  serverIds: Set<string>,
  pendingKeys: Set<string>,
): Promise<string[]> {
  const rows = await getShadowTakesForMaterial(materialId);
  const deleted: string[] = [];
  for (const r of rows) {
    if (r.kind === 'dual') continue;
    if (r.serverId === null) continue;
    const hasServerRow = (r.serverId !== null && serverIds.has(r.serverId)) || serverIds.has(r.key);
    const orphan = isShadowOrphan(hasServerRow, pendingKeys.has(r.key), r.pendingUpload || pendingKeys.has(r.key));
    if (orphan) {
      await deleteShadowTake(r.key);
      deleted.push(r.key);
    }
  }
  return deleted;
}

/** 去重后的显示键（P26）：GUID 与本地并存只显 GUID。 */
export function visibleShadowKeys(records: ShadowTakeRecord[]): string[] {
  return dedupeShadowKeys(
    records.map((r) => ({ key: r.key, serverId: r.serverId, localTakeId: r.localTakeId })),
  );
}
