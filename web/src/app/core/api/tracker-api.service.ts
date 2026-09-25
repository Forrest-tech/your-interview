import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ApiClient } from './api-client';
import { AnswerTemplate, Communication, CommunicationType } from '../models/api.models';

/**
 * Tracker(投递跟踪)后端访问层(M3)。
 *
 * 与 practice-api.service.ts 同构:端点路径只写一遍,组件只管交互。
 * 覆盖三块:M3 新增的「申请问答库」「沟通记录」,以及新建投递所需的公司解析。
 */
@Injectable({ providedIn: 'root' })
export class TrackerApi {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/jobs';

  // ---------- 公司(新建投递时按名解析/创建,幂等) ----------

  /** 按公司名解析或创建公司,返回其 id(已存在直接返回,不重复创建)。 */
  resolveCompany(name: string): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${TrackerApi.BASE}/companies`, { name });
  }

  // ---------- 申请问答库(用户级) ----------

  listAnswerTemplates(category?: string): Observable<AnswerTemplate[]> {
    return this.api.get<AnswerTemplate[]>(
      `${TrackerApi.BASE}/answer-templates`, category ? { category } : {});
  }

  createAnswerTemplate(body: { category: string; question: string; answer: string }):
    Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${TrackerApi.BASE}/answer-templates`, body);
  }

  updateAnswerTemplate(id: string, body: { category: string; question: string; answer: string }):
    Observable<void> {
    return this.api.put<void>(`${TrackerApi.BASE}/answer-templates/${id}`, body);
  }

  deleteAnswerTemplate(id: string): Observable<void> {
    return this.api.delete<void>(`${TrackerApi.BASE}/answer-templates/${id}`);
  }

  // ---------- 沟通记录(投递级) ----------

  listCommunications(applicationId: string): Observable<Communication[]> {
    return this.api.get<Communication[]>(
      `${TrackerApi.BASE}/applications/${applicationId}/communications`);
  }

  createCommunication(applicationId: string, body: {
    type: CommunicationType; subject: string | null; content: string;
    contactName: string | null; contactEmail: string | null; occurredAt: string;
  }): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(
      `${TrackerApi.BASE}/applications/${applicationId}/communications`, body);
  }

  updateCommunication(applicationId: string, commId: string, body: {
    type: CommunicationType; subject: string | null; content: string;
    contactName: string | null; contactEmail: string | null; occurredAt: string;
  }): Observable<void> {
    return this.api.put<void>(
      `${TrackerApi.BASE}/applications/${applicationId}/communications/${commId}`, body);
  }

  deleteCommunication(applicationId: string, commId: string): Observable<void> {
    return this.api.delete<void>(
      `${TrackerApi.BASE}/applications/${applicationId}/communications/${commId}`);
  }
}
