import { Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { AutoFocusDirective } from './auto-focus.directive';

/**
 * 素材节点。folder=true 可嵌套 children;文件承载正文。
 */
export interface MaterialNode {
  id: string;
  name: string;
  folder: boolean;
  /** ★ 第三十八轮:同层排序号。数组下标即权威顺序,
   *  拖拽后由 ai-practice 的 toPayload 按下标重写并落库。
   *  这里让它可选,是为了兼容旧的就地构造点。 */
  sortOrder?: number;
  children?: MaterialNode[];
  /** 文件正文(仅 folder=false 时有意义) */
  content?: string;
  /** 展开状态(仅文件夹) */
  expanded?: boolean;
  /** 是否已完成(仅文件;任务书第四节:已完成显示绿勾) */
  completed?: boolean;
}

/**
 * 可复用的「分层文件夹 / 文件树」控件。
 *
 * 需求来源(Forrest 2026-09-15):AI 面试练习页左侧要能自定义文件夹与文件,
 * 文件夹分层(如 自我介绍 / 学历、工签状态、公司介绍、工作职责),用户自建。
 *
 * 设计取舍:
 *  - 完全受控组件:数据由父级持有,本组件只渲染与交互,通过 @Output 抛事件。
 *    换任何页面复用都不用改组件内部。
 *  - 不用 mat-tree:其展开/选中状态与我们的重命名、拖拽排序叠加会互相打架,
 *    自写层级渲染更可控(与口语跟读系统侧栏同一套思路)。
 */
@Component({
  selector: 'app-material-tree',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatIconModule, MatButtonModule,
    MatMenuModule, MatTooltipModule, AutoFocusDirective
  ],
  templateUrl: './material-tree.component.html',
  styleUrl: './material-tree.component.scss'
})
export class MaterialTreeComponent {
  /** 树数据(受控)。 */
  @Input() nodes: MaterialNode[] = [];

  /** 当前选中的文件 id。 */
  @Input() selectedId: string | null = null;

  /** 是否可编辑(只读展示时传 false)。 */
  @Input() editable = true;

  /** 面板标题。 */
  @Input() title = '素材库';

  @Input() emptyHint = '还没有素材。用上方 + 号新建文件夹与文件。';

  @Output() nodeSelect = new EventEmitter<MaterialNode>();
  /**
   * 文件夹展开/收起 —— ★ 2026-09-19 新增。
   * 与 nodeChange 分开:展开是纯 UI 状态,不该把树标脏、也不该触发落盘。
   */
  @Output() folderToggle = new EventEmitter<MaterialNode>();
  @Output() nodeChange = new EventEmitter<MaterialNode[]>();
  @Output() nodeDelete = new EventEmitter<MaterialNode>();

  /** 正在重命名的节点 id。 */
  readonly renamingId = signal<string | null>(null);

  /** 拖拽中的节点 id。 */
  /**
   * 正在拖拽的节点 id。
   *
   * 必须是 public:模板里用 [class.dragging]="draggingId === n.id" 读它,
   * Anguar 模板只能访问组件的 public 成员 —— 声明成 private 会直接编译失败
   * (NG1: Property 'draggingId' is private and only accessible within class)。
   * 注意它是普通字段不是 signal,靠 change detection 刷新;
   * 拖拽开始/结束都会触发事件回调,所以能正常重绘。
   */
  draggingId: string | null = null;

  /** 当前拖拽悬停的落点,用于可视化插到哪。 */
  readonly dropTargetId = signal<string | null>(null);
  readonly dropMode = signal<'into' | 'before' | 'after' | 'root' | null>(null);

  /** 「移动到…」弹层当前展开的节点 id。 */
  readonly moveMenuId = signal<string | null>(null);

  /** 当前选中节点所在层级的选择器数据。 */
  readonly folderOptions = signal<{ id: string; name: string; depth: number }[]>([]);

