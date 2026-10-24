/**
 * ★ 票124 前端验收(F1–F6)。
 *
 * 基建口径(先说清,防误读):本仓 Jest = node 环境 + @angular/* 替身
 * (见 jest.config.cjs 头注与 test/jest-stub.ts),无 TestBed / DOM 渲染。
 * 票124 的等价构造:
 *   · F1 —— 测**真实** PracticeApi 源文件(jest.config 的 moduleNameMapper
 *     已把 practice-api.service 的替身**收窄到 intake-panel 一处**),
 *     其 inject(HttpClient) 经 stub 的 setInjectProvider 钩子拿到假 HttpClient,
 *     逐字断言请求 URL / HTTP 方法 / 树摘要采纳 / 错误原样上抛。
 *   · F2/F3/F5 —— 测纯逻辑层(material-trash.helpers.ts):字段视图、
 *     显隐条件、空态 vs 错误态、徽标与指针判定。
 *   · F4 —— 逐键断言 i18n.service.ts 里 12 个 practice.trash.* 键的三语非空。
 *   · F6 —— 测 PracticeMaterialTree 的 C3 复位:恢复成功后 treeDirty 与
 *     treeDeletedSinceSave 均为 false。真实类经同一 inject 钩子构造。
 * DOM 层断言(徽标/指针行的实际渲染)以**模板源码契约断言**落码
 * (同 ticket122 / ticket128 spec 的模板契约先例),真实浏览器行为由活体 E1–E8 兜底。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it, beforeAll, beforeEach, afterEach } from '@jest/globals';
import { firstValueFrom, of, throwError } from 'rxjs';
import * as ngCore from '@angular/core';
import { setInjectProvider } from '../../../../test/jest-stub';
import { DeletedMaterialDto, MaterialNodeDto, PracticeApi } from '../../core/api/practice-api.service';
import {
  trashBadgeLabel, trashItemView, shouldShowRestoreAll,
  shouldShowTrashEmpty, shouldShowTrashError
} from './material-trash.helpers';

// ============================================================
// 替身环境的 inject 钩子:让真实源文件能拿到假 HttpClient。
// ============================================================

interface FakeHttp {
  get(url: string, opts?: unknown): unknown;
  post(url: string, body?: unknown): unknown;
  put(url: string, body?: unknown): unknown;
}

/** 记录下来的请求(供 F1 逐字断言)。 */
interface Recorded {
  method: 'GET' | 'POST' | 'PUT';
  url: string;
  body?: unknown;
}

/** 装上假 HttpClient 并返回请求记录表。 */
function installFakeHttp(responder: (r: Recorded) => unknown): Recorded[] {
  const calls: Recorded[] = [];
  const http: FakeHttp = {
    get: (url: string) => { const rec = { method: 'GET' as const, url }; calls.push(rec); return responder(rec); },
    post: (url: string, body?: unknown) => {
      const rec = { method: 'POST' as const, url, body }; calls.push(rec); return responder(rec);
    },
    put: (url: string, body?: unknown) => {
      const rec = { method: 'PUT' as const, url, body }; calls.push(rec); return responder(rec);
    }
  };
  patchInject(http);
  return calls;
}

// setInjectProvider 是 stub 的钩子;真实源文件里的 inject 来自 '@angular/core'
// (被 moduleNameMapper 映射到 stub),故两条路都要铺。
function patchInject(http: unknown): void {
  setInjectProvider(() => http);
  (ngCore as unknown as { inject: unknown }).inject = () => http;
}

afterEach(() => {
  setInjectProvider(null);
  (ngCore as unknown as { inject: unknown }).inject = () => {
    throw new Error('jest-stub: inject() called outside a stubbed component test scope');
  };
});

// ============================================================
// F1 —— PracticeApi 三方法:URL/方法逐字、treeVersion 采纳、错误原样上抛
// ============================================================

const TREE_NODES: MaterialNodeDto[] = [{
  id: 'm1', name: 'A', folder: true, sortOrder: 0, expanded: true,
  content: null, markColor: null, categoryId: null, children: []
}];

