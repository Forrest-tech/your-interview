/**
 * 票 272：展开区播放控件清理 — 模板/SCSS 契约单测。
 *   · 展开区 Play together 按钮已删（与行首播放钮同函数 toggleShadowPlay，
 *     行首钮仍是唯一的一起放入口；playTogether 三语键仍在，因行首钮
 *     tooltip 还在用它，不得误删）；
 *   · 展开区剩余控件（solo 三态 + 倍速菜单钮）与 .card-foot .btn-mini
 *     同款控件语言对齐（32px 高、9px 圆角、#e2e8f0 边框、白底、12.5px）；
 *   · solo 激活态 .on 必须一眼可辨（浅蓝填充 + 加深边框，且是行内唯一蓝色）。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';

const SRC = (rel: string) => readFileSync(join(__dirname, rel), 'utf8');

const tpl = SRC('./ai-practice.component.html');
const scss = SRC('./ai-practice.component.scss');
const i18n = SRC('../../core/i18n/i18n.service.ts');

// 展开区动作行：从 shadow-expanded-actions 起，到倍速菜单止（含全部按钮）。
const expandedActions = (() => {
  const start = tpl.indexOf('shadow-expanded-actions');
  const end = tpl.indexOf('<mat-menu #shadowReviewMenu', start);
  expect(start).toBeGreaterThan(-1);
  expect(end).toBeGreaterThan(start);
  return tpl.slice(start, end);
})();

const scssBlock = (selector: string) => {
  const m = scss.match(new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{([^}]*)\\}`));
  expect(m).not.toBeNull();
  return m![1];
};

describe('票272：展开区 Play together 已删，行首钮仍是唯一一起放入口', () => {
  it('展开区不再调 toggleShadowPlay、不再出现 playTogether 文案', () => {
    expect(expandedActions).not.toContain('toggleShadowPlay(row)');
    expect(expandedActions).not.toContain('practice.shadow.playTogether');
  });

  it('行首播放钮仍在（调 toggleShadowPlay，tooltip 用 playTogether 键）', () => {
    expect(tpl).toContain('toggleShadowPlay(row)');
    expect(tpl).toContain(`t('practice.shadow.playTogether')`);
  });

  it('playTogether 三语键仍在（行首 tooltip 在用，不得随展开区按钮误删）', () => {
    const m = i18n.match(/'practice\.shadow\.playTogether':\s*\{([^}]*)\}/);
    expect(m).not.toBeNull();
    expect(m![1]).toContain('zh:');
    expect(m![1]).toContain('en:');
    expect(m![1]).toContain('fr:');
  });
});

describe('票272：展开区剩余控件与 .card-foot .btn-mini 同款对齐', () => {
  it('展开区仍有 solo 三态 + 倍速菜单钮（功能一个不少）', () => {
    for (const key of ['practice.shadow.soloVoice', 'practice.shadow.soloRef', 'practice.shadow.together']) {
      expect(expandedActions).toContain(key);
    }
    expect(expandedActions).toContain('shadow-rate-menu-btn');
    expect(expandedActions).toContain('practice.shadow.reviewRate');
  });

  it('solo 钮基础样式 = .card-foot .btn-mini 同款（32px、9px 圆角、#e2e8f0 边框、白底）', () => {
    const b = scssBlock('.shadow-expanded-actions .btn-mini');
    expect(b).toContain('height: 32px');
    expect(b).toContain('border-radius: 9px');
    expect(b).toContain('border: 1px solid #e2e8f0');
    expect(b).toContain('background: #ffffff');
    expect(b).toContain('font-size: 12.5px');
  });

  it('倍速菜单钮同款对齐（旧 12px 蓝边小 pill 已改掉）', () => {
    const b = scssBlock('.shadow-rate-menu-btn');
    expect(b).toContain('height: 32px');
    expect(b).toContain('border-radius: 9px');
    expect(b).toContain('border: 1px solid #e2e8f0');
    expect(b).toContain('color: #475569');
    expect(b).not.toContain('border-radius: 12px');
    expect(b).not.toContain('#dbeafe');
  });

  it('solo 激活态 .on 一眼可辨：浅蓝填充 + 加深边框', () => {
    const b = scssBlock('.shadow-expanded-actions .btn-mini.on');
    expect(b).toContain('background: #dbeafe');
    expect(b).toContain('border-color: #bfdbfe');
    expect(b).toContain('color: #1d4ed8');
  });

  it('悬停不盖掉激活态：hover 规则排除 .on（否则点击瞬间指针正在钮上，浅蓝会被悬停底色盖掉）', () => {
    expect(scss).toContain('.shadow-expanded-actions .btn-mini:hover:not([disabled]):not(.on)');
  });
});