  // ---------- NeetCode 风格展示(任务书第四节) ----------
  /**
   * 一级分类的单色线性图标。按名称关键词匹配,匹配不上给通用 "notes"。
   * 目的是去掉黄文件夹图标,换成极简现代风。
   */
  nodeIcon(node: MaterialNode): string {
    const n = (node.name || '').toLowerCase();
    if (/自我介绍|self|intro|pitch/.test(n)) return 'record_voice_over';
    if (/公司|company|work|job|经验|experience/.test(n)) return 'business_center';
    if (/技术|tech|skill|栈|stack|coding|code/.test(n)) return 'code';
    if (/行为|behavior|bq|软技能|soft/.test(n)) return 'forum';
    if (/项目|project/.test(n)) return 'layers';
    if (/学历|教育|education|学历|degree/.test(n)) return 'school';
    if (/签证|工签|status|visa|perm/.test(n)) return 'badge';
    return 'notes';
  }

  /** 该素材是否已完成(驱动绿勾 / 灰圆点)。目前读节点上的 completed 标记。 */
  isDone(node: MaterialNode): boolean {
    return node.completed === true;
  }

  // ---------- 选中 ----------
  select(node: MaterialNode): void {
    if (node.folder) {
      this.toggle(node);
      return;
    }
    this.nodeSelect.emit(node);
  }

  /**
   * 展开/收起文件夹。
   *
   * ★ 2026-09-19 修复(Forrest 报"点任何树节点 Save Changes 都被激活"):
   *   展开/收起是**纯 UI 状态**,不是内容变更。
   *   旧实现在这里 emit nodeChange → 父组件无条件置脏(并触发一次落盘),
   *   于是用户每点一下文件夹,侧栏就亮起"保存修改"—— 不符合 UX。
   *   现在改为只 emit 专门的 folderToggle 事件,父组件只更新本地展开状态,
   *   **不置脏、不落盘**。expanded 仍会随整树保存一起写库,
   *   但"仅仅展开"本身不再把树标成脏的。
   */
  toggle(node: MaterialNode): void {
    node.expanded = !node.expanded;
    this.folderToggle.emit(node);
  }

  // ---------- 新建 ----------
  addFolder(parent: MaterialNode | null): void {
    const node: MaterialNode = {
      id: this.newId(),
      name: '新建文件夹',
      folder: true,
      expanded: true,
      children: []
    };
    this.attach(node, parent);
    this.startRename(node);
  }

  addFile(parent: MaterialNode | null): void {
    const node: MaterialNode = {
      id: this.newId(),
      name: '新建素材',
      folder: false,
      content: ''
    };
    this.attach(node, parent);
    this.startRename(node);
  }

  private attach(node: MaterialNode, parent: MaterialNode | null): void {
    if (parent) {
      parent.children = parent.children ?? [];
      parent.children.push(node);
      parent.expanded = true;
    } else {
      this.nodes.push(node);
    }
    this.nodeChange.emit(this.nodes);
  }

  // ---------- 重命名 ----------
  startRename(node: MaterialNode): void {
    if (!this.editable) return;
    this.renamingId.set(node.id);
  }

