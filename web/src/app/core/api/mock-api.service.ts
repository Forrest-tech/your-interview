import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { MockSession, Paged } from '../models/api.models';

/** Mock 会话专用 API 层（端点实际挂在 Assessment 服务）。 */
@Injectable({ providedIn: 'root' })
export class MockApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/assessment/sessions';

  getSessions(params?: { page?: number; pageSize?: number }): Observable<Paged<MockSession>> {
    return this.api.get<Paged<MockSession>>(MockApiService.BASE, params);
  }

  getSession<T>(id: string): Observable<T> {
    return this.api.get<T>(`${MockApiService.BASE}/${id}`);
  }

  createSession(body: unknown): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(MockApiService.BASE, body);
  }

  deleteSession(id: string): Observable<void> {
    return this.api.delete<void>(`${MockApiService.BASE}/${id}`);
  }

  answerQuestion<T>(sessionId: string, questionId: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${MockApiService.BASE}/${sessionId}/questions/${questionId}/answer`, body);
  }

  nextQuestion<T>(sessionId: string): Observable<T> {
    return this.api.post<T>(`${MockApiService.BASE}/${sessionId}/next-question`, {});
  }

  completeSession<T>(sessionId: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${MockApiService.BASE}/${sessionId}/complete`, body);
  }
}
