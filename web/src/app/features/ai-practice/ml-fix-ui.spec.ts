/**
 * ML-FIX-UI 前端验收(10 项)。
 * 基建口径(同票124/票130 spec):Jest = node 环境 + @angular/* 替身,无 TestBed / DOM 渲染。
 * 行为断言用真实 PracticeMaterialTree + 记录型宿主替身;
 * 模板/路由/令牌层用源码契约断言(读文件断言关键绑定与取值存在);
 * 宽度类判据(jsdom 无布局)归活体复验,本文件只断言状态与契约。
 */
import { readFileSync, readdirSync, statSync } from 'fs';
import { join, dirname } from 'path';
import { describe, expect, it } from '@jest/globals';
import { throwError } from 'rxjs';
import { PracticeMaterialTree } from './practice-material-tree';

const HERE = __dirname;
const read = (rel: string): string => readFileSync(join(HERE, rel), 'utf8');

function walkScss(dir: string, out: string[] = []): string[] {
  for (const e of readdirSync(dir)) {
    const p = join(dir, e);
    if (statSync(p).isDirectory()) walkScss(p, out);
    else if (p.endsWith('.scss')) out.push(p);
  }
  return out;
}

/** WCAG 相对亮度/对比度(与 BENCHMARK-AUDIT §4 同口径)。 */
function luminance(hex: string): number {
  const f = (c: number) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const r = f(parseInt(hex.slice(1, 3), 16));
  const g = f(parseInt(hex.slice(3, 5), 16));
  const b = f(parseInt(hex.slice(5, 7), 16));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
function ratio(a: string, b: string): number {
  const la = luminance(a);
  const lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

function makeTree(canEdit: boolean, saveImpl: (payload: unknown, force: unknown) => unknown) {
  const toasts: string[] = [];
  const host = {
    canEditTree: () => canEdit,
    toast: (s: string) => { toasts.push(s); },
    t: (k: string) => k,
    practiceApi: { saveMaterials: saveImpl, getMaterials: () => throwError(() => new Error('unused')) },
  };
  const tree = new PracticeMaterialTree(host as never);
  return { tree, toasts };
}

describe('W-UI-1:保存失败必须可见', () => {
  it('树模板渲染 saveError 横幅(role=alert + 重试走 saveRequest)', () => {
    const html = read('../../shared/material-tree/material-tree.component.html');
    expect(html).toContain('tree-save-error');
    expect(html).toContain('role="alert"');
    expect(html).toContain('saveRequest.emit()');
  });

  it('宿主模板把 treeError 直通为 [saveError]', () => {
    expect(read('./ai-practice.component.html')).toContain('[saveError]="treeSaveError()"');
  });

  it('显式保存 403:treeError 置值 + toast 明示 + 脏标识保持', () => {
    const { tree, toasts } = makeTree(true, () => throwError(() => ({ status: 403, message: 'Forbidden' })));
    tree.doSaveTree();
    expect(tree.treeError()).toBeTruthy();
    expect(toasts.length).toBe(1);
    expect(tree.treeDirty()).toBe(true);
  });

  it('静默自动保存 403:只亮横幅、不弹 toast(不刷屏)', () => {
    const { tree, toasts } = makeTree(true, () => throwError(() => ({ status: 403, message: 'Forbidden' })));
    tree.persistQuiet();
    expect(tree.treeError()).toBeTruthy();
    expect(tree.treeDirty()).toBe(true);
    expect(toasts.length).toBe(0);
  });
});

describe('W-UI-2:无 mock.manage 不给创作入口', () => {
  it('宿主模板传 [canEdit] 且树模板禁用新建/编辑并显示只读说明', () => {
    expect(read('./ai-practice.component.html')).toContain('[canEdit]="canEditTree()"');
    const html = read('../../shared/material-tree/material-tree.component.html');
    expect(html).toContain('[disabled]="!canEdit"');
    expect(html).toContain('tree-readonly');
    expect(html).toContain("t('practice.treeReadOnly')");
  });

  it('practice.treeReadOnly 三语键齐全非空', () => {
    const src = read('../../core/i18n/i18n.service.ts');
    // 法语值因含撇号写成双引号,故 fr 组按双引号匹配。
    const m = src.match(/'practice\.treeReadOnly': \{ zh: '(.+?)', en: '(.+?)', fr: "(.+?)" \}/);
    expect(m).not.toBeNull();
    expect(m!.slice(1).every((s) => s.length > 0)).toBe(true);
  });

  it('startTreeEdit 代码层守门:无权限不进编辑态,有权限可进', () => {
    const denied = makeTree(false, () => throwError(() => new Error('unused')));
    denied.tree.startTreeEdit();
    expect(denied.tree.treeEditing()).toBe(false);
    const allowed = makeTree(true, () => throwError(() => new Error('unused')));
    allowed.tree.startTreeEdit();
    expect(allowed.tree.treeEditing()).toBe(true);
  });
});

describe('W-UI-3/4:Dashboard 窄屏与 Offer 翻译', () => {
  it('600px 断点下漏斗标签换行(不再 nowrap 挤成一串)', () => {
    const scss = read('../dashboard/dashboard.component.scss');
    expect(scss).toContain('@media (max-width: 600px)');
    expect(scss).toMatch(/600px\) \{\s*\.band/);
    expect(scss).toContain('white-space: normal');
  });

  it("status.Offer 与 dash.offer 的 zh 均为'录用意向'", () => {
    const src = read('../../core/i18n/i18n.service.ts');
    expect(src).toContain("'status.Offer': { zh: '录用意向'");
    expect(src).toContain("'dash.offer': { zh: '录用意向'");
  });
});

describe('W-UI-5:注册入口', () => {
  it('路由含 /register 且登录页链向它', () => {
    const routes = read('../../app.routes.ts');
    expect(routes).toContain("path: 'register'");
    expect(routes).toContain('register.component');
    expect(read('../auth/login.component.html')).toContain('routerLink="/register"');
  });

  it('注册页调 auth.register 且含三字段 + 人机验证门槛', () => {
    const ts = read('../auth/register.component.ts');
    expect(ts).toContain('auth.register(');
    const html = read('../auth/register.component.html');
    expect(html).toContain('auth.fieldDisplayName');
    expect(html).toContain('app-human-check');
    expect(html).toContain('routerLink="/login"');
  });
});

describe('W-UI-9:Suggest 按界面语言', () => {
  it('mockApi.suggest 传 lang,落地页传当前语言', () => {
    expect(read('../../core/api/mock-api.service.ts')).toContain('lang');
    expect(read('../mock/mock.component.ts')).toContain('this.i18n.lang()');
  });
});

describe('W-UI-10:日历横幅按轮去重', () => {
  it('日历组件用 dueBanners 且确认走整轮 ack', () => {
    const ts = read('../playbook/playbook-calendar.component.ts');
    expect(ts).toContain('dueBanners(');
    expect(ts).toContain('dueReminderIdsForRound(');
  });
});

describe('G-DES:正文对比度 AA', () => {
  it('--attention 正文 on 白 / on tint / 白字 on 它均 ≥4.5:1', () => {
    const styles = read('../../../styles.scss');
    const m = styles.match(/--attention: (#[0-9a-f]{6});/);
    expect(m).not.toBeNull();
    const att = m![1];
    expect(ratio(att, '#ffffff')).toBeGreaterThanOrEqual(4.5);
    expect(ratio(att, '#fffbeb')).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#ffffff', att)).toBeGreaterThanOrEqual(4.5);
  });

  it('.empty 用 --ink-2(≥4.5:1),app 内无文本色/实底 #b06a00 硬编码残留', () => {
    const styles = read('../../../styles.scss');
    expect(styles).toContain('color: var(--ink-2)');
    const scssFiles = walkScss(join(HERE, '..', '..', '..', 'app'));
    for (const f of scssFiles) {
      const src = readFileSync(f, 'utf8');
      expect(src).not.toContain('color: #b06a00');
      expect(src).not.toContain('background: #b06a00');
    }
  });
});

describe('L-1:启动指示', () => {
  it('index.html 含 Angular 接管前可见的静态启动指示', () => {
    const html = read('../../../index.html');
    expect(html).toContain('boot-splash');
    expect(html).toContain('role="status"');
  });
});
