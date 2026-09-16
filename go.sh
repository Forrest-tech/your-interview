#!/usr/bin/env bash
# =============================================================================
#  practice — 口语练习一键起(Forrest 专用最简版)
#
#  一条命令搞定:数据库 + 登录 + 练习后端 + 网页
#
#    cd /Users/jadenfly/.openclaw/dev/your-interview
#    bash go.sh
#
#  然后浏览器打开 http://127.0.0.1:4200/practice
#
#  停止:  bash go.sh stop
#  看状态:bash go.sh status
#
#  ── 只起口语练习真正需要的 4 样东西 ────────────────────────────
#    1. PostgreSQL  :5432   存素材树 / 录音记录 / 评分(本机已装,直接复用)
#    2. Identity    :5262   登录发令牌(练习后端要验令牌)
#    3. Assessment  :5266   练习后端本体(评分 / TTS / 素材 / 录音)
#    4. Gateway     :5200   前端所有请求的统一入口
#    5. 前端        :4200   网页
#  其余服务(jobs / interviews / knowledge / analytics)与本页无关,一概不起。
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

LOG="$ROOT/.logs"; RUN="$ROOT/.run"
mkdir -p "$LOG" "$RUN"
WEB_LOG="$LOG/web.log"; WEB_PID="$RUN/web.pid"

# ---------- 载入 .env(密钥只在本机,不入库) ----------
if [ -f "$ROOT/.env" ]; then
  set -a; . "$ROOT/.env"; set +a
  echo "  ✓ 已载入 .env"

  # 🔑 .env 的键名是 JWT_SIGNING_KEY,服务读的配置路径却是 Jwt:SigningKey。
  #    只 source 不映射的话,服务拿不到真值,会回落到 appsettings.json 里的占位符
  #    REPLACE_WITH_SIGNING_KEY_AT_LEAST_32_CHARS。本地全直跑时"两边都是占位符"
  #    还能凑合登录,但只要有一边(例如容器里的 identity)用了真 key,验签就会 401。
  #    所以这里统一映射,让容器内外的签名密钥永远一致。
  if [ -n "${JWT_SIGNING_KEY:-}" ]; then
    export Jwt__SigningKey="$JWT_SIGNING_KEY"
  else
    echo "  ⚠ .env 里没有 JWT_SIGNING_KEY —— 容器与宿主机可能用不同签名密钥,登录会 401"
  fi
else
  echo "  ⚠ 没找到 .env —— 会登录成功但接口全部 401,请先: cp .env.example .env 并填真值"
fi

web_running() { [ -f "$WEB_PID" ] && kill -0 "$(cat "$WEB_PID")" 2>/dev/null; }

start_web() {
  if web_running; then echo "  ✓ 网页已在运行"; return 0; fi
  if [ ! -d "$ROOT/web/node_modules" ]; then
    echo "  · 首次运行:安装网页依赖(约 1-3 分钟)…"
    ( cd "$ROOT/web" && npm install ) || { echo "  ✗ npm install 失败"; return 1; }
  fi
  # ⚠️ setsid 是 Linux 专有,macOS 没有 → 能力探测后回退 nohup
  local LAUNCH=""
  command -v setsid >/dev/null 2>&1 && LAUNCH="setsid "
  ( cd "$ROOT/web" && ${LAUNCH}nohup npx ng serve \
      --proxy-config proxy.conf.json --host 127.0.0.1 --port 4200 \
      > "$WEB_LOG" 2>&1 < /dev/null & echo $! > "$WEB_PID" )
  echo "  ↻ 网页编译中(首次 20-40 秒)…"
  local i
  for i in $(seq 1 60); do
    curl -sf -o /dev/null "http://127.0.0.1:4200" 2>/dev/null && { echo "  ✓ 网页就绪"; return 0; }
    web_running || { echo "  ✗ 网页进程退出,最后 15 行:"; tail -15 "$WEB_LOG" | sed 's/^/      /'; return 1; }
    sleep 2
  done
  echo "  ⚠ 网页 2 分钟未就绪,看 tail -f .logs/web.log"
}

stop_web() {
  [ -f "$WEB_PID" ] && { kill "$(cat "$WEB_PID")" 2>/dev/null; rm -f "$WEB_PID"; }
  pkill -f "ng serve" 2>/dev/null
  echo "  ✓ 网页已停"
}

case "${1:-start}" in
  start)
    echo "▸ 数据库"
    # 2026-09-15 决定:本机已装 PostgreSQL(5432)且有图形客户端,直接用它。
    # 不再启动 embedded-postgres(5433) —— 那套是"零依赖开发"的备胎,
    # 现在两套并存只会白占内存(老 Intel Mac 尤其明显),还容易查错库。
    #
    # 服务的连接串来自 .env(ConnectionStrings__*,默认 host:5432),
    # 所以这里只需要**确认本机 PG 可达**,不需要自己去起数据库。
    # 探测方式:用 node 的 pg 驱动真连一次(不自造 psql 假设)。
    PG_PROBE="$(node "$ROOT/tools/db/db.js" psql "SELECT 1" 2>&1 | tail -3)"
    if echo "$PG_PROBE" | grep -q "SQL 失败"; then
      echo "  ✗ 连不上 .env 里配置的 PostgreSQL(.env 里 Host/Port 指向的库)"
      echo "    探测输出: $PG_PROBE"
      echo "    → 确认本机 PostgreSQL 已启动,或修正 .env 里的连接串"
      exit 1
    fi
    PG_TARGET="$(grep -o 'Port=[0-9]*' "$ROOT/.env" 2>/dev/null | head -1 | cut -d= -f2)"
    echo "  ✓ PostgreSQL 可达 (${PG_TARGET:-5432},来自 .env)"

    echo "▸ 后端(登录 + 练习 + 网关)"
    bash "$ROOT/tools/dev.sh" practice

    echo "▸ 网页"
    start_web

    echo
    echo "  ┌──────────────────────────────────────────────┐"
    echo "  │  打开:  http://127.0.0.1:4200/practice        │"
    echo "  │  停止:  bash go.sh stop                       │"
    echo "  └──────────────────────────────────────────────┘"
    echo
    echo "  出问题看日志:"
    echo "    tail -f .logs/identity.log     登录服务"
    echo "    tail -f .logs/assessment.log   练习后端"
    echo "    tail -f .logs/gateway.log      网关"
    echo "    tail -f .logs/web.log          网页"
    ;;
  stop)
    stop_web
    bash "$ROOT/tools/dev.sh" down
    ;;
  status)
    bash "$ROOT/tools/dev.sh" status
    web_running && echo "  ✓ 网页 (pid $(cat "$WEB_PID"))" || echo "  · 网页未运行"
    ;;
  *) echo "用法: go.sh {start|stop|status}"; exit 1 ;;
esac
