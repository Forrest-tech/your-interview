/**
 * 票 309 §11 前端 Jest 部分（DOM-free 作用域，诚实边界说明）：
 * - 本 spec 只断言 node 环境可跑的纯逻辑：sandbox 常量（测试 18）、
 *   style 白名单不变式、逐声明过滤、srcdoc 形态（含无 <style> 元素）、
 *   URI 正则、判空规则、单一失败路径决策。
 * - sanitizeMailHtml 本体需 DOM + 浏览器绑定的 DOMPurify 实例，Jest（node 环境、
 *   无 jsdom、无 TestBed）跑不了——其行为由 Playwright sanitize spec
 *   （测试 17/19/24/24b 的 DOM 断言）在真实 Chromium 中证明，见
 *   web/e2e/t309-mail-html-sanitize.spec.ts。
 * - sanitizeMailHtml 在本 spec 中只做“可 import 且不抛”断言：模块 import dompurify
 *   时只拿到工厂函数（无 window 不执行），与生产组件传浏览器实例的路径分离。
 */
import { describe, expect, it } from '@jest/globals';
import {
  MAIL_HTML_ALLOWED_CSS_PROPERTIES,
  MAIL_HTML_ALLOWED_URI_REGEXP,
  MAIL_HTML_PLACEHOLDER,
  MAIL_HTML_SANDBOX,
  buildMailSrcdoc,
  filterStyleDeclarations,
  isMailHtmlEmpty,
  mailHtmlOutcome,
  sanitizeMailHtml,
} from './mail-html.sanitize';

describe('票309 测试 18：iframe sandbox 常量', () => {
  it('sandbox 字符串精确等于计划值，且绝不含 allow-scripts', () => {
    expect(MAIL_HTML_SANDBOX).toBe(
      'allow-same-origin allow-popups allow-popups-to-escape-sandbox',
    );
    expect(MAIL_HTML_SANDBOX).not.toContain('allow-scripts');
    expect(MAIL_HTML_SANDBOX).not.toContain('allow-top-navigation');
  });
});

describe('票309 §3：客户端 style 白名单与服务端同表（不变式）', () => {
  it('全部小写 kebab、无重复', () => {
    const list = MAIL_HTML_ALLOWED_CSS_PROPERTIES;
    expect(new Set(list).size).toBe(list.length);
    for (const p of list) expect(p).toMatch(/^[a-z][a-z0-9-]*$/);
  });

  it('不含任何可带 URL 的属性（含 background 简写与 animation 等）', () => {
    const banned = [
      'background', 'background-image', 'background-position', 'background-size',
      'list-style', 'list-style-image', 'cursor', 'content', 'animation',
      'transition', 'filter', 'clip-path', 'mask', 'mask-image', 'src',
      'behavior', '-webkit-box-reflect',
    ];
    for (const p of banned) expect(MAIL_HTML_ALLOWED_CSS_PROPERTIES).not.toContain(p);
  });

  it('含关键放行项（颜色/限宽/字体/圆角/布局）', () => {
    for (const p of [
      'color', 'background-color', 'max-width', 'font-family', 'font-size',
      'border-radius', 'border-collapse', 'flex-direction', 'text-align',
      'line-height', 'opacity',
    ]) {
      expect(MAIL_HTML_ALLOWED_CSS_PROPERTIES).toContain(p);
    }
  });
});

describe('票309 hook (c)：逐声明过滤', () => {
  it('只留白名单声明；非白名单即使无 URL 也去掉', () => {
    expect(filterStyleDeclarations('color: red; animation: spin 1s; font-size: 14px'))
      .toBe('color: red; font-size: 14px');
  });

  it('自定义属性（--*）一律去掉', () => {
    expect(filterStyleDeclarations('--x: 1; color: blue')).toBe('color: blue');
  });

  it('括号/引号内的分号不切分声明', () => {
    expect(filterStyleDeclarations('font-family: "a;b", sans-serif; color: red'))
      .toBe('font-family: "a;b", sans-serif; color: red');
  });

  it('全被过滤后返回空串（调用方删整属性）', () => {
    expect(filterStyleDeclarations('animation: spin 1s')).toBe('');
  });
});

