import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { MockSession, Paged } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class AssessmentApiService {
  private readonly api = inject(ApiClient);

  getSessions(params?: { page?: number; pageSize?: number }): Observable<Paged<MockSession>> {
    return this.api.get<Paged<MockSession>>('/api/assessment/sessions', params);
  }

  createSession(body: unknown): Observable<{ id: string }> {
    return this.api.post<{ id: string }>('/api/assessment/sessions', body);
  }
}
