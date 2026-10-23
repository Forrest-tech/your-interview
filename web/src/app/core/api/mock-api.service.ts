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

  deleteSession(id: string, version?: number | null): Observable<void> {
    return this.api.delete<void>(`${MockApiService.BASE}/${id}`, ApiClient.ifMatch(version));
  }

  answerQuestion<T>(sessionId: string, questionId: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${MockApiService.BASE}/${sessionId}/questions/${questionId}/answer`, body);
  }

  /** 票95:后端编排 AI 出题(幂等补差额)。 */
  generateQuestions(sessionId: string): Observable<{ questionCount: number }> {
    return this.api.post<{ questionCount: number }>(`${MockApiService.BASE}/${sessionId}/generate`, {});
  }

  /** 票95:后端编排 AI 评分(答后调用,已评分幂等)。 */
  scoreWithAi(sessionId: string, questionId: string): Observable<{ scored: boolean }> {
    return this.api.post<{ scored: boolean }>(
      `${MockApiService.BASE}/${sessionId}/questions/${questionId}/score-ai`, {});
  }

  /** 票95:Suggest 推荐(最弱维度) —— 列表页推荐卡数据源。 */
  suggest<T>(questionCount = 5): Observable<T> {
    return this.api.get<T>('/api/assessment/suggest', { questionCount });
  }

  completeSession<T>(sessionId: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${MockApiService.BASE}/${sessionId}/complete`, body);
  }
}
