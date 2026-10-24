#!/usr/bin/env python3
"""
票123 · R-2 调色板全量清扫(源码侧普查,不改任何文件)。

白名单(定稿 R-2 第 3 条,逐字照抄):
  1. :root 令牌块(styles.scss:78-103)
  2. $brand-palette 色阶(styles.scss:116-120)50–900 + A100..A700
  3. 上述任一白名单色的 alpha 衍生物(rgba(...))
  4. 字面 hex 等于某个令牌值(算命中)
  5. 头像豁免:background 来自 avatarColor() 的元素(源码侧标记为 AVATAR)

用法:python3 tools/census-hex-r2.py [根目录]
输出:每个非白名单 hex 的出现位置(文件:行:列 + 上下文)
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else '/workspace/your-interview'
WEB = os.path.join(ROOT, 'web', 'src')

# ---- 白名单 ----
TOKEN_HEX = {
    '#1b2350', '#2e3a8c', '#e4e7f5', '#c6cdea', '#1b7f4b', '#e2f1e8',
    '#b3261e', '#f9e8e5', '#b06a00', '#faf0d2', '#68707e', '#edf0f4',
    '#f5f6f8', '#ffffff', '#e1e4ec', '#1f2430', '#5b6270', '#e2e5ec',
}
BRAND_RAMP = {
    '#eef0f9', '#d9ddf2', '#b6bee6', '#8e9ad6', '#6673c2', '#3e4da3',
    '#2e3a8c', '#27316f', '#212a5c', '#1b2350', '#d9dfff', '#a6b4ff',
    '#7387ff', '#5b74ff',
}
# 对比色(rgba 黑/白)也算主题产物
WHITE = '#ffffff'
WHITELIST = TOKEN_HEX | BRAND_RAMP | {WHITE, '#000000', '#000'}

# 头像豁免:这些文件里的 avatarColor() 产物不算
AVATAR_FILES = {
    'features/tracker/tracker.component.ts',
    'features/playbook/playbook.component.ts',
    'features/dashboard/dashboard.component.ts',
    'features/tracker/application-detail-dialog.component.ts',
}

HEX_RE = re.compile(r'#([0-9a-fA-F]{3,8})\b')
RGBA_RE = re.compile(r'rgba?\(\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*(?:,\s*([0-9.]*)\s*)?\)')

EXT = ('.scss', '.ts', '.html', '.css')
SKIP_DIRS = {'node_modules', 'dist', '.angular', 'coverage'}


def norm_hex(h):
    """#abc -> #aabbcc;#abcd -> #aabbccdd;返回 8 位或 6 位。"""
    h = h.lower()
    if len(h) == 3:
        return '#' + ''.join(c * 2 for c in h)
    if len(h) == 4:
        return '#' + ''.join(c * 2 for c in h)
    return '#' + h


def rgba_to_hex(m):
    r, g, b = int(m.group(1)), int(m.group(2)), int(m.group(3))
    a = m.group(4)
    if a is None:
        return norm_hex('%02x%02x%02x' % (r, g, b)), None
    try:
        av = float(a)
    except ValueError:
        return None, None
    return norm_hex('%02x%02x%02x' % (r, g, b)), av


def main():
    findings = []
    scanned = 0
    for dirpath, dirnames, filenames in os.walk(WEB):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for fn in filenames:
            if not fn.endswith(EXT):
                continue
            path = os.path.join(dirpath, fn)
            rel = os.path.relpath(path, WEB)
            try:
                text = open(path, encoding='utf-8').read()
            except Exception:
                continue
            scanned += 1
            lines = text.split('\n')
            for i, line in enumerate(lines, 1):
                # 跳过纯注释行里的示例值?(先不跳过,全量报给人看)
                for m in HEX_RE.finditer(line):
                    raw = m.group(1)
                    h = norm_hex(raw)
                    if h in WHITELIST:
                        continue
                    # rgba() 内的 hex 不合法,跳过
                    if len(raw) not in (3, 4, 6, 8):
                        continue
                    findings.append((rel, i, h, line.strip()[:140], 'hex'))
                for m in RGBA_RE.finditer(line):
                    h, av = rgba_to_hex(m)
                    if h is None:
                        continue
                    if h in WHITELIST:
                        continue
                    findings.append((rel, i, h, line.strip()[:140], 'rgba'))

    # 按颜色聚类
    by_color = {}
    for rel, ln, h, ctx, kind in findings:
        by_color.setdefault(h, []).append((rel, ln, ctx, kind))

    print(f'扫描文件 {scanned} 个;非白名单命中 {len(findings)} 处,涉及 {len(by_color)} 个颜色\n')
    for h in sorted(by_color, key=lambda x: -len(by_color[x])):
        hits = by_color[h]
        print(f'=== {h}  ({len(hits)} 处) ===')
        for rel, ln, ctx, kind in hits:
            print(f'  {rel}:{ln}  [{kind}]  {ctx}')
        print()


if __name__ == '__main__':
    main()
