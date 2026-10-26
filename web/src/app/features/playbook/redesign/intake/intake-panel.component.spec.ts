/**
 * 票64 ARC-08c 首批 spec —— intake 抽取器:extractInterviewWhen / adaptLocalOcrEmailText。
 *
 * 对照源:evidence-57/harness-57.mjs(8 例)与 evidence-60/harness-60.mjs
 * (31 例中 adapt/管线相关形态),期望值逐字守两票落盘口径;扩充 boundary 例
 * 均由现行源码逐行读证。特征测试锁现状,不改被测函数行为。
 *
 * 加载方式:本文件是真组件文件(值导入 @angular/* 与 API 服务),jest.config
 * 的 moduleNameMapper 已把这些依赖映射到 test/jest-stub.ts 替身;被测纯函数
 * 与其真实同文件/跨文件依赖(brief.helpers)保持真实加载,不走 TestBed。
 */
import { describe, expect, it } from '@jest/globals';
import { adaptLocalOcrEmailText, classifyEmail, extractFormat, extractInterviewWhen, extractInterviewerNames, isEmptyEmailHeaders, targetFromSelectValue, targetSelectValueOf } from './intake-panel.component';
import { parsePastedEmail } from '../../components/brief/brief.helpers';

/** 票59 服务对真实图的实际输出(evidence-59/sync-result.json 的 .text,逐字内嵌)。 */
const SVC_TEXT = "Forrest Lin : Design Interview for Full Stack developer position at Corpay(Toronto)\nforrestlin2024@gmail.com\nKhalil Musazade 2026-10-08 09:18\ne1\nK\n发至 Forrest Lin,Stan Shillis\n详情\n会议邀请\n2026-10-13 周二10:00~11:00\n你已接受本次会议邀请\nMicrosoft Teams Meeting\nHi Forrest,\nWe would like to invite you for Design Interview :\nDesign Session\nFor the technical design interview, we will focus on architectural design and system/product building.\nPlease create an account familiarize yourself with https://docs.google.com/drawings. You will visualize\nyour thoughts and design using block diagrams and rough outlines of your solution.\nPreparation Questions:\n• How do you iterate on the design?\n• How do you scale you system for an increased read load?\n• What are the security considerations for a website?\nBest Regards,\nKhalil\nMicrosoft Teams meeting\n<redacted-sensitive-line>\n<redacted-sensitive-line>\n<redacted-sensitive-line>\n<redacted-sensitive-line>\nNeed help? | System reference\nDial in by phone\n<redacted-sensitive-line>\nFind a local number\n<redacted-sensitive-line>\nFor organizers: Meeting options | Reset dial-in PIN";
/** 票60 落盘的样本1 adapt 合成全文(evidence-60/verify-after.txt NOTE 行,逐字)。 */
const SVC_ADAPTED = "Subject: Design Interview for Full Stack developer position at Corpay(Toronto)\nFrom: Khalil Musazade\nSent: 2026-10-08 09:18\n\ne1\nK\n发至 Forrest Lin,Stan Shillis\n详情\n会议邀请\n2026-10-13 周二10:00~11:00\n你已接受本次会议邀请\nMicrosoft Teams Meeting\nHi Forrest,\nWe would like to invite you for Design Interview :\nDesign Session\nFor the technical design interview, we will focus on architectural design and system/product building.\nPlease create an account familiarize yourself with https://docs.google.com/drawings. You will visualize\nyour thoughts and design using block diagrams and rough outlines of your solution.\nPreparation Questions:\n• How do you iterate on the design?\n• How do you scale you system for an increased read load?\n• What are the security considerations for a website?\nBest Regards,\nKhalil\nMicrosoft Teams meeting\n<redacted-sensitive-line>\n<redacted-sensitive-line>\n<redacted-sensitive-line>\n<redacted-sensitive-line>\nNeed help? | System reference\nDial in by phone\n<redacted-sensitive-line>\nFind a local number\n<redacted-sensitive-line>\nFor organizers: Meeting options | Reset dial-in PIN";
/** 票57/60 生产形态:Key:value 头 + transcription.txt 自「会议邀请」起全文(逐字)。 */
const PROD_SHAPE = "From: Khalil Musazade\nSent: 2026-10-08 09:18\nSubject: Design Interview for Full Stack developer position at Corpay(Toronto)\nTo: Forrest Lin, Stan Shillis\n\n会议邀请\n2026-10-13 周二 10:00～11:00\nMicrosoft Teams Meeting\n你已接受本次会议邀请\n\nHi Forrest,\n\nWe would like to invite you for Design Interview :\n\nDesign Session\n\nFor the technical design interview, we will focus on architectural design and system/product building.\nPlease create an account familiarize yourself with https://docs.google.com/drawings. You will visualize\nyour thoughts and design using block diagrams and rough outlines of your solution.\n\nPreparation Questions:\n\n  • How do you iterate on the design?\n  • How do you scale you system for an increased read load?\n  • What are the security considerations for a website?\n\nBest Regards,\nKhalii\n\nMicrosoft Teams meeting\n\nJoin: https://teams.microsoft.com/meet/21259607151058?\np=n5eDCoZ5QWQCAMZHXt\n\nMeeting ID: 212 596 071 510 58\nPasscode: AV9pG6hx\n\nNeed help? | System reference\n\nDial in by phone\n\n+1 213-204-2557,,463842651# United States, Los Angeles\nFind a local number\nPhone conference ID: 463 842 651#\n\nFor organizers: Meeting options | Reset dial-in PIN\n";


