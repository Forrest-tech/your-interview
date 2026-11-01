import DOMPurify, { type DOMPurify as DOMPurifyInstance } from 'dompurify';

/**
 * 票 309 §8：mail-html 客户端消毒纯函数（无 Angular 依赖，供组件与 Jest 同用）。
 *
 * 与服务端 §3 镜像的规则：
 * - FORBID_TAGS 含 'style'（<style> 块整段去掉，两层同策 T4）；
 * - FORBID_ATTR 含 srcset/formaction/ping/autofocus/poster/background
 *  （事件处理器由 DOMPurify 默认 strip）；
 * - ALLOWED_URI_REGEXP 只放 https:/mailto:/同源相对 URL——cid: 一定活不到客户端
 *   （serve 层已改写；此处是第二道），data: 亦不在放行列（见下 hook (d) 的说明）；
 * - style 属性三道 hook：(a) 含 url(/@import 整属性删；(c) 逐声明过滤 §3 白名单；
 *   (b) <a> 强制 target=_blank rel=noopener noreferrer；(d) <img> src 收敛到
 *   https:/同源 /inline/，其余一律占位图（永无无样式破图，含 serve 层漏网的 cid:）；
 *   (e) <img> 限宽 100%（srcdoc 不用 <style> 元素，见 buildMailSrcdoc 注释）。
 * - 输入规则：DOMPurify 永远只喂字符串（fetch 到的 /html 响应文本），绝不喂预解析 DOM。
 */

/**
 * iframe sandbox 精确值。注意：mail-html.component.ts 的 iframe 模板里另有一份
 * 逐字相同的静态字面量——Angular NG0910 禁止以绑定形式设 iframe 的 sandbox，
 * 故模板不能绑定本常量；改值时两处必须同步（Playwright 测试 20 断言真实
 * iframe 的属性值，Jest 测试 18 断言本常量）。
 */
export const MAIL_HTML_SANDBOX =
  'allow-same-origin allow-popups allow-popups-to-escape-sandbox';

export const MAIL_HTML_PLACEHOLDER =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8Xw8AAwMCAO+ip1sAAAAASUVORK5CYII=';

/** §3 显式 URL-free 内联 style 属性表（与服务端 MailHtmlSanitizer 同表）。 */
export const MAIL_HTML_ALLOWED_CSS_PROPERTIES: readonly string[] = [
  'margin', 'margin-top', 'margin-right', 'margin-bottom', 'margin-left',
  'padding', 'padding-top', 'padding-right', 'padding-bottom', 'padding-left',
  'width', 'height', 'min-width', 'min-height', 'max-width', 'max-height',
  'display', 'float', 'clear', 'overflow', 'overflow-x', 'overflow-y',
  'visibility', 'box-sizing', 'table-layout', 'border-collapse',
  'border-spacing', 'empty-cells', 'caption-side', 'vertical-align',
  'border', 'border-top', 'border-right', 'border-bottom', 'border-left',
  'border-width', 'border-top-width', 'border-right-width',
  'border-bottom-width', 'border-left-width',
  'border-style', 'border-top-style', 'border-right-style',
  'border-bottom-style', 'border-left-style',
  'border-color', 'border-top-color', 'border-right-color',
  'border-bottom-color', 'border-left-color',
  'border-radius', 'border-top-left-radius', 'border-top-right-radius',
  'border-bottom-right-radius', 'border-bottom-left-radius',
  'outline', 'outline-width', 'outline-style', 'outline-color', 'outline-offset',
  'color', 'background-color', 'font', 'font-family', 'font-size',
  'font-style', 'font-weight', 'font-variant', 'line-height',
  'letter-spacing', 'word-spacing', 'text-align', 'text-decoration',
  'text-decoration-line', 'text-decoration-color', 'text-decoration-style',
  'text-decoration-thickness', 'text-transform', 'text-indent',
  'white-space', 'word-break', 'overflow-wrap', 'text-overflow',
  'direction', 'unicode-bidi',
  'position', 'top', 'right', 'bottom', 'left', 'z-index',
  'list-style-type', 'list-style-position',
  'flex', 'flex-direction', 'flex-wrap', 'flex-flow', 'flex-grow',
  'flex-shrink', 'flex-basis', 'justify-content', 'align-items',
  'align-self', 'align-content', 'gap', 'row-gap', 'column-gap',
  'order', 'opacity',
];

