/**
 * 素材导入 / 导出的纯函数层(★ 2026-09-25 第四轮 Forrest)。
 *
 * 为什么独立成文件:
 *   序列化 / 解析是纯逻辑,不依赖 Angular 与 DOM,单项 Euler 可测、可复用;
 *   弹窗组件只负责交互与呈现,两边职责清晰(同 Big Tech 的 "core + view" 分层)。
 *
 * 设计取舍(对齐成熟产品):
 *   · 导出格式对齐 Notion Export:Markdown(带层级编号)/ 纯文本 / PDF(浏览器打印)/
 *     结构化数据(JSON)与可交换数据(XML),用户按需选择;
 *   · MD / TXT 采用**层级编号**("1. / 1.1 / 1.1.1"),不是缩进树 ——
 *     阅读软件、邮件、IM 里粘贴后仍保持结构(这是任务书明确要求的阅读体感);
 *   · TXT 的内容行统一加 "> " 前缀引号块:标题行与正文行因此可以被无歧义地区分,
 *     导出的文件再导入能原样还原(round-trip),这是 TaskPaper / Obsidian
 *     outline 文件普遍采用的"显式标记正文"做法。
 */
import { MaterialNode } from '../../shared/material-tree/material-tree.component';

/** 导出/导入共用的树节点(不含 id —— 跨文件的中性结构)。 */
export interface TransferNode {
  name: string;
  folder: boolean;
  children?: TransferNode[];
  content?: string;
}

/** 一个类别 + 其下素材树。 */
export interface TransferCategory {
  name: string;
  children: TransferNode[];
}

/** 导出文件的统一载荷。 */
export interface TransferPayload {
  version: number;
  app: string;
  exportedAt: string;
  categories: TransferCategory[];
}

/** 支持的导出格式。 */
export type TransferFormat = 'markdown' | 'txt' | 'pdf' | 'json' | 'xml';

/** 可选导入文件的扩展名(accept 属性用)。 */
export const IMPORT_ACCEPT = '.json,.xml,.txt,.md,.markdown,.text';

export function fileExtension(format: TransferFormat): string {
  return format === 'markdown' ? 'md' : format === 'pdf' ? 'pdf' : format;
}

/** 文件名消毒:非法字符换下划线、压掉多余空白并限长(Windows/macOS 都安全)。 */
export function sanitizeName(s: string): string {
  return (s ?? '')
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, 80);
}

/** 示例文件名(固定英文,跨语言稳定;下载后按里面说明填写)。 */
export const SAMPLE_FILE_NAME = 'practice-materials-sample.txt';

/**
 * 说明行前缀(★ 第六轮 Forrest)。
 * 为什么需要:示例文件要"边看说明边填",但不能让说明变成一条素材 ——
 * 用 "//" 写说明、导入时整行忽略,是 .gitignore / .env / 各类模板文件的通用做法,
 * 用户不用先删说明再导入,也永远不会在树里多出一条"如何使用本示例"。
 */
export const COMMENT_PREFIX = '//';

export function isCommentLine(line: string): boolean {
  return /^\s*\/\//.test(line);
}

/**
 * 生成示例文件(★ 第六轮 Forrest)。
 * 设计要点:
 *  · 头部是 "//" 说明块 —— 打开文件就能照着填,导入时自动忽略;
 *  · 说明下面是**填好的真实例子**,原样导入即得一棵干净的示例树
 *    (和 GitHub / Mailchimp 的 sample file 同一思路,但更进一步:示例即可用模板)。
 * 文案走 i18n,结构编号固定。
 */
export function buildSampleFile(t: (key: string) => string): string {
  const note = (s: string): string => `${COMMENT_PREFIX} ${s}`;
  const howto = t('transfer.sample.howtoBody').split('\n').filter((l) => l.trim());
  return [
    note(t('transfer.sample.title')),
    ...howto.map(note),
    '',
    `1. ${t('practice.tpl.jobInterview.label')}`,
    `1.1 ${t('practice.ti.selfIntro')}`,
    `> ${t('transfer.sample.body1')}`,
    `1.2 ${t('practice.ti.tech')}`,
    `> ${t('transfer.sample.body2')}`,
    '',
    `2. ${t('practice.tpl.dailyEnglish.label')}`,
    `2.1 ${t('practice.ti.travel')}`,
    `> ${t('transfer.sample.body3')}`,
    ''
  ].join('\n');
}

