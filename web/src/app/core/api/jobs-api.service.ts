import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { ActionCenter, Application, Company, Paged } from '../models/api.models';
import { ApplicationStatus } from '../models/api.models';

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

  getApplications<T = unknown>(params?: Record<string, unknown>): Observable<Paged<T>> {
    return this.api.get<Paged<T>>(`${JobsApiService.BASE}/applications`, params);
  }

  getApplication(id: string): Observable<Application> {
    return this.api.get<Application>(`${JobsApiService.BASE}/applications/${id}`);
  }

  createApplication<T = { id: string }>(body: unknown): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications`, body);
  }

  updateApplication<T = void>(id: string, body: unknown): Observable<T> {
    return this.api.put<T>(`${JobsApiService.BASE}/applications/${id}`, body);
  }

  deleteApplication<T = void>(id: string): Observable<T> {
    return this.api.delete<T>(`${JobsApiService.BASE}/applications/${id}`);
  }

  changeStatus<T = void>(id: string, status: ApplicationStatus, note?: string | null): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications/${id}/status`, { status, note });
  }

  archive<T = void>(id: string): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications/${id}/archive`, {});
  }

  unarchive<T = void>(id: string): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications/${id}/unarchive`, {});
  }

  updateOutreach<T = void>(id: string, body: {
    posterName?: string | null;
    needsConnectFirst: boolean;
    message: string | null;
  }): Observable<T> {
    return this.api.put<T>(`${JobsApiService.BASE}/applications/${id}/outreach`, body);
  }

  parseJd(raw: string, useAi = true): Observable<unknown> {
    return this.api.post(`${JobsApiService.BASE}/parse-jd`, { raw, useAi });
  }

  saveApplicationJd<T = void>(id: string, body: { jdText: string | null; jdSourceUrl?: string | null; version?: number | null }): Observable<T> {
    return this.api.put<T>(`${JobsApiService.BASE}/applications/${id}/jd`, body);
  }

  // ---------- 简历 ----------

  getResumeText(): Observable<{ resumeText: string | null; version?: number; rowVersion?: number }> {
    return this.api.get<{ resumeText: string | null }>(`${JobsApiService.BASE}/resume-text`);
  }

  saveResumeText(body: unknown): Observable<unknown> {
    return this.api.put(`${JobsApiService.BASE}/resume-text`, body);
  }

  uploadResumeFile(file: File): Observable<unknown> {
    return this.api.upload(`${JobsApiService.BASE}/resume-file`, file);
  }

  getResumeFileMeta(): Observable<{ fileName: string }> {
    return this.api.get<{ fileName: string }>(`${JobsApiService.BASE}/resume-file/meta`);
  }

  uploadResumeFileForm(fd: FormData): Observable<unknown> {
    return this.api.postForm(`${JobsApiService.BASE}/resume-file`, fd);
  }

  getResumeFile(): Observable<Blob> {
    return this.api.getBlob(`${JobsApiService.BASE}/resume-file`);
  }

  deleteResumeFile(): Observable<unknown> {
    return this.api.delete(`${JobsApiService.BASE}/resume-file`);
  }

  getApplicationResume(appId: string): Observable<{ kind?: string; fileName?: string | null; textContent?: string | null }> {
    return this.api.get<{ kind?: string; fileName?: string | null; textContent?: string | null }>(
      `${JobsApiService.BASE}/applications/${appId}/resume`);
  }

  uploadApplicationResume<T = unknown>(appId: string, fd: FormData): Observable<T> {
    return this.api.postForm<T>(`${JobsApiService.BASE}/applications/${appId}/resume`, fd);
  }

  deleteApplicationResume<T = unknown>(appId: string): Observable<T> {
    return this.api.delete<T>(`${JobsApiService.BASE}/applications/${appId}/resume`);
  }

  getApplicationResumeFile(appId: string): Observable<Blob> {
    return this.api.getBlob(`${JobsApiService.BASE}/applications/${appId}/resume-file`);
  }

  getCompany(id: string): Observable<Company> {
    return this.api.get<Company>(`${JobsApiService.BASE}/companies/${id}`);
  }

  // ---------- 求职信 ----------

  getCoverLetter<T = unknown>(appId: string): Observable<T> {
    return this.api.get<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter`);
  }

  getCoverLetterReadiness<T = unknown>(appId: string): Observable<T> {
    return this.api.get<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter/readiness`);
  }

  generateCoverLetter<T = unknown>(appId: string, body: {
    extraInstructions?: string | null;
    overwrite?: boolean;
  }): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter/generate`, body);
  }

  saveCoverLetter<T = unknown>(appId: string, content: string, version?: number | null): Observable<T> {
    // 票101:回传读时 xmin(过期后端 409);不传 = 旧行为不钉版
    return this.api.put<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter`, { content, version: version ?? null });
  }

  finalizeCoverLetter<T = unknown>(appId: string): Observable<T> {
    return this.api.post<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter/final`, {});
  }

  deleteCoverLetter<T = unknown>(appId: string): Observable<T> {
    return this.api.delete<T>(`${JobsApiService.BASE}/applications/${appId}/cover-letter`);
  }
}
