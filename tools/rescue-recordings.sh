#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────
# rescue-recordings.sh — 抢救"母素材被误删、但音频文件还在磁盘"的录音
#
# 背景(2026-09-17 第四十七轮):
#   前端旧版本的硬编码种子曾整树覆盖,把真实素材(含 vuereal 那批)硬删;
#   这些素材下的录音元数据与音频文件仍在,但 MaterialId 指向已不存在的素材
#   → 成为"孤儿录音",界面按素材查录音查不到 → 用户以为录音全丢了。
#
# 本脚本(幂等,可重复跑):
#   1. 在素材树根建「录音归档」文件夹;
#   2. 为每条"音频文件仍存在"的孤儿录音建一个素材节点
#      (正文 = 评分时的参考文本,恢复上下文);
#   3. 把这些录音的 MaterialId 改指到新节点。
#
# 安全:
#   · 只处理音频文件在磁盘上确实存在的录音(不存在的跳过,绝不造假)。
#   · 默认 dry-run;加 --apply 才写库。全程在一个事务里,失败自动回滚。
#   · 不删除任何数据;已挂到有效素材下的录音不动。
#
# 用法:
#   DB=yourinterview bash tools/rescue-recordings.sh            # 预览
#   DB=yourinterview bash tools/rescue-recordings.sh --apply    # 执行
#
# 依赖:psql(自动探测)、PGPASSWORD / PGPORT / PGHOST / STORAGE_DIR
# ─────────────────────────────────────────────────────────────────────────
set -uo pipefail

DB="${DB:-yourinterview}"
PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
PGPASSWORD="${PGPASSWORD:-200808}"
export PGPASSWORD PGHOST PGPORT PGUSER

APPLY=0
[ "${1:-}" = "--apply" ] && APPLY=1

