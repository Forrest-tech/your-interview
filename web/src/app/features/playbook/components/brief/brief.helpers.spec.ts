/**
 * 票64 ARC-08c 首批 spec —— brief.helpers:parsePastedEmail / normalizeEmailDate。
 *
 * 对照源:evidence-41/helper-assert.cjs(票41 §7 特征断言 38 例)中属本函数的
 * 22 例逐项映射(下方每条标注「票41」),期望值逐字守票 41 落盘口径;
 * 另加的 boundary 扩充例均由现行源码逐行读证得出,标注「扩充」与类别。
 * 特征测试锁现状:不改被测函数行为;可疑行为只登记报告,不在本票修。
 * 票102 修订:票64 登记 B-64-1 (normalizeEmailDate 不校验日历合法性) 已修复——
 * 原 2 例 (locked) 期望按票55先例改口径并在用例内注明依据=票102;另加日历
 * 合法性 boundary 组 21 例 + parsePastedEmail 管线 2 例。
 */
import { describe, expect, it } from '@jest/globals';
import { normalizeEmailDate, parsePastedEmail } from './brief.helpers';

describe('normalizeEmailDate(票41 映射 14 例)', () => {
  // [功能] 票41:ISO 原样
  it('ISO date stays', () => {
    expect(normalizeEmailDate('2026-10-13')).toBe('2026-10-13');
  });
  // [功能/Unicode] 票41:中文年月日补零
  it('zh date pads', () => {
    expect(normalizeEmailDate('2026年10月5日')).toBe('2026-10-05');
  });
  // [功能] 票41:斜杠日期
  it('slash date', () => {
    expect(normalizeEmailDate('2026/1/2')).toBe('2026-01-02');
  });
  // [功能] 票41:d MMM yyyy
  it('d MMM yyyy', () => {
    expect(normalizeEmailDate('5 Oct 2026')).toBe('2026-10-05');
  });
  // [功能] 票41:MMM d, yyyy
  it('MMM d, yyyy', () => {
    expect(normalizeEmailDate('Oct 5, 2026')).toBe('2026-10-05');
  });
  // [功能/日期-时区临界] 票41:RFC 形带 GMT 尾巴走字符串路径,不受时区挪日
  it('RFC-ish with GMT tail', () => {
    expect(normalizeEmailDate('Tue, 13 Oct 2026 10:00:00 GMT-0400 (Eastern Daylight Time)'))
      .toBe('2026-10-13');
  });
  // [boundary-空] 票41:空串
  it('empty string', () => {
    expect(normalizeEmailDate('')).toBe('');
  });
  // [boundary-空] 票41:纯空白
  it('whitespace only', () => {
    expect(normalizeEmailDate('   ')).toBe('');
  });
  // [boundary-无法识别] 票41:垃圾文本
  it('garbage', () => {
    expect(normalizeEmailDate('not a date at all')).toBe('');
  });
  // [boundary-月份名变体] 票41:SEPT 大写
  it('SEPT uppercase', () => {
    expect(normalizeEmailDate('5 SEPT 2026')).toBe('2026-09-05');
  });
  // [boundary-月份名变体] 票41:全名 September
  it('full month name', () => {
    expect(normalizeEmailDate('September 5, 2026')).toBe('2026-09-05');
  });
  // [boundary-未知月份] 票41:未知月份名回落 Date 解析失败
  it('unknown month', () => {
    expect(normalizeEmailDate('5 Foo 2026')).toBe('');
  });
  // [boundary-日期临界] 票41:年末
  it('year end', () => {
    expect(normalizeEmailDate('2026-12-31')).toBe('2026-12-31');
  });
  // [boundary-日期临界] 票41:新年中文
  it('new year zh', () => {
    expect(normalizeEmailDate('2027年1月1日')).toBe('2027-01-01');
  });
});