describe('F1(PracticeApi 回收站三方法:URL/方法逐字 + treeVersion 采纳 + 错误原样上抛)', () => {
  it('getMaterialTrash → GET /api/assessment/materials/trash,裸数组原样返回', async () => {
    const list: DeletedMaterialDto[] = [{
      id: 'd1', name: 'x', folder: false, parentId: null, path: null,
      categoryId: null, categoryName: null, markColor: null,
      deletedAt: '2026-10-20T10:00:00Z', descendantTotal: 0,
      descendantDeleted: 0, recordingCount: 0
    }];
    const calls = installFakeHttp(() => of(list));

    const api = new PracticeApi();
    const out = await firstValueFrom(api.getMaterialTrash());

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('GET');
    expect(calls[0].url).toBe('/api/assessment/materials/trash');
    expect(out).toEqual(list);
  });

  it('restoreMaterial(id) → POST /api/assessment/materials/{id}/restore,无 body', async () => {
    const calls = installFakeHttp(() => of({ restored: 3, treeVersion: 'v-1', nodes: TREE_NODES }));
    const api = new PracticeApi();
    await firstValueFrom(api.restoreMaterial('abc-123'));

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toBe('/api/assessment/materials/abc-123/restore');
    // 无 body:传 {} (与 PracticeApi 其余 POST 端点同口径)。
    expect(calls[0].body).toEqual({});
  });

  it('restoreAllMaterials() → POST /api/assessment/materials/trash/restore-all', async () => {
    const calls = installFakeHttp(() => of({ restored: 9, treeVersion: 'v-2', nodes: TREE_NODES }));
    const api = new PracticeApi();
    await firstValueFrom(api.restoreAllMaterials());

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toBe('/api/assessment/materials/trash/restore-all');
  });

  it('恢复成功后 this.treeVersion 被响应值更新 —— 随后的 saveMaterials 携带新版本(契约 C1)', async () => {
    const calls = installFakeHttp((r) => {
      if (r.method === 'POST') return of({ restored: 1, treeVersion: 'v-after-restore', nodes: TREE_NODES });
      return of({ saved: 1, treeVersion: 'v-after-restore' });
    });
    const api = new PracticeApi();

    const r = await firstValueFrom(api.restoreMaterial('m1'));
    expect(r.treeVersion).toBe('v-after-restore');
    expect(r.nodes).toEqual(TREE_NODES);

    // 关键(C1):下一次整树保存必须带**恢复后的**版本,不是恢复前的。
    await firstValueFrom(api.saveMaterials([]));
    const put = calls.find(c => c.method === 'PUT');
    expect(put).toBeDefined();
    expect((put!.body as { treeVersion: string }).treeVersion).toBe('v-after-restore');
  });

  it('恢复响应缺 nodes 时以空数组兜底(不把 undefined 泄给调用方)', async () => {
    installFakeHttp(() => of({ restored: 0, treeVersion: 'v-x' } as never));
    const api = new PracticeApi();
    const r = await firstValueFrom(api.restoreAllMaterials());
    expect(r.nodes).toEqual([]);
    expect(r.restored).toBe(0);
    expect(r.treeVersion).toBe('v-x');
  });

  it('错误原样上抛(不吞、不改形)—— 三个方法都把后端错误透给调用方', async () => {
    const boom = new Error('backend down');
    installFakeHttp(() => throwError(() => boom));

    const api = new PracticeApi();
    await expect(firstValueFrom(api.getMaterialTrash())).rejects.toBe(boom);
    await expect(firstValueFrom(api.restoreMaterial('m1'))).rejects.toBe(boom);
    await expect(firstValueFrom(api.restoreAllMaterials())).rejects.toBe(boom);
  });
});

// ============================================================
// F2 —— 对话框列表项字段与显隐条件
// ============================================================

/** 造一个清单项(默认值便于逐项覆盖)。 */
function item(over: Partial<DeletedMaterialDto> = {}): DeletedMaterialDto {
  return {
    id: 'i1', name: 'a1', folder: false, parentId: null, path: null,
    categoryId: null, categoryName: null, markColor: null,
    deletedAt: '2026-10-20T10:00:00Z', descendantTotal: 0,
    descendantDeleted: 0, recordingCount: 0, ...over
  };
}

