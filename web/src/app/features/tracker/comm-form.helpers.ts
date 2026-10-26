/**
 * 票105:沟通表单模式判定纯逻辑。
 *
 * 存量缺陷:startAddComm 把 commEditingId 置 null,而表单仅在 commEditingId
 * 非空时渲染,新增入口点了没反应。修法是加独立的新增态标记 commAdding,
 * 模式判定收敛到这里:编辑 id 非空恒为 edit(保存走 update);仅新增标记
 * 为 add(保存走 create);两者皆无为 closed(不渲染表单)。
 */
export type CommFormMode = 'closed' | 'add' | 'edit';

export function commFormMode(editingId: string | null, adding: boolean): CommFormMode {
  if (editingId) return 'edit';
  return adding ? 'add' : 'closed';
}

/** 保存分流:有编辑 id 走 update,否则一律 create(含新增态)。 */
export function commSaveChannel(editingId: string | null): 'create' | 'update' {
  return editingId ? 'update' : 'create';
}
