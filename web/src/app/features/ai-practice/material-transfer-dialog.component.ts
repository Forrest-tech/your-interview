import {
  Component, computed, ElementRef, inject, OnInit, signal, ViewChild
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
import { CategoryManagerDialogComponent } from './category-manager-dialog.component';
import {
  IMPORT_ACCEPT, SAMPLE_FILE_NAME, TransferCategory, TransferFormat, TransferNode,
  buildExportPayload, buildSampleFile, countNodes, fileExtension, ImportWarning, parseImport,
  sanitizeName, serializeTransfer
} from './material-transfer';

/** 弹窗的三个页签:类别管理 / 导出 / 导入(★ 第九轮:三合一)。 */
type ManageTab = 'categories' | 'export' | 'import';

/* File System Access API 的最小类型面(项目未引入 @types/wicg-file-system-access,
   只声明真正用到的几个成员,避免把整个 lib 拉进来)。 */
interface FsWritable { write(data: string): Promise<void>; close(): Promise<void>; }
interface FsFileHandle { createWritable(): Promise<FsWritable>; }
interface FsDirHandle {
  readonly name: string;
  queryPermission?(opts: { mode: 'readwrite' }): Promise<PermissionState>;
  requestPermission?(opts: { mode: 'readwrite' }): Promise<PermissionState>;
  getFileHandle(name: string, opts?: { create?: boolean }): Promise<FsFileHandle>;
}
interface FsWindow { showDirectoryPicker?(opts: { mode: 'readwrite' }): Promise<FsDirHandle> }

interface DialogData {
  /** 整棵素材树(已在内存中的全量数据)。 */
  nodes: MaterialNode[];
  categories: PracticeCategoryDto[];
  /** 打开弹窗时树上的类别过滤值,作为导出来源的默认值。 */
  filter: string;
  /** 打开时落在哪个页签(工具栏两个入口分别指向 类别 / 导出)。 */
  tab?: ManageTab;
}

/** 收集一个节点及其全部后代的 id(勾选/取消要沿子树级联)。 */
function collectIds(n: MaterialNode): string[] {
  const out: string[] = [n.id];
  for (const c of n.children ?? []) out.push(...collectIds(c));
  return out;
}

/* ============================================================
   本地文件夹句柄的持久化(★ 第九轮:导出目录由用户自己选)
   File System Access API 的句柄可以存进 IndexedDB —— 下次打开
   弹窗还是同一个文件夹,不用每次重选(VS Code Web / Excalidraw 同款做法)。
   ============================================================ */
const DIR_DB = 'yi-export-fs';
const DIR_STORE = 'handles';
const DIR_KEY = 'export-dir';

function openDirDb(): Promise<IDBDatabase | null> {
  return new Promise((resolve) => {
    try {
      const req = indexedDB.open(DIR_DB, 1);
      req.onupgradeneeded = () => { req.result.createObjectStore(DIR_STORE); };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
    } catch { resolve(null); }
  });
}

function idbRun<T>(
  mode: IDBTransactionMode,
  work: (store: IDBObjectStore) => IDBRequest<T>
): Promise<T | undefined> {
  return openDirDb().then((db) => new Promise<T | undefined>((resolve) => {
    if (!db) { resolve(undefined); return; }
    try {
      const tx = db.transaction(DIR_STORE, mode);
      const req = work(tx.objectStore(DIR_STORE));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(undefined);
    } catch { resolve(undefined); }
  }));
}

const putDirHandle = (h: unknown): Promise<unknown> => idbRun('readwrite', (s) => s.put(h, DIR_KEY));
const delDirHandle = (): Promise<unknown> => idbRun('readwrite', (s) => s.delete(DIR_KEY));
const getDirHandle = (): Promise<unknown> => idbRun('readonly', (s) => s.get(DIR_KEY));

/** 关闭回传:需要合并进整树的节点 + 最新的类别列表(可选,仅关闭时带)。 */
export interface TransferResult {
  importedNodes: MaterialNode[];
  categories?: PracticeCategoryDto[];
}

/**
 * 素材「类别 / 导出 / 导入」弹窗(★ 2026-09-25 第四轮 Forrest;第九轮三合一)。
 *
 * ★ 第九轮:类别管理与导入导出**合成一个弹窗的三个页签**(原先是两个弹窗),
 *   尺寸固定 —— 三个页签共用同一块内容区(内容超高时页签内部滚动),
 *   切换页签时窗口不跳动(Notion Settings / VS Code Settings 的同款形态)。
 *
 * UX 参考(成熟产品):
 *  · Notion「Settings → Import / Export」:导入导出同处一处、分页签,
 *    用户不用记功能的藏身之处;
 *  · 导出侧提供**范围勾选**(勾选文件夹)而非整库导出 —— 对齐 Notion Export
 *    的 "Include subpages" 思路,但粒度更细:精确到子树;
 *  · 导出位置 = 用户**自己选的本地文件夹**(默认浏览器下载文件夹),
 *    句柄存 IndexedDB 记住下次(VS Code Web / Excalidraw 同款);
 *  · 导入侧强制**先预览再落库**(显示层级统计与条目预览),
 *    不做"选完文件直接写库"的隐式行为 —— 与 GitHub Import / Notion Import 一致;
 *  · 格式描述里明确写出"可再导入"(JSON/XML),让用户知道哪种格式适合备份迁移。
 */
@Component({
  selector: 'app-material-transfer-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatTooltipModule, MatTabsModule, MatRadioModule, MatCheckboxModule, MatSnackBarModule,
    CategoryManagerDialogComponent
  ],
  templateUrl: './material-transfer-dialog.component.html',
  styleUrl: './material-transfer-dialog.component.scss'
})
export class MaterialTransferDialogComponent implements OnInit {
  readonly dialogRef = inject<MatDialogRef<MaterialTransferDialogComponent, TransferResult>>(MatDialogRef);
  readonly data = inject<DialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(PracticeApi);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  // ---------- 本地数据 ----------
  private readonly roots: MaterialNode[] = [...(this.data.nodes ?? [])];
  /** ★ 类别是 signal:在「类别」页签里增删改后,导出/导入的下拉要立刻同步。 */
  readonly categories = signal<PracticeCategoryDto[]>([...(this.data.categories ?? [])]);