# ---- 定位 psql ----
_find_psql() {
  if [ -n "${PSQL_BIN:-}" ] && [ -x "$PSQL_BIN" ]; then echo "$PSQL_BIN"; return; fi
  if command -v psql >/dev/null 2>&1; then command -v psql; return; fi
  local c
  for c in \
    /Applications/PostgreSQL*/bin/psql \
    /Library/PostgreSQL/*/bin/psql \
    /opt/homebrew/opt/postgresql*/bin/psql \
    /usr/local/opt/postgresql*/bin/psql \
    /tmp/pgdebs/extract/usr/lib/postgresql/*/bin/psql; do
    [ -x "$c" ] && { echo "$c"; return; }
  done
  echo ""
}
PSQL="$(_find_psql)"
if [ -z "$PSQL" ]; then
  echo "❌ 找不到 psql。请显式指定:PSQL_BIN=/path/to/psql bash $0"; exit 1
fi

# ---- 存储根目录(音频字节所在;库里 StoragePath 相对它)----
_find_storage() {
  if [ -n "${STORAGE_DIR:-}" ] && [ -d "$STORAGE_DIR" ]; then echo "$STORAGE_DIR"; return; fi
  local c
  for c in \
    "$HOME/.openclaw/dev/your-interview/storage/recordings" \
    "$HOME/dev/your-interview/storage/recordings" \
    "$PWD/storage/recordings" \
    "$PWD/.storage/recordings"; do
    [ -d "$c" ] && { echo "$c"; return; }
  done
  echo ""
}
STORAGE="$(_find_storage)"
echo "────────────────────────────────────────────────────────────────────────"
echo " 录音抢救  |  库: $DB @ $PGHOST:$PGPORT"
echo " psql: $PSQL"
echo " 存储根: ${STORAGE:-<未找到,将无法核对音频>}"
echo " 模式: $([ $APPLY -eq 1 ] && echo '★ APPLY 真正写库' || echo 'DRY-RUN 仅预览')"
echo "────────────────────────────────────────────────────────────────────────"

run_sql() { "$PSQL" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1 -t -A -F '|' "$@"; }

# 由录音 id 确定性派生一个合法 UUID(用于幂等地建素材节点)
_derive_uuid() {
  python3 -c "import uuid,hashlib,sys; h=hashlib.sha1(('recording-node:'+sys.argv[1]).encode()).hexdigest(); print(uuid.UUID(h[:32]))" "$1"
}

# ---- 1) 找出所有"孤儿录音"(MaterialId 不在 practice_materials 里) ----
echo
echo "【1】扫描孤儿录音(母素材已不存在)…"
ORPHANS="$(run_sql -c "
select r.\"Id\", r.\"MaterialId\", r.\"StoragePath\", coalesce(r.\"DurationSeconds\",0)::text,
       r.\"UserId\", coalesce(s.\"ReferenceText\",'')
from assessment.practice_recordings r
left join assessment.practice_recording_scores s on s.\"RecordingId\" = r.\"Id\"
where r.\"IsDeleted\" = false
  and not exists (select 1 from assessment.practice_materials m where m.\"Id\" = r.\"MaterialId\")
order by r.\"CreatedAt\";")"

if [ -z "$ORPHANS" ]; then
  echo "  ✓ 没有孤儿录音,无需抢救。"
  exit 0
fi

RESCUABLE=""
SKIPPED=0
while IFS='|' read -r rid mid spath dur uid reftext; do
  [ -z "$rid" ] && continue
  if [ -n "$STORAGE" ] && [ -f "$STORAGE/$spath" ]; then
    RESCUABLE="${RESCUABLE}${rid}|${uid}|${dur}|${reftext}"$'\n'
  else
    SKIPPED=$((SKIPPED+1))
    echo "  ✗ 跳过 $rid —— 音频不在磁盘 ($spath)"
  fi
done <<< "$ORPHANS"

TOTAL=$(printf '%s' "$RESCUABLE" | grep -c . || true)
echo "  → 可抢救: $TOTAL 条;  音频已丢失无法抢救: $SKIPPED 条"
if [ "$TOTAL" -eq 0 ]; then
  echo "  (没有音频文件在磁盘上,不新建任何节点。)"
  exit 0
fi

echo
echo "【2】可抢救明细:"
printf '%s' "$RESCUABLE" | while IFS='|' read -r rid uid dur reftext; do
  [ -z "$rid" ] && continue
  echo "  · $rid  ${dur}s  原文: ${reftext:0:70}"
done

# ---- 3) 生成 SQL(全部在一个事务)----
ARCHIVE_ID="a0000000-0000-4000-8000-000000000001"
ARCHIVE_NAME="录音归档(2026-09-16)"

# 每个用户的归档文件夹 id 固定;若已存在则复用
SQL="begin;"$'\n'

IDX=0
# 收集涉及的 UserId(去重)
UIDS="$(printf '%s' "$RESCUABLE" | cut -d'|' -f2 | sort -u | grep -c . || true)"

printf '%s' "$RESCUABLE" | while IFS='|' read -r rid uid dur reftext; do
  [ -z "$rid" ] && continue
  IDX=$((IDX+1))
  REF_ESC="$(printf '%s' "$reftext" | sed "s/'/''/g")"
  # 确定性派生节点 id:sha1('recording-node:<rid>') → 合法 UUID(幂等,可重复跑)
  NODE_ID="$(_derive_uuid "$rid")"
  NAME="录音 ${IDX} · ${dur}s"
  cat <<SQLEOF
-- 归档文件夹(每个用户一个,固定 id → 幂等)
insert into assessment.practice_materials
  ("Id","UserId","ParentId","Kind","Name","Content","SortOrder","IsExpanded","IsDeleted","CreatedAt","UpdatedAt")
select '$ARCHIVE_ID', '$uid', null, 'Folder', '$ARCHIVE_NAME', null, 9999, true, false, now(), now()
where not exists (
  select 1 from assessment.practice_materials where "Id" = '$ARCHIVE_ID' and "UserId" = '$uid');

-- 录音对应的素材节点(id 由录音 id 派生 → 幂等)
insert into assessment.practice_materials
  ("Id","UserId","ParentId","Kind","Name","Content","SortOrder","IsExpanded","IsDeleted","CreatedAt","UpdatedAt")
select '$NODE_ID', '$uid', '$ARCHIVE_ID', 'File', '$NAME', '$REF_ESC', $IDX, true, false, now(), now()
where not exists (select 1 from assessment.practice_materials where "Id" = '$NODE_ID');

-- 把录音挂到新节点
update assessment.practice_recordings set "MaterialId" = '$NODE_ID' where "Id" = '$rid';
SQLEOF
done > /tmp/rescue-body.sql

SQL="begin;"$'\n'"$(cat /tmp/rescue-body.sql)"$'\n'"commit;"

echo
echo "【3】将执行的 SQL:"
echo "────────────────────────────────────────────────────────"
printf '%s\n' "$SQL"
echo "────────────────────────────────────────────────────────"

if [ "$APPLY" -eq 0 ]; then
  echo
  echo "DRY-RUN 结束。确认无误后执行:"
  echo "  DB=$DB bash $0 --apply"
  exit 0
fi

echo
echo "【4】写库…"
printf '%s\n' "$SQL" | "$PSQL" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=1
RC=$?
if [ $RC -eq 0 ]; then
  echo "  ✅ 完成。刷新界面 → 「录音归档(2026-09-16)」下即可看到抢救回来的录音。"
else
  echo "  ❌ 失败(事务已回滚,未改动数据)。rc=$RC"
fi
exit $RC