describe('normalizeEmailDate(boundary 扩充,源码读证)', () => {
  // [boundary-补零] 扩充:个位月日 ISO 输入补零(m1 路径 padStart)
  it('single digit ISO pads', () => {
    expect(normalizeEmailDate('2026-1-5')).toBe('2026-01-05');
  });
  // [boundary-分隔符] 扩充:点分隔同在 m1 字符类内
  it('dot separator', () => {
    expect(normalizeEmailDate('2026.10.5')).toBe('2026-10-05');
  });
  // [boundary-月份名变体] 扩充:Sept. 带点(m3 路径 months[sept]=9)
  it('Sept. with dot, month-first', () => {
    expect(normalizeEmailDate('Sept. 5, 2026')).toBe('2026-09-05');
  });
  // [boundary-空白] 扩充:首尾空白先 trim
  it('padded with whitespace', () => {
    expect(normalizeEmailDate('  Oct 5, 2026  ')).toBe('2026-10-05');
  });
  // [boundary-日期临界] 票64 (locked) 例改期望,依据=票102(B-64-1 修复):
  // 2026 非闰年,2月29日日历上不存在 → 归空(原 locked 期望为原样回传 '2026-02-29')
  it('Feb 29 on non-leap year rejected (票102 改期望,原 locked)', () => {
    expect(normalizeEmailDate('2026-02-29')).toBe('');
  });
  // [boundary-日期临界] 票64 (locked) 例改期望,依据=票102:13 月不存在 → 归空
  // (原 locked 期望为原样回传 '2026-13-01')
  it('month 13 rejected (票102 改期望,原 locked)', () => {
    expect(normalizeEmailDate('2026-13-01')).toBe('');
  });
  // [boundary-超长] 扩充:超长垃圾串解析失败回空
  it('very long garbage', () => {
    expect(normalizeEmailDate('x'.repeat(10000))).toBe('');
  });
  // [boundary-Unicode] 扩充:纯中文无数字文本回空
  it('pure zh text no digits', () => {
    expect(normalizeEmailDate('日期不存在')).toBe('');
  });
  // [boundary-null] 扩充:签名为非空 string,传 null 现行抛 TypeError(特征锁定)
  it('null throws (non-nullable signature, locked)', () => {
    expect(() => normalizeEmailDate(null as unknown as string)).toThrow(TypeError);
  });
  // [boundary-null] 扩充:undefined 同上
  it('undefined throws (non-nullable signature, locked)', () => {
    expect(() => normalizeEmailDate(undefined as unknown as string)).toThrow(TypeError);
  });
  // [boundary-文件类] 不适用:无文件入参(票面口径注明)
});

describe('normalizeEmailDate(票102 日历合法性 boundary)', () => {
  // [boundary-日期临界/闰年] 2024 闰年 2/29 合法,ISO 路径守恒
  it('Feb 29 on leap year 2024 valid (ISO)', () => {
    expect(normalizeEmailDate('2024-02-29')).toBe('2024-02-29');
  });
  // [boundary-日期临界/闰年] 同上,m2 日-月名路径
  it('Feb 29 on leap year valid (d MMM yyyy)', () => {
    expect(normalizeEmailDate('29 Feb 2024')).toBe('2024-02-29');
  });
  // [boundary-日期临界/闰年] 同上,m3 月名-日路径
  it('Feb 29 on leap year valid (MMM d, yyyy)', () => {
    expect(normalizeEmailDate('Feb 29, 2024')).toBe('2024-02-29');
  });
  // [boundary-日期临界/闰年-百年] 2000 被 400 整除 → 闰年,2/29 合法
  it('Feb 29 year 2000 valid (400-year leap)', () => {
    expect(normalizeEmailDate('2000-02-29')).toBe('2000-02-29');
  });
  // [boundary-日期临界/闰年-百年] 1900 被 100 整除但不被 400 → 平年,2/29 归空
  it('Feb 29 year 1900 rejected (century non-leap)', () => {
    expect(normalizeEmailDate('1900-02-29')).toBe('');
  });
  // [boundary-日期临界] 平年 2/29,m2 路径同样校验
  it('Feb 29 non-leap rejected (d MMM yyyy)', () => {
    expect(normalizeEmailDate('29 Feb 2026')).toBe('');
  });
  // [boundary-日期临界] 平年 2/29,m3 路径同样校验
  it('Feb 29 non-leap rejected (MMM d, yyyy)', () => {
    expect(normalizeEmailDate('Feb 29, 2026')).toBe('');
  });
  // [boundary-日期临界] 2/30 任何年份都不存在
  it('Feb 30 rejected', () => {
    expect(normalizeEmailDate('2026-02-30')).toBe('');
  });
  // [boundary-日期临界] 0 月归空
  it('month 0 rejected', () => {
    expect(normalizeEmailDate('2026-00-10')).toBe('');
  });
  // [boundary-日期临界] 0 日归空
  it('day 0 rejected', () => {
    expect(normalizeEmailDate('2026-01-00')).toBe('');
  });
  // [boundary-日期临界] 32 日归空
  it('day 32 rejected', () => {
    expect(normalizeEmailDate('2026-01-32')).toBe('');
  });
  // [boundary-日期临界] 4 月只有 30 天,31 日归空 (ISO)
  it('Apr 31 rejected (ISO)', () => {
    expect(normalizeEmailDate('2026-04-31')).toBe('');
  });
  // [boundary-日期临界] 4/31,m3 路径
  it('Apr 31 rejected (MMM d, yyyy)', () => {
    expect(normalizeEmailDate('Apr 31, 2026')).toBe('');
  });
  // [boundary-日期临界] 4/31,m2 路径
  it('Apr 31 rejected (d MMM yyyy)', () => {
    expect(normalizeEmailDate('31 Apr 2026')).toBe('');
  });
  // [boundary-日期临界/Unicode] 中文写法平年 2/29 归空
  it('Feb 29 non-leap zh rejected', () => {
    expect(normalizeEmailDate('2026年2月29日')).toBe('');
  });
  // [boundary-日期临界/Unicode] 中文写法 13 月归空
  it('month 13 zh rejected', () => {
    expect(normalizeEmailDate('2026年13月1日')).toBe('');
  });
  // [boundary-日期临界/跨年] 年末守恒
  it('Dec 31 valid across year boundary', () => {
    expect(normalizeEmailDate('2024-12-31')).toBe('2024-12-31');
  });
  // [boundary-日期临界/跨年] 年初守恒
  it('Jan 1 valid across year boundary', () => {
    expect(normalizeEmailDate('2025-01-01')).toBe('2025-01-01');
  });
  // [boundary-日期临界] 平年 2/28 (非法 2/29 的前一日) 守恒
  it('Feb 28 non-leap valid', () => {
    expect(normalizeEmailDate('2026-02-28')).toBe('2026-02-28');
  });
  // [boundary-日期临界] 平年 3/1 守恒
  it('Mar 1 non-leap valid', () => {
    expect(normalizeEmailDate('2026-03-01')).toBe('2026-03-01');
  });
  // [boundary-日期临界] 合法月末守恒:1/31 与 4/30
  it('valid month ends preserved', () => {
    expect(normalizeEmailDate('2026-01-31')).toBe('2026-01-31');
    expect(normalizeEmailDate('2026-04-30')).toBe('2026-04-30');
  });
});

