#!/usr/bin/env python3
"""票241:建 Logto Machine-to-Machine 应用 + 指派 Management API 角色(前置凭证)。

这是审批门/迁移/扫表一切 Management API 调用的前提:没有它,全部调用 401。
流程:
  1. 用一个已有管理权限的 token(首次只能从 Console 里建一个 M2M 应用取得,
     见 docs/logto/SETUP.md —— 脚本无法凭空变出第一个管理凭证)建专用 M2M 应用。
  2. 找 Management API 角色:优先名叫 admin 的 M2M 角色(Logto 内置的管理角色),
     否则若只剩唯一 M2M 角色就用它,否则中止并打印候选清单(人工在 Console 指派)。
  3. 指派角色给新应用,随后用新凭据 client_credentials 取 token 并调一次
     GET /api/users 验收,最后打印 .env 行(只打印到终端,绝不写文件)。

依赖:python3 标准库。用法:
  export LOGTO_ENDPOINT=http://localhost:3001 LOGTO_ADMIN_TOKEN=<Console 建的 M2M token>
  python3 scripts/logto-migrate/setup-m2m.py [--app-name yourinterview-management]
"""
import argparse
import json
import os
import sys
import urllib.request
import urllib.error
import urllib.parse


def fail(msg):
    print(f"[setup-m2m] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def api(endpoint, token, method, path, payload=None):
    req = urllib.request.Request(
        endpoint.rstrip("/") + "/api" + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        return e.code, {"_error": e.read().decode(errors="replace")[:300]}


def main():
    ap = argparse.ArgumentParser(description="建 Logto M2M 应用 + Management API 角色")
    ap.add_argument("--endpoint", default=os.environ.get("LOGTO_ENDPOINT", "http://localhost:3001"))
    ap.add_argument("--app-name", default="yourinterview-management")
    args = ap.parse_args()

    admin_token = os.environ.get("LOGTO_ADMIN_TOKEN", "")
    if not admin_token:
        fail("缺 LOGTO_ADMIN_TOKEN:先在 Console → Applications 建一个临时 M2M 应用并取其 token"
             "(见 docs/logto/SETUP.md),这是唯一需要人工的一步;脚本无法凭空变出第一个管理凭证。")

    status, apps = api(args.endpoint, admin_token, "GET", "/applications?page=1&page_size=100")
    if status != 200:
        fail(f"取应用列表失败 HTTP {status} {apps.get('_error', '')} —— 管理 token 无效或无权")
    if isinstance(apps, dict):
        apps = apps.get("data", [])
    for app in apps:
        if app.get("name") == args.app_name:
            print(f"[setup-m2m] 应用 {args.app_name} 已存在(id={app.get('id')})。"
                  "如需新 secret,请在 Console 里重新生成;脚本不自动轮换已存在的 secret。")
            return

    # 票251:1.45.0 的 POST /api/applications 不返回 secret —— secret 必须事后
    # 经 POST /api/applications/{id}/secrets 另取(或查 DB 行),建应用响应只有 id。
    status, created = api(args.endpoint, admin_token, "POST", "/applications",
                          {"name": args.app_name, "type": "MachineToMachine"})
    if status not in (200, 201):
        fail(f"建 M2M 应用失败 HTTP {status} {created.get('_error', '')}")
    app_id = created.get("id")
    if not app_id:
        fail("建应用响应缺 id")
    status, sec = api(args.endpoint, admin_token, "POST", f"/applications/{app_id}/secrets", {})
    secret = sec.get("secret") if isinstance(sec, dict) else None
    if status not in (200, 201) or not secret:
        fail("建应用后取 secret 失败:请在 Console → 该应用 → Secrets 里取一次"
             "(脚本对已存在应用不自动轮换 secret,重跑只会提示已存在)。")
    print(f"[setup-m2m] 已建 M2M 应用 id={app_id}")

    status, roles = api(args.endpoint, admin_token, "GET", "/roles?type=MachineToMachine&page=1&page_size=100")
    if status != 200:
        fail(f"取 M2M 角色列表失败 HTTP {status}")
    if isinstance(roles, dict):
        roles = roles.get("data", [])
    picked = None
    for r in roles:
        if (r.get("name") or "").lower() == "admin":
            picked = r
            break
    if picked is None and len(roles) == 1:
        picked = roles[0]
    if picked is None:
        print("[setup-m2m] 无法唯一确定 Management API 角色,候选:")
        for r in roles:
            print(f"  - {r.get('id')} name={r.get('name')}")
        fail("请在 Console → Applications → 该 M2M 应用 → Roles 里手工指派含 Management API "
             "权限的角色(带 Logto 图标的角色),然后用新凭据重跑验收步骤(见 SETUP.md)。")
    print(f"[setup-m2m] 指派角色 {picked.get('name')} ({picked.get('id')})")
    status, _ = api(args.endpoint, admin_token, "POST", f"/applications/{app_id}/roles",
                    {"roleIds": [picked["id"]]})
    if status not in (200, 201, 204):
        fail(f"指派角色失败 HTTP {status} —— 请在 Console 里手工指派后重跑验收。")

    # 验收:新凭据 client_credentials 取 token + 调一次 list users。
    body = (
        "grant_type=client_credentials"
        f"&client_id={urllib.parse.quote(app_id)}"
        f"&client_secret={urllib.parse.quote(secret)}"
        # 票251:resource 必须是租户 Management API indicator,不是 {endpoint}/api
        # (1.45.0 对后者回 400 oidc.invalid_target);可用 LOGTO_MGMT_RESOURCE 覆盖。
        f"&resource={urllib.parse.quote(os.environ.get('LOGTO_MGMT_RESOURCE', 'https://default.logto.app/api'))}"
        "&scope=all"
    ).encode()
    req = urllib.request.Request(
        args.endpoint.rstrip("/") + "/oidc/token", data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            new_token = json.load(resp)["access_token"]
    except urllib.error.HTTPError as e:
        fail(f"新凭据取 token 失败 HTTP {e.code} —— 角色指派可能未生效,请在 Console 核对后重跑。")
    status, _ = api(args.endpoint, new_token, "GET", "/users?page=1&page_size=1")
    if status != 200:
        fail(f"新凭据调 Management API 失败 HTTP {status} —— 角色权限不足,请核对角色。")
    print("[setup-m2m] 验收通过:新凭据可调 Management API。把下面两行写进 .env(绝不入库,")
    print("  终端输出用后即清,不要粘进任何聊天记录或工单):")
    print(f"LOGTO_M2M_CLIENT_ID={app_id}")
    print(f"LOGTO_M2M_CLIENT_SECRET={secret}")


if __name__ == "__main__":
    main()
