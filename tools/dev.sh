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
#   ./dev.sh practice      【口语练习专用】只起 PG + RabbitMQ + assessment + gateway
#                          —— 不必起全部七个服务,老 Mac 上省一半内存
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src"
RUN="$ROOT/.run"
LOGS="$ROOT/.logs"
mkdir -p "$RUN" "$LOGS"

# DOTNET_ROOT 可能未设置 —— 在 set -u 下直接引用会报 unbound variable 而整体退出。
export DOTNET_ROOT="${DOTNET_ROOT:-}"
export PATH="${DOTNET_ROOT:+$DOTNET_ROOT:}$HOME/.dotnet/tools:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_NOLOGO=1
export ASPNETCORE_ENVIRONMENT=Development

# 🔑 JWT 签名密钥的键名映射(供**直接**调用本脚本时兜底)
#    .env 用 JWT_SIGNING_KEY,而服务读的配置路径是 Jwt:SigningKey。
#    正常由 go.sh / hybrid.sh 载入 .env 并映射;这里兜底,避免出现
#    "有的服务用真 key、有的回落到占位符"这种验签必 401 的状态。
if [ -z "${Jwt__SigningKey:-}" ] && [ -f "$ROOT/.env" ]; then
  _jwt="$(grep -E '^JWT_SIGNING_KEY=' "$ROOT/.env" | head -1 | cut -d= -f2-)"
  _jwt="${_jwt%\"}"
  _jwt="${_jwt#\"}"
  if [ -n "$_jwt" ]; then
    export Jwt__SigningKey="$_jwt"
    export JWT_SIGNING_KEY="$_jwt"
  fi
  unset _jwt
fi

# 服务名 → 端口
get_port() {
  case "$1" in
    identity) echo 5262 ;;
    jobs) echo 5263 ;;
    interviews) echo 5264 ;;
    knowledge) echo 5265 ;;
    assessment) echo 5266 ;;
    analytics) echo 5267 ;;
    gateway) echo 5200 ;;
  esac
}

svc_pidfile() { echo "$RUN/$1.pid"; }
svc_log()     { echo "$LOGS/$1.log"; }

# ---------------------------------------------------------------------------
# is_svc_pid —— 判断某 PID 是否真的是我们要操作的那个服务(而非 Docker 等)
#
# ⚠️ 2026-09-16 Forrest 亲历两次 Docker Desktop 被"关"(实为重启),根因就在下面:
#   容器里的 gateway 经 host.docker.internal:5266 转发到宿主机 assessment
#   (docker-compose.hybrid.yml:79)。macOS 上该转发由 Docker 的
#   vpnkit/com.docker.backend 进程实现 —— 它在宿主机端口上挂着 ESTABLISHED 连接。
#   于是 `lsof -ti tcp:<port>` 会把 **Docker 的进程一并列出来**,
#   "按端口无脑杀"就顺手打断了 Docker 网络栈 → watchdog 判定致命故障 →
#   **整个 Docker 栈重启**。实证(lsof -nP -iTCP:5266):
#     YourInter 54624 ... 127.0.0.1:5266 (LISTEN)
#     com.docke 55058 ... 127.0.0.1:52290->127.0.0.1:5266 (ESTABLISHED)  ← 元凶
#
#   因此:凡是按 PID 下手之前,先读命令行验明身份;不匹配就跳过,绝不碰。
#   宁可少杀(留给用户手动处理),也绝不像 Docker 那样误伤无关进程。
# ---------------------------------------------------------------------------
is_svc_pid() {
  local s="$1" pid="$2" cmd proj
  [ -n "$pid" ] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  cmd="$(ps -o command= -p "$pid" 2>/dev/null || true)"
  [ -n "$cmd" ] || return 1
  proj="$(svc_project "$s")"
  # 认两种特征:dotnet run --project .../Services.X,或已编译的宿主进程 YourInterview.Services.X
  case "$cmd" in
    *"$proj"*) return 0 ;;
    *"YourInterview.$proj"*) return 0 ;;
    *) return 1 ;;
  esac
}

# svc_in_container —— 判断该服务的端口是否正被 **Docker/容器** 占用
# 用于在启动宿主机副本前拦住用户:该服务属于容器(hybrid.sh 管),别用 dev.sh 起。
is_svc_in_container() {
  local s="$1" port
  port="$(get_port "$s")"
  [ -n "$port" ] || return 1
  local h cmd
  for h in $(lsof -ti "tcp:$port" 2>/dev/null || true); do
    cmd="$(ps -o command= -p "$h" 2>/dev/null || true)"
    case "$cmd" in
      *com.docker*|*Docker.app*|*vpnkit*|*com.docke*) return 0 ;;
    esac
  done
  return 1
}

