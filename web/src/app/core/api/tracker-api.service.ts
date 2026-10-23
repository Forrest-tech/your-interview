import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ApiClient } from './api-client';
import { AnswerTemplate, Communication, CommunicationType } from '../models/api.models';

/** 后端 ParsedJdDto。Status: Ok / Partial / Failed。 */
export interface ParsedJd {
  sourceKind: 'Url' | 'Text' | 'Image' | string;
  status: 'Ok' | 'Partial' | 'Failed' | string;
  /** FUN-07(票35):结构化码列表(原 message 中文句已移除),前端 parseMessageText 本地化。 */
  messageCodes: string[];
  company: string | null;
  role: string | null;
  location: string | null;
  salary: string | null;
  workMode: string | null;
  jobType: string | null;
  jdSummary: string | null;
  matchKeywords: string | null;
  priority: string | null;
  link: string | null;
  jdText: string | null;
  companyDescription: string | null;
  requirements: string | null;
  benefits: string | null;
  workAuthorization: string | null;
  deadline: string | null;
  detectedSource: string | null;
  confidence: Record<string, number>;
  usedAi: boolean;
  needsManualReview: boolean;
}

/** 后端 ParsedInviteDto。 */
export interface ParsedInvite {
  status: 'Ok' | 'Partial' | 'Failed' | string;
  /** FUN-07(票35):结构化码列表(原 message 中文句已移除),前端 parseMessageText 本地化。 */
  messageCodes: string[];
  company: string | null;
  role: string | null;
  roundNo: number | null;
  stage: string | null;
  scheduledAt: string | null;
  location: string | null;
  format: string | null;
  interviewer: string | null;
  rawLink: string | null;
  confidence: Record<string, number>;
  usedAi: boolean;
  needsManualReview: boolean;
}

/** 后端 FavoriteDto(COD-10a/票14):收藏 = (applicationId + createdAt)。 */
export interface FavoriteDto {
  applicationId: string;
  createdAt: string;
}

/** 后端 ApplicationQuestionDto(COD-10a/票14):按投递自备问题。 */
export interface ApplicationQuestionDto {
  id: string;
  applicationId: string;
  text: string;
  sortOrder: number;
  createdAt: string;
  updatedAt: string | null;
}

/** 一次性导入(localStorage → 后端)的对账结果。 */
export interface ImportResultDto {
  imported: number;
  total: number;
}

/** 建档结果。 */
export interface SmartAddResult {
  applicationId: string;
  companyId: string;
  companyName: string;
  companyCreated: boolean;
  status: string;
}

/** 邀请关联结果。 */
export interface LinkInviteResult {
  applicationId: string;
  roundId: string;
  roundNo: number;
  status: string;
}

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

  // ---------- 智能粘贴(需求 6.2.2 / 6.2.3) ----------

  /** 解析粘贴的 JD 链接 / 文本 / 图片,返回可编辑草稿。 */
  parseJd(raw: string, useAi = true): Observable<ParsedJd> {
    return this.api.post<ParsedJd>(`${TrackerApi.BASE}/parse-jd`, { raw, useAi });
  }

  /** 2026-10-19:LinkedIn 职位页自动抓取（后端用登录态）。 */
  fetchLinkedIn(url: string): Observable<any> {
    return this.api.post<any>(`${TrackerApi.BASE}/fetch-linkedin`, { url });
  }

  /** 把(用户改过的)草稿落成公司与投递记录。 */
  smartAdd(body: {
    company: string; role: string; location?: string | null; salary?: string | null;
    workMode?: string | null; jobType?: string | null; jdSummary?: string | null; matchKeywords?: string | null;
    priority?: string | null; link?: string | null; jdText?: string | null;
    source?: string | null; status?: string | null; deadline?: string | null;
    companyDescription?: string | null; workAuthorization?: string | null;
    requirements?: string | null; benefits?: string | null;
  }): Observable<SmartAddResult> {
    return this.api.post<SmartAddResult>(`${TrackerApi.BASE}/applications/smart-add`, body);
  }

  /** 解析面试邀请邮件/消息。 */
  parseInvite(raw: string, useAi = true): Observable<ParsedInvite> {
    return this.api.post<ParsedInvite>(`${TrackerApi.BASE}/parse-invite`, { raw, useAi });
  }

  /**
   * 一键关联邀请:登记轮次并推进状态。
   * 后端会顺带发出"邀请已登记"事件去建机经草稿,所以这里只调一次即可。
   */
  linkInvite(applicationId: string, body: {
    stage: string | null; scheduledDate: string | null; interviewer: string | null;
    format: string | null; notes: string | null;
  }): Observable<LinkInviteResult> {
    return this.api.post<LinkInviteResult>(
      `${TrackerApi.BASE}/applications/${applicationId}/invite`, body);
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

  // ---------- Tracker 收藏(COD-10a/票14,原 localStorage yi-tracker-favorites) ----------

  listFavorites(): Observable<FavoriteDto[]> {
    return this.api.get<FavoriteDto[]>(`${TrackerApi.BASE}/favorites`);
  }

  addFavorite(applicationId: string): Observable<void> {
    return this.api.put<void>(`${TrackerApi.BASE}/favorites/${applicationId}`, {});
  }

  removeFavorite(applicationId: string): Observable<void> {
    return this.api.delete<void>(`${TrackerApi.BASE}/favorites/${applicationId}`);
  }

  /** 一次性导入本地收藏(幂等,后端按归属+去重处理,返回对账)。 */
  importFavorites(applicationIds: string[]): Observable<ImportResultDto> {
    return this.api.post<ImportResultDto>(`${TrackerApi.BASE}/favorites/import`, { applicationIds });
  }

  // ---------- 按投递自备问题(COD-10a/票14,原 localStorage yi-tracker-questions-{id}) ----------

  listQuestions(applicationId: string): Observable<ApplicationQuestionDto[]> {
    return this.api.get<ApplicationQuestionDto[]>(
      `${TrackerApi.BASE}/applications/${applicationId}/questions`);
  }

  addQuestion(applicationId: string, text: string): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(
      `${TrackerApi.BASE}/applications/${applicationId}/questions`, { text });
  }

  deleteQuestion(applicationId: string, questionId: string): Observable<void> {
    return this.api.delete<void>(
      `${TrackerApi.BASE}/applications/${applicationId}/questions/${questionId}`);
  }

  /** 一次性导入某条投递的本地自备问题(幂等,后端按正文去重,返回对账)。 */
  importQuestions(applicationId: string, questions: string[]): Observable<ImportResultDto> {
    return this.api.post<ImportResultDto>(
      `${TrackerApi.BASE}/applications/${applicationId}/questions/import`, { questions });
  }
}