describe('parsePastedEmail(票41 映射 8 例)', () => {
  // [功能] 票41:英文头三件套 + 正文
  it('en headers', () => {
    expect(parsePastedEmail('From: alice@example.com\nDate: Oct 5, 2026\nSubject: Interview invitation\n\nHello Forrest,\nSee you soon.'))
      .toEqual({ from: 'alice@example.com', date: '2026-10-05', subject: 'Interview invitation', body: 'Hello Forrest,\nSee you soon.' });
  });
  // [功能/Unicode] 票41:中文头(发件人/发送时间/主题)
  it('zh headers unicode', () => {
    expect(parsePastedEmail('发件人: 王小明 <wang@example.com>\n发送时间: 2026年10月13日\n主题: 面试邀请\n\n您好，请确认时间。'))
      .toEqual({ from: '王小明 <wang@example.com>', date: '2026-10-13', subject: '面试邀请', body: '您好，请确认时间。' });
  });
  // [boundary-无头形态] 票41:无头全文当正文
  it('no headers body only', () => {
    expect(parsePastedEmail('Just a plain note\nsecond line'))
      .toEqual({ from: '', date: '', subject: '', body: 'Just a plain note\nsecond line' });
  });
  // [boundary-空] 票41:空串全空返回
  it('empty string', () => {
    expect(parsePastedEmail('')).toEqual({ from: '', date: '', subject: '', body: '' });
  });
  // [boundary-只有头无正文] 票41
  it('headers only no body', () => {
    expect(parsePastedEmail('From: a@b.c\nSubject: Hi'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Hi', body: '' });
  });
  // [功能-折行] 票41:subject 折行续接
  it('folded subject', () => {
    expect(parsePastedEmail('From: a@b.c\nSubject: Long\n subject line\n\nBody'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Long subject line', body: 'Body' });
  });
  // [boundary-超长] 票41:20000 字正文不截断
  it('long body not truncated', () => {
    const longBody = 'x'.repeat(20000);
    expect(parsePastedEmail('From: a@b.c\n\n' + longBody).body.length).toBe(20000);
  });
  // [boundary-换行形态] 票41:CRLF 归一
  it('CRLF normalized', () => {
    expect(parsePastedEmail('From: a@b.c\r\nSubject: S\r\n\r\nB1\r\nB2'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'S', body: 'B1\nB2' });
  });
});

describe('parsePastedEmail(boundary 扩充,源码读证)', () => {
  // [boundary-头部形态] 扩充:Sent 是 date 的别名键(源码 lastKey 分支含 sent)
  it('Sent header alias', () => {
    expect(parsePastedEmail('From: a@b.c\nSent: Oct 5, 2026\nSubject: Hi\n\nBody'))
      .toEqual({ from: 'a@b.c', date: '2026-10-05', subject: 'Hi', body: 'Body' });
  });
  // [boundary-头部形态/Unicode] 扩充:发送日期别名 + 标题别名(源码键表含 发送日期/标题)
  it('zh alias keys 发送日期/标题', () => {
    expect(parsePastedEmail('发件人: a@b.c\n发送日期: 2026年10月13日\n标题: 你好\n\n正文'))
      .toEqual({ from: 'a@b.c', date: '2026-10-13', subject: '你好', body: '正文' });
  });
  // [boundary-头部形态/Unicode] 扩充:全角冒号同样命中(headerLine 含 [:：])
  it('fullwidth colon headers', () => {
    expect(parsePastedEmail('发件人：王小明 <wang@example.com>\n主题：面试邀请\n\n您好'))
      .toEqual({ from: '王小明 <wang@example.com>', date: '', subject: '面试邀请', body: '您好' });
  });
  // [boundary-头部形态] 扩充:未知头行被消费但不入字段,后续已知头仍识别
  it('unknown header consumed, known headers still parsed', () => {
    expect(parsePastedEmail('From: a@b.c\nX-Custom: zzz\nSubject: Hi\n\nBody'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Hi', body: 'Body' });
  });
  // [boundary-正文隔离] 扩充:正文里的 From: 行不回头污染头部(空行后即正文域)
  it('header-like line in body stays body', () => {
    expect(parsePastedEmail('From: a@b.c\nSubject: Hi\n\nFrom: impostor\nBody2'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Hi', body: 'From: impostor\nBody2' });
  });
  // [boundary-无空行分隔] 扩充:头后无空行、首个非头行即正文起
  it('body starts without blank separator', () => {
    expect(parsePastedEmail('From: a@b.c\nHello body'))
      .toEqual({ from: 'a@b.c', date: '', subject: '', body: 'Hello body' });
  });
  // [boundary-折行] 扩充:from 折行续接(源码 from 分支与 subject 对称)
  it('folded from', () => {
    expect(parsePastedEmail('From: Alice\n Smith\nSubject: Hi\n\nB'))
      .toEqual({ from: 'Alice Smith', date: '', subject: 'Hi', body: 'B' });
  });
  // [boundary-日期临界/票102] Date 头为日历不存在日期(平年 2/29)→ 经 normalizeEmailDate 归空,其余字段照常
  it('calendar-invalid date header normalizes to empty (票102)', () => {
    expect(parsePastedEmail('From: a@b.c\nDate: 2026-02-29\nSubject: Hi\n\nB'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Hi', body: 'B' });
  });
  // [boundary-日期临界/票102] Date 头为闰年 2/29 → 管线内正常归一,其余字段照常
  it('leap-year date header normalizes through pipeline (票102)', () => {
    expect(parsePastedEmail('From: a@b.c\nDate: Feb 29, 2024\nSubject: Hi\n\nB'))
      .toEqual({ from: 'a@b.c', date: '2024-02-29', subject: 'Hi', body: 'B' });
  });
  // [boundary-日期不可解析] 扩充:Date 头垃圾值经 normalizeEmailDate 归空,其余字段照常
  it('unparseable date header normalizes to empty', () => {
    expect(parsePastedEmail('From: a@b.c\nDate: not a date\nSubject: Hi\n\nB'))
      .toEqual({ from: 'a@b.c', date: '', subject: 'Hi', body: 'B' });
  });
  // [boundary-空] 扩充:纯空白输入全空(首行空白即终止头部扫描,回落全文 trim)
  it('whitespace only input', () => {
    expect(parsePastedEmail('   \n  ')).toEqual({ from: '', date: '', subject: '', body: '' });
  });
  // [boundary-超长±1] 扩充:超长 subject 原样保留(函数无截断语义)
  it('very long subject preserved', () => {
    const longSubject = 's'.repeat(10000);
    expect(parsePastedEmail('Subject: ' + longSubject + '\n\nB').subject).toBe(longSubject);
  });
  // [boundary-Unicode/特殊字符] 扩充:emoji + 中文主题原样
  it('unicode emoji subject verbatim', () => {
    expect(parsePastedEmail('Subject: Interview 🎉 面试\n\nB').subject).toBe('Interview 🎉 面试');
  });
  // [boundary-null] 扩充:签名为非空 string,传 null 现行抛 TypeError(特征锁定)
  it('null throws (non-nullable signature, locked)', () => {
    expect(() => parsePastedEmail(null as unknown as string)).toThrow(TypeError);
  });
  // [boundary-0与负数] 不适用:无数值入参(票面口径注明)
  // [boundary-文件类] 不适用:无文件入参
});