start_svc() {
  local s="$1" port
  port="$(get_port "$s")"
  [ -n "$port" ] || { echo "  ✗ 未知服务 $s"; return 1; }
  local proj="$SRC/$(svc_project "$1")"
  [ -d "$proj" ] || { echo "  ✗ $s 项目不存在"; return 1; }
  # ⚠️ 不能只看 pidfile:上一轮 stop 若没杀干净,端口仍被旧进程占着,
  # 这时"已经在运行"其实意味着**跑的是旧代码/旧配置**,静默沿用会让人以为
  # 新改动生效了(今晚就踩了:identity/assessment 一直用旧进程)。
  # 所以:pidfile 活着且端口确有应答 → 才算真在运行;
  #      否则视作残留,先清端口再启动。
  if is_running "$s" && [ -n "$port" ] && lsof -ti "tcp:$port" >/dev/null 2>&1; then
    echo "  · $s 已在运行 (pid $(cat "$(svc_pidfile "$s")"))"
    return 0
  fi

  # 🔑 容器保护(2026-09-16 Forrest 定):
  #   本项目是**混合模式** —— gateway / identity 跑在容器里(归 tools/hybrid.sh 管),
  #   只有 assessment 在宿主机直跑。如果这两个服务的端口正被 Docker 占用,
  #   说明它们已经在容器里好好跑着,此时再在宿主机起一个副本毫无意义,
  #   还会因端口冲突把容器搞乱。直接拦住并告知正确命令。
  if [ -n "$port" ] && is_svc_in_container "$s"; then
    echo "  ✗ $s 正跑在 Docker 容器里(端口 $port 被容器占用)"
    echo "    本项目的 gateway / identity 归容器管,**不要用 dev.sh 碰它们**。"
    echo "    → 正确命令: bash tools/hybrid.sh up | down | restart | status"
    echo "    → 宿主机直跑的服务只有 assessment: bash tools/restart-assessment.sh"
    return 1
  fi

  if [ -n "$port" ] && lsof -ti "tcp:$port" >/dev/null 2>&1; then
    echo "  ⚠ $s 端口 $port 被残留进程占用 —— 先清理再启动"
    stop_svc "$s"
  fi

  # 先构建再启动 —— dotnet run --no-build 会用旧二进制。
  # 这个坑在加 EF 迁移时尤其折磨(迁移文件是新的,跑的却是旧代码)。
  if ! dotnet build "$proj" -v q --nologo > "$LOGS/$s.build.log" 2>&1; then
    echo "  ✗ $s 构建失败 —— 关键错误如下,完整日志 $LOGS/$s.build.log"
    # 只挑错误行,避免刷屏;用户第一眼就能看到根因
    grep -E "error [A-Z]+[0-9]+|error :|MSB[0-9]+" "$LOGS/$s.build.log" 2>/dev/null \
      | sort -u | head -12 | sed 's/^/      /'
    return 1
  fi

  # ⚠️ setsid 是 Linux(util-linux)专有命令,**macOS 根本没有** ——
  # 之前写死 setsid 导致 Mac 上整条启动命令失败(setsid: command not found),
  # 服务从未真正起来,所有健康检查必然超时。这里做能力探测:
  #   有 setsid(Linux/容器)→ 用它彻底脱离控制终端;
  #   没有(macOS)→ 退回 nohup + &,同样能后台常驻。
  local LAUNCH=""
  command -v setsid >/dev/null 2>&1 && LAUNCH="setsid "
  # --no-launch-profile:**不读 Properties/launchSettings.json**。
  # 那个文件里写着 applicationUrl=http://localhost:5241、launchBrowser=true,
  # 会跟这里的 --urls 打架,在 macOS 上还可能因为没有 GUI/TTY 而卡住。
  # 我们永远只用 --urls 指定的 127.0.0.1:<port>,所以必须显式屏蔽它。
  ( cd "$proj" && ${LAUNCH}nohup dotnet run --no-build --no-launch-profile \
      --urls "http://127.0.0.1:$port" \
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
  local s="$1" pf port pid
  pf="$(svc_pidfile "$s")"
  port="$(get_port "$s")"

  # (1) pidfile 里的 PID 是 `dotnet run` **包装进程**的 PID,真正监听端口的是
  #     它 fork 出来的子进程(dll)。只杀父进程会留下"僵尸监听者":
  #     端口仍被占用,下次 start 探测到就误报"已在运行"。
  #
  #     🔑 2026-09-16 修正:原实现在此用 `kill -TERM -$pid`(负号 = 杀整个**进程组**),
  #     而 $pid 读自**陈旧的** pidfile。macOS 会回收复用 PID,
  #     那个号可能已分给 Docker 等无关进程 → 杀进程组时连带误伤。
  #     现在:必须先 is_svc_pid 验明身份,且**只用单个 PID**,绝不用负号。
  if [ -f "$pf" ]; then
    pid="$(cat "$pf" 2>/dev/null)"
    if is_svc_pid "$s" "$pid"; then
      # 只杀单 PID(不用负号,不碰进程组),再逐个终止它的子进程
      for child in $(pgrep -P "$pid" 2>/dev/null); do
        is_svc_pid "$s" "$child" && kill -TERM "$child" 2>/dev/null || true
      done
      kill -TERM "$pid" 2>/dev/null || true
      sleep 1
      kill -KILL "$pid" 2>/dev/null || true
    elif [ -n "$pid" ]; then
      echo "  · pidfile 里的 PID $pid 已不是 $s(可能已被系统复用),忽略不杀"
    fi
    rm -f "$pf"
  fi

  # (2) 按端口兜底 —— 但**必须先验明命令行身份**。
  #     ⚠️ 绝不能"谁占着端口就杀谁":容器转发进程(com.docke/vpnkit)也占端口,
  #     盲杀会打断 Docker 网络栈,导致 Docker Desktop 重启(Forrest 已亲历两次)。
  if [ -n "$port" ]; then
    local h
    for h in $(lsof -ti "tcp:$port" 2>/dev/null || true); do
      if is_svc_pid "$s" "$h"; then
        kill -TERM "$h" 2>/dev/null || true
      else
        echo "  · PID $h 占着 $port 但不是 $s(可能是 Docker 转发),跳过不碰"
      fi
    done
    sleep 1
    for h in $(lsof -ti "tcp:$port" 2>/dev/null || true); do
      is_svc_pid "$s" "$h" && kill -KILL "$h" 2>/dev/null || true
    done
  fi

  echo "  ✓ $s 已停"
}

wait_health() {
  local s="$1" port i
  port="$(get_port "$s")"
  [ -n "$port" ] || { echo "  ⚠ 未知服务 $s"; return 1; }
  # 探测函数:/health/live 为主;万一 health 中间件异常,回落根端点(也返回 200 JSON)
  _probe() {
    curl -sf -o /dev/null "http://127.0.0.1:$port/health/live" 2>/dev/null \
      || curl -sf -o /dev/null "http://127.0.0.1:$port/" 2>/dev/null
  }
  # 先探一次:服务可能上一轮就在跑(幂等),不该白等 3 分钟。
  # 注意措辞 —— 探测通过**只说明端口有应答**,可能是残留的旧进程,
  # 不等于"本次启动的这个服务健康"。start_svc 已在启动前清理残留,
  # 这里再加上"确认是本轮 PID"的判断,避免把旧进程当成新服务。
  if _probe; then
    local cur; cur="$(cat "$(svc_pidfile "$s")" 2>/dev/null)"
    if [ -n "$cur" ] && is_running "$s"; then
      echo "  ✓ $s 就绪 :$port (本次进程 pid $cur)"
    else
      echo "  ✓ $s 就绪 :$port (已在运行)"
    fi
    return 0
  fi

  for i in $(seq 1 90); do
    # 服务进程若已死,不必再等满 —— 立刻报出最后几行日志,便于定位
    if ! is_running "$s"; then
      echo "  ✗ $s 进程已退出 —— 最后 20 行日志:"
      tail -20 "$(svc_log "$s")" 2>/dev/null | sed "s/^/      /"
      return 1
    fi
    if _probe; then
      echo "  ✓ $s 就绪 :$port (等了 $((i*2)) 秒)"; return 0
    fi
    # 每 15 秒报一次进度 + 当前在哪一步(EF 迁移很慢时用户不会以为卡死)
    if [ $((i % 8)) -eq 0 ]; then
      local last
      last="$(tail -1 "$(svc_log "$s")" 2>/dev/null | cut -c1-90)"
      echo "  · $s 启动中… $((i*2))s  ${last:+| $last}"
    fi
    sleep 2
  done
  # ⚠️ 超时**不等于**失败:再确认一次进程是否还活着 + 给出手动验证命令
  if is_running "$s"; then
    echo "  ⚠ $s 180 秒内没就绪,但**进程仍在运行**(可能 EF 首次迁移慢)"
    echo "     手动验证:  curl -s http://127.0.0.1:$port/ ; echo"
    echo "     看日志:    tail -30 $ROOT/.logs/$s.log"
  else
    echo "  ✗ $s 未启动 —— 最后 20 行日志:"
    tail -20 "$(svc_log "$s")" 2>/dev/null | sed "s/^/      /"
  fi
  return 1
}

case "${1:-status}" in
  up)
    echo "▸ 基础设施"
    # 用本机 PG(连接串来自 .env);只探测可达性,不再自起 embedded(见 practice 分支注释)
    node "$ROOT/tools/db/db.js" psql "SELECT 1" 2>&1 | grep -q "SQL 失败" \
      && echo "  ⚠ PG 不可达(.env 指向的库连不上)" || echo "  ✓ PostgreSQL 可达 (来自 .env)"
    "$ROOT/tools/rabbit/rabbit.sh" start >/dev/null 2>&1 && echo "  ✓ RabbitMQ :5672 / :15672" || echo "  ⚠ RabbitMQ 启动异常"
    echo "▸ 微服务"
    for s in identity jobs interviews knowledge assessment analytics gateway; do start_svc "$s"; done
    for s in identity jobs interviews knowledge assessment analytics gateway; do wait_health "$s"; done
    echo "▸ 全部就绪"
    "$0" urls
    ;;
  practice)
    # 口语练习只用到 Assessment(评分/TTS/素材/录音)+ Gateway(前端统一走 /api)。
    # 其余五个服务与本页无关,不起 → 老 Intel 双核 Mac 上内存直接省一半。
    echo "▸ 基础设施"
    # 数据库用**本机已装的 PostgreSQL**(连接串来自 .env,默认 :5432)。
    # 不再自动启动 embedded-postgres(5433) —— 那是"零依赖"备胎,
    # 与本机 PG 并存只会白占内存、且容易查错库(2026-09-15 决定)。
    # 这里只探测 .env 指向的库是否可达。
    if node "$ROOT/tools/db/db.js" psql "SELECT 1" 2>&1 | grep -q "SQL 失败"; then
      echo "  ✗ 连不上 .env 里配置的 PostgreSQL"
      echo "    手动看原因: node tools/db/db.js psql \"SELECT 1\""
      exit 1
    fi
    echo "  ✓ PostgreSQL 可达 (来自 .env)"
    # RabbitMQ:练习页不需要,装了才起,没装跳过(不刷警告)
    if [ -x "$HOME/rabbitmq/rootfs/usr/lib/rabbitmq/bin/rabbitmq-server" ]; then
      if "$ROOT/tools/rabbit/rabbit.sh" status >/dev/null 2>&1 \
         || "$ROOT/tools/rabbit/rabbit.sh" start >/dev/null 2>&1; then
        echo "  ✓ RabbitMQ :5672"
      else
        echo "  · RabbitMQ 未起来(练习页不需要)"
      fi
    else
      echo "  · RabbitMQ 未安装(练习页不需要)"
    fi
    echo "▸ 练习所需微服务"
    # identity 必须起:前端登录要它签 JWT,assessment 的 [Authorize] 认这个 JWT。
    # 其余四个(jobs/interviews/knowledge/analytics)与练习无关,不起。
    for s in identity assessment gateway; do start_svc "$s"; done
    for s in identity assessment gateway; do wait_health "$s"; done
    echo "▸ 就绪 —— 前端: cd web && npm start  然后打开 http://127.0.0.1:4200/practice"
    echo "   Assessment swagger: http://127.0.0.1:5266/swagger"
    ;;
  down)
    for s in jobs identity interviews knowledge assessment analytics gateway; do stop_svc "$s"; done
    "$ROOT/tools/rabbit/rabbit.sh" stop >/dev/null 2>&1 || true
    # 不再停 embedded PG:数据库用的是本机 PostgreSQL(见 practice 分支注释),
    # 停掉它会连累用户其它项目 —— 那不是我们该管的进程。
    echo "  ✓ 全部停止(数据保留)"
    ;;
  restart)
    s="${2:?用法: dev.sh restart <service>}"
    stop_svc "$s"; sleep 2; start_svc "$s"; wait_health "$s"
    ;;
  status)
    node "$ROOT/tools/db/db.js" status 2>/dev/null || echo "[db] stopped"
    "$ROOT/tools/rabbit/rabbit.sh" status 2>/dev/null || echo "[rabbit] stopped"
    for s in identity jobs interviews knowledge assessment analytics gateway; do
      if is_running "$s"; then echo "  ✓ $s (port $(get_port "$s"))"; fi
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
  PostgreSQL      由 .env 配置(默认 127.0.0.1:5432)  yourinterview
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
  *) echo "用法: dev.sh {up|practice|down|restart <svc>|build|status|logs <svc>|e2e [svc]|urls}"; exit 1 ;;
esac
