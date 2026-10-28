/**
 * 票 283：Recognition 五项指标放右侧 — 模板/SCSS 契约单测。
 *   · 用户实测视口（约 800–1050px，截图中图例呈 3+2 列）下，五项指标必须留在右侧，
 *     与 >1100px 的既有两列版式一致；堆叠只在真正窄屏（≤600px）才发生。
 *   · 根因：旧 1100px 断点在用户视口宽度下把 .recog-grid 压成单列，图例栏落到词行下方。
 *   · 修法只动断点阈值（1100px → 600px，两处配对：.recog-grid 与 .err-cards），
 *     版式语言、指标顺序、计数值、词行着色一字不动；与 Recognition 无关的
 *     1100px 规则（结果区通用退让）不得动。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

const tpl = SRC('./ai-practice.component.html');
const scss = SRC('./ai-practice.component.scss');

describe('票 283：Recognition 五项指标放右侧', () => {
  it('两列版式仍在：左词行宽列 + 右图例栏（168–220px）', () => {
    expect(scss).toContain('grid-template-columns: minmax(0, 1fr) minmax(168px, 220px);');
  });

  it('堆叠阈值已降至 600px：单列 .recog-grid 只出现在 600px 断点内，不再出现在 1100px 断点内', () => {
    expect(scss).toContain('@media (max-width: 600px) {\n  .recog-grid { grid-template-columns: minmax(0, 1fr); }');
    expect(scss).not.toContain('@media (max-width: 1100px) {\n  .recog-grid { grid-template-columns: minmax(0, 1fr); }');
  });

  it('图例栏堆叠规则与网格配对：601px 以上图例栏保持右侧单列（flex column），不再在 1100px 就改 2 列 wrap', () => {
    expect(scss).toContain('.err-cards {\n  display: flex;\n  flex-direction: column;');
    expect(scss).not.toContain('@media (max-width: 1100px) {\n  .err-cards {');
  });

  it('模板结构未动：.recog-grid 内仍是词行 + 图例栏，五项指标仍由 errorCounts() 渲染', () => {
    const gridStart = tpl.indexOf('class="recog-grid"');
    expect(gridStart).toBeGreaterThan(-1);
    const gridSlice = tpl.slice(gridStart, tpl.indexOf('</div>', tpl.indexOf('err-cards', gridStart)) + 6);
    expect(gridSlice).toContain('class="recog-text"');
    expect(gridSlice).toContain('class="err-cards"');
    expect(gridSlice).toContain('@for (c of errorCounts(); track c.key)');
  });

  it('无关规则未动：结果区通用 1100px 退让（.main-body/.text-card）仍在', () => {
    expect(scss).toContain('.main-body { max-width: none; }');
  });
});
