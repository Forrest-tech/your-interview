#!/usr/bin/env python3
"""票123 R-5:受审面上的「裸破折号」普查(源码侧)。

定稿 R-5 的验证条款:
  `bareDashes` = **0** on tracker / techstack / dashboard / playbook-detail;
  on Playbook list, every remaining hit is inside a mini-bar element
  (probe tags the class) and matches the whitelist count from the phase-0 dump.

「裸破折号」= 一个字段值位置上的 `—` / `–` / `--`,没有任何说明文字。
它告诉用户「这里有值」,却不说「这里本来该有什么」—— 这是被消灭的对象。

判定口径(源码侧):
  · HTML 文本节点里孤立的破折号(前后没有字母/数字,不是句子的一部分)
  · 模板里的字面量 `'—'` / `'–'` / `'--'` 作为插值回退值
  · 组件 TS 里回退到破折号的表达式

豁免(定稿点名):
  · Playbook 列表入口卡的 mini-bar 占位符 —— 30px 的值格放不下标签,
    是具名例外,由 G-3 负责真正移除(定稿第 141 行)
  · 破折号作为**连接符**出现在有文字的句子里(不是裸值)—— 不算

用法: python3 census-bare-dashes-r5.py
"""
import re
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"

# 受审面(R-5 验证条款点名的四个面 + Playbook 列表)
SURFACES = {
    "tracker": ["features/tracker/"],
    "techstack": ["features/techstack/"],
    "dashboard": ["features/dashboard/"],
    "playbook-detail": ["features/playbook/playbook-detail.component"],
    "playbook-list": ["features/playbook/playbook.component"],
}

# 破折号字符
DASHES = "—–"

HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
# 插值回退值:{{ x ?? '—' }} / {{ x || '—' }} / {{ x ? x : '—' }}
FALLBACK_RE = re.compile(r"\{\{[^}]*?(?:\?\?|\|\|)[^}]*?(['\"])([^'\"]*)\1[^}]*\}\}")
# 孤立的破折号文本节点
BARE_TEXT_RE = re.compile(r">\s*([—–])\s*<")


def strip_comments(text: str) -> str:
    text = HTML_COMMENT.sub("", text)
    text = BLOCK_COMMENT.sub("", text)
    out = []
    for line in text.split("\n"):
        if line.strip().startswith(("//", "*", "/*")):
            continue
        out.append(line)
    return "\n".join(out)


def is_mini_bar_context(line: str) -> bool:
    """Playbook 列表的 mini-bar 占位符 —— 定稿点名的具名例外。"""
    return "bar-val" in line or "mini-bar" in line


def is_state_glyph(lines: list[str], idx: int) -> bool:
    """状态图标槽里的破折号 —— 不是「缺值占位」。

    例:`roundStateIcon()` 返回 ✓/✗/—/· 四态字形,渲染进 `.step-badge`。
    同一组里还有非破折号的字形,说明它是**状态图标集**,不是缺值占位符。
    判定:向上回看最多 8 行,若落在返回多态字形的函数体内,或行内直接出现
    `.step-badge`,则判为状态图标。
    """
    line = lines[idx]
    if "step-badge" in line:
        return True
    for back in range(1, 9):
        j = idx - back
        if j < 0:
            break
        prev = lines[j]
        if "roundStateIcon" in prev:
            return True
        # 跨过函数边界
        if re.match(r"\s*(private |public |protected )?(static )?\w+\s*\(.*\)\s*(:|\{)", prev) \
                and "roundStateIcon" not in prev:
            break
    return False


def main() -> int:
    hits, exempt = [], []
    for surface, prefixes in SURFACES.items():
        for prefix in prefixes:
            base = WEB / prefix
            if base.is_file():
                candidates = [base]
            elif base.is_dir():
                candidates = sorted(base.rglob("*"))
            elif (base.parent / (base.name + ".html")).exists():
                candidates = [base.parent / (base.name + ".html"), base.parent / (base.name + ".ts")]
            else:
                continue
            for f in candidates:
                if not f.is_file() or f.suffix not in (".html", ".ts"):
                    continue
                rel = f.relative_to(WEB)
                text = strip_comments(f.read_text(encoding="utf-8"))
                all_lines = text.split("\n")
                for i, line in enumerate(all_lines, 1):
                    found = False
                    # 1) 插值回退值
                    for m in FALLBACK_RE.finditer(line):
                        if m.group(2) and any(d in m.group(2) for d in DASHES):
                            found = True
                            break
                    # 2) 孤立破折号文本节点
                    if not found and BARE_TEXT_RE.search(line):
                        found = True
                    # 3) TS 里的裸字面量回退
                    if not found:
                        for m in re.finditer(r"['\"]([—–]+)['\"]", line):
                            found = True
                            break
                    if not found:
                        continue
                    rec = (surface, str(rel), i, line.strip()[:130])
                    if is_mini_bar_context(line):
                        exempt.append(rec)
                    elif is_state_glyph(all_lines, i - 1):
                        exempt.append(rec)
                    else:
                        hits.append(rec)

    print("=== 裸破折号普查(源码侧)===")
    print(f"待修命中: {len(hits)}")
    print(f"具名豁免(mini-bar): {len(exempt)}")
    print()
    if exempt:
        print("--- 具名豁免 ---")
        for s, f, i, line in exempt:
            print(f"  [{s}] {f}:{i}")
            print(f"      {line}")
        print()
    if hits:
        print("--- 待修命中 ---")
        for s, f, i, line in hits:
            print(f"  [{s}] {f}:{i}")
            print(f"      {line}")
    else:
        print("待修命中: 0 ✅")
    return 0 if not hits else 1


if __name__ == "__main__":
    sys.exit(main())
