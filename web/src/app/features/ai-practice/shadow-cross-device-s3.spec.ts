/**
 * 票 260 S3：非 1x 行上服务端 — 前端契约单测。
 *   · 行 kind 派生（前后端同一公式）：IsShadow=true 且 StoragePath 非空 → 跟读-混音行；
 *     IsShadow=true 且 StoragePath 为空 且 VoiceStoragePath 非空 → 跟读-only 行；
 *     IsShadow=true 且两路径皆空 → 未完成行；其余 → 普通行。
 *   · serverOnlyShadowRows：跟读-only 行进列（kind 按 rate）、未完成行在别设备不可见、
 *     旧响应（两轨字段皆缺省）仍按混音行处理、已链接/非跟读行不重复进列。
 *   · 评分隔离第一道防线（前端约定，非结构保证）：正常列表口径 NOT isShadowRow，
 *     跟读-only 行同样排除在外；结构保证是后端 assess 入口 400（后端单测覆）。
 *   · PracticeApi.createShadowRow：POST /materials/{id}/shadow-rows，JSON 体逐字。
 *   · i18n/模板契约：三新键三语齐全；“上传到服务器”钮只在本地-only 双轨行上出现。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it, afterEach } from '@jest/globals';
import { firstValueFrom, of } from 'rxjs';
import * as ngCore from '@angular/core';
import { setInjectProvider } from '../../../../test/jest-stub';
import { PracticeApi } from '../../core/api/practice-api.service';
import {
  isShadowRow,
  recordingKind,
  serverOnlyShadowRows,
} from './practice-shadow-session';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

interface Recorded {
  method: 'GET' | 'POST';
  url: string;
  body?: unknown;
  opts?: unknown;
}

function installFakeHttp(responder: (r: Recorded) => unknown): Recorded[] {
  const calls: Recorded[] = [];
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

describe('recordingKind（票260 S3 四态派生）', () => {
  it('普通行：IsShadow false/缺省 → ordinary（与轨字段无关）', () => {
    expect(recordingKind(false, true, true)).toBe('ordinary');
    expect(recordingKind(undefined, false, false)).toBe('ordinary');
    expect(recordingKind(false, false, false)).toBe('ordinary');
  });

  it('跟读-混音行：IsShadow true 且有混音 → shadow-mix', () => {
    expect(recordingKind(true, true, true)).toBe('shadow-mix');
    expect(recordingKind(true, true, false)).toBe('shadow-mix');
  });

  it('跟读-only 行：IsShadow true、无混音、有人声 → shadow-only', () => {
    expect(recordingKind(true, false, true)).toBe('shadow-only');
  });

  it('未完成行：IsShadow true、两轨皆无 → shadow-unfinished', () => {
    expect(recordingKind(true, false, false)).toBe('shadow-unfinished');
    expect(recordingKind(true, undefined, undefined)).toBe('shadow-unfinished');
  });
});

describe('serverOnlyShadowRows（票260 S3 别设备列表）', () => {
  const rec = (id: string, o: { isShadow?: boolean; hasMixTrack?: boolean; hasVoiceTrack?: boolean; takeRate?: number | null; laneShiftSec?: number | null } = {}) => ({
    id,
    createdAt: 1000,
    duration: 12.5,
    isShadow: o.isShadow,
    hasMixTrack: o.hasMixTrack,
    hasVoiceTrack: o.hasVoiceTrack,
    takeRate: o.takeRate,
    laneShiftSec: o.laneShiftSec,
  });

  it('跟读-only 行进列：kind 按 rate（1.5 → dual），laneShift 透传', () => {
    const rows = serverOnlyShadowRows(
      [rec('only-1', { isShadow: true, hasMixTrack: false, hasVoiceTrack: true, takeRate: 1.5, laneShiftSec: 0.3 })],
      new Set());
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      id: 'only-1', kind: 'dual', rate: 1.5, laneShiftSec: 0.3,
      mixStatus: 'done', uploaded: true, localTakeId: '',
    });
  });

  it('跟读-混音行进列：rate 1 → kind mix', () => {
    const rows = serverOnlyShadowRows(
      [rec('mix-1', { isShadow: true, hasMixTrack: true, hasVoiceTrack: true, takeRate: 1 })],
      new Set());
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('mix');
  });

  it('未完成行（两轨皆明确无）在别设备不可见', () => {
    const rows = serverOnlyShadowRows(
      [rec('unf-1', { isShadow: true, hasMixTrack: false, hasVoiceTrack: false, takeRate: 1.5 })],
      new Set());
    expect(rows).toHaveLength(0);
  });

  it('旧响应（两轨字段皆缺省）仍按混音行处理，不破坏 S1/S2 行为', () => {
    const rows = serverOnlyShadowRows(
      [rec('old-1', { isShadow: true })],
      new Set());
    expect(rows).toHaveLength(1);
    expect(rows[0].kind).toBe('mix');
  });

  it('已链接/非跟读行不进列', () => {
    const rows = serverOnlyShadowRows(
      [rec('linked', { isShadow: true, hasMixTrack: false, hasVoiceTrack: true }),
       rec('plain', { isShadow: false, hasMixTrack: true })],
      new Set(['linked']));
    expect(rows).toHaveLength(0);
  });

  it('混合四态：仅完整行进列，未完成行不进列，普通行留普通列表', () => {
    const recordings = [
      rec('only-1', { isShadow: true, hasMixTrack: false, hasVoiceTrack: true, takeRate: 1.5 }),
      rec('unf-1', { isShadow: true, hasMixTrack: false, hasVoiceTrack: false, takeRate: 1.5 }),
      rec('mix-1', { isShadow: true, hasMixTrack: true, hasVoiceTrack: true, takeRate: 1 }),
      rec('plain', { isShadow: false, hasMixTrack: true }),
    ];
    const shadow = serverOnlyShadowRows(recordings, new Set());
    expect(shadow.map((r) => r.id).sort()).toEqual(['mix-1', 'only-1']);
    // 正常列表口径 NOT isShadowRow：跟读-only 行同样排除（第一道防线，前端约定）。
    const normal = recordings.filter((r) => !isShadowRow(r.isShadow, false));
    expect(normal.map((r) => r.id)).toEqual(['plain']);
  });
});

describe('PracticeApi.createShadowRow（票260 S3 两步创建第一步）', () => {
  it('POST /api/assessment/materials/{id}/shadow-rows，JSON 体逐字', async () => {
    const calls = installFakeHttp(() => of({
      id: 'row-1', materialId: 'm-1', durationSeconds: 2.5, sizeBytes: 0,
      contentType: 'audio/webm', createdAt: new Date(1000).toISOString(), score: null,
      isShadow: true, hasVoiceTrack: false, hasMixTrack: false, takeRate: 1.5, laneShiftSec: 0.3,
    }));
    const api = new PracticeApi();
    const out = await firstValueFrom(api.createShadowRow('m-1', 2.5, 1.5, 0.3));
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toBe('/api/assessment/materials/m-1/shadow-rows');
    expect(calls[0].body).toEqual({ durationSeconds: 2.5, takeRate: 1.5, laneShiftSec: 0.3 });
    expect(out.id).toBe('row-1');
    expect(out.hasMixTrack).toBe(false);
    expect(out.hasVoiceTrack).toBe(false);
  });
});

describe('i18n/模板契约（票260 S3 迁移控件）', () => {
  it('三新键三语齐全', () => {
    const src = SRC('../../core/i18n/i18n.service.ts');
    for (const key of [
      'practice.shadow.uploadToServer',
      'practice.shadow.uploadedToServer',
      'practice.shadow.uploadToServerFailed',
    ]) {
      expect(src).toContain(`'${key}'`);
      const line = src.split('\n').find((l) => l.includes(`'${key}'`)) ?? '';
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });

  it('模板：“上传到服务器”钮只在本地-only 双轨行上出现', () => {
    const html = SRC('./ai-practice.component.html');
    expect(html).toContain(`!row.serverId && row.kind === 'dual'`);
    expect(html).toContain(`shadow.uploadShadowRowToServer(row)`);
    expect(html).toContain(`practice.shadow.uploadToServer`);
  });
});
