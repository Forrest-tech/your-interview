#!/usr/bin/env bash
# Your Interview —— 混合开发模式(容器: identity + gateway / 宿主机: assessment)
#
#   为什么这样分(2026-09-15 Forrest 定):
#     assessment 是唯一在开发的服务 → 留宿主机,dotnet run 直跑,改代码即时生效、可断点;
#     identity / gateway 稳定且不常改 → 进容器,进程管理交给 Docker,不再有孤儿进程;
#     jobs / interviews / knowledge / analytics 与练习无关 → 一个都不起。
#     数据库用**你本机原生 PostgreSQL 5432**(已有图形客户端,数据不分裂)。
#
#   ./tools/hybrid.sh up        起容器(identity+gateway)+ 宿主机 assessment + 前端
#   ./tools/hybrid.sh down      全停(容器 down + 宿主机进程 + 前端)
#   ./tools/hybrid.sh status    看状态
#   ./tools/hybrid.sh logs      跟容器日志
#   ./tools/hybrid.sh rebuild   重新构建镜像(改了 identity/gateway 代码后)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE="docker compose -f $ROOT/docker-compose.hybrid.yml"
LOGDIR="$ROOT/.logs"
mkdir -p "$LOGDIR"

c_green() { printf '  \033[32m✓\033[0m %s\n' "$1"; }
c_red()   { printf '  \033[31m✗\033[0m %s\n' "$1"; }
c_dim()   { printf '  · %s\n' "$1"; }

require_docker() {
  if ! docker info >/dev/null 2>&1; then
    c_red "Docker 没在运行 —— 请先启动 Docker Desktop"
    exit 1
  fi
}

load_env() {
  if [ -f "$ROOT/.env" ]; then
    set -a; . "$ROOT/.env"; set +a
    c_green "已载入 .env"
  else
    c_red "缺少 .env —— 请先从 .env.example 复制并填值"
    exit 1
  fi

  # 🔑 .env 里的键名是 JWT_SIGNING_KEY,而服务读的配置路径是 Jwt:SigningKey。
  #    `set -a; . .env` 只把 JWT_SIGNING_KEY 放进环境,**服务看不懂这个名字** ——
  #    结果服务回落到 appsettings.json 里的占位符 REPLACE_WITH_SIGNING_KEY_AT_LEAST_32_CHARS。
  #    混合模式下这是致命的:容器 identity 拿到真 key 签令牌,宿主机 assessment
  #    用占位符验签 → 必然 401。所以这里显式映射一次。
  if [ -n "${JWT_SIGNING_KEY:-}" ]; then
    export Jwt__SigningKey="$JWT_SIGNING_KEY"
    c_dim "JWT 签名密钥已映射为 Jwt__SigningKey(与容器内一致)"
  else
    c_red "警告:.env 里没有 JWT_SIGNING_KEY —— 容器与宿主机可能用不同的签名密钥,登录会 401"
  fi

  # 🔑 数据库密码同样必须显式映射 —— 2026-09-16 第二轮踩坑。
  #
  #    症状:在"AI 语音设置"页保存 key → 报
  #      Npgsql.PostgresException 28P01: password authentication failed for user "postgres"
  #
  #    根因:各服务 appsettings.json 里写的是**占位符密码** (Password=postgres),
  #    真实密码只存在 .env 的 PGPASSWORD_LOCAL。
  #      · 容器 identity 没事 —— docker-compose.hybrid.yml:46 显式注入了
  #        ConnectionStrings__Identity(内含 ${PGPASSWORD_LOCAL});
  #      · 宿主机 assessment 报错 —— 它直接读 appsettings.json,拿到占位符 → 认证失败。
  #
  #    所以这里把 .env 里已备好的 ConnectionStrings__AssessmentDb 显式导出。
  #    该键在 .env 里已存在且带真实密码(2026-09-16 核验),直接透传给子进程即可。
  if [ -n "${ConnectionStrings__AssessmentDb:-}" ]; then
    export ConnectionStrings__AssessmentDb
    c_dim "DB 连接串已映射为 ConnectionStrings__AssessmentDb(宿主机 assessment 用)"
  else
    c_red "警告:.env 里缺 ConnectionStrings__AssessmentDb —— 宿主机 assessment 会回落到"
    c_red "      appsettings.json 的占位符密码,保存语音设置会报 28P01 认证失败"
  fi
}

# ---------- 宿主机 assessment ----------
A_PORT=5266
a_pidfile() { echo "$LOGDIR/assessment.pid"; }

a_running() {
  local pid; pid="$(cat "$(a_pidfile)" 2>/dev/null || true)"
  [ -n "${pid:-}" ] && kill -0 "$pid" 2>/dev/null
}

port_busy() { lsof -ti "tcp:$1" >/dev/null 2>&1; }

