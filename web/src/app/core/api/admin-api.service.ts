import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { AdminPermission, AdminRole, AdminStats, AdminUser, AuditLog, Paged } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/admin';

  getStats(): Observable<AdminStats> {
    return this.api.get<AdminStats>(`${AdminApiService.BASE}/stats`);
  }

  getUsers(params?: {
    page?: number; pageSize?: number; search?: string; role?: string; isActive?: string
  }): Observable<Paged<AdminUser>> {
    return this.api.get<Paged<AdminUser>>(`${AdminApiService.BASE}/users`, params);
  }

  createUser(form: Partial<AdminUser>): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${AdminApiService.BASE}/users`, form);
  }

  updateUser(id: string, form: Record<string, unknown>): Observable<void> {
    return this.api.put<void>(`${AdminApiService.BASE}/users/${id}`, form);
  }

  resetPassword(id: string, newPassword: string): Observable<void> {
    return this.api.post<void>(`${AdminApiService.BASE}/users/${id}/reset-password`, { newPassword });
  }

  unlockUser(id: string): Observable<void> {
    return this.api.post<void>(`${AdminApiService.BASE}/users/${id}/unlock`, {});
  }

  deleteUser(id: string): Observable<void> {
    return this.api.delete<void>(`${AdminApiService.BASE}/users/${id}`);
  }

  getRoles(): Observable<AdminRole[]> {
    return this.api.get<AdminRole[]>(`${AdminApiService.BASE}/roles`);
  }

  getPermissions(): Observable<AdminPermission[]> {
    return this.api.get<AdminPermission[]>(`${AdminApiService.BASE}/permissions`);
  }

  createRole(form: Partial<AdminRole>): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${AdminApiService.BASE}/roles`, form);
  }

  updateRole(id: string, form: Partial<AdminRole>): Observable<void> {
    return this.api.put<void>(`${AdminApiService.BASE}/roles/${id}`, form);
  }

  deleteRole(id: string): Observable<void> {
    return this.api.delete<void>(`${AdminApiService.BASE}/roles/${id}`);
  }

  getAudit(params?: Record<string, unknown>): Observable<Paged<AuditLog>> {
    return this.api.get<Paged<AuditLog>>(`${AdminApiService.BASE}/audit`, params);
  }
}
