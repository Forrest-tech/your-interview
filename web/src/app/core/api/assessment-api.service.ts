import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { MockSession, Paged } from '../models/api.models';

/** 会话详情（Assessment 的 SessionDetailDto，字段名严格对齐后端）。 */
export interface AssessmentSessionDetail {
  id: string;
  title: string;
  mode: string;
  topic?: string;
  status: string;
  questionCount?: number;
  startedAt: string;
  completedAt?: string;
  overallSummary?: string;
  priorityAction?: string;
  overallScore?: number;
  averageScores?: unknown;
  questions: unknown[];
  weaknesses?: { dimension: string; count: number; avgSeverity: number }[];
}

/** 会话结束回包（POST .../complete）。 */
export interface AssessmentSessionComplete {
  overallScore?: number;
  scoredQuestions: number;
  weaknesses: { dimension: string; count: number; avgSeverity: number }[];
  weakestDimension?: string;
}

@Injectable({ providedIn: 'root' })
export class AssessmentApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/assessment/sessions';

  getSessions(params?: { page?: number; pageSize?: number }): Observable<Paged<MockSession>> {
    return this.api.get<Paged<MockSession>>(`${AssessmentApiService.BASE}`, params);
  }

  createSession(body: unknown): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${AssessmentApiService.BASE}`, body);
  }

  deleteSession(id: string): Observable<void> {
    return this.api.delete<void>(`${AssessmentApiService.BASE}/${id}`);
  }

  getSession(id: string): Observable<AssessmentSessionDetail> {
    return this.api.get<AssessmentSessionDetail>(`${AssessmentApiService.BASE}/${id}`);
  }

  answerQuestion(sessionId: string, questionId: string, body: unknown): Observable<void> {
    return this.api.post<void>(
      `${AssessmentApiService.BASE}/${sessionId}/questions/${questionId}/answer`, body);
  }


  completeSession(sessionId: string, body: unknown): Observable<AssessmentSessionComplete> {
    return this.api.post<AssessmentSessionComplete>(
      `${AssessmentApiService.BASE}/${sessionId}/complete`, body);
  }
}
