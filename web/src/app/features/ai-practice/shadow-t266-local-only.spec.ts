/**
 * 票 266：跟读只留本机（用户点选定案：去掉上传）。
 *   · 模板：无上传控件，无上传用待提交块；写失败重试钮改挂错误提示区；
 *     人声重试钮（服务端行）与行删除钮保留。
 *     票 274 补充：卡片底部共享的 Submit/Preview/Discard 三钮复用于
 *     跟读“本地确认”（停录先挂起、Submit 仅本地落库），与 266 删掉的
 *     “上传用待提交”是两回事——上传路径仍删，本地确认是新加的。
 *   · session：上传/混音方法与混音信号全部删除；Stop 统一为本地终态行；
 *     新 take 不再调任何新建服务端行的 API（人声重试与服务端读路径除外）。
 *     票 274：Stop 改为先挂 pending（见票 274 规约），Submit 才落库。
 *   · i18n：被删 UI 专用的键已移除，无残留引用。
 * 结构断言（沿用票 264 前例）：读源码文本钉死契约，不跑 Angular。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

describe('票 266：上传删除', () => {
  it('模板无 Submit/Discard/上传控件与待提交块', () => {
    const html = SRC('./ai-practice.component.html');
    expect(html).not.toContain('shadow-pending');
    expect(html).not.toContain('submitShadowPending');
    expect(html).not.toContain('discardShadowPending');
    expect(html).not.toContain('uploadShadowRowToServer');
    expect(html).not.toContain('practice.shadow.submit');
    expect(html).not.toContain('practice.shadow.discard');
    expect(html).not.toContain('practice.shadow.uploadToServer');
    expect(html).not.toContain('shadowMixStatus');
    expect(html).not.toContain('shadowMixProgress');
  });

  it('模板保留写失败重试钮（错误提示区）、人声重试钮与行删除钮', () => {
    const html = SRC('./ai-practice.component.html');
    expect(html).toContain('retryShadowWrite()');
    expect(html).toContain('practice.shadow.retryWrite');
    expect(html).toContain('retryVoiceTrack(row)');
    expect(html).toContain('removeShadowRow(row)');
  });

  it('人声重试钮须有失败 id 才出现（否则 null === 本地行 null 会在每条本地行上误显上传图标）', () => {
    const html = SRC('./ai-practice.component.html');
    const guards = html.match(/voiceTrackFailedFor\(\) !== null/g) || [];
    expect(guards.length).toBe(2);
  });

  it('session 删除提交/丢弃/上传/混音方法与待提交信号', () => {
    const session = SRC('./practice-shadow-session.ts');
    // 票 274 接管说明：266 删的是“上传用待提交”（Submit 上传到服务端）；
    // 274 重新引入的是“本地确认用 pending”（Submit 仅本地落库、永不上传），
    // 故 submit/discard + pending 信号三项不再断言缺失（见票 274 规约）；
    // 上传/混音相关删除项仍全量断言。
    for (const name of [
      'async uploadShadowRowToServer',
      'private async renderMix',
      'async retryShadowMix',
      'cancelShadowMix',
      'async regenerateReference',
      'readonly shadowMixStatus',
      'readonly shadowMixProgress',
      'readonly uploadingToServerFor',
      'interface ShadowPendingView',
    ]) {
      expect(session).not.toContain(name);
    }
  });

  it('Stop 统一为本地终态行：kind 恒 dual、mixStatus 恒 local', () => {
    const session = SRC('./practice-shadow-session.ts');
    expect(session).toContain(`const kind = 'dual' as const`);
    expect(session).not.toContain(`this.takeRate === 1 ? 'mix' : 'dual'`);
    expect(session).toContain(`mixStatus: 'local'`);
  });

  it('新 take 不新建服务端行：session 不调上传录音/创建跟读行端点', () => {
    const session = SRC('./practice-shadow-session.ts');
    expect(session).not.toContain('practiceApi.uploadRecording');
    expect(session).not.toContain('practiceApi.createShadowRow');
    // 例外（保留项，非新建上传）：已提交行的的人声重试与服务端读路径。
    expect(session).toContain('practiceApi.uploadVoiceTrack');
    expect(session).toContain('practiceApi.fetchVoiceTrack');
    expect(session).toContain('practiceApi.fetchRecordingAudio');
  });

  it('被删 UI 的 i18n 键已移除，保留键不受影响', () => {
    const src = SRC('../../core/i18n/i18n.service.ts');
    for (const key of [
      'practice.shadow.submit',
      'practice.shadow.discard',
      'practice.shadow.retryMix',
      'practice.shadow.mixRendering',
      'practice.shadow.partialKept',
      'practice.shadow.mixFailed',
      'practice.shadow.needSavedMaterial',
      'practice.shadow.uploadFailed',
      'practice.shadow.rekeyFailed',
      'practice.shadow.submitted',
      'practice.shadow.uploadToServer',
      'practice.shadow.uploadedToServer',
      'practice.shadow.uploadToServerFailed',
      'practice.shadow.regenFailed',
    ]) {
      expect(src).not.toContain(`'${key}'`);
    }
    for (const key of [
      'practice.shadow.savedLocal',
      'practice.shadow.partialKeptLocal',
      'practice.shadow.storageFailed',
      'practice.shadow.retryWrite',
      'practice.shadow.retryVoice',
      'practice.shadow.voiceUploadFailed',
    ]) {
      expect(src).toContain(`'${key}'`);
    }
  });

  it('服务端读路径保留：serverOnlyShadowRows 与行删除确认仍在', () => {
    const session = SRC('./practice-shadow-session.ts');
    expect(session).toContain('serverOnlyShadowRows');
    expect(session).toContain('async removeShadowRow');
    const removeBlock = session.slice(session.indexOf('async removeShadowRow'));
    expect(removeBlock.slice(0, 1200)).toContain('confirmDialog');
  });
});