start_assessment() {
  if a_running && port_busy "$A_PORT"; then
    c_dim "assessment 已在运行 (pid $(cat "$(a_pidfile)"))"
    return 0
  fi
  # 残留清理:pidfile 没进程但端口被占 → 上一轮没杀干净
  if port_busy "$A_PORT"; then
    c_dim "assessment 端口 $A_PORT 被残留进程占用,先清理"
    stop_assessment
  fi

  local dotnet_bin="${DOTNET_BIN:-dotnet}"
  if ! command -v "$dotnet_bin" >/dev/null 2>&1; then
    c_red "找不到 dotnet —— 请安装 .NET 8 SDK"
    return 1
  fi

  c_dim "assessment 启动中 (port $A_PORT) …"
  # ⚠️ macOS 没有 setsid → 探测后回退 nohup
  # ⚠️ --no-launch-profile:项目 launchSettings 的 applicationUrl/launchBrowser 会跟 --urls 打架
  # ⚠️ 必须显式把 DB 连接串与 JWT 密钥传给子进程(见 load_env 里的说明):
  #    子 shell 里重新 export 一次,避免依赖父环境是否已被 set -a 污染。
  #    否则 assessment 回落到 appsettings.json 的占位符密码 → 28P01 认证失败,
  #    表现为"在设置页保存语音 key 时报保存失败"。
  if command -v setsid >/dev/null 2>&1; then
    ( cd "$ROOT" && \
      env "ConnectionStrings__AssessmentDb=${ConnectionStrings__AssessmentDb:-}" \
          "Jwt__SigningKey=${Jwt__SigningKey:-}${JWT_SIGNING_KEY:-}" \
      setsid "$dotnet_bin" run --project src/Services.Assessment \
        --no-build --no-launch-profile --urls "http://127.0.0.1:$A_PORT" \
        >"$LOGDIR/assessment.log" 2>&1 & echo $! > "$(a_pidfile)" )
  else
    ( cd "$ROOT" && \
      env "ConnectionStrings__AssessmentDb=${ConnectionStrings__AssessmentDb:-}" \
          "Jwt__SigningKey=${Jwt__SigningKey:-}${JWT_SIGNING_KEY:-}" \
      nohup "$dotnet_bin" run --project src/Services.Assessment \
        --no-build --no-launch-profile --urls "http://127.0.0.1:$A_PORT" \
        >"$LOGDIR/assessment.log" 2>&1 & echo $! > "$(a_pidfile)" )
  fi

  # 等待就绪:只要**能拿到 HTTP 状态码**就算监听成功(不要求 2xx)
  local i code
  for i in $(seq 1 90); do
    if ! a_running; then
      c_red "assessment 进程已退出 —— 最后 20 行日志:"
      tail -20 "$LOGDIR/assessment.log" 2>/dev/null | sed 's/^/      /'
      return 1
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 \
              "http://127.0.0.1:$A_PORT/health/live" 2>/dev/null)"
    if [ -n "$code" ] && [ "$code" != "000" ]; then
      c_green "assessment 就绪 :$A_PORT (本次进程 pid $(cat "$(a_pidfile)"))"
      return 0
    fi
    sleep 2
  done
  c_red "assessment 90 秒未就绪 —— 日志: tail -20 $LOGDIR/assessment.log"
  return 1
}

stop_pidfile_svc() {
  local name="$1" pf="$2" port="$3" pid
  pid="$(cat "$pf" 2>/dev/null || true)"
  if [ -n "${pid:-}" ]; then
    kill -TERM -"$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
    kill -TERM "$pid" 2>/dev/null
    local kid
    kid="$(pgrep -P "$pid" 2>/dev/null || true)"
    [ -n "$kid" ] && kill -TERM $kid 2>/dev/null
    sleep 1
    kill -KILL -"$pid" 2>/dev/null
    kill -KILL "$pid" 2>/dev/null
    [ -n "$kid" ] && kill -KILL $kid 2>/dev/null
  fi
  # 端口兜底:谁占着端口谁就是要停的(dotnet run 会 fork 子进程,光杀父不够)
  if [ -n "$port" ]; then
    local owners
    owners="$(lsof -ti "tcp:$port" 2>/dev/null || true)"
    if [ -n "$owners" ]; then
      kill -TERM $owners 2>/dev/null; sleep 1; kill -KILL $owners 2>/dev/null
    fi
  fi
  rm -f "$pf"
  if [ -n "$port" ] && lsof -ti "tcp:$port" >/dev/null 2>&1; then
    c_red "$name 已停(端口 $port 仍被占用,需手动查: lsof -i tcp:$port)"
  else
    c_green "$name 已停"
  fi
}

