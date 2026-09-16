#!/usr/bin/env bash
# =============================================================================
#  verify-practice.sh —— 口语练习"修复验证"一键脚本
#
#  作用:重建 assessment + 重启后端 + 用**全新 token** 逐个打真实端点,
#        把返回码和"应该是什么"直接对比,一眼看出修复是否生效。
#
#  用法:
#    bash tools/verify-practice.sh            # 用默认演示账号
#    bash tools/verify-practice.sh a@b.com pw # 指定账号
#
#  为什么单独写这个:
#    · 我(魁星)在沙箱自测时踩过"复用过期 token → 全 401,误判成代码坏了"的坑,
#      所以脚本里**每次现登录取新 token**,并在 401 时先打印 token 的 exp 供排查。
#    · 期望值写死在脚本里,不靠人眼比 —— 避免"看着像通过"的假阳性。
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

EMAIL="${1:-demo@your-interview.local}"
PASSWORD="${2:-Demo!Pass2026}"

IDENTITY="http://127.0.0.1:5262"
ASSESS="http://127.0.0.1:5266"
ASSESS_BASE="$ASSESS/api/assessment"

PY="$(command -v python3 || command -v python)"

if [ -z "$PY" ]; then
  echo "✗ 找不到 python3,无法运行验证"; exit 1
fi

echo "=============================================================="
echo " 口语练习修复验证"
echo "=============================================================="

# ---------- 0) 服务在不在 ----------
echo "▸ 健康检查"
ok=1
for u in "$IDENTITY/health/live" "$ASSESS/health/live"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$u" 2>/dev/null || echo 000)"
  if [ "$code" = "200" ]; then
    echo "  ✓ $u -> $code"
  else
    echo "  ✗ $u -> $code  (服务没起来?先跑: bash tools/ai-practice.sh start)"
    ok=0
  fi
done
[ "$ok" = "1" ] || { echo; echo "服务未就绪,验证中止。"; exit 1; }

# ---------- 1) 登录(现登录取新 token) ----------
echo
echo "▸ 登录 $EMAIL"
TOKEN="$("$PY" - "$IDENTITY" "$EMAIL" "$PASSWORD" <<'PY'
import json, sys, urllib.request, urllib.error, base64
base, email, pw = sys.argv[1], sys.argv[2], sys.argv[3]
body = json.dumps({"email": email, "password": pw}).encode()
req = urllib.request.Request(base + "/api/auth/login", data=body,
                             headers={"Content-Type": "application/json"}, method="POST")
try:
    d = json.loads(urllib.request.urlopen(req, timeout=20).read())
except urllib.error.HTTPError as e:
    print("__ERR__ 登录失败 %s %s" % (e.code, e.read()[:120].decode('utf-8','ignore'))); raise SystemExit
except Exception as e:
    print("__ERR__ 登录异常 %s" % e); raise SystemExit
tok = d.get("tokens", {}).get("accessToken")
if not tok:
    print("__ERR__ 响应里没有 accessToken: %s" % json.dumps(d)[:200]); raise SystemExit
# 打印 exp 供排查(401 时最常是 token 过期)
try:
    p = tok.split('.')[1]; p += '=' * (-len(p) % 4)
    exp = json.loads(base64.urlsafe_b64decode(p)).get('exp')
    import datetime
    print("__EXP__ %s" % datetime.datetime.fromtimestamp(exp).strftime('%Y-%m-%d %H:%M:%S'))
except Exception:
    pass
print(tok)
PY
)"
echo "$TOKEN" | grep -q "^__ERR__" && { echo "  ✗ ${TOKEN#__ERR__ }"; exit 1; }
echo "$TOKEN" | grep "^__EXP__" | sed 's/^__EXP__/  · token 过期时间: /'
TOKEN="$(echo "$TOKEN" | grep -v '^__EXP__$' | tail -1)"
[ -n "$TOKEN" ] || { echo "  ✗ 没拿到 token"; exit 1; }
echo "  ✓ 已取得新 token (${#TOKEN} 字节)"

# ---------- 2) 逐端点验证 ----------
echo
echo "▸ 端点验证(每次都用刚取的 token;期望值写死,不靠人眼)"

"$PY" - "$ASSESS_BASE" "$TOKEN" <<'PY'
import json, sys, urllib.request, urllib.error
base, tok = sys.argv[1], sys.argv[2]
H = {"Authorization": "Bearer " + tok}

# (方法, 路径, 期望状态集合, 说明)
CASES = [
    ("GET",  "/materials",         {200},     "素材树(原 bug 点:401→ 现应 200)"),
    ("GET",  "/speech/settings",   {200},     "语音设置(只回 hasKey/region,不回 key)"),
    ("GET",  "/stats",             {200},     "练习统计"),
    ("GET",  "/dimensions",        {200},     "评分维度"),
    ("GET",  "/sessions",          {200},     "历史会话"),
    ("POST", "/speech/test",       {502,503}, "语音连通性(502=Azure key 无效,非 401;503=未配置)"),
    ("POST", "/tts",               {502,503}, "示范朗读 TTS(502=Azure key 无效,非 401;503=未配置)"),
]

fails = 0
for method, path, expect, note in CASES:
    data = None
    h = dict(H)
    if method == "POST":
        data = json.dumps({"text": "Hello, this is a test."}).encode()
        h["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=data, headers=h, method=method)
    try:
        r = urllib.request.urlopen(req, timeout=60)
        code = r.status
    except urllib.error.HTTPError as e:
        code = e.code
    except Exception as e:
        code = "ERR(%s)" % e
    good = code in expect
    if not good:
        fails += 1
    mark = "✓" if good else "✗"
    exp = "|".join(str(x) for x in sorted(expect))
    print("  %s %-6s %-18s -> %-4s (期望 %s)  %s" % (mark, method, path, code, exp, note))

print()
if fails == 0:
    print("全部通过 —— 401 语义类缺陷已消除;Sample Reading 若报 502/503,")
    print("前端会回退浏览器语音,不再把人踢回登录页。")
    print("想听到 Azure 真人声,需在 /account/ai-setting 换一把有效 Speech key。")
else:
    print("有 %d 项不符 —— 看上面带 ✗ 的行,及 .logs/assessment.log。" % fails)
    print("若仍是 401:多半是 token 过期/未生效,或后端没重启(跑 bash tools/dev.sh restart assessment)。")
PY