/** 下载用的 MIME(部分格式用 text/plain 兜底,浏览器一律走下载而非预览)。 */
export function transferMime(format: TransferFormat): string {
  switch (format) {
    case 'json': return 'application/json;charset=utf-8';
    case 'xml': return 'application/xml;charset=utf-8';
    case 'pdf': return 'text/html;charset=utf-8';
    default: return 'text/plain;charset=utf-8';
  }
}

// ---------------------------------------------------------------------------
// MaterialNode → TransferNode
// ---------------------------------------------------------------------------

/** 把本地树节点映射成中性结构(content 缺失补空串,folder 决定有无 children)。 */
export function toTransferNode(n: MaterialNode): TransferNode {
  const base: TransferNode = { name: n.name || '', folder: !!n.folder };
  if (n.folder) base.children = (n.children ?? []).map(toTransferNode);
  else base.content = n.content ?? '';
  return base;
}

export function buildExportPayload(categoryName: string, roots: MaterialNode[]): TransferPayload {
  return {
    version: 1,
    app: 'your-interview',
    exportedAt: new Date().toISOString(),
    categories: [{ name: categoryName, children: roots.map(toTransferNode) }]
  };
}

// ---------------------------------------------------------------------------
// 序列化:JSON / XML / Markdown / TXT / PDF(打印 HTML)
// ---------------------------------------------------------------------------

export function serializeTransfer(payload: TransferPayload, format: TransferFormat): string {
  switch (format) {
    case 'json': return toJson(payload);
    case 'xml': return toXml(payload);
    case 'markdown': return toMarkdown(payload);
    case 'pdf': return toPrintHtml(payload);
    default: return toText(payload);
  }
}

function toJson(payload: TransferPayload): string {
  return JSON.stringify(payload, null, 2);
}

/** XML 属性值转义(name 走属性,content 走 CDATA,避免尖括号破坏结构)。 */
function escAttr(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}

function xmlHasUnsafe(s: string): boolean {
  return /]]>|[\u0000-\u0008\u000B\u000C\u000E-\u001F]/.test(s);
}

function indent(level: number): string {
  return '  '.repeat(level);
}

function toXmlNode(n: TransferNode, level: number): string {
  const tag = n.folder ? 'folder' : 'file';
  const open = `${indent(level)}<${tag} name="${escAttr(n.name)}">`;
  if (n.folder) {
    const kids = (n.children ?? []).map((c) => toXmlNode(c, level + 1)).join('\n');
    return kids ? `${open}\n${kids}\n${indent(level)}</${tag}>` : `${open}\n${indent(level)}</${tag}>`;
  }
  const body = n.content ?? '';
  // 含 "]]>" 或控制字符时退回转义文本,不让 XML 变成非法文档
  const inner = xmlHasUnsafe(body) ? escAttr(body) : `<![CDATA[${body}]]>`;
  return `${open}${inner}</${tag}>`;
}

function toXml(payload: TransferPayload): string {
  const cats = payload.categories.map((c) => {
    const kids = c.children.map((n) => toXmlNode(n, 2)).join('\n');
    return `  <category name="${escAttr(c.name)}">${kids ? `\n${kids}\n  ` : ''}</category>`;
  }).join('\n');
  return [
    '<?xml version="1.0" encoding="UTF-8"?>',
    `<materials version="${payload.version}" app="${escAttr(payload.app)}" exportedAt="${escAttr(payload.exportedAt)}">`,
    cats,
    '</materials>',
    ''
  ].join('\n');
}