describe('extractInterviewWhen(票57 映射 7 例)', () => {
  // [功能-卡片格式] 票57:ISO+星期夹中间+全角波浪时段,只取起始时间
  it('card ISO with weekday and fullwidth tilde range', () => {
    expect(extractInterviewWhen('会议邀请\n2026-10-13 周二 10:00～11:00\nMicrosoft Teams Meeting'))
      .toBe('2026-10-13 10:00');
  });
  // [功能-卡片格式] 票57:年月日
  it('card zh date', () => {
    expect(extractInterviewWhen('面试时间:2026年10月13日 14:00,请提前参加')).toBe('2026-10-13 14:00');
  });
  // [功能-卡片格式] 票57:年月日+星期+全角波浪时段
  it('card zh date with weekday and range', () => {
    expect(extractInterviewWhen('2026年10月13日星期二 14:00～15:00')).toBe('2026-10-13 14:00');
  });
  // [功能] 票57:旧格式 ISO 空格
  it('legacy ISO space', () => {
    expect(extractInterviewWhen('Your interview is scheduled for 2026-10-07 11:00.'))
      .toBe('2026-10-07 11:00');
  });
  // [功能] 票57:旧格式英文月份 AM
  it('legacy english month AM', () => {
    expect(extractInterviewWhen('scheduled for Oct 7, 2026 at 11:00 AM')).toBe('2026-10-07 11:00');
  });
  // [功能] 票57:旧格式英文月份 PM 换算
  it('legacy english month PM converts', () => {
    expect(extractInterviewWhen('October 13, 2026 at 2:30 PM')).toBe('2026-10-13 14:30');
  });
  // [boundary-无时间] 票57:无时间返回 null
  it('no time returns null', () => {
    expect(extractInterviewWhen('We will be in touch soon.')).toBeNull();
  });
});

describe('extractInterviewWhen(生产形态 + boundary 扩充,源码读证)', () => {
  // [功能-生产形态] 票57:整段头+卡+正文经 parsePastedEmail 后,body 抽取不得
  // 误抽 Sent 的 2026-10-08 09:18,必须抽中会议卡 2026-10-13 10:00
  it('production shape body extraction avoids Sent time', () => {
    const p = parsePastedEmail(PROD_SHAPE);
    expect(p.subject).toBe('Design Interview for Full Stack developer position at Corpay(Toronto)');
    expect(p.date).toBe('2026-10-08');
    expect(p.body.includes('2026-10-13')).toBe(true);
    expect(extractInterviewWhen(p.body)).toBe('2026-10-13 10:00');
  });
  // [boundary-空] 扩充:空串无任何形态命中
  it('empty string returns null', () => {
    expect(extractInterviewWhen('')).toBeNull();
  });
  // [boundary-缺时间] 扩充:只有日期没有时间不命中(三分支都要求时间)
  it('date without time returns null', () => {
    expect(extractInterviewWhen('面试时间 2026-10-13,请留意后续通知')).toBeNull();
  });
  // [boundary-缺日期] 扩充:只有时间没有日期不命中
  it('time without date returns null', () => {
    expect(extractInterviewWhen('明天 10:00 见')).toBeNull();
  });
  // [boundary-Unicode] 扩充:纯中文无日期时间文本
  it('pure zh text returns null', () => {
    expect(extractInterviewWhen('周末去公园走了走,天气不错')).toBeNull();
  });
  // [boundary-全角标点] 扩充:时间冒号可全角([:：] 字符类,iso 分支)
  it('fullwidth colon in time', () => {
    expect(extractInterviewWhen('2026-10-13 周二 10：00')).toBe('2026-10-13 10:00');
  });
  // [boundary-分隔符] 扩充:ISO 斜杠分隔同样命中
  it('ISO slash separator', () => {
    expect(extractInterviewWhen('时间 2026/10/13 09:05')).toBe('2026-10-13 09:05');
  });
  // [boundary-分隔符] 扩充:ISO T 分隔同样命中
  it('ISO T separator', () => {
    expect(extractInterviewWhen('2026-10-13T10:00 开始')).toBe('2026-10-13 10:00');
  });
  // [boundary-12 小时临界] 扩充:12 AM 换算 00(en 分支 hh===12 && AM -> 0)
  it('12 AM converts to 00', () => {
    expect(extractInterviewWhen('Oct 7, 2026 at 12:00 AM')).toBe('2026-10-07 00:00');
  });
  // [boundary-12 小时临界] 扩充:12 PM 保持 12
  it('12 PM stays 12', () => {
    expect(extractInterviewWhen('Oct 7, 2026 at 12:30 PM')).toBe('2026-10-07 12:30');
  });
  // [boundary-分支优先级] 扩充:年月日分支先于 ISO 分支求值(源码顺序)
  it('zh branch wins over ISO when both present', () => {
    expect(extractInterviewWhen('2026年10月13日 14:00,另有 2026-10-07 11:00')).toBe('2026-10-13 14:00');
  });
  // [boundary-超长] 扩充:超长前缀不影响全文扫描命中
  it('very long prefix still finds meeting time', () => {
    expect(extractInterviewWhen('x'.repeat(5000) + '\n2026-10-13 周二 10:00～11:00'))
      .toBe('2026-10-13 10:00');
  });
  // [boundary-null] 扩充:签名为非空 string,传 null 现行抛 TypeError(特征锁定)
  it('null throws (non-nullable signature, locked)', () => {
    expect(() => extractInterviewWhen(null as unknown as string)).toThrow(TypeError);
  });
  // [boundary-0与负数/文件类] 不适用:无数值/文件入参(票面口径注明)
});