const FORBID_TAGS = [
  'script', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet',
  'link', 'meta', 'base', 'form', 'input', 'button', 'select', 'textarea',
  'option', 'svg', 'math', 'video', 'audio', 'source', 'track', 'picture',
  'canvas', 'dialog', 'style',
];

const FORBID_ATTR = ['srcset', 'formaction', 'ping', 'autofocus', 'poster', 'background'];

/**
 * 只放 https:/mailto:/同源相对 URL。
 * - 同源绝对路径：单斜杠开头（// 开头的 protocol-relative 不算同源相对，一定排除）；
 * - 纯相对引用：首段无冒号（cid:/javascript:/data: 一律排除；#/? 开头的片段与查询亦放行）。
 */
export const MAIL_HTML_ALLOWED_URI_REGEXP =
  /^(?:https:|mailto:|\/(?!\/)|[#?]|[^:/?#]+(?:[/?#]|$))/i;

const INLINE_PREFIX = '/api/interviews/mailbox/assets/';

function splitCssDeclarations(style: string): string[] {
  const out: string[] = [];
  let cur = '';
  let depth = 0;
  let quote = '';
  for (const ch of style) {
    if (quote) {
      cur += ch;
      if (ch === quote) quote = '';
    } else if (ch === '"' || ch === "'") {
      quote = ch;
      cur += ch;
    } else if (ch === '(') {
      depth++;
      cur += ch;
    } else if (ch === ')') {
      depth = Math.max(0, depth - 1);
      cur += ch;
    } else if (ch === ';' && depth === 0) {
      out.push(cur);
      cur = '';
    } else {
      cur += ch;
    }
  }
  if (cur.trim()) out.push(cur);
  return out;
}

/** hook (c)：逐声明过滤 §3 白名单（纯函数，供 Jest 直接断言）。 */
export function filterStyleDeclarations(style: string): string {
  const allowed = new Set(MAIL_HTML_ALLOWED_CSS_PROPERTIES);
  const kept: string[] = [];
  for (const decl of splitCssDeclarations(style)) {
    const colon = decl.indexOf(':');
    if (colon <= 0) continue;
    const prop = decl.slice(0, colon).trim().toLowerCase();
    if (allowed.has(prop)) kept.push(decl.trim());
  }
  return kept.join('; ');
}

/**
 * 客户端消毒（DOMPurify，字符串输入）。调用方传 DOMPurify 实例——浏览器里用
 * 默认导出（已绑 window），测试里可注入带 window 的实例。
 */
export function sanitizeMailHtml(html: string, purify: DOMPurifyInstance): string {
  purify.addHook('uponSanitizeAttribute', (_node, event) => {
    if (event.attrName !== 'style') return;
    const value = event.attrValue ?? '';
    // hook (a)：值层防御——含 url( 或 @import 整属性删（与 serve Job 4 同规则）。
    if (/url\(|@import/i.test(value)) {
      event.keepAttr = false;
      return;
    }
    // hook (c)：同白名单再过滤一遍（防入库旁路把非白名单属性带到渲染层）。
    const filtered = filterStyleDeclarations(value);
    if (!filtered) event.keepAttr = false;
    else event.attrValue = filtered;
  });
  purify.addHook('afterSanitizeAttributes', (node) => {
    if (!(node instanceof Element)) return;
    const tag = node.tagName.toLowerCase();
    if (tag === 'a') {
      // hook (b)：链接硬化（与 serve Job 3 同规则，替换非合并）。
      node.setAttribute('target', '_blank');
      node.setAttribute('rel', 'noopener noreferrer');
      return;
    }
    if (tag !== 'img') return;
    // hook (d)：img src 收敛——只留 https:（Load images 模式）与同源 /inline/；
    // 缺失/空/cid:/data:/mailto:/相对路径等一律占位图，永无破图。
    // 注：data: 不在 ALLOWED_URI_REGEXP 放行列，故服务端保留的 data: 光栅图
    // 到客户端亦归占位（计划字面如此；无追踪风险，仅保真度说明，见票据报告）。
    const src = node.getAttribute('src');
    if (!src || !(src.startsWith('https:') || src.startsWith(INLINE_PREFIX))) {
      node.setAttribute('src', MAIL_HTML_PLACEHOLDER);
    }
    // hook (e)：图片限宽（srcdoc 不用 <style> 元素，见 buildMailSrcdoc）。
    const style = node.getAttribute('style') ?? '';
    if (!/max-width\s*:/i.test(style)) {
      node.setAttribute(
        'style',
        (style ? style.replace(/;?\s*$/, '; ') : '') + 'max-width:100%; height:auto',
      );
    }
  });
  try {
    return purify.sanitize(html, {
      FORBID_TAGS,
      FORBID_ATTR,
      ALLOWED_URI_REGEXP: MAIL_HTML_ALLOWED_URI_REGEXP,
      RETURN_TRUSTED_TYPE: false,
    }) as string;
  } finally {
    purify.removeAllHooks();
  }
}

/**
 * 实体解码（纯函数，无 DOM 依赖——Jest 可直接断言）。
 * 覆盖范围 = DOMPurify 序列化输出的实体字母表：文本节点转义的 &amp;/&lt;/&gt;、
 * 序列化保留的 &nbsp;、以及数字引用（&quot;/&#39; 兜底；属性值已随标签剥离）。
 */
function decodeEntities(text: string): string {
  const named: Record<string, string> = {
    amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ',
  };
  return text.replace(/&(#x[0-9a-fA-F]+|#\d+|[a-zA-Z]+);/g, (m, body: string) => {
    if (body.startsWith('#x') || body.startsWith('#X')) {
      const code = parseInt(body.slice(2), 16);
      return Number.isFinite(code) ? String.fromCodePoint(code) : m;
    }
    if (body.startsWith('#')) {
      const code = parseInt(body.slice(1), 10);
      return Number.isFinite(code) ? String.fromCodePoint(code) : m;
    }
    return named[body] ?? m;
  });
}

/**
 * §2/§8 共用判空规则：去全部标签、实体解码后仅剩空白即为空。
 * 与服务端 MailHtmlSanitizer.IsEmpty 同规则（服务端走 HtmlDecode；
 * 客户端输入已是 DOMPurify 序列化结果，实体字母表见 decodeEntities 注释）。
 */
export function isMailHtmlEmpty(cleanHtml: string): boolean {
  const textOnly = cleanHtml.replace(/<[^>]*>/g, '');
  return decodeEntities(textOnly).trim() === '';
}

/**
 * srcdoc 组装：整文档 + body 内联 reset 样式（白底、应用字体栈、长词折行），
 * 刻意不用 <style> 元素——测试 20 断言 iframe DOM 内无 <style> 元素，
 * 故 reset 只能走内联样式；图片限宽由 sanitize hook (e) 负责。
 */
export function buildMailSrcdoc(cleanHtml: string): string {
  const bodyStyle = [
    'margin:0',
    'background-color:#ffffff',
    'color:#111827',
    "font-family:Inter, -apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Hiragino Sans GB', 'Noto Sans SC', 'Microsoft YaHei', Roboto, Helvetica, Arial, sans-serif",
    'font-size:14px',
    'line-height:1.5',
    'overflow-wrap:break-word',
    'word-break:break-word',
  ].join('; ');
  return '<!doctype html><html><head><meta charset="utf-8"></head>' +
    `<body style="${bodyStyle}">` + cleanHtml + '</body></html>';
}

/**
 * 单一失败路径的决策函数：任何取信失败（404/500/网络/超时）与消毒为空
 * 都映射到同一终态 'fallback'（父级渲染纯文本）；只有取信成功且消毒后
 * 非空才进 'html'。初始渲染与 Load-images 重渲染共用此函数。
 */
export function mailHtmlOutcome(
  fetchOk: boolean,
  cleanHtml: string | null,
): 'html' | 'fallback' {
  if (!fetchOk || cleanHtml === null) return 'fallback';
  return isMailHtmlEmpty(cleanHtml) ? 'fallback' : 'html';
}
