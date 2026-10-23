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
import { adaptLocalOcrEmailText, extractInterviewWhen } from './intake-panel.component';
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