describe('extractInterviewWhen(票104 日历合法性 boundary)', () => {
  // 票104:三路(cn/iso/en)抽取结果产出前过日历合法性校验(同源复用票102
  // isValidCalendarDate:月 1-12 / 日 ≤ 当月实际天数 / 闰年百年 400 规则),
  // 非法 → 回落 null(该函数既有无匹配口径),走既有「无时间」路径。
  // 注:三路年份正则均为 20\d{2}(2000-2099),百年负例(1900/2100)经正则
  // 本就不可达;百年规则以 2000 年(被 400 整除→闰)正例 + 共享辅助同源守恒。
  // [boundary-非法/cn] 平年 2/29 不存在
  it('cn non-leap Feb 29 returns null', () => {
    expect(extractInterviewWhen('面试时间:2026年2月29日 10:00')).toBeNull();
  });
  // [boundary-非法/cn] 4 月只有 30 天
  it('cn Apr 31 returns null', () => {
    expect(extractInterviewWhen('面试时间:2026年4月31日 10:00')).toBeNull();
  });
  // [boundary-非法/cn] 13 月不存在
  it('cn month 13 returns null', () => {
    expect(extractInterviewWhen('面试时间:2026年13月1日 10:00')).toBeNull();
  });
  // [boundary-非法/iso] 票面主例:2026 非闰年,2 月无 29 日
  it('iso 2026-02-29 returns null', () => {
    expect(extractInterviewWhen('Your interview is scheduled for 2026-02-29 10:00.')).toBeNull();
  });
  // [boundary-非法/iso] 4/31
  it('iso Apr 31 returns null', () => {
    expect(extractInterviewWhen('2026-04-31 10:00 开始')).toBeNull();
  });
  // [boundary-非法/iso] 13 月
  it('iso month 13 returns null', () => {
    expect(extractInterviewWhen('2026-13-01 10:00 开始')).toBeNull();
  });
  // [boundary-非法/iso] 斜杠分隔路同样校验
  it('iso slash non-leap Feb 29 returns null', () => {
    expect(extractInterviewWhen('时间 2026/02/29 10:00')).toBeNull();
  });
  // [boundary-非法/iso] 月 00
  it('iso month 00 returns null', () => {
    expect(extractInterviewWhen('2026-00-10 10:00 开始')).toBeNull();
  });
  // [boundary-非法/iso] 日 00
  it('iso day 00 returns null', () => {
    expect(extractInterviewWhen('2026-01-00 10:00 开始')).toBeNull();
  });
  // [boundary-非法/en] 平年 2/29
  it('en Feb 29 2026 returns null', () => {
    expect(extractInterviewWhen('scheduled for Feb 29, 2026 at 10:00 AM')).toBeNull();
  });
  // [boundary-非法/en] 4/31
  it('en Apr 31 returns null', () => {
    expect(extractInterviewWhen('scheduled for Apr 31, 2026 at 10:00 AM')).toBeNull();
  });
  // [合法守恒/cn] 闰年 2/29
  it('cn leap Feb 29 extracts', () => {
    expect(extractInterviewWhen('面试时间:2028年2月29日 10:00')).toBe('2028-02-29 10:00');
  });
  // [合法守恒/iso] 闰年 2/29
  it('iso leap Feb 29 extracts', () => {
    expect(extractInterviewWhen('Your interview is scheduled for 2028-02-29 10:00.'))
      .toBe('2028-02-29 10:00');
  });
  // [合法守恒/en] 闰年 2/29
  it('en leap Feb 29 extracts', () => {
    expect(extractInterviewWhen('scheduled for Feb 29, 2028 at 10:00 AM')).toBe('2028-02-29 10:00');
  });
  // [合法守恒/iso-百年规则] 2000 被 400 整除 → 闰年,2/29 合法
  it('iso year-2000 Feb 29 extracts (400-year leap rule)', () => {
    expect(extractInterviewWhen('2000-02-29 10:00 开始')).toBe('2000-02-29 10:00');
  });
  // [合法守恒/cn-百年规则] 同上,cn 路
  it('cn year-2000 Feb 29 extracts (400-year leap rule)', () => {
    expect(extractInterviewWhen('面试时间:2000年2月29日 10:00')).toBe('2000-02-29 10:00');
  });
  // [合法守恒/en-百年规则] 同上,en 路
  it('en year-2000 Feb 29 extracts (400-year leap rule)', () => {
    expect(extractInterviewWhen('scheduled for Feb 29, 2000 at 10:00 AM')).toBe('2000-02-29 10:00');
  });
  // [合法守恒/iso] 平年 2/29 的次日(票面活体合法例)
  it('iso 2026-03-01 extracts', () => {
    expect(extractInterviewWhen('2026-03-01 10:00 开始')).toBe('2026-03-01 10:00');
  });
  // [合法守恒/iso] 平年 2/28 月末
  it('iso 2026-02-28 extracts', () => {
    expect(extractInterviewWhen('2026-02-28 10:00 开始')).toBe('2026-02-28 10:00');
  });
  // [合法守恒/cn] 4/30 月末
  it('cn Apr 30 extracts', () => {
    expect(extractInterviewWhen('面试时间:2026年4月30日 10:00')).toBe('2026-04-30 10:00');
  });
  // [合法守恒/en] 4/30 + PM 换算逐字守恒
  it('en Apr 30 PM extracts', () => {
    expect(extractInterviewWhen('April 30, 2026 at 2:30 PM')).toBe('2026-04-30 14:30');
  });
});

