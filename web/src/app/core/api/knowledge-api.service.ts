import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from './api-client';
import { KnowledgeItem, KnowledgeTopic, Paged } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class KnowledgeApiService {
  private readonly api = inject(ApiClient);

  getItems(params?: { topic?: string; page?: number; pageSize?: number }): Observable<Paged<KnowledgeItem>> {
    return this.api.get<Paged<KnowledgeItem>>('/api/knowledge', params);
  }

  getItemsByTopic(topic: string, pageSize = 100): Observable<Paged<KnowledgeItem>> {
    return this.api.get<Paged<KnowledgeItem>>('/api/knowledge', { topic, pageSize });
  }

  createItem(body: unknown): Observable<unknown> {
    return this.api.post('/api/knowledge', body);
  }

  getTopics(): Observable<KnowledgeTopic[]> {
    return this.api.get<KnowledgeTopic[]>('/api/knowledge/topics');
  }

  importCandidates(body: unknown): Observable<unknown> {
    return this.api.post('/api/knowledge/import-candidates', body);
  }

  getStats(): Observable<unknown> {
    return this.api.get('/api/knowledge/stats');
  }

  getDuplicates(): Observable<unknown> {
    return this.api.get('/api/knowledge/duplicates');
  }

  merge(body: unknown): Observable<unknown> {
    return this.api.post('/api/knowledge/merge', body);
  }

  getSources(): Observable<unknown> {
    return this.api.get('/api/knowledge/sources');
  }
}
