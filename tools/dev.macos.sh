#!/usr/bin/env bash
# Your Interview — 本地开发栈管理(macOS / Linux 通用)
#
# 基础设施(PostgreSQL + RabbitMQ)跑在 Docker 里,微服务在宿主机用 dotnet run 跑。
#
#   ./dev.sh up            起基础设施 + 七个微服务 + 网关
#   ./dev.sh infra         只起 Docker 基础设施(PG + RabbitMQ)
#   ./dev.sh down          停全部服务(基础设施容器保留)
#   ./dev.sh down-all      停全部,并停掉 Docker 基础设施
#   ./dev.sh restart <svc> 重启单个服务
#   ./dev.sh build         预构建全部(不启动)
#   ./dev.sh status        看状态
#   ./dev.sh logs <svc>    查看日志
#   ./dev.sh e2e [svc]     跑端到端自测
#   ./dev.sh urls          打印所有地址
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"
RUN="$ROOT/.run"
LOGS="$ROOT/.logs"
mkdir -p "$RUN" "$LOGS"

export DOTNET_NOLOGO=1
export ASPNETCORE_ENVIRONMENT=Development

# 有 ICU 的系统别禁用全球化;容器里没有 ICU 时才关(否则中文/排序会崩)。
if [ "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" = "" ]; then
  if ! ldconfig -p 2>/dev/null | grep -q libicu; then
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
  fi
fi

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
SVC_ORDER=(identity jobs interviews knowledge assessment analytics gateway)

svc_pidfile() { echo "$RUN/$1.pid"; }
svc_log()     { echo "$LOGS/$1.log"; }

svc_project() {
  case "$1" in
    identity)   echo Services.Identity ;;
    jobs)       echo Services.Jobs ;;
    interviews) echo Services.Interviews ;;
    knowledge)  echo Services.Knowledge ;;
    assessment) echo Services.Assessment ;;
    analytics)  echo Services.Analytics ;;
    gateway)    echo Services.Gateway ;;
  esac
}

is_running() {
  local pf; pf="$(svc_pidfile "$1")"
  [ -f "$pf" ] && kill -0 "$(cat "$pf")" 2>/dev/null
}

compose() { docker compose -f "$ROOT/docker-compose.yml" "$@"; }

infra_up() {
  echo "▸ 基础设施 (Docker)"
  if ! docker info >/dev/null 2>&1; then
    echo "  ✗ Docker 没在运行 —— 请先启动 Docker Desktop"
    return 1
  fi
  compose up -d >/dev/null 2>&1 || { echo "  ✗ docker compose up 失败"; return 1; }

  # 等 PG 真能接受连接(容器 healthy 不代表端口已通)
  local i
  for i in $(seq 1 40); do
    if docker exec yourinterview-pg pg_isready -U postgres -d yourinterview >/dev/null 2>&1; then
      echo "  ✓ PostgreSQL :5433"; break
    fi
    [ "$i" = "40" ] && { echo "  ⚠ PostgreSQL 等待超时"; return 1; }
    sleep 2
  done
  for i in $(seq 1 40); do
    if docker exec yourinterview-rabbit rabbitmq-diagnostics -q ping >/dev/null 2>&1; then
      echo "  ✓ RabbitMQ :5672 / :15672"; break
    fi
    [ "$i" = "40" ] && { echo "  ⚠ RabbitMQ 等待超时"; return 1; }
    sleep 2
  done
  ensure_databases
}

ensure_databases() {
  docker exec yourinterview-pg psql -U postgres -tAc \
    "select 1 from pg_database where datname='yourinterview'" 2>/dev/null | grep -q 1 || {
    docker exec yourinterview-pg psql -U postgres -c "create database yourinterview" >/dev/null 2>&1 \
      && echo "  ✓ 已创建数据库 yourinterview"
  }
}

infra_down() {
  compose down >/dev/null 2>&1 && echo "  ✓ Docker 基础设施已停(数据保留)" || true
}