describe('extractInterviewWhen(票158 day-first + 无时间回落)', () => {
  // 票158 用户原句:day-first 且只有日期没有时间,旧三路全 miss 致 fWhen 为空、
  // Belongs to 回落 Communications;修后当抽日期-only,分类仍为 invitation
  // (format 由未导出的 extractFormat 映射,/teams/i → '线上视频',本票不改其
  // 声明,故此处不断言,见实装报告源码确认)
  it('user sentence day-first date-only extracts date and invitation', () => {
    const s = 'I recived a email that I will take part in the screening interview via teams on 26 oct 2026';
    expect(extractInterviewWhen(s)).toBe('2026-10-26');
    expect(classifyEmail('', s)).toBe('invitation');
  });
  // [旧行为守恒] month-first 带时间仍走旧 en 路,值一字不动
  it('month-first with time unchanged', () => {
    expect(extractInterviewWhen('scheduled for Oct 26, 2026 at 11:00 AM')).toBe('2026-10-26 11:00');
  });
  // [变体] day-first 裸日期
  it('day-first bare date', () => {
    expect(extractInterviewWhen('interview on 26 Oct 2026, see you then')).toBe('2026-10-26');
  });
  // [变体] day-first 序数词 + 逗号、月份全大写
  it('day-first ordinal comma and uppercase month', () => {
    expect(extractInterviewWhen('on 26th Oct, 2026 please join')).toBe('2026-10-26');
    expect(extractInterviewWhen('on 26 OCT 2026 please join')).toBe('2026-10-26');
  });
  // [变体] month-first 无时间(序数词 + 逗号 + 全写月份)
  it('month-first date-only with ordinal and full month', () => {
    expect(extractInterviewWhen('scheduled October 26th, 2026, details to follow')).toBe('2026-10-26');
  });
  // [变体] day-first 带时间(含 PM 换算与全写月份无 AM/PM)
  it('day-first with time keeps time and converts PM', () => {
    expect(extractInterviewWhen('interview on 26 oct 2026 at 11:00 AM')).toBe('2026-10-26 11:00');
    expect(extractInterviewWhen('interview on 26 oct 2026 at 2:30 PM')).toBe('2026-10-26 14:30');
    expect(extractInterviewWhen('interview 26th October 2026 14:30')).toBe('2026-10-26 14:30');
  });
  // [优先级] 既有带时间路在先:全文先出现 month-first 带时间时,后出的
  // day-first 裸日期不抢
  it('existing timed route wins over new date-only route', () => {
    expect(extractInterviewWhen('Oct 30, 2026 at 10:00 AM, also mentioned 26 oct 2026'))
      .toBe('2026-10-30 10:00');
  });
  // [非法守恒] 新增路同样过日历合法性校验,非法即 null
  it('new routes reject invalid calendar dates', () => {
    expect(extractInterviewWhen('interview on 29 Feb 2026')).toBeNull();
    expect(extractInterviewWhen('scheduled Feb 29, 2026, to be confirmed')).toBeNull();
    expect(extractInterviewWhen('interview on 31 Apr 2026')).toBeNull();
  });
  // [范围边界] ISO 日期-only 仍为 null(既有用例锁定,本票不扩 CN/ISO 日期-only)
  it('ISO date-only still returns null', () => {
    expect(extractInterviewWhen('面试时间 2026-10-13,请留意后续通知')).toBeNull();
  });
});