/** 固定时间的格式化器(避免时区影响断言)。 */
const fmt = (iso: string): string => iso;

describe('F2(回收站列表项:字段渲染 + 显隐条件)', () => {
  it('类型图标:文件夹 folder / 文件 description', () => {
    expect(trashItemView(item({ folder: true }), fmt, (k, p) => k).icon).toBe('folder');
    expect(trashItemView(item({ folder: false }), fmt, (k, p) => k).icon).toBe('description');
  });

  it('主行 = Name;路径为空/空白 → null(不显示祖先链)', () => {
    expect(trashItemView(item({ name: '简历' }), fmt, (k, p) => k).name).toBe('简历');
    expect(trashItemView(item({ path: null }), fmt, (k, p) => k).path).toBeNull();
    expect(trashItemView(item({ path: '   ' }), fmt, (k, p) => k).path).toBeNull();
    expect(trashItemView(item({ path: 'A / B' }), fmt, (k, p) => k).path).toBe('A / B');
  });

  it('类别名:null / 空白 → null(由模板回落"未分类"既有键文案)', () => {
    expect(trashItemView(item({ categoryName: null }), fmt, (k, p) => k).categoryName).toBeNull();
    expect(trashItemView(item({ categoryName: ' ' }), fmt, (k, p) => k).categoryName).toBeNull();
    expect(trashItemView(item({ categoryName: '面试题' }), fmt, (k, p) => k).categoryName).toBe('面试题');
  });

  it('删除时间:原样交给格式化器(格式 yyyy-MM-dd HH:mm 由组件实现)', () => {
    const v = trashItemView(item({ deletedAt: '2026-10-20T10:00:00Z' }), fmt, (k, p) => k);
    expect(v.deletedAt).toBe('2026-10-20T10:00:00Z');
  });

  it('子树提示:DescendantTotal=0 → 不显示;>0 → 显示且 {count} 替换为该数', () => {
    expect(trashItemView(item({ descendantTotal: 0 }), fmt, (k, p) => k).containsItems).toBeNull();
    const v = trashItemView(item({ descendantTotal: 3 }), fmt,
      (k, p) => `${k}:${String(p['count'])}`);
    expect(v.containsItems).toBe('practice.trash.containsItems:3');
  });

  it('录音提示:RecordingCount=0 → 不显示;>0 → 显示且 {count} 替换为该数', () => {
    expect(trashItemView(item({ recordingCount: 0 }), fmt, (k, p) => k).containsRecordings).toBeNull();
    const v = trashItemView(item({ recordingCount: 2 }), fmt,
      (k, p) => `${k}:${String(p['count'])}`);
    expect(v.containsRecordings).toBe('practice.trash.containsRecordings:2');
  });

  it('「全部恢复」按钮:顶层项 ≥2 才显示(1 项时不显示)', () => {
    expect(shouldShowRestoreAll(0)).toBe(false);
    expect(shouldShowRestoreAll(1)).toBe(false);
    expect(shouldShowRestoreAll(2)).toBe(true);
    expect(shouldShowRestoreAll(7)).toBe(true);
  });
});

// ============================================================
// F3 —— 空态 vs 加载失败态(绝不冒充空态)
// ============================================================

describe('F3(空态与错误态:失败绝不渲染成空态)', () => {
  it('空态:仅"加载成功 + 确实为空"才显示', () => {
    expect(shouldShowTrashEmpty('loaded', [])).toBe(true);
    expect(shouldShowTrashEmpty('loaded', [item()])).toBe(false);
  });

  it('加载中:不显示空态', () => {
    expect(shouldShowTrashEmpty('loading', [])).toBe(false);
  });

  it('加载失败:不显示空态,改显示错误态', () => {
    expect(shouldShowTrashEmpty('failed', [])).toBe(false);
    expect(shouldShowTrashError('failed')).toBe(true);
  });

  it('加载中/成功态:不显示错误态', () => {
    expect(shouldShowTrashError('loading')).toBe(false);
    expect(shouldShowTrashError('loaded')).toBe(false);
  });
});

