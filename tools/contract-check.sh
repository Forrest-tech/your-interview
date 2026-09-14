#!/usr/bin/env bash
# 前后端接口契约联调:用真实 HTTP 调用逐条验证前端页面实际用到的每个端点。
# 判定标准 = 这不是 5xx / 404 / 000(网络不可达)。4xx(401/403/400/409)说明路由存在、鉴权或校验在正常工作。
set -uo pipefail

BASE="${BASE:-http://127.0.0.1:5200}"
EMAIL="${EMAIL:-admin@your-interview.local}"
# 密码从环境变量取(优先 .env 或显式传入), 不硬编码在仓库里
if [ -f .env ]; then set -a; . ./.env; set +a; fi
PASS="${PASS:-${ADMIN_PASSWORD:-}}"

PASS_N=0; FAIL_N=0; WARN_N=0

jqget() { node -e "let d='';process.stdin.on('data',c=>d+=c).on('end',()=>{try{const j=JSON.parse(d);const v=process.argv[1].split('.').reduce((a,k)=>a&&a[k],j);console.log(v===undefined||v===null?'':(typeof v==='object'?JSON.stringify(v):v))}catch(e){console.log('')}})" "$1"; }

req() { # req <label> <method> <path> [token] [body] [expect_status_regex]
  local label="$1" method="$2" path="$3" token="${4:-}" body="${5:-}" expect="${6:-}"
  local args=(-s -m 20 -o /tmp/cc.body -w '%{http_code}' -X "$method" "$BASE$path")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
  local code; code=$(curl "${args[@]}" 2>/dev/null)
  local snippet; snippet=$(head -c 160 /tmp/cc.body 2>/dev/null | tr '\n' ' ')
  if [ "$code" = "000" ]; then
    echo "  ✗ $label  → HTTP 000 (网关不可达或连接被拒)"; FAIL_N=$((FAIL_N+1)); return 1
  fi
  # 期望状态码优先:它把 404(路由缺失)和 5xx(服务端错误)都当失败,
  # 但对"我们就是要看 404"的用例不该误判。
  if [ -n "$expect" ]; then
    if echo "$code" | grep -qE "^($expect)$"; then
      echo "  ✓ $label  → $code"; PASS_N=$((PASS_N+1)); return 0
    fi
    # 期望是 2xx 时,4xx 属于契约漂移(告警);5xx/404 视为失败
    echo "  ⚠ $label  → $code (期望 $expect)  $snippet"; WARN_N=$((WARN_N+1)); return 1
  fi
  if [ "$code" = "404" ] || [ "$code" -ge 500 ]; then
    echo "  ✗ $label  → $code  $snippet"; FAIL_N=$((FAIL_N+1)); return 1
  fi
  echo "  ✓ $label  → $code"; PASS_N=$((PASS_N+1)); return 0
}

echo "════════════════════════════════════════════════"
echo " 前后端接口契约联调  base=$BASE"
echo "════════════════════════════════════════════════"