describe('adaptLocalOcrEmailText(票60 形态集)', () => {
  // [形态-样本1] 票60:真实 OCR 文本 -> 合成头全文逐字(窗口标题前缀已剥、
  // 发件人行切 From/Sent、会议卡留在 body、发信时间不入 body)
  it('sample1 synthesizes exact header block', () => {
    expect(adaptLocalOcrEmailText(SVC_TEXT)).toBe(SVC_ADAPTED);
  });
  // [形态-样本2 粘连] 票60:发件人行姓名与日期时间无空格粘连,切开结果与
  // 样本1 逐字相同(from=Khalil Musazade / Sent=2026-10-08 09:18)
  it('sample2 glued sender line splits identically', () => {
    const glued = SVC_TEXT.replace(
      'Khalil Musazade 2026-10-08 09:18', 'Khalil Musazade2026-10-0809:18');
    expect(glued.includes('Khalil Musazade2026-10-0809:18')).toBe(true);
    expect(adaptLocalOcrEmailText(glued)).toBe(SVC_ADAPTED);
  });
  // [形态-不硬造头] 票60:无日期时间行的散文本返回 null
  it('prose without datetime returns null', () => {
    expect(adaptLocalOcrEmailText('周末去公园走了走\n天气不错，拍了几张照片\n回来路上买了点水果\n明天记得带伞'))
      .toBeNull();
  });
  // [形态-不硬造头] 票60:只有会议卡行(以日期开头)不冒充发件人行,返回 null
  it('meeting-card-only returns null', () => {
    expect(adaptLocalOcrEmailText('会议邀请提醒\n2026-10-13 周二10:00~11:00\n请提前十分钟加入会议'))
      .toBeNull();
  });
});

describe('adaptLocalOcrEmailText(boundary 扩充,源码读证)', () => {
  // [boundary-空] 扩充:空串无首个非空行
  it('empty string returns null', () => {
    expect(adaptLocalOcrEmailText('')).toBeNull();
  });
  // [boundary-空] 扩充:纯空白同上
  it('whitespace only returns null', () => {
    expect(adaptLocalOcrEmailText('  \n \n')).toBeNull();
  });
  // [boundary-窗口±1] 扩充:发件人行在主题后第 6 行(窗口内最后一行)命中
  it('sender line at window edge (index 6) adapts', () => {
    const raw = ['Subj', 'f1', 'f2', 'f3', 'f4', 'f5', 'Bob 2026-10-08 09:18', 'Hello'].join('\n');
    expect(adaptLocalOcrEmailText(raw)).toBe('Subject: Subj\nFrom: Bob\nSent: 2026-10-08 09:18\n\nHello');
  });
  // [boundary-窗口±1] 扩充:发件人行在主题后第 7 行(窗口外)不命中,不硬造
  it('sender line beyond window (index 7) returns null', () => {
    const raw = ['Subj', 'f1', 'f2', 'f3', 'f4', 'f5', 'f6', 'Bob 2026-10-08 09:18', 'Hello'].join('\n');
    expect(adaptLocalOcrEmailText(raw)).toBeNull();
  });
  // [boundary-形态] 扩充:候选行只有日期没有时间,不算发件人行
  it('date-only line is not a sender line', () => {
    expect(adaptLocalOcrEmailText('Subj\nBob 2026-10-08\nHello')).toBeNull();
  });
  // [boundary-分隔符] 扩充:发件人行斜杠日期被归一进 Sent
  it('slash date sender line normalizes', () => {
    expect(adaptLocalOcrEmailText('Subj\nBob 2026/10/08 09:18\nHello'))
      .toBe('Subject: Subj\nFrom: Bob\nSent: 2026-10-08 09:18\n\nHello');
  });
  // [boundary-跳过会议卡行] 扩充:发件人行之前以日期开头的行被跳过(不作 from),
  // 且不进入合成 body(body 取发件人行之后原文——源码口径,特征锁定)
  it('meeting line before sender is skipped', () => {
    const raw = ['Subj', '2026-10-13 周二10:00~11:00', 'Bob 2026-10-08 09:18', 'Body'].join('\n');
    expect(adaptLocalOcrEmailText(raw)).toBe('Subject: Subj\nFrom: Bob\nSent: 2026-10-08 09:18\n\nBody');
  });
  // [boundary-窗口标题前缀] 扩充:全角冒号型窗口标题前缀同样剥离
  it('fullwidth-colon window title prefix stripped', () => {
    const raw = ['Forrest Lin ： Interview Plan', 'Bob 2026-10-08 09:18', 'Hello'].join('\n');
    expect(adaptLocalOcrEmailText(raw))
      .toBe('Subject: Interview Plan\nFrom: Bob\nSent: 2026-10-08 09:18\n\nHello');
  });
  // [boundary-null] 扩充:签名为非空 string,传 null 现行抛 TypeError(特征锁定)
  it('null throws (non-nullable signature, locked)', () => {
    expect(() => adaptLocalOcrEmailText(null as unknown as string)).toThrow(TypeError);
  });
  // [boundary-0与负数/文件类] 不适用:无数值/文件入参
});