/** 逐个节点编号:1 / 1.1 / 1.1.1 —— 深度越小级越高。 */
function eachNode(nodes: TransferNode[], prefix: string, visit: (n: TransferNode, num: string, depth: number) => void, depth = 1): void {
  nodes.forEach((n, i) => {
    const num = prefix ? `${prefix}.${i + 1}` : String(i + 1);
    visit(n, num, depth);
    if (n.folder && n.children?.length) eachNode(n.children, num, visit, depth + 1);
  });
}

/**
 * 编号渲染:顶层写成 "1. 标题",更深层写成 "1.1 标题"。
 * 只在**没有小数点**时补一个点 —— 需求样例就是这种写法("1. 文件夹 / 1.1 标题"),
 * 深层再补点会变成难看的 "1.1. 标题"。
 */
function prefixNumber(num: string): string {
  return `${num}${num.includes('.') ? '' : '.'} `;
}

function contentLines(content?: string): string[] {
  return (content ?? '').replace(/\r\n/g, '\n').split('\n');
}

function toMarkdown(payload: TransferPayload): string {
  const out: string[] = [];
  for (const cat of payload.categories) {
    eachNode([{ name: cat.name, folder: true, children: cat.children }], '', (n, num, depth) => {
      const hashes = '#'.repeat(Math.min(depth, 6));
      out.push(`${hashes} ${prefixNumber(num)}${n.name}`.trim());
      if (!n.folder) {
        const body = contentLines(n.content).join('\n').trim();
        if (body) { out.push(''); out.push(body); }
      }
      out.push('');
    });
  }
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trim() + '\n';
}

function toText(payload: TransferPayload): string {
  const out: string[] = [];
  for (const cat of payload.categories) {
    eachNode([{ name: cat.name, folder: true, children: cat.children }], '', (n, num) => {
      out.push(`${prefixNumber(num)}${n.name}`);
      // 正文行加 "> " 前缀:再导入时能与标题行严格区分
      if (!n.folder) {
        const body = contentLines(n.content).join('\n').trim();
        if (body) out.push(body.split('\n').map((l) => `> ${l}`).join('\n'));
      }
    });
    out.push('');
  }
  return out.join('\n').trim() + '\n';
}

function escHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** PDF 走浏览器"打印 → 另存为 PDF":零依赖、中文不缺字体、文本可选可复制。 */
function toPrintHtml(payload: TransferPayload): string {
  const rows: string[] = [];
  for (const cat of payload.categories) {
    eachNode([{ name: cat.name, folder: true, children: cat.children }], '', (n, num, depth) => {
      const tag = `h${Math.min(depth, 4)}`;
      rows.push(`<${tag} class="l${Math.min(depth, 4)}"><span class="no">${prefixNumber(num)}</span>${escHtml(n.name)}</${tag}>`);
      if (!n.folder) {
        const body = contentLines(n.content).join('\n').trim();
        if (body) rows.push(`<div class="body">${escHtml(body).replace(/\n/g, '<br>')}</div>`);
      }
    });
  }
  return `<!doctype html>
<html lang="zh"><head><meta charset="utf-8"><title>${escHtml(payload.categories.map((c) => c.name).join(' / '))}</title>
<style>
  @page { margin: 18mm 16mm; }
  body { font: 14px/1.7 -apple-system, "PingFang SC", "Microsoft YaHei", Helvetica, Arial, sans-serif; color: #1c1e21; max-width: 780px; margin: 0 auto; }
  h1 { font-size: 22px; margin: 0 0 14px; border-bottom: 2px solid #e3e7ee; padding-bottom: 8px; }
  h2 { font-size: 18px; margin: 20px 0 8px; }
  h3 { font-size: 15.5px; margin: 16px 0 6px; }
  h4 { font-size: 14px; margin: 14px 0 4px; }
  .no { display: inline-block; min-width: 3.2em; color: #6b7280; font-weight: 600; }
  .body { white-space: normal; color: #33383f; margin: 0 0 10px 3.2em; }
</style></head><body>${rows.join('')}</body></html>`;
}

// ---------------------------------------------------------------------------
// 解析:JSON / XML / 大纲文本(TXT / MD)
// ---------------------------------------------------------------------------

