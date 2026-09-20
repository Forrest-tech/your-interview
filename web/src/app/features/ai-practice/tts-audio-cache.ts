/**
 * ★ 2026-09-20(Forrest):示范朗读音频的**本地缓存**。
 *
 * 逻辑(Forrest 明确):
 *   · 一旦播放过一次,音频就要留在本地;
 *   · 每次点播放先比对文本是否改动过 ——
 *       未改动 → 直接用本地这份音频(不再调 Azure);
 *       有改动 → 重新向 Azure 合成,拿到后覆盖本地;
 *   · 本地已有音频时,刷新页面 / 新进页面也要能显示正确时长。
 *
 * 存 Blob 到 IndexedDB(音频体积大,localStorage 装不下)。
 * 键 = 文本 + 音色 + 语言 的指纹,文本一变指纹就变 → 自然走"重新合成"。
 *
 * 说明:这是纯前端缓存,不改任何服务端逻辑。
 */

const DB_NAME = 'your-interview-tts';
const STORE = 'audio';
const DB_VERSION = 1;

export interface CachedAudio {
  /** 指纹键:文本 + 音色 + 语言 */
  key: string;
  blob: Blob;
  /** 合成时使用的音色/语言,便于排查 */
  voice: string;
  lang: string;
  /** 写入时间(ms) */
  savedAt: number;
  /**
   * ★ 2026-09-20(Forrest):归属的素材 id。
   * 刷新时正文可能还没回填(拿不到文本指纹),但选中项的 id 是恢复得到的 ——
   * 用它就能查回本地音频、显示正确时长。
   */
  materialId?: string;
  /** 合成这段音频时用的正文(用于正文回填后校验是否已改)。 */
  text?: string;
}

/** 文本 + 音色 + 语言 → 稳定指纹键。 */
export function ttsKey(text: string, voice: string, lang: string): string {
  const raw = `${lang}|${voice}|${text}`;
  // 简单稳定的 32 位 hash + 原文长度,避免超长键
  let h = 5381;
  for (let i = 0; i < raw.length; i++) {
    h = ((h << 5) + h) ^ raw.charCodeAt(i);
  }
  return `tts_${(h >>> 0).toString(36)}_${raw.length}`;
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

async function withStore<T>(
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<T> {
  const db = await openDb();
  return new Promise<T>((resolve, reject) => {
    const tx = db.transaction(STORE, mode);
    const req = fn(tx.objectStore(STORE));
    req.onsuccess = () => resolve(req.result as T);
    req.onerror = () => reject(req.error);
    tx.oncomplete = () => db.close();
  });
}

/** 取本地音频;没有返回 null。IndexedDB 不可用(隐私模式等)时静默返回 null。 */
export async function getCachedAudio(key: string): Promise<CachedAudio | null> {
  try {
    const row = await withStore<CachedAudio | undefined>('readonly', (s) => s.get(key));
    return row && row.blob ? row : null;
  } catch {
    return null;
  }
}

/** 存本地音频(覆盖同键)。失败不抛,缓存只是加速手段。 */
export async function putCachedAudio(entry: CachedAudio): Promise<void> {
  try {
    await withStore<IDBValidKey>('readwrite', (s) => s.put(entry));
  } catch {
    /* 缓存写失败不影响播放 */
  }
}

/** ★ 按素材 id 取最近写入的一条(刷新时正文未回填的兜底路径)。 */
export async function getCachedAudioByMaterial(materialId: string): Promise<CachedAudio | null> {
  try {
    const rows = await withStore<CachedAudio[]>('readonly', (s) => s.getAll());
    const mine = (rows || []).filter((r) => r && r.materialId === materialId && r.blob);
    if (!mine.length) return null;
    mine.sort((a, b) => (b.savedAt ?? 0) - (a.savedAt ?? 0));
    return mine[0];
  } catch {
    return null;
  }
}

/** 清掉过期的本地音频(可选,用于后续维护)。 */
export async function deleteCachedAudio(key: string): Promise<void> {
  try {
    await withStore<undefined>('readwrite', (s) => s.delete(key));
  } catch {
    /* ignore */
  }
}