describe('票60 管线口径(适配门复刻,与 harness-60 cardFromText 同口径)', () => {
  // 适配门:本地 OCR 文本且 parsePastedEmail 无头命中时才过 adapt 再走同一管线。
  function viaGate(raw: string): ReturnType<typeof parsePastedEmail> {
    let p = parsePastedEmail(raw);
    if (!p.from && !p.date && !p.subject) {
      const adapted = adaptLocalOcrEmailText(raw);
      if (adapted) p = parsePastedEmail(adapted);
    }
    return p;
  }
  // [管线-样本1] 票60:成卡三件套 + when 抽会议卡而非发信时间
  it('sample1 through gate yields header fields and meeting when', () => {
    const p = viaGate(SVC_TEXT);
    expect(p.subject).toBe('Design Interview for Full Stack developer position at Corpay(Toronto)');
    expect(p.from).toBe('Khalil Musazade');
    expect(p.date).toBe('2026-10-08');
    expect(extractInterviewWhen(p.body)).toBe('2026-10-13 10:00');
  });
  // [管线-对照组] 票60:带 Key:value 头的文本不经适配(parse 已命中头),
  // 字段与直接 parse 逐项一致,适配无副作用
  it('headed text bypasses adapt with identical fields', () => {
    const direct = parsePastedEmail(PROD_SHAPE);
    const gated = viaGate(PROD_SHAPE);
    expect(gated).toEqual(direct);
    expect(gated.subject).toBe('Design Interview for Full Stack developer position at Corpay(Toronto)');
    expect(gated.from).toBe('Khalil Musazade');
    expect(gated.date).toBe('2026-10-08');
  });
  // [管线-不硬造头] 票60:散文本经门后仍全空(failed 口径)
  it('prose through gate stays empty', () => {
    const p = viaGate('周末去公园走了走\n天气不错，拍了几张照片\n回来路上买了点水果\n明天记得带伞');
    expect(p).toEqual({ from: '', date: '', subject: '', body: '周末去公园走了走\n天气不错，拍了几张照片\n回来路上买了点水果\n明天记得带伞' });
  });
  // [管线-不硬造头] 票60:仅会议卡经门后仍全空(会议时间不冒充发信时间);
  // 但 extractInterviewWhen 本身能抽中会议时间——成卡失败靠的是头部缺失,不是 when
  it('meeting-only through gate stays empty while when extracts', () => {
    const raw = '会议邀请提醒\n2026-10-13 周二10:00~11:00\n请提前十分钟加入会议';
    const p = viaGate(raw);
    expect(p.from).toBe('');
    expect(p.date).toBe('');
    expect(p.subject).toBe('');
    expect(extractInterviewWhen(raw)).toBe('2026-10-13 10:00');
  });
});

