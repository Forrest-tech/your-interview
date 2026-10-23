import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';

import { AuthUser } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class AuthApiService {
  private readonly api = inject(ApiClient);

  getMe(): Observable<AuthUser> {
    return this.api.get<AuthUser>('/api/auth/me');
  }

  changePassword(body: unknown): Observable<unknown> {
    return this.api.post('/api/auth/change-password', body);
  }

  getGoogleAuthorizeUrl(returnUrl: string): string {
    return '/api/auth/google/authorize?returnUrl=' + encodeURIComponent(returnUrl);
  }
}
