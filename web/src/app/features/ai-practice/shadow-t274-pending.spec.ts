/**
 * 票 274：跟读结束走三按钮确认（用户令：跟读结束也要默认用原来的
 * Submit / Preview / Discard，确认后才进列表或删除）。
 *   · 停录（手动/自动同一 stopShadow 路径）不再直接落库：take 先挂
 *     内存 pending 态；列表只认 shadowMeta（IDB），pending 期间行不出现。
 *   · 卡片底部共享三钮接影子专属处理：Submit 落库进列表（仅本地，
 *     永不上传），Preview 播这条待确认的，Discard 丢干净（无行、无 blob）。
 *   · 有待确认 take 时禁开任何新录音（先 Submit 或 Discard），提示可见；
 *     待确认态本身在影子面板有可见提示块。
 *   · 与票 266 的关系：266 删的是“上传用待提交”，274 加的是“本地确认
 *     用 pending”——上传路径仍删，本地确认是新加的（见 266 规约注）。
 * 结构断言（沿用票 264/266 前例）：读源码文本钉死契约，不跑 Angular；
 * 行为由沙盒走查实证（停录→挂起→试听→提交进列表；停录→丢弃无行；
 * 挂起时开新录音被挡），见 ~/workspace/t274-shadow-confirm/。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');
const sliceBetween = (src: string, from: string, to: string) => {
  const a = src.indexOf(from);
  const b = to ? src.indexOf(to, a + 1) : src.length;
  return src.slice(a, b < 0 ? src.length : b);
};

describe('票 274：跟读待确认态（session）', () => {
  const session = SRC('./practice-shadow-session.ts');

  it('待确认信号存在（take + 试听态）', () => {
    expect(session).toContain('readonly shadowPendingTake = signal<ShadowTakeRecord | null>(null)');
    expect(session).toContain('readonly shadowPendingPlaying = signal(false)');
  });

  it('stopShadow 只挂 pending：不落库、不刷新列表、不弹保存提示', () => {
    const stop = sliceBetween(session, 'async stopShadow()', 'async submitShadowPending');
    expect(stop).toContain('this.shadowPendingTake.set(rec)');
    expect(stop).not.toContain('putShadowTake');
    expect(stop).not.toContain('refreshShadowMeta');
    expect(stop).not.toContain('savedLocal');
    expect(stop).not.toContain('partialKeptLocal');
  });

  it('Submit 落库进列表：写 IDB + 刷新 + 清 pending，全程无服务端调用', () => {
    const submit = sliceBetween(session, 'async submitShadowPending', 'discardShadowPending(): void');
    expect(submit).toContain('await putShadowTake(rec)');
    expect(submit).toContain('await this.refreshShadowMeta()');
    expect(submit).toContain('this.shadowPendingTake.set(null)');
    expect(submit).not.toContain('practiceApi.');
  });

  it('Submit 写失败保留 pending（可再点 Submit 重试），不丢 take', () => {
    const submit = sliceBetween(session, 'async submitShadowPending', 'discardShadowPending(): void');
    expect(submit).toContain('this.shadowStorageFailed.set(true)');
    expect(submit).toContain('this.lastFailedTakeId = rec.localTakeId');
    // 失败分支在清 pending 之前 return：失败路绝不走 set(null) 成功路
    const failStart = submit.indexOf('if (!persisted)');
    const failBranch = submit.slice(failStart, submit.indexOf('return;', failStart));
    expect(failBranch).not.toContain('shadowPendingTake.set(null)');
  });

  it('Discard 丢干净：清 pending + 清这条 take 的提示，绝不写库', () => {
    const discard = sliceBetween(session, 'discardShadowPending(): void', 'async previewShadowPending');
    expect(discard).toContain('this.shadowPendingTake.set(null)');
    expect(discard).toContain('this.shadowQuietHint.set(null)');
    expect(discard).toContain('this.shadowError.set(null)');
    expect(discard).not.toContain('putShadowTake');
  });

  it('Preview 播待确认 take：人声 blob + 参考缓存，双轨构造复用增益级', () => {
    const preview = sliceBetween(session, 'async previewShadowPending', 'stopPendingPreview(): void');
    expect(preview).toContain('rec.voiceBlob');
    expect(preview).toContain('getCachedAudio(rec.refKey)');
    expect(preview).toContain(`mode: 'dual'`);
    expect(preview).toContain('this.attachTogetherGain(vEl)');
    expect(preview).not.toContain('putShadowTake');
  });

  it('控制器销毁即停待确认试听（行播放先走此处，不留幽灵试听态）', () => {
    const stop = sliceBetween(session, 'private stopShadowPlayback()', 'private async attachTogetherGain');
    expect(stop).toContain('this.shadowPendingPlaying.set(false)');
  });

  it('startShadow 门禁：有 pending 时先挡，且提示在清提示之前', () => {
    const start = sliceBetween(session, 'async startShadow()', 'async stopShadow()');
    expect(start).toContain('if (this.shadowPendingTake())');
    expect(start).toContain('practice.shadow.pendingBlocksRecording');
    expect(start.indexOf('pendingBlocksRecording')).toBeLessThan(
      start.indexOf('this.shadowError.set(null)'),
    );
  });

  it('素材切换丢弃旧素材的 pending（不落库、不留行、不留旧提示）', () => {
    const onChange = sliceBetween(session, 'onMaterialChange(): void', '// ---------- 采集 ----------');
    expect(onChange).toContain('if (this.shadowPendingTake())');
    expect(onChange).toContain('this.shadowPendingTake.set(null)');
  });

  it('写失败重试钮接 pending 提交（pending 在内存时不走 IDB 空查）', () => {
    const retry = sliceBetween(session, 'async retryShadowWrite()', 'private async computeAndCachePostF0');
    expect(retry).toContain('pending.localTakeId === this.lastFailedTakeId');
    expect(retry).toContain('await this.submitShadowPending()');
  });
});

describe('票 274：跟读待确认态（component 路由 + 模板 + i18n）', () => {
  it('Submit 门 = 常规待提交 或 跟读待确认', () => {
    const component = SRC('./ai-practice.component.ts');
    const gate = sliceBetween(component, 'readonly canSubmitTake', 'startRecording(): Promise<void>');
    expect(gate).toContain('this.shadow.shadowPendingTake() !== null');
    expect(gate).toContain('this.grading.canSubmitTake()');
  });

  it('底部三钮跟读待确认优先（Submit 落库 / Preview 试听 / Discard 丢弃）', () => {
    const component = SRC('./ai-practice.component.ts');
    for (const [method, call] of [
      ['previewPending(): void', 'this.shadow.previewShadowPending()'],
      ['submitPending(): void', 'this.shadow.submitShadowPending()'],
      ['discardPending(): void', 'this.shadow.discardShadowPending()'],
    ] as const) {
      const block = sliceBetween(component, method, '\n  }');
      expect(block).toContain('if (this.shadow.shadowPendingTake())');
      expect(block).toContain(call);
    }
  });

  it('开新录音门禁：有 pending 时 toast 提示，不进任何录音分支', () => {
    const component = SRC('./ai-practice.component.ts');
    const start = sliceBetween(component, 'startRecording(): Promise<void>', 'stopRecording(): void');
    expect(start).toContain('if (this.shadow.shadowPendingTake())');
    expect(start).toContain('practice.shadow.pendingBlocksRecording');
    expect(start.indexOf('pendingBlocksRecording')).toBeLessThan(
      start.indexOf('this.shadow.startShadow()'),
    );
  });

  it('模板：待确认提示块可见（testid + 文案键），卡片底部钮接 pending', () => {
    const html = SRC('./ai-practice.component.html');
    expect(html).toContain('data-testid="shadow-confirm"');
    expect(html).toContain(`t('practice.shadow.pendingConfirm')`);
    expect(html).toContain('@if (recorder.pendingTake() || shadow.shadowPendingTake())');
    expect(html).toContain('shadow.shadowPendingPlaying()');
    // Submit 钮本身不动：同一文案、同一门禁表达式入口
    expect(html).toContain(`t('practice.submitTakeTip')`);
  });

  it('i18n：两新键齐全（文案本身不硬编码进模板）', () => {
    const i18n = SRC('../../core/i18n/i18n.service.ts');
    for (const key of [
      'practice.shadow.pendingConfirm',
      'practice.shadow.pendingBlocksRecording',
    ]) {
      expect(i18n).toContain(`'${key}'`);
    }
  });
});