describe('adaptLocalOcrEmailText(票160 发件人行在先版式,全合成文本)', () => {
  // [版式-发件人行在先] 用户报障版式:首行即发件人行(姓名 + 日期时间同行),
  // 其后收件人行、中文主题行、Teams 会议卡行、英文正文。旧口径下主题会被
  // 取成发件人行、之后 6 行内再无发件人行 → null;新口径须出 From/Date/Subject。
  const SENDER_FIRST = [
    'Alex Morgan 2026-09-15 10:20',
    '发至 Sam Lee、Jamie Chen',
    '会议邀请',
    '2026-10-06 周二 15:00～16:00',
    'Microsoft Teams Meeting',
    'Hi Jamie,',
    'We would like to invite you to interview for our Full Stack Developer role.',
    'Best Regards,',
    'Alex'
  ].join('\n');
  const SENDER_FIRST_ADAPTED = [
    'Subject: 会议邀请',
    'From: Alex Morgan',
    'Sent: 2026-09-15 10:20',
    '',
    '2026-10-06 周二 15:00～16:00',
    'Microsoft Teams Meeting',
    'Hi Jamie,',
    'We would like to invite you to interview for our Full Stack Developer role.',
    'Best Regards,',
    'Alex'
  ].join('\n');
  it('sender-first layout adapts with recipient line skipped', () => {
    expect(adaptLocalOcrEmailText(SENDER_FIRST)).toBe(SENDER_FIRST_ADAPTED);
  });
  // [版式-发件人行星期夹] 发件人行日期与时间之间夹星期,同样切开
  it('sender line with weekday between date and time adapts', () => {
    const raw = 'Alex Morgan 2026-09-15 周二 10:20\n发至 Sam Lee\n会议邀请\nHi Jamie,';
    expect(adaptLocalOcrEmailText(raw))
      .toBe('Subject: 会议邀请\nFrom: Alex Morgan\nSent: 2026-09-15 10:20\n\nHi Jamie,');
  });
  // [版式-发件人行粘连] 发件人行姓名与日期时间粘连,同票60 样本2 口径
  it('sender-first glued sender line splits identically', () => {
    const raw = 'Alex Morgan2026-09-1510:20\n发至 Sam Lee\n会议邀请\nHi Jamie,';
    expect(adaptLocalOcrEmailText(raw))
      .toBe('Subject: 会议邀请\nFrom: Alex Morgan\nSent: 2026-09-15 10:20\n\nHi Jamie,');
  });
  // [版式-不硬造头] 发件人行在先但其后只有收件人/会议卡/平台行、无主题行 → null
  it('sender-first without subject candidate returns null', () => {
    const raw = [
      'Alex Morgan 2026-09-15 10:20',
      '发至 Sam Lee',
      '2026-10-06 周二 15:00～16:00',
      'Microsoft Teams Meeting'
    ].join('\n');
    expect(adaptLocalOcrEmailText(raw)).toBeNull();
  });
  // [版式-旧口径守恒] 票60 样本1(主题行在先)仍逐字同前,新路只在首行即发件人行时触发
  it('subject-first sample1 still adapts identically', () => {
    expect(adaptLocalOcrEmailText(SVC_TEXT)).toBe(SVC_ADAPTED);
  });
});

