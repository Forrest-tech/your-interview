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
echo "【6】★ 深层取证:种子覆盖痕迹 vs 真实数据"
q "select count(*) as 素材总数,
          count(*) filter (where \"IsDeleted\") as 软删数,
          min(\"CreatedAt\") as 最早创建,
          max(\"CreatedAt\") as 最晚创建
   from assessment.practice_materials;"

echo "  → 若上面众数创建时间高度集中在同一秒,通常意味着"某次整树覆盖":
     那一刻写入的就是当时前端持有的树(旧版本可能是硬编码种子)。"
echo

echo "     同一秒写入的素材个数分布(>1 秒即真实多次操作):"
q "select date_trunc('second', \"CreatedAt\") as 秒, count(*) as 条数
   from assessment.practice_materials
   group by 1 order by 2 desc limit 10;"

echo
echo "【7】★ 孤儿录音的"母素材"是否曾存在(任何状态)"
q "select r.\"MaterialId\",
          (select count(*) from assessment.practice_materials m where m.\"Id\" = r.\"MaterialId\") as 素材仍存在,
          min(r.\"CreatedAt\") as 该素材下最早录音
   from assessment.practice_recordings r
   where not r.\"IsDeleted\"
   group by r.\"MaterialId\"
   order by 2 desc, 3 asc;"
echo "  → 全部为 0 = 这些录音的母素材**已被真删**(旧版硬删路径),
     只能在磁盘上找回音频,树里已无对应节点。"
echo

echo "【8】★ 搜索真实数据痕迹(vuereal / vue / 等)"
q "select \"Id\", \"Name\", \"Kind\", \"IsDeleted\", \"CreatedAt\"
   from assessment.practice_materials
   where \"Name\" ilike '%vue%' or \"Name\" ilike '%real%'
      or \"Content\" ilike '%vue%' or \"Content\" ilike '%real%';"

echo
echo "【9】★ 音频文件实体是否还在磁盘(录音乐观数据物理载体)"
q "select r.\"Id\", r.\"StoragePath\", r.\"CreatedAt\"
   from assessment.practice_recordings r
   where not r.\"IsDeleted\" order by r.\"CreatedAt\" desc limit 20;"
echo "  → 拿到 StoragePath 后,可在项目里核对文件是否存在:
     ls -la <PRACTICE_STORAGE_DIR>/<StoragePath>"

echo
hr
echo " 只读体检结束,未做任何修改。"
echo " 恢复被软删的素材:  DB=$DB bash $0 --restore"
echo " 找回磁盘上的音频实体: DB=$DB STORAGE_DIR=<路径> bash $0 --audio"
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

if [[ "${1:-}" == "--audio" ]]; then
  # 列出磁盘上的音频实体,并与库内录音路径对照。
  # 用于“素材被真删、但录音 mp3 还在磁盘”的抢修场景。
  SRV="${STORAGE_DIR:-}"
  if [[ -z "$SRV" ]]; then
    for c in "$PWD/storage" "$PWD/.storage" "$HOME/.your-interview/storage" \
             "/tmp/yi-verify-storage"; do
      [[ -d "$c" ]] && { SRV="$c"; break; }
    done
  fi
  echo
  if [[ -z "$SRV" || ! -d "$SRV" ]]; then
    echo "❌ 未找到音频目录。请显式指定: STORAGE_DIR=<路径> bash $0 --audio"
    echo "   (在 .env 中搜 PRACTICE_STORAGE_DIR / Storage 可找到真实路径)"
    exit 0
  fi
  echo "音频目录: $SRV"
  echo "磁盘音频文件数: $(find "$SRV" -type f \( -name '*.mp3' -o -name '*.wav' -o -name '*.webm' \) 2>/dev/null | wc -l | tr -d ' ')"
  echo "--- 最近 20 个音频文件 ---"
  find "$SRV" -type f \( -name '*.mp3' -o -name '*.wav' -o -name '*.webm' \) -print0 2>/dev/null \
    | xargs -0 ls -lt 2>/dev/null | head -20
  echo
  echo "→ 即使素材行被真删,这些音频文件仍在,可人工重挂到一个素材下。"
fi
