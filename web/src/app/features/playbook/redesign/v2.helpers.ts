import { I18nService } from '../../../core/i18n/i18n.service';
import { applyParams } from '../components/brief/brief.helpers';
import { V2_I18N } from './v2.i18n';

/**
 * v2 组件共用的 t():先查本地 V2_I18N(按当前语言),查不到再回退全局
 * I18nService —— 与 brief 的 makeBriefT 同一套接线方式。
 */
export function makeV2T(i18n: I18nService): (key: string) => string {
  return (key: string): string => {
    const e = V2_I18N[key];
    if (e) {
      const l = i18n.lang();
      return e[l] ?? e.en ?? e.zh ?? key;
    }
    return i18n.t(key);
  };
}

/** 带 {param} 插值的版本:fmt('pb.v2.d.scorePts', { n: 87 }) */
export function makeV2Fmt(
  i18n: I18nService
): (key: string, params: Record<string, string | number>) => string {
  const t = makeV2T(i18n);
  return (key, params) => applyParams(t(key), params);
}

/** 人名列表的连接符:中文用顿号,其它语言用逗号。 */
export function nameSep(i18n: I18nService): string {
  return i18n.lang() === 'zh' ? '、' : ', ';
}

export function joinNames(i18n: I18nService, names: string[]): string {
  return names.join(nameSep(i18n));
}

/**
 * 面试形式是存储值(如 intake 解析出的「线上视频」会原样存进 round.format / meta.format),
 * 存储值不改;展示时把已知的四种规范值映射成本地化标签,未知值原样显示。
 */
const FORMAT_KEYS: Record<string, string> = {
  '线上视频': 'pb.v2.fmt.video',
  '线上做题平台': 'pb.v2.fmt.platform',
  '电话': 'pb.v2.fmt.phone',
  '现场': 'pb.v2.fmt.onsite'
};

export function formatLabel(t: (key: string) => string, v: string | null | undefined): string {
  if (!v) return '';
  const key = FORMAT_KEYS[v];
  if (key) return t(key);
  // 存储值常带补充说明(「线上视频(Teams)」「现场(Toronto 办公室)」):
  // 映射已知前缀,其余原文保留。
  for (const stored of Object.keys(FORMAT_KEYS).sort((a, b) => b.length - a.length)) {
    if (v.startsWith(stored)) return t(FORMAT_KEYS[stored]) + v.slice(stored.length);
  }
  return v;
}

/**
 * 把可能混着整页 HTML 的 JD / 公司介绍文本清成纯文本(展示层清洗,存量数据不动):
 *  - head / script / style / title / meta 等整段丢弃,内容不进正文;
 *  - <br> 与块级标签(p/li/div/section/h1-h6/tr 等)转成换行;
 *  - 其余标签剥掉,只留文本;HTML 实体解码;连续空行收成一行。
 * 纯文本输入基本原样返回(只做空白规范化)。
 */
export function htmlToPlainText(raw: string | null | undefined): string {
  if (!raw) return '';
  let s = raw;
  if (!/<[a-z!/][^>]*>/i.test(s)) {
    return s.replace(/\r\n?/g, '\n').trim();
  }
  s = s
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<head\b[^>]*>[\s\S]*?<\/head\s*>/gi, '')
    .replace(/<script\b[^>]*>[\s\S]*?<\/script\s*>/gi, '')
    .replace(/<style\b[^>]*>[\s\S]*?<\/style\s*>/gi, '')
    .replace(/<title\b[^>]*>[\s\S]*?<\/title\s*>/gi, '')
    .replace(/<noscript\b[^>]*>[\s\S]*?<\/noscript\s*>/gi, '')
    .replace(/<svg\b[^>]*>[\s\S]*?<\/svg\s*>/gi, '');
  // 只剩零散的自闭合杂标签(meta/link/base 等)直接剥掉
  s = s.replace(/<(meta|link|base|input|img|hr)\b[^>]*>/gi, '');
  // 块级结构 → 换行;列表项加项目符号前缀,与原展示习惯接近
  s = s
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<li\b[^>]*>/gi, '\n• ')
    .replace(/<\/(p|div|section|article|header|footer|ul|ol|li|h[1-6]|tr|table|blockquote|pre)\s*>/gi, '\n')
    .replace(/<(p|div|section|article|header|footer|ul|ol|h[1-6]|tr|blockquote|pre)\b[^>]*>/gi, '\n');
  // 其余标签一律剥除
  s = s.replace(/<[^>]+>/g, '');
  // 实体解码(DOM 解析一遍最稳;无 DOM 环境退回手工替换)
  s = decodeEntities(s);
  return s
    .split('\n')
    .map(line => line.replace(/[ \t ]+/g, ' ').trim())
    .filter((line, i, arr) => line !== '' || (i > 0 && arr[i - 1] !== ''))
    .join('\n')
    .trim();
}

function decodeEntities(s: string): string {
  if (typeof document !== 'undefined') {
    const ta = document.createElement('textarea');
    ta.innerHTML = s;
    return ta.value;
  }
  return s
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'");
}
