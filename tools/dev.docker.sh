#!/usr/bin/env bash
# Your Interview — 本地开发栈管理(全容器版)
#
# 后端全部跑在 Docker 里(数据库 + 消息队列 + 7 个微服务 + 网关),
# 前端在宿主机用 npm start 跑。
#
#   ./dev.sh up            构建并启动全部容器
#   ./dev.sh down          停全部(数据保留)
#   ./dev.sh reset         停并清空数据(彻底重来)
#   ./dev.sh status        看容器状态与后端连通性
#   ./dev.sh logs <svc>    实时看服务日志
#   ./dev.sh restart <svc> 重启单个服务
#   ./dev.sh rebuild [svc] 重新构建(默认全部)
#   ./dev.sh web           起前端(需要另开终端)
#   ./dev.sh crawl         打印所有地址
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE="docker compose -f $ROOT/docker-compose.yml"

SVC_LIST="postgres rabbitmq identity jobs interviews knowledge assessment analytics gateway"

require_docker() {
  if ! docker info >/dev/null 2>&1; then
    echo "  ✗ Docker 没在运行 —— 请先启动 Docker Desktop"
    exit 1
  fi
}

wait_ready() {
  echo "▸ 等待服务就绪(首次构建/迁移会慢,约 10-20 分钟)…"
  local i
  for i in $(seq 1 120); do
    if curl -sf -o /dev/null "http://localhost:5200/api/gateway/info" 2>/dev/null; then
      echo "  ✓ 后端就绪 → http://localhost:5200"
      return 0
    fi
    sleep 5
  done
  echo "  ⚠ 等待超时。看日志: $0 logs gateway"
  return 1
}

show_urls() {
  echo
  echo "  前端:            http://localhost:4200   (cd web && npm start)"
  echo "  网关:            http://localhost:5200"
  echo "  登录:            admin@your-interview.local / $ADMIN_PASSWORD(.env 里的)"
  echo "  数据库(外部连):  localhost:5433  postgres / postgres  db=yourinterview"
  echo "  RabbitMQ 管理台: http://localhost:15672  (guest / guest)"
  echo
}

case "${1:-status}" in
  up)
    require_docker
    $COMPOSE up -d --build || exit 1
    wait_ready
    show_urls
    ;;
  down)
    require_docker
    $COMPOSE down
    echo "  ✓ 已停(数据保留,dev.sh reset 可清空)"
    ;;
  reset)
    require_docker
    $COMPOSE down -v
    echo "  ✓ 已停并清空数据。下次 up 会从零初始化。"
    ;;
  status)
    require_docker
    $COMPOSE ps
    echo
    if curl -sf -o /dev/null "http://localhost:5200/api/gateway/info" 2>/dev/null; then
      echo "  ✓ 后端可从宿主机访问 (localhost:5200)"
    else
      echo "  · 后端暂不可达(可能还在启动)"
    fi
    ;;
  logs)
    s="${2:-gateway}"
    $COMPOSE logs -f --tail 100 "$s"
    ;;
  restart)
    s="${2:?用法: dev.sh restart <postgres|rabbitmq|identity|jobs|interviews|knowledge|assessment|analytics|gateway>}"
    $COMPOSE restart "$s"
    ;;
  rebuild)
    require_docker
    if [ -n "${2:-}" ]; then
      $COMPOSE up -d --build "$2"
    else
      $COMPOSE up -d --build
    fi
    ;;
  web)
    cd "$ROOT/web" || exit 1
    [ -d node_modules ] || npm install
    npm start
    ;;
  crawl|urls) show_urls ;;
  *)
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
