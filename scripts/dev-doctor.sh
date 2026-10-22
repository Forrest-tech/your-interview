#!/usr/bin/env bash
# =============================================================================
# 起后自检(票 93 / B4)
#
# 目的:解决"页面能开但 API 全 500"这类假成功 —— 起完环境必须能一眼看出
#       **哪一个**没活,而不是让人去翻日志猜。
#
# 用法:
#   scripts/dev-doctor.sh                 # 检查默认端口(宿主机进程模式)
#   E2E_API=http://127.0.0.1:5200 scripts/dev-doctor.sh
#
# 退出码:全部通过 = 0;任一必需项失败 = 1(可直接被编排/CI 当门禁用)
# =============================================================================
set -uo pipefail

HOST="${DOCTOR_HOST:-127.0.0.1}"
GW="${DOCTOR_GATEWAY:-http://$HOST:5200}"
WEB="${DOCTOR_WEB:-http://$HOST:4200}"

pass=0
fail=0
warn=0
failed_names=()

# 记一项结果。$1=名称 $2=状态(ok/fail/warn) $3=说明
report() {
  local name="$1" st="$2" detail="$3"
  case "$st" in
    ok)   printf '  \033[32m✔\033[0m %-14s %s\n' "$name" "$detail"; pass=$((pass + 1)) ;;
    warn) printf '  \033[33m!\033[0m %-14s %s\n' "$name" "$detail"; warn=$((warn + 1)) ;;
    *)    printf '  \033[31m✘\033[0m %-14s %s\n' "$name" "$detail";
          fail=$((fail + 1)); failed_names+=("$name") ;;
  esac
}

# TCP 端口是否通(不依赖 nc/pg_isready)
tcp_alive() {
  (echo > "/dev/tcp/$1/$2") >/dev/null 2>&1
}

# HTTP 状态码。连不上时 curl 会因 -w 输出 000 并返回非零退出码 —— 这里刻意
# 不加 `|| echo 000`,否则会和 -w 的输出叠成 "000000"(就是这么踩过一次)。
http_code() {
  local c
  c=$(curl -s -o /dev/null -m 5 -w '%{http_code}' "$1" 2>/dev/null)
  [ -n "$c" ] && echo "$c" || echo '000'
}

echo '== 基础设施 =='

if tcp_alive "$HOST" 5432; then
  report 'PostgreSQL' ok "$HOST:5432 可连接"
else
  report 'PostgreSQL' fail "$HOST:5432 连不上 —— 数据库没起来,后面所有服务都会失败"
fi

if tcp_alive "$HOST" 5672; then
  report 'RabbitMQ' ok "$HOST:5672 可连接"
else
  report 'RabbitMQ' fail "$HOST:5672 连不上"
fi

echo '== 七个微服务 =='

# 端口 → 服务名(与 docker-compose / launchSettings 一致)
for entry in 5262:identity 5263:jobs 5264:interviews 5265:knowledge \
             5266:assessment 5267:analytics 5268:aigateway; do
  port="${entry%%:*}"
  name="${entry##*:}"
  code=$(http_code "http://$HOST:$port/health/live")
  if [ "$code" = '200' ]; then
    report "$name" ok "/health/live 200"
  elif [ "$code" = '000' ]; then
    report "$name" fail "端口 $port 无响应(没起,或还在启动)"
  else
    report "$name" fail "端口 $port 响应 HTTP $code(起了但不健康)"
  fi
done

echo '== 网关与前端 =='

# 网关不用 /health/live 判定:它是聚合健康,会被没起的 OCR 服务拖成 503,
# 即便业务 API 完全可用(详见票 94)。这里改用真实转发探针 ——
# 未带令牌访问受保护端点返回 401 = 网关通了且成功转发到下游。
gw_code=$(http_code "$GW/api/knowledge/review-activity")
case "$gw_code" in
  200|401) report 'Gateway' ok "5200 通,转发已到下游(HTTP $gw_code)" ;;
  000)     report 'Gateway' fail "5200 无响应(网关没起)" ;;
  *)       report 'Gateway' fail "5200 响应 HTTP $gw_code(起了但转发下游失败)" ;;
esac

web_code=$(http_code "$WEB")
if [ "$web_code" = '200' ]; then
  report '前端' ok '4200 可访问'
else
  # 前端不在 compose 里(开发模式在宿主机跑),所以只提示不算必需项的硬失败
  report '前端' fail "4200 响应 HTTP $web_code(前端未跑;docker 模式下本就不在容器里)"
fi

echo '== 可选 =='

ocr_code=$(http_code "http://$HOST:5300/health")
if [ "$ocr_code" = '200' ]; then
  report 'OCR 服务' ok '5300 可访问'
else
  # OCR 是 PaddleOCR 的容器服务(票59),很重;业务 API 不依赖它转发,故不计入失败
  report 'OCR 服务' warn "5300 未起(HTTP $ocr_code)—— 仅影响 OCR 成卡,不影响业务 API"
fi

echo
if [ "$fail" -gt 0 ]; then
  printf '\033[31m自检未通过\033[0m: %d 项通过, \033[31m%d 项失败\033[0m —— %s\n' \
    "$pass" "$fail" "$(IFS=,; echo "${failed_names[*]}")"
  exit 1
fi
printf '\033[32m自检通过\033[0m: %d 项正常(另有 %d 项警告)\n' "$pass" "$warn"
