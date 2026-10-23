/**
 * 票105 spec —— comm-form.helpers:沟通表单模式判定/保存分流纯逻辑。
 *
 * 对照源:application-detail-dialog 沟通区状态机——commEditingId(编辑 id,
 * 新增/空态为 null)+ commAdding(票105 新增的独立新增态标记,同 questionAdding
 * 先例)。改前缺陷:startAddComm 置 commEditingId=null 且无新增标记,模板
 * `@if (commEditingId())` 不渲染,新增入口不可达。
 * boundary 类别逐条标注(总纲第七节第 3 条)。
 */
import { describe, expect, it } from '@jest/globals';
import { commFormMode, commSaveChannel } from './comm-form.helpers';

describe('commFormMode(票105)', () => {
  // [功能] 新增态:无编辑 id + 新增标记 → add(改前此态被判为不渲染)
  it('null id + adding -> add', () => {
    expect(commFormMode(null, true)).toBe('add');
  });
  // [功能] 编辑态:有编辑 id → edit
  it('id + not adding -> edit', () => {
    expect(commFormMode('c-1', false)).toBe('edit');
  });
  // [功能] 关闭态:两者皆无 → closed
  it('null id + not adding -> closed', () => {
    expect(commFormMode(null, false)).toBe('closed');
  });
  // [boundary-冲突] 编辑 id 与新增标记同时存在:startEditComm/startAddComm 互清对方,
  // 万一同存以编辑为准(与保存分流 update 优先一致,不误走 create 建重复记录)
  it('id + adding -> edit (edit wins)', () => {
    expect(commFormMode('c-1', true)).toBe('edit');
  });
  // [boundary-空] 空字符串 id 视为无 id(防哨兵空串误入 edit)
  it('empty-string id + adding -> add; + not adding -> closed', () => {
    expect(commFormMode('', true)).toBe('add');
    expect(commFormMode('', false)).toBe('closed');
  });
});

describe('commSaveChannel(票105)', () => {
  // [功能] 新增态保存走既有 create 通道
  it('null id -> create', () => {
    expect(commSaveChannel(null)).toBe('create');
  });
  // [功能] 编辑态保存走 update(行为守恒)
  it('id -> update', () => {
    expect(commSaveChannel('c-1')).toBe('update');
  });
  // [boundary-空] 空字符串 id 不走 update(避免 PUT 到空 id)
  it('empty-string id -> create', () => {
    expect(commSaveChannel('')).toBe('create');
  });
  // [boundary-一致性] 模式与分流对齐:add↔create、edit↔update
  it('mode/channel alignment', () => {
    expect(commSaveChannel(null)).toBe('create');
    expect(commFormMode(null, true)).toBe('add');
    expect(commSaveChannel('x')).toBe('update');
    expect(commFormMode('x', false)).toBe('edit');
  });
});
