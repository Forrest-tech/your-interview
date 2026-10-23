import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { InterviewEntry, Paged, UpcomingRound } from '../models/api.models';
import { CompanySummary, CoverageRow, InterviewStats } from './jobs-api.service';

@Injectable({ providedIn: 'root' })
export class InterviewsApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/interviews';

  getEntries(params?: { page?: number; pageSize?: number }): Observable<Paged<InterviewEntry>> {
    return this.api.get<Paged<InterviewEntry>>(`${InterviewsApiService.BASE}`, params);
  }

  createEntry(body: unknown): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${InterviewsApiService.BASE}`, body);
  }

  getCompanies(): Observable<CompanySummary[]> {
    return this.api.get<CompanySummary[]>(`${InterviewsApiService.BASE}/companies`);
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
}
