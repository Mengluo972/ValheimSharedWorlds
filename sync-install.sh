#!/usr/bin/env bash
# 把最新构建的 ValheimSaveShare.dll 同步到所有安装位置。
# 需先退出 Valheim / r2modman（否则 DLL 被占用会失败）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DLL="$ROOT/dist/pkg/plugins/ValheimSaveShare.dll"
[ -f "$DLL" ] || DLL="$ROOT/bin/Release/netstandard2.1/ValheimSaveShare.dll"
[ -f "$DLL" ] || { echo "找不到构建产物，先运行 ./build-package.sh" >&2; exit 1; }

GAME="${VALHEIM_DIR:-$ROOT/..}"
VH="$APPDATA/r2modmanPlus-local/Valheim"
FULL="SuperVikingDepartment-ValheimSaveShare"
VER=$(node -e "console.log(require('./manifest.json').version_number)")

echo ">> 源: $DLL"
echo ">> 版本: $VER"

fail=0

# 1) 游戏根目录（直接用 valheim.exe 启动时用）
if [ -d "$GAME/BepInEx/plugins" ]; then
  if cp "$DLL" "$GAME/BepInEx/plugins/ValheimSaveShare.dll" 2>/dev/null; then
    echo "   [ok] 游戏根目录"
  else
    echo "   [跳过] 游戏根目录被占用（游戏还在运行？）"; fail=1
  fi
fi

# 2) r2modman profile
PROF="$VH/profiles/Default/BepInEx/plugins/$FULL"
if [ -d "$VH/profiles" ]; then
  mkdir -p "$PROF"
  cp "$DLL" "$PROF/ValheimSaveShare.dll"
  for f in manifest.json README.md CHANGELOG.md LICENSE icon.png; do
    [ -f "$ROOT/$f" ] && cp "$ROOT/$f" "$PROF/"
  done
  echo "   [ok] r2modman profile"

  # 3) r2modman cache
  CACHE="$VH/cache/$FULL/$VER"
  rm -rf "$VH/cache/$FULL"
  mkdir -p "$CACHE/plugins"
  cp "$DLL" "$CACHE/plugins/ValheimSaveShare.dll"
  for f in manifest.json README.md CHANGELOG.md LICENSE icon.png; do
    [ -f "$ROOT/$f" ] && cp "$ROOT/$f" "$CACHE/"
  done
  echo "   [ok] r2modman cache ($VER)"
fi

echo
echo ">> 完成。各副本哈希："
for f in "$DLL" "$GAME/BepInEx/plugins/ValheimSaveShare.dll" "$PROF/ValheimSaveShare.dll" "$VH/cache/$FULL/$VER/plugins/ValheimSaveShare.dll"; do
  [ -f "$f" ] && printf '   %s  %s\n' "$(sha256sum "$f" | awk '{print substr($1,1,16)}')" "$f"
done

[ "$fail" = "0" ] || { echo; echo "!! 有位置未能更新，请退出游戏后重跑本脚本。" >&2; exit 1; }
