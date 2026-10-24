/**
 * 详情页 v2(2026-10-20 定稿)的前端模型。
 * 与后端 JSON 约定对应:Asset.MetaJson / Question.ReviewJson / Round.ReviewJson /
 * Round.PrepReportJson / Jobs Communication.MetaJson —— 后端只透传存储,结构在此定义。
 */

export function safeJson<T>(s: string | null | undefined): T | null {
  if (!s) return null;
  try { return JSON.parse(s) as T; } catch { return null; }
}

/** 转写分段:有录音时带起止秒;纯文本粘贴时 start/end 缺省。 */
export interface TranscriptSegment {
  idx?: number;
  speaker: string;
  start?: number | null;
  end?: number | null;
  text: string;
}

/** 材料元数据(统一入口解析结果 + 沟通处理信息)。 */
export interface AssetMeta {
  /** 邮件分类:invitation 邀约 / reschedule 改期 / rejection 拒绝 / followup 跟进 / other 其它。 */
  category?: string;
  from?: string;
  fromEmail?: string;
  subject?: string;
  /** 邮件日期原文/归一化日期(yyyy-MM-dd)。 */
  date?: string;
  /** 提取到的面试时间(ISO 或 yyyy-MM-dd HH:mm)。 */
  interviewAt?: string;
  interviewers?: string[];
  format?: string;
  /** 来源场景:email 邮件 / call 电话 / recording 录音 / note 备注。 */
  channel?: string;
  /** AI 摘要(沟通记录展示用)。 */
  summary?: string;
  /** 提取到的新安排(沟通记录"未处理"判定与一键建轮次的依据)。 */
  arrangement?: {
    date?: string;
    time?: string;
    stage?: string;
    interviewers?: string;
    format?: string;
  } | null;
  /** 处理状态:Pending 未处理 / Done 已处理 / Ignored 先放着。 */
  handlingStatus?: 'Pending' | 'Done' | 'Ignored' | string;
  /** 粘贴截图(原图)的资产 id —— 截图是本材料的附件,不是独立类型。 */
  screenshotAssetId?: string;
  /** 邮件主题聚合标识(归一化主题 key,定稿第九节):同主题往来共用,供展示聚合。 */
  threadKey?: string;
  /** 关联到的 Interviews 材料 id(沟通镜像行指回材料本体)。 */
  assetId?: string;
  /** 已被移入某轮次的材料:沟通时间线不再重复展示。 */
  movedToRoundId?: string;
  /** 关联到的 Jobs 沟通记录 id(双写同源时的对侧指针)。 */
  communicationId?: string;
  /** 去重键:发件人+日期+主题。 */
  dedupeKey?: string;
}

/** 逐题分析的单个维度。 */
export interface DimReview {
  score: number | null;
  comment?: string;
  fix?: string;
}

export interface QuestionReviewStd {
  ai?: string | null;
  user?: string | null;
}

/** 逐题复盘(归一化后):后端生成的是扁平形态(stdSpoken 为字符串),解析时统一成 {ai,user}。 */
export interface QuestionReview {
  overall?: number | null;
  verdict?: string;
  dims?: {
    relevance?: DimReview;
    depth?: DimReview;
    expression?: DimReview;
    pronunciation?: DimReview;
  };
  stdSpoken?: QuestionReviewStd;
  stdTechnical?: QuestionReviewStd;
}

export function parseQuestionReview(raw: string | null | undefined): QuestionReview | null {
  const j = safeJson<any>(raw);
  if (!j) return null;
  const std = (v: any): QuestionReviewStd | undefined => {
    if (v == null) return undefined;
    if (typeof v === 'string') return { ai: v, user: null };
    return { ai: v.ai ?? null, user: v.user ?? null };
  };
  return {
    overall: j.overall ?? null,
    verdict: j.verdict,
    dims: j.dims,
    stdSpoken: std(j.stdSpoken),
    stdTechnical: std(j.stdTechnical)
  };
}

export function serializeQuestionReview(r: QuestionReview): string {
  return JSON.stringify({
    overall: r.overall ?? null,
    verdict: r.verdict ?? null,
    dims: r.dims ?? null,
    stdSpoken: r.stdSpoken ?? null,
    stdTechnical: r.stdTechnical ?? null
  });
}

/** 整轮复盘汇总。 */
export interface RoundReview {
  overall?: number | null;
  /** 通过概率:AI 估计,passBasis 是依据。 */
  passProbability?: number | null;
  passBasis?: string;
  strongest?: string;
  weakest?: string;
  summary?: string;
}

// ---------------- 备考报告 ----------------

export interface PrepBlock {
  type: 'script' | 'points' | 'list' | string;
  title?: string;
  text?: string | null;
  /** script 的用户编辑版(用户版优先,规则同标准答案)。 */
  userText?: string | null;
  items?: string[];
}

export interface PrepQuestionItem {
  question: string;
  category?: string;
  sourceTags?: string[];
  weaknessRelated?: boolean;
  stdSpoken?: string | { ai?: string | null; user?: string | null } | null;
  stdTechnical?: string | { ai?: string | null; user?: string | null } | null;
  /** 弱点预案卡的内部诊断(票133):卡因 / 匹配弱点建议;只在卡片内显示一行,永不进入导出。 */
  note?: string | null;
  /** 已入 Tech Stack 时挂接的条目 id —— 答案以 Tech Stack 那份为准(同一条记录)。 */
  knowledgeId?: string | null;
  /** 我的回答(入 Tech Stack 时用作 myAnswer 的补充)。 */
  myAnswer?: string | null;
}

export interface PrepSection {
  id: string;
  title: string;
  blocks?: PrepBlock[];
  questions?: PrepQuestionItem[];
}

/** 备考数据源逐项状态(2026-10-21:后端 sources.details,与 AiGateway 口径一致)。 */
export interface PrepSourceDetail {
  key: 'questionBank' | 'company' | 'web' | 'glassdoor' | 'manualExperiences' | string;
  /** fetched 已抓取 / disabled 未启用 / notConfigured 未配置 / noData 无数据 / failed 抓取失败 */
  status: 'fetched' | 'disabled' | 'notConfigured' | 'noData' | 'failed' | string;
  count?: number;
  links?: string[];
}

export interface PrepReport {
  generatedAt?: string;
  generatedBy?: string;
  sources?: {
    jd?: boolean;
    company?: boolean;
    experiences?: boolean | number;
    resume?: boolean;
    pastQuestions?: number;
    weaknesses?: number;
    details?: PrepSourceDetail[];
  };
  sections: PrepSection[];
}

/** 备考题的标准答案取文:对象形态取 user ?? ai;字符串直接用。 */
export function prepStdText(
  v: PrepQuestionItem['stdSpoken']): { text: string; isUser: boolean } {
  if (v == null) return { text: '', isUser: false };
  if (typeof v === 'string') return { text: v, isUser: false };
  if (v.user) return { text: v.user, isUser: true };
  return { text: v.ai ?? '', isUser: false };
}

// ---------------- 沟通记录(统一展示模型) ----------------

export interface CommItem {
  /** jobs = Jobs Communication(与 Tracker 同表);asset = 条目级材料(未关联投递时)。 */
  source: 'jobs' | 'asset';
  id: string;
  /** Email / Call / Interview / Message / Note / Transcript / Notes ... */
  type: string;
  subject?: string | null;
  content: string;
  contactName?: string | null;
  occurredAt: string;
  meta: AssetMeta;
  handlingStatus?: string | null;
  /** 关联的 Interviews 材料 id(截图/原文在材料侧时)。 */
  assetId?: string;
}