/**
 * 解析提示:只带**词条 key 和参数**,文案交给 i18n ——
 * 否则界面语言切到英文/法文时会冒出一句中文。
 */
export interface ImportWarning {
  key: string;
  count?: number;
  detail?: string;
}

export interface ImportResult {
  categories: TransferCategory[];
  /** 解析过程中的提示(如"忽略了无法识别的行"),给用户看而不是静默吞掉。 */
  warnings: ImportWarning[];
}

export function countNodes(cats: TransferCategory[]): { folders: number; files: number } {
  let folders = 0; let files = 0;
  const walk = (list: TransferNode[]): void => {
    for (const n of list ?? []) {
      if (n.folder) { folders++; walk(n.children ?? []); } else files++;
    }
  };
  cats.forEach((c) => walk(c.children));
  return { folders, files };
}

const EMPTY: ImportResult = { categories: [], warnings: [] };

/**
 * 按内容自动识别文件种类 —— 不看扩展名(用户常改扩展名,"看内容"更稳,
 * 与 VS Code 语言识别的思路一致)。
 */
export function parseImport(text: string, fallbackName = 'Imported'): ImportResult {
  const raw = (text ?? '').replace(/^\uFEFF/, '').trim();
  if (!raw) return EMPTY;
  if (raw.startsWith('{') || raw.startsWith('[')) return parseJson(raw, fallbackName);
  if (raw.startsWith('<')) return parseXml(raw, fallbackName);
  return parseOutline(raw, fallbackName);
}

// ---------- JSON ----------

/** 兼容自家导出格式与常见外来结构(子节点数组、备选字段名都尽量认)。 */
function normalizeJsonNode(x: unknown): TransferNode | null {
  if (!x || typeof x !== 'object') return null;
  const o = x as Record<string, unknown>;
  const name = firstString(o, ['name', 'title', 'text', 'label']);
  if (!name) return null;
  const kids = Array.isArray(o['children']) ? o['children']
    : Array.isArray(o['nodes']) ? o['nodes']
      : Array.isArray(o['items']) ? o['items'] : null;
  const folder = typeof o['folder'] === 'boolean' ? o['folder'] as boolean : !!kids;
  const node: TransferNode = { name, folder };
  if (folder) node.children = (kids ?? []).map(normalizeJsonNode).filter((n): n is TransferNode => !!n);
  else node.content = firstString(o, ['content', 'body', 'answer', 'text', 'value']) ?? '';
  return node;
}

function firstString(o: Record<string, unknown>, keys: string[]): string | null {
  for (const k of keys) {
    const v = o[k];
    if (typeof v === 'string' && v.trim()) return v.trim();
    if (typeof v === 'number') return String(v);
  }
  return null;
}

function parseJson(raw: string, fallbackName: string): ImportResult {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch (e) {
    return { categories: [], warnings: [{ key: 'transfer.warn.jsonInvalid', detail: (e as Error).message }] };
  }
  const warnings: ImportWarning[] = [];
  const cats: TransferCategory[] = [];

  const obj = data as Record<string, unknown>;
  if (Array.isArray(obj?.['categories'])) {
    for (const c of obj['categories'] as Record<string, unknown>[]) {
      const name = firstString(c ?? {}, ['name', 'title']) ?? fallbackName;
      const kids = Array.isArray((c as Record<string, unknown>)['children'])
        ? c['children'] as unknown[] : [];
      cats.push({ name, children: kids.map(normalizeJsonNode).filter((n): n is TransferNode => !!n) });
    }
  } else if (Array.isArray(data)) {
    cats.push({ name: fallbackName, children: data.map(normalizeJsonNode).filter((n): n is TransferNode => !!n) });
  } else if (obj && Array.isArray(obj['children'])) {
    cats.push({ name: firstString(obj, ['name', 'title']) ?? fallbackName, children: (obj['children'] as unknown[]).map(normalizeJsonNode).filter((n): n is TransferNode => !!n) });
  }
  if (cats.length === 0) warnings.push({ key: 'transfer.warn.none' });
  return { categories: cats, warnings };
}

