/**
 * 票165：邮件正文分段 helper 单测（定稿 §2.3 防蒙混三例 + 行式覆盖）。
 * 全部夹具均为合成文本（张三/李四 + 合成日期），零真实邮件内容、零真实会议凭据。
 */
import { describe, expect, it } from '@jest/globals';
import { normalizeEmailBodyText, segmentEmailBody } from './email-body-segments';

describe('segmentEmailBody', () => {
  it('防蒙混1：全切分成功——多消息 + 会议卡，时间线节点齐全', () => {
    const text = [
      'From: 张三',
      'Sent: 2026-10-20 09:00',
      'Hi, invite text line.',
      '李四 2026-10-20 10:30',
      'Reply text line.',
      '2026-10-21 周三 14:00～15:00',
      'Microsoft Teams meeting',
      '线上会议室（合成示例）',
    ].join('\n');
    const r = segmentEmailBody(text);
    expect(r.fallback).toBe(false);
    expect(r.timeline.map(s => [s.dateTime, s.sender, s.meetingCard])).toEqual([
      ['2026-10-20 09:00', '张三', false],
      ['2026-10-20 10:30', '李四', false],
      ['2026-10-21 14:00', null, true],
    ]);
    expect(r.timeline[0].body).toBe('Hi, invite text line.');
    expect(r.timeline[1].body).toBe('Reply text line.');
    expect(r.timeline[2].body).toBe(
      '2026-10-21 周三 14:00～15:00\nMicrosoft Teams meeting\n线上会议室（合成示例）');
    expect(r.remainder).toEqual([]);
  });

  it('防蒙混2：中间一段不可切分——时间线照常，余段块附后', () => {
    const text = [
      '张三 2026-10-20 09:00',
      'First message.',
      'From: 李四',
      'Untimed message body.',
      '王五 2026-10-20 11:00',
      'Third message.',
    ].join('\n');
    const r = segmentEmailBody(text);
    expect(r.fallback).toBe(false);
    expect(r.timeline.map(s => [s.dateTime, s.sender])).toEqual([
      ['2026-10-20 09:00', '张三'],
      ['2026-10-20 11:00', '王五'],
    ]);
    expect(r.remainder.map(s => s.body)).toEqual(['Untimed message body.']);
  });

  it('防蒙混3：全不可切分——整段原文、无时间线', () => {
    const text = 'Hi Forrest,\nJust a plain note without any headers.\nBest regards';
    const r = segmentEmailBody(text);
    expect(r.fallback).toBe(true);
    expect(r.timeline).toEqual([]);
    expect(r.remainder).toEqual([]);
  });

  it('引用头行 On wrote 与中文写道行均认作新消息', () => {
    const r1 = segmentEmailBody('On 2026-10-21 09:00, 张三 wrote:\nQuoted reply.');
    expect(r1.fallback).toBe(false);
    expect(r1.timeline.map(s => [s.dateTime, s.sender])).toEqual([['2026-10-21 09:00', '张三']]);
    expect(r1.timeline[0].body).toBe('Quoted reply.');

    const r2 = segmentEmailBody('在 2026-10-21 09:00，张三写道：\n中文回复。');
    expect(r2.fallback).toBe(false);
    expect(r2.timeline.map(s => [s.dateTime, s.sender])).toEqual([['2026-10-21 09:00', '张三']]);
  });

  it('斜杠日期与一位数月日时均归一化为 YYYY-MM-DD HH:mm', () => {
    const r1 = segmentEmailBody('张三 2026/10/20 09:00\nSlash date message.');
    expect(r1.timeline[0].dateTime).toBe('2026-10-20 09:00');
    const r2 = segmentEmailBody('张三 2026-10-5 9:00\nOne digit.');
    expect(r2.timeline[0].dateTime).toBe('2026-10-05 09:00');
  });

  it('无年份行不认作时间，首段余文作余段附后', () => {
    const text = '10月5日 9:00\nNo year line.\n张三 2026-10-20 09:00\nReal message.';
    const r = segmentEmailBody(text);
    expect(r.fallback).toBe(false);
    expect(r.timeline.map(s => s.sender)).toEqual(['张三']);
    expect(r.remainder.map(s => s.body)).toEqual(['10月5日 9:00\nNo year line.']);
  });

  it('非法日历日期不认作边界，整段回落', () => {
    const r = segmentEmailBody('张三 2026-02-30 09:00\nInvalid date.');
    expect(r.fallback).toBe(true);
  });

  it('同时间按原文先后稳定排序，异时间按时间升序（与原文顺序无关）', () => {
    const same = segmentEmailBody('张三 2026-10-20 09:00\nFirst.\n李四 2026-10-20 09:00\nSecond.');
    expect(same.timeline.map(s => s.sender)).toEqual(['张三', '李四']);
    const sorted = segmentEmailBody('李四 2026-10-20 11:00\nLater first in text.\n张三 2026-10-20 09:00\nEarlier second in text.');
    expect(sorted.timeline.map(s => s.sender)).toEqual(['张三', '李四']);
  });

  it('From 行尾随时间拆出时间；裸邮箱发件人无时间时作余段', () => {
    const r1 = segmentEmailBody('From: 张三 2026-10-20 09:00\nTrailing time.');
    expect(r1.timeline.map(s => [s.dateTime, s.sender])).toEqual([['2026-10-20 09:00', '张三']]);
    const r2 = segmentEmailBody('From: zhangsan@example.com\nEmail sender.\n李四 2026-10-20 10:30\nTimed.');
    expect(r2.timeline.map(s => s.sender)).toEqual(['李四']);
    expect(r2.remainder.map(s => s.body)).toEqual(['Email sender.']);
  });

  it('Sent 行不紧邻 From 行时不附着时间，整段回落', () => {
    const r = segmentEmailBody('From: 张三\n\nSent: 2026-10-20 09:00\nBody.');
    expect(r.fallback).toBe(true);
  });

  it('会议卡块无时间时不成节点，行文留在消息正文内', () => {
    const text = '张三 2026-10-20 09:00\nMicrosoft Teams meeting\nNo time in this block.';
    const r = segmentEmailBody(text);
    expect(r.fallback).toBe(false);
    expect(r.timeline).toHaveLength(1);
    expect(r.timeline[0].meetingCard).toBe(false);
    expect(r.timeline[0].body).toBe('Microsoft Teams meeting\nNo time in this block.');
  });

  it('会议卡时间行在平台行之前时同属一卡，取起始时间；卡前正文不吞入卡内', () => {
    const text = '张三 2026-10-20 09:00\nSome body text.\n2026-10-21 周三 14:00～15:00\nMicrosoft Teams meeting';
    const r = segmentEmailBody(text);
    expect(r.timeline.map(s => [s.dateTime, s.meetingCard])).toEqual([
      ['2026-10-20 09:00', false],
      ['2026-10-21 14:00', true],
    ]);
    expect(r.timeline[0].body).toBe('Some body text.');
    expect(r.timeline[1].body).toBe('2026-10-21 周三 14:00～15:00\nMicrosoft Teams meeting');
  });

  it('空文本与纯空白整段回落', () => {
    expect(segmentEmailBody('').fallback).toBe(true);
    expect(segmentEmailBody('   \n  ').fallback).toBe(true);
  });
});

describe('normalizeEmailBodyText（票217：纯空白不得渲染不可见空块）', () => {
  it('null / 空串 / 纯空白字符一律归一为空串（模板落入 noBody 可见态）', () => {
    expect(normalizeEmailBodyText(null)).toBe('');
    expect(normalizeEmailBodyText(undefined)).toBe('');
    expect(normalizeEmailBodyText('')).toBe('');
    expect(normalizeEmailBodyText('   \n\t\n  ')).toBe('');
  });

  it('非空文本原样返回（不改写原文，只拦纯空白）', () => {
    expect(normalizeEmailBodyText('  Hi, invite text.\n')).toBe('  Hi, invite text.\n');
    expect(normalizeEmailBodyText('From: 张三\n正文行。')).toBe('From: 张三\n正文行。');
  });

  it('机理钉死：纯空白经分段必走 fallback 全文分支（未归一时模板 <pre> 只出不可见空白）', () => {
    const r = segmentEmailBody('   \n  ');
    expect(r.fallback).toBe(true);
    expect(r.timeline).toEqual([]);
    expect(r.remainder).toEqual([]);
  });
});
