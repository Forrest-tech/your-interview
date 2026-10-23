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
  /** 头像 URL(Google 登录用户 = Google 头像);无则用首字母占位。 */
  avatarUrl?: string | null;
  /** 管理员强制改密标记 —— 个人中心顶部据此显示警示条。 */
  mustChangePassword?: boolean;
  /** 最近一次成功登录(UTC ISO);个人中心按所选时区展示。 */
  lastLoginAt?: string | null;
  /** 账号偏好:界面语言(zh/en/fr)与 IANA 时区(默认 America/Toronto)。 */
  preferredLanguage?: string | null;
  timeZone?: string | null;
  /** 账号创建时间。 */
  createdAt?: string;
  /** 票101:读时 xmin(PUT /auth/me 回传)。 */
  version?: number;
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
  | 'Accepted' | 'Rejected' | 'Ghosted' | 'Paused' | 'Withdrawn';

export interface Application {
  id: string;
  companyId: string;
  companyName: string;
  /** M3:公司 Logo 地址(后端按官网自动解析,可为空)。 */
  companyLogoUrl?: string | null;
  role: string;
  location?: string;
  salary?: string;
  /** 工作方式 (Remote / Hybrid / Onsite;后端 DTO 一直有返回,此前模型漏声明导致编辑回填无从引用)。 */
  workMode?: string | null;
  status: ApplicationStatus;
  priority?: string;
  /** 投递来源(如 LinkedIn / Indeed / 官网)。 */
  source?: string;
  appliedDate?: string;
  /** 投递截止日(ISO 日期,如 2026-09-30)。 */
  deadline?: string;
  /** 下次跟进时间(ISO 时间)。 */
  nextFollowUpAt?: string;
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
  /** 公司介绍(2026-10-19):完整存储,不截断。Smart paste 从 "About the company" 提取。 */
  companyDescription?: string;
  /** 工作身份要求(2026-10-19):归一短文本。Smart paste 从 JD 正文提取。 */
  workAuthorization?: string;
  /** 岗位性质(2026-10-19):归一短文本(Full-time/Part-time/Permanent/Contract/...)。Smart paste 提取。 */
  jobType?: string;
  /** 任职要求(2026-10-19):完整存储。Smart paste 从 Qualifications/Requirements 段提取。 */
  requirements?: string;
  /** 福利待遇(2026-10-19):完整存储。Smart paste 从 Benefits/Advantages 段提取。 */
  benefits?: string;
  resumeScore?: number;
  /** 预估通过率(后端存为字符串,如 "75%";展示时原样显示)。 */
  passRateEstimate?: string | null;
  notes?: string;
  /** 外联留言(对应后端 OutreachMessage;如"已发 LinkedIn 私信")。 */
  outreachMessage?: string | null;
  /** 命中 JD 要求的关键词(逗号分隔),由匹配分析写入。 */
  matchKeywords?: string;
  /** 拒因(状态 Rejected 时展示;M1.5 起实战机经回写也写这里)。 */
  rejectionReason?: string;
  history?: StatusChange[];
  rounds?: InterviewRound[];
  createdAt: string;
  updatedAt?: string;
  /** 票101:读时 xmin 乐观并发版本(保存时回传,过期后端 409)。 */
  version?: number;
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
  /** 票101:读时 xmin(同 Application.version)。 */
  version?: number;
}

/** Playbook 公司聚合(后端 CompanySummaryDto 子集):备考中的公司。 */
export interface PlaybookCompany {
  companyId: string;
  companyName: string;
  entryCount: number;
}

/** 公司轮次(后端 CompanyRoundDto):Tech Stack 来源选择用。 */
export interface PlaybookCompanyRound {
  roundId: string;
  entryId: string;
  order: number;
  stage: string;
  scheduledDate?: string;
}

/** 状态变更记录(与后端 StatusChangeDto 对齐:status/note/changedAt)。 */
export interface StatusChange {
  status: string;
  note?: string;
  changedAt: string;
}

