#!/usr/bin/env node
/**
 * 模板结构预检 —— 用 Angular 的 HtmlParser(与 ng serve 同一条 HTML 解析路径)。
 *
 * ⚠️ 两个已踩过的坑(2026-09-16):
 *  1) 不能用 ng.parseTemplate 做门禁:对真机报 NG5002 的坏模板,
 *     parseTemplate 各类参数组合都返回 no error;HtmlParser 才报得出来。
 *  2) ParseErrorLevel 是【数字枚举】:ERROR = 1,WARNING = 0。
 *     只按字符串 'error' 过滤会把真错误全部漏掉 → 门禁形同虚设。
 *
 * 用法(必须在 web/ 目录下):
 *   node tools/check-templates.mjs
 */
import fs from 'fs';
import path from 'path';

const ng = await import('@angular/compiler');

const LEVEL_ERROR = ng.ParseErrorLevel ? ng.ParseErrorLevel.ERROR : 1;

function isError(e) {
  // 兼容数字枚举与字符串两种形态
  if (typeof e.level === 'number') return e.level === LEVEL_ERROR;
  if (typeof e.level === 'string') return e.level === 'error';
  return true; // 无 level 视为错误(保守)
}

function walk(dir, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, acc);
    else if (p.endsWith('.html')) acc.push(p);
  }
  return acc;
}

let total = 0;
let bad = 0;

for (const file of walk('src')) {
  total++;
  const src = fs.readFileSync(file, 'utf8');
  const parser = new ng.HtmlParser(undefined, undefined, false, false);
  const res = parser.parse(src, file, true);
  const errs = (res.errors || []).filter(isError);
  if (errs.length) {
    bad += errs.length;
    console.log('\u274c ' + file);
    for (const e of errs.slice(0, 6)) {
      console.log('    ' + (e.msg || e.message || String(e)));
    }
  }
}

if (bad) {
  console.log('\n' + bad + ' 处模板结构错误 —— ng serve 会失败,请先修好再启动。');
  process.exit(1);
}
console.log('\u2705 ' + total + ' 个模板结构通过(HtmlParser)');
