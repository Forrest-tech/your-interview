#!/usr/bin/env bash
# =============================================================================
# 一条命令起全套(票 93 / B4)
#
#   ./scripts/dev-up.sh
#
# 顺序就是关键 —— 起服务 ≠ 可用。必须:起容器 → 等数据库真能连 → 建 schema →
# 跑迁移 → 最后跑自检报告。少了任何一步,都会得到"页面能开但 API 全 500"这种
# 假成功,而报错信息根本指不到真正的病根上。
# =============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# ---------- 0. 前置条件(早失败,别等到最后才报一堆看不懂的错) ----------
echo '== 0/5 前置条件 =='

if ! docker info >/dev/null 2>&1; then
  echo '✘ Docker 没在跑。请先启动 Docker Desktop,再重跑本脚本。'
  echo '  (这是最常见的坑:Docker 没开时 compose 命令本身才会失败,'
  echo '   而服务起不来时界面只表现为"接口全 500",完全指不到这里。)'
  exit 1
fi
echo '  ✔ Docker 在运行'

if [ ! -f .env ]; then
  if [ -f .env.example ]; then
    echo '  ! 没有 .env —— 已从 .env.example 复制一份,请先填好再重跑。'
    cp .env.example .env
    echo '    必须填:JWT_SIGNING_KEY(六个服务共用同一个值)、PGPASSWORD_LOCAL、'
    echo '           ADMIN_PASSWORD。AI/OCR 相关键留空则以无 AI 模式运行。'
    exit 1
  fi
  echo '  ! 没有 .env,也没有 .env.example —— 继续(全部走 compose 里的默认值)。'
else
  echo '  ✔ .env 已就位'
fi

# ---------- 1. 起容器 ----------
echo '== 1/5 起容器(首次会编译镜像,较慢) =='
docker compose up -d --build || { echo '✘ compose up 失败'; exit 1; }

# ---------- 2. 等数据库真能连(容器 healthy 还不够,要能执行 SQL) ----------
echo '== 2/5 等数据库就绪 =='
PG_USER="${PG_USER:-postgres}"
for i in $(seq 1 60); do
  if docker compose exec -T postgres pg_isready -U "$PG_USER" -d yourinterview >/dev/null 2>&1; then
    echo "  ✔ 数据库就绪(第 ${i} 次探)"
    break
  fi
  if [ "$i" -eq 60 ]; then
    echo '✘ 数据库 60 次探活仍未就绪,后续步骤跳过'
  fi
  sleep 2
done

# ---------- 3. 建 7 个服务的 schema ----------
echo '== 3/5 建 schema =='
docker compose exec -T postgres psql -U "$PG_USER" -d yourinterview -v ON_ERROR_STOP=1 -c '
  CREATE SCHEMA IF NOT EXISTS identity;
  CREATE SCHEMA IF NOT EXISTS jobs;
  CREATE SCHEMA IF NOT EXISTS interviews;
  CREATE SCHEMA IF NOT EXISTS knowledge;
  CREATE SCHEMA IF NOT EXISTS assessment;
  CREATE SCHEMA IF NOT EXISTS analytics;
  CREATE SCHEMA IF NOT EXISTS aigateway;
' >/dev/null 2>&1 && echo '  ✔ 7 个 schema 已就位' || echo '  ! schema 创建未成功(可能数据库未就绪)'

# ---------- 4. 跑迁移 ----------
echo '== 4/5 跑迁移 =='
if ! command -v dotnet >/dev/null 2>&1; then
  echo '  ! 没找到 dotnet,跳过迁移(容器内起的服务会用各自配置连接;'
  echo '    若表还没建,请装 .NET 8 SDK 后重跑本脚本或手动跑 dotnet ef database update)'
else
  export PATH="$PATH:$HOME/.dotnet/tools"
  if ! dotnet ef --version >/dev/null 2>&1; then
    echo '  ! 没找到 dotnet-ef,先装:dotnet tool install --global dotnet-ef'
  else
    CS="Host=127.0.0.1;Port=5432;Database=yourinterview;Username=$PG_USER;Password=${PGPASSWORD_LOCAL:-postgres}"
    while read -r proj schema; do
      if dotnet ef database update --project "src/$proj" \
           --connection "$CS;Search Path=$schema" >/dev/null 2>&1; then
        echo "  ✔ $proj"
      else
        echo "  ! $proj 迁移未成功"
      fi
    done <<'EOF'
Services.Identity identity
Services.Jobs jobs
Services.Interviews interviews
Services.Knowledge knowledge
Services.Assessment assessment
Services.Analytics analytics
Services.AiGateway aigateway
EOF
  fi
fi

# ---------- 5. 起后自检 ----------
echo '== 5/5 起后自检 =='
scripts/dev-doctor.sh
doc_rc=$?

echo
echo '== 访问地址 =='
echo '  前端(需另外起):cd web && npm start   →  http://localhost:4200'
echo '  网关 API:                              http://localhost:5200'
echo '  RabbitMQ 管理台(guest/guest):         http://localhost:15672'
echo '  常用:docker compose logs -f <服务名> / docker compose down'

if [ "$doc_rc" -ne 0 ]; then
  echo
  echo '⚠️ 自检有未通过项 —— 上面已点名列出是哪一个,照着修即可(不要只看"页面开不开")。'
  exit 1
fi
echo
echo '✅ 全套已起且自检通过。'