// ---------- XML ----------

function xmlText(el: Element): string {
  // CDATA 与文本子节点在浏览器里都落在 nodeValue 上,直接拼接即可
  let out = '';
  el.childNodes.forEach((ch) => { if (ch.nodeType === 3 || ch.nodeType === 4) out += ch.nodeValue ?? ''; });
  return out;
}

function fromXmlNode(el: Element): TransferNode | null {
  const tag = el.tagName.toLowerCase();
  const name = el.getAttribute('name') || el.getAttribute('title') || xmlText(el).trim().slice(0, 80);
  if (!name) return null;
  const node: TransferNode = { name, folder: tag === 'folder' || tag === 'dir' || tag === 'directory' };
  if (node.folder) {
    const kids: TransferNode[] = [];
    el.childNodes.forEach((ch) => {
      if (ch.nodeType === 1) {
        const kid = fromXmlNode(ch as Element);
        if (kid) kids.push(kid);
      }
    });
    node.children = kids;
  } else {
    node.content = xmlText(el);
  }
  return node;
}

function parseXml(raw: string, fallbackName: string): ImportResult {
  const doc = new DOMParser().parseFromString(raw, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) {
    return { categories: [], warnings: [{ key: 'transfer.warn.xmlInvalid' }] };
  }
  const root = doc.documentElement;
  const cats: TransferCategory[] = [];
  const direct: TransferNode[] = [];
  root.childNodes.forEach((ch) => {
    if (ch.nodeType !== 1) return;
    const el = ch as Element;
    const tag = el.tagName.toLowerCase();
    if (tag === 'category') {
      cats.push({
        name: el.getAttribute('name') ?? fallbackName,
        children: Array.from(el.children).map(fromXmlNode).filter((n): n is TransferNode => !!n)
      });
    } else {
      const n = fromXmlNode(el);
      if (n) direct.push(n);
    }
  });
  const result = cats.length > 0 ? cats : [{ name: fallbackName, children: direct }];
  if (result.every((c) => c.children.length === 0)) {
    return { categories: result, warnings: [{ key: 'transfer.warn.none' }] };
  }
  return { categories: result, warnings: [] };
}

// ---------- 大纲文本(TXT / Markdown) ----------

/**
 * ⚠️ 分隔点必须是**可选**且后面必须跟空白:
 *   写成 `^(\d+(?:\.\d+)*)[.、]\s*(.*)$` 时,正则会回溯 ——
 *   "1.1 Trip" 会被切成 编号="1" + 标题="1 Trip",层级全乱(实测过);
 *   改成 `)[.、]?\s+` 后,"1. Trip"、"1.1 Trip" 都能正确取到编号 "1" / "1.1"。
 */
