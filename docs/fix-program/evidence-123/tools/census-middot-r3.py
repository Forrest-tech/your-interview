#!/usr/bin/env python3
"""票123 R-3:受审面中点普查(源码侧)。

定稿 R-3.3 的目标是「middotJoins = 0 on every audited page」。
本脚本做源码侧普查:扫受审面模板 + 其组件 TS 的可渲染字符串(排除注释),
命中 ` · ` 的行逐条打印 —— 用于实现期自查,与浏览器 census 的口径一致:
只算会渲染成正文的 ` · ` 串。

用法: python3 census-middot-r3.py
"""
import re
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"

# 受审七面(定稿 R-3 Verification 行):login / dashboard / tracker / playbook /
# playbook-detail / techstack / calendar
SURFACES = {
    "login": ["features/auth/"],
    "dashboard": ["features/dashboard/"],
    "tracker": ["features/tracker/"],
    "playbook": ["features/playbook/playbook.component"],
    "playbook-detail": ["features/playbook/playbook-detail.component"],
    "techstack": ["features/techstack/"],
    "calendar": ["features/playbook/playbook-calendar.component"],
}

# 豁免(定稿 R-3.3 点名):
#   #5 票73 flow-strip middot(user-confirmed,渲染在换行后,census 不可见)
WHITELIST = [
    (re.compile(r"tracker\.component\.html"), "flow-strip(豁免 #5:票 73 用户确认稿)"),
]

BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
LINE_COMMENT = re.compile(r"^\s*(//|\*|/\*)")
HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)


def strip_comments(text: str, is_html: bool) -> str:
    if is_html:
        text = HTML_COMMENT.sub("", text)
        # HTML 注释块内可能含 TS 风格注释,再清一次
    text = BLOCK_COMMENT.sub("", text)
    out = []
    for line in text.split("\n"):
        if LINE_COMMENT.match(line):
            continue
        out.append(line)
    return "\n".join(out)


def main() -> int:
    hits = []
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
                print(f"[warn] 面 {surface} 的路径不存在: {base}")
                continue
            for f in candidates:
                if not f.is_file() or f.suffix not in (".html", ".ts"):
                    continue
                is_html = f.suffix == ".html"
                rel = f.relative_to(WEB)
                text = strip_comments(f.read_text(encoding="utf-8"), is_html)
                for i, line in enumerate(text.split("\n"), 1):
                    if " · " not in line:
                        continue
                    whitelist_reason = None
                    for pat, reason in WHITELIST:
                        if pat.search(str(rel)):
                            whitelist_reason = reason
                            break
                    hits.append((surface, str(rel), i, line.strip(), whitelist_reason))

    real = [h for h in hits if h[4] is None]
    exempted = [h for h in hits if h[4] is not None]

    print(f"=== 中点普查(源码侧)===")
    print(f"命中总数: {len(hits)}(其中豁免 {len(exempted)}, 需修 {len(real)})")
    print()
    if exempted:
        print("--- 豁免命中 ---")
        for s, f, i, line, reason in exempted:
            print(f"  [{s}] {f}:{i}  ({reason})")
            print(f"      {line[:120]}")
        print()
    if real:
        print("--- 待修命中 ---")
        for s, f, i, line, _ in real:
            print(f"  [{s}] {f}:{i}")
            print(f"      {line[:140]}")
    else:
        print("待修命中: 0 ✅")
    return 0 if not real else 1


if __name__ == "__main__":
    sys.exit(main())
