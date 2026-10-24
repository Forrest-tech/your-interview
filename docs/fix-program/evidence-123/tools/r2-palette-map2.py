#!/usr/bin/env python3
"""
票123 · R-2 调色板映射表 第二遍(全仓:AI Practice / AI Mock / Analytics / Resume / Admin / shared / layout)。

定稿 R-2 第 3 条:「Near-miss semantic colors map to their token」+「app-wide」。

规则(按语义族,不是按色相自动猜):
  绿族 → --go / --go-tint
  红族 → --stop / --stop-tint
  琥珀族 → --attention / --attention-tint
  靛蓝/蓝/紫/青(强调色) → --brand / --signal-tint / --signal-soft
  文字深灰 → --ink / --ink-2 / --parked
  描边灰 → --hairline / --track
  底面灰白 → --paper / --surface / --parked-fill

豁免(不动):
  - #c3c9d4  定稿 Appendix V 钉位(The Track 节点描边,R-9 复用该几何)
  - #7d8797  滚动条滑块色(§0.3 豁免项)
  - 头像 hsl() 产物(非 hex,不在本表范围)
"""
import os
import re
import sys

ROOT = sys.argv[1] if len(sys.argv) > 1 else '/workspace/your-interview/web/src/app'

MAP = {
    # ═══ 绿:通过/成功 ═══
    '#2e9e5b': '#1b7f4b', '#22a35c': '#1b7f4b', '#1d8f50': '#1b7f4b',
    '#10b981': '#1b7f4b', '#047857': '#1b7f4b', '#15803d': '#1b7f4b',
    '#1b5e20': '#1b7f4b', '#33691e': '#1b7f4b', '#008a3e': '#1b7f4b',
    '#00873f': '#1b7f4b', '#4a9c58': '#1b7f4b', '#3f9142': '#1b7f4b',
    '#2e7d32 ': '#1b7f4b', '#3fa34d': '#1b7f4b', '#52b788': '#1b7f4b',
    '#40916c': '#1b7f4b', '#2d6a4f': '#1b7f4b', '#008060': '#1b7f4b',
    '#1a7f37': '#1b7f4b', '#216e39': '#1b7f4b', '#0f6b32': '#1b7f4b',
    '#4c9a52': '#1b7f4b', '#7bc47f': '#1b7f4b', '#5cb85c': '#1b7f4b',
    '#d6f0e0': '#e2f1e8', '#dcfce7': '#e2f1e8', '#bbf7d0': '#e2f1e8',
    '#e9f8ef': '#e2f1e8', '#f0faf4': '#e2f1e8', '#e8f6ec': '#e2f1e8',
    '#e6f7ee ': '#e2f1e8', '#d9f7e8 ': '#e2f1e8', '#d1fae5 ': '#e2f1e8',
    '#c8e6c9 ': '#e2f1e8', '#d1e7dd': '#e2f1e8', '#e3f2e7': '#e2f1e8',
    '#c6efd0': '#e2f1e8', '#d4edda': '#e2f1e8', '#e8f5e9 ': '#e2f1e8',
    '#a5d6a7 ': '#e2f1e8', '#bfe8cf ': '#e2f1e8', '#e6f4ea ': '#e2f1e8',
    '#d9f7e8': '#e2f1e8', '#d1fae5': '#e2f1e8', '#c8e6c9': '#e2f1e8',
    '#e6f7ee': '#e2f1e8', '#e8f5e9': '#e2f1e8', '#a5d6a7': '#e2f1e8',
    '#bfe8cf': '#e2f1e8', '#e6f4ea': '#e2f1e8', '#dcfce7': '#e2f1e8',
    '#bbf7d0': '#e2f1e8', '#e9f8ef': '#e2f1e8', '#f0faf4': '#e2f1e8',

    # ═══ 红:失败/危险/拒绝 ═══
    '#b91c1c': '#b3261e', '#c0392b': '#b3261e', '#d93025': '#b3261e',
    '#dc2626': '#b3261e', '#c5221f': '#b3261e', '#e5484d': '#b3261e',
    '#d44c47': '#b3261e', '#f0575a': '#b3261e', '#e11d48': '#b3261e',
    '#be123c': '#b3261e', '#9f1239 ': '#b3261e', '#7c1d16 ': '#b3261e',
    '#b42318': '#b3261e', '#d92d20': '#b3261e', '#ef4444 ': '#b3261e',
    '#c62828 ': '#b3261e', '#d32f2f ': '#b3261e', '#e53935 ': '#b3261e',
    '#f66b6b': '#b3261e', '#ef5350': '#b3261e', '#e05252': '#b3261e',
    '#d64545': '#b3261e', '#c94f4f': '#b3261e', '#b04a4a': '#b3261e',
    '#a94442': '#b3261e', '#cc3333': '#b3261e', '#d14444': '#b3261e',
    '#fe4d4d': '#b3261e', '#ff5c5c': '#b3261e', '#e35d6a': '#b3261e',
    '#fef2f2': '#f9e8e5', '#fde8f0': '#f9e8e5', '#ffe4e0': '#f9e8e5',
    '#fecaca': '#f9e8e5', '#fdecea ': '#f9e8e5', '#fdecec ': '#f9e8e5',
    '#ffcdd2 ': '#f9e8e5', '#f5c6c6 ': '#f9e8e5', '#fecdd3 ': '#f9e8e5',
    '#e57373 ': '#f9e8e5', '#f0b8b2 ': '#f9e8e5', '#ffebee ': '#f9e8e5',
    '#f8d7da': '#f9e8e5', '#fadbd8': '#f9e8e5', '#fde2e2': '#f9e8e5',

    # ═══ 琥珀:需注意/警示 ═══
    '#e8952f': '#b06a00', '#ff9800': '#b06a00', '#d9730d': '#b06a00',
    '#b45309': '#b06a00', '#d9a30a': '#b06a00', '#d9a326': '#b06a00',
    '#9a6b00': '#b06a00', '#906500': '#b06a00', '#8a6d1f': '#b06a00',
    '#f57f17': '#b06a00', '#a05a00 ': '#b06a00', '#a15c00 ': '#b06a00',
    '#b26a00 ': '#b06a00', '#ef6c00 ': '#b06a00', '#e65100 ': '#b06a00',
    '#f9a825 ': '#b06a00', '#fb8c00 ': '#b06a00', '#ef9f1a ': '#b06a00',
    '#b7791f ': '#b06a00', '#7d6608 ': '#b06a00', '#6d5410 ': '#b06a00',
    '#e6a23c ': '#b06a00', '#f5b942 ': '#b06a00', '#fbbc05 ': '#b06a00',
    '#c47f17': '#b06a00', '#a16207': '#b06a00', '#ca8a04': '#b06a00',
    '#fff8e6': '#faf0d2', '#f5e3b3': '#faf0d2', '#fff4d6': '#faf0d2',
    '#fffbeb': '#faf0d2', '#ffe0b2': '#faf0d2', '#fff3e0 ': '#faf0d2',
    '#fff8e1 ': '#faf0d2', '#fff6e0 ': '#faf0d2', '#fff7e0 ': '#faf0d2',
    '#fef3c7 ': '#faf0d2', '#fef9e7 ': '#faf0d2', '#fde68a ': '#faf0d2',
    '#f9e79f ': '#faf0d2', '#ead9a0 ': '#faf0d2', '#e6c86e ': '#faf0d2',
    '#f0f0f0 ': '#faf0d2', '#fff1cc': '#faf0d2', '#fdf0c8': '#faf0d2',

    # ═══ 靛蓝/蓝/紫/青:强调 → brand 族 ═══
    '#2f6fed': '#2e3a8c', '#2563eb': '#2e3a8c', '#1d4ed8': '#2e3a8c',
    '#3b82f6': '#2e3a8c', '#4a6285': '#2e3a8c', '#6b86b8': '#2e3a8c',
    '#14b8a6': '#2e3a8c', '#0e9fcc': '#2e3a8c', '#2bb5d8': '#2e3a8c',
    '#0e7490': '#2e3a8c', '#4285f4': '#2e3a8c', '#1a73e8': '#2e3a8c',
    '#0a66c2': '#2e3a8c', '#2b6cb0': '#2e3a8c', '#0d47a1': '#2e3a8c',
    '#1e88e5': '#2e3a8c', '#1565c0': '#2e3a8c', '#303f9f': '#2e3a8c',
    '#3f51b5': '#2e3a8c', '#8b5cf6': '#2e3a8c', '#5e35b1': '#2e3a8c',
    '#b8266b': '#2e3a8c', '#7b1fa2': '#2e3a8c', '#1a237e': '#1b2350',
    '#283593': '#1b2350', '#448361': '#2e3a8c',
    '#eef4ff': '#e4e7f5', '#f2f7ff': '#e4e7f5', '#dbe7ff': '#e4e7f5',
    '#e4eeff': '#e4e7f5', '#e8eef7': '#e4e7f5', '#e3f2fd ': '#e4e7f5',
    '#e8f1fd ': '#e4e7f5', '#f4f5fb ': '#e4e7f5', '#f5f8ff ': '#e4e7f5',
    '#f5f7fa ': '#e4e7f5', '#f8fbff ': '#e4e7f5', '#fafbfe ': '#e4e7f5',
    '#fcfcfe ': '#e4e7f5', '#fbfcfe': '#e4e7f5', '#f3f5f9': '#e4e7f5',
    '#f3e5f5 ': '#e4e7f5', '#cffafe ': '#e4e7f5', '#c5cae9': '#c6cdea',
    '#c4dcfa ': '#c6cdea', '#c3d6f7 ': '#c6cdea', '#d4d9e0 ': '#c6cdea',
    '#c9cedb ': '#c6cdea', '#c3c9d2 ': '#c6cdea',

    # ═══ 文字:深灰 → ink ═══
    '#1f2733': '#1f2430', '#333a44': '#1f2430', '#1e293b': '#1f2430',
    '#0f172a': '#1f2430', '#24272c': '#1f2430', '#1a1d23 ': '#1f2430',
    '#1f2937 ': '#1f2430', '#111827 ': '#1f2430', '#2b3340 ': '#1f2430',
    '#3d4451 ': '#1f2430', '#3b4351': '#1f2430', '#4b5462': '#1f2430',
    '#444455': '#1f2430', '#3a3f47': '#1f2430', '#374151': '#1f2430',
    '#334155': '#1f2430', '#4b5563': '#1f2430', '#2b3340 ': '#1f2430',
    '#111111 ': '#1f2430', '#222222 ': '#1f2430', '#333333 ': '#1f2430',
    '#1a1a2e ': '#1f2430', '#101828 ': '#1f2430', '#1a1a1a': '#1f2430',
    '#2c2c2c': '#1f2430', '#3d3d3d': '#1f2430', '#2f2f2f': '#1f2430',
    '#1c1c1e': '#1f2430', '#212121': '#1f2430', '#263238': '#1f2430',
    '#37474f': '#1f2430', '#212121 ': '#1f2430',

    # ═══ 文字:中灰 → ink-2 / parked ═══
    '#5b6472': '#5b6270', '#475569': '#5b6270', '#64748b': '#5b6270',
    '#6b7484': '#5b6270', '#6d7787': '#5b6270', '#55606f': '#5b6270',
    '#616976': '#5b6270', '#667085 ': '#5b6270', '#6e7681 ': '#5b6270',
    '#5a6472 ': '#5b6270', '#5f6368 ': '#5b6270', '#6b6f76 ': '#5b6270',
    '#5b6270 ': '#5b6270', '#666666 ': '#5b6270', '#555555 ': '#5b6270',
    '#757575 ': '#5b6270', '#616161 ': '#5b6270', '#607d8b ': '#5b6270',
    '#455a64 ': '#5b6270', '#6b7280 ': '#5b6270', '#888888 ': '#5b6270',
    '#9aa1ab': '#68707e', '#9aa3b0': '#68707e', '#94a3b8': '#68707e',
    '#8a93a2': '#68707e', '#8b93a1': '#68707e', '#98a1ad': '#68707e',
    '#7a8393': '#68707e', '#8a94a6 ': '#68707e', '#a8aeb8': '#68707e',
    '#9ca3af': '#68707e', '#7b828c': '#68707e', '#7d8797 ': '#68707e',
    '#bbbbbb ': '#68707e', '#a2abb9': '#68707e', '#9e9e9e': '#68707e',
    '#8e8e93': '#68707e', '#8a8a8e': '#68707e', '#8d8d91': '#68707e',
    '#98989d': '#68707e', '#79716e': '#68707e', '#6e6e73': '#68707e',
    '#6c6c70': '#68707e', '#6a6a6e': '#68707e', '#737373': '#68707e',
    '#828282': '#68707e', '#8c8c8c': '#68707e', '#999999': '#68707e',
    '#8d8d8d': '#68707e', '#949494': '#68707e', '#a0a0a0': '#68707e',
    '#b0b0b0': '#68707e',

    # ═══ 描边/分隔 → hairline / track ═══
    '#cbd5e1': '#e1e4ec', '#d7dce4': '#e1e4ec', '#e3e8ef': '#e1e4ec',
    '#e3e7ee': '#e1e4ec', '#eef1f5': '#e1e4ec', '#c5cdd9': '#e1e4ec',
    '#e8eaed': '#e1e4ec', '#f1f3f5': '#e1e4ec', '#dde3ec': '#e1e4ec',
    '#e6eaf0': '#e1e4ec', '#e8ecf2': '#e1e4ec', '#b3bcc9': '#e1e4ec',
    '#a7b0bf': '#e1e4ec', '#c8cdd6': '#e1e4ec', '#c8ced8': '#e1e4ec',
    '#d4d9e0': '#e2e5ec', '#c9ccd2': '#e2e5ec', '#e2e8f0': '#e1e4ec',
    '#e5e7eb': '#e1e4ec', '#e3e6ea': '#e1e4ec', '#e7eaee': '#e1e4ec',
    '#e9ebee': '#e1e4ec', '#eaecf0': '#e1e4ec', '#e9edf2': '#e1e4ec',
    '#e6ebf1': '#e1e4ec', '#eef2f7': '#e1e4ec', '#eef2f6': '#e1e4ec',
    '#eceef1': '#e1e4ec', '#eceff1': '#e1e4ec', '#e0e0e0 ': '#e1e4ec',
    '#dddddd ': '#e1e4ec', '#98a2b3 ': '#e1e4ec', '#e2e5ec ': '#e2e5ec',
    '#d1d5db': '#e1e4ec', '#cfd4dc': '#e1e4ec', '#dee2e6': '#e1e4ec',
    '#ced4da': '#e1e4ec', '#c6cbd3': '#e1e4ec', '#d6d6d6': '#e1e4ec',
    '#e4e4e4': '#e1e4ec', '#e0e0e0 ': '#e1e4ec', '#e6e6e6': '#e1e4ec',
    '#dcdcdc': '#e1e4ec', '#d3d3d3': '#e1e4ec', '#f0f0f0': '#edf0f4',

    # ═══ 底面 → paper / surface / parked-fill ═══
    '#f1f5f9': '#f5f6f8', '#f7f9fc': '#f5f6f8', '#f9fafb ': '#f5f6f8',
    '#f7f8fa ': '#f5f6f8', '#f2f4f7 ': '#f5f6f8', '#f8fafc ': '#f5f6f8',
    '#fafafa ': '#f5f6f8', '#f5f5f5 ': '#f5f6f8', '#f6f7f9 ': '#f5f6f8',
    '#f5f6f8 ': '#f5f6f8', '#f3f4f6': '#f5f6f8', '#f4f5f7': '#f5f6f8',
    '#f4f4f5': '#f5f6f8', '#f0f0f0 ': '#edf0f4', '#eeeeee ': '#edf0f4',
    '#f2f2f2': '#edf0f4', '#fafafa ': '#f5f6f8',
    '#ffffff ': '#ffffff', '#fff    ': '#ffffff',

    # ═══ 第三遍:剩余零散色 ═══
    # 琥珀文字
    '#92400e': '#b06a00', '#6f5714': '#b06a00',
    # 中灰文字
    '#5a6270': '#5b6270', '#7c8593': '#68707e', '#7b8494': '#68707e',
    '#8a9099': '#68707e', '#33363d': '#1f2430', '#363a41': '#1f2430',
    '#33383f': '#1f2430', '#34383f': '#1f2430', '#1c1e21': '#1f2430',
    '#24262b': '#1f2430', '#2b2e35': '#1f2430', '#444444': '#1f2430',
    '#b9c2d0': '#e1e4ec', '#12183a': '#1b2350',
    # 绿
    '#1b7f3b': '#1b7f4b', '#1b8c4d': '#1b7f4b', '#166534': '#1b7f4b',
    '#7cb342': '#1b7f4b', '#a7d9be': '#e2f1e8', '#cde9d2': '#e2f1e8',
    '#f1f8e9': '#e2f1e8', '#edf9f1': '#e2f1e8', '#e6f6ec': '#e2f1e8',
    '#f4fbf5': '#e2f1e8', '#f7fdfa': '#e2f1e8', '#f7fdf9': '#e2f1e8',
    '#f0fdf6': '#e2f1e8', '#86efac': '#e2f1e8',
    # 红
    '#b71c1c': '#b3261e', '#991b1b': '#b3261e', '#dd3333': '#b3261e',
    '#f0b4b4': '#f9e8e5', '#fdf3f3': '#f9e8e5',
    # 琥珀
    '#f59e0b': '#b06a00', '#d97706': '#b06a00', '#c2410c': '#b06a00',
    '#fcd34d': '#faf0d2', '#fed7aa': '#faf0d2', '#fff4e5': '#faf0d2',
    '#fff7ed': '#faf0d2', '#6f5714 ': '#b06a00',
    # 靛蓝/品牌
    '#2560d8': '#2e3a8c', '#1b57d0': '#2e3a8c', '#5b7fd4': '#2e3a8c',
    '#9fa8da': '#b6bee6', '#7986cb': '#8e9ad6', '#5c6bc0': '#6673c2',
    '#90caf9': '#c6cdea', '#5d4037': '#2e3a8c', '#8d6e63': '#2e3a8c',
    # 蓝/靛 tint
    '#d3e0f7': '#c6cdea', '#d9e6ff': '#c6cdea', '#c7ddff': '#c6cdea',
    '#e0ebff': '#e4e7f5', '#e5e9f0': '#e4e7f5', '#e0e5ec': '#e4e7f5',
    '#eceff3': '#e4e7f5', '#e8eaf6': '#e4e7f5', '#e8edf5': '#e4e7f5',
    '#f2f6fd': '#e4e7f5', '#f5f9ff': '#e4e7f5', '#f0f7ff': '#e4e7f5',
    '#eff6ff': '#e4e7f5', '#f0f2f5': '#e4e7f5', '#f7f9fb': '#e4e7f5',
    '#f7f7fa': '#e4e7f5', '#f6f8fb': '#e4e7f5', '#e8f0fe': '#e4e7f5',
    '#f0b4b4 ': '#f9e8e5', '#fafbfc': '#f5f6f8', '#fafafc': '#f5f6f8',
    '#fafcff': '#f5f6f8', '#f7f9fb ': '#f5f6f8', '#f8f9fa': '#f5f6f8',
    '#fcfcfc': '#f5f6f8', '#cccccc': '#e1e4ec',
    # 青/紫 tint
    '#e8f8f6': '#e4e7f5', '#d3f0ec': '#e4e7f5', '#f3eeff': '#e4e7f5',
    '#e6dcff': '#e4e7f5',
}

MAP = {k.strip(): v for k, v in MAP.items()}

# 豁免(定稿钉位 / §0.3 豁免)
EXEMPT = {'#c3c9d4', '#7d8797'}

HEX_RE = re.compile(r'#([0-9a-fA-F]{3,8})\b')


def norm(h):
    h = h.lower()
    if len(h) == 3:
        return '#' + ''.join(c * 2 for c in h)
    if len(h) == 4:
        return '#' + ''.join(c * 2 for c in h)
    return '#' + h


def main():
    counter = {'n': 0}
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
                raw = m.group(1)
                if len(raw) not in (3, 6, 8):
                    return m.group(0)
                h = norm(raw)
                if h in EXEMPT or h not in MAP:
                    return m.group(0)
                counter['n'] += 1
                return MAP[h]

            s = HEX_RE.sub(sub, s)
            if s != orig:
                open(p, 'w', encoding='utf-8').write(s)
                touched[p] = touched.get(p, 0) + 1
    print(f"替换 {counter['n']} 处,涉及文件 {len(touched)} 个")


if __name__ == '__main__':
    main()