/** 面试轮次(与后端 InterviewRoundDto 对齐:order/scheduledDate/interviewer)。 */
export interface TrackerStats {
  total: number;
  byStatus: Record<string, number>;
  bySource?: Record<string, number>;
  active: number;
  inInterview: number;
  offers: number;
  responseRate?: number;
  weeklyTrend?: { week: string; applied: number; interviews: number }[];
}

/** M4: 行动中心聚合(Jobs 侧)。 */
export interface StatusCount {
  status: string;
  count: number;
}

export interface ActionCenter {
  followUps: Application[];
  deadlines: Application[];
  byStatus: StatusCount[];
  activeCount: number;
  interviewCount: number;
  offerCount: number;
  total: number;
}

/** M3:申请问答库条目(用户级)。 */
export interface AnswerTemplate {
  id: string;
  category: string;
  question: string;
  answer: string;
  sortOrder: number;
  createdAt: string;
  updatedAt?: string | null;
  /** 票101:读时 xmin。 */
  version?: number;
}

/** M3:沟通记录类型。 */
export type CommunicationType = 'Email' | 'Call' | 'Interview' | 'Message' | 'Note';

/** M3:沟通记录(投递级)。 */
export interface Communication {
  id: string;
  applicationId: string;
  type: CommunicationType;
  subject?: string | null;
  content: string;
  contactName?: string | null;
  contactEmail?: string | null;
  /** 发生时间(ISO 字符串)。 */
  occurredAt: string;
  createdAt: string;
  updatedAt?: string | null;
  /** 票101:读时 xmin。 */
  version?: number;
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
  /**
   * 结构化结果(票05/ARC-14):后端 DeriveOutcome 派生的枚举名
   * (Pending|Passed|Rejected|Ghosted|Cancelled|NoShow),列表与详情 DTO 同源。
   * 分组/徽章只许消费此字段,禁止再判读 result 自由文本。
   */
  outcome?: string | null;
  jdText?: string;
  jdSummary?: string;
  companyProfile?: string;
  notes?: string;
  analysisSummary?: string;
  /** 客观声学指标(缺口4):Worker 回写,字段与后端 SpeechMetricsDto 对应。 */
  speechMetrics?: SpeechMetrics;
  /** 计分文本(2026-10-15):被标为候选人发言的分段拼接,前端复盘面板展示供肉眼验证。 */
  candidateTranscript?: string;
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
  hasTranscript?: boolean;
  createdAt: string;
  analyzedAt?: string;
  /** 票101:读时 xmin(全量保存回传,过期后端 409)。 */
  version?: number;
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
  /** 2026-10-20 v2:归轮 / 提问人 / 逐题复盘 JSON。 */
  roundId?: string | null;
  askedBy?: string | null;
  reviewJson?: string | null;
  mastery?: number;
  /** 票101:读时 xmin。 */
  version?: number;
}

export interface InterviewRound {
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
  prepQuestionsJson?: string;
  emailsJson?: string;
  transcript?: string;
  recordingUrl?: string;
  checklistJson?: string;
  /** 2026-10-20 v2:整轮复盘汇总 / 结构化备考报告 JSON。 */
  reviewJson?: string | null;
  prepReportJson?: string | null;
  /** 票101:读时 xmin。 */
  version?: number;
}

export interface PrepQuestion {
  text: string;
  checked: boolean;
}