start_svc() {
  local s="$1" port="${PORTS[$1]}"
  local proj="$SRC/$(svc_project "$1")"
  [ -d "$proj" ] || { echo "  ✗ $s 项目不存在"; return 1; }
  if is_running "$s"; then echo "  · $s 已在运行"; return 0; fi

  # 先构建再启动 —— dotnet run --no-build 会用旧二进制。
  if ! dotnet build "$proj" -v q --nologo > "$LOGS/$s.build.log" 2>&1; then
    echo "  ✗ $s 构建失败,详见 $LOGS/$s.build.log"; return 1
  fi

  ( cd "$proj" && setsid nohup dotnet run --no-build --urls "http://127.0.0.1:$port" \
      > "$(svc_log "$s")" 2>&1 < /dev/null & echo $! > "$(svc_pidfile "$s")" )
  echo "  ↻ $s 启动中 (port $port) …"
}

stop_svc() {
  local s="$1" pf; pf="$(svc_pidfile "$s")"
  if [ -f "$pf" ]; then
    local pid; pid="$(cat "$pf")"
    # dotnet run 会 fork 出真正的子进程,杀掉整个进程组才干净
    kill -- "-$(ps -o pgid= "$pid" 2>/dev/null | tr -d ' ')" 2>/dev/null || kill "$pid" 2>/dev/null
    rm -f "$pf"
  fi
  pkill -f "YourInterview.Services.$(echo "$s" | sed 's/.*/\u&/')" 2>/dev/null
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

show_urls() {
  echo
  echo "  前端(需另开终端):  cd $ROOT/web && npm start"
  echo "  前端地址:           http://localhost:4200"
  echo "  网关:               http://localhost:5200"
  echo "  登录:               admin@your-interview.local / Admin!Passw0rd2026"
  echo "  RabbitMQ 管理台:    http://localhost:15672  (guest / guest)"
  echo
}

case "${1:-status}" in
  up)
    infra_up || exit 1
    echo "▸ 微服务"
    for s in "${SVC_ORDER[@]}"; do start_svc "$s"; done
    for s in "${SVC_ORDER[@]}"; do wait_health "$s"; done
    echo "▸ 全部就绪"
    show_urls
    ;;
  infra)
    infra_up
    ;;
  down)
    for s in "${SVC_ORDER[@]}"; do stop_svc "$s"; done
    echo "  ✓ 服务已停(基础设施保留,dev.sh down-all 可一并停掉)"
    ;;
  down-all)
    for s in "${SVC_ORDER[@]}"; do stop_svc "$s"; done
    infra_down
    echo "  ✓ 全部停止(数据保留在 Docker 卷里)"
    ;;
  restart)
    s="${2:?用法: dev.sh restart <identity|jobs|interviews|knowledge|assessment|analytics|gateway>}"
    stop_svc "$s"; sleep 2; start_svc "$s"; wait_health "$s"
    ;;
  build)
    for s in "${SVC_ORDER[@]}"; do
      proj="$SRC/$(svc_project "$s")"
      echo "  ▸ 构建 $s …"
      dotnet build "$proj" -v q --nologo > "$LOGS/$s.build.log" 2>&1 \
        && echo "    ✓ $s" || echo "    ✗ $s(见 $LOGS/$s.build.log)"
    done
    ;;
  status)
    if docker info >/dev/null 2>&1; then
      echo "[infra] $(compose ps --format '{{.Name}} {{.State}}' 2>/dev/null | tr '\n' ' ')"
    else
      echo "[infra] Docker 未运行"
    fi
    for s in "${SVC_ORDER[@]}"; do
      if is_running "$s"; then echo "  ✓ $s (port ${PORTS[$s]})"; else echo "  · $s 未运行"; fi
    done
    ;;
  logs)
    s="${2:?用法: dev.sh logs <service>}"
    tail -n 80 -f "$(svc_log "$s")"
    ;;
  e2e)
    bash "$ROOT/tools/e2e.sh" "${2:-all}"
    ;;
  urls) show_urls ;;
  *)
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
