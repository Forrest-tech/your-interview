#!/usr/bin/env python3
"""票123 R-6 静态核验探针 —— 「列表视图零态卡」定稿契约逐条对照。

定稿依据:docs/fix-program/source/ticket123-REMEDIATION-PLAN.md §R-6
  Changes 1 —— 列表视图零态改成看板卡自己的形态:
                inbox 图标 / 标题(既有键 tracker.empty)/ 正文(新键 tracker.emptyBody)/
                一个打开既有新建对话框的主行动,CTA 绑 tracker.dlgNew(对话框标题键),
                使 CTA 文案与对话框标题同源、不可能漂移。
  Changes 2 —— 看板空态卡的��个纯图标按钮补可访问名:
                新建 mini-fab 绑 tracker.firstApp、清除筛选图标按钮绑 common.clearFilter。
  Changes 3 —— 不新增次要导入入口(未接线就不给)。

本探针只做静态核验(模板 + i18n + 样式),不启动浏览器;
活体交互验证由主线的浏览器探针在真实环境完成。

用法: python3 docs/fix-program/evidence-123/tools/census-zero-state-r6.py
退出码: 0 = 全部满足;1 = 有未满足项
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"
TRACKER_HTML = WEB / "features/tracker/tracker.component.html"
TRACKER_SCSS = WEB / "features/tracker/tracker.component.scss"
I18N = WEB / "core/i18n/i18n.service.ts"
APP_DIALOG = WEB / "features/tracker/application-dialog.component.ts"

FAILURES: list[str] = []
NOTES: list[str] = []


def check(ok: bool, label: str, detail: str = "") -> bool:
    mark = "✅" if ok else "❌"
    print(f"  {mark} {label}" + (f" —— {detail}" if detail else ""))
    if not ok:
        FAILURES.append(f"{label}{(' —— ' + detail) if detail else ''}")
    return ok


def main() -> int:
    html = TRACKER_HTML.read_text(encoding="utf-8")
    scss = TRACKER_SCSS.read_text(encoding="utf-8")
    i18n = I18N.read_text(encoding="utf-8")
    dialog = APP_DIALOG.read_text(encoding="utf-8") if APP_DIALOG.exists() else ""

    # ---------- 定位列表视图 @empty 块 ----------
    # 列表视图的 @empty 与流程视图的 @empty 都要在,靠 filtersActive() 分支区分。
    m = re.search(r"\} @empty \{(.*?)\n\s*\}\n", html, re.S)
    if not m:
        print("❌ 未能定位列表视图的 @empty 块")
        FAILURES.append("列表 @empty 块未找到")
        return 1
    empty_block = m.group(1)
    # 只看未筛选分支(@else 之后)
    else_idx = empty_block.find("@else")
    unfiltered = empty_block[else_idx:] if else_idx >= 0 else empty_block

    print("=== R-6.1 列表视图零态卡 ===")
    check("list-empty" in empty_block,
          "列表 @empty 仍挂在 .list-empty 容器上")
    check("filtersActive()" in empty_block,
          "筛选分支保留(不属于本项范围,未动)")
    check("<mat-icon>inbox</mat-icon>" in unfiltered,
          "未筛选分支有 inbox 图标")
    check("tracker.empty" in unfiltered,
          "标题读既有键 tracker.empty(未复制字符串)")
    check("tracker.emptyBody" in unfiltered,
          "正文读新键 tracker.emptyBody")
    check("openCreate()" in unfiltered,
          "CTA 打开既有新建流程 openCreate()(未引入新流程)")
    check("tracker.dlgNew" in unfiltered,
          "CTA 绑 tracker.dlgNew —— 与对话框标题同源")
    check("tracker.noResults" not in unfiltered,
          "未筛选分支不再渲染裸文案 tracker.noResults")

    # CTA 文本 == 对话框标题:两者读同一个键 ⇒ 构造上相等。
    print("\n=== R-6.1.4 CTA 与对话框标题同源(构造相等) ===")
    check("tracker.dlgNew" in dialog,
          "application-dialog 标题读 tracker.dlgNew")
    key_def = re.search(r"'tracker\.dlgNew'\s*:\s*\{([^}]*)\}", i18n)
    check(key_def is not None, "tracker.dlgNew 键存在于 i18n")
    if key_def:
        body = key_def.group(1)
        for lang, expect in (("en", "New application"),
                             ("zh", "新建投递"),
                             ("fr", "Nouvelle candidature")):
            seg = re.search(rf"{lang}\s*:\s*'([^']*)'", body)
            check(seg is not None and seg.group(1) == expect,
                  f"tracker.dlgNew[{lang}] == 「{expect}」",
                  (seg.group(1) if seg else "缺失"))

    print("\n=== R-6.1.2 新键 tracker.emptyBody 三语逐字 ===")
    key_def = re.search(r"'tracker\.emptyBody'\s*:\s*\{([^}]*)\}", i18n)
    check(key_def is not None, "tracker.emptyBody 键存在于 i18n")
    if key_def:
        body = key_def.group(1)
        for lang, expect in (
            ("en", "Add your first application to start tracking it by stage."),
            ("zh", "添加第一个投递,开始按阶段跟踪它。"),
            ("fr", "Ajoutez votre première candidature pour commencer à la suivre par étape."),
        ):
            seg = re.search(rf"{lang}\s*:\s*'([^']*)'", body)
            check(seg is not None and seg.group(1) == expect,
                  f"tracker.emptyBody[{lang}] 逐字相符",
                  (seg.group(1) if seg else "缺失"))

    print("\n=== R-6.2 看板空态卡两个纯图标按钮的可访问名 ===")
    board_m = re.search(r"viewMode\(\) === 'board'\)\s*\{(.*?)\n\s*\}\n\s*\}\n", html, re.S)
    board = board_m.group(1) if board_m else ""
    fab_m = re.search(r"<button mat-mini-fab[^>]*openCreate\(\)[^>]*>", board, re.S)
    check(fab_m is not None, "看板空态卡新建 mini-fab 已定位")
    if fab_m:
        fab = fab_m.group(0)
        check("tracker.firstApp" in fab,
              "mini-fab 的 aria-label 读 tracker.firstApp(与它已有的 tooltip 同键)")
        check("[attr.aria-label]" in fab, "mini-fab 确有 [attr.aria-label] 绑定")
    clr_m = re.search(r"<button mat-icon-button[^>]*clearFilters\(\)[^>]*>", board, re.S)
    check(clr_m is not None, "看板空态卡清除筛选图标按钮已定位")
    if clr_m:
        clr = clr_m.group(0)
        check("common.clearFilter" in clr,
              "清除筛选按钮的 aria-label 读 common.clearFilter")
        check("[attr.aria-label]" in clr, "清除筛选按钮确有 [attr.aria-label] 绑定")

    print("\n=== R-6.3 未新增次要导入入口 ===")
    check("import" not in unfiltered.lower() or "tracker.import" not in unfiltered,
          "未筛选分支未新增导入入口(定稿 Changes 3:未接线就不给)")

    print("\n=== 样式:卡片形态成立 ===")
    check(".list-empty-card" in scss, ".list-empty-card 有专属样式")
    check("border-radius: 10px" in scss.split(".list-empty-card")[1][:400] if
          ".list-empty-card" in scss else False,
          "卡片圆角 10px(§0.3 卡片档)")
    check("var(--surface)" in scss.split(".list-empty-card")[1][:400] if
          ".list-empty-card" in scss else False,
          "卡片底色用既有令牌 --surface(未新增色值)")

    print("\n" + "=" * 60)
    if FAILURES:
        print(f"❌ 未满足 {len(FAILURES)} 项:")
        for f in FAILURES:
            print(f"   - {f}")
        return 1
    print("✅ R-6 全部静态契约项满足")
    return 0


if __name__ == "__main__":
    sys.exit(main())
