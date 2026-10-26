#!/usr/bin/env python3
"""票123 R-10 静态核验探针 —— 「热力图邀请行」定稿契约逐条对照。

定稿依据:docs/fix-program/source/ticket123-REMEDIATION-PLAN.md §R-10(第 253–261 行)
  Changes —— 当热力图可见窗口内的复习总数为 0(heatmapWeeks() 里过去日子 count 之和)时,
            图例下方渲染一条邀请行:一句正文(新键 ts.heatmap.empty)+
            一个文字按钮 CTA(新键 ts.heatmap.startReview),接既有 startReview();
            没有到期项时由既有 techstack.reviewNoDue 提示(继承的行为,不是新发明的)。
            窗口内有任何复习则一切不变。不新增颜色、不新增版式系统。
  Verification —— 零活动账号:邀请行在图例下可见,CTA 进入复习流程;
            记录一次复习后邀请消失,至少一格渲染 lv-1 或更高。

用法: python3 docs/fix-program/evidence-123/tools/census-heatmap-invite-r10.py
退出码: 0 = 全部满足;1 = 有未满足项
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
WEB = ROOT / "web" / "src" / "app"
TS_HTML = WEB / "features/techstack/techstack.component.html"
TS_TS = WEB / "features/techstack/techstack.component.ts"
TS_SCSS = WEB / "features/techstack/techstack.component.scss"
I18N = WEB / "core/i18n/i18n.service.ts"

FAILURES: list[str] = []


def check(ok: bool, label: str, detail: str = "") -> bool:
    print(f"  {'✅' if ok else '❌'} {label}" + (f" —— {detail}" if detail else ""))
    if not ok:
        FAILURES.append(f"{label}{(' —— ' + detail) if detail else ''}")
    return ok


def main() -> int:
    html = TS_HTML.read_text(encoding="utf-8")
    ts = TS_TS.read_text(encoding="utf-8")
    scss = TS_SCSS.read_text(encoding="utf-8")
    i18n = I18N.read_text(encoding="utf-8")

    print("=== R-10.1 邀请行的触发条件:窗口内复习总数为 0 ===")
    check("heatmapTotalCount" in ts,
          "存在 heatmapTotalCount 计算属性")
    # 求和口径:count > 0 的日子累加(未来日子 count = -1,不参与)
    m = re.search(r"readonly heatmapTotalCount\s*=\s*computed\(\(\)\s*=>\s*\{(.*?)\n\s*\}\);", ts, re.S)
    check(m is not None, "heatmapTotalCount 实现可定位")
    if m:
        body = m.group(1)
        check("heatmapWeeks()" in body, "求和来源是 heatmapWeeks()(即可见窗口)")
        check("day.count > 0" in body,
              "只累加 count > 0 的日子 —— 未来日子 count = -1 不计入")

    print("\n=== R-10.2 模板:图例下方的邀请行 ===")
    inv_m = re.search(r"@if \(heatmapTotalCount\(\) === 0\) \{(.*?)\n\s*\}\n", html, re.S)
    check(inv_m is not None, "邀请行挂在 heatmapTotalCount() === 0 条件下")
    if inv_m:
        inv = inv_m.group(1)
        check("ts.heatmap.empty" in inv, "正文读新键 ts.heatmap.empty")
        check("ts.heatmap.startReview" in inv, "CTA 读新键 ts.heatmap.startReview")
        check("startReview()" in inv, "CTA 接既有 startReview()(未引入新流程)")
        check("mat-button" in inv, "CTA 是文字按钮(mat-button)")

    # 位置:邀请行必须在图例之后
    legend_idx = html.find('class="heatmap-legend"')
    inv_idx = html.find("heatmapTotalCount() === 0")
    check(legend_idx >= 0 and inv_idx > legend_idx,
          "邀请行在图例之后(定稿:under the legend)",
          f"legend@{legend_idx} invite@{inv_idx}")

    print("\n=== R-10.3 新键三语逐字 ===")
    for key, expect in (
        ("ts.heatmap.empty",
         (("en", "No reviews yet. Finish a first review and this map starts filling in."),
          ("zh", "还没有复习记录。完成第一次复习,这张图就会开始填起来。"),
          ("fr", "Aucune révision pour le moment. Terminez une première révision et cette carte commencera à se remplir."))),
        ("ts.heatmap.startReview",
         (("en", "Start a review"),
          ("zh", "开始复习"),
          ("fr", "Commencer une révision"))),
    ):
        km = re.search(rf"'{re.escape(key)}'\s*:\s*\{{([^}}]*)\}}", i18n)
        check(km is not None, f"{key} 键存在于 i18n")
        if km:
            kbody = km.group(1)
            for lang, exp in expect:
                seg = re.search(rf"{lang}\s*:\s*'([^']*)'", kbody)
                check(seg is not None and seg.group(1) == exp,
                      f"{key}[{lang}] 逐字相符",
                      (seg.group(1) if seg else "缺失"))

    print("\n=== R-10.4 startReview 是既有流程入口,空队列走既有提示 ===")
    sr = re.search(r"\n\s*startReview\(\): void \{(.*?)\n\s*\}\n", ts, re.S)
    check(sr is not None, "startReview() 存在且是公开方法(非 private)")
    if sr:
        check("techstack.reviewNoDue" in sr.group(1),
              "空队列时由既有 techstack.reviewNoDue 提示(继承行为)")
    check("techstack.reviewNoDue" in i18n, "techstack.reviewNoDue 键存在")

    print("\n=== R-10.5 有复习时不变 / 不新增颜色与版式系统 ===")
    # 只取 .heatmap-invite 开头的规则块本身,不要连带后面的其他选择器
    invite_rules = re.findall(r"^\.heatmap-invite[^{]*\{[^}]*\}", scss, re.M)
    check(len(invite_rules) >= 2,
          "邀请行有专属样式(至少一条规则 + 子元素规则)",
          f"{len(invite_rules)} 条规则")
    invite_styles = "\n".join(invite_rules)
    check("--hairline" in invite_styles,
          "分隔线用既有令牌 --hairline(未新增色值)")
    check("--ink-2" in invite_styles,
          "正文用既有次级墨色 --ink-2(未新增色值)")
    # 确认邀请行自己的样式里没有引入新的十六进制色值
    hexes = re.findall(r"#[0-9a-fA-F]{3,8}\b", invite_styles)
    check(not hexes, "邀请行样式未新增十六进制色值", ", ".join(hexes))

    print("\n" + "=" * 60)
    if FAILURES:
        print(f"❌ 未满足 {len(FAILURES)} 项:")
        for f in FAILURES:
            print(f"   - {f}")
        return 1
    print("✅ R-10 全部静态契约项满足")
    return 0


if __name__ == "__main__":
    sys.exit(main())