  /** 打开时落在哪个页签(工具栏两个入口分别指向 类别 / 导出)。 */
  readonly tab = signal<ManageTab>(this.data.tab ?? 'export');
  readonly tabIndex = computed<number>(() =>
    ({ categories: 0, export: 1, import: 2 } as Record<ManageTab, number>)[this.tab()]);

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
    return this.categories().find((c) => c.id === f)?.name ?? this.t('practice.categoryAll');
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
   * ★ 第九轮(Forrest):保存位置 = **用户自己选的本地文件夹**,默认下载文件夹。
   *  · 没选过 → 走浏览器默认下载文件夹(与系统"下载"一致,零学习成本);
   *  · 点「选择文件夹」→ 系统目录选择器,之后导出**直接写入**该目录,不再弹窗;
   *  · 句柄存 IndexedDB,下次打开弹窗自动找回(权限失效会如实提示并回落下载文件夹);
   *  · 浏览器不支持(File System Access API 缺失,如 Firefox/Safari)时如实说明。
   */
  readonly saveLocationSupported =
    typeof (window as unknown as { showDirectoryPicker?: unknown }).showDirectoryPicker === 'function';

  /** 已选文件夹的名字(空 = 用默认下载文件夹)。 */
  readonly saveDirName = signal<string>('');
  /** 是否已有可用文件夹(模板用它决定显示"更改"还是"选择文件夹")。 */
  readonly hasDir = computed<boolean>(() => this.saveDirName().length > 0);
  private dirHandle: FsDirHandle | null = null;

  ngOnInit(): void {
    void this.restoreDir();
  }

  /** 找回上次选过的文件夹:句柄还在且权限仍是 granted 才直接用。 */
  private async restoreDir(): Promise<void> {
    if (!this.saveLocationSupported) return;
    const h = (await getDirHandle()) as FsDirHandle | null;
    if (!h?.name) return;
    try {
      const perm = (await h.queryPermission?.({ mode: 'readwrite' })) ?? 'granted';
      if (perm === 'granted') {
        this.dirHandle = h;
        this.saveDirName.set(h.name);
      }
    } catch { /* 读权限失败就当作没选过,退回默认下载文件夹 */ }
  }

