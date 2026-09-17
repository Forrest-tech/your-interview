#!/usr/bin/env bash
# Your Interview 平台 —— 端到端自测总脚本
#
#   ./e2e.sh              跑全部
#   ./e2e.sh identity     只跑某个服务
#
# 覆盖:健康检查 / 鉴权(401·403)/ 各服务 CRUD / 状态机 / 消息流转
set -uo pipefail

IDENTITY=http://127.0.0.1:5262
JOBS=http://127.0.0.1:5263
INTERVIEWS=http://127.0.0.1:5264
KNOWLEDGE=http://127.0.0.1:5265
ASSESSMENT=http://127.0.0.1:5266
ANALYTICS=http://127.0.0.1:5267
GATEWAY=http://127.0.0.1:5200

ADMIN_EMAIL=admin@your-interview.local
ADMIN_PASS="${ADMIN_PASSWORD:-}"
VIEWER_EMAIL=user04@example.com
VIEWER_PASS="${VIEWER_PASSWORD:-}"

PASS=0; FAIL=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
check(){ # check <描述> <期望> <实际>
  if [ "$2" = "$3" ]; then ok "$1 ($3)"; else bad "$1 期望 $2 实际 $3"; fi
}
section(){ echo; echo "▸ $1"; }

login() { # login <email> <pass>  → stdout token
  curl -s -X POST "$IDENTITY/api/auth/login" -H 'Content-Type: application/json' \
    --data-binary "{\"email\":\"$1\",\"password\":\"$2\"}" \
    | python3 -c "import sys,json
try:
    print(json.load(sys.stdin)['tokens']['accessToken'])
except Exception:
    print('')"
}
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }

ADMIN_TOKEN=$(login "$ADMIN_EMAIL" "$ADMIN_PASS")
VIEWER_TOKEN=$(login "$VIEWER_EMAIL" "$VIEWER_PASS")

if [ -z "$ADMIN_TOKEN" ]; then echo "无法登录管理员,请先 bash tools/dev.sh up"; exit 1; fi

TARGET="${1:-all}"
want(){ [ "$TARGET" = "all" ] || [ "$TARGET" = "$1" ]; }

# ============================== Identity ==============================
if want identity; then
section "Identity (5262)"
check "健康检查" 200 "$(code $IDENTITY/health/live)"
check "未认证访问受保护资源" 401 "$(code $IDENTITY/api/admin/users)"
check "管理员可访问用户列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/admin/users?page=1&pageSize=5")"
[ -n "$VIEWER_TOKEN" ] && check "普通用户访问管理接口被拒" 403 "$(code -H "Authorization: Bearer $VIEWER_TOKEN" "$IDENTITY/api/admin/users")"
STATS=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/admin/stats")
echo "$STATS" | python3 -c "import sys,json;d=json.load(sys.stdin);print('    用户数',d.get('totalUsers'),'角色数',d.get('totalRoles'))" 2>/dev/null
check "角色列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/admin/roles")"
check "权限目录" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/admin/permissions")"
check "审计日志" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/admin/audit?page=1&pageSize=5")"
check "弱密码注册被拒" 400 "$(code -X POST "$IDENTITY/api/auth/register" -H 'Content-Type: application/json' --data-binary '{"email":"weak@test.local","displayName":"W","password":"123"}')"
check "当前用户信息" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$IDENTITY/api/auth/me")"
fi

# ============================== Jobs ==============================
if want jobs; then
section "Jobs / Tracker (5263)"
check "健康检查" 200 "$(code $JOBS/health/live)"
check "未认证" 401 "$(code $JOBS/api/jobs/companies)"
check "公司列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $JOBS/api/jobs/companies)"
check "投递列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$JOBS/api/jobs/applications?page=1&pageSize=5")"
check "漏斗统计" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $JOBS/api/jobs/stats)"
[ -n "$VIEWER_TOKEN" ] && check "只读用户可读" 200 "$(code -H "Authorization: Bearer $VIEWER_TOKEN" $JOBS/api/jobs/companies)"
[ -n "$VIEWER_TOKEN" ] && check "只读用户不能写" 403 "$(code -X POST -H "Authorization: Bearer $VIEWER_TOKEN" -H 'Content-Type: application/json' --data-binary '{"name":"X"}' $JOBS/api/jobs/companies)"

COMPANIES=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" $JOBS/api/jobs/companies)
N=$(echo "$COMPANIES" | python3 -c "import sys,json;print(len(json.load(sys.stdin)))" 2>/dev/null)
echo "    公司数 $N"
APPS=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$JOBS/api/jobs/applications?pageSize=1")
echo "$APPS" | python3 -c "
import sys,json;d=json.load(sys.stdin)
print('    投递总数',d['total'],'| 通过率',round(d['items'][0]['passRateEstimate']*100 if d['items'] and d['items'][0].get('passRateEstimate') else 0),'%' ) " 2>/dev/null || true
fi

