#!/usr/bin/env bash
# 本地开发栈一键管理:PostgreSQL + RabbitMQ + 各微服务
#
#   ./dev.sh up            起全部(pg + rabbitmq + 七个微服务 + 网关)
#   ./dev.sh down          停全部
#   ./dev.sh restart <svc> 重启单个服务(identity|jobs|interviews|knowledge|assessment|analytics|gateway)
#   ./dev.sh build         预构建全部(不启动)
#   ./dev.sh status        看状态
#   ./dev.sh logs <svc>    tail 日志
#   ./dev.sh e2e [svc]     跑端到端自测
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

  # 先构建再启动 —— dotnet run --no-build 会用旧二进制。
  # 这个坑在加 EF 迁移时尤其折磨(迁移文件是新的,跑的却是旧代码)。
  if ! dotnet build "$proj" -v q --nologo > "$LOGS/$s.build.log" 2>&1; then
    echo "  ✗ $s 构建失败,详见 $LOGS/$s.build.log"; return 1
  fi

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
    gateway) echo Services.Gateway ;;
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
    for s in identity jobs interviews knowledge assessment analytics gateway; do start_svc "$s"; done
    for s in identity jobs interviews knowledge assessment analytics gateway; do wait_health "$s"; done
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
  e2e)
    bash "$ROOT/tools/e2e.sh" "${2:-all}"
    ;;
  build)
    for s in identity jobs interviews knowledge assessment analytics gateway; do
      proj="$SRC/$(svc_project "$s")"
      if dotnet build "$proj" -v q --nologo > "$LOGS/$s.build.log" 2>&1; then
        echo "  ✓ $s 构建通过"
      else
        echo "  ✗ $s 构建失败 (见 $LOGS/$s.build.log)"
      fi
    done
    ;;
  *) echo "用法: dev.sh {up|down|restart <svc>|build|status|logs <svc>|e2e [svc]|urls}"; exit 1 ;;
esac
