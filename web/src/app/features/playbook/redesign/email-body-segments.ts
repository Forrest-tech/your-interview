/**
 * 票165：邮件正文分段（展示层纯函数，定稿 §2.3）。
 * 输入已存 TranscriptText，输出时间线节点 + 余段；只做展示切分，不改写原文、不猜填。
 *
 * 边界只认两类行式（行式之外的文本一律不切分）：
 * - 发件人行（4 式）：From:/发件人：行、客户端单行头 <姓名> <时间>、
 *   引用头 On … wrote:/…写道：、From 行 + 紧邻 Sent 行配对；
 * - 会议卡块：含会议平台标识行的连续非空行块，且块内含时间行（取块内首个时间的起始值）。
 * 日期时间行本身不是边界，只提供时间（同行 / 紧邻 Sent 行 / 卡块内）。
 * 无年份行不认；非法日历日期不认；落款名不认作面试官。
 *
 * 回落口径（定死：按条回落）：无任何可识别时间时整段回落（fallback=true，
 * 调用方原样展示全文、无时间线）；部分可切分时，已切出者成时间线，
 * 无时间者按原文顺序作余段附后（余段无时间线圆点、无时间/发件人标签）。
 */

export interface EmailBodySegment {
  /** 'YYYY-MM-DD HH:mm'，无时间时 null（余段）。 */
  dateTime: string | null;
  sender: string | null;
  body: string;
  /** 会议卡节点：只标时间 + “会议卡”，不标发件人。 */
  meetingCard: boolean;
}

export interface EmailBodySegmentation {
  /** true => 无任何可识别时间，调用方应整段原文原样展示、无时间线。 */
  fallback: boolean;
  /** 有时间的段，时间升序、同时间原文先后（稳定）。 */
  timeline: EmailBodySegment[];
  /** 无时间的段，原文顺序。 */
  remainder: EmailBodySegment[];
}

const DATE_TIME = '(\\d{4})[-/](\\d{1,2})[-/](\\d{1,2})\\s+(\\d{1,2}):(\\d{2})';
const WEEKDAY_INTERVAL =
  '(\\d{4})[-/](\\d{1,2})[-/](\\d{1,2})\\s+周[一二三四五六日天]\\s+(\\d{1,2}):(\\d{2})[～〜~](\\d{1,2}):(\\d{2})';

function daysInMonth(y: number, m: number): number {
  const leap = (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0;
  return [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][m - 1] ?? 0;
}

function validDateTime(y: number, mo: number, d: number, h: number, mi: number): boolean {
  return mo >= 1 && mo <= 12 && d >= 1 && d <= daysInMonth(y, mo) && h >= 0 && h <= 23 && mi >= 0 && mi <= 59;
}

function pad(n: number): string {
  return n < 10 ? '0' + n : '' + n;
}

function norm(y: number, mo: number, d: number, h: number, mi: number): string {
  return `${y}-${pad(mo)}-${pad(d)} ${pad(h)}:${pad(mi)}`;
}

/** 严格匹配一整段日期时间 token（一位数月/日/时变体均可），非法日历日期返回 null。 */
function parseDateTimeToken(token: string): string | null {
  const w = token.match(new RegExp(`^${WEEKDAY_INTERVAL}$`));
  if (w) {
    const [y, mo, d, h, mi] = [w[1], w[2], w[3], w[4], w[5]].map(Number);
    return validDateTime(y, mo, d, h, mi) ? norm(y, mo, d, h, mi) : null;
  }
  const p = token.match(new RegExp(`^${DATE_TIME}$`));
  if (p) {
    const [y, mo, d, h, mi] = [p[1], p[2], p[3], p[4], p[5]].map(Number);
    return validDateTime(y, mo, d, h, mi) ? norm(y, mo, d, h, mi) : null;
  }
  return null;
}

/** 在一行内找首个日期时间（卡块内时间用；星期夹间形态只取起始时间）。 */
function findBlockDateTime(line: string): string | null {
  const w = line.match(new RegExp(WEEKDAY_INTERVAL));
  if (w) {
    const [y, mo, d, h, mi] = [w[1], w[2], w[3], w[4], w[5]].map(Number);
    if (validDateTime(y, mo, d, h, mi)) return norm(y, mo, d, h, mi);
  }
  const p = line.match(new RegExp(DATE_TIME));
  if (p) {
    const [y, mo, d, h, mi] = [p[1], p[2], p[3], p[4], p[5]].map(Number);
    if (validDateTime(y, mo, d, h, mi)) return norm(y, mo, d, h, mi);
  }
  return null;
}

/** 发件人行匹配：返回 { 发件人, 时间|null }；不是发件人行返回 null。 */
function matchSenderLine(line: string): { sender: string; dateTime: string | null } | null {
  const t = line.trim();
  // 式1：From: / 发件人：
  const from = t.match(/^From\s*:\s*(.+)$/i) ?? t.match(/^发件人\s*[:：]\s*(.+)$/);
  if (from) {
    const raw = from[1].trim();
    // 尾随时间拆出（“From: 张三 2026-10-20 09:00”）；尾随时间非法则整行不认，避免发件人名带垃圾日期。
    const trailing = raw.match(/^(.*?)\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})$/);
    if (trailing) {
      const dt = parseDateTimeToken(trailing[2]);
      if (!dt) return null;
      const name = trailing[1].replace(/<[^>]*>/g, '').trim();
      return name ? { sender: name, dateTime: dt } : null;
    }
    let name = raw.replace(/<[^>]*>/g, '').trim();
    if (!name) {
      const em = raw.match(/<([^>]*)>/);
      name = em ? em[1].trim() : '';
    }
    return name ? { sender: name, dateTime: null } : null;
  }
  // 式3：引用头 On … wrote: / …写道：
  const quote = t.match(/^On\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})\s*,\s*(.+?)\s+wrote:\s*$/i)
    ?? t.match(/^在\s*(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})\s*，\s*(.+?)\s*写道\s*[:：]\s*$/);
  if (quote) {
    const dt = parseDateTimeToken(quote[1]);
    const name = quote[2].trim();
    return dt && name ? { sender: name, dateTime: dt } : null;
  }
  // 式2：客户端单行头 <姓名> <时间>（名中不含冒号，避免主题行误切；名长设限，避免长句误切）。
  const header = t.match(/^(.+?)\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})$/);
  if (header) {
    const name = header[1].trim();
    if (name && !/[:：]/.test(name) && name.length <= 40) {
      const dt = parseDateTimeToken(header[2]);
      if (dt) return { sender: name, dateTime: dt };
    }
    return null;
  }
  return null;
}

