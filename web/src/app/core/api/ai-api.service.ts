import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';

@Injectable({ providedIn: 'root' })
export class AiApiService {
  private readonly api = inject(ApiClient);

  complete(body: unknown): Observable<{ text?: string; content?: string }> {
    return this.api.post<{ text?: string; content?: string }>('/api/ai/complete', body);
  }
}
