import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { Application, InterviewDetail, InterviewEntry, Paged, RoundReminderDto, UpcomingRound } from '../models/api.models';
import { CompanySummary, CoverageRow, InterviewStats } from './jobs-api.service';

/** 后端 PrepPlanDto。 */
export interface PrepPlanResult {
  assetId: string;
  entryId: string;
  roundId: string;
  roundOrder: number;
  markdown: string;
  model?: string;
  generatedAt: string;
}

@Injectable({ providedIn: 'root' })
export class InterviewsApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/interviews';

  getEntries(params?: Record<string, unknown>): Observable<Paged<InterviewEntry>> {
    return this.api.get<Paged<InterviewEntry>>(`${InterviewsApiService.BASE}`, params);
  }

  createEntry<T = { id: string }>(body: unknown): Observable<T> {
    return this.api.post<T>(`${InterviewsApiService.BASE}`, body);
  }

  deleteEntry(id: string, version?: number | null): Observable<void> {
    return this.api.delete<void>(`${InterviewsApiService.BASE}/${id}`, ApiClient.ifMatch(version));
  }

  getEntry(id: string): Observable<InterviewDetail> {
    return this.api.get<InterviewDetail>(`${InterviewsApiService.BASE}/${id}`);
  }

  updateEntry(id: string, body: unknown): Observable<unknown> {
    return this.api.put(`${InterviewsApiService.BASE}/${id}`, body);
  }

  linkApplication(id: string, applicationId: string): Observable<unknown> {
    return this.api.post(`${InterviewsApiService.BASE}/${id}/link-application`, { jobApplicationId: applicationId });
  }

  createAction(id: string, title: string): Observable<unknown> {
    return this.api.post(`${InterviewsApiService.BASE}/${id}/actions`, { title });
  }

  updateAction(id: string, actionId: string, isDone: boolean): Observable<unknown> {
    return this.api.patch(`${InterviewsApiService.BASE}/${id}/actions/${actionId}`, { isDone });
  }

  deleteAction(id: string, actionId: string): Observable<unknown> {
    return this.api.delete(`${InterviewsApiService.BASE}/${id}/actions/${actionId}`);
  }

  getCompanies(): Observable<CompanySummary[]> {
    return this.api.get<CompanySummary[]>(`${InterviewsApiService.BASE}/companies`);
  }

  getCompanyRounds(companyId: string): Observable<{ roundId: string; entryId: string; order: number; stage: string; scheduledDate?: string }[]> {
    return this.api.get<{ roundId: string; entryId: string; order: number; stage: string; scheduledDate?: string }[]>(
      `${InterviewsApiService.BASE}/companies/${companyId}/rounds`);
  }

  getStats(): Observable<InterviewStats> {
    return this.api.get<InterviewStats>(`${InterviewsApiService.BASE}/stats`);
  }

  getCoverage(): Observable<CoverageRow[]> {
    return this.api.get<CoverageRow[]>(`${InterviewsApiService.BASE}/coverage`);
  }

  getUpcomingRounds(days = 30): Observable<UpcomingRound[]> {
    return this.api.get<UpcomingRound[]>(`${InterviewsApiService.BASE}/rounds/upcoming`, { days });
  }

  /** CONF-CAL-2:我的全部待触发轮次提醒(后端查询时对账生成,按触发时刻升序)。 */
  getReminders(): Observable<RoundReminderDto[]> {
    return this.api.get<RoundReminderDto[]>(`${InterviewsApiService.BASE}/reminders`);
  }

  /** CONF-CAL-2:已到期未确认的提醒(日历页横幅数据源)。 */
  getDueReminders(): Observable<RoundReminderDto[]> {
    return this.api.get<RoundReminderDto[]>(`${InterviewsApiService.BASE}/reminders/due`);
  }

  /** CONF-CAL-2:确认一条提醒(点「知道了」);成功后该提醒离开列表。 */
  acknowledgeReminder(id: string): Observable<void> {
    return this.api.post<void>(`${InterviewsApiService.BASE}/reminders/${id}/ack`, {});
  }

  createPrepPlan<T = PrepPlanResult>(entryId: string, roundId: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${InterviewsApiService.BASE}/${entryId}/rounds/${roundId}/prep-plan`, body);
  }

  // ---------------- 票 240：邮箱直连导入 ----------------

  /** 发起邮箱连接：返回授权跳转 URL（前端直接跳转）。 */
  connectMailbox(provider: string): Observable<{ authUrl: string }> {
    return this.api.post<{ authUrl: string }>(`${InterviewsApiService.BASE}/mailbox/connect`, { provider });
  }

  getMailboxConnections(): Observable<MailboxConnectionDto[]> {
    return this.api.get<MailboxConnectionDto[]>(`${InterviewsApiService.BASE}/mailbox/connections`);
  }

  disconnectMailbox(id: string): Observable<void> {
    return this.api.delete<void>(`${InterviewsApiService.BASE}/mailbox/connections/${id}`);
  }

  /** 手动刷新：entryId 缺省 = 全部条目规则；否则仅该条目规则。 */
  refreshMailbox(entryId?: string | null): Observable<RefreshMailboxResult> {
    return this.api.post<RefreshMailboxResult>(`${InterviewsApiService.BASE}/mailbox/refresh`,
      { entryId: entryId ?? null });
  }

  getMailboxPending(entryId?: string | null): Observable<MailboxPendingDto[]> {
    return this.api.get<MailboxPendingDto[]>(`${InterviewsApiService.BASE}/mailbox/pending`,
      entryId ? { entryId } : undefined);
  }

  confirmMailboxPending(id: string, entryId?: string | null): Observable<{ assetId: string }> {
    return this.api.post<{ assetId: string }>(
      `${InterviewsApiService.BASE}/mailbox/pending/${id}/confirm`, { entryId: entryId ?? null });
  }

  ignoreMailboxPending(id: string): Observable<void> {
    return this.api.post<void>(`${InterviewsApiService.BASE}/mailbox/pending/${id}/ignore`, {});
  }

  getMailboxImports(entryId?: string | null): Observable<MailboxImportDto[]> {
    return this.api.get<MailboxImportDto[]>(`${InterviewsApiService.BASE}/mailbox/imports`,
      entryId ? { entryId } : undefined);
  }

  /** 票 298：条目最新邮件日期（yyyy-MM-dd 或 null）。 */
  getMailboxLatestMailDate(entryId: string): Observable<{ latestMailDate: string | null }> {
    return this.api.get<{ latestMailDate: string | null }>(
      `${InterviewsApiService.BASE}/mailbox/latest-date`, { entryId });
  }

  /** 条目级匹配规则读（条目详情页邮箱匹配区用；设置页绝不放规则）。 */
  getMailMatchRules(entryId: string): Observable<MailMatchRulesDto> {
    return this.api.get<MailMatchRulesDto>(`${InterviewsApiService.BASE}/${entryId}/mail-match-rules`);
  }

  updateMailMatchRules(entryId: string, body: MailMatchRulesDto): Observable<MailMatchRulesDto> {
    return this.api.put<MailMatchRulesDto>(`${InterviewsApiService.BASE}/${entryId}/mail-match-rules`, body);
  }

  // ---------------- 票 245：邮箱 OAuth 凭据卡 ----------------

  /** OAuth 凭据卡读（Secret 永不下发，只给状态位）。 */
  getMailboxOAuthSettings(): Observable<MailboxOAuthSettingsDto[]> {
    return this.api.get<MailboxOAuthSettingsDto[]>(`${InterviewsApiService.BASE}/mailbox/oauth-settings`);
  }

  /** OAuth 凭据卡保存（Secret 留空 = 沿用已存密文，首次保存必填）。 */
  saveMailboxOAuthSettings(body: SaveMailboxOAuthSettingsBody): Observable<MailboxOAuthSettingsDto> {
    return this.api.put<MailboxOAuthSettingsDto>(`${InterviewsApiService.BASE}/mailbox/oauth-settings`, body);
  }

  /** OAuth 凭据卡清除（删 DB 行，恢复环境变量兜底）。 */
  clearMailboxOAuthSettings(provider: string): Observable<void> {
    return this.api.delete<void>(`${InterviewsApiService.BASE}/mailbox/oauth-settings/${provider}`);
  }
}

/** 票 240：邮箱连接行（refresh token 永不下发，只给状态）。 */
export interface MailboxConnectionDto {
  id: string;
  provider: string;
  accountEmail: string;
  scope: string;
  status: string;
  connectedAt: string;
  lastRefreshAt: string | null;
  hasCursor: boolean;
}

/** 票 245：OAuth 凭据卡行（Secret 永不下发，只给状态位）。 */
export interface MailboxOAuthSettingsDto {
  provider: string;
  source: string;
  clientId: string;
  redirectUri: string;
  secretSaved: boolean;
  secretUnreadable: boolean;
}

/** 票 245：OAuth 凭据卡保存体（Secret 留空 = 沿用已存密文）。 */
export interface SaveMailboxOAuthSettingsBody {
  provider: string;
  clientId: string;
  clientSecret: string | null;
  redirectUri: string;
}

/** 票 240：手动刷新结果条（三数 + 失败说明 + 状态旗）。 */
export interface RefreshMailboxResult {
  added: number;
  pending: number;
  skippedDuplicates: number;
  failed: number;
  failedReasons: string[];
  cancelled: boolean;
  rateLimited: boolean;
  needsReconnect: boolean;
  cursorAdvanced: boolean;
}

/** 票 240：待确认队列行（不含原文；确认时后端重取）。 */
export interface MailboxPendingDto {
  id: string;
  provider: string;
  accountEmail: string;
  fromEmail: string;
  fromName: string | null;
  subject: string;
  receivedAt: string;
  signal: string;
  matchedRule: string;
  entryId: string | null;
  candidateEntryIds: string[];
  snippet: string | null;
  status: string;
  createdAt: string;
}

/** 票 240：导入审计行。 */
export interface MailboxImportDto {
  id: string;
  entryId: string;
  assetId: string | null;
  provider: string;
  accountEmail: string;
  providerMessageId: string;
  matchedRule: string;
  rawMimeBytes: number;
  isNew: boolean;
  importedAt: string;
}

/** 票 240：条目级匹配规则（域名 / 往来人 / 关键词 / 排除列表）。 */
export interface MailMatchRulesDto {
  entryId: string;
  domains: string[];
  contacts: string[];
  keywords: string[];
  exclusions: string[];
}