describe('票160 管线口径(视觉纯转写同样过适配,门与 cardFromText 同口径)', () => {
  // 票160 起 cardFromText 的适配门不再分来源:viaGate 本就无来源条件,
  // 此处锁定门行为——视觉纯转写(无 Key:value 头)经门后同样出三件套。
  // cardFromText 为组件私有方法,门逻辑由 viaGate 复刻锁定(与票60 同做法)。
  function viaGate160(raw: string): ReturnType<typeof parsePastedEmail> {
    let p = parsePastedEmail(raw);
    if (!p.from && !p.date && !p.subject) {
      const adapted = adaptLocalOcrEmailText(raw);
      if (adapted) p = parsePastedEmail(adapted);
    }
    return p;
  }
  const VISION_TEXT = [
    'Alex Morgan 2026-09-15 10:20',
    '发至 Sam Lee、Jamie Chen',
    '会议邀请',
    '2026-10-06 周二 15:00～16:00',
    'Microsoft Teams Meeting',
    'Hi Jamie,',
    'We would like to invite you to interview for our Full Stack Developer role.'
  ].join('\n');
  it('vision transcription without headers yields header fields and meeting when', () => {
    const p = viaGate160(VISION_TEXT);
    expect(p.subject).toBe('会议邀请');
    expect(p.from).toBe('Alex Morgan');
    expect(p.date).toBe('2026-09-15');
    // 会议卡落在 body 内,when 抽会议卡时间而非发信时间(ISO + 星期 + 时段口径已在票57 锁定)
    expect(extractInterviewWhen(p.body)).toBe('2026-10-06 15:00');
  });
  // [管线-不硬造头] 散文本经门后仍全空(失败卡 + ocrNoEmail 口径,非成卡)
  it('prose through gate stays empty', () => {
    const p = viaGate160('周末去公园走了走\n天气不错，拍了几张照片\n回来路上买了点水果\n明天记得带伞');
    expect(p.from).toBe('');
    expect(p.date).toBe('');
    expect(p.subject).toBe('');
  });
});
describe('票161 正文-only 粘贴 + 会议卡单数字日期 + 归属下拉 + 空头保存守卫', () => {
  // 合成正文(零真实凭据):邮箱客户端复制正文时头行本来就不在复制内容里,
  // 与用户 12:17 截图的粘贴形态一致——无头行、无日期时间、无面试官姓名。
  const BODY_ONLY = [
    'Hi Forrest,',
    'We would like to invite you to interview for our Full Stack Developer role.',
    'Technical Screening / Live Coding Session',
    'During the technical interview, you will work through live coding questions in the programming language of your choice.',
    'Could you please create an account on JDoodle ahead of time, as there will be live coding questions during the interview.',
    'Backup IDE (Recommended)',
    'Please review our website to learn more about our business and our services.',
    'Kindly confirm receipt of this email.',
    'Best of luck!',
    'Alex',
  ].join('\n');

  // [头行缺失不编造] 正文-only 经 parsePastedEmail 后三件套全空、全文作 body;
  // 输入里有的才提出来:分类 invitation、形式线上做题平台;面试官留空
  // (落款名不是面试官信号,保守抽取不认,用户在预览里手动补)。
  it('body-only paste yields empty headers but invitation and platform', () => {
    const p = parsePastedEmail(BODY_ONLY);
    expect(p.from).toBe('');
    expect(p.date).toBe('');
    expect(p.subject).toBe('');
    expect(p.body).toBe(BODY_ONLY);
    expect(classifyEmail(p.subject, p.body)).toBe('invitation');
    expect(extractFormat(p.body)).toBe('线上做题平台');
    expect(extractInterviewerNames(p.body)).toEqual([]);
  });

  // [正文无时间不编造] 正文-only 里没有任何日期时间,when 必须为 null
  // (客户端会议卡的日期时间不在复制文本里,不能凭空补)。
  it('body-only paste without any datetime yields null when', () => {
    const p = parsePastedEmail(BODY_ONLY);
    expect(extractInterviewWhen(p.body)).toBeNull();
  });

  // [会议卡单数字日期] ISO 路已覆盖:单数字月/日 + 星期 + ～ 时段,只取起始时间。
  it('meeting-card single-digit date with weekday and range extracts start', () => {
    expect(extractInterviewWhen('会议邀请\n2026-10-1 周四 13:00～14:00\nMicrosoft Teams Meeting'))
      .toBe('2026-10-01 13:00');
    expect(extractInterviewWhen('2026-10-1 13:00 开始')).toBe('2026-10-01 13:00');
  });

  // [会议卡年月日版] CN 路已覆盖:单数字月/日 + 星期 + ～ 时段。
  it('meeting-card Chinese single-digit date extracts start', () => {
    expect(extractInterviewWhen('2026年10月1日 周四 13:00～14:00')).toBe('2026-10-01 13:00');
  });

  // [范围边界] 无年份的会议卡不识别——年份不能凭空按当年补。
  it('year-less meeting card stays null', () => {
    expect(extractInterviewWhen('10月1日 周四 13:00～14:00')).toBeNull();
  });

  // [空头保存守卫] 三件套全空(仅空白亦算空)才拦;任一有值即放行。
  it('empty email headers gate blocks only all-empty', () => {
    expect(isEmptyEmailHeaders('', '', '')).toBe(true);
    expect(isEmptyEmailHeaders('  ', '', '')).toBe(true);
    expect(isEmptyEmailHeaders('Interview', '', '')).toBe(false);
    expect(isEmptyEmailHeaders('', 'Alex', '')).toBe(false);
    expect(isEmptyEmailHeaders('', '', '2026-10-01')).toBe(false);
  });

  // [归属下拉映射] 值与单选旧文本对应:轮次 round:<id>、沟通 comm、新建 new;
  // 往返一致;非法值解析 null(调用方忽略,保持当前归属不动)。
  it('target select value round-trips', () => {
    expect(targetSelectValueOf({ kind: 'round', roundId: 'r1' })).toBe('round:r1');
    expect(targetSelectValueOf({ kind: 'comm' })).toBe('comm');
    expect(targetSelectValueOf({ kind: 'new' })).toBe('new');
    expect(targetFromSelectValue('round:r1')).toEqual({ kind: 'round', roundId: 'r1' });
    expect(targetFromSelectValue('comm')).toEqual({ kind: 'comm' });
    expect(targetFromSelectValue('new')).toEqual({ kind: 'new' });
    expect(targetFromSelectValue('round:')).toBeNull();
    expect(targetFromSelectValue('bogus')).toBeNull();
    expect(targetFromSelectValue('')).toBeNull();
  });
});
