#!/usr/bin/env python3
"""合并多份 coverlet opencover 报告,按 (模块, 文件, 行列) 对 SequencePoint 取并集。

为什么不用 coverlet 自带的 --merge-with:各测试项目分别构建时,同一生产程序集的
ModuleHash 随构建变化,coverlet 的 merge-with 匹配不上模块会直接失败/丢模块
(票 28 实测:7 套件合并链丢了 3 套件的报告)。源码不变时 (文件, 行列) 是稳定身份,
按它取并集等价于"任一一次运行覆盖到即算覆盖"。

用法:
    python3 scripts/merge-coverage.py report1.opencover.xml [report2 ...]
          [--file SmartParseRules.cs]

输出:每模块一行 + 后端整体一行;--file 时另打该文件的行/分支覆盖。
"""
import argparse
import sys
import xml.etree.ElementTree as ET


def load(path):
    root = ET.parse(path).getroot()
    files = {f.get("uid"): f.get("fullPath") for f in root.iter("File")}
    modules = {}
    for mod in root.iter("Module"):
        name = mod.findtext("ModuleName")
        pts = modules.setdefault(name, {})
        for sp in mod.iter("SequencePoint"):
            key = (files.get(sp.get("fileid")), sp.get("sl"), sp.get("sc"),
                   sp.get("el"), sp.get("ec"))
            pts[key] = pts.get(key, 0) + int(sp.get("vc"))
        for bp in mod.iter("BranchPoint"):
            key = ("branch", files.get(bp.get("fileid")), bp.get("sl"),
                   bp.get("path"), bp.get("offset"))
            pts[key] = pts.get(key, 0) + int(bp.get("vc"))
    return modules


def summarize(modules, file_filter=None):
    total = visited = 0
    for name in sorted(modules):
        pts = {k: v for k, v in modules[name].items() if k[0] != "branch"}
        if file_filter:
            pts = {k: v for k, v in pts.items() if k[0] and file_filter in k[0]}
            if not pts:
                continue
        t = len(pts)
        v = sum(1 for vc in pts.values() if vc > 0)
        total += t
        visited += v
        print(f"{name}: {v}/{t} = {100 * v / t:.2f}%" if t else f"{name}: 0 points")
    if total:
        print(f"OVERALL: {visited}/{total} = {100 * visited / total:.2f}%")
    return visited, total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("reports", nargs="+")
    ap.add_argument("--file", dest="file_filter")
    args = ap.parse_args()

    merged = {}
    for report in args.reports:
        for name, pts in load(report).items():
            dest = merged.setdefault(name, {})
            for key, vc in pts.items():
                dest[key] = dest.get(key, 0) + vc

    if args.file_filter:
        print(f"== file filter: {args.file_filter} ==")
        summarize(merged, args.file_filter)
    else:
        summarize(merged)


if __name__ == "__main__":
    sys.exit(main())
