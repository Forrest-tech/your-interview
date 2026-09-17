#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
# audit-all-recording-traces.sh — 彻底核查:录音相关的所有痕迹是否已清空
#
# 覆盖三层:
#   A. 数据库:录音行 / 评分行 / TTS 缓存 / mock 会话
#   B. 磁盘:两个已知 storage 树 + 全仓库任意 *.webm/*.mp3/*.opus/*.wav
#   C. 回收目录:是否有未清空的 _trash / _orphans-trash
#
# 只读脚本,不改任何数据。可反复跑。
#
# 用法:
#   PGPASSWORD=xxx bash tools/audit-all-recording-traces.sh
# ─────────────────────────────────────────────────────────────────────────
set -uo pipefail

DB="${DB:-yourinterview}"
PGHOST="${PGHOST:-127.0.0.1}"; PGPORT="${PGPORT:-5432}"; PGUSER="${PGUSER:-postgres}"
export PGHOST PGPORT PGUSER
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

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

echo "════════════════════════════════════════════════════"
echo " 录音痕迹全面核查"
echo " 仓库: $ROOT"
echo "════════════════════════════════════════════════════"

echo
echo "【A】数据库"
if [ -z "$PSQL" ]; then
  echo "  ⚠️  找不到 psql,跳过数据库检查(可设 PSQL_BIN)"
elif ! "$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -tAc 'SELECT 1;' >/dev/null 2>&1; then
  echo "  ⚠️  连不上库 $DB(检查 PGPASSWORD)。跳过。"
else
  for tbl in practice_recordings practice_recording_scores; do
    n="$("$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -tAc \
        "SELECT count(*) FROM assessment.$tbl;" 2>/dev/null)"
    printf '  %-32s %s 行\n' "$tbl" "${n:-ERR}"
  done
  # TTS 缓存表(若存在)
  n="$("$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -tAc \
      "SELECT count(*) FROM assessment.practice_tts_cache;" 2>/dev/null || echo 'n/a')"
  printf '  %-32s %s 行\n' 'practice_tts_cache' "$n"
fi

echo
echo "【B】磁盘 —— 两个已知 storage 树"
for t in "$ROOT/storage/recordings" "$ROOT/src/Services.Assessment/storage/recordings"; do
  if [ -d "$t" ]; then
    c="$(find "$t" -type f 2>/dev/null | wc -l | tr -d ' ')"
    printf '  %-52s %s 个文件\n' "${t#$ROOT/}" "$c"
  else
    printf '  %-52s (目录不存在)\n' "${t#$ROOT/}"
  fi
done

echo
echo "【B2】磁盘 —— 全仓库任意音频文件(*.webm/*.mp3/*.opus/*.wav/*.m4a)"
HITS="$(find "$ROOT" \( -name node_modules -o -name .git \) -prune -o \
        -type f \( -name '*.webm' -o -name '*.mp3' -o -name '*.opus' -o -name '*.wav' -o -name '*.m4a' \) -print 2>/dev/null)"
if [ -z "$HITS" ]; then
  echo "  ✅ 无"
else
  echo "$HITS" | sed "s|^$ROOT/|  · |"
fi

echo
echo "【C】回收目录(未清空则还会占盘)"
for d in "$ROOT/storage/_trash-all-recordings" "$ROOT/storage/recordings/_orphans-trash"; do
  if [ -d "$d" ]; then
    c="$(find "$d" -type f 2>/dev/null | wc -l | tr -d ' ')"
    printf '  %-52s %s 个文件  ⚠️ 未清空\n' "${d#$ROOT/}" "$c"
  else
    printf '  %-52s (不存在)\n' "${d#$ROOT/}"
  fi
done

echo
echo "════════════════════════════════════════════════════"
echo " 全部为 0 / 无 ✅ 即表示录音痕迹已彻底清除。"
echo "════════════════════════════════════════════════════"
