#!/usr/bin/env python3
"""票241:回滚用 Logto-only 用户导出(灰度期在 Logto 新注册、无旧 Identity 账号的用户)。

输出 JSON(不含任何密码哈希,Logto 本来也不导出哈希):
  [{logtoUserId, email, legacyUserId, approval, isSuspended, createdAt}]
其中 legacyUserId 来自 identity.logto_user_mapping 镜像行 —— 回迁时按此 Guid
重建旧账号,灰度期该用户名下产生的业务数据(以该 Guid 为外键)不断链。
用法:
  export LOGTO_ENDPOINT=http://localhost:3001
  export LOGTO_M2M_CLIENT_ID=... LOGTO_M2M_CLIENT_SECRET=...
  export PGHOST=... PGUSER=... PGPASSWORD=...
  python3 scripts/logto-migrate/export-logto-users.py --since 2026-10-28T00:00:00Z \
      --out ./logto-only-users.json
"""
import argparse
import csv
import json
import os
import subprocess
import sys
import urllib.request
import urllib.error
import urllib.parse
from datetime import datetime


def fail(msg):
    print(f"[export-logto-users] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def get_token(endpoint, client_id, client_secret):
    body = (
        "grant_type=client_credentials"
        f"&client_id={urllib.parse.quote(client_id)}"
        f"&client_secret={urllib.parse.quote(client_secret)}"
        f"&resource={urllib.parse.quote(endpoint.rstrip('/') + '/api')}"
        "&scope=all"
    ).encode()
    req = urllib.request.Request(
        endpoint.rstrip("/") + "/oidc/token", data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.load(resp)["access_token"]
    except urllib.error.HTTPError as e:
        fail(f"取 M2M token 失败 HTTP {e.code}")


def main():
    ap = argparse.ArgumentParser(description="导出 Logto-only 用户(回滚回迁用)")
    ap.add_argument("--endpoint", default=os.environ.get("LOGTO_ENDPOINT", "http://localhost:3001"))
    ap.add_argument("--since", default=os.environ.get("LOGTO_GATE_LAUNCHED_AT", ""),
                    help="只导此时间之后创建的用户(默认取 LOGTO_GATE_LAUNCHED_AT,空=全部)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--db", default="yourinterview")
    ap.add_argument("--host", default=os.environ.get("PGHOST"))
    ap.add_argument("--port", default=os.environ.get("PGPORT"))
    ap.add_argument("--user", default=os.environ.get("PGUSER", "postgres"))
    args = ap.parse_args()

    token = get_token(args.endpoint,
                      os.environ.get("LOGTO_M2M_CLIENT_ID", ""),
                      os.environ.get("LOGTO_M2M_CLIENT_SECRET", ""))
    # 镜像行:logtoUserId -> legacyUserId/approval(回迁不断链的关键)。
    cmd = ["psql", "-X", "-A", "-F", ",", "--csv", "-c",
           'SELECT "LogtoUserId", "LegacyUserId", "Approval" FROM identity.logto_user_mapping']
    if args.host:
        cmd += ["-h", args.host]
    if args.port:
        cmd += ["-p", str(args.port)]
    if args.user:
        cmd += ["-U", args.user]
    cmd += ["-d", args.db]
    proc = subprocess.run(cmd, capture_output=True, text=True, env=dict(os.environ))
    if proc.returncode != 0:
        fail(f"读镜像表失败: {proc.stderr.strip()}")
    mirror = {r["LogtoUserId"]: r for r in csv.DictReader(proc.stdout.splitlines())}

    since = None
    if args.since:
        try:
            since = datetime.fromisoformat(args.since.replace("Z", "+00:00"))
        except ValueError:
            fail(f"--since 解析失败: {args.since}")

    users, page = [], 1
    while True:
        req = urllib.request.Request(
            args.endpoint.rstrip("/") + f"/api/users?page={page}&page_size=100",
            headers={"Authorization": f"Bearer {token}"}, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                batch = json.load(resp)
        except urllib.error.HTTPError as e:
            fail(f"取用户列表失败 HTTP {e.code}")
        if isinstance(batch, dict):
            batch = batch.get("data", [])
        if not batch:
            break
        users.extend(batch)
        if len(batch) < 100:
            break
        page += 1

    out = []
    for u in users:
        created_raw = u.get("createdAt") or ""
        try:
            created = datetime.fromisoformat(created_raw.replace("Z", "+00:00")) if created_raw else None
        except ValueError:
            created = None
        if since and created and created < since:
            continue
        row = mirror.get(u["id"], {})
        # Logto-only = 在 Logto 里存在、但旧 users 表里没有对应 Guid 行。
        # 镜像行本身不能判定(门也给新注册写行);判定交给回迁步骤的旧库查重,
        # 这里把全部候选 + 镜像信息都导出,回迁脚本按旧库查重过滤。
        out.append({
            "logtoUserId": u["id"],
            "email": u.get("primaryEmail"),
            "legacyUserId": row.get("LegacyUserId"),
            "approval": row.get("Approval") or (u.get("customData") or {}).get("approval"),
            "isSuspended": u.get("isSuspended", False),
            "createdAt": created_raw,
        })
    with open(args.out, "w") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
    print(f"[export-logto-users] 导出 {len(out)} 条候选 → {args.out}")
    print("[export-logto-users] 回迁时:逐条查旧库 users 表,无对应 Guid 行的才是真 Logto-only,"
          "按 legacyUserId 重建(见 docs/logto/ROLLBACK.md)")


if __name__ == "__main__":
    main()