const NUMBERED = /^(\d+(?:\.\d+)*)[.、]?\s+(.*)$/;
const MD_HEAD = /^(#{1,6})\s+(.*)$/;
const BULLET = /^[-*+]\s+(.*)$/;

/**
 * 大纲文本解析。先判断整篇是"标题型"(Markdown # / 数字编号)还是"缩进型",
 * 因为两种模式对"这行是子节点还是正文"的判定完全相反,必须二选一,
 * 否则同一个文件会得出不同的树(这也是多数 outline 工具的做法)。
 */
function parseOutline(raw: string, fallbackName: string): ImportResult {
  // "//" 说明行先剔除再判断形态 —— 否则示例文件的说明块会被当成正文/节点
  const lines = raw.split(/\r?\n/).filter((l) => !isCommentLine(l));
  const hasHead = lines.some((l) => MD_HEAD.test(l.trim()));
  const hasNumber = lines.some((l) => NUMBERED.test(l.trim()));
  const warnings: ImportWarning[] = [];

  if (hasHead || hasNumber) {
    return buildFromTitles(lines, fallbackName, warnings);
  }
  return buildFromIndent(lines, fallbackName, warnings);
}

/** 标题型:标题行建节点,紧随其后的非标题行并入该节点正文(兼容 "> " 引号块)。 */
function buildFromTitles(lines: string[], fallbackName: string, warnings: ImportWarning[]): ImportResult {
  interface Item { node: TransferNode; level: number }
  const stack: Item[] = [];
  const roots: TransferNode[] = [];
  let last: TransferNode | null = null;
  let skipped = 0;

  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    let level = 0;
    let title = '';
    const md = trimmed.match(MD_HEAD);
    const num = trimmed.match(NUMBERED);
    if (md) {
      level = md[1].length;
      title = stripNumber(md[2]);
    } else if (num) {
      level = num[1].split('.').length;
      title = stripNumber(num[2]);
    }
    if (level > 0) {
      const node: TransferNode = { name: title || 'Untitled', folder: false, children: [] };
      while (stack.length && stack[stack.length - 1].level >= level) stack.pop();
      if (stack.length === 0) roots.push(node);
      else {
        const parent = stack[stack.length - 1].node;
        parent.folder = true;
        (parent.children = parent.children ?? []).push(node);
      }
      stack.push({ node, level });
      last = node;
      continue;
    }
    // 非标题行 = 正文。"> " 行即便后面是空的也要接上 —— 导出把段落空行写成
    // 单独一个 ">",不接回来的话两段正文会粘在一起(round-trip 丢格式)。
    const quote = trimmed.match(/^>\s?(.*)$/);
    const text = quote ? quote[1] : quoted(trimmed);
    if (last) {
      if (quote || text) {
        last.folder = false;
        last.content = last.content ? `${last.content}\n${text}` : text;
      }
    } else if (!quote) {
      skipped++;      // 标题之前的散行;文件头的 "> " 引言不算"被忽略的行"
    }
  }
  if (skipped > 0) warnings.push({ key: 'transfer.warn.skipped', count: skipped });
  return { categories: [{ name: fallbackName, children: roots }], warnings };
}

/** 去掉残留的 "1.2.3 " 之类前缀,避免导出→导入后标题被重复编号。 */
function stripNumber(s: string): string {
  return s.replace(/^\d+(?:\.\d+)*[.、]?\s*/, '').trim();
}

function quoted(s: string): string {
  const m = s.match(/^>\s?(.*)$/);
  return m ? m[1] : s;
}

/** 缩进型:按缩进量定层级(自动识别 2/4 空格或 Tab),没有正文字段。 */
function buildFromIndent(lines: string[], fallbackName: string, warnings: ImportWarning[]): ImportResult {
  const effective: { text: string; unitLevel: number }[] = [];
  for (const line of lines) {
    if (!line.trim()) continue;
    const lead = line.match(/^[\t ]*/)?.[0] ?? '';
    effective.push({ text: line.trim(), unitLevel: lead.length });
  }
  if (effective.length === 0) return EMPTY;

  // 用相邻行缩进差的最小正值当"一级缩进单位",兼容 2 空格 / 4 空格 / Tab
  let unit = 0;
  for (let i = 1; i < effective.length; i++) {
    const diff = effective[i].unitLevel - effective[i - 1].unitLevel;
    if (diff > 0) unit = unit === 0 ? diff : Math.min(unit, diff);
  }
  if (unit === 0) unit = effective.every((e) => e.unitLevel === 0) ? 1 : 2;

  const stack: { node: TransferNode; level: number }[] = [];
  const roots: TransferNode[] = [];
  for (const line of effective) {
    const level = Math.max(0, Math.round(line.unitLevel / unit));
    const text = quoted(line.text);
    const bullet = text.match(BULLET);
    const node: TransferNode = { name: (bullet ? bullet[1] : text) || 'Untitled', folder: false, children: [] };
    while (stack.length && stack[stack.length - 1].level >= level) stack.pop();
    if (stack.length === 0) roots.push(node);
    else {
      const parent = stack[stack.length - 1].node;
      parent.folder = true;
      (parent.children = parent.children ?? []).push(node);
    }
    stack.push({ node, level });
  }
  if (roots.length === 0) return EMPTY;
  return { categories: [{ name: fallbackName, children: roots }], warnings };
}
