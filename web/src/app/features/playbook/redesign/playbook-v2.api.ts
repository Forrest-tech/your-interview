import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ApiClient } from '../../../core/api/api-client';
import { KnowledgeItem, InterviewAsset } from '../../../core/models/api.models';
import { AssetMeta, CommItem, safeJson } from './v2.models';

/** Jobs 侧沟通记录 DTO(与 Tracker 同表)。 */
export interface JobsCommunication {
  id: string;
  applicationId: string;
  type: string;
  subject?: string | null;
  content: string;
  contactName?: string | null;
  contactEmail?: string | null;
  occurredAt: string;
  metaJson?: string | null;
  handlingStatus?: string | null;
}

/**
 * 详情页 v2 的数据访问层。所有新端点集中在这里,组件不直接拼 URL。
 */
@Injectable({ providedIn: 'root' })
export class PlaybookV2Api {
  private readonly api = inject(ApiClient);

  private base(entryId: string): string { return `/api/interviews/${entryId}`; }

  // ---------------- 材料 ----------------

  attachAsset(entryId: string, body: {
    kind: string;
    fileName: string;
    transcriptText?: string | null;
    transcriptSegmentsJson?: string | null;
    roundId?: string | null;
    phase?: string | null;
    metaJson?: string | null;
  }): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${this.base(entryId)}/assets`, body);
  }

  uploadFile(entryId: string, file: File, extra: Record<string, string>): Observable<InterviewAsset> {
    return this.api.upload<InterviewAsset>(`${this.base(entryId)}/assets/upload`, file, extra);
  }

  reassignAsset(entryId: string, assetId: string, roundId: string | null): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/assets/${assetId}/assignment`, { roundId });
  }

  updateAssetMeta(entryId: string, assetId: string, meta: AssetMeta): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/assets/${assetId}/meta`,
      { metaJson: JSON.stringify(meta) });
  }

  deleteAsset(entryId: string, assetId: string): Observable<unknown> {
    return this.api.delete(`${this.base(entryId)}/assets/${assetId}`);
  }

  getTranscript(entryId: string, assetId: string):
    Observable<{ transcriptText: string | null; segmentsJson: string | null }> {
    return this.api.get(`${this.base(entryId)}/assets/${assetId}/transcript`);
  }

  audioBlob(entryId: string, assetId: string): Observable<Blob> {
    return this.api.getBlob(`${this.base(entryId)}/assets/${assetId}/audio`);
  }

  // ---------------- 轮次 / 复盘 / 备考 ----------------

  addRound(entryId: string, body: {
    stage: string;
    scheduledDate?: string | null;
    scheduledTime?: string | null;
    interviewers?: string | null;
    format?: string | null;
    location?: string | null;
    meetingLink?: string | null;
  }): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${this.base(entryId)}/rounds`, body);
  }

  updateRound(entryId: string, roundId: string, body: Record<string, unknown>): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/rounds/${roundId}`, body);
  }

  deleteRound(entryId: string, roundId: string): Observable<unknown> {
    return this.api.delete(`${this.base(entryId)}/rounds/${roundId}`);
  }

  setQuestionReview(entryId: string, questionId: string, reviewJson: string): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/questions/${questionId}/review`, { reviewJson });
  }

  setRoundReview(entryId: string, roundId: string, reviewJson: string): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/rounds/${roundId}/review`, { reviewJson });
  }

  generateRoundReview(entryId: string, roundId: string): Observable<{ reviewJson: string }> {
    return this.api.post<{ reviewJson: string }>(
      `${this.base(entryId)}/rounds/${roundId}/review/generate`, {});
  }

  extractQuestions(entryId: string, roundId: string): Observable<{ created: number }> {
    return this.api.post<{ created: number }>(
      `${this.base(entryId)}/rounds/${roundId}/questions/extract`, {});
  }

  setPrepReport(entryId: string, roundId: string, prepReportJson: string): Observable<unknown> {
    return this.api.put(`${this.base(entryId)}/rounds/${roundId}/prep-report`, { prepReportJson });
  }

  generatePrepReport(entryId: string, roundId: string, body: {
    jdText?: string | null; resumeText?: string | null; experiences?: string | null;
  }): Observable<{ prepReportJson: string }> {
    return this.api.post<{ prepReportJson: string }>(
      `${this.base(entryId)}/rounds/${roundId}/prep-report/generate`, body);
  }

  renameSpeaker(entryId: string, roundId: string, from: string, to: string):
    Observable<{ changed: number }> {
    return this.api.post<{ changed: number }>(
      `${this.base(entryId)}/rounds/${roundId}/speakers/rename`, { from, to });
  }

  addQuestion(entryId: string, body: {
    questionText: string;
    myAnswerText?: string | null;
    recommendedAnswer?: string | null;
    category?: string | null;
    askedAtSeconds?: number | null;
    roundId?: string | null;
    askedBy?: string | null;
  }): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`${this.base(entryId)}/questions`, body);
  }

  startTranscription(entryId: string, assetId: string): Observable<unknown> {
    return this.api.post(`${this.base(entryId)}/assets/${assetId}/transcribe`, {});
  }

  runAnalysis(entryId: string): Observable<unknown> {
    return this.api.post(`${this.base(entryId)}/analyze`, {});
  }

  roundIcsUrl(entryId: string, roundId: string): string {
    return `${this.base(entryId)}/rounds/${roundId}/ics`;
  }

  downloadIcs(entryId: string, roundId: string): Observable<Blob> {
    return this.api.getBlob(this.roundIcsUrl(entryId, roundId));
  }

  // ---------------- Tech Stack(Knowledge) ----------------

  listKnowledge(): Observable<KnowledgeItem[]> {
    return this.api.get<KnowledgeItem[]>('/api/knowledge');
  }

  getKnowledge(id: string): Observable<KnowledgeItem> {
    return this.api.get<KnowledgeItem>(`/api/knowledge/${id}`);
  }

  importCandidates(body: {
    sourceInterviewEntryId?: string;
    sourceCompanyName?: string;
    sourceDate?: string;
    sourceRoundNo?: number;
    sourceRoundStage?: string;
    sourceJobApplicationId?: string;
    items: { question: string; myAnswer?: string; category?: string; difficulty?: number }[];
  }): Observable<{ created: number; skipped: number; itemIds: string[] }> {
    return this.api.post('/api/knowledge/import-candidates', body);
  }

  updateKnowledgeAnswer(id: string, body: {
    answer?: string; betterAnswer?: string; editNote?: string;
  }): Observable<unknown> {
    return this.api.put(`/api/knowledge/${id}/answer`, body);
  }

  addKnowledgeSource(id: string, body: Record<string, unknown>): Observable<unknown> {
    return this.api.post(`/api/knowledge/${id}/sources`, body);
  }

  // ---------------- 沟通记录(Jobs 同源) ----------------

  listCommunications(appId: string): Observable<JobsCommunication[]> {
    return this.api.get<JobsCommunication[]>(`/api/jobs/applications/${appId}/communications`);
  }

  createCommunication(appId: string, body: {
    type: string;
    subject?: string | null;
    content: string;
    contactName?: string | null;
    contactEmail?: string | null;
    occurredAt: string;
    metaJson?: string | null;
    handlingStatus?: string | null;
  }): Observable<{ id: string }> {
    return this.api.post<{ id: string }>(`/api/jobs/applications/${appId}/communications`, body);
  }

  setCommunicationMeta(appId: string, commId: string, body: {
    metaJson?: string | null;
    handlingStatus?: string | null;
  }): Observable<unknown> {
    return this.api.put(`/api/jobs/applications/${appId}/communications/${commId}/meta`, body);
  }

  deleteCommunication(appId: string, commId: string): Observable<unknown> {
    return this.api.delete(`/api/jobs/applications/${appId}/communications/${commId}`);
  }

  /** JobsCommunication → 统一展示模型。 */
  static commFromJobs(c: JobsCommunication): CommItem {
    const meta = safeJson<AssetMeta>(c.metaJson) ?? {};
    return {
      source: 'jobs',
      id: c.id,
      type: c.type,
      subject: c.subject,
      content: c.content,
      contactName: c.contactName,
      occurredAt: c.occurredAt,
      meta,
      handlingStatus: c.handlingStatus ?? meta.handlingStatus ?? null,
      assetId: (meta as unknown as { assetId?: string }).assetId
    };
  }

  /** 条目级材料(无关联投递时)→ 统一展示模型。 */
  static commFromAsset(a: InterviewAsset & { transcriptPreview?: string }): CommItem {
    const meta = safeJson<AssetMeta>(a.metaJson) ?? {};
    return {
      source: 'asset',
      id: a.id,
      type: a.kind,
      subject: meta.subject ?? a.fileName,
      content: a.transcriptText ?? '',
      contactName: meta.from ?? null,
      occurredAt: meta.date ?? a.uploadedAt,
      meta,
      handlingStatus: meta.handlingStatus ?? null,
      assetId: a.id
    };
  }

  /** 未处理判定:提取到安排且状态未落定。 */
  static isPending(c: CommItem): boolean {
    return !!c.meta.arrangement && c.handlingStatus !== 'Done' && c.handlingStatus !== 'Ignored';
  }
}
