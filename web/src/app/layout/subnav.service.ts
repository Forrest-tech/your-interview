import { Injectable } from '@angular/core';

export interface SubnavItem {
  key: string;
  labelKey: string;
  icon: string;
  /** 点击行为:同页内滚动/切页签,或跳转路由 */
  action?: () => void;
  /** 是否高亮 */
  active?: () => boolean;
}

/**
 * 页面级子导航注册表。
 *
 * 设计意图(对齐 Forrest 2026-09-15 要求第 2 条):
 * 左边那根竖栏不再放全站模块导航,而是"当前页面自己的功能"。
 * 谁是这个页面的功能,由页面自己声明 —— 所以这里是注册制,
 * 页面 onInit 时 register(key, items),destroy 时 unregister(key)。
 *
 * 同时内建一份"按路由推导"的默认子导航,保证即使页面还没接入,
 * 左侧栏也能显示该模块的功能入口而不是空着。
 */
@Injectable({ providedIn: 'root' })
export class SubnavService {
  /** 页面自己注册的子导航,优先级最高。 */
  private readonly overrides = new Map<string, SubnavItem[]>();

  register(key: string, items: SubnavItem[]): void {
    this.overrides.set(key, items);
  }

  unregister(key: string): void {
    this.overrides.delete(key);
  }

  /** 根据当前 URL 解析出左侧栏要显示什么。 */
  resolve(url: string): SubnavItem[] {
    const path = (url || '').split('?')[0];

    // 1) 页面自己注册的优先
    for (const [key, items] of this.overrides) {
      if (path === key || path.startsWith(key + '/')) return items;
    }

    // 2) 按模块给出默认子导航(锚点式,滚动到对应区块)
    if (path.startsWith('/playbook')) {
      return [
        this.anchor('sub.overview', 'dashboard', 'overview'),
        this.anchor('sub.questions', 'forum', 'questions'),
        this.anchor('sub.assets', 'folder_open', 'assets'),
        this.anchor('sub.edit', 'edit_note', 'edit')
      ];
    }
    if (path.startsWith('/tracker')) {
      return [
        this.anchor('sub.status', 'view_kanban', 'status'),
        this.anchor('sub.overview', 'insights', 'overview')
      ];
    }
    if (path.startsWith('/tech-stack')) {
      return [
        this.anchor('sub.concepts', 'account_tree', 'concepts'),
        this.anchor('sub.dueReview', 'schedule', 'due')
      ];
    }
    // ⚠️ /practice **故意不返回子导航**(Forrest 2026-09-15 明确要求):
    //    左侧竖栏里的「材料 / 评分 / 记录」必须彻底删除 ——
    //    这三项在 /practice 页里没有任何对应锚点(页面内既无 #assets/#scores/#history
    //    容器,评分与记录也已是页面内嵌区块,不需要二级导航),
    //    留着只会让页面左边多出一根无用竖栏、还挤掉正文宽度。
    //    该页自己的左栏就是「面试素材」树,由 ai-practice 组件自绘。
    //    因此这里 return [](空数组),shell 的左栏 @if (subnav().length > 0) 即不渲染。
    if (path.startsWith('/practice')) {
      return [];
    }
    if (path.startsWith('/mock')) {
      return [
        this.anchor('sub.sessions', 'list_alt', 'sessions'),
        this.anchor('sub.overview', 'graphic_eq', 'overview')
      ];
    }
    if (path.startsWith('/admin')) {
      return [
        this.anchor('sub.users', 'people_outline', 'users'),
        this.anchor('sub.roles', 'badge', 'roles'),
        this.anchor('sub.audit', 'history', 'audit')
      ];
    }
    if (path.startsWith('/analytics')) {
      return [
        this.anchor('sub.overview', 'radar', 'overview'),
        this.anchor('sub.concepts', 'bar_chart', 'distribution')
      ];
    }

    // 总览 / 其它:没有页面级子功能,左侧栏不渲染
    return [];
  }

  private anchor(labelKey: string, icon: string, id: string): SubnavItem {
    return {
      key: labelKey + ':' + id,
      labelKey,
      icon,
      active: () => this.lastAnchor === id,
      action: () => {
        this.lastAnchor = id;
        if (typeof document === 'undefined') return;
        const el = document.getElementById(id);
        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    };
  }

  private lastAnchor = '';
}
