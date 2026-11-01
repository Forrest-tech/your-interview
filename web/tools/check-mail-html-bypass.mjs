#!/usr/bin/env node
/**
 * 票 309 测试 23：mail-html 渲染信任旁路静态门禁。
 *
 * 规则：邮件 HTML 只经 DOMPurify（mail-html.component.ts）渲染，绝不走 Angular
 * 的信任旁路；下列文件出现旁路标识即判失败（注释里出现亦算——宁可保守，
 * 避免“注释掉的旁路”日后被取消注释复活）。
 *
 * 作用域刻意只限两文件：仓库级 grep 会误报 tracker 高亮等合法的 innerHTML
 * 用途（与邮件 HTML 无关），故不在此查。
 *
 * 维护规则：邮件 HTML 渲染若迁往他文件，本清单必须随之迁移（漏迁 = 门禁失效）。
 *
 * 用法（必须在 web/ 目录下）：
 *   node tools/check-mail-html-bypass.mjs
 */
import fs from 'fs';
import path from 'path';

const FILES = [
  'src/app/features/playbook/redesign/round/mail-html.component.ts',
  'src/app/features/playbook/redesign/round/materials-list.component.ts',
];
// 旁路标识拆字拼接，避免本脚本自身被误当作“出现”（本脚本不在扫描清单内，
// 拆字纯属防御性写法，日后若有人把脚本纳入扫描亦不自误报）。
const BYPASS = ['bypass', 'Security', 'Trust', 'Html'].join('');

let failed = false;
for (const rel of FILES) {
  const abs = path.join(process.cwd(), rel);
  const lines = fs.readFileSync(abs, 'utf8').split('\n');
  lines.forEach((line, i) => {
    if (line.includes(BYPASS)) {
      console.error(`FAIL ${rel}:${i + 1}: trust bypass found`);
      failed = true;
    }
  });
}
if (failed) {
  console.error('check-mail-html-bypass: FAILED (mail HTML must go through DOMPurify only)');
  process.exit(1);
}
console.log('check-mail-html-bypass: OK (no trust bypass in mail-html rendering files)');
