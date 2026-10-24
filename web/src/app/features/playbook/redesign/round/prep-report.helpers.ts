import { PrepQuestionItem, prepStdText } from '../v2.models';
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
