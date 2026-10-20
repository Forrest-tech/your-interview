/**
 * F3/F4/F5/F6 本地 DTO —— 绝不改 core/models/api.models.ts。
 *
 * 字段名与后端 JSON（camelCase）对齐；TS 是结构化类型，后端多给字段不影响。
 *
 * ★ 已知后端缺口（2026-10-16）：GET /api/interviews/{id} 返回的 RoundDto
 *   只有 id/order/stage/scheduledDate/interviewers/format/location/outcome/notes/feedback，
 *   契约里的 meetingLink / scheduledTime / emailsJson / transcript 暂未下发。
 *   这里按契约声明为可选字段，前端全部做"缺字段"防御；后端补上后自动生效。
 */
export interface BriefQuestion {
  id: string;
  sequence: number;
  questionText: string;
  myAnswerText?: string;
  category?: string;
  difficulty?: number;
  gotStuck?: boolean;
  stuckReason?: string;
  /** 掌握度:0=待练 1=练习中 2=已掌握 3=卡壳（后端 A1） */
  mastery?: number;
}

export interface BriefRound {
  id: string;
  order: number;
  stage: string;
  scheduledDate?: string;
  interviewers?: string;
  format?: string;
  location?: string;
  outcome: string;
  notes?: string;
  feedback?: string;
  meetingLink?: string;
  scheduledTime?: string;
  emailsJson?: string;
  transcript?: string;
  prepQuestionsJson?: string;
  recordingUrl?: string;
  checklistJson?: string;
}

/** GET /api/interviews/{id} 的子集 —— 只声明 Brief 用到的字段。 */
export interface BriefEntry {
  id: string;
  companyName: string;
  role: string;
  status: string;
  companyProfile?: string;
  jdText?: string;
  jdSummary?: string;
  roundNo?: number;
  interviewDate?: string;
  interviewFormat?: string;
  interviewers?: string;
  location?: string;
  result?: string;
  notes?: string;
  questionsToAskJson?: string;
  questions?: BriefQuestion[];
  rounds?: BriefRound[];
  assets?: BriefAsset[];
}

/**
 * 轮次资产(2026-10-19):后端 AssetDto 的子集。
 * roundId 为 null = 历史遗留未归类材料;phase = Pre(面试前) / Post(面试后)。
 */
export interface BriefAsset {
  id: string;
  kind: string;
  fileName: string;
  contentType?: string;
  sizeBytes: number;
  blobUrl?: string;
  durationSeconds?: number;
  sourceLanguage?: string;
  hasTranscript: boolean;
  transcriptLength: number;
  uploadedAt: string;
  roundId?: string | null;
  phase?: string | null;
}

/** A7 邮件条目 —— 与后端 RoundEmailItem 的 JSON 键一致。 */
export interface RoundEmailItem {
  from?: string;
  subject?: string;
  date?: string;
  snippet?: string;
}

/** A2 待办事项 —— 与后端 ActionDto 对齐。 */
export interface InterviewAction {
  id: string;
  title: string;
  detail?: string;
  dueDate?: string;
  isDone: boolean;
  createdAt?: string;
}

/** A5 技术覆盖图谱行 —— 与后端 CoverageCategoryDto 对齐。 */
export interface CoverageRow {
  category: string;
  askedCount: number;
  stuckCount: number;
  masteredCount: number;
  interviews: string[];
}

/** GET /api/interviews/rounds/upcoming 行 —— 与后端 UpcomingRoundDto 对齐。 */
export interface UpcomingRoundItem {
  roundId: string;
  entryId: string;
  companyName: string;
  role: string;
  order: number;
  stage: string;
  scheduledDate?: string;
  scheduledTime?: string;
  interviewers?: string;
  format?: string;
  location?: string;
  meetingLink?: string;
  outcome: string;
}

/** JD 关键词提取结果。 */
export interface Keyword {
  word: string;
  count: number;
}