describe('票309 §8：客户端 URI 正则（https:/mailto:/同源相对 only）', () => {
  const ok = ['https://example.com/a.png', 'mailto:a@b.com', '/api/interviews/mailbox/assets/x/inline/y', '#frag', '?q=1', 'rel/path', 'rel?x=1'];
  const bad = ['//evil.com/x.png', 'cid:abc', 'data:image/png;base64,AAAA', 'javascript:alert(1)', 'http://example.com/x.png', 'vbscript:msgbox', 'java\tscript:alert(1)'];
  it.each(ok)('放行 %s', (u) => {
    expect(MAIL_HTML_ALLOWED_URI_REGEXP.test(u)).toBe(true);
  });
  it.each(bad)('拦截 %s', (u) => {
    expect(MAIL_HTML_ALLOWED_URI_REGEXP.test(u)).toBe(false);
  });
});

describe('票309 §2/§8：判空规则（去标签 + 实体解码 + trim）', () => {
  it('纯空白/仅标签/仅 &nbsp; 均为空', () => {
    expect(isMailHtmlEmpty('   ')).toBe(true);
    expect(isMailHtmlEmpty('<p></p><br>')).toBe(true);
    expect(isMailHtmlEmpty('<p>&nbsp;</p>')).toBe(true);
    expect(isMailHtmlEmpty('<p>&#32;</p>')).toBe(true);
  });
  it('有可见字符即非空；&amp; 解码后仍为可见字符', () => {
    expect(isMailHtmlEmpty('<p>hi</p>')).toBe(false);
    expect(isMailHtmlEmpty('<p>&amp;</p>')).toBe(false);
  });
  it('纯图片邮件按计划 §2 判空（无文本即空——保真度说明，见票据报告）', () => {
    expect(isMailHtmlEmpty('<img src="x.png">')).toBe(true);
  });
});

describe('票309 srcdoc 形态', () => {
  it('含净化后 HTML、白底 reset、无 <style> 元素', () => {
    const doc = buildMailSrcdoc('<p>hi</p>');
    expect(doc).toContain('<p>hi</p>');
    expect(doc.startsWith('<!doctype html>')).toBe(true);
    expect(doc).toContain('background-color:#ffffff');
    expect(doc).not.toContain('<style');
    expect(doc).toContain('overflow-wrap:break-word');
  });
});

describe('票309 占位图常量（与服务端同字节）', () => {
  it('为 1×1 透明 PNG data URI（PNG 魔数校验）', () => {
    expect(MAIL_HTML_PLACEHOLDER.startsWith('data:image/png;base64,')).toBe(true);
    const bytes = Buffer.from(MAIL_HTML_PLACEHOLDER.split(',')[1], 'base64');
    expect([...bytes.subarray(0, 4)]).toEqual([0x89, 0x50, 0x4e, 0x47]);
  });
});

describe('票309 单一失败路径决策（测试 24b 的 Jest 层）', () => {
  it('取信失败（500 态与网络错误态在决策层同形）一律 fallback', () => {
    expect(mailHtmlOutcome(false, null)).toBe('fallback');
    expect(mailHtmlOutcome(false, '<p>hi</p>')).toBe('fallback');
  });
  it('取信成功但消毒为空 → fallback；非空 → html', () => {
    expect(mailHtmlOutcome(true, '   ')).toBe('fallback');
    expect(mailHtmlOutcome(true, '<p>hi</p>')).toBe('html');
  });
});

describe('票309 sanitizeMailHtml 模块可 import（不执行消毒）', () => {
  it('导出函数存在；node 下 import dompurify 不抛（工厂函数，未绑 window）', () => {
    expect(typeof sanitizeMailHtml).toBe('function');
  });
});