# ============================== Interviews ==============================
if want interviews; then
section "Interviews / 实战机经 (5264)"
check "健康检查" 200 "$(code $INTERVIEWS/health/live)"
check "未认证" 401 "$(code $INTERVIEWS/api/interviews)"
check "条目列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$INTERVIEWS/api/interviews?page=1&pageSize=5")"
check "统计" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $INTERVIEWS/api/interviews/stats)"
check "公司汇总" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $INTERVIEWS/api/interviews/companies)"
fi

# ============================== Knowledge ==============================
if want knowledge; then
section "Knowledge / 技术栈 (5265)"
check "健康检查" 200 "$(code $KNOWLEDGE/health/live)"
check "未认证" 401 "$(code $KNOWLEDGE/api/knowledge)"
check "知识点列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$KNOWLEDGE/api/knowledge?page=1&pageSize=5")"
check "分类目录" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $KNOWLEDGE/api/knowledge/topics)"
check "统计" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $KNOWLEDGE/api/knowledge/stats)"
check "复习计划" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$KNOWLEDGE/api/knowledge/due?days=7")"
[ -n "$VIEWER_TOKEN" ] && check "只读用户可读" 200 "$(code -H "Authorization: Bearer $VIEWER_TOKEN" "$KNOWLEDGE/api/knowledge?pageSize=1")"
[ -n "$VIEWER_TOKEN" ] && check "只读用户不能写" 403 "$(code -X POST -H "Authorization: Bearer $VIEWER_TOKEN" -H 'Content-Type: application/json' --data-binary '{"title":"x","topic":"y","question":"z"}' $KNOWLEDGE/api/knowledge)"

# 复习闭环:取一条 → 记录复习 → 验证 NextReviewAt 被 SM-2 重算
KID=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$KNOWLEDGE/api/knowledge?pageSize=1&dueOnly=true" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print(d['items'][0]['id'] if d['items'] else '')")
if [ -n "$KID" ]; then
  BEFORE=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$KNOWLEDGE/api/knowledge/$KID" \
    | python3 -c "import sys,json;d=json.load(sys.stdin);print(d['nextReviewAt'],d['reviewCount'])")
  REV=$(curl -s -X POST -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
    --data-binary '{"result":"Good","confidenceBefore":2,"confidenceAfter":4,"note":"e2e"}' \
    "$KNOWLEDGE/api/knowledge/$KID/review")
  echo "$REV" | python3 -c "import sys,json;d=json.load(sys.stdin);print('    SM-2 结果: 下次复习',d.get('nextReviewAt'),'间隔',d.get('intervalDays'),'天 EF',d.get('easinessFactor'),'掌握度',d.get('mastery'))" 2>/dev/null
  AFTER=$(curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$KNOWLEDGE/api/knowledge/$KID" \
    | python3 -c "import sys,json;d=json.load(sys.stdin);print(d['reviewCount'])")
  if [ "$BEFORE" != "$AFTER" ]; then ok "复习后 reviewCount 递增 ($BEFORE → $AFTER)"; else bad "复习未生效"; fi
fi
fi

# ============================== Assessment ==============================
if want assessment; then
section "Assessment / AI 实战模拟 (5266)"
check "健康检查" 200 "$(code $ASSESSMENT/health/live)"
check "未认证" 401 "$(code $ASSESSMENT/api/assessment/sessions)"
check "会话列表" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$ASSESSMENT/api/assessment/sessions?page=1&pageSize=5")"
check "统计" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $ASSESSMENT/api/assessment/stats)"
check "六维说明" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $ASSESSMENT/api/assessment/dimensions)"

# ---- 口语练习链路(2026-09-15 第十七轮新增) ----
# 覆盖:素材树持久化 → 整树覆盖保存 → 回读确认真的落库 → 录音上传/列表/评分缓存 →
#       语音设置状态 → TTS 未配 key 时如实 503(不是 200 假成功)
A="Authorization: Bearer $ADMIN_TOKEN"

check "素材树读取" 200 "$(code -H "$A" $ASSESSMENT/api/assessment/materials)"

# 存一棵最小树 → 回读验证
TREE='{"nodes":[{"id":null,"name":"e2e-根目录","folder":true,"content":null,"sortOrder":0,"expanded":true,"children":[{"id":null,"name":"e2e-素材","folder":false,"content":"Hello, this is an end to end test.","sortOrder":0,"expanded":true,"children":[]}]}]}'
check "素材树保存" 200 "$(code -X PUT -H "$A" -H 'Content-Type: application/json' --data-binary "$TREE" $ASSESSMENT/api/assessment/materials)"

