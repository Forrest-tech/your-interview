import { PrepQuestionItem, PrepReport, PrepSection, prepStdText } from '../v2.models';
import { ExportBlock } from '../export/export-doc';

/**
 * 票133:轮次分类(规格 §4.2)—— 服务端 PrepReportClassifier 的前端镜像,只读 Stage 自由文本。
 * 仅用于未生成报告时的 fw 骨架组装;已生成报告的渲染绝不依赖前端分类。
 */
export interface PrepStageClass {
  isCoding: boolean;
  isDesign: boolean;
  isScreen: boolean;
  isBehavioral: boolean;
  isFinal: boolean;
}

export function classifyPrepStage(stage: string | null | undefined): PrepStageClass {
  const s = stage ?? '';
  return {
    isCoding: /coding|live[ -]?coding|pair[ -]?programming|online assessment|\boa\b|hackerrank|codility/i.test(s),
    isDesign: /system[ -]?design|architect|design/i.test(s),
    isScreen: /screen|phone|recruit|\bhr\b/i.test(s),
    isBehavioral: /behav|culture|fit/i.test(s),
    isFinal: /final|hiring|manager|onsite|panel/i.test(s),
  };
}

/** 规范节位顺序(规格 §4.1);条件节按分类取舍,design 卡不占节位。 */
export const PREP_CANONICAL_ORDER = [
  'round-brief', 'greeting', 'intro-listen', 'self-intro', 'deep-dive', 'tech-qa',
  'coding-round', 'design-round', 'behavioral', 'weakness-plan', 'ask-back',
  'comp-process', 'closing',
];

export function prepFrameworkSectionIds(cls: PrepStageClass): string[] {
  return PREP_CANONICAL_ORDER.filter(id =>
    (id !== 'coding-round' || cls.isCoding) && (id !== 'design-round' || cls.isDesign));
}

/**
 * 票207：存量旧版报告的读侧迁移（纯函数，无副作用，存储一字不动）。
 * 旧版 = 133 票之前旧生成器的存量（旧提示词 8 节、标题带“1 ·”编号，本来就没有
 * round-brief / weakness-plan / comp-process 三节）。迁移只做五件事：
 *  1) 旧标题去编号（“1 · Warm-up”→“Warm-up”）；
 *  2) 按规范顺序重排（分类只决定补哪些脚手架，不丢已存的规范节）；
 *  3) 缺失的期望节补框架脚手架（标 scaffold=true，显示“待生成”）；
 *  4) 同一规范 id 重复出现只留首个（其余丢弃——这是“非规范节丢弃”的唯一含义）；
 *  5) 认不出的节原样保留在末尾并标 unmapped=true，绝不静默丢弃。
 * 输入绝不被改动（先 JSON 深拷贝再加工）；用户改写与题目三档答案随节原样保留。
 */
export function migrateLegacyPrepReport(
  report: PrepReport,
  opts: {
    /** 该轮次分类下的期望节位序列（prepFrameworkSectionIds 结果）。 */
    expectedIds: string[];
    /** 为缺失节补脚手架（返回完整节；迁移会再标 scaffold=true）。 */
    scaffoldSection: (id: string) => PrepSection | null;
  }
): PrepReport {
  const next: PrepReport = JSON.parse(JSON.stringify(report)) as PrepReport;
  const sections = Array.isArray(next.sections) ? next.sections : [];
  const canonical = new Set(PREP_CANONICAL_ORDER);
  const seen = new Set<string>();
  const byId = new Map<string, PrepSection>();
  const unmapped: PrepSection[] = [];
  for (const s of sections) {
    const id = s.id;
    // 去编号：仅剥开头的“数字 + 分隔符”前缀，其余标题原文不动。
    const title = (s.title ?? '').replace(/^\s*\d+\s*[·•.\-–—]\s*/, '');
    const sec: PrepSection = { ...s, title: title || id };
    if (!canonical.has(id)) { sec.unmapped = true; unmapped.push(sec); continue; }
    if (seen.has(id)) continue; // 重复规范节：只留首个
    seen.add(id);
    byId.set(id, sec);
  }
  const ordered: PrepSection[] = [];
  for (const id of PREP_CANONICAL_ORDER) {
    const kept = byId.get(id);
    if (kept) { ordered.push(kept); continue; }
    if (!opts.expectedIds.includes(id)) continue;
    const scaffold = opts.scaffoldSection(id);
    if (scaffold) ordered.push({ ...scaffold, scaffold: true });
  }
  // 已存但不在期望序列里的规范节（如非设计轮存有的 design-round）不丢：
  // 它们不在上面循环里，按规范顺序补进对应位置之后。
  for (const id of PREP_CANONICAL_ORDER) {
    if (opts.expectedIds.includes(id)) continue;
    const kept = byId.get(id);
    if (kept && !ordered.includes(kept)) {
      const at = PREP_CANONICAL_ORDER.indexOf(id);
      let pos = ordered.length;
      for (let i = 0; i < ordered.length; i++) {
        const oid = ordered[i].id;
        if (!ordered[i].unmapped && PREP_CANONICAL_ORDER.indexOf(oid) > at) { pos = i; break; }
      }
      ordered.splice(pos, 0, kept);
    }
  }
  next.sections = [...ordered, ...unmapped];
  return next;
}

export interface PrepQuestionExportOpts {
  /** 已入 Tech Stack 时的挂接答案(空则不写这一行)。 */
  linked: string;
  stackLabel: string;
  spokenLabel: string;
  technicalLabel: string;
}

/**
 * 单题导出映射(票133):标题 + 标准答案行。
 * note 是弱点预案卡的内部诊断信息,永不进入导出文档。
 */
export function prepQuestionExportBlocks(q: PrepQuestionItem, o: PrepQuestionExportOpts): ExportBlock[] {
  const blocks: ExportBlock[] = [{ kind: 'subheading', text: q.question }];
  const sp = prepStdText(q.stdSpoken);
  const te = prepStdText(q.stdTechnical);
  if (o.linked) blocks.push({ kind: 'std', label: o.stackLabel, text: o.linked });
  if (sp.text) blocks.push({ kind: 'std', label: o.spokenLabel, text: sp.text });
  if (te.text) blocks.push({ kind: 'std', label: o.technicalLabel, text: te.text });
  return blocks;
}
