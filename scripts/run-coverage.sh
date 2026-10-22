#!/usr/bin/env bash
# =============================================================================
# 后端覆盖率脚本(票 28 / ARC-08a 落地,总纲第七节基建)
#
# 机制:
#   - 各 *.Tests.csproj 已接入 coverlet.msbuild(6.0.4,中央版本 Directory.Packages.props):
#     在正常环境可用 `dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover`
#     直接出报告。
#   - 沙盒里 dotnet test 的 vstest↔testhost TCP 通道被 egress 代理污染损坏(全工程定版),
#     故本脚本改用等价通道:coverlet.console 工具包装 xunit console runner 执行测试程序集,
#     coverlet 以进程外插桩采集行覆盖,产出 opencover 报告。测试执行口径与全工程一致
#     (xunit console runner + DOTNET_ROLL_FORWARD=LatestMajor)。
#
# 用法:
#   scripts/run-coverage.sh <TestsProjectDirName> [mergeWithOpencoverFile]
# 例:
#   scripts/run-coverage.sh Services.Jobs.Tests
#   scripts/run-coverage.sh Services.Interviews.Tests /tmp/coverage/merged.opencover.xml
#
# 产物:覆盖报告写入 COVERAGE_OUT(默认 /tmp/coverage/<项目名>.opencover.xml),
#       xunit XML 结果写入同目录 <项目名>.xunit.xml(供逐例耗时核验,防假绿)。
# 跨项目合并:不要用 coverlet 的 --merge-with——各项目分别构建时同一程序集的
#       ModuleHash 会变,merge-with 匹配不上会丢模块(票 28 实测)。改法:每项目各出
#       一份 opencover,再用 scripts/merge-coverage.py 按 (模块,文件,行列) 取并集
#       得到后端整体数字。本脚本第二个参数 [mergeWith] 因此已废弃,仅为兼容保留。
#
# 前置:
#   - .NET SDK 在 PATH(本机:~/workspace/.dotnet),TMPDIR 可写。
#   - coverlet.console 工具:dotnet tool install coverlet.console --version 6.0.4 \
#       --tool-path ~/workspace/.tools/coverlet(可用 COVERLET 环境变量改路径)。
#   - DB 门控套件:先起对应服务跑完迁移建 schema(防假红),且脚本已按定版规程把
#     ~/.nuget/packages/microsoft.extensions.*/8.0.0 全套 dll 拷入测试输出目录(防假绿)。
# =============================================================================
set -euo pipefail

# 本机 SDK 在 ~/workspace/.dotnet(非系统安装):coverlet apphost 与子进程 dotnet 都靠它定位运行时
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/workspace/.dotnet}"
case ":$PATH:" in *":$DOTNET_ROOT:"*) ;; *) PATH="$DOTNET_ROOT:$PATH" ;; esac

PROJ_DIR_NAME="${1:?用法: run-coverage.sh <TestsProjectDirName> [mergeWith]}"
MERGE_WITH="${2:-}"
SRC_DIR="$(cd "$(dirname "$0")/../src" && pwd)"
COVERLET="${COVERLET:-$HOME/workspace/.tools/coverlet/coverlet}"
XUNIT_CONSOLE="$HOME/.nuget/packages/xunit.runner.console/2.9.2/tools/net6.0/xunit.console.dll"
OUT_DIR="${COVERAGE_OUT_DIR:-/tmp/coverage}"
mkdir -p "$OUT_DIR"

PROJ_PATH="$(find "$SRC_DIR" -maxdepth 2 -type d -name "$PROJ_DIR_NAME" | head -1)"
[ -n "$PROJ_PATH" ] || { echo "找不到测试项目目录: $PROJ_DIR_NAME" >&2; exit 1; }
CSPROJ="$(find "$PROJ_PATH" -maxdepth 1 -name '*.csproj' | head -1)"

echo "== build $CSPROJ (Debug, 含 PDB 供 coverlet 定位源码行)"
dotnet build "$CSPROJ" -c Debug --nologo -v q

BIN_DIR="$(dirname "$CSPROJ")/bin/Debug/net8.0"
TEST_DLL="$(find "$BIN_DIR" -maxdepth 1 -name '*.Tests.dll' | head -1)"
[ -n "$TEST_DLL" ] || { echo "测试程序集未找到: $BIN_DIR" >&2; exit 1; }

echo "== 防假绿:拷入 microsoft.extensions.* 8.0.0 全套 dll 到测试输出"
for d in "$HOME"/.nuget/packages/microsoft.extensions.*/8.0.0/lib/net8.0/*.dll; do
  [ -f "$d" ] && cp -f "$d" "$BIN_DIR/" || true
done

REPORT="$OUT_DIR/${PROJ_DIR_NAME}.opencover.xml"
XUNIT_XML="$OUT_DIR/${PROJ_DIR_NAME}.xunit.xml"

MERGE_ARG=()
if [ -n "$MERGE_WITH" ]; then echo "警告: mergeWith 已废弃(见脚本头注),忽略: $MERGE_WITH" >&2; fi

echo "== coverlet + xunit console runner: $TEST_DLL"
DOTNET_ROLL_FORWARD=LatestMajor "$COVERLET" "$TEST_DLL" \
  --target "dotnet" \
  --targetargs "exec $XUNIT_CONSOLE $TEST_DLL -xml $XUNIT_XML" \
  --format opencover \
  --output "$REPORT" \
  "${MERGE_ARG[@]}"

echo "覆盖报告: $REPORT"
echo "xunit 结果: $XUNIT_XML"
