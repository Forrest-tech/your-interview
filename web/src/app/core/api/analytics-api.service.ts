import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { AbilityTrend, Dashboard } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class AnalyticsApiService {
  private readonly api = inject(ApiClient);

  getDashboard(): Observable<Dashboard> {
    return this.api.get<Dashboard>('/api/analytics/dashboard');
  }

  getAbilityTrend(days = 30): Observable<AbilityTrend> {
    return this.api.get<AbilityTrend>('/api/analytics/ability/trend', { days });
  }
}
