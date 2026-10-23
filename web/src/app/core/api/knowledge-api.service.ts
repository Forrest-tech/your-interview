import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { KnowledgeDuplicateGroup, KnowledgeItem, KnowledgeSourceCompany, KnowledgeStats, KnowledgeTopic, Paged } from '../models/api.models';

/** 后端 ReviewOutcomeDto(POST /api/knowledge/{id}/review)。 */
export interface ReviewOutcome {
  nextReviewAt: string | null;
  intervalDays: number;
  easinessFactor: number;
  mastery: string;
  repetitionStreak: number;
}

@Injectable({ providedIn: 'root' })
export class KnowledgeApiService {
  private readonly api = inject(ApiClient);

  private static readonly BASE = '/api/knowledge';

  getItems(params?: Record<string, unknown>): Observable<Paged<KnowledgeItem>> {
    return this.api.get<Paged<KnowledgeItem>>(KnowledgeApiService.BASE, params);
  }

  getItemsByTopic(topic: string, pageSize = 100): Observable<Paged<KnowledgeItem>> {
    return this.api.get<Paged<KnowledgeItem>>(KnowledgeApiService.BASE, { topic, pageSize });
  }

  getItem<T = KnowledgeItem>(id: string): Observable<T> {
    return this.api.get<T>(`${KnowledgeApiService.BASE}/${id}`);
  }

  createItem(body: unknown): Observable<unknown> {
    return this.api.post(KnowledgeApiService.BASE, body);
  }

  updateItem<T = unknown>(id: string, body: unknown): Observable<T> {
    return this.api.put<T>(`${KnowledgeApiService.BASE}/${id}`, body);
  }

  deleteItem<T = void>(id: string, version?: number | null): Observable<T> {
    return this.api.delete<T>(`${KnowledgeApiService.BASE}/${id}`, ApiClient.ifMatch(version));
  }

  restoreItem<T = void>(id: string): Observable<T> {
    return this.api.post<T>(`${KnowledgeApiService.BASE}/${id}/restore`, {});
  }

  updateMastery<T = KnowledgeItem>(id: string, body: unknown): Observable<T> {
    return this.api.post<T>(`${KnowledgeApiService.BASE}/${id}/mastery`, body);
  }

  review(id: string, body: unknown): Observable<ReviewOutcome> {
    return this.api.post<ReviewOutcome>(`${KnowledgeApiService.BASE}/${id}/review`, body);
  }

  updateFromTech<T = void>(id: string, body: unknown): Observable<T> {
    return this.api.put<T>(`${KnowledgeApiService.BASE}/${id}/from-tech`, body);
  }

  updateBetterAnswer<T = void>(id: string, body: unknown): Observable<T> {
    return this.api.put<T>(`${KnowledgeApiService.BASE}/${id}/better-answer`, body);
  }

  getTopics(): Observable<KnowledgeTopic[]> {
    return this.api.get<KnowledgeTopic[]>(`${KnowledgeApiService.BASE}/topics`);
  }

  renameTopic<T = number>(oldName: string, newName: string): Observable<T> {
    return this.api.put<T>(`${KnowledgeApiService.BASE}/topics/rename`, { oldName, newName });
  }

  deleteTopic<T = number>(name: string): Observable<T> {
    return this.api.delete<T>(`${KnowledgeApiService.BASE}/topics/${encodeURIComponent(name)}`);
  }

  importCandidates(body: unknown): Observable<unknown> {
    return this.api.post(`${KnowledgeApiService.BASE}/import-candidates`, body);
  }

  getStats(): Observable<KnowledgeStats> {
    return this.api.get<KnowledgeStats>(`${KnowledgeApiService.BASE}/stats`);
  }

  getDuplicates(): Observable<KnowledgeDuplicateGroup[]> {
    return this.api.get<KnowledgeDuplicateGroup[]>(`${KnowledgeApiService.BASE}/duplicates`);
  }

  getSources(): Observable<KnowledgeSourceCompany[]> {
    return this.api.get<KnowledgeSourceCompany[]>(`${KnowledgeApiService.BASE}/sources`);
  }

  merge(body: unknown): Observable<unknown> {
    return this.api.post(`${KnowledgeApiService.BASE}/merge`, body);
  }
}
