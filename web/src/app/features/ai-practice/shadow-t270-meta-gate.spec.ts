/**
 * 票 270（F1）：meta 素材标签 + 视图层门禁结构契约。
 * 背景：素材切换后、新刷新未落前，shadowMeta 仍 holding 旧素材行，
 * shadowRows 会把旧素材行渲染出来（F1 的签名 A 机制；数据本身从未丢，
 * 只是切换窗口内行属于错素材）。修法：meta 配一个素材标签，shadowRows
 * 只在标签与当前素材一致时采用 meta 行；标签与 map 在同一同步块内
 * 原子设置，迟到的旧素材刷新也过不了门禁。
 * 此处钉住结构契约；真机行为以沙盒走查为准（见实现报告）。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';

describe('票 270：meta 素材标签门禁结构契约', () => {
  const src = fs.readFileSync(path.join(__dirname, 'practice-shadow-session.ts'), 'utf8');

  it('标签信号与 shadowMeta 相邻声明，初始 null', () => {
    expect(src).toContain('private readonly shadowMeta = signal<Map<string, ShadowTakeRecord>>(new Map());');
    expect(src).toContain('private readonly shadowMetaMaterial = signal<string | null>(null);');
    const metaIdx = src.indexOf('private readonly shadowMeta = signal');
    const tagIdx = src.indexOf('private readonly shadowMetaMaterial = signal');
    expect(tagIdx).toBeGreaterThan(metaIdx);
  });

  it('标签与 map 在同一同步块内原子设置，中间无 await', () => {
    // 刷新落点处两行连续出现，且其间没有任何 await——门禁不闪烁。
    const block = 'this.shadowMeta.set(map);\n    this.shadowMetaMaterial.set(materialId);';
    expect(src).toContain(block);
    const setIdx = src.indexOf('this.shadowMeta.set(map);');
    const tagIdx = src.indexOf('this.shadowMetaMaterial.set(materialId);');
    const between = src.slice(setIdx, tagIdx);
    expect(between).not.toContain('await');
  });

  it('shadowMeta 只有一个 set 落点，配对无法从别处绕过', () => {
    const sets = src.match(/this\.shadowMeta\.set\(/g) ?? [];
    expect(sets).toHaveLength(1);
  });

  it('shadowRows 门禁：标签与当前素材一致才展开 meta，不一致或 null 时 meta 为空', () => {
    const computedIdx = src.indexOf('readonly shadowRows = computed');
    const gateIdx = src.indexOf('const tag = this.shadowMetaMaterial();', computedIdx);
    expect(gateIdx).toBeGreaterThan(computedIdx);
    const gateBlock = src.slice(gateIdx, gateIdx + 300);
    expect(gateBlock).toContain('tag !== null && tag === this.host.selectedId()');
    expect(gateBlock).toContain('[...this.shadowMeta().values()]');
    expect(gateBlock).toContain(': [];');
  });

  it('门禁只挡 meta 行：服务端行仍无条件渲染（切换窗口内不空屏）', () => {
    // serverOnlyShadowRows 的调用在门禁的 meta 展开之外、且不受 tag 影响。
    expect(src).toContain('rows.push(...serverOnlyShadowRows(this.host.grading.myRecordings(), linkedIds));');
    const computedIdx = src.indexOf('readonly shadowRows = computed');
    const serverIdx = src.indexOf('serverOnlyShadowRows(this.host.grading.myRecordings()', computedIdx);
    expect(serverIdx).toBeGreaterThan(computedIdx);
  });

  it('未扩范围：门禁不引入新 i18n 键（纯视图层过滤，无新用户可见文案）', () => {
    expect(src).not.toContain('practice.shadow.metaMismatch');
    expect(src).not.toContain('practice.shadow.staleRows');
  });
});
