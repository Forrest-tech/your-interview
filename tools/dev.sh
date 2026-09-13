#!/usr/bin/env bash
# 本地开发栈一键管理:PostgreSQL + RabbitMQ + 各微服务
#
#   ./dev.sh up            起全部(pg + rabbitmq + identity + jobs)
#   ./dev.sh down          停全部
#   ./dev.sh restart <svc> 重启单个服务(identity|jobs|interviews|knowledge|assessment|analytics)
#   ./dev.sh status        看状态
#   ./dev.sh logs <svc>    tail 日志
#   ./dev.sh urls          打印所有地址
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"
RUN="$ROOT/.run"
LOGS="$ROOT/.logs"
mkdir -p "$RUN" "$LOGS"

export DOTNET_ROOT="/home/node/.dotnet"
export PATH="$DOTNET_ROOT:$HOME/.dotnet/tools:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_NOLOGO=1
export ASPNETCORE_ENVIRONMENT=Development

# 服务名 → 端口
declare -A PORTS=(
  [identity]=5262
  [jobs]=5263
  [interviews]=5264
  [knowledge]=5265
  [assessment]=5266
  [analytics]=5267
  [gateway]=5200
)

svc_pidfile() { echo "$RUN/$1.pid"; }
svc_log()     { echo "$LOGS/$1.log"; }

start_svc() {
  local s="$1" port="${PORTS[$1]}"
  local proj="$SRC/$(svc_project "$1")"
  [ -d "$proj" ] || { echo "  ✗ $s 项目不存在"; return 1; }
  if is_running "$s"; then echo "  · $s 已在运行"; return 0; fi
  ( cd "$proj" && setsid nohup dotnet run --no-build --urls "http://127.0.0.1:$port" \
      > "$(svc_log "$s")" 2>&1 < /dev/null & echo $! > "$(svc_pidfile "$s")" )
  echo "  ↻ $s 启动中 (port $port) …"
}

svc_project() {
  case "$1" in
    identity) echo Services.Identity ;;
    jobs) echo Services.Jobs ;;
    interviews) echo Services.Interviews ;;
    knowledge) echo Services.Knowledge ;;
    assessment) echo Services.Assessment ;;
    analytics) echo Services.Analytics ;;
    gateway) echo Gateway ;;
  esac
}

is_running() {
  local pf; pf="$(svc_pidfile "$1")"
  [ -f "$pf" ] && kill -0 "$(cat "$pf")" 2>/dev/null
}

stop_svc() {
  local s="$1" pf; pf="$(svc_pidfile "$s")"
  if [ -f "$pf" ]; then kill "$(cat "$pf")" 2>/dev/null; rm -f "$pf"; fi
  pkill -f "YourInterview.Services.$(echo "$s" | sed 's/.*/\u&/')" 2>/dev/null
  pkill -f "Services.$(echo "$s" | sed 's/.*/\u&/')" 2>/dev/null
  echo "  ✗ $s 已停"
}

wait_health() {
  local s="$1" port="${PORTS[$1]}" i
  for i in $(seq 1 60); do
    if curl -sf -o /dev/null "http://127.0.0.1:$port/health/live" 2>/dev/null; then
      echo "  ✓ $s 就绪 :$port"; return 0
    fi
    sleep 2
  done
  echo "  ⚠ $s 健康检查超时,看日志: $ROOT/tools/dev.sh logs $s"; return 1
}

case "${1:-status}" in
  up)
    echo "▸ 基础设施"
    node "$ROOT/tools/db/db.js" start >/dev/null 2>&1 && echo "  ✓ PostgreSQL :5433" || echo "  ⚠ PG 启动异常"
    "$ROOT/tools/rabbit/rabbit.sh" start >/dev/null 2>&1 && echo "  ✓ RabbitMQ :5672 / :15672" || echo "  ⚠ RabbitMQ 启动异常"
    echo "▸ 微服务"
    for s in identity jobs; do start_svc "$s"; done
    for s in identity jobs; do wait_health "$s"; done
    echo "▸ 全部就绪"
    "$0" urls
    ;;
  down)
    for s in jobs identity interviews knowledge assessment analytics gateway; do stop_svc "$s"; done
    "$ROOT/tools/rabbit/rabbit.sh" stop >/dev/null 2>&1 || true
    node "$ROOT/tools/db/db.js" stop >/dev/null 2>&1 || true
    echo "  ✓ 全部停止(数据保留)"
    ;;
  restart)
    s="${2:?用法: dev.sh restart <service>}"
    stop_svc "$s"; sleep 2; start_svc "$s"; wait_health "$s"
    ;;
  status)
    node "$ROOT/tools/db/db.js" status 2>/dev/null || echo "[db] stopped"
    "$ROOT/tools/rabbit/rabbit.sh" status 2>/dev/null || echo "[rabbit] stopped"
    for s in "${!PORTS[@]}"; do
      if is_running "$s"; then echo "  ✓ $s (port ${PORTS[$s]})"; fi
    done | sort
    ;;
  logs)
    s="${2:?用法: dev.sh logs <service>}"; tail -f "$(svc_log "$s")" ;;
  urls)
    cat <<EOF

  前端 SPA        http://localhost:4200
  API 网关        http://localhost:5200
  ─────────────────────────────────────
  Identity        http://localhost:5262/swagger
  Jobs (Tracker)  http://localhost:5263/swagger
  Interviews      http://localhost:5264/swagger
  Knowledge       http://localhost:5265/swagger
  Assessment      http://localhost:5266/swagger
  Analytics       http://localhost:5267/swagger
  ─────────────────────────────────────
  RabbitMQ 管理台 http://localhost:15672  (guest/guest)
  PostgreSQL      127.0.0.1:5433  yourinterview
EOF
    ;;
  *) echo "用法: dev.sh {up|down|restart <svc>|status|logs <svc>|urls}"; exit 1 ;;
esac