// ============================================================
// F4 —— i18n 十二键三语逐键核对
// ============================================================

/** 规格 §8.5 的十二键与三语期望文案(逐字)。 */
const TRASH_I18N: Record<string, { zh: string; en: string; fr: string }> = {
  'practice.trash.title': { zh: '回收站', en: 'Trash', fr: 'Corbeille' },
  'practice.trash.empty': { zh: '回收站是空的', en: 'Trash is empty', fr: 'La corbeille est vide' },
  'practice.trash.restore': { zh: '恢复', en: 'Restore', fr: 'Restaurer' },
  'practice.trash.restored': { zh: '已恢复', en: 'Restored', fr: 'Restauré' },
  'practice.trash.restoreAll': { zh: '全部恢复', en: 'Restore all', fr: 'Tout restaurer' },
  'practice.trash.restoredAll': { zh: '全部已恢复', en: 'All restored', fr: 'Tout a été restauré' },
  'practice.trash.containsItems': { zh: '含子项 {count} 个', en: 'Contains {count} item(s)', fr: 'Contient {count} élément(s)' },
  'practice.trash.containsRecordings': { zh: '含录音 {count} 条', en: 'Contains {count} recording(s)', fr: 'Contient {count} enregistrement(s)' },
  'practice.trash.deletedAt': { zh: '删除于', en: 'Deleted', fr: 'Supprimé le' },
  'practice.trash.loadFailed': { zh: '回收站加载失败', en: 'Failed to load trash', fr: 'Échec du chargement de la corbeille' },
  'practice.trash.actionFailed': { zh: '操作失败，请重试', en: 'Action failed, please retry', fr: "Échec de l'action, veuillez réessayer" },
  'practice.trash.hintInEmpty': { zh: '回收站里有 {count} 个已删除的素材，可恢复', en: '{count} deleted material(s) in Trash can be restored', fr: '{count} élément(s) supprimé(s) dans la corbeille peuvent être restaurés' }
};

