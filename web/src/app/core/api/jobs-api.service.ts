import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { ActionCenter, Paged } from '../models/api.models';

/** 后端 InterviewStatsDto 的子集 —— 顶部只放最该看的几个数。 */
export interface InterviewStats {
  totalEntries: number;
  analyzed: number;
  pendingAnalysis: number;
  gotStuckQuestions: number;
  averageOverallScore?: number;
}

/** 后端 CompanySummaryDto。 */
export interface CompanySummary {
  companyId: string;
  companyName: string;
  entryCount: number;
  analyzedCount: number;
  averageScore?: number;
  latestInterviewDate?: string;
  latestResult?: string;
}

/** 后端 CoverageCategoryDto。 */
export interface CoverageRow {
  category: string;
  askedCount: number;
  stuckCount: number;
  masteredCount: number;
  interviews: string[];
}

@Injectable({ providedIn: 'root' })
export class JobsApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/jobs';

  getStats(): Observable<InterviewStats> {
    return this.api.get<InterviewStats>(`${JobsApiService.BASE}/stats`);
  }

  getActionCenter(): Observable<ActionCenter> {
    return this.api.get<ActionCenter>(`${JobsApiService.BASE}/action-center`);
  }

  getApplications(params?: { page?: number; pageSize?: number }): Observable<Paged<unknown>> {
    return this.api.get<Paged<unknown>>(`${JobsApiService.BASE}/applications`, params);
  }

  createApplication(body: unknown): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${JobsApiService.BASE}/applications`, body);
  }

  parseJd(raw: string, useAi = true): Observable<unknown> {
    return this.api.post(`${JobsApiService.BASE}/parse-jd`, { raw, useAi });
  }

  // ---------- 简历 ----------

  getResumeText(): Observable<unknown> {
    return this.api.get(`${JobsApiService.BASE}/resume-text`);
  }

  saveResumeText(body: unknown): Observable<unknown> {
    return this.api.put(`${JobsApiService.BASE}/resume-text`, body);
  }

  uploadResumeFile(file: File): Observable<unknown> {
    return this.api.upload(`${JobsApiService.BASE}/resume-file`, file);
  }

  getResumeFileMeta(): Observable<unknown> {
    return this.api.get(`${JobsApiService.BASE}/resume-file/meta`);
  }
}
