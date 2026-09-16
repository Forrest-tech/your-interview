#!/usr/bin/env bash
# =============================================================================
#  ai-practice.sh —— 口语练习模块一键启动/停止(Mac 本地,不用 Docker)
#
#  用法:
#    bash tools/ai-practice.sh start    启动(PostgreSQL + RabbitMQ + Assessment + Gateway + 前端)
#    bash tools/ai-practice.sh stop     停止后端与前端
#    bash tools/ai-practice.sh status   看状态
#    bash tools/ai-practice.sh open     只看网址
#
#  设计要点(都是踩过的坑):
#   1. 显式 source 仓库根 .env —— tools/dev.sh 自己不读 .env,
#      靠的是各服务的 appsettings.Development.json(gitignore 忽略)。
#      一旦那个文件缺失,服务会拿着占位符 JWT key 启动 → 登录成功但接口全 401。
#      这里优先注入 .env,双保险。
#   2. 前端固定 --host 127.0.0.1 —— macOS 上 localhost 可能解析到 ::1 导致
#      ENOTFOUND,一律用 127.0.0.1。
#   3. 只起练习用得到的两个服务(assessment + gateway),
#      其余五个与本页无关,老 Intel 双核 Mac 上省一半内存。
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

LOG="$ROOT/.logs"
RUN="$ROOT/.run"
mkdir -p "$LOG" "$RUN"

WEB_LOG="$LOG/web.log"
WEB_PID="$RUN/web.pid"
WEB_PORT=4200

# ---------- 载入 .env(真实密钥只在本机) ----------
load_env() {
  if [ -f "$ROOT/.env" ]; then
    set -a; . "$ROOT/.env"; set +a
    echo "  ✓ 已载入 .env(JWT / 管理员密码 / 连接串 / 语音 key)"
  else
    echo "  ⚠ 未找到 .env —— 若服务起来后登录报 401,先执行: cp .env.example .env 并填真值"
  fi
}

web_running() { [ -f "$WEB_PID" ] && kill -0 "$(cat "$WEB_PID")" 2>/dev/null; }

start_web() {
  if web_running; then echo "  · 前端已在运行"; return 0; fi

  if [ ! -d "$ROOT/web/node_modules" ]; then
    echo "  ✗ web/node_modules 不存在,请先: cd web && npm install"
    return 1
  fi

  # Angular 应用在 web/ 子目录(仓库根的 package.json 只是占位代理),必须 cd 进去
  # ⚠️ setsid 是 Linux 专有,macOS 没有 → 能力探测后回退 nohup
  LAUNCH=""
  command -v setsid >/dev/null 2>&1 && LAUNCH="setsid "
  ( cd "$ROOT/web" && ${LAUNCH}nohup npx ng serve \
      --proxy-config proxy.conf.json --host 127.0.0.1 --port "$WEB_PORT" \
      > "$WEB_LOG" 2>&1 < /dev/null & echo $! > "$WEB_PID" )
  echo "  ↻ 前端编译中(首次约 20-40 秒)… 想盯着看: tail -f .logs/web.log"

  local i
  for i in $(seq 1 60); do
    if curl -sf -o /dev/null "http://127.0.0.1:$WEB_PORT" 2>/dev/null; then
      echo "  ✓ 前端就绪 http://127.0.0.1:$WEB_PORT"; return 0
    fi
    if ! web_running; then
      echo "  ✗ 前端进程已退出,最后 15 行日志:"
      tail -15 "$WEB_LOG" 2>/dev/null | sed 's/^/      /'
      return 1
    fi
    sleep 2
  done
  echo "  ⚠ 前端 2 分钟未就绪,看日志: tail -f $WEB_LOG"
}

stop_web() {
  if [ -f "$WEB_PID" ]; then kill "$(cat "$WEB_PID")" 2>/dev/null; rm -f "$WEB_PID"; fi
  pkill -f "ng serve" 2>/dev/null
  echo "  ✗ 前端已停"
}

urls() {
  cat <<EOF

  ── 打开这些 ─────────────────────────────────────────────
   口语练习页       http://127.0.0.1:4200/practice
   Azure 语音设置    http://127.0.0.1:4200/account/ai-setting
   Assessment 调试   http://127.0.0.1:5266/swagger
   Gateway 心跳      http://127.0.0.1:5200/health/live
  ─────────────────────────────────────────────────────────
   日志:  tail -f .logs/assessment.log    后端
          tail -f .logs/web.log           前端
EOF
}

case "${1:-start}" in
  start)
    load_env
    # tools/db 只需要 `pg` 这个驱动(不再需要 embedded-postgres 二进制,
    # 因为库用的是本机 PostgreSQL)。缺了就自动装,不让用户猜。
    if [ ! -d "$ROOT/tools/db/node_modules/pg" ]; then
      echo "  · 首次运行:安装 tools/db 依赖(pg 驱动)…"
      ( cd "$ROOT/tools/db" && npm install ) || { echo "  ✗ tools/db 依赖安装失败"; exit 1; }
    fi

    echo "▸ 基础设施"
    # 数据库用本机已装的 PostgreSQL(连接串来自 .env,默认 :5432)。
    # 不再启动 embedded-postgres(5433)—— 与本机 PG 并存白占内存,还易查错库。
    PG_OUT="$(node "$ROOT/tools/db/db.js" psql "SELECT 1" 2>&1)"
    if echo "$PG_OUT" | grep -q "SQL 失败"; then
      echo "  ✗ 连不上 .env 里配置的 PostgreSQL"
      echo "$PG_OUT" | sed 's/^/      /'
      echo "    → 确认本机 PostgreSQL 已启动;或修正 .env 里的连接串"
      exit 1
    fi
    echo "  ✓ PostgreSQL 可达 (来自 .env)"

    # RabbitMQ:练习页(素材树/录音/评分/TTS)全程不经过消息队列,
    # 所以这里**只尝试、不阻塞、不刷警告** —— 装了就起,没装就跳过。
    if [ -x "$HOME/rabbitmq/rootfs/usr/lib/rabbitmq/bin/rabbitmq-server" ]; then
      "$ROOT/tools/rabbit/rabbit.sh" start >/dev/null 2>&1 \
        && echo "  ✓ RabbitMQ :5672" || echo "  · RabbitMQ 未起来(练习页不需要,忽略)"
    else
      echo "  · RabbitMQ 未安装(练习页不需要,跳过)"
    fi

    echo "▸ 后端服务(只起练习需要的两个)"
    bash "$ROOT/tools/dev.sh" practice
    echo "▸ 前端"
    start_web
    urls
    ;;
  stop)
    stop_web
    bash "$ROOT/tools/dev.sh" down
    ;;
  status)
    echo "▸ 后端"
    bash "$ROOT/tools/dev.sh" status
    echo "▸ 前端"
    if web_running; then echo "  ✓ 前端运行中 (pid $(cat "$WEB_PID"))"; else echo "  · 前端未运行"; fi
    ;;
  open) urls ;;
  *) echo "用法: ai-practice.sh {start|stop|status|open}"; exit 1 ;;
esac
