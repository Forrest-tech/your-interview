#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
# purge-all-audio-files.sh — 删除所有录音音频文件(两个 storage 目录都清)
#
# 背景(2026-09-17):LocalAudioStore 的根目录取自 Directory.GetCurrentDirectory(),
# 于是历史上产生了**两个** storage 树:
#   ① <repo>/storage/recordings                  (从仓库根启动时)
#   ② <repo>/src/Services.Assessment/storage/recordings  (从服务目录启动时)
# 只查其中一个会漏掉真实的音频文件。
#
# Forrest 要求:录音全删。
# 本脚本把两个树里所有音频文件移入回收目录(**不 rm**,可恢复),
# 并打印最终清空命令。
#
# 用法:
#   bash tools/purge-all-audio-files.sh            # 预览
#   bash tools/purge-all-audio-files.sh --apply    # 执行移动
# ─────────────────────────────────────────────────────────────────────────
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPLY=0; [ "${1:-}" = "--apply" ] && APPLY=1

TREES=(
  "$ROOT/storage/recordings"
  "$ROOT/src/Services.Assessment/storage/recordings"
)

echo "════════════════════════════════════════════════"
echo " 清空所有录音音频(两个 storage 树)"
echo "════════════════════════════════════════════════"

FILES=()
for t in "${TREES[@]}"; do
  [ -d "$t" ] || continue
  while IFS= read -r f; do FILES+=("$f"); done < <(find "$t" -type f 2>/dev/null)
done

if [ "${#FILES[@]}" -eq 0 ]; then
  echo "✅ 两个树里都没有音频文件了。"; exit 0
fi

echo "发现 ${#FILES[@]} 个音频文件:"
TOTAL=0
for f in "${FILES[@]}"; do
  sz=$(wc -c < "$f" | tr -d ' ')
  TOTAL=$((TOTAL+sz))
  printf '  · %s (%s bytes)\n' "${f#$ROOT/}" "$sz"
done
echo "合计 $((TOTAL/1024)) KB"

if [ "$APPLY" -ne 1 ]; then
  echo; echo "(预览模式。加 --apply 执行移动)"; exit 0
fi

STAMP="$(date +%Y%m%d-%H%M%S)"
TRASH="$ROOT/storage/_trash-all-recordings/$STAMP"
for f in "${FILES[@]}"; do
  rel="${f#$ROOT/}"
  dest="$TRASH/$rel"
  mkdir -p "$(dirname "$dest")"
  mv "$f" "$dest" && echo "  → $rel"
done

echo
echo "✅ 已全部移入回收目录(未真删):"
echo "   $TRASH"
echo
echo "   确认无误后自行彻底清除:"
echo "   rm -rf \"$ROOT/storage/_trash-all-recordings\""
