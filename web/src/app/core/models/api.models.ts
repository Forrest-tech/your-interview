/** 与后端契约一一对应的类型定义。字段名严格跟随后端 JSON(小驼峰),不要自造。 */

// ============================ 通用 ============================

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNext?: boolean;
  hasPrevious?: boolean;
}

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
}

// ============================ 认证 ============================

export interface Tokens {
  accessToken: string;
  refreshToken: string;
  expiresIn?: number;
}

export interface AuthUser {
  id: string;
  email: string;
  displayName: string;
  roles: string[];
  permissions: string[];
}

export interface AuthResult {
  /**
   * 字段名必须是 profile —— 后端 AuthResult(Tokens, Profile) 的序列化结果就是 { tokens, profile }。
   * 之前这里写成 user,登录能拿到 token 但用户信息永远为空、权限判断全 false,
   * 而且不会报错(只是 undefined),属于最难查的一类契约漂移。
   */
  profile: AuthUser;
  tokens: Tokens;
}

// ============================ Tracker(投递跟踪) ============================

export type ApplicationStatus =
  | 'Saved' | 'Applied' | 'Screen' | 'Interview' | 'Offer'
  | 'Rejected' | 'Paused' | 'Withdrawn';

export interface Application {
  id: string;
  companyId: string;
  companyName: string;
  role: string;
  location?: string;
  salary?: string;
  status: ApplicationStatus;
  priority?: string;
  appliedDate?: string;
  needsConnectFirst?: boolean;
  link?: string;
  jdSummary?: string;
  /**
   * JD 全文(2026-09-18)。与 jdSummary 并存:
   * 摘要是给人看的速览,全文是匹配分析的原料。
   * ⚠️ 后端上限 40000 字符,详情页按需折叠展示,不要直接铺满。
   */
  jdText?: string;
  /** JD 出处 URL,便于复核原文。 */
  jdSourceUrl?: string;
  resumeScore?: number;
  passRateEstimate?: number;
  notes?: string;
  outreachStatus?: string;
  /** 命中 JD 要求的关键词(逗号分隔),由匹配分析写入。 */
  matchKeywords?: string;
  history?: StatusChange[];
  rounds?: InterviewRound[];
  createdAt: string;
  updatedAt?: string;
}

/** 公司(2026-09-18:补 Profile 公司情报,面试前准备包的输入之一)。 */
export interface Company {
  id: string;
  name: string;
  website?: string;
  industry?: string;
  location?: string;
  logoUrl?: string;
  notes?: string;
  isBlacklisted: boolean;
  companyType: string;
  applicationCount: number;
  /** 公司情况长文本(规模/主营业务/技术栈/面试风格/文化/近期动态)。 */
  profile?: string;
  /** 公司情报来源 URL(JSON 数组字符串)。 */
  profileSourcesJson?: string;
  createdAt: string;
}

export interface StatusChange {
  from: string;
  to: string;
  at: string;
  note?: string;
}

export interface InterviewRound {
  id: string;
  roundNo: number;
  scheduledAt?: string;
  format?: string;
  interviewers?: string;
  outcome?: string;
  notes?: string;
}

export interface TrackerStats {
  total: number;
  byStatus: Record<string, number>;
  bySource?: Record<string, number>;
  activeCount: number;
  interviewCount: number;
  offerCount: number;
  responseRate?: number;
  weeklyTrend?: { week: string; applied: number; interviews: number }[];
}

// ============================ 实战机经(Interviews) ============================

export type InterviewStatus =
  | 'Draft' | 'AssetsUploaded' | 'Transcribing' | 'Transcribed'
  | 'Analyzing' | 'Analyzed' | 'Failed';

export interface InterviewEntry {
  id: string;
  companyId: string;
  companyName: string;
  role: string;
  roundNo: number;
  interviewDate?: string;
  interviewFormat?: string;
  interviewers?: string;
  location?: string;
  status: InterviewStatus;
  result?: string;
  jdText?: string;
  jdSummary?: string;
  companyProfile?: string;
  notes?: string;
  analysisSummary?: string;
  failureReason?: string;
  // 六维
  overallScore?: number;
  pronunciationScore?: number;
  fluencyScore?: number;
  structureScore?: number;
  technicalDepthScore?: number;
  relevanceScore?: number;
  assetCount?: number;
  questionCount?: number;
  weaknessCount?: number;
  hasTranscript?: boolean;
  createdAt: string;
  analyzedAt?: string;
}

