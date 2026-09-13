#!/usr/bin/env bash
# RabbitMQ 本地运行器(无需 root / 无需 Docker)
#
# 原理:从 Debian 包解出 rabbitmq-server 3.10.8 + erlang 25 到 ~/rabbitmq/rootfs,
# 然后以当前用户身份启动 broker(AMQP 5672 + 管理界面 15672)。
#
#   ./rabbit.sh start|stop|status|logs|ctl <cmd>
set -euo pipefail

ROOTFS="$HOME/rabbitmq/rootfs"
ERL="$ROOTFS/usr/lib/erlang"
RABBIT="$ROOTFS/usr/lib/rabbitmq"
DATA="$HOME/rabbitmq/data"
LOGS="$HOME/rabbitmq/log"
NODE_NAME="yourinterview@$(hostname)"
AMQP_PORT="${AMQP_PORT:-5672}"
MGMT_PORT="${MGMT_PORT:-15672}"

export ERL_ROOTDIR="$ERL"
export PATH="$ERL/bin:$PATH"
export RABBITMQ_HOME="$RABBIT"
export RABBITMQ_MNESIA_BASE="$DATA/mnesia"
export RABBITMQ_LOG_BASE="$LOGS"
export RABBITMQ_ENABLED_PLUGINS_FILE="$DATA/enabled_plugins"
export RABBITMQ_PID_FILE="$HOME/rabbitmq/rabbit.pid"
export RABBITMQ_NODENAME="$NODE_NAME"
export RABBITMQ_USE_LONGNAME=false
export RABBITMQ_NODE_PORT="$AMQP_PORT"
export RABBITMQ_DIST_PORT="${RABBITMQ_DIST_PORT:-25672}"
export RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS="-rabbitmq_management listener [{port,$MGMT_PORT}]"
export HOME_FOR_ERL="$HOME"

mkdir -p "$DATA/mnesia" "$LOGS" "$DATA/plugins"

# 启用管理插件(写 enabled_plugins 文件,避免 rabbitmq-plugins 的依赖问题)
if [ ! -f "$RABBITMQ_ENABLED_PLUGINS_FILE" ]; then
  echo '[rabbitmq_management,rabbitmq_prometheus].' > "$RABBITMQ_ENABLED_PLUGINS_FILE"
fi

start() {
  if status >/dev/null 2>&1; then echo "[rabbit] 已在运行"; return 0; fi
  echo "[rabbit] 启动 RabbitMQ(node=$NODE_NAME, amqp=$AMQP_PORT, mgmt=$MGMT_PORT) …"
  "$RABBIT/bin/rabbitmq-server" > "$LOGS/console.log" 2>&1 &
  for i in $(seq 1 60); do
    sleep 2
    if "$RABBIT/bin/rabbitmq-diagnostics" -q ping >/dev/null 2>&1; then
      echo "[rabbit] 已启动"
      "$RABBIT/bin/rabbitmqctl" -q set_vm_memory_high_watermark 0.6 >/dev/null 2>&1 || true
      "$RABBIT/bin/rabbitmqctl" -q set_disk_free_limit 1GB >/dev/null 2>&1 || true
      return 0
    fi
    if ! kill -0 $! 2>/dev/null; then
      echo "[rabbit] 启动失败,日志尾部:"; tail -30 "$LOGS/console.log"; return 1
    fi
  done
  echo "[rabbit] 超时,日志尾部:"; tail -30 "$LOGS/console.log"; return 1
}

stop() {
  if ! status >/dev/null 2>&1; then echo "[rabbit] 未在运行"; return 0; fi
  echo "[rabbit] 停止 …"
  "$RABBIT/bin/rabbitmqctl" -q stop >/dev/null 2>&1 || true
  sleep 3
  pkill -f "beam.smp.*$NODE_NAME" 2>/dev/null || true
  echo "[rabbit] 已停止"
}

status() {
  "$RABBIT/bin/rabbitmq-diagnostics" -q ping 2>/dev/null && echo "[rabbit] running amqp=$AMQP_PORT mgmt=$MGMT_PORT"
}

cmd() { "$RABBIT/bin/rabbitmqctl" "$@"; }

case "${1:-status}" in
  start) start ;;
  stop) stop ;;
  status)
    if "$RABBIT/bin/rabbitmq-diagnostics" -q ping >/dev/null 2>&1; then
      echo "[rabbit] running  amqp=$AMQP_PORT  mgmt=http://127.0.0.1:$MGMT_PORT  (guest/guest)"
    else
      echo "[rabbit] stopped"
    fi ;;
  logs) tail -50 "$LOGS/console.log" ;;
  ctl) shift; cmd "$@" ;;
  *) echo "用法: $0 {start|stop|status|logs|ctl <rabbitmqctl args>}"; exit 1 ;;
esac