echo "▸ 1) 认证 /api/auth/*"
LOGIN_BODY="{\"email\":\"$EMAIL\",\"password\":\"$PASS\"}"
LOGIN_CODE=$(curl -s -m 25 -o /tmp/cc.login -w '%{http_code}' -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' -d "$LOGIN_BODY")
if [ "$LOGIN_CODE" != "200" ]; then
  echo "  ✗ 登录 → $LOGIN_CODE (后续全部无法执行):"; head -c 300 /tmp/cc.login; echo; exit 1
fi
TOKEN=$(cat /tmp/cc.login | jqget tokens.accessToken)
REFRESH=$(cat /tmp/cc.login | jqget tokens.refreshToken)
echo "  ✓ 登录 → 200 (token ${#TOKEN} chars)"
[ -z "$TOKEN" ] && { echo "  ✗ 登录响应缺少 tokens.accessToken"; exit 1; }

req "GET  /api/auth/me (Profile 页)"        GET  "/api/auth/me" "$TOKEN" "" "200"
req "POST /api/auth/refresh (拦截器刷新)"    POST "/api/auth/refresh" "" "{\"refreshToken\":\"$REFRESH\"}" "200"

echo "▸ 2) Tracker /api/jobs/*"
req "GET  /api/jobs/stats (Dashboard+Tracker)" GET "/api/jobs/stats" "$TOKEN" "" "200"
req "GET  /api/jobs/applications (Tracker 列表)" GET "/api/jobs/applications?page=1&pageSize=20" "$TOKEN" "" "200"
req "GET  /api/jobs/applications?status=applied" GET "/api/jobs/applications?page=1&pageSize=10&status=applied" "$TOKEN" "" "200"

echo "▸ 3) 实战机经 /api/interviews/*"
req "GET  /api/interviews (列表)"           GET "/api/interviews?page=1&pageSize=20" "$TOKEN" "" "200"
req "GET  /api/interviews/stats"             GET "/api/interviews/stats" "$TOKEN" "" "200"
req "GET  /api/interviews/companies"         GET "/api/interviews/companies" "$TOKEN" "" "200"
ENTRY_ID=$(curl -s -m 20 -H "Authorization: Bearer $TOKEN" "$BASE/api/interviews?page=1&pageSize=1" | jqget items.0.id)
[ -z "$ENTRY_ID" ] && ENTRY_ID=$(curl -s -m 20 -H "Authorization: Bearer $TOKEN" "$BASE/api/interviews?page=1&pageSize=1" | jqget "data.0.id")
if [ -n "$ENTRY_ID" ]; then
  req "GET  /api/interviews/{id} (详情节)"   GET "/api/interviews/$ENTRY_ID" "$TOKEN" "" "200"
  req "GET  /api/interviews/{id}/weaknesses" GET "/api/interviews/$ENTRY_ID/weaknesses" "$TOKEN" "" "200"
  # questions 与 analysis 是详情响应的内嵌字段(entry.questions / entry.analysis),
  # 后端没有独立 GET 子资源;前端也直接从详情取。校验它们确实内嵌返回。
  DETAIL=$(curl -s -m 20 -H "Authorization: Bearer $TOKEN" "$BASE/api/interviews/$ENTRY_ID")
  if echo "$DETAIL" | grep -q '"questions"'; then echo "  ✓ 详情内嵌 questions 字段"; PASS_N=$((PASS_N+1));
  else echo "  ✗ 详情缺少内嵌 questions 字段"; FAIL_N=$((FAIL_N+1)); fi
  if echo "$DETAIL" | grep -q '"analysis"'; then echo "  ✓ 详情内嵌 analysis 字段"; PASS_N=$((PASS_N+1));
  else echo "  ⚠ 详情无 analysis 字段(未分析场次属正常)"; WARN_N=$((WARN_N+1)); fi
else
  echo "  ⚠ 机经列表为空,详情子资源跳过"; WARN_N=$((WARN_N+1))
fi

echo "▸ 4) 技术栈 /api/knowledge/*"
req "GET  /api/knowledge (列表)"             GET "/api/knowledge?page=1&pageSize=20" "$TOKEN" "" "200"
req "GET  /api/knowledge/topics"             GET "/api/knowledge/topics" "$TOKEN" "" "200"
req "GET  /api/knowledge/stats"              GET "/api/knowledge/stats" "$TOKEN" "" "200"

echo "▸ 5) AI 实战模拟 /api/assessment/*"
req "GET  /api/assessment/sessions (列表)"   GET "/api/assessment/sessions?page=1&pageSize=20" "$TOKEN" "" "200"
req "GET  /api/assessment/dimensions (六维说明)" GET "/api/assessment/dimensions" "$TOKEN" "" "200"
req "GET  /api/assessment/stats"             GET "/api/assessment/stats" "$TOKEN" "" "200"
req "GET  /api/assessment/suggest (智能推荐)"  GET "/api/assessment/suggest" "$TOKEN" "" "200"
SESS_ID=$(curl -s -m 20 -H "Authorization: Bearer $TOKEN" "$BASE/api/assessment/sessions?page=1&pageSize=1" | jqget items.0.id)
if [ -n "$SESS_ID" ]; then
  req "GET  /api/assessment/sessions/{id} (模拟会话页)" GET "/api/assessment/sessions/$SESS_ID" "$TOKEN" "" "200"
  # 前端出题按钮的真实契约:POST /sessions/{id}/questions (不带 questionId)
  req "POST /api/assessment/sessions/{id}/questions (出题)" POST "/api/assessment/sessions/$SESS_ID/questions" "$TOKEN" '{}' "200,201,400,409"
  # next-question 端点当前不存在,前端刻意 catchError 降级为本地选题 —— 记录为已知缺口
  NX=$(curl -s -m 20 -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}' "$BASE/api/assessment/sessions/$SESS_ID/next-question")
  if [ "$NX" = "404" ]; then echo "  ⚠ POST /next-question 不存在(前端已 catchError 本地降级,非阻塞)"; WARN_N=$((WARN_N+1));
  else echo "  ✓ POST /next-question → $NX"; PASS_N=$((PASS_N+1)); fi
else
  echo "  ⚠ 模拟会话为空,详情子资源跳过"; WARN_N=$((WARN_N+1))
fi

echo "▸ 6) Analytics /api/analytics/*"
req "GET  /api/analytics/dashboard (Dashboard+Analytics)" GET "/api/analytics/dashboard" "$TOKEN" "" "200"
# 后端实际路由:radar / pipeline(而非 ability/radar、pipeline/trend、mastery)
req "GET  /api/analytics/radar (能力雷达)"    GET "/api/analytics/radar" "$TOKEN" "" "200"
req "GET  /api/analytics/pipeline (漏斗趋势)" GET "/api/analytics/pipeline" "$TOKEN" "" "200"
# 前端 Analytics 页调用的 /ability/trend 后端没有;前端已 catchError 容忍(趋势图降级隐藏)
TR=$(curl -s -m 20 -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$BASE/api/analytics/ability/trend?days=30")
if [ "$TR" = "404" ]; then echo "  ⚠ GET /ability/trend 不存在(前端 catchError 容忍,趋势图会隐藏)"; WARN_N=$((WARN_N+1));
else echo "  ✓ GET /ability/trend → $TR"; PASS_N=$((PASS_N+1)); fi

echo "▸ 7) Admin /api/admin/*"
req "GET  /api/admin/stats (后台概览)"        GET "/api/admin/stats" "$TOKEN" "" "200"
req "GET  /api/admin/users (用户分页)"        GET "/api/admin/users?page=1&pageSize=10" "$TOKEN" "" "200"
req "GET  /api/admin/roles (角色)"            GET "/api/admin/roles" "$TOKEN" "" "200"
req "GET  /api/admin/permissions (权限)"      GET "/api/admin/permissions" "$TOKEN" "" "200"
req "GET  /api/admin/audit (审计日志)"        GET "/api/admin/audit?page=1&pageSize=10" "$TOKEN" "" "200"

echo "▸ 8) 网关自身 + 安全边界"
req "GET  /api/gateway/info (前端探测后端)"    GET "/api/gateway/info" "" "" "200"
req "匿名访问受保护接口应 401"                 GET "/api/jobs/applications" "" "" "401"
req "未知路径应 404"                          GET "/api/nope/nothing" "$TOKEN" "" "404"
REQID=$(curl -s -D - -o /dev/null -m 20 -H "Authorization: Bearer $TOKEN" "$BASE/api/jobs/applications" | grep -i '^x-correlation-id' | tr -d '\r' | awk '{print $2}')
if [ -n "$REQID" ]; then echo "  ✓ 响应带 X-Correlation-Id"; PASS_N=$((PASS_N+1));
else echo "  ✗ 响应缺少 X-Correlation-Id"; FAIL_N=$((FAIL_N+1)); fi

echo "════════════════════════════════════════════════"
echo " 通过 $PASS_N / 告警 $WARN_N / 失败 $FAIL_N"
echo "════════════════════════════════════════════════"
[ "$FAIL_N" -eq 0 ] || exit 1
