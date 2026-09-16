#!/usr/bin/env bash
# =============================================================================
#  restart-assessment.sh —— 【只重启 assessment】,绝不碰 Docker / 其它服务
#
#  用法:
#    bash tools/restart-assessment.sh
#
#  ⚠️ 为什么不直接用 tools/dev.sh restart assessment:
#     dev.sh 的 stop_svc 有一行 `kill -TERM -$pid`(负号 = 杀**整个进程组**),
#     而 $pid 读自 .run/assessment.pid —— 那是**上一次**的陈旧 PID。
#     macOS 会回收复用 PID,那个号可能已分给 Docker 的进程,
#     于是 kill 进程组时把 Docker Desktop 一起带走(Forrest 2026-09-16 亲历两次)。
#
#  本脚本的铁律(逐条对应上面的教训):
#     1. 只用 lsof 精确定位 **5266** 的持有者,绝不扫其它端口。
#     2. 只对**单个 PID** 发信号,绝不用负号 PID(不杀进程组)。
#     3. 不读、不信 .run/assessment.pid(陈旧 PID 才是炸弹引信)。
#     4. 全程不出现 docker 字样,绝不 stop/restart 任何容器。
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PORT=5266
LOG="$ROOT/.logs/assessment.log"
mkdir -p "$ROOT/.logs"

c_ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
c_no()   { printf '  \033[31m✗\033[0m %s\n' "$1"; }
c_dim()  { printf '  · %s\n' "$1"; }

# ---------- 已加载环境(沿用 dev.sh 的键名映射,保证与容器内 JWT key 一致) ----------
# dotnet 可能装在 ~/.dotnet(非 Homebrew),但 PATH 里未必有它。
# 显式补上,避免 "dotnet: command not found"(沙箱/Mac 都可能遇到)。
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  case ":$PATH:" in *":$HOME/.dotnet:"*) ;; *) export PATH="$HOME/.dotnet:$PATH" ;; esac
fi
export PATH="$HOME/.dotnet/tools:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_NOLOGO=1
export ASPNETCORE_ENVIRONMENT=Development

if [ -z "${Jwt__SigningKey:-}" ] && [ -f "$ROOT/.env" ]; then
  _jwt="$(grep -E '^JWT_SIGNING_KEY=' "$ROOT/.env" | head -1 | cut -d= -f2-)"
  _jwt="${_jwt%\"}"; _jwt="${_jwt#\"}"
  [ -n "$_jwt" ] && export Jwt__SigningKey="$_jwt"
  unset _jwt
fi

# ---------- 1) 停:只处理 5266 的持有者,单个 PID,不杀进程组 ----------
echo "▸ 停止 5266 上的 assessment(若有)"
holders="$(lsof -ti "tcp:$PORT" 2>/dev/null || true)"
if [ -z "$holders" ]; then
  c_dim "5266 空闲,无需停止"
else
  for h in $holders; do
    # 只对这一个 PID,不用负号(= 不碰进程组,不碰 Docker)
    kill -TERM "$h" 2>/dev/null || true
    c_dim "已向 PID $h 发送 TERM"
  done
  # 给它 5 秒优雅退出,否则只对**同一个 PID** 强杀
  for _ in $(seq 1 5); do
    lsof -ti "tcp:$PORT" >/dev/null 2>&1 || break
    sleep 1
  done
  still="$(lsof -ti "tcp:$PORT" 2>/dev/null || true)"
  for h in $still; do
    kill -KILL "$h" 2>/dev/null || true
    c_dim "PID $h 未退出,已 KILL"
  done
  c_ok "assessment 已停"
fi

# ---------- 2) 起:先构建,再后台运行(nohup/setsid 探测) ----------
sleep 1
echo "▸ 构建 assessment"
if ! dotnet build "$ROOT/src/Services.Assessment" -v q --nologo > "$ROOT/.logs/assessment.build.log" 2>&1; then
  c_no "构建失败,关键错误:"
  grep -E "error [A-Z]+[0-9]+|error :" "$ROOT/.logs/assessment.build.log" 2>/dev/null \
    | sort -u | head -12 | sed 's/^/      /'
  exit 1
fi
c_ok "构建通过"

LAUNCH=""
command -v setsid >/dev/null 2>&1 && LAUNCH="setsid "
echo "▸ 启动 assessment (port $PORT)"
( cd "$ROOT" && ${LAUNCH}nohup dotnet run --project src/Services.Assessment \
    --no-build --no-launch-profile --urls "http://127.0.0.1:$PORT" \
    > "$LOG" 2>&1 < /dev/null & echo $! > "$ROOT/.run/assessment.pid" )

# ---------- 3) 等就绪 ----------
for i in $(seq 1 45); do
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 \
            "http://127.0.0.1:$PORT/health/live" 2>/dev/null)"
  if [ -n "$code" ] && [ "$code" != "000" ]; then
    c_ok "assessment 就绪 :$PORT (HTTP $code)"
    echo
    echo "  日志: tail -f .logs/assessment.log"
    exit 0
  fi
  sleep 2
done
c_no "90 秒未就绪 —— 最后 20 行日志:"
tail -20 "$LOG" 2>/dev/null | sed 's/^/      /'
exit 1