export interface InterviewQuestion {
  id: string;
  sequence: number;
  questionText: string;
  myAnswerText?: string;
  recommendedAnswer?: string;
  assessment?: string;
  category?: string;
  difficulty?: number;
  gotStuck?: boolean;
  stuckReason?: string;
  missedPointsJson?: string;
  followUpQuestionsJson?: string;
  weaknessTagsJson?: string;
  askedAtSeconds?: number;
}

export interface InterviewWeakness {
  id: string;
  category: string;
  title: string;
  detail?: string;
  evidence?: string;
  suggestion?: string;
  severity: number;
  sourceType?: string;
  occurrenceCount: number;
  createdAt?: string;
}

export interface InterviewAsset {
  id: string;
  kind: 'Audio' | 'Transcript' | 'Notes' | string;
  fileName: string;
  contentType?: string;
  sizeBytes: number;
  storagePath?: string;
  blobUrl?: string;
  durationSeconds?: number;
  sourceLanguage?: string;
  uploadedAt: string;
  hasTranscript?: boolean;
  /** 内容摘要(上传时服务端计算)。完整性巡检比对这个值。 */
  sha256?: string;
  /** 最近一次巡检确认文件还在;false = 录音文件已丢失,前端要给出警示。 */
  fileExists?: boolean;
}

export interface InterviewDetail extends InterviewEntry {
  assets?: InterviewAsset[];
  questions?: InterviewQuestion[];
  weaknesses?: InterviewWeakness[];
}

/** 分析任务台账行 —— 「流水线记录」区直出(投递尝试与失败原因留痕)。 */
export interface AnalysisJob {
  id: string;
  jobType: 'Transcription' | 'Analysis' | string;
  status: 'Pending' | 'Dispatched' | 'Succeeded' | 'Failed' | 'Dead' | string;
  attempts: number;
  maxAttempts: number;
  failureReason?: string;
  lastError?: string;
  createdAt: string;
  dispatchedAt?: string;
  completedAt?: string;
}

// ============================ 技术栈(Knowledge) ============================

export type MasteryLevel = 'New' | 'Learning' | 'Familiar' | 'Proficient' | 'Mastered';

/**
 * 技术栈条目 —— 字段名严格对齐后端 KnowledgeHandlers 实际返回。
 *
 * 设计说明:后端的九块学习结构(直觉/定义/详解/生活例/落地/坑/面试答)
 * 没有拆成 8 个字段,而是收在两处:
 *   conceptExplanation = 完整讲解正文
 *   keyPointsJson / commonMistakesJson = 要点与常见误区(JSON 字符串,前端解析)
 * 这样后端演进结构时不必频繁加列,但前端必须做 JSON 解析兜底。
 */
export interface KnowledgeItem {
  id: string;
  title: string;
  topic: string;
  question?: string;
  source: 'Personal' | 'FromInterview' | string;
  difficulty: number;
  importance: number;
  tagsJson?: string;
  mastery: MasteryLevel;
  reviewCount: number;
  nextReviewAt?: string | null;
  easinessFactor?: number;
  repetitionStreak?: number;
  conceptExplanation?: string;
  keyPointsJson?: string;
  commonMistakesJson?: string;
  referencesJson?: string;
  isDue?: boolean;
  dueInDays?: number | null;
  reviewLogs?: KnowledgeReviewLog[];
  relations?: KnowledgeRelation[];
  createdAt?: string;
  updatedAt?: string;
}

export interface KnowledgeReviewLog {
  id?: string;
  reviewedAt: string;
  confidenceAfter: number;
  intervalDays?: number;
  easinessFactor?: number;
}

export interface KnowledgeRelation {
  id?: string;
  relatedItemId: string;
  relatedTitle?: string;
  relationType: string;
}

/**
 * 分类目录项。/topics 与 /stats 的 byTopic 是同一个形状 ——
 * 后端复用同一个投影,前端也应该只定义一个类型,避免两处各写一半字段。
 */
export interface KnowledgeTopic {
  topic: string;
  total: number;
  mastered: number;
  dueToday: number;
  averageReviewCount: number;
}

