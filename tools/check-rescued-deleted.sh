#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
# check-rescued-deleted.sh — 确认"昨天抢救回来的 3 条录音"是否已被彻底删除
#
# 检查三条核心录音(学历 / 工签 / 技术架构)在**数据库**与**磁盘**两侧的残留:
#   de794078  RESTful services on .NET 8 / Clean Architecture
#   49c81eb8  Master of Science in Computational Science (Laurentian)
#   0669668d  authorized to work in Canada (工签)
#
# 只读脚本:不发任何 DELETE/UPDATE。安全可反复跑。
#
# 用法:
#   bash tools/check-rescued-deleted.sh
# ─────────────────────────────────────────────────────────────────────────
set -uo pipefail

DB="${DB:-yourinterview}"
PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
export PGHOST PGPORT PGUSER
# 凭据走 PGPASSWORD / PGPASSFILE 环境变量,不硬编码在脚本里

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

_find_storage() {
  local c
  for c in \
    "$HOME/.openclaw/dev/your-interview/storage/recordings" \
    "$(cd "$(dirname "$0")/.." && pwd)/storage/recordings"; do
    [ -d "$c" ] && { echo "$c"; return; }
  done
  echo "$HOME/.openclaw/dev/your-interview/storage/recordings"
}
STORAGE="${STORAGE_DIR:-$(_find_storage)}"

echo "════════════════════════════════════════════════════════════"
echo " 抢救 3 条录音 删除确认"
echo " 数据库: $DB @ $PGHOST:$PGPORT"
echo " 磁盘  : $STORAGE"
echo "════════════════════════════════════════════════════════════"

echo
echo "【1】数据库侧 —— 三条录音行是否还在?(含已软删的)"
"$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -P pager=off -c "
SELECT \"Id\", \"StoragePath\", \"IsDeleted\" AS is_deleted, \"CreatedAt\"
  FROM assessment.practice_recordings
 WHERE \"Id\"::text LIKE 'de794078%'
    OR \"Id\"::text LIKE '49c81eb8%'
    OR \"Id\"::text LIKE '0669668d%'
 ORDER BY \"CreatedAt\";" 2>&1

echo
echo "【2】数据库侧 —— 这三条的评分行是否还在?"
"$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -P pager=off -c "
SELECT \"RecordingId\", left(\"ReferenceText\", 70) AS reference_text_head
  FROM assessment.practice_recording_scores
 WHERE \"RecordingId\"::text LIKE 'de794078%'
    OR \"RecordingId\"::text LIKE '49c81eb8%'
    OR \"RecordingId\"::text LIKE '0669668d%';" 2>&1

echo
echo "【3】数据库侧 —— 本地磁盘 ____ 条录音总数(未删)"
"$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -P pager=off -c "
SELECT count(*) FILTER (WHERE NOT \"IsDeleted\") AS active,
       count(*) FILTER (WHERE \"IsDeleted\")     AS soft_deleted,
       count(*)                                  AS total
  FROM assessment.practice_recordings;" 2>&1

echo
echo "【4】磁盘侧 —— 这三条音频文件是否还在?"
FOUND=0
for id in de794078 49c81eb8 0669668d; do
  hits="$(find "$STORAGE" -type f -name "${id}*" 2>/dev/null)"
  if [ -n "$hits" ]; then
    echo "  ⚠️  仍存在: $hits"
    FOUND=$((FOUND+1))
  else
    echo "  ✅ 已删除: $id*"
  fi
done

echo
echo "【5】磁盘侧 —— storage 下现存音频文件总量"
find "$STORAGE" -type f 2>/dev/null | wc -l
echo "  (列出前 20 个)"
find "$STORAGE" -type f 2>/dev/null | head -20

echo
echo "════════════════════════════════════════════════════════════"
if [ "$FOUND" -eq 0 ]; then
  echo " 结论:三条录音的音频文件在磁盘上均已不存在。"
else
  echo " 结论:磁盘上仍有 $FOUND 条该类文件残留 —— 见上方 ⚠️ 行。"
fi
echo " 数据库侧请对照【1】的输出:0 行 = 行已删除。"
echo "════════════════════════════════════════════════════════════"