  commitRename(node: MaterialNode, value: string): void {
    const v = (value ?? '').trim();
    // ★ 第三十六轮:改名必须真的变了才提交。
    //   为什么重要:输入框有 (blur)=commitRename,而回车/点空白都会先触发 blur。
    //   若名字没变也一律 emit,就会对树发一次无意义的变更 →
    //   触发一次多余的全树 PUT。加个相等判断,既省一次请求,也避免
    //   在"只是点了一下又点回来"的情况下把 treeDirty 误置为 true。
    const changed = !!v && v !== node.name;
    if (v) node.name = v;
    this.renamingId.set(null);
    if (changed) this.nodeChange.emit(this.nodes);
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  // ---------- 删除 ----------
  // ★ 2026-09-20(Forrest):删除必须先经确认弹窗。树组件不再自己动手删 ——
  //   只把"用户想删谁"抛给父级,由父级弹确认框、确认后才真正从树上摘除。
  //   (旧实现直接删了再通知,用户手一抖节点就没了。)

  // ---------- 拖拽:移入文件夹 / 同级排序 / 移出到根层 ----------
  onDragStart(node: MaterialNode, ev: DragEvent): void {
    if (!this.editable) return;
    this.draggingId = node.id;
    this.moveMenuId.set(null);
    ev.dataTransfer?.setData('text/plain', node.id);
    if (ev.dataTransfer) ev.dataTransfer.effectAllowed = 'move';
  }

  onDragEnd(): void {
    this.resetDrag();
  }

  onDragOver(ev: DragEvent): void {
    if (!this.editable) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';
  }

  /**
   * 悬停在某一行的上/中/下三区之一,决定落点语义。
   *  - 文件夹行:上 1/4 = 插到它前面,下 1/4 = 插到它后面,中间 = 移入
   *  - 文件行:上 1/2 = 前,下 1/2 = 后(文件不能作为容器)
   * 这样"插到某个文件夹之前/之后"这种以前根本没有的操作现在有了。
   */
  onRowDragOver(node: MaterialNode, ev: DragEvent): void {
    if (!this.editable || !this.draggingId) return;
    ev.preventDefault();
    ev.stopPropagation();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';

    const el = ev.currentTarget as HTMLElement;
    const r = el.getBoundingClientRect();
    const ratio = r.height > 0 ? (ev.clientY - r.top) / r.height : 0.5;

    let mode: 'into' | 'before' | 'after';
    if (node.folder) {
      // ★ 2026-09-20(Forrest 报"拖进文件夹容易发生偏差"):
      //   文件夹的"移入"区从中间 50%(0.25~0.75)收窄到 40%(0.3~0.7) ——
      //   想插到它前面/后面时更容易命中,不会动不动变成"移入"。
      if (ratio < 0.3) mode = 'before';
      else if (ratio > 0.7) mode = 'after';
      else mode = 'into';
    } else {
      mode = ratio < 0.5 ? 'before' : 'after';
    }

    this.dropTargetId.set(node.id);
    this.dropMode.set(mode);
  }

  onRowDragLeave(node: MaterialNode): void {
    if (this.dropTargetId() === node.id) {
      this.dropTargetId.set(null);
      this.dropMode.set(null);
    }
  }

  /** 统一落点处理:按悬停区语义执行 before / after / into。 */
  onRowDrop(node: MaterialNode, ev: DragEvent): void {
    if (!this.editable) return;
    ev.preventDefault();
    ev.stopPropagation();
    const mode = this.dropMode();
    const id = this.draggingId;
    this.resetDrag();
    if (!id) return;

    if (mode === 'into') this.moveInto(id, node);
    else if (mode === 'before') this.moveBeside(id, node, 'before');
    else if (mode === 'after') this.moveBeside(id, node, 'after');
    else this.moveBeside(id, node, 'after');
  }

  /** 拖到空白区 = 移到最外层(这是以前"出不来"的解药)。 */
  onRootDragOver(ev: DragEvent): void {
    if (!this.editable || !this.draggingId) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';
    this.dropTargetId.set(null);
    this.dropMode.set('root');
  }

  onRootDrop(ev: DragEvent): void {
    if (!this.editable) return;
    ev.preventDefault();
    const id = this.draggingId;
    this.resetDrag();
    if (!id) return;
    const src = this.locate(id, this.nodes, null);
    if (!src) return;
    if (!src.parent) return; // 已在根层
    const from = src.parent;
    from.splice(from.findIndex((n) => n.id === src.node.id), 1);
    this.nodes.push(src.node);
    this.nodeChange.emit(this.nodes);
  }

  /**
   * ★ 2026-09-20(Forrest 报"拖进文件夹后节点变灰"):
   * 灰色的来源 —— 行上的 `[class.dragging]`(opacity 0.45)卡死不掉。
   * 真根因:drop 把节点从原列表摘除后,Angular 重渲染会**先拆掉正在拖的
   * 那个 DOM 元素**,浏览器就不会再对它派发 dragend → draggingId 永远
   * 不被清掉,新位置上的同一节点一直顶着 .dragging 类。
   * 修法:所有落点处理完立刻统一清状态,不再依赖 dragend。
   */
  private resetDrag(): void {
    this.draggingId = null;
    this.dropTargetId.set(null);
    this.dropMode.set(null);
  }

  /** 移入某文件夹。 */
  private moveInto(dragId: string, target: MaterialNode): void {
    if (!target.folder) return;
    const src = this.locate(dragId, this.nodes, null);
    if (!src || src.node.id === target.id) return;
    if (this.isDescendant(src.node, target)) return;
    const from = src.parent ?? this.nodes;
    from.splice(from.findIndex((n) => n.id === src.node.id), 1);
    target.children = target.children ?? [];
    target.children.push(src.node);
    target.expanded = true;
    this.nodeChange.emit(this.nodes);
  }

  /** 移到目标的同级前/后。 */
  private moveBeside(dragId: string, target: MaterialNode, side: 'before' | 'after'): void {
    const src = this.locate(dragId, this.nodes, null);
    if (!src || src.node.id === target.id) return;
    if (this.isDescendant(src.node, target)) return;
    const tgt = this.locate(target.id, this.nodes, null);
    if (!tgt) return;

    const from = src.parent ?? this.nodes;
    from.splice(from.findIndex((n) => n.id === src.node.id), 1);
    const to = tgt.parent ?? this.nodes;
    const at = to.findIndex((n) => n.id === target.id);
    to.splice(side === 'before' ? at : at + 1, 0, src.node);
    this.nodeChange.emit(this.nodes);
  }

  // ---------- 「移动到…」兜底(不依赖拖拽) ----------
  openMoveMenu(node: MaterialNode): void {
    this.moveMenuId.set(this.moveMenuId() === node.id ? null : node.id);
    this.folderOptions.set(this.collectFolders(node));
  }

  /** 列出可作为目标的文件夹(排除自身与自身子孙)。 */
  private collectFolders(node: MaterialNode): { id: string; name: string; depth: number }[] {
    const out: { id: string; name: string; depth: number }[] = [];
    const walk = (list: MaterialNode[], depth: number) => {
      for (const n of list) {
        if (!n.folder) continue;
        if (n.id !== node.id && !this.isDescendant(node, n)) {
          out.push({ id: n.id, name: n.name, depth });
        }
        if (n.children?.length) walk(n.children, depth + 1);
      }
    };
    walk(this.nodes, 0);
    return out;
  }

  /** 把节点移到指定文件夹;folderId 为 null 表示移到最外层。 */
  moveTo(node: MaterialNode, folderId: string | null): void {
    if (!this.editable) return;
    this.moveMenuId.set(null);
    if (folderId === null) {
      const src = this.locate(node.id, this.nodes, null);
      if (!src || !src.parent) return;
      const from = src.parent;
      from.splice(from.findIndex((n) => n.id === node.id), 1);
      this.nodes.push(src.node);
      this.nodeChange.emit(this.nodes);
      return;
    }
    const target = this.locate(folderId, this.nodes, null)?.node;
    if (!target) return;
    this.moveInto(node.id, target);
  }

  /** 当前节点是否已在最外层。 */
  isRoot(node: MaterialNode): boolean {
    const src = this.locate(node.id, this.nodes, null);
    return !!src && src.parent === null;
  }

  // ---------- 工具 ----------
  private newId(): string {
    return 'n_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 7);
  }

  private locate(
    id: string | null,
    list: MaterialNode[],
    parent: MaterialNode[] | null
  ): { node: MaterialNode; parent: MaterialNode[] | null } | null {
    if (!id) return null;
    for (const n of list) {
      if (n.id === id) return { node: n, parent };
      if (n.children?.length) {
        const hit = this.locate(id, n.children, n.children);
        if (hit) return hit;
      }
    }
    return null;
  }

  private isDescendant(ancestor: MaterialNode, maybeChild: MaterialNode): boolean {
    if (!ancestor.children?.length) return false;
    for (const c of ancestor.children) {
      if (c.id === maybeChild.id) return true;
      if (this.isDescendant(c, maybeChild)) return true;
    }
    return false;
  }
}
