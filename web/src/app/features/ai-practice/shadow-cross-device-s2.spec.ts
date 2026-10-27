/**
 * 票 259 S2：跨设备跟读（人声跟人走）— 前端契约单测。
 *   · 分类：serverOnlyShadowRows 取服务端 TakeRate（缺省 1 = S1 旧行）；
 *   · 客户端：真实 PracticeApi 的 voice-track 两端点（URL/方法/表单字段逐字，
 *     票124 F1 同款 inject 钩子 + 假 HttpClient）；
 *   · i18n/模板契约：两新键三语齐全；重试钮两处（行内 + 展开区）均只在
 *     voiceTrackFailedFor 命中该行时出现、且只调 retryVoiceTrack。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it, afterEach } from '@jest/globals';
import { firstValueFrom, of } from 'rxjs';
import * as ngCore from '@angular/core';
import { setInjectProvider } from '../../../../test/jest-stub';
import { PracticeApi } from '../../core/api/practice-api.service';
import { serverOnlyShadowRows } from './practice-shadow-session';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

// ===========================================================
// inject 钩子（票124 F1 同款）：真实源文件经 stub 拿到假 HttpClient。
// ===========================================================

interface Recorded {
  method: 'GET' | 'POST';
  url: string;
  body?: unknown;
  opts?: unknown;
}

function installFakeHttp(responder: (r: Recorded) => unknown): Recorded[] {
  const calls: Recorded[] = [];
  // 注意：PracticeApi 经 inject 拿到的是 ApiClient —— 假对象须同时实现
  // ApiClient 口径（get/post/getBlob/postForm），而非裸 HttpClient；
  // postForm/getBlob 在此直接记成同一张请求表（与真实 ApiClient 的转发一致）。
  const http = {
    get: (url: string, opts?: unknown) => {
      const rec: Recorded = { method: 'GET', url, opts };
      calls.push(rec);
      return responder(rec);
    },
    post: (url: string, body?: unknown, opts?: unknown) => {
      const rec: Recorded = { method: 'POST', url, body, opts };
      calls.push(rec);
      return responder(rec);
    },
    getBlob: (path: string) => {
      const rec: Recorded = { method: 'GET', url: path, opts: { responseType: 'blob' } };
      calls.push(rec);
      return responder(rec);
    },
    postForm: (path: string, form: FormData) => {
      const rec: Recorded = { method: 'POST', url: path, body: form };
      calls.push(rec);
      return responder(rec);
    },
  };
  setInjectProvider(() => http);
  (ngCore as unknown as { inject: unknown }).inject = () => http;
  return calls;
}

afterEach(() => {
  setInjectProvider(null);
  (ngCore as unknown as { inject: unknown }).inject = () => {
    throw new Error('jest-stub: inject() called outside a stubbed component test scope');
  };
});

describe('serverOnlyShadowRows（票259 S2：take 率取服务端 TakeRate）', () => {
  const rec = (id: string, takeRate?: number | null) => ({
    id,
    createdAt: 1000,
    duration: 12.5,
    isShadow: true,
    takeRate,
  });

  it('TakeRate 有值 → rate 取该值（不再缺省 1）', () => {
    const rows = serverOnlyShadowRows([rec('srv-1', 1.5)], new Set());
    expect(rows).toHaveLength(1);
    expect(rows[0].rate).toBe(1.5);
  });

  it('TakeRate 缺省/null（S1 旧行）→ rate 缺省 1', () => {
    const rows = serverOnlyShadowRows([rec('a'), rec('b', null)], new Set());
    expect(rows.map((r) => r.rate)).toEqual([1, 1]);
  });
});

describe('PracticeApi voice-track 两端点（票259 S2）', () => {
  const blob = new Blob(['voice-bytes'], { type: 'audio/webm' });

  it('uploadVoiceTrack → POST /api/assessment/recordings/{id}/voice-track，表单含 file/takeRate/laneShiftSec', async () => {
    const calls = installFakeHttp(() => of({
      recordingId: 'r-1', contentType: 'audio/webm', sizeBytes: 11, takeRate: 1.5, laneShiftSec: 0.3,
    }));
    const api = new PracticeApi();
    const out = await firstValueFrom(api.uploadVoiceTrack('r-1', blob, 'voice.webm', 'audio/webm', 1.5, 0.3));

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toBe('/api/assessment/recordings/r-1/voice-track');
    const fd = calls[0].body as FormData;
    expect(fd.get('takeRate')).toBe('1.5');
    expect(fd.get('laneShiftSec')).toBe('0.3');
    const file = fd.get('file') as File;
    expect(file.name).toBe('voice.webm');
    expect(out.takeRate).toBe(1.5);
  });

  it('uploadVoiceTrack 缺省对齐参数 → 表单不含 takeRate/laneShiftSec（服务端保留既有值）', async () => {
    const calls = installFakeHttp(() => of({
      recordingId: 'r-1', contentType: 'audio/webm', sizeBytes: 11,
    }));
    const api = new PracticeApi();
    await firstValueFrom(api.uploadVoiceTrack('r-1', blob, 'voice.webm', 'audio/webm'));

    const fd = calls[0].body as FormData;
    expect(fd.get('takeRate')).toBeNull();
    expect(fd.get('laneShiftSec')).toBeNull();
  });

  it('fetchVoiceTrack → GET /api/assessment/recordings/{id}/voice-track，responseType blob', async () => {
    const calls = installFakeHttp(() => of(blob));
    const api = new PracticeApi();
    const out = await firstValueFrom(api.fetchVoiceTrack('r-1'));

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('GET');
    expect(calls[0].url).toBe('/api/assessment/recordings/r-1/voice-track');
    expect((calls[0].opts as { responseType?: string } | undefined)?.responseType).toBe('blob');
    expect(out).toBe(blob);
  });
});

describe('i18n + 模板契约（票259 S2：人声重试）', () => {
  const i18n = SRC('../../core/i18n/i18n.service.ts');
  const tpl = SRC('./ai-practice.component.html');

  it('两新键三语齐全（voiceUploadFailed / retryVoice）', () => {
    for (const key of ['practice.shadow.voiceUploadFailed', 'practice.shadow.retryVoice']) {
      const re = new RegExp(`'${key}':\\s*\\{([^}]*)\\}`);
      const m = i18n.match(re);
      expect(m).not.toBeNull();
      expect(m![1]).toContain('zh:');
      expect(m![1]).toContain('en:');
      expect(m![1]).toContain('fr:');
    }
  });

  it('重试钮两处：行内（hi-retry）+ 展开区（btn-mini），均只在 voiceTrackFailedFor 命中该行时出现', () => {
    const rowBtn = tpl.match(/@if \(shadow\.voiceTrackFailedFor\(\) === row\.serverId\) \{[\s\S]*?hi-retry[\s\S]*?retryVoiceTrack\(row\)/);
    expect(rowBtn).not.toBeNull();
    const expandedBtn = tpl.match(/shadow-expanded-actions[\s\S]*?@if \(shadow\.voiceTrackFailedFor\(\) === row\.serverId\) \{[\s\S]*?retryVoiceTrack\(row\)/);
    expect(expandedBtn).not.toBeNull();
  });

  it('重试钮不调 submitShadowPending（只重传人声，绝不重传混音）', () => {
    const retryBlocks = [...tpl.matchAll(/voiceTrackFailedFor\(\) === row\.serverId\) \{([\s\S]*?)\n\s*\}/g)];
    expect(retryBlocks.length).toBeGreaterThanOrEqual(2);
    for (const b of retryBlocks) {
      expect(b[1]).not.toContain('submitShadowPending');
      expect(b[1]).toContain('retryVoiceTrack(row)');
    }
  });
});
