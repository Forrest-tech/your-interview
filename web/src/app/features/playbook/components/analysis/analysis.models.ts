/**
 * Analysis 模块本地 DTO —— 只定义本目录组件需要的字段子集,
 * 多出的后端字段会被 HttpClient 自动忽略,所以 GET 整条目时可以直接复用。
 *
 * 铁律:不要改 web/src/app/core/models/api.models.ts,后端 DTO 变化时在这里同步。
 */

/** GET /api/interviews/{id}/analysis/latest —— 无快照时后端返回 null。 */
export interface AnalysisSnapshotDto {
  id: string;
  createdAt: string;
  overallScore?: number | null;
  /** 六维 JSON, key = overall/pronunciation/fluency/structure/technicalDepth/relevance(见后端 PlaybookText.BuildDimensionsJson)。 */
  dimensionsJson?: string | null;
  /** JSON 字符串数组,3~5 条一句话结论。 */
  summaryBulletsJson?: string | null;
}

/** GET /api/interviews/{id}/analysis/trend —— 按时间 asc。 */
export interface AnalysisTrendPointDto {
  id: string;
  createdAt: string;
  overallScore?: number | null;
}

/** GET/POST/PATCH/DELETE /api/interviews/{id}/actions(+/{aid}) —— A2 Next actions。 */
export interface InterviewActionDto {
  id: string;
  title: string;
  detail?: string | null;
  dueDate?: string | null;
  isDone: boolean;
  createdAt: string;
}

/** 逐题手风琴只读卡需要的字段子集(来自 GET /api/interviews/{id} 的 questions[])。 */
export interface AnalysisQuestionView {
  id: string;
  questionText: string;
  myAnswerText?: string | null;
  recommendedAnswer?: string | null;
  category?: string | null;
  difficulty?: number | null;
  gotStuck?: boolean | null;
  stuckReason?: string | null;
}

/** GET /api/interviews/{id} —— 本报告只消费这三个字段。 */
export interface AnalysisEntryView {
  questions?: AnalysisQuestionView[] | null;
  candidateTranscript?: string | null;
  analysisSummary?: string | null;
  /** 后端 WeaknessDto 数组(camelCase)。 */
  weaknesses?: WeaknessView[] | null;
}

/** 短板(WeaknessDto 子集):分类 chip + 标题 + 建议 + 证据。 */
export interface WeaknessView {
  id: string;
  /** 后端枚举字符串,如 Pronunciation / Structure / TechnicalDepth。 */
  category: string;
  title: string;
  detail?: string | null;
  evidence?: string | null;
  /** 严重度 1-5,列表按它倒序。 */
  severity: number;
  suggestion?: string | null;
  occurrenceCount: number;
}

/** 六维展示元数据 —— 顺序即报告推荐阅读顺序。 */
export interface DimScore {
  key: string;
  /** 全站 i18n 已有键(dim.overall 等),通过 t() 回退到 I18nService 解析。 */
  labelKey: string;
  value: number | null;
}

export const DIMENSION_KEYS: { key: string; labelKey: string }[] = [
  { key: 'overall', labelKey: 'dim.overall' },
  { key: 'pronunciation', labelKey: 'dim.pronunciation' },
  { key: 'fluency', labelKey: 'dim.fluency' },
  { key: 'structure', labelKey: 'dim.structure' },
  { key: 'technicalDepth', labelKey: 'dim.technicalDepth' },
  { key: 'relevance', labelKey: 'dim.relevance' }
];

/**
 * 解析 DimensionsJson。
 * 后端用 JsonSerializerDefaults.Web 序列化 → camelCase key,值为 int?。
 * AI 产出的 JSON 偶发被截断,try/catch 兜底返回空对象;非数字值直接丢弃。
 */
export function parseDimensions(json: string | null | undefined): Record<string, number | null> {
  if (!json) return {};
  try {
    const raw: unknown = JSON.parse(json);
    if (!raw || typeof raw !== 'object') return {};
    const out: Record<string, number | null> = {};
    for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
      const n = typeof v === 'number' ? v : Number(v);
      out[k] = Number.isFinite(n) ? Math.max(0, Math.min(100, Math.round(n))) : null;
    }
    return out;
  } catch {
    return {};
  }
}

/**
 * 解析 SummaryBulletsJson(JSON 字符串数组)。
 * 解析失败/非数组/空元素都过滤掉 —— 页面绝不因为一个字段崩掉。
 */
export function parseBullets(json: string | null | undefined): string[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((x) => (typeof x === 'string' ? x.trim() : ''))
      .filter((s) => s.length > 0);
  } catch {
    return [];
  }
}

/**
 * 无快照时的结论兜底:把 analysisSummary 按中英文句号切分,取前 max 句。
 */
export function splitSentences(text: string | null | undefined, max: number): string[] {
  if (!text) return [];
  return text
    .split(/(?<=[。！？.!?])\s*/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .slice(0, Math.max(0, max));
}

/** 分数档位 —— 与现有 overview 的 scoreClass 口径一致(>=75 好,>=55 中,其余差)。 */
export type ScoreBand = 'good' | 'mid' | 'bad' | 'none';

export function scoreBand(score: number | null | undefined): ScoreBand {
  if (score === null || score === undefined) return 'none';
  if (score >= 75) return 'good';
  if (score >= 55) return 'mid';
  return 'bad';
}