  /** 打开系统目录选择器(必须在点击任务里同步调用,浏览器要求用户手势)。 */
  async chooseFolder(): Promise<void> {
    if (!this.saveLocationSupported) return;
    try {
      const h = await (window as unknown as FsWindow).showDirectoryPicker!({ mode: 'readwrite' });
      this.dirHandle = h;
      this.saveDirName.set(h.name);
      void putDirHandle(h);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return;   // 用户取消,静默
      this.notify(this.t('transfer.dirFailed'));
    }
  }

  /** 恢复默认下载文件夹。 */
  async resetFolder(): Promise<void> {
    this.dirHandle = null;
    this.saveDirName.set('');
    void delDirHandle();
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
   * ★ 第九轮(Forrest):导出落盘 ——
   *  · 用户选过本地文件夹 → 直接写进去(不弹任何系统窗口);
   *  · 没选过 / 浏览器不支持 → 普通下载(浏览器下载文件夹);
   *  · 写选定的文件夹失败(权限失效等)→ 如实提示并回落下载,用户不会空手而归。
   */
  private async saveToFile(name: string, text: string, fmt: TransferFormat): Promise<boolean> {
    if (this.dirHandle) {
      if (await this.writeToDir(this.dirHandle, name, text)) return true;
      this.downloadFile(name, text, fmt);
      return true;
    }
    this.downloadFile(name, text, fmt);
    return true;
  }

  /** 往已选文件夹里写文件;失败返回 false(调用方回落下载并已提示原因)。 */
  private async writeToDir(dir: FsDirHandle, name: string, text: string): Promise<boolean> {
    try {
      const opts = { mode: 'readwrite' as const };
      let perm = (await dir.queryPermission?.(opts)) ?? 'granted';
      // 权限在页面重新加载后会变成 prompt,点导出时的用户手势正好可以补申请
      if (perm !== 'granted') perm = (await dir.requestPermission?.(opts)) ?? 'denied';
      if (perm !== 'granted') {
        this.notify(this.t('transfer.dirDenied'));
        return false;
      }
      const file = await dir.getFileHandle(name, { create: true });
      const stream = await file.createWritable();
      await stream.write(text);
      await stream.close();
      return true;
    } catch {
      this.notify(this.t('transfer.dirFailed'));
      return false;
    }
  }

  /** ★ 第六轮:下载示例文件 —— 用户"照着填",不用猜格式(GitHub/Mailchimp 模板同款)。 */
  async downloadSample(): Promise<void> {
    const text = buildSampleFile(this.t);
    if (this.dirHandle) {
      if (await this.writeToDir(this.dirHandle, SAMPLE_FILE_NAME, text)) {
        this.notify(this.t('transfer.sampleDone'));
        return;
      }
    }
    this.downloadFile(SAMPLE_FILE_NAME, text, 'txt');
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
    this.categories().length > 0 ? 'existing' : 'none'
  );
  readonly importCategoryId = signal<string>(this.categories()[0]?.id ?? '');
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
      this.close({ importedNodes: this.toMaterialNodes(cats, categoryId) });
    } catch {
      this.importing.set(false);
      this.notify(this.t('practice.errUnknown'));
    }
  }

  // ---------- 「类别」页签与宿主的联动 ----------

  /** 类别增删改/排序 → 同步本地列表,导出/导入页的下拉立刻可见。 */
  onCategoriesChanged(list: PracticeCategoryDto[]): void {
    this.categories.set([...list]);
    if (this.importMode() === 'none' && list.length > 0) {
      this.importMode.set('existing');
      this.importCategoryId.set(list[0].id);
    }
    if (this.importMode() === 'existing' && !list.some((c) => c.id === this.importCategoryId())) {
      this.importCategoryId.set(list[0]?.id ?? '');
    }
  }

  /** 模板导入 → 回传骨架节点并关闭弹窗(与原先类别弹窗的行为一致)。 */
  onCategoryImported(nodes: MaterialNode[]): void {
    this.close({ importedNodes: nodes });
  }

  /** 统一出口:关闭时总是带上最新类别列表,宿主的下拉不会因为"直接关窗"而漏更新。 */
  private close(result: TransferResult): void {
    this.dialogRef.close(result);
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
    this.close({ importedNodes: [], categories: this.categories() });
  }
}