/** 全局 upcoming 轮次(日历视图):跨所有面试条目。 */
export interface UpcomingRound {
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

/** 轮次通过概率:基于历史同 stage 通过率 + 准备度信号。 */
export interface PassProbability {
  probability: number;
  sampleSize: number;
  passedCount: number;
  stage: string;
  factors: OfferFactor[];
}

/** 整单 offer 概率:漏斗模型。 */
export interface OfferFactor {
  key: string;
  arg1: number;
  arg2: number;
  arg3: number;
}
export interface OfferProbability {
  probability: number;
  sampleSize: number;
  offerCount: number;
  currentRound: number;
  factors: OfferFactor[];
}

export interface RoundEmail {
  subject: string;
  from: string;
  date: string;
  snippet: string;
}

export interface GuidanceMaterial {
  id: string;
  version: number;
  contentMarkdown: string;
  model: string;
  generatedAt: string;
  promptTokens?: number;
  completionTokens?: number;
}

export interface GuidanceVersion {
  id: string;
  version: number;
  generatedAt: string;
  model: string;
}

/** 客观声学指标(缺口4)。 */
export interface ProblemWord {
  word: string;
  accuracyScore: number;
  fluencyScore?: number;
  startSeconds: number;
  endSeconds: number;
}

export interface SpeechMetrics {
  durationSeconds: number;
  wordCount: number;
  wordsPerMinute: number;
  averageSentenceLength: number;
  shortSentenceCount: number;
  fillerWordCount: number;
  fillerWordBreakdown?: Record<string, number>;
  selfRepetitionCount: number;
  longPauseCount: number;
  totalSilenceSeconds: number;
  pronunciationAccuracy: number;
  lowScoreWordRatio: number;
  problemWords?: ProblemWord[];
  /** 结构骨架标记(缺口4补齐):标记 → 出现次数。 */
  structureMarkers?: Record<string, number>;
}

/** 问答候选(缺口3):勾选导入 TechStack。 */
export interface QuestionCandidate {
  questionId: string;
  questionText: string;
  myAnswerText?: string;
  recommendedAnswer?: string;
  category?: string;
  difficulty: number;
  gotStuck: boolean;
  assessment?: string;
  /** 前端本地状态:是否勾选 / 行内编辑草稿。 */
  selected?: boolean;
  editText?: string;
  editAnswer?: string;
  editCategory?: string;
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
  /** 完整性巡检结论:'Ok' | 'Missing' | 'SizeMismatch' | 'HashMismatch'。 */
  integrityStatus?: string | null;
  /** 最近一次巡检时间(上传即首次校验)。 */
  lastVerifiedAt?: string | null;
  /** 2026-10-19:资产按 Round 管理。null = 历史遗留未归类材料。 */
  roundId?: string | null;
  /** Pre = 面试前，Post = 面试后。 */
  phase?: string | null;
  /** 2026-10-20 v2:统一入口元数据 JSON(解析字段/截图引用/AI 摘要/提取安排/处理状态)。 */
  metaJson?: string | null;
  /** 转写全文(详情接口在 AssetDto 里不下发全文;前端按需调 transcript 端点)。 */
  transcriptText?: string | null;
}

export interface InterviewWeakness {
  id: string;
  category: string;
  title: string;
  detail?: string;
  evidence?: string;
  severity: number;
  suggestion?: string;
  occurrenceCount: number;
  sourceType: string;
  createdAt: string;
}

export interface InterviewAction {
  id: string;
  title: string;
  detail?: string;
  dueDate?: string;
  isDone: boolean;
  createdAt: string;
}

export interface InterviewDetail extends InterviewEntry {
  assets?: InterviewAsset[];
  questions?: InterviewQuestion[];
  rounds?: InterviewRound[];
  weaknesses?: InterviewWeakness[];
  actions?: InterviewAction[];
  /** 关联的 Tracker 投递 id(沟通记录同源与简历分来源)。 */
  jobApplicationId?: string | null;
  questionsToAskJson?: string | null;
  debriefJson?: string | null;
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

export type MasteryLevel = 'New' | 'Learning' | 'Familiar' | 'Mastered' | 'NeedsReview';

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
  myAnswer?: string;
  betterAnswer?: string;
  keyPointsJson?: string;
  commonMistakesJson?: string;
  referencesJson?: string;
  sourcesJson?: string;
  isDue?: boolean;
  dueInDays?: number | null;
  reviewLogs?: KnowledgeReviewLog[];
  relations?: KnowledgeRelation[];
  createdAt?: string;
  updatedAt?: string;
  /** 票70:读详情时的 xmin 并发版本;详情编辑保存时原样回传,过期保存会被后端 409 拒绝。列表项不带此字段。 */
  version?: number;

