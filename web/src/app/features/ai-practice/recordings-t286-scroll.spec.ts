/**
 * 票 286：Recordings 区块右侧滚动条 — 模板/SCSS 契约单测。
 *   · 用户截图：Recordings (10) Shadow 列表行数一多就把卡片无限撑高；
 *     要求区块右侧出滚动条，内容超高时在区块内滚动，不再把整页往下顶。
 *   · 修法只加一层滚动容器：标头（.hist-head）留在容器外，行 + 展开区/报告区
 *     全部在 .hist-list 内；容器封顶 + overflow-y:auto，未超高时不出滚动条。
 *   · 行内容、播放、View Report、删除、评分逻辑一字不动；其他滚动区不动。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

const tpl = SRC('./ai-practice.component.html');
const scss = SRC('./ai-practice.component.scss');

describe('票 286：Recordings 区块滚动条', () => {
  it('模板有且仅有一个 .hist-list 滚动容器，包住整条 @if/@else 列表链', () => {
    const opens = tpl.match(/class="hist-list"/g) ?? [];
    expect(opens.length).toBe(1);
    const listOpen = tpl.indexOf('class="hist-list"');
    const shadowBranch = tpl.indexOf('@if (shadow.shadowMode())', listOpen);
    const nonShadowBranch = tpl.indexOf('orderedNonShadowRecordings()', listOpen);
    expect(shadowBranch).toBeGreaterThan(listOpen);
    expect(nonShadowBranch).toBeGreaterThan(listOpen);
  });

  it('标头在容器外：.hist-head 出现在 .hist-list 之前，滚动时标头不动', () => {
    expect(tpl.indexOf('class="hist-head"')).toBeLessThan(tpl.indexOf('class="hist-list"'));
  });

  it('容器封顶且只在超高时滚动：max-height + overflow-y:auto，不用 overflow:scroll（未超高不出滚动条）', () => {
    expect(scss).toContain('.hist-card .hist-list {');
    expect(scss).toContain('max-height: min(480px, 60vh);');
    expect(scss).toContain('overflow-y: auto;');
    const listRule = scss.slice(scss.indexOf('.hist-card .hist-list {'), scss.indexOf('}', scss.indexOf('.hist-card .hist-list {')));
    expect(listRule).not.toContain('overflow-y: scroll;');
  });

  it('横向不滚、滚到头尾不连带滚整页：overflow-x:hidden + overscroll-behavior:contain', () => {
    expect(scss).toContain('overflow-x: hidden;');
    expect(scss).toContain('overscroll-behavior: contain;');
  });

  it('行结构未动：跟读行与常规行仍在容器内渲染，播放/View Report/删除按钮都在', () => {
    const listSlice = tpl.slice(tpl.indexOf('class="hist-list"'));
    expect(listSlice).toContain('data-testid="shadow-row"');
    expect(listSlice).toContain('data-testid="shadow-view-report"');
    expect(listSlice).toContain('class="hi-del"');
    expect(listSlice).toContain('toggleTakeReport(r.id)');
  });
});
