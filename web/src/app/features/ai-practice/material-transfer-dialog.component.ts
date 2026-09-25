import {
  Component, computed, ElementRef, inject, signal, ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatTabsModule } from '@angular/material/tabs';
import { MatRadioModule } from '@angular/material/radio';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { firstValueFrom } from 'rxjs';

import { PracticeApi, PracticeCategoryDto } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { MaterialNode } from '../../shared/material-tree/material-tree.component';
import {
  IMPORT_ACCEPT, SAMPLE_FILE_NAME, TransferCategory, TransferFormat, TransferNode,
  buildExportPayload, buildSampleFile, countNodes, fileExtension, ImportWarning, parseImport,
  sanitizeName, serializeTransfer, transferMime
} from './material-transfer';

interface DialogData {
  /** 整棵素材树(已在内存中的全量数据)。 */
  nodes: MaterialNode[];
  categories: PracticeCategoryDto[];
  /** 打开弹窗时树上的类别过滤值,作为导出来源的默认值。 */
  filter: string;
}

/** 保存位置偏好落盘 key(第七轮:每次询问 / 浏览器下载文件夹)。 */
const SAVE_MODE_KEY = 'yi.exportSaveMode';

/** 收集一个节点及其全部后代的 id(勾选/取消要沿子树级联)。 */
function collectIds(n: MaterialNode): string[] {
  const out: string[] = [n.id];
  for (const c of n.children ?? []) out.push(...collectIds(c));
  return out;
}

function readSaveMode(): 'ask' | 'downloads' {
  try {
    const v = localStorage.getItem(SAVE_MODE_KEY);
    return v === 'downloads' ? 'downloads' : 'ask';
  } catch {
    return 'ask';
  }
}

/** 关闭回传:需要合并进整树的节点。 */
export interface TransferResult {
  importedNodes: MaterialNode[];
}

/**
 * 素材「导入 / 导出」弹窗(★ 2026-09-25 第四轮 Forrest)。
 *
 * UX 参考(成熟产品):
 *  · Notion「Settings → Import / Export」:导入导出同处一处、分两个标签页,
 *    用户不用记功能的藏身之处;
 *  · 导出侧提供**范围勾选**(勾选文件夹)而非整库导出 —— 对齐 Notion Export
 *    的 "Include subpages" 思路,但粒度更细:精确到子树;
 *  · 导入侧强制**先预览再落库**(显示层级统计与条目预览),
 *    不做"选完文件直接写库"的隐式行为 —— 与 GitHub Import / Notion Import 一致;
 *  · 格式描述里明确写出"可再导入"(JSON/XML),让用户知道哪种格式适合备份迁移。
 */
@Component({
  selector: 'app-material-transfer-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatTooltipModule, MatTabsModule, MatRadioModule, MatCheckboxModule, MatSnackBarModule
  ],
  templateUrl: './material-transfer-dialog.component.html',
  styleUrl: './material-transfer-dialog.component.scss'
})
export class MaterialTransferDialogComponent {
  readonly dialogRef = inject<MatDialogRef<MaterialTransferDialogComponent, TransferResult>>(MatDialogRef);
  readonly data = inject<DialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(PracticeApi);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  // ---------- 本地数据 ----------
  private readonly roots: MaterialNode[] = [...(this.data.nodes ?? [])];
  readonly categories: PracticeCategoryDto[] = [...(this.data.categories ?? [])];

  readonly tab = signal<'export' | 'import'>('export');

  /** 文件选择框的 accept(与 material-transfer 里支持的格式一致)。 */
  readonly accept = IMPORT_ACCEPT;

  // ---------- 导出 ----------
  readonly exportSource = signal<string>(this.data.filter ?? 'all');
  readonly format = signal<TransferFormat>('markdown');
  readonly selectedIds = signal<Set<string>>(new Set());
  readonly expandedIds = signal<Set<string>>(new Set());
  readonly exporting = signal(false);