/** Sent 行匹配（仅式4配对时由调用方在紧邻位置使用）。 */
function matchSentLine(line: string): string | null {
  const t = line.trim();
  const m = t.match(/^Sent\s*:\s*(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})\s*$/i)
    ?? t.match(/^发送时间\s*[:：]\s*(\d{4}[-/]\d{1,2}[-/]\d{1,2}\s+\d{1,2}:\d{2})\s*$/);
  return m ? parseDateTimeToken(m[1]) : null;
}

/** 会议平台标识行（行首匹配，避免正文提及误触）。 */
function isPlatformLine(line: string): boolean {
  return /^(microsoft\s+teams|teams\s*会议|microsoft\s+teams\s*会议)/i.test(line.trim());
}

/** 去掉首尾空行，行内空白与缩进原样保留（逐字）。 */
function trimBlock(s: string): string {
  const ls = s.split('\n');
  while (ls.length && ls[0].trim() === '') ls.shift();
  while (ls.length && ls[ls.length - 1].trim() === '') ls.pop();
  return ls.join('\n');
}

interface RawChunk {
  sender: string | null;
  dateTime: string | null;
  lines: string[];
  leading: boolean;
}

export function segmentEmailBody(text: string): EmailBodySegmentation {
  const lines = (text ?? '').split('\n');
  // 第一遍：按发件人行切出原文块（首块为 leading 余文）。
  const chunks: RawChunk[] = [];
  let cur: RawChunk = { sender: null, dateTime: null, lines: [], leading: true };
  for (let i = 0; i < lines.length; i++) {
    const s = matchSenderLine(lines[i]);
    if (s) {
      chunks.push(cur);
      cur = { sender: s.sender, dateTime: s.dateTime, lines: [], leading: false };
      // 式4：From 行 + 紧邻 Sent 行配对（非紧邻不附着）。
      if (!cur.dateTime && i + 1 < lines.length) {
        const sent = matchSentLine(lines[i + 1]);
        if (sent) { cur.dateTime = sent; i++; }
      }
      continue;
    }
    cur.lines.push(lines[i]);
  }
  chunks.push(cur);

  // 第二遍：块内抽会议卡（平台行所在的连续非空行块，块内有时间才成节点）。
  interface Node extends EmailBodySegment { order: number }
  const nodes: Node[] = [];
  let order = 0;
  for (const chunk of chunks) {
    const msgOrder = order++;
    const bodyLines: string[] = [];
    const ls = chunk.lines;
    let j = 0;
    while (j < ls.length) {
      if (ls[j].trim() === '') { bodyLines.push(ls[j]); j++; continue; }
      let k = j;
      while (k < ls.length && ls[k].trim() !== '') k++;
      const run = ls.slice(j, k);
      const platIdx = run.findIndex(isPlatformLine);
      if (platIdx >= 0) {
        // 卡块从块内首个时间行/平台行（二者较早者）起算，之前的行仍属消息正文，不吞。
        let dtIdx = -1;
        let dt: string | null = null;
        for (let x = 0; x < run.length; x++) {
          const found = findBlockDateTime(run[x]);
          if (found) { dtIdx = x; dt = found; break; }
        }
        if (dt !== null) {
          const start = Math.min(platIdx, dtIdx);
          bodyLines.push(...run.slice(0, start));
          nodes.push({ dateTime: dt, sender: null, body: trimBlock(run.slice(start).join('\n')), meetingCard: true, order: order++ });
          j = k;
          continue;
        }
      }
      bodyLines.push(...run);
      j = k;
    }
    const body = trimBlock(bodyLines.join('\n'));
    if (chunk.leading) {
      if (body) nodes.push({ dateTime: null, sender: null, body, meetingCard: false, order: msgOrder });
    } else if (body) {
      nodes.push({ dateTime: chunk.dateTime, sender: chunk.sender, body, meetingCard: false, order: msgOrder });
    }
  }

  const timed = nodes.filter(n => n.dateTime !== null);
  if (timed.length === 0) return { fallback: true, timeline: [], remainder: [] };
  const timeline = [...timed]
    .sort((a, b) => (a.dateTime! < b.dateTime! ? -1 : a.dateTime! > b.dateTime! ? 1 : a.order - b.order))
    .map(({ dateTime, sender, body, meetingCard }) => ({ dateTime, sender, body, meetingCard }));
  const remainder = nodes
    .filter(n => n.dateTime === null)
    .map(({ dateTime, sender, body, meetingCard }) => ({ dateTime, sender, body, meetingCard }));
  return { fallback: false, timeline, remainder };
}
