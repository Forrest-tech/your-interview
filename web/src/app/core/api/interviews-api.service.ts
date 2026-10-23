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
}
