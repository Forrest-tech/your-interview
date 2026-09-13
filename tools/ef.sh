#!/usr/bin/env bash
# EF Core 迁移工具(封装正确的 DOTNET_ROOT / 无 ICU 环境变量)
#
#   ./ef.sh <service> add <Name>        新增迁移
#   ./ef.sh <service> update            应用到数据库
#   ./ef.sh <service> remove            撤销最后一个迁移
#   ./ef.sh <service> list              列出迁移
#   ./ef.sh <service> script            生成 SQL 脚本
#
# 例:./ef.sh Jobs add InitialCreate
set -euo pipefail
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/../src" && pwd)"

export PATH="/home/node/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="/home/node/.dotnet"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_NOLOGO=1

SERVICE="${1:?用法: ef.sh <service> <command> [args]}"; shift
PROJ="$SRC/Services.$SERVICE"
CTX="$(grep -rl "class .*DbContext" "$PROJ/Infrastructure/Persistence" 2>/dev/null | head -1 | xargs -r grep -o 'class [A-Za-z]*DbContext' | head -1 | cut -d' ' -f2)"

if [ -z "$CTX" ]; then echo "找不到 DbContext(服务 $SERVICE)"; exit 1; fi

CMD="${1:?}"; shift || true
OUT="--output-dir Infrastructure/Persistence/Migrations"

case "$CMD" in
  add)     dotnet ef migrations add "$1" --project "$PROJ" --startup-project "$PROJ" --context "$CTX" $OUT ;;
  update)  dotnet ef database update --project "$PROJ" --startup-project "$PROJ" --context "$CTX" ;;
  remove)  dotnet ef migrations remove --project "$PROJ" --startup-project "$PROJ" --context "$CTX" ;;
  list)    dotnet ef migrations list --project "$PROJ" --startup-project "$PROJ" --context "$CTX" ;;
  script)  dotnet ef migrations script --project "$PROJ" --startup-project "$PROJ" --context "$CTX" ;;
  *) echo "未知命令: $CMD"; exit 1 ;;
esac
