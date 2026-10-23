import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';

/** 后端 UserProfileDto 子集。 */
export interface UserProfile {
  id: string;
  email?: string;
  name?: string;
  picture?: string;
}

@Injectable({ providedIn: 'root' })
export class AuthApiService {
  private readonly api = inject(ApiClient);

  getMe(): Observable<UserProfile> {
    return this.api.get<UserProfile>('/api/auth/me');
  }

  changePassword(body: unknown): Observable<unknown> {
    return this.api.post('/api/auth/change-password', body);
  }

  getGoogleAuthorizeUrl(returnUrl: string): string {
    return '/api/auth/google/authorize?returnUrl=' + encodeURIComponent(returnUrl);
  }
}
