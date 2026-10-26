#!/usr/bin/env python3
"""
票123 · R-2 第二步:rgba() 非白名单值映射。

定稿 R-2 白名单第 3 条:「alpha derivatives of any whitelisted hex at any alpha」。
即:rgba 的基色必须落在白名单(:root 令牌 / $brand-palette 色阶)内,透明度可任意。

本表把「118 前的旧调色板」rgba 基色改写成「当前令牌」rgba,alpha 原样保留。
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else '/workspace/your-interview/web/src/app'

# 旧基色(归一化 RGB 串) → 新基色
BASE_MAP = {
    # 靛蓝(118 前 stock indigo / 其它蓝紫)→ brand #2e3a8c = (46,58,140)
    '63, 81, 181': '46, 58, 140', '63,81,181': '46, 58, 140',
    '47, 111, 237': '46, 58, 140', '47,111,237': '46, 58, 140',
    '21, 101, 192': '46, 58, 140', '37, 99, 235': '46, 58, 140',
    '48,63,159': '46, 58, 140', '103, 58, 183': '46, 58, 140',
    '15, 23, 42': '31, 36, 48', '16, 24, 40': '31, 36, 48',
    '20, 30, 50': '31, 36, 48', '16, 185, 129': '27, 127, 75',
    '14, 165, 233': '46, 58, 140', '0, 172, 190': '46, 58, 140',
    # 绿 → go #1b7f4b = (27,127,75)
    '46, 125, 50': '27, 127, 75', '46,125,50': '27, 127, 75',
    '34, 163, 92': '27, 127, 75', '34,163,92': '27, 127, 75',
    '16, 185, 129': '27, 127, 75', '76, 175, 80': '27, 127, 75',
    '27, 127, 75': '27, 127, 75',
    # 红 → stop #b3261e = (179,38,30)
    '229, 57, 53': '179, 38, 30', '229,57,53': '179, 38, 30',
    '186,26,26': '179, 38, 30', '186, 26, 26': '179, 38, 30',
    '198, 40, 40': '179, 38, 30', '198,40,40': '179, 38, 30',
    '211, 47, 47': '179, 38, 30', '244, 67, 54': '179, 38, 30',
    '239, 83, 80': '179, 38, 30', '244,63,94': '179, 38, 30',
    # 琥珀 → attention #b06a00 = (176,106,0)
    '249, 168, 37': '176, 106, 0', '249,168,37': '176, 106, 0',
    '251, 140, 0': '176, 106, 0', '255, 152, 0': '176, 106, 0',
    '245, 158, 11': '176, 106, 0', '217, 119, 6': '176, 106, 0',
    '194, 65, 12': '176, 106, 0', '239, 108, 0': '176, 106, 0',
    # 中性黑(阴影)→ ink #1f2430 = (31,36,48)
    '0, 0, 0': '31, 36, 48', '0,0,0': '31, 36, 48',
}

RGBA_RE = re.compile(
    r'rgba\(\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*,\s*([0-9.]+)\s*\)')


def main():
    n = 0
    touched = {}
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in ('node_modules', 'dist', '.angular')]
        for fn in filenames:
            if not fn.endswith(('.scss', '.ts', '.html', '.css')):
                continue
            p = os.path.join(dirpath, fn)
            try:
                s = open(p, encoding='utf-8').read()
            except Exception:
                continue
            orig = s

            def sub(m):
                nonlocal n
                key = '%s, %s, %s' % (m.group(1), m.group(2), m.group(3))
                new = BASE_MAP.get(key)
                if new is None:
                    return m.group(0)
                n += 1
                return 'rgba(%s, %s)' % (new, m.group(4))

            s = RGBA_RE.sub(sub, s)
            if s != orig:
                open(p, 'w', encoding='utf-8').write(s)
                touched[p] = touched.get(p, 0) + 1
    print(f'替换 {n} 处,涉及文件 {len(touched)} 个')


if __name__ == '__main__':
    main()