describe('F4(i18n:12 键 × zh/en/fr 逐键核对)', () => {
  const src = readFileSync(join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');

  for (const [key, exp] of Object.entries(TRASH_I18N)) {
    it(`${key} 三语齐全且逐字一致`, () => {
      // 正文里可能含 {count} 占位符 —— 正则要能跨过花括号,不能用 [^}]*。
      const re = new RegExp(`'${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}':\\s*\\{([\\s\\S]*?)\\},`);
      const m = src.match(re);
      expect(m).not.toBeNull();
      const body = m![1];
      expect(body).toContain(`zh: '${exp.zh}'`);
      expect(body).toContain(`en: '${exp.en}'`);
      // fr 里有单引号时用双引号包裹(源码口径)—— 两种引号形态都接受。
      const frOk = body.includes(`fr: '${exp.fr}'`) || body.includes(`fr: "${exp.fr}"`);
      expect(frOk).toBe(true);
    });
  }

  it('en/fr 不得与 zh 雷同占位(每键三语互不相同)', () => {
    for (const [, exp] of Object.entries(TRASH_I18N)) {
      expect(exp.en.length).toBeGreaterThan(0);
      expect(exp.fr.length).toBeGreaterThan(0);
      expect(exp.en).not.toBe(exp.zh);
      expect(exp.fr).not.toBe(exp.zh);
    }
  });

  it('键数恰好 12 个(不多不少)', () => {
    const found = [...src.matchAll(/'(practice\.trash\.[A-Za-z]+)':/g)].map(m => m[1]);
    expect(new Set(found).size).toBe(12);
    expect(new Set(found)).toEqual(new Set(Object.keys(TRASH_I18N)));
  });
});

// ============================================================
// F5 —— 徽标与空态指针(纯逻辑判定 + 模板契约)
// ============================================================

describe('F5(计数徽标 + 树空时的回收站指针)', () => {
  it('徽标:0 / 负数 → 不渲染(null);1..99 → 数字本身;>99 → 99+', () => {
    expect(trashBadgeLabel(0)).toBeNull();
    expect(trashBadgeLabel(-3)).toBeNull();
    expect(trashBadgeLabel(1)).toBe('1');
    expect(trashBadgeLabel(99)).toBe('99');
    expect(trashBadgeLabel(100)).toBe('99+');
    expect(trashBadgeLabel(135)).toBe('99+');
  });

  it('模板契约:树控件有回收站入口按钮 + 徽标 + 空态指针行(源码级断言)', () => {
    const html = readFileSync(join(__dirname, '../../shared/material-tree/material-tree.component.html'), 'utf8');
    // 入口按钮:图标 restore_from_trash + 徽标 + trashOpen 抛出。
    expect(html).toContain('restore_from_trash');
    expect(html).toContain('trash-badge');
    expect(html).toContain('(click)="trashOpen.emit()"');
    // 指针行:在既有 emptyHint **下方**(源码顺序:tree-empty 在前,指针行在后)。
    const emptyAt = html.indexOf('class="tree-empty"');
    const hintAt = html.indexOf('class="tree-trash-hint"');
    expect(emptyAt).toBeGreaterThan(-1);
    expect(hintAt).toBeGreaterThan(emptyAt);
    // 指针行只在 trashCount>0 时渲染(showTrashHint 判定 trashCount > 0)。
    expect(html).toContain('@if (showTrashHint)');
  });

  it('组件契约:trashCount 为 Input、trashOpen 为 Output', () => {
    const ts = readFileSync(join(__dirname, '../../shared/material-tree/material-tree.component.ts'), 'utf8');
    expect(ts).toContain('@Input() trashCount = 0;');
    expect(ts).toContain('@Output() trashOpen = new EventEmitter<void>();');
  });

  it('宿主绑定:Practice 页的 app-material-tree 绑了 [trashCount] 与 (trashOpen)', () => {
    const html = readFileSync(join(__dirname, 'ai-practice.component.html'), 'utf8');
    expect(html).toContain('[trashCount]="trashCount()"');
    expect(html).toContain('(trashOpen)="openTrash()"');
  });
});

// ============================================================
// F6 —— 契约 C3:恢复成功后 treeDirty 与 treeDeletedSinceSave 均为 false
// ============================================================

describe('F6(契约 C3:恢复成功后待保存状态复位)', () => {
  it('源码契约:采纳恢复响应后复位 treeDirty 与 treeDeletedSinceSave', () => {
    const src = readFileSync(join(__dirname, 'practice-material-tree.ts'), 'utf8');
    // adoptRestoredTree 内部三件事:采纳 nodes / treeDirty 复位 / treeDeletedSinceSave 复位。
    const at = src.indexOf('private adoptRestoredTree');
    expect(at).toBeGreaterThan(-1);
    const body = src.slice(at, at + 900);
    expect(body).toContain('PracticeMaterialTree.fromDto');
    expect(body).toContain('this.treeDirty.set(false);');
    expect(body).toContain('this.treeDeletedSinceSave = false;');
  });

  it('契约 C1:PracticeApi 的恢复方法在 service 层即采纳树包(adoptTreePackage)', () => {
    const src = readFileSync(join(__dirname, '../../core/api/practice-api.service.ts'), 'utf8');
    expect(src).toContain('private adoptTreePackage');
    // 两个恢复方法都走 adoptTreePackage —— 只回计数不许出现。
    expect(src).toContain('restoreMaterial(id: string): Observable<MaterialRestoreResult>');
    expect(src).toContain('restoreAllMaterials(): Observable<MaterialRestoreResult>');
  });

  it('恢复不会把 force 打开:删除标记只在整树保存时被读取,恢复路径不置位', () => {
    const src = readFileSync(join(__dirname, 'practice-material-tree.ts'), 'utf8');
    // treeDeletedSinceSave = true 的赋值点只允许出现在删除动作上,不在恢复路径上。
    const restoreAt = src.indexOf('private adoptRestoredTree');
    const body = src.slice(restoreAt, restoreAt + 900);
    expect(body).not.toContain('this.treeDeletedSinceSave = true');
    expect(body).toContain('this.treeDeletedSinceSave = false;');
  });
});
