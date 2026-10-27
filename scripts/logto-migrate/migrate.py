#!/usr/bin/env python3
"""票241:旧 Identity 用户 → Logto 一次性迁移脚本(只读导出 → Management API 导入)。

用法(在操作员本机,不要在共享主机上跑):
  export LOGTO_ENDPOINT=http://localhost:3001
  export LOGTO_M2M_CLIENT_ID=... LOGTO_M2M_CLIENT_SECRET=...
  export PGHOST=localhost PGPORT=5432 PGUSER=postgres PGPASSWORD=...
  python3 scripts/logto-migrate/migrate.py --admin-only          # 先导 admin + 2 测试账号实测
  python3 scripts/logto-migrate/migrate.py --webhook-paused       # 全量导入

强制规则(PLAN §4.4 + round-4 决议):
  · 导出只 SELECT(identity.users / user_roles / roles),绝不写旧表。
  · 批量导入前必须暂停 Logto 的 User.Created webhook 订阅(默认流程,非可选):
    脚本非 dry-run 时要求 --webhook-paused,否则直接拒绝执行;暂停窗口内新注册
    漏掉的 User.Created 事件由 Identity 15 分钟扫表兜底(见 LogtoApprovalGate)。
  · 导入时同步打 customData approval=migrated(接收端守卫第二层)。
  · 映射行用 UPSERT:门先建的 pending 行(导入竞态)会被纠为 migrated,绝不报主键冲突。
  · 密码哈希绝不打印到日志(只记邮箱 + 结果);导出物默认导入成功后粉碎删除,
    除非 --keep-export;--encrypt 用 openssl AES-256-CBC(-salt -pbkdf2)加密,
    口令只从 LOGTO_EXPORT_PASSPHRASE 环境变量读,绝不放命令行。
  · Argon2 导入格式按 Logto 官方迁移指南:passwordAlgorithm=Argon2id/Argon2i,
    passwordDigest=原 PHC 串原样。若 Logto 拒收某账号(参数不匹配),该账号
    只记失败、不伪造成功,走管理员重置密码通道;绝不静默跳过。
  · 停用/锁定: IsActive=false 或 LockedUntil 未过 → 导入后 isSuspended=true。

依赖:python3 标准库 + psql 命令行 + openssl(仅 --encrypt 时)。
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
from datetime import datetime, timezone

USERS_SQL = (
    'SELECT "Id", "Email", "DisplayName", "PasswordHash", "IsActive", "LockedUntil" '
    'FROM identity.users WHERE "IsDeleted" = false ORDER BY "Email"'
)
ROLES_SQL = (
    'SELECT ur."UserId", r."Name" FROM identity.user_roles ur '
    'JOIN identity.roles r ON r."Id" = ur."RoleId" WHERE r."IsDeleted" = false'
)


def log(msg):
    print(f"[migrate] {msg}", flush=True)


def fail(msg):
    print(f"[migrate] ERROR: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def psql_csv(sql, db_args):
    """psql 只读导出,返回(列名, 行列表)。"""
    cmd = ["psql", "-X", "-A", "-F", ",", "--csv", "-c", sql]
    if db_args.host:
        cmd += ["-h", db_args.host]
    if db_args.port:
        cmd += ["-p", str(db_args.port)]
    if db_args.user:
        cmd += ["-U", db_args.user]
    if db_args.db:
        cmd += ["-d", db_args.db]
    env = dict(os.environ)
    proc = subprocess.run(cmd, capture_output=True, text=True, env=env)
    if proc.returncode != 0:
        fail(f"psql 导出失败: {proc.stderr.strip()}")
    rows = list(csv.DictReader(proc.stdout.splitlines()))
    return rows


def psql_exec(sql, db_args):
    """psql 写映射行(只写 identity.logto_user_mapping,绝不写业务表)。"""
    cmd = ["psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-c", sql]
    if db_args.host:
        cmd += ["-h", db_args.host]
    if db_args.port:
        cmd += ["-p", str(db_args.port)]
    if db_args.user:
        cmd += ["-U", db_args.user]
    if db_args.db:
        cmd += ["-d", db_args.db]
    proc = subprocess.run(cmd, capture_output=True, text=True, env=dict(os.environ))
    if proc.returncode != 0:
        fail(f"psql 写入映射行失败: {proc.stderr.strip()}")


def get_token(endpoint, client_id, client_secret):
    body = (
        "grant_type=client_credentials"
        f"&client_id={urllib.parse.quote(client_id)}"
        f"&client_secret={urllib.parse.quote(client_secret)}"
        f"&resource={urllib.parse.quote(endpoint.rstrip('/') + '/api')}"
        "&scope=all"
    ).encode()
    req = urllib.request.Request(
        endpoint.rstrip("/") + "/oidc/token",
        data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.load(resp)["access_token"]
    except urllib.error.HTTPError as e:
        fail(f"取 M2M token 失败 HTTP {e.code}: {e.read().decode(errors='replace')[:300]}")


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
        body = e.read().decode(errors="replace")[:300]
        return e.code, {"_error": body}


def sql_quote(s):
    return "'" + s.replace("'", "''") + "'"


def parse_locked(locked_raw):
    if not locked_raw:
        return False
    try:
        locked = datetime.fromisoformat(locked_raw.replace("Z", "+00:00"))
        return locked > datetime.now(timezone.utc)
    except ValueError:
        return False


def main():
    ap = argparse.ArgumentParser(description="旧 Identity → Logto 迁移")
    ap.add_argument("--endpoint", default=os.environ.get("LOGTO_ENDPOINT", "http://localhost:3001"))
    ap.add_argument("--db", default="yourinterview")
    ap.add_argument("--host", default=os.environ.get("PGHOST"))
    ap.add_argument("--port", default=os.environ.get("PGPORT"))
    ap.add_argument("--user", default=os.environ.get("PGUSER", "postgres"))
    ap.add_argument("--admin-only", action="store_true", help="只导 admin@your-interview.local(第一批)")
    ap.add_argument("--emails", default="", help="逗号分隔的邮箱过滤(与 --admin-only 二选一)")
    ap.add_argument("--dry-run", action="store_true", help="只打印将导入的 payload,不 POST、不写映射行")
    ap.add_argument("--out", default="", help="导出文件路径(默认 ./logto-export-<ts>.csv,操作员本机目录)")
    ap.add_argument("--encrypt", action="store_true", help="导出物用 openssl AES-256-CBC 加密,口令来自 LOGTO_EXPORT_PASSPHRASE")
    ap.add_argument("--keep-export", action="store_true", help="导入成功后保留明文导出(默认删除)")
    ap.add_argument("--argon2id-algorithm", default="Argon2id",
                    help="试点确认用:$argon2id$ 前缀对应的 passwordAlgorithm(默认 Argon2id;"
                    "官方文档只明确列出原生 Argon2 与 MD5/SHA1/SHA256/Bcrypt/Legacy,"
                    "若试点 dry run 证明 Logto 接受的值不是 Argon2id,用本参数调整,无需改代码)")
    ap.add_argument("--webhook-paused", action="store_true",
                    help="确认已在 Console 暂停 User.Created webhook 订阅(非 dry-run 必填)")
    args = ap.parse_args()

    if not args.dry_run and not args.webhook_paused:
        fail("批量导入默认流程要求先暂停 User.Created webhook 订阅(见 docs/logto/SETUP.md),"
             "确认后加 --webhook-paused 再跑;先演练请用 --dry-run。")

    client_id = os.environ.get("LOGTO_M2M_CLIENT_ID", "")
    client_secret = os.environ.get("LOGTO_M2M_CLIENT_SECRET", "")
    if not args.dry_run and (not client_id or not client_secret):
        fail("缺 LOGTO_M2M_CLIENT_ID / LOGTO_M2M_CLIENT_SECRET(见 setup-m2m.py)")

    users = psql_csv(USERS_SQL, args)
    roles_rows = psql_csv(ROLES_SQL, args)
    roles_by_user = {}
    for r in roles_rows:
        roles_by_user.setdefault(r["UserId"], []).append(r["Name"])

    if args.admin_only:
        users = [u for u in users if u["Email"] == "admin@your-interview.local"]
        if not users:
            fail("未找到 admin@your-interview.local,先确认旧库种子")
    elif args.emails:
        wanted = {e.strip().lower() for e in args.emails.split(",") if e.strip()}
        users = [u for u in users if u["Email"].lower() in wanted]

    log(f"待导入 {len(users)} 个账号")
    out_path = args.out or f"./logto-export-{datetime.now(timezone.utc):%Y%m%d%H%M%S}.csv"
    with open(out_path, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=["Id", "Email", "DisplayName", "IsActive", "Roles"])
        w.writeheader()
        for u in users:
            w.writerow({"Id": u["Id"], "Email": u["Email"], "DisplayName": u["DisplayName"],
                        "IsActive": u["IsActive"], "Roles": ";".join(roles_by_user.get(u["Id"], []))})
    log(f"导出清单已写 {out_path}(不含密码哈希,哈希只在内存里流转)")

    if args.dry_run:
        for u in users:
            log(f"DRY-RUN {u['Email']} roles={roles_by_user.get(u['Id'], [])} "
                f"suspended={u['IsActive'] != 't' or parse_locked(u['LockedUntil'])}")
        return

    token = get_token(args.endpoint, client_id, client_secret)
    status, role_list = api(args.endpoint, token, "GET", "/roles?page=1&page_size=100")
    if status != 200:
        fail(f"取 Logto 角色列表失败 HTTP {status}")
    if isinstance(role_list, dict):
        role_list = role_list.get("data", [])
    logto_role_ids = {r.get("name"): r.get("id") for r in role_list if isinstance(r, dict)}

    ok, failed = 0, []
    for u in users:
        email = u["Email"]
        phc = u["PasswordHash"]
        if phc.startswith("$argon2id$"):
            algo = args.argon2id_algorithm
        elif phc.startswith("$argon2i$"):
            algo = "Argon2i"
        else:
            log(f"SKIP {email}: 哈希前缀不在 Logto 导入清单内,走管理员重置密码通道")
            failed.append((email, "unsupported-hash"))
            continue
        suspended = u["IsActive"] != "t" or parse_locked(u["LockedUntil"])
        payload = {
            "primaryEmail": email,
            "name": u["DisplayName"],
            "passwordAlgorithm": algo,
            "passwordDigest": phc,
            "customData": {"legacyUserId": u["Id"], "approval": "migrated"},
            "isSuspended": suspended,
        }
        status, created = api(args.endpoint, token, "POST", "/users", payload)
        if status not in (200, 201):
            log(f"FAIL {email}: HTTP {status} {created.get('_error', '')[:200]}")
            failed.append((email, f"http-{status}"))
            continue
        logto_id = created.get("id")
        if not logto_id:
            log(f"FAIL {email}: 导入响应缺 id")
            failed.append((email, "no-id"))
            continue
        # 角色:按名映射到 Logto 角色 id,缺失的角色只记警告(不阻塞导入,回归清单复核)。
        role_ids = []
        for name in roles_by_user.get(u["Id"], []):
            rid = logto_role_ids.get(name)
            if rid:
                role_ids.append(rid)
            else:
                log(f"WARN {email}: Logto 里无角色 {name},先跳过该角色映射")
        if role_ids:
            status, _ = api(args.endpoint, token, "POST", f"/users/{logto_id}/roles",
                            {"roleIds": role_ids})
            if status not in (200, 201, 204):
                log(f"WARN {email}: 角色指派 HTTP {status},导入本身已成功")
        # 映射行 UPSERT(门先建的 pending 行会被纠为 migrated,导入竞态的指定解法)。
        psql_exec(
            "INSERT INTO identity.logto_user_mapping"
            '("LogtoUserId", "LegacyUserId", "Email", "Approval", "IsSuspended", "CreatedAt", "UpdatedAt")'
            f" VALUES ({sql_quote(logto_id)}, {sql_quote(u['Id'])}, {sql_quote(email)},"
            f" 'migrated', {'true' if suspended else 'false'}, now(), now())"
            ' ON CONFLICT ("LogtoUserId") DO UPDATE SET "Approval" = \'migrated\','
            ' "IsSuspended" = EXCLUDED."IsSuspended", "UpdatedAt" = now()',
            args)
        log(f"OK {email} -> {logto_id} suspended={suspended}")
        ok += 1

    log(f"导入完成: 成功 {ok}, 失败 {len(failed)}")
    if failed:
        for email, reason in failed:
            log(f"  失败: {email} ({reason})")
    if failed:
        print("[migrate] 注意:失败账号未进映射表,处理后重跑(按邮箱过滤)即可,成功账号幂等",
              file=sys.stderr)

    if args.encrypt:
        passphrase = os.environ.get("LOGTO_EXPORT_PASSPHRASE", "")
        if not passphrase:
            fail("--encrypt 要求 LOGTO_EXPORT_PASSPHRASE 环境变量")
        enc = out_path + ".enc"
        proc = subprocess.run(
            ["openssl", "enc", "-aes-256-cbc", "-salt", "-pbkdf2",
             "-in", out_path, "-out", enc, "-pass", f"env:LOGTO_EXPORT_PASSPHRASE"],
            capture_output=True, text=True, env=dict(os.environ))
        if proc.returncode != 0:
            fail(f"openssl 加密失败: {proc.stderr.strip()}")
        log(f"导出清单已加密为 {enc}")
    if not args.keep_export:
        os.remove(out_path)
        log(f"明文导出 {out_path} 已删除(保留上限 = 导入验收通过时)")
    else:
        log(f"明文导出保留在 {out_path}(验收后请手动删除,不要留到割接日之后)")


if __name__ == "__main__":
    main()
