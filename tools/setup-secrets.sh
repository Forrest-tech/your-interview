#!/usr/bin/env bash
# ============================================================================
#  Your Interview —— 本地密钥配置向导
#
#  做两件事:
#    1. 在仓库根创建 .env(从 .env.example 复制), 提示你填真实密钥
#    2. 在 src/Analysis.Worker/ 创建 appsettings.Development.json
#       (从 .example 复制), 供不开 Docker、直接 dotnet run 时使用
#
#  两个产物都在 .gitignore 里, 不会被提交。
#
#  用法: bash tools/setup-secrets.sh
# ============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "▸ Your Interview 本地密钥配置"
echo

# --- 1. .env(Docker 路线用)-------------------------------------------------
if [ -f .env ]; then
  echo "  · .env 已存在,跳过(如需重建,先删除它)"
else
  cp .env.example .env
  chmod 600 .env
  echo "  ✓ 已创建 .env(权限 600)—— 请编辑它填入真实密钥"
fi

# --- 2. Analysis.Worker 的 appsettings.Development.json(本地 dotnet run 用)--
DEV_JSON="src/Analysis.Worker/appsettings.Development.json"
DEV_EXAMPLE="src/Analysis.Worker/appsettings.Development.json.example"
if [ -f "$DEV_JSON" ]; then
  echo "  · $DEV_JSON 已存在,跳过"
else
  if [ -f "$DEV_EXAMPLE" ]; then
    cp "$DEV_EXAMPLE" "$DEV_JSON"
    echo "  ✓ 已创建 $DEV_JSON —— 请编辑它填入真实密钥与仓库绝对路径"
  else
    echo "  ⚠ 找不到 $DEV_EXAMPLE"
  fi
fi

echo
echo "▸ 接下来要填的密钥(两处保持一致)"
echo
echo "  1) Azure Speech Key"
echo "     Azure 门户 → 你的 Speech 资源 → 密钥和终结点 → 密钥 1"
echo "     填到: .env 的 AZURE_SPEECH_KEY,以及 appsettings.Development.json 的 AzureSpeech.Key"
echo
echo "  2) 服务账号密码(Worker 回写结果时登录 Identity 用)"
echo "     填到: .env 的 SERVICE_ACCOUNT_PASSWORD,以及 appsettings.Development.json 的 ServiceAccount.Password"
echo
echo "  3) JWT 签名密钥(仅 .env;六个服务必须用同一个值,至少 32 字符)"
echo "     生成: openssl rand -base64 48"
echo "     填到: .env 的 JWT_SIGNING_KEY"
echo
echo "  4) 管理员密码(仅 .env;Identity 首次播种时用)"
echo "     填到: .env 的 ADMIN_PASSWORD(登录用 admin@your-interview.local)"
echo
echo "  5) 仓库绝对路径(仅 appsettings.Development.json 需要)"
echo "     填到: Storage.RootDirectory,例如 /Users/<你的用户名>/dev/your-interview"
echo
echo "▸ 验证配置是否生效"
echo "  grep -c REPLACE_WITH .env $DEV_JSON     # 输出都应为 0"
echo
echo "▸ 然后启动"
echo "  docker compose up -d --build            # 全容器(Docker 路线)"
echo "  bash tools/dev.sh up                    # 本机 dotnet run(需装 .NET 8 + PG + RabbitMQ)"
echo
