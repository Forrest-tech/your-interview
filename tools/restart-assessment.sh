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

# ---------------------------------------------------------------------------
# 1) 停 —— ⚠️ 绝对不能用 lsof -ti tcp:5266 无脑杀!
#
#    Forrest 2026-09-16 亲历两次 Docker Desktop 被"关"(实为重启),真根因在此:
#    容器里的 gateway 通过 host.docker.internal:5266 转发到宿主机 assessment
#    (docker-compose.hybrid.yml 第 79 行)。macOS 上这个转发由 Docker 的
#    vpnkit/com.docker.backend 进程持有 —— 它在 5266 上有一条 ESTABLISHED 连接。
#    lsof -ti tcp:5266 会把 **Docker 的转发进程一并列出来**,
#    于是"按端口杀"就顺手打断了 Docker 的网络栈,
#    Docker Desktop 的 watchdog 判定链路致命故障 → **整个 Docker 栈重启**
#    (菜单栏短暂显示 stopped,随后是一批全新 PID)。
#
#    实测证据(lsof -nP -iTCP:5266):
#      YourInter 54624 ... 127.0.0.1:5266 (LISTEN)
#      com.docke 55058 ... 127.0.0.1:52290->127.0.0.1:5266 (ESTABLISHED)  ← 元凶
#
#    ✅ 正确做法:只认"命令行确实是 assessment"的进程,别的(尤其 Docker)一概不碰。
#       宁可少杀、让用户自己处理,也绝不误伤 Docker。
# ---------------------------------------------------------------------------

# 判断某 PID 是否真的是我们的 assessment 服务
is_assessment_pid() {
  local pid="$1" cmd
  [ -n "$pid" ] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  cmd="$(ps -o command= -p "$pid" 2>/dev/null || true)"
  [ -n "$cmd" ] || return 1
  # 只认这几种特征:dotnet run --project .../Services.Assessment,或已编译的
  # YourInterview.Services.Assessment 宿主进程。其余(含 Docker、com.docke)一律不认。
  case "$cmd" in
    *"Services.Assessment"*) return 0 ;;
    *YourInterview.Services.Assessment*) return 0 ;;
    *) return 1 ;;
  esac
}

echo "▸ 停止 assessment(只杀命令行确认为 assessment 的进程,绝不碰 Docker)"
stopped=0
# 候选一:pidfile 里的 PID(我们上次启动时记下的),但必须先验证身份
if [ -f "$ROOT/.run/assessment.pid" ]; then
  pf_pid="$(cat "$ROOT/.run/assessment.pid" 2>/dev/null || true)"
  if is_assessment_pid "$pf_pid"; then
    kill -TERM "$pf_pid" 2>/dev/null || true
    c_dim "已向 pidfile 中的 assessment (PID $pf_pid) 发送 TERM"
    stopped=1
  elif [ -n "$pf_pid" ]; then
    c_dim "pidfile 里的 PID $pf_pid 已不是 assessment(可能已被系统复用),忽略它"
  fi
fi

# 候选二:监听 5266 的进程里,**逐个用命令行验明身份**,只对确认是 assessment 的下手
if [ "$stopped" = "0" ]; then
  for h in $(lsof -ti "tcp:$PORT" 2>/dev/null || true); do
    if is_assessment_pid "$h"; then
      kill -TERM "$h" 2>/dev/null || true
      c_dim "已向占着 $PORT 的 assessment (PID $h) 发送 TERM"
      stopped=1
    else
      c_dim "PID $h 占着 $PORT 但不是 assessment(可能是 Docker 转发),跳过不碰"
    fi
  done
fi

if [ "$stopped" = "0" ]; then
  c_dim "没发现运行中的 assessment,无需停止"
else
  # 优雅退出等待:只检查"是否还存在仍是 assessment 的进程"
  for _ in $(seq 1 5); do
    still_alive=0
    for h in $(lsof -ti "tcp:$PORT" 2>/dev/null || true); do
      is_assessment_pid "$h" && still_alive=1
    done
    [ "$still_alive" = "0" ] && break
    sleep 1
  done
  # 仍未退出的,只对**确认是 assessment** 的 PID 强杀
  for h in $(lsof -ti "tcp:$PORT" 2>/dev/null || true); do
    if is_assessment_pid "$h"; then
      kill -KILL "$h" 2>/dev/null || true
      c_dim "assessment (PID $h) 未退出,已 KILL"
    fi
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