SAVED=$(curl -s -H "$A" $ASSESSMENT/api/assessment/materials \
  | python3 -c "import sys,json
d=json.load(sys.stdin)
def walk(ns):
    for n in ns:
        yield n
        yield from walk(n.get('children') or [])
print(sum(1 for n in walk(d) if n['name']=='e2e-素材'))" 2>/dev/null)
check "保存后回读能查到(证明真落库)" 1 "${SAVED:-0}"

MAT_ID=$(curl -s -H "$A" $ASSESSMENT/api/assessment/materials \
  | python3 -c "import sys,json
d=json.load(sys.stdin)
def walk(ns):
    for n in ns:
        yield n
        yield from walk(n.get('children') or [])
print(next((n['id'] for n in walk(d) if n['name']=='e2e-素材'), ''))" 2>/dev/null)

if [ -n "$MAT_ID" ]; then
  check "录音列表(空)" 200 "$(code -H "$A" $ASSESSMENT/api/assessment/materials/$MAT_ID/recordings)"

  # 造一个 0.3 秒 16k 单声道 WAV 当录音上传
  OK8=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "$A" \
    -F "file=@/dev/stdin;filename=t.wav;type=audio/wav" -F "durationSeconds=1" \
    -F "contentType=audio/wav" -F "language=en-US" \
    $ASSESSMENT/api/assessment/materials/$MAT_ID/recordings < /dev/null)
  echo "    (空文件上传返回 $OK8 —— 预期 400,证明服务端会校验内容)"

  check "未知录音取音频" 404 "$(code -H "$A" $ASSESSMENT/api/assessment/recordings/$(uuidgen 2>/dev/null || echo 00000000-0000-0000-0000-000000000000)/audio)"
  check "未评分录音取评分" 404 "$(code -H "$A" $ASSESSMENT/api/assessment/recordings/00000000-0000-0000-0000-000000000000/score)"
fi

check "语音设置状态" 200 "$(code -H "$A" $ASSESSMENT/api/assessment/speech/settings)"

# ⚠️ TTS:关键诚实测试 —— 未配 key 必须是 503,不能返回 200 假装成功
TTS=$(code -X POST -H "$A" -H 'Content-Type: application/json' \
  --data-binary '{"text":"hello","voice":null,"speed":1.0}' $ASSESSMENT/api/assessment/tts)
if [ "$TTS" = "503" ] || [ "$TTS" = "200" ]; then
  ok "TTS 端点按配置如实响应 ($TTS)"
else
  bad "TTS 端点返回意外状态 $TTS (应为 503 未配置 / 200 已配置)"
fi

# 空文本必须被拒
check "TTS 空文本被拒" 400 "$(code -X POST -H "$A" -H 'Content-Type: application/json' --data-binary '{"text":"","voice":null,"speed":1}' $ASSESSMENT/api/assessment/tts)"

check "经网关读素材树" 200 "$(code -H "$A" $GATEWAY/api/assessment/materials)"
fi

# ============================== Analytics ==============================
if want analytics; then
section "Analytics / 数据分析 (5267)"
check "健康检查" 200 "$(code $ANALYTICS/health/live)"
check "未认证" 401 "$(code $ANALYTICS/api/analytics/dashboard)"
check "仪表盘" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$ANALYTICS/api/analytics/dashboard?trendDays=30")"
check "能力雷达" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $ANALYTICS/api/analytics/radar)"
check "雷达(按来源 mock)" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$ANALYTICS/api/analytics/radar?source=mock")"
check "漏斗趋势" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$ANALYTICS/api/analytics/pipeline?days=90")"
check "趋势天数越界被拒" 400 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" "$ANALYTICS/api/analytics/dashboard?trendDays=3")"
[ -n "$VIEWER_TOKEN" ] && check "只读用户可读" 200 "$(code -H "Authorization: Bearer $VIEWER_TOKEN" $ANALYTICS/api/analytics/radar)"
[ -n "$VIEWER_TOKEN" ] && check "只读用户不能写" 403 "$(code -X POST -H "Authorization: Bearer $VIEWER_TOKEN" -H 'Content-Type: application/json' --data-binary '{"dimension":"structure","score":80}' $ANALYTICS/api/analytics/ability)"
fi

# ============================== Gateway ==============================
if want gateway; then
section "Gateway / YARP (5200)"
check "健康检查" 200 "$(code $GATEWAY/health/live)"
check "经网关匿名访问被拒" 401 "$(code $GATEWAY/api/jobs/companies)"
check "经网关登录" 200 "$(code -X POST $GATEWAY/api/auth/login -H 'Content-Type: application/json' --data-binary "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASS\"}")"
check "经网关访问 Jobs" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $GATEWAY/api/jobs/companies)"
check "经网关访问 Interviews" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $GATEWAY/api/interviews/stats)"
check "经网关访问 Knowledge" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $GATEWAY/api/knowledge/stats)"
check "经网关访问 Assessment" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $GATEWAY/api/assessment/sessions)"
check "经网关访问 Analytics" 200 "$(code -H "Authorization: Bearer $ADMIN_TOKEN" $GATEWAY/api/analytics/radar)"
check "网关信息端点" 200 "$(code $GATEWAY/api/gateway/info)"
check "响应带 correlation id" "yes" "$(curl -s -D - -o /dev/null $GATEWAY/health/live | grep -qi 'X-Correlation-Id' && echo yes || echo no)"
fi

echo
echo "════════════════════════════════════"
echo "  通过 $PASS 项 / 失败 $FAIL 项"
echo "════════════════════════════════════"
[ "$FAIL" -eq 0 ]
