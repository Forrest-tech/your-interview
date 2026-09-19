#!/usr/bin/env bash
# =============================================================================
#  go.sh —— 口语练习一键起(Forrest 专用最简入口)
#
#    bash go.sh          起:数据库检查 + 后端 + 前端
#    bash go.sh stop     停:后端 + 前端
#    bash go.sh status   看状态
#
#  然后浏览器打开 http://127.0.0.1:4200/practice
#
#  ⚠️ 本脚本只是 tools/stack.sh 的薄封装,不再自己实现启停逻辑。
#     所有服务定义、配置注入、健康探测都在 stack.sh 里 —— 改一处,全局生效。
#     需要更多能力(日志/环境/逐服务重启):bash tools/stack.sh
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

case "${1:-start}" in
  start|"")
    bash "$ROOT/tools/stack.sh" up practice
    echo
    echo "  打开:  http://127.0.0.1:4200/practice"
    echo "  停止:  bash go.sh stop"
    echo
    echo "  看日志: bash tools/stack.sh logs <服务名>"
    ;;
  stop)
    bash "$ROOT/tools/stack.sh" down practice
    ;;
  status)
    bash "$ROOT/tools/stack.sh" status
    ;;
  *)
    echo "用法: go.sh {start|stop|status}"
    exit 1
    ;;
esac