  readonly formats: { key: TransferFormat; labelKey: string }[] = [
    { key: 'markdown', labelKey: 'transfer.fmt.markdown' },
    { key: 'txt', labelKey: 'transfer.fmt.txt' },
    { key: 'pdf', labelKey: 'transfer.fmt.pdf' },
    { key: 'json', labelKey: 'transfer.fmt.json' },
    { key: 'xml', labelKey: 'transfer.fmt.xml' }
  ];

  /** 当前导出来源下的根节点(与树上过滤口径一致)。 */
  readonly exportRoots = computed<MaterialNode[]>(() => {
    const f = this.exportSource();
    if (f === 'all') return this.roots;
    if (f === 'uncategorized') return this.roots.filter((n) => !n.categoryId);
    return this.roots.filter((n) => n.categoryId === f);
  });

  /** 导出来源的名称(写进文件标题/文件名)。 */
  private readonly sourceName = computed<string>(() => {
    const f = this.exportSource();
    if (f === 'all') return this.t('practice.categoryAll');
    if (f === 'uncategorized') return this.t('practice.categoryUncategorized');
    return this.categories.find((c) => c.id === f)?.name ?? this.t('practice.categoryAll');
  });

  /**
   * ★ 第五轮(Forrest):文件名可编辑 ——
   *  · 输入框里只写**基础名**,扩展名由所选格式自动补(后缀以灰色块固定展示);
   *    换格式时扩展名自动跟着变,永远不会出现 ".md 后缀配 JSON 内容"的错位;
   *  · 用户没改过(或清空)时跟随默认「来源-日期」,改过就完全尊重用户输入;
   *  · 保存前过一遍 sanitizeName,非法字符不落盘。
   *  (Chrome「另存为」/ Notion Export 的同款拆分方式。)
   */
  readonly defaultBase = computed<string>(() =>
    sanitizeName(`${this.sourceName()}-${new Date().toISOString().slice(0, 10)}`));
  readonly customName = signal('');
  readonly fileNameBase = computed<string>(() => {
    const c = this.customName().trim();
    return c ? sanitizeName(c) : this.defaultBase();
  });

  readonly exportFileName = computed<string>(
    () => `${this.fileNameBase()}.${fileExtension(this.format())}`);

  onNameInput(value: string): void {
    this.customName.set(value);
  }

  /** 当前格式的扩展名(模板里展示后缀块用)。 */
  get ext(): string {
    return fileExtension(this.format());
  }

  /**
   * ★ 第七轮(Forrest):保存位置**在界面上可见、可选** ——
   *  · 「每次询问」= 点导出弹系统「另存为」,用户自己挑文件夹(默认,Chrome/Edge);
   *  · 「浏览器下载文件夹」= 跳过询问直接下载,不弹窗;
   *  · 浏览器不支持 File System Access API(Firefox/Safari)时如实说明,
   *    而不是让用户以为功能丢了。选择持久化,下次打开弹窗还记得。
   */
  readonly saveLocationSupported =
    typeof (window as unknown as { showSaveFilePicker?: unknown }).showSaveFilePicker === 'function';
  readonly saveMode = signal<'ask' | 'downloads'>(readSaveMode());

  setSaveMode(mode: 'ask' | 'downloads'): void {
    this.saveMode.set(mode);
    try {
      localStorage.setItem(SAVE_MODE_KEY, mode);
    } catch { /* 隐私模式:只影响本次会话 */ }
  }

  /** 已勾选的节点数量(含子项 —— 勾了什么导出什么,计数也要一致)。 */
  readonly selectedCount = computed<number>(() => {
    const set = this.selectedIds();
    let count = 0;
    const walk = (list: MaterialNode[]): void => {
      for (const n of list) {
        if (set.has(n.id)) count++;
        if (n.folder) walk(n.children ?? []);
      }
    };
    walk(this.exportRoots());
    return count;
  });