  // ---- 来源标记(需求 6.4.2 / 6.4.4)----
  /** 来源实战机经条目 id。 */
  sourceInterviewEntryId?: string | null;
  sourceCompanyName?: string | null;
  /** 来源面试日期(YYYY-MM-DD)。 */
  sourceDate?: string | null;
  /** 第几轮被问到的。 */
  sourceRoundNo?: number | null;
  /** 轮次类型:Screen / Technical / SystemDesign / Behavioral / Final。 */
  sourceRoundStage?: string | null;
  /** 来源投递记录 id(可据此跳回 Tracker)。 */
  sourceJobApplicationId?: string | null;
  /** 导入幂等键(由导入方生成,重复导入靠它判重)。 */
  importKey?: string | null;
}

/** 多来源条目(JSON 数组元素):{公司,轮次号,轮次类型,日期,机经条目ID,投递ID} */
export interface KnowledgeSourceEntry {
  companyName?: string;
  roundNo?: number;
  roundStage?: string;
  date?: string;
  interviewEntryId?: string;
  roundId?: string;
  jobApplicationId?: string;
}

/** 来源公司汇总(GET /api/knowledge/sources)。 */
export interface KnowledgeSourceCompany {
  company: string;
  count: number;
  masteredCount: number;
  dueCount: number;
  lastDate?: string | null;
}

/** 单条知识点的来源反查结果(GET /api/knowledge/{id}/trail)。 */
export interface KnowledgeSourceTrail {
  itemId: string;
  company?: string | null;
  date?: string | null;
  roundNo?: number | null;
  roundStage?: string | null;
  entryId?: string | null;
  applicationId?: string | null;
  role?: string | null;
  siblings: KnowledgeItem[];
}

/** 重复条目分组(GET /api/knowledge/duplicates)。 */
export interface KnowledgeDuplicateGroup {
  topic: string;
  key: string;
  items: KnowledgeItem[];
}

/** 机经候选题导入结果(POST /api/knowledge/import-candidates)。 */
export interface CandidateImportResult {
  created: number;
  skipped: number;
  total: number;
  ids: string[];
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
  /** 后端 int?:无快照时为 null(JSON 中直接省略),见 radar.helpers.ts 的守卫口径。 */
  best: number | null;
  previous: number;
  changeFromPrevious: number;
}

/** FUN-07(票35):后端只回结构化 Code+Params,文案由前端 localized-messages.helpers 本地化。 */
export interface Insight {
  code: string;
  params: Record<string, string>;
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

/**
 * ★ M2.4:下面这几个管理后台类型之前的字段名是**凭空写的**,跟后端对不上 ——
 * 页面上统计卡显示 undefined、审计日志的"对象/IP"两列永远是 —。
 * 现在全部按后端实际返回的 JSON 改回真实字段名(小驼峰)。
 * 规则:这里的字段名严格跟着后端,不要自造。
 */
export interface AdminStats {
  totalUsers: number;
  activeUsers: number;
  newUsersLast7Days: number;
  totalRoles: number;
  totalAuditEvents: number;
  failedLoginsLast24h: number;
  lockedAccounts: number;
  signupsTrend: DailyCount[];
}

export interface DailyCount {
  date: string;
  count: number;
}

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  isActive: boolean;
  mustChangePassword: boolean;
  lastLoginAt?: string;
  createdAt: string;
  roles: string[];
  avatarUrl?: string;
  /** 锁定到期时间;大于当前时间即处于锁定状态。 */
  lockedUntil?: string;
  /** 票101:读时 xmin(管理员保存回传)。 */
  version?: number;
}

export interface AdminRole {
  id: string;
  name: string;
  description?: string;
  isSystemRole: boolean;
  permissions: string[];
  userCount: number;
  /** 票101:读时 xmin(角色保存回传)。 */
  version?: number;
}

/** GET /api/admin/permissions —— 权限点目录(按资源分组渲染勾选框)。 */
export interface AdminPermission {
  key: string;
  resource: string;
  action: string;
  description: string;
}

export interface AuditLog {
  id: string;
  userId?: string;
  userEmail?: string;
  action: string;
  resource: string;
  resourceId?: string;
  detail?: string;
  ipAddress?: string;
  success: boolean;
  occurredAt: string;
}
