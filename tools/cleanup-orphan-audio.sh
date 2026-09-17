#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
# cleanup-orphan-audio.sh — 清除 storage 中"库里没有任何行认领"的孤儿音频
#
# 背景(2026-09-17):录音目录里剩 2 个 mp3 文件,在 practice_recordings /
# practice_recording_scores 里都查不到对应行 —— 是历史上"删了行但没删文件"
# 的残留(旧代码的硬删只删库、或删除中断)。Forrest 要求清除。
#
# 安全设计:
#   · 默认 dry-run 只列出;--apply 才动。
#   · **不真删** —— 移动到 storage/_orphans-trash/<时间戳>/ 下(可恢复),
#     而不是 rm。确认无碍后由用户自行清空该目录。
#   · 只处理"文件名(去扩展名)在库中找不到"的文件,DB 拿不到就整体拒绝运行。
#
# 用法:
#   bash tools/cleanup-orphan-audio.sh            # 预览
#   bash tools/cleanup-orphan-audio.sh --apply    # 移到回收目录
# ─────────────────────────────────────────────────────────────────────────
set -uo pipefail

DB="${DB:-yourinterview}"
PGHOST="${PGHOST:-127.0.0.1}"; PGPORT="${PGPORT:-5432}"; PGUSER="${PGUSER:-postgres}"
export PGHOST PGPORT PGUSER
# 凭据走 PGPASSWORD / PGPASSFILE 环境变量,不硬编码在脚本里

APPLY=0; [ "${1:-}" = "--apply" ] && APPLY=1

_find_psql() {
  [ -n "${PSQL_BIN:-}" ] && [ -x "$PSQL_BIN" ] && { echo "$PSQL_BIN"; return; }
  command -v psql >/dev/null 2>&1 && { command -v psql; return; }
  local c
  for c in /Applications/PostgreSQL*/bin/psql /Library/PostgreSQL/*/bin/psql \
           /opt/homebrew/opt/postgresql*/bin/psql /usr/local/opt/postgresql*/bin/psql; do
    [ -x "$c" ] && { echo "$c"; return; }
  done
  echo ""
}
PSQL="$(_find_psql)"
[ -z "$PSQL" ] && { echo "❌ 找不到 psql,请指定 PSQL_BIN=/path/to/psql"; exit 1; }

STORAGE="${STORAGE_DIR:-$HOME/.openclaw/dev/your-interview/storage/recordings}"
[ -d "$STORAGE" ] || { echo "❌ 存储目录不存在: $STORAGE"; exit 1; }

echo "════════════════════════════════════════════════"
echo " 孤儿音频清理   库=$DB  目录=$STORAGE"
echo "════════════════════════════════════════════════"

# 库里所有已知录音 id(小写、去连字符),用于比对文件名
KNOWN="$(mktemp)"
trap 'rm -f "$KNOWN"' EXIT
"$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -tAc \
  'SELECT lower(replace("Id"::text,'"'"'-'"'"','"'"''"'"')) FROM assessment.practice_recordings;' \
  > "$KNOWN" 2>/dev/null
if [ ! -s "$KNOWN" ] && [ "$(wc -l < "$KNOWN")" -eq 0 ]; then
  rc=$?; fi
# 守卫:查库失败(非"零行")就不动手
if ! "$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -tAc 'SELECT 1;' >/dev/null 2>&1; then
  echo "❌ 无法连接数据库,拒绝继续(避免误判孤儿)。"; exit 1
fi
echo "库中录音行数: $(grep -c . "$KNOWN")"

ORPHANS=()
while IFS= read -r f; do
  base="$(basename "$f")"; stem="${base%.*}"
  if ! grep -qix "$stem" "$KNOWN"; then ORPHANS+=("$f"); fi
done < <(find "$STORAGE" -type f ! -name '*.tmp' 2>/dev/null)

if [ "${#ORPHANS[@]}" -eq 0 ]; then
  echo "✅ 没有孤儿音频,无需清理。"; exit 0
fi

echo
echo "发现 ${#ORPHANS[@]} 个孤儿文件(库中无对应录音行):"
for f in "${ORPHANS[@]}"; do printf '  · %s (%s bytes)\n' "$f" "$(wc -c < "$f" | tr -d ' ')"; done

if [ "$APPLY" -ne 1 ]; then
  echo; echo "(预览模式。加 --apply 执行移动)"; exit 0
fi

TRASH="$STORAGE/_orphans-trash/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$TRASH"
for f in "${ORPHANS[@]}"; do
  rel="${f#$STORAGE/}"; mkdir -p "$TRASH/$(dirname "$rel")"
  mv "$f" "$TRASH/$rel" && echo "  → 已移入回收: $rel"
done
echo
echo "✅ 完成。文件已移至(未真删): $TRASH"
echo "   确认无误后自行清空: rm -rf \"$TRASH\""