  constructor() {
    // 默认全选根节点 —— 大部分时候用户就是想导出整个类别
    this.selectAll(true);
    // 展开状态沿用主树:主树里展开的文件夹在这里也展开,
    // 否则子项勾选框看不见,用户会以为"子项没被选中/不会导出"(第八轮实测反馈)。
    const expanded = new Set<string>();
    const walk = (list: MaterialNode[]): void => {
      for (const n of list) {
        if (n.folder) {
          if (n.expanded) expanded.add(n.id);
          walk(n.children ?? []);
        }
      }
    };
    walk(this.roots);
    this.expandedIds.set(expanded);
  }

  // ---------- 复选框树 ----------
  isSelected(id: string): boolean { return this.selectedIds().has(id); }
  isExpanded(id: string): boolean { return this.expandedIds().has(id); }

  toggleExpanded(node: MaterialNode): void {
    if (!node.folder) return;
    const set = new Set(this.expandedIds());
    set.has(node.id) ? set.delete(node.id) : set.add(node.id);
    this.expandedIds.set(set);
  }

  /**
   * ★ 第八轮(Forrest):勾选语义 = 「勾了什么导出什么」,所见即所得。
   *  · 勾选文件夹 = 连同子项一起勾上(标准树形语义,和系统文件选择器一致);
   *  · 取消勾选 = 连同子树一起取消;
   *  · 导出时**逐节点**过滤 —— 文件夹里没勾的子项不会混进去,
   *    反过来,只勾了深层子项时,父文件夹会以半选状态被带上(保住层级)。
   *  旧行为的两个毛病:Select all 只勾根节点(子项看着没选,其实会导出,
   *  用户以为丢了);只勾子项不勾父级时整个子树都被丢掉。
   */
  toggleChecked(node: MaterialNode): void {
    const set = new Set(this.selectedIds());
    if (set.has(node.id)) {
      collectIds(node).forEach((id) => set.delete(id));   // 取消 = 整棵子树取消
    } else {
      collectIds(node).forEach((id) => set.add(id));      // 勾选 = 连子项一起勾
    }
    this.selectedIds.set(set);
  }

  /** 半选态:自己没勾,但子树里有勾选的 —— 文件夹上显示一条短横线。 */
  hasCheckedDesc(node: MaterialNode): boolean {
    return (node.children ?? []).some((c) => this.selectedIds().has(c.id) || this.hasCheckedDesc(c));
  }

  selectAll(on: boolean): void {
    const set = new Set<string>();
    if (on) for (const r of this.exportRoots()) collectIds(r).forEach((id) => set.add(id));
    this.selectedIds.set(set);
  }

  onSourceChange(value: string): void {
    this.exportSource.set(value);
    this.selectAll(true);      // 换了类别就重选一次,避免勾着上个类别的旧 id
  }

  // ---------- 导出动作 ----------
  async doExport(): Promise<void> {
    // 逐节点过滤:只导出勾选中的节点;只勾了深层子项时,
    // 父文件夹作为半选容器被自动带上,层级不丢。
    const picked = this.exportRoots()
      .map((r) => this.filterChecked(r))
      .filter((n): n is MaterialNode => n !== null);
    if (picked.length === 0) {
      this.notify(this.t('transfer.exportNone'));
      return;
    }
    this.exporting.set(true);
    try {
      const fmt = this.format();
      const text = serializeTransfer(buildExportPayload(this.sourceName(), picked), fmt);
      const name = this.exportFileName();
      if (fmt === 'pdf') {
        this.printToPdf(text);
      } else {
        // ★ 第六轮:路径由用户选 —— 系统另存为;取消则什么都不发生
        const saved = await this.saveToFile(name, text, fmt);
        if (!saved) return;
      }
      this.notify(this.t('transfer.exportDone'));
    } finally {
      this.exporting.set(false);
    }
  }

  /** 只保留勾选中的节点:文件夹在「自己被勾」或「子树里有勾选」时保留。 */
  private filterChecked(n: MaterialNode): MaterialNode | null {
    const kids = n.folder
      ? (n.children ?? [])
          .map((c) => this.filterChecked(c))
          .filter((c): c is MaterialNode => c !== null)
      : [];
    if (!this.selectedIds().has(n.id) && kids.length === 0) return null;
    if (!n.folder) return n;
    return { ...n, children: kids };
  }