stop_web() {
  local pf="$LOGDIR/web.pid" pid
  pid="$(cat "$pf" 2>/dev/null || true)"
  [ -n "${pid:-}" ] && { kill -TERM -"$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null; }
  local owners; owners="$(lsof -ti tcp:4200 2>/dev/null || true)"
  [ -n "$owners" ] && { kill -TERM $owners 2>/dev/null; sleep 1; kill -KILL $owners 2>/dev/null; }
  rm -f "$pf"
  c_green "网页已停"
}

start_web() {
  if [ ! -d "$ROOT/web/node_modules" ]; then
    c_red "web/node_modules 不存在,请先: cd web && npm install"
    return 1
  fi
  c_dim "网页编译中(首次 20-40 秒)…"
  if command -v setsid >/dev/null 2>&1; then
    ( cd "$ROOT/web" && setsid npm start -- --host 127.0.0.1 >"$LOGDIR/web.log" 2>&1 & echo $! > "$LOGDIR/web.pid" )
  else
    ( cd "$ROOT/web" && nohup npm start -- --host 127.0.0.1 >"$LOGDIR/web.log" 2>&1 & echo $! > "$LOGDIR/web.pid" )
  fi
  local i
  for i in $(seq 1 60); do
    if lsof -ti tcp:4200 >/dev/null 2>&1; then c_green "网页就绪"; return 0; fi
    sleep 2
  done
  c_red "网页未就绪 —— 日志: tail -20 $LOGDIR/web.log"
  return 1
}

if [ $# -eq 0 ]; then
  sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
fi

case "${1:-status}" in
  up)
    require_docker
    load_env

    echo "▸ 数据库(你本机原生 PostgreSQL,不由本脚本启动)"
    if nc -z 127.0.0.1 5432 2>/dev/null || lsof -ti tcp:5432 >/dev/null 2>&1; then
      c_green "PostgreSQL 可达 (127.0.0.1:5432)"
    else
      c_red "PostgreSQL 5432 连不上 —— 请先启动你本机的 PostgreSQL"
      exit 1
    fi

    echo "▸ 容器(identity + gateway)"
    $COMPOSE up -d --build || exit 1
    # 等网关就绪(注意:这段在 case 顶层,不能用 local)
    i=0; code=""
    for i in $(seq 1 60); do
      code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:5200/health/live 2>/dev/null)"
      if [ "$code" = "200" ]; then c_green "gateway 就绪 :5200 (health/live 200)"; break; fi
      sleep 2
    done
    [ "$code" = "200" ] || c_dim "gateway 暂未返回 200(当前 $code)—— 看日志: $0 logs"

    echo "▸ 宿主机(assessment —— 你调试的那个)"
    start_assessment || exit 1

    echo "▸ 网页"
    start_web || true

    echo
    echo "  ┌──────────────────────────────────────────────┐"
    echo "  │  打开:  http://127.0.0.1:4200/practice        │"
    echo "  │  停止:  bash tools/hybrid.sh down             │"
    echo "  └──────────────────────────────────────────────┘"
    echo
    echo "  改 assessment 代码 → 重新编译并重启它:"
    echo "    dotnet build src/Services.Assessment && bash tools/hybrid.sh restart-assessment"
    echo
    echo "  日志: .logs/assessment.log(宿主机) / $0 logs(容器)"
    ;;
  down)
    stop_web
    stop_pidfile_svc "assessment" "$LOGDIR/assessment.pid" "$A_PORT"
    if docker info >/dev/null 2>&1; then
      $COMPOSE down
      c_green "容器已停(数据保留)"
    else
      c_dim "Docker 未运行,跳过容器停止"
    fi
    ;;
  restart-assessment)
    stop_pidfile_svc "assessment" "$LOGDIR/assessment.pid" "$A_PORT"
    start_assessment
    ;;
  status)
    echo "▸ 容器"
    if docker info >/dev/null 2>&1; then $COMPOSE ps; else c_dim "Docker 未运行"; fi
    echo "▸ 宿主机"
    a_running && c_green "assessment 在跑 (pid $(cat "$(a_pidfile)"))" || c_dim "assessment 未运行"
    lsof -ti tcp:4200 >/dev/null 2>&1 && c_green "网页在跑 :4200" || c_dim "网页未运行"
    echo "▸ 连通性"
    for u in "http://127.0.0.1:5200/health/live|gateway" \
             "http://127.0.0.1:5266/health/live|assessment" \
             "http://127.0.0.1:4200|web"; do
      url="${u%%|*}"; nm="${u##*|}"
      code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$url" 2>/dev/null)"
      [ -n "$code" ] && [ "$code" != "000" ] && c_green "$nm $code" || c_red "$nm 不可达"
    done
    ;;
  logs)
    require_docker
    $COMPOSE logs -f --tail 100
    ;;
  rebuild)
    require_docker
    load_env
    $COMPOSE up -d --build
    c_green "镜像已重建并重启"
    ;;
  *)
    sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
    ;;
esac
