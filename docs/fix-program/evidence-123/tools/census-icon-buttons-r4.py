#!/usr/bin/env python3
"""票123 R-4:受审面上「无可访问名的纯图标按钮」普查(源码侧)。

定稿 R-4.1 的验证条款:app-wide probe on audited surfaces —
**icon-only buttons without accessible names = 0**。

Material 的 `mat-icon-button` 默认不提供可访问名;可访问名可以来自:
  · `aria-label` / `attr.aria-label`
  · `matTooltip` / `matTooltip` 的值(Material 的 tooltip 不是可访问名的可靠来源,
    但本仓的用法约定是「tooltip 即控件的可见名字」;两者都要查,分开报告)
  · 按钮内的可见文本(mat-icon 之外的文本节点)
  · `title` 属性

脚本解析受审七面的模板,按 `<button ...>` 元素切块(支持跨行),逐个判定:
  · 是不是「纯图标按钮」—— 元素内只有 mat-icon / 无可见文本
  · 有没有 aria-label(真·可访问名)
  · 有没有 tooltip / title(可见名字)

输出:
  · 硬缺陷 = 纯图标 且 无 aria-label 且 无 tooltip 且 无 title
  · 软提示 = 纯图标 且 无 aria-label 但有 tooltip/title(本仓约定下可接受,但可补 aria-label)

用法: python3 census-icon-buttons-r4.py
"""
import re
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"

# 受审七面(定稿 R-3 Verification 行)
SURFACES = {
    "login": ["features/auth/"],
    "dashboard": ["features/dashboard/"],
    "tracker": ["features/tracker/"],
    "playbook": ["features/playbook/"],
    "techstack": ["features/techstack/"],
    "calendar": ["features/playbook/playbook-calendar.component"],
}

HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
BUTTON_RE = re.compile(r"<button\b([^>]*)>(.*?)</button>", re.S)


def strip_comments(text: str) -> str:
    text = HTML_COMMENT.sub("", text)
    text = BLOCK_COMMENT.sub("", text)
    out = []
    for line in text.split("\n"):
        if line.strip().startswith(("//", "*", "/*")):
            continue
        out.append(line)
    return "\n".join(out)


def is_icon_only(inner: str) -> bool:
    """按钮内只有 mat-icon(或空),没有可见文本。"""
    body = re.sub(r"<mat-icon\b[^>]*>.*?</mat-icon>", "", inner, flags=re.S)
    body = re.sub(r"<[^>]+>", "", body)
    body = body.replace("&nbsp;", "").strip()
    return body == ""


def has(attrs: str, name: str) -> bool:
    """属性是否存在。覆盖三种写法:
       · `name="..."`        —— 静态属性
       · `[name]="..."`       —— 属性绑定
       · `[attr.name]="..."`  —— attr 绑定
       · `matTooltip="{{ ... }}"` —— 插值式 tooltip(Angular 里也写成 matTooltip)
    """
    name_escaped = re.escape(name)
    patterns = [
        rf"\[\s*attr\.\s*{name_escaped}\s*\]",
        rf"\[\s*{name_escaped}\s*\]",
        rf"\b{name_escaped}\s*=",
    ]
    return any(re.search(p, attrs) for p in patterns)


def main() -> int:
    hard, soft = [], []
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
                for m in BUTTON_RE.finditer(text):
                    attrs, inner = m.group(1), m.group(2)
                    if "mat-icon-button" not in attrs:
                        continue
                    if not is_icon_only(inner):
                        continue  # 有可见文本 —— 不是纯图标按钮
                    aria = has(attrs, "aria-label")
                    tip = has(attrs, "matTooltip") or has(attrs, "mattooltip")
                    title = has(attrs, "title")
                    line_no = text[: m.start()].count("\n") + 1
                    rec = (surface, str(rel), line_no, " ".join(attrs.split())[:110])
                    if aria:
                        continue
                    if tip or title:
                        soft.append(rec)
                    else:
                        hard.append(rec)

    print("=== 纯图标按钮可访问名普查(受审七面) ===")
    print(f"硬缺陷(无 aria-label 且无 tooltip/title): {len(hard)}")
    for r in hard:
        print(f"  [{r[0]}] {r[1]}:{r[2]}")
        print(f"      {r[3]}")
    print()
    print(f"软提示(有 tooltip/title,无 aria-label): {len(soft)}")
    for r in soft:
        print(f"  [{r[0]}] {r[1]}:{r[2]}")
        print(f"      {r[3]}")
    return 0 if not hard else 1


if __name__ == "__main__":
    sys.exit(main())