  private downloadFile(name: string, text: string, fmt: TransferFormat): void {
    const blob = new Blob([text], { type: fmt === 'json' ? 'application/json;charset=utf-8' : 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
  }

  /**
   * ★ 第六轮(Forrest):导出路径可选 ——
   * 支持 File System Access API 的浏览器(Chrome/Edge)弹系统「另存为」,
   * 用户自己挑目录与文件名(Excalidraw / Figma 网页版的同款做法);
   * 不支持的浏览器回落普通下载(存到浏览器下载目录)。
   * 返回 false 仅代表用户点了取消(正常反悔,不算错误,静默)。
   */
  private async saveToFile(name: string, text: string, fmt: TransferFormat): Promise<boolean> {
    const win = window as unknown as {
      showSaveFilePicker?: (opts: {
        suggestedName?: string;
        types?: { description?: string; accept: Record<string, string[]> }[];
      }) => Promise<{
        createWritable: () => Promise<{
          write: (data: string) => Promise<void>;
          close: () => Promise<void>;
        }>;
      }>;
    };
    // 「浏览器下载文件夹」模式 = 明确跳过询问;浏览器不支持时也只能下载
    if (this.saveMode() === 'downloads' || typeof win.showSaveFilePicker !== 'function') {
      this.downloadFile(name, text, fmt);
      return true;
    }
    try {
      // 注意:必须在点击事件的任务里同步调用(浏览器要求 user activation),
      // 前面不能插入 await。
      const handle = await win.showSaveFilePicker({
        suggestedName: name,
        types: [{
          description: this.t('transfer.fmt.' + fmt),
          accept: { [transferMime(fmt)]: ['.' + fileExtension(fmt)] }
        }]
      });
      const stream = await handle.createWritable();
      await stream.write(text);
      await stream.close();
      return true;
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return false;
      // 其它异常(如权限被拒)不吞结果 —— 回落普通下载,用户至少拿到文件
      this.downloadFile(name, text, fmt);
      return true;
    }
  }

  /** ★ 第六轮:下载示例文件 —— 用户"照着填",不用猜格式(GitHub/Mailchimp 模板同款)。 */
  downloadSample(): void {
    this.downloadFile(SAMPLE_FILE_NAME, buildSampleFile(this.t), 'txt');
    this.notify(this.t('transfer.sampleDone'));
  }

  /**
   * PDF:把排版好的 HTML 塞进隐藏 iframe 再触发系统打印窗口 ——
   * 零第三方依赖、中文不缺字体、生成的是真 PDF(文字可选可搜),
   * 这是很多 Web 应用(Notion / Linear 早期)采用的方案。
   */
  private printToPdf(html: string): void {
    document.getElementById(MaterialTransferDialogComponent.PRINT_FRAME)?.remove();
    const frame = document.createElement('iframe');
    frame.id = MaterialTransferDialogComponent.PRINT_FRAME;
    frame.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;';
    frame.srcdoc = html;
    frame.onload = () => {
      try {
        frame.contentWindow?.focus();
        frame.contentWindow?.print();
      } catch { /* 打印被拦截时什么都不做,静默失败不影响其它格式 */ }
      setTimeout(() => frame.remove(), 1000);
    };
    document.body.appendChild(frame);
  }

  private static readonly PRINT_FRAME = 'yi-print-frame';

  // ---------- 导入 ----------
  readonly importMode = signal<'existing' | 'new' | 'none'>(
    this.categories.length > 0 ? 'existing' : 'none'
  );
  readonly importCategoryId = signal<string>(this.categories[0]?.id ?? '');
  readonly newCategoryName = signal('');
  readonly pickedFileName = signal('');
  readonly parsed = signal<TransferCategory[]>([]);
  readonly warnings = signal<ImportWarning[]>([]);
  readonly importing = signal(false);

  /** 隐藏的 file input(由风格化「浏览文件」按钮触发,原生控件不入画)。 */
  @ViewChild('fileInput') private fileInput?: ElementRef<HTMLInputElement>;

  pickFile(): void {
    this.fileInput?.nativeElement.click();
  }

  readonly importCounts = computed(() => countNodes(this.parsed()));

  async onFilePicked(ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.pickedFileName.set(file.name);
    try {
      const text = await file.text();
      const base = file.name.replace(/\.[^.]+$/, '') || 'Imported';
      const result = parseImport(text, base);
      this.parsed.set(result.categories);
      this.warnings.set(result.warnings);
      if (result.categories.length === 0) this.notify(this.t('transfer.importEmpty'));
    } catch {
      this.warnings.set([{ key: 'transfer.readFailed' }]);
      this.parsed.set([]);
    } finally {
      input.value = '';        // 允许重复选同一个文件
    }
  }

  /** 预览里显示的前若干条根节点名,帮用户确认选对了文件。 */
  previewNames(): string[] {
    const flat: string[] = [];
    const walk = (list: TransferNode[]): void => {
      for (const n of list ?? []) {
        if (flat.length >= 8) return;
        flat.push(n.name);
        if (n.folder) walk(n.children ?? []);
      }
    };
    for (const c of this.parsed()) walk(c.children);
    return flat;
  }

  /**
   * 导入 → 落到素材树。
   * 目标类别:'existing' 用现有 id;'new' 先建类别再挂;'none' 就是未分类(null)。
   * 结构还原规则:文件里只有一个类别时直接把它的子树并入目标类别(少一层);
   * 多个类别时每个类别成为一个根文件夹,结构不丢。
   */
  async doImport(): Promise<void> {
    const cats = this.parsed();
    if (cats.length === 0 || this.importing()) return;
    this.importing.set(true);
    try {
      let categoryId: string | null = null;
      if (this.importMode() === 'existing') {
        categoryId = this.importCategoryId() || null;
      } else if (this.importMode() === 'new') {
        const name = this.newCategoryName().trim();
        if (!name) {
          this.importing.set(false);
          this.notify(this.t('practice.categoryNewPlaceholder'));
          return;
        }
        const created = await firstValueFrom(this.api.createCategory(name));
        categoryId = created.id;
      }
      this.dialogRef.close({ importedNodes: this.toMaterialNodes(cats, categoryId) });
    } catch {
      this.importing.set(false);
      this.notify(this.t('practice.errUnknown'));
    }
  }

  private toMaterialNodes(cats: TransferCategory[], categoryId: string | null): MaterialNode[] {
    if (cats.length === 1) return cats[0].children.map((n) => this.mapNode(n, categoryId));
    return cats.map((c) => ({
      id: this.newId(),
      name: c.name,
      folder: true,
      expanded: true,
      categoryId,
      children: c.children.map((n) => this.mapNode(n, null))
    }));
  }

  private mapNode(n: TransferNode, categoryId: string | null): MaterialNode {
    const node: MaterialNode = { id: this.newId(), name: n.name, folder: !!n.folder, categoryId };
    if (node.folder) {
      node.expanded = true;
      node.children = (n.children ?? []).map((c) => this.mapNode(c, null));
    } else {
      node.content = n.content ?? '';
    }
    return node;
  }

  private newId(): string {
    return 'n_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 7);
  }

  private notify(msg: string): void {
    this.snack.open(msg, this.t('common.close'), { duration: 3000 });
  }

  /** 把 i18n 词条里的 {folders}/{files} 占位符替换成真实数量。 */
  countsText(): string {
    const c = this.importCounts();
    return this.t('transfer.previewCounts')
      .replace('{folders}', String(c.folders))
      .replace('{files}', String(c.files));
  }

  /** 解析提示 → 当前语言的文案(占位符 {count} / {detail} 在这里替换)。 */
  warningTexts(): string[] {
    return this.warnings().map((w) => {
      let s = this.t(w.key);
      if (w.count !== undefined) s = s.replace('{count}', String(w.count));
      if (w.detail !== undefined) s = s.replace('{detail}', w.detail);
      return s;
    });
  }

  cancel(): void {
    this.dialogRef.close();
  }
}
