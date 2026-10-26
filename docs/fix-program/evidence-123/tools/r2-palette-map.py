#!/usr/bin/env python3
"""
票123 · R-2 调色板映射表(实施用)。

定稿 R-2 第 3 条:「Near-miss semantic colors map to their token」。
语义族 → :root 令牌:
  绿(通过/成功)      → --go         #1b7f4b / --go-tint    #e2f1e8
  红(失败/危险/拒绝) → --stop       #b3261e / --stop-tint  #f9e8e5
  琥珀(需注意/警示)  → --attention  #b06a00 / --attention-tint #faf0d2
  靛蓝(品牌/主色)    → --brand      #2e3a8c / --signal-tint #e4e7f5 / --signal-soft #c6cdea
  灰(文字/描边)      → --ink #1f2430 / --ink-2 #5b6270 / --parked #68707e
                     / --hairline #e1e4ec / --track #e2e5ec / --paper #f5f6f8
  底面(白/近白)      → --surface #ffffff / --paper #f5f6f8
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else '/workspace/your-interview/web/src/app'

# ── 语义族映射(旧 hex → 新 hex)──────────────────────────────
# 每条都给出理由类别,便于人工复核。
MAP = {
    # ── 绿:通过/成功 ──
    '#2e7d32': ('#1b7f4b', 'go'),
    '#1e7e34': ('#1b7f4b', 'go'),
    '#157f3d': ('#1b7f4b', 'go'),
    '#1faa5c': ('#1b7f4b', 'go'),
    '#34c77b': ('#1b7f4b', 'go'),
    '#43a047': ('#1b7f4b', 'go'),
    '#059669': ('#1b7f4b', 'go'),
    '#065f46': ('#1b7f4b', 'go'),
    '#34a853': ('#1b7f4b', 'go'),
    '#e8f5e9': ('#e2f1e8', 'go-tint'),
    '#c8e6c9': ('#e2f1e8', 'go-tint'),
    '#a5d6a7': ('#e2f1e8', 'go-tint'),
    '#bfe8cf': ('#e2f1e8', 'go-tint'),
    '#d9f7e8': ('#e2f1e8', 'go-tint'),
    '#e6f4ea': ('#e2f1e8', 'go-tint'),
    '#d1fae5': ('#e2f1e8', 'go-tint'),
    '#e6f7ee': ('#e2f1e8', 'go-tint'),

    # ── 红:失败/危险/拒绝 ──
    '#c62828': ('#b3261e', 'stop'),
    '#d32f2f': ('#b3261e', 'stop'),
    '#e53935': ('#b3261e', 'stop'),
    '#ef4444': ('#b3261e', 'stop'),
    '#d63c30': ('#b3261e', 'stop'),
    '#c53030': ('#b3261e', 'stop'),
    '#f0665e': ('#b3261e', 'stop'),
    '#ea4335': ('#b3261e', 'stop'),
    '#9f1239': ('#b3261e', 'stop'),
    '#7c1d16': ('#b3261e', 'stop'),
    '#ffebee': ('#f9e8e5', 'stop-tint'),
    '#ffcdd2': ('#f9e8e5', 'stop-tint'),
    '#fdecea': ('#f9e8e5', 'stop-tint'),
    '#fdecec': ('#f9e8e5', 'stop-tint'),
    '#f5c6c6': ('#f9e8e5', 'stop-tint'),
    '#fecdd3': ('#f9e8e5', 'stop-tint'),
    '#e57373': ('#f9e8e5', 'stop-tint'),
    '#f0b8b2': ('#f9e8e5', 'stop-tint'),

    # ── 琥珀:需注意/警示 ──
    '#ef6c00': ('#b06a00', 'attention'),
    '#e65100': ('#b06a00', 'attention'),
    '#f9a825': ('#b06a00', 'attention'),
    '#fb8c00': ('#b06a00', 'attention'),
    '#b26a00': ('#b06a00', 'attention'),
    '#a05a00': ('#b06a00', 'attention'),
    '#a15c00': ('#b06a00', 'attention'),
    '#ef9f1a': ('#b06a00', 'attention'),
    '#b7791f': ('#b06a00', 'attention'),
    '#7d6608': ('#b06a00', 'attention'),
    '#6d5410': ('#b06a00', 'attention'),
    '#e6a23c': ('#b06a00', 'attention'),
    '#f5b942': ('#b06a00', 'attention'),
    '#fbbc05': ('#b06a00', 'attention'),
    '#f0f0f0': ('#faf0d2', 'attention-tint'),
    '#fff3e0': ('#faf0d2', 'attention-tint'),
    '#fff8e1': ('#faf0d2', 'attention-tint'),
    '#fff6e0': ('#faf0d2', 'attention-tint'),
    '#fff7e0': ('#faf0d2', 'attention-tint'),
    '#fef3c7': ('#faf0d2', 'attention-tint'),
    '#fef9e7': ('#faf0d2', 'attention-tint'),
    '#fde68a': ('#faf0d2', 'attention-tint'),
    '#f9e79f': ('#faf0d2', 'attention-tint'),
    '#ead9a0': ('#faf0d2', 'attention-tint'),
    '#e6c86e': ('#faf0d2', 'attention-tint'),

    # ── 靛蓝/品牌 ──
    '#3f51b5': ('#2e3a8c', 'brand'),
    '#303f9f': ('#2e3a8c', 'brand'),
    '#1a237e': ('#1b2350', 'midnight'),
    '#283593': ('#1b2350', 'midnight'),
    '#1565c0': ('#2e3a8c', 'brand'),
    '#0d47a1': ('#2e3a8c', 'brand'),
    '#1e88e5': ('#2e3a8c', 'brand'),
    '#0a66c2': ('#2e3a8c', 'brand'),
    '#1a73e8': ('#2e3a8c', 'brand'),
    '#2b6cb0': ('#2e3a8c', 'brand'),
    '#4285f4': ('#2e3a8c', 'brand'),
    '#0e9fcc': ('#2e3a8c', 'brand'),
    '#0e7490': ('#2e3a8c', 'brand'),
    '#2bb5d8': ('#2e3a8c', 'brand'),
    '#e8f1fd': ('#e4e7f5', 'signal-tint'),
    '#e3f2fd': ('#e4e7f5', 'signal-tint'),
    '#f4f5fb': ('#e4e7f5', 'signal-tint'),
    '#f5f8ff': ('#e4e7f5', 'signal-tint'),
    '#f5f7fa': ('#e4e7f5', 'signal-tint'),
    '#f8fbff': ('#e4e7f5', 'signal-tint'),
    '#c4dcfa': ('#c6cdea', 'signal-soft'),
    '#c3d6f7': ('#c6cdea', 'signal-soft'),
    '#d4d9e0': ('#c6cdea', 'signal-soft'),
    '#c3c9d2': ('#c6cdea', 'signal-soft'),

    # ── 紫(非品牌强调,映射到品牌)──
    '#7b1fa2': ('#2e3a8c', 'brand'),
    '#f3e5f5': ('#e4e7f5', 'signal-tint'),

    # ── 青(信息,映射到品牌)──
    '#cffafe': ('#e4e7f5', 'signal-tint'),

    # ── 文字:深灰 → ink / ink-2 / parked ──
    '#111827': ('#1f2430', 'ink'),
    '#1f2937': ('#1f2430', 'ink'),
    '#1a1d23': ('#1f2430', 'ink'),
    '#111111': ('#1f2430', 'ink'),
    '#222222': ('#1f2430', 'ink'),
    '#333333': ('#1f2430', 'ink'),
    '#1a1a2e': ('#1f2430', 'ink'),
    '#101828': ('#1f2430', 'ink'),
    '#2b3340': ('#1f2430', 'ink'),
    '#3d4451': ('#1f2430', 'ink'),
    '#475467': ('#5b6270', 'ink-2'),
    '#5f6368': ('#5b6270', 'ink-2'),
    '#5a6472': ('#5b6270', 'ink-2'),
    '#6e7681': ('#5b6270', 'ink-2'),
    '#667085': ('#5b6270', 'ink-2'),
    '#6b7280': ('#68707e', 'parked'),
    '#6b6f76': ('#68707e', 'parked'),
    '#8a94a6': ('#68707e', 'parked'),
    '#757575': ('#68707e', 'parked'),
    '#666666': ('#5b6270', 'ink-2'),
    '#888888': ('#68707e', 'parked'),
    '#555555': ('#5b6270', 'ink-2'),
    '#616161': ('#5b6270', 'ink-2'),
    '#607d8b': ('#5b6270', 'ink-2'),
    '#455a64': ('#5b6270', 'ink-2'),
    '#607d8b ': ('#5b6270', 'ink-2'),

    # ── 描边/分隔:→ hairline / track ──
    '#e5e7eb': ('#e1e4ec', 'hairline'),
    '#e3e6ea': ('#e1e4ec', 'hairline'),
    '#e7eaee': ('#e1e4ec', 'hairline'),
    '#e9ebee': ('#e1e4ec', 'hairline'),
    '#eaecf0': ('#e1e4ec', 'hairline'),
    '#e9edf2': ('#e1e4ec', 'hairline'),
    '#e6ebf1': ('#e1e4ec', 'hairline'),
    '#eef2f7': ('#e1e4ec', 'hairline'),
    '#eef2f6': ('#e1e4ec', 'hairline'),
    '#eceef1': ('#e1e4ec', 'hairline'),
    '#eceff1': ('#e1e4ec', 'hairline'),
    '#e0e0e0': ('#e1e4ec', 'hairline'),
    '#dddddd': ('#e1e4ec', 'hairline'),
    '#e2e8f0': ('#e1e4ec', 'hairline'),
    '#e2e5ec': ('#e2e5ec', 'track'),
    '#c9ccd2': ('#e2e5ec', 'track'),
    '#98a2b3': ('#e1e4ec', 'hairline'),

    # ── 底面:→ surface / paper / parked-fill ──
    '#f9fafb': ('#f5f6f8', 'paper'),
    '#f7f8fa': ('#f5f6f8', 'paper'),
    '#f2f4f7': ('#f5f6f8', 'paper'),
    '#f8fafc': ('#f5f6f8', 'paper'),
    '#fafafa': ('#f5f6f8', 'paper'),
    '#f5f5f5': ('#f5f6f8', 'paper'),
    '#fafbfe': ('#f5f6f8', 'paper'),
    '#fcfcfe': ('#f5f6f8', 'paper'),
    '#f6f7f9': ('#f5f6f8', 'paper'),
    '#edf0f4': ('#edf0f4', 'parked-fill'),
    '#f0f0f0 ': ('#edf0f4', 'parked-fill'),
    '#eeeeee': ('#edf0f4', 'parked-fill'),
}

# 归一化键(去掉尾部空格)
MAP = {k.strip(): v for k, v in MAP.items()}

HEX_RE = re.compile(r'#([0-9a-fA-F]{3,8})\b')


def norm(h):
    h = h.lower()
    if len(h) == 3:
        return '#' + ''.join(c * 2 for c in h)
    if len(h) == 4:
        return '#' + ''.join(c * 2 for c in h)
    return '#' + h


def main():
    touched = {}
    counter = {'n': 0}
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
                raw = m.group(1)
                if len(raw) not in (3, 6, 8):
                    return m.group(0)
                h = norm(raw)
                if h not in MAP:
                    return m.group(0)
                new, tag = MAP[h]
                counter['n'] += 1
                return new

            s = HEX_RE.sub(sub, s)
            if s != orig:
                open(p, 'w', encoding='utf-8').write(s)
                touched[p] = touched.get(p, 0) + 1
    print(f"替换 {counter['n']} 处,涉及文件 {len(touched)} 个")
    for k in sorted(touched):
        print(' ', os.path.relpath(k, ROOT))


if __name__ == '__main__':
    main()
