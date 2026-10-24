#!/usr/bin/env python3
"""票123 R-4:给受审面上的纯图标按钮补 `[attr.aria-label]`,与既有 matTooltip 同值。

背景:定稿 R-4 的验证条款要求「icon-only buttons without accessible names = 0」。
Material 的 `matTooltip` 只会挂 `aria-describedby`(悬停描述),**不是**可访问名;
所以只带 tooltip 的图标按钮在读屏下仍是无名控件。

本脚本做的事(纯加法,不动视觉、不动行为):
  · 找到「纯图标按钮」(内含 mat-icon、无可见文本)
  · 若它已有 aria-label → 跳过
  · 若它有 matTooltip="X" / [matTooltip]="X" → 在同一元素上补
      [attr.aria-label]="X"
    (matTooltip 是插值式 `matTooltip="{{ ... }}"` 时,aria-label 用同样的插值写法)
  · 若它没有任何 tooltip → 跳过(需要新文案,那属于设计决策,不在本脚本范围)

用法: python3 add-aria-labels-r4.py [--dry]
"""
import re
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"

SURFACES = [
    "features/auth/",
    "features/dashboard/",
    "features/tracker/",
    "features/playbook/",
    "features/techstack/",
]

HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
BUTTON_RE = re.compile(r"(<button\b[^>]*>)(.*?)(</button>)", re.S)
TOOLTIP_RE = re.compile(r"(?:\[matTooltip\]|matTooltip)\s*=\s*(?:\"([^\"]*)\"|'([^']*)')")
ARIA_RE = re.compile(r"\[\s*attr\.\s*aria-label\s*\]|\baria-label\s*=")


def is_icon_only(inner: str) -> bool:
    body = re.sub(r"<mat-icon\b[^>]*>.*?</mat-icon>", "", inner, flags=re.S)
    body = re.sub(r"<[^>]+>", "", body).replace("&nbsp;", "").strip()
    return body == ""


def add_aria(open_tag: str, indent: str) -> tuple[str, bool]:
    if ARIA_RE.search(open_tag):
        return open_tag, False
    m = TOOLTIP_RE.search(open_tag)
    if not m:
        return open_tag, False
    value = m.group(1) if m.group(1) is not None else m.group(2)
    aria = f'[attr.aria-label]="{value}"'
    idx = open_tag.rfind(">")
    if idx < 0:
        return open_tag, False
    return open_tag[:idx].rstrip() + "\n" + indent + aria + open_tag[idx:], True


def main() -> int:
    dry = "--dry" in sys.argv
    total = 0
    for prefix in SURFACES:
        base = WEB / prefix
        files = sorted(base.rglob("*")) if base.is_dir() else []
        for f in files:
            if not f.is_file() or f.suffix not in (".html", ".ts"):
                continue
            rel = f.relative_to(WEB)
            raw = f.read_text(encoding="utf-8")
            # 保护注释:注释里的 button 不该被改
            spans = [(m.start(), m.end()) for m in HTML_COMMENT.finditer(raw)]
            spans += [(m.start(), m.end()) for m in BLOCK_COMMENT.finditer(raw)]

            def in_comment(pos: int) -> bool:
                return any(s <= pos < e for s, e in spans)

            out = []
            last = 0
            changed = 0
            for m in BUTTON_RE.finditer(raw):
                if in_comment(m.start()):
                    continue
                open_tag, inner = m.group(1), m.group(2)
                if "mat-icon-button" not in open_tag or not is_icon_only(inner):
                    continue
                # 缩进:取 <button 在文件里那一列(行首到 <button 之间的空白)
                line_start = raw.rfind("\n", 0, m.start()) + 1
                line_prefix = raw[line_start:m.start()]
                indent = " " * (len(line_prefix) - len(line_prefix.lstrip()))
                new_open, ok = add_aria(open_tag, indent)
                if not ok:
                    continue
                out.append(raw[last:m.start()])
                out.append(new_open)
                last = m.start() + len(open_tag)
                changed += 1
            if changed:
                out.append(raw[last:])
                new_raw = "".join(out)
                total += changed
                print(f"  {rel}: +{changed} 处 aria-label")
                if not dry:
                    f.write_text(new_raw, encoding="utf-8")
    print(f"\n合计补 aria-label: {total} 处{'(dry-run,未写入)' if dry else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