export interface MasteryCount {
  mastery: MasteryLevel;
  count: number;
}

/** /stats 契约:掌握度分布 + 按主题分布 + 复习概览。 */
export interface KnowledgeStats {
  totalItems: number;
  dueToday: number;
  dueThisWeek: number;
  reviewedThisWeek: number;
  totalReviews: number;
  averageReviewCount: number;
  masteryRate: number;
  byMastery: MasteryCount[];
  byTopic: KnowledgeTopic[];
}

// ============================ AI 实战模拟(Assessment) ============================

export interface MockSession {
  id: string;
  title: string;
  mode: string;
  topic?: string;
  difficulty?: number;
  status: 'InProgress' | 'Completed' | 'Abandoned';
  questionCount: number;
  answeredCount: number;
  scoredCount: number;
  overallScore?: number;
  pronunciationScore?: number;
  fluencyScore?: number;
  structureScore?: number;
  technicalDepthScore?: number;
  relevanceScore?: number;
  sentenceIntegrityScore?: number;
  summary?: string;
  startedAt: string;
  completedAt?: string;
}

export interface MockQuestion {
  id: string;
  sequence: number;
  questionText: string;
  expectedPointsJson?: string;
  myAnswerText?: string;
  answeredAt?: string;
  isScored?: boolean;
  overallScore?: number;
  pronunciationScore?: number;
  fluencyScore?: number;
  structureScore?: number;
  technicalDepthScore?: number;
  relevanceScore?: number;
  sentenceIntegrityScore?: number;
  feedback?: string;
  issues?: DimensionIssue[];
}

export interface DimensionIssue {
  dimension: string;
  issue: string;
  suggestion?: string;
  severity?: number;
}

export interface MockSessionDetail extends MockSession {
  questions?: MockQuestion[];
  issues?: DimensionIssue[];
}

// ============================ 数据分析(Analytics) ============================

export interface RadarPoint {
  dimension: string;
  nameZh: string;
  current: number;
  best: number;
  previous: number;
  changeFromPrevious: number;
}

export interface Insight {
  headline: string;
  detail: string;
  suggestedAction?: string;
  severity?: 'info' | 'warning' | 'critical' | string;
}

export interface PipelineSnapshot {
  date: string;
  saved: number;
  applied: number;
  screening: number;
  interviewing: number;
  offered: number;
  rejected: number;
  interviewRate: number;
}

export interface MasteryTopic {
  topic: string;
  total: number;
  mastered: number;
  learning: number;
  fresh: number;
}

export interface ActivityStats {
  practiceDays: number;
  totalSnapshots: number;
  lastActivityDate?: string | null;
  sessionsThisWeek: number;
}

export interface TrendPoint {
  date: string;
  value: number;
}

export interface TrendSeries {
  dimension: string;
  nameZh: string;
  points: TrendPoint[];
}

/**
 * 仪表盘契约 —— 字段名严格对齐后端 AnalyticsHandlers 的 GetDashboardQuery 返回。
 * 这些是后端已 E2E 实测的真实形状(pipeline 是数组、insight 是对象),
 * 前端模型必须跟着后端走,不能反过来假设。
 */
export interface Dashboard {
  radar: RadarPoint[];
  trends: TrendSeries[];
  pipeline: PipelineSnapshot[];
  mastery: MasteryTopic[];
  activity: ActivityStats;
  insight: Insight;
}

/** 六个维度的权重(后端 Assessment 的加权算法,前端展示用)。 */
export interface DimensionWeight {
  dimension: string;
  nameZh: string;
  weight: number;
  description?: string;
}

export interface AbilityTrend {
  days: number;
  series: TrendSeries[];
}

export interface AdminStats {
  userCount: number;
  activeUserCount: number;
  roleCount: number;
  permissionCount: number;
  auditEventCount: number;
  recentLogins?: number;
}

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  isActive: boolean;
  roles: string[];
  createdAt: string;
  lastLoginAt?: string;
}

export interface AdminRole {
  id: string;
  name: string;
  description?: string;
  permissions: string[];
  userCount?: number;
}

export interface AuditLog {
  id: string;
  action: string;
  entityType?: string;
  entityId?: string;
  userId?: string;
  userEmail?: string;
  ip?: string;
  occurredAt: string;
  detail?: string;
}
