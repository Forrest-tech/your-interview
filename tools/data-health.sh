#!/usr/bin/env bash
# ============================================================================
#  your-interview 数据体检 & 恢复工具(第四十轮)
#
#  用途:Forrest 报「vuereal 数据丢失、树回到初始状态」后,在他的 Mac 上一键查:
#    1) 素材树到底存在哪(答案:PostgreSQL,不是配置文件)
#    2) 数据库里还剩什么(含被软删的行)
#    3) 有没有"孤儿录音"(录音还在,但素材已没了)
#  默认只读。恢复动作需显式 --restore 并二次确认。
# ============================================================================
set -uo pipefail

DB="${DB:-yourinterview}"
PGUSER="${PGUSER:-postgres}"
PGHOST="${PGHOST:-127.0.0.1}"
# 本机 PostgreSQL 端口(与 .env 里 ConnectionStrings__* 一致,默认 5432)
PGPORT="${PGPORT:-5432}"
# 口令:优先环境变量;未给则回退到本机开发默认 200808(与 .env 的 ConnectionStrings 一致)
PGPASSWORD="${PGPASSWORD:-200808}"

# psql 可执行文件:自动探测(Forrest 的 Mac 没把 psql 装进 PATH,
# 但安装了 "PostgreSQL 13" 这个 Mac 应用,它自带 psql)。
# 可用 PSQL_BIN=... 显式覆盖。
_find_psql() {
  if [[ -n "${PSQL_BIN:-}" ]]; then echo "$PSQL_BIN"; return; fi
  if command -v psql >/dev/null 2>&1; then command -v psql; return; fi
  # macOS "PostgreSQL N" 应用(在 /Applications 下,bin 里带 psql)
  local c
  for c in "/Applications/PostgreSQL "*/bin/psql \
           /Library/PostgreSQL/*/bin/psql \
           /Applications/Postgres.app/Contents/Versions/*/bin/psql \
           /opt/homebrew/opt/postgresql*/bin/psql \
           /usr/local/opt/postgresql*/bin/psql \
           /tmp/pgdebs/extract/usr/lib/postgresql/*/bin/psql; do
    if [[ -x "$c" ]]; then echo "$c"; return; fi
  done
  echo "psql"
}
PSQL_BIN="$(_find_psql)"

# 沙箱内 libpq 不在系统路径 → 若存在解包目录则自动补 LD_LIBRARY_PATH。
if [[ -z "${LD_LIBRARY_PATH:-}" && -d /tmp/pgdebs/extract/usr/lib/x86_64-linux-gnu ]]; then
  export LD_LIBRARY_PATH=/tmp/pgdebs/extract/usr/lib/x86_64-linux-gnu
fi

q() {  # q <SQL>  —— 只读执行
  PGPASSWORD="$PGPASSWORD" "$PSQL_BIN" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DB" -v ON_ERROR_STOP=0 -P pager=off -c "$1"
}

hr() { printf '%s\n' "────────────────────────────────────────────────────────────────────────"; }

echo
hr
echo " your-interview 数据体检报告"
echo " 目标库: $DB @ $PGHOST:$PGPORT   时间: $(date '+%Y-%m-%d %H:%M:%S %Z')"
hr

echo
echo "【1】素材树存储位置确认"
q "select 1" >/dev/null 2>&1 || {
  echo "  ❌ 连不上数据库。请确认 PostgreSQL 在跑、端口/口令正确。"
  echo "     psql 实际使用: $PSQL_BIN"
  echo "     可显式指定: PSQL_BIN='/Applications/PostgreSQL 13/bin/psql' bash tools/data-health.sh"
  echo "     改口/端口: PGPASSWORD=你的口令 PGPORT=5432 bash tools/data-health.sh"
  exit 1
}
echo "  · 素材树(文件夹/素材/正文) : PostgreSQL  assessment.practice_materials"
echo "  · 录音元数据(路径/时长)   : PostgreSQL  assessment.practice_recordings"
echo "  · 评分结果                 : PostgreSQL  assessment.practice_recording_scores"
echo "  · 音频字节本身             : 服务器磁盘(库里只存相对路径)"
echo "  · AI 语音密钥              : PostgreSQL  assessment.speech_settings"
echo "  → 结论:全部重要数据都在数据库,不在配置文件。"

echo
echo "【2】当前数据量"
q "select '素材(总)' as 项, count(*) from assessment.practice_materials
   union all select '录音(未删)', count(*) from assessment.practice_recordings where not \"IsDeleted\"
   union all select '评分记录', count(*) from assessment.practice_recording_scores;"

echo
echo "【3】★ 被软删除的素材(数据还在库里,可恢复)"
if q "select \"IsDeleted\" from assessment.practice_materials limit 1" >/dev/null 2>&1; then
  q "select \"Id\", \"Name\", \"Kind\", \"CreatedAt\" from assessment.practice_materials
     where \"IsDeleted\" order by \"CreatedAt\" desc limit 50;"
else
  echo "  (此库还没有 IsDeleted 列 —— 修复版尚未部署到这个库;先升级,再来体检)"
fi

echo
echo "【4】★ 孤儿录音(录音还在,但挂的素材已不在树里)"
q "select r.\"Id\", r.\"MaterialId\", r.\"DurationSeconds\", r.\"CreatedAt\"
   from assessment.practice_recordings r
   where not r.\"IsDeleted\"
     and not exists (select 1 from assessment.practice_materials m
                     where m.\"Id\" = r.\"MaterialId\" and not m.\"IsDeleted\")
   order by r.\"CreatedAt\" desc;"

echo
echo "【5】最近 20 个素材(含已删)—— 看还有没有 vuereal 的痕迹"
q "select \"Name\", \"Kind\", \"IsDeleted\", \"CreatedAt\" from assessment.practice_materials
   order by \"CreatedAt\" desc limit 20;"

echo
hr
echo " 只读体检结束,未做任何修改。"
echo " 恢复被软删的素材:  DB=$DB bash $0 --restore"
hr
echo

if [[ "${1:-}" == "--restore" ]]; then
  echo
  echo "⚠️  即将把所有被软删的素材恢复为可见。"
  read -r -p "    确认执行?输入 yes 继续: " ok
  [[ "$ok" == "yes" ]] || { echo "    已取消。"; exit 0; }
  q "update assessment.practice_materials set \"IsDeleted\" = false where \"IsDeleted\" = true;"
  echo "    ✅ 已恢复。请刷新页面查看。"
fi
