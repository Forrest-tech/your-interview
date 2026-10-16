/**
 * Q&A 题库模块(F1/F5)本地 DTO。
 *
 * 只声明本模块用到的字段 —— 不改动 core/models/api.models.ts,
 * 后端多给字段不影响编译,少给字段时各处都用 ?? / 可选链兜底。
 */

/** 掌握度:A1 契约。0=待练 Todo, 1=练习中 Practicing, 2=已掌握 Mastered, 3=还卡壳 Stuck。 */
export type QaMastery = 0 | 1 | 2 | 3;

export const QA_MASTERY_VALUES: QaMastery[] = [0, 1, 2, 3];

/** 题库卡片用的题目形状 —— 对应 GET /api/interviews/{id} 返回的 questions[] 子集。 */
export interface QaQuestion {
  id: string;
  sequence: number;
  questionText: string;
  myAnswerText?: string | null;
  recommendedAnswer?: string | null;
  assessment?: string | null;
  category?: string | null;
  difficulty?: number | null;
  gotStuck?: boolean;
  stuckReason?: string | null;
  /** A1 新增,后端可能没回(旧数据)时按 0(待练)处理。 */
  mastery?: number | null;
}

/** 条目详情 —— 本模块只需要 questions,其它字段一律不声明。 */
export interface QaEntryDetail {
  id?: string;
  questions?: QaQuestion[];
}

/**
 * 历史回答条目 —— 对应 A4:
 * GET /api/interviews/{id}/questions/{qid}/history
 * → [{ interviewId, companyName, role, interviewDate, myAnswerText, mastery }]。
 */
export interface QaHistoryItem {
  interviewId: string;
  companyName: string;
  role: string;
  interviewDate?: string | null;
  myAnswerText?: string | null;
  mastery?: number | null;
}

/** 题型兜底:后端没给 category 时归为 Other,保证分布条与筛选不断。 */
export function qaCategoryOf(q: Pick<QaQuestion, 'category'>): string {
  const c = (q.category ?? '').trim();
  return c ? c : 'Other';
}

/** 掌握度兜底:旧数据没有 mastery 字段时按 0(待练)处理。 */
export function qaMasteryOf(q: Pick<QaQuestion, 'mastery'>): QaMastery {
  const m = q.mastery ?? 0;
  return (m === 0 || m === 1 || m === 2 || m === 3) ? m : 0;
}

/**
 * 是否"卡壳"(spec F1 定义):mastery == 3(还卡壳)或 gotStuck 标记。
 * 注意 mastery==3 本身已算卡壳,不必再看 gotStuck。
 */
export function qaIsStuck(q: QaQuestion): boolean {
  return qaMasteryOf(q) === 3 || q.gotStuck === true;
}

/** 参考答案标题是否用中性文案:Behavioral / Culture 类题不用"参考答案"。 */
export function qaIsSoftSkill(q: Pick<QaQuestion, 'category'>): boolean {
  const c = qaCategoryOf(q);
  return c === 'Behavioral' || c === 'Culture';
}
