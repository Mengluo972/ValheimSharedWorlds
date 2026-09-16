#!/usr/bin/env bash
# 一键发版：版本号三处同步 → Release 构建 → Thunderstore 打包 → 源码提交推送。
# 用法：./release.sh <新版本号> [变更摘要]
#   例：./release.sh 1.0.3 "修复手柄导航"
# 要求：CHANGELOG.md 顶部必须已有 `## <新版本号>` 条目（先写变更说明再发版）。
set -euo pipefail
cd "$(dirname "$0")"

VER="${1:?用法: ./release.sh <新版本号> [变更摘要]}"
MSG="${2:-Release $VER}"
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "!! 版本号必须是 X.Y.Z 格式" >&2; exit 1; }

CUR=$(node -e "console.log(require('./manifest.json').version_number)")
if [ "$VER" = "$CUR" ]; then
  echo ">> 版本号保持 $VER（重发当前版本）"
else
  echo ">> 版本号: $CUR -> $VER"
fi

# 1) 三处版本号同步：manifest.json / csproj / 插件常量
NEWVER="$VER" node -e '
const fs = require("fs");
const v = process.env.NEWVER;
let m = fs.readFileSync("manifest.json", "utf8");
if (!/"version_number":\s*"[^"]+"/.test(m)) { console.error("manifest.json 缺少 version_number"); process.exit(1); }
m = m.replace(/("version_number":\s*")[^"]+(")/, "$1" + v + "$2");
fs.writeFileSync("manifest.json", m);
let c = fs.readFileSync("ValheimSaveShare.csproj", "utf8");
if (!/<Version>[^<]+<\/Version>/.test(c)) { console.error("csproj 缺少 <Version>"); process.exit(1); }
c = c.replace(/(<Version>)[^<]+(<\/Version>)/, "$1" + v + "$2");
fs.writeFileSync("ValheimSaveShare.csproj", c);
let p = fs.readFileSync("SaveSharePlugin.cs", "utf8");
if (!/PluginVersion = "[^"]+"/.test(p)) { console.error("SaveSharePlugin.cs 缺少 PluginVersion"); process.exit(1); }
p = p.replace(/(PluginVersion = ")[^"]+(")/, "$1" + v + "$2");
fs.writeFileSync("SaveSharePlugin.cs", p);
'
grep -q "\"version_number\": \"$VER\"" manifest.json || { echo "!! manifest 版本号写入失败" >&2; exit 1; }
grep -q "<Version>$VER</Version>" ValheimSaveShare.csproj || { echo "!! csproj 版本号写入失败" >&2; exit 1; }
grep -q "PluginVersion = \"$VER\"" SaveSharePlugin.cs || { echo "!! PluginVersion 写入失败" >&2; exit 1; }
echo ">> 版本号三处已同步: $VER"

# 2) CHANGELOG 校验：先写变更说明才允许发版
if ! grep -q "^## $VER" CHANGELOG.md; then
  echo "!! CHANGELOG.md 还没有 '## $VER' 条目。" >&2
  echo "   请先在 CHANGELOG.md 顶部补上本版变更说明，然后重跑本脚本。" >&2
  exit 1
fi
echo ">> CHANGELOG 已含 $VER 条目"

# 3) Release 构建 + 打包
echo ">> 构建 Release..."
dotnet build -c Release -v quiet
./build-package.sh

# 4) 源码提交推送（仅在 git 仓库内时）
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git add -A
  if git diff --cached --quiet; then
    echo ">> 源码无新变更，跳过提交"
  else
    git commit -m "Release $VER: $MSG" >/dev/null
    echo ">> 已提交: Release $VER"
    if git remote get-url origin >/dev/null 2>&1; then
      git push
    else
      echo ">> 未配置 origin remote，跳过 push"
    fi
  fi
else
  echo ">> 不是 git 仓库，跳过源码提交"
fi

ZIP="dist/SuperVikingDepartment-ValheimSaveShare-$VER.zip"
[ -f "$ZIP" ] || { echo "!! 打包产物缺失：$ZIP" >&2; exit 1; }

echo
echo ">> 发版完成: $ZIP"
echo ">> 最后一步（人工确认后执行）: 上传该 zip 到 https://thunderstore.io/c/valheim/create/"
echo "   （Team = SuperVikingDepartment，社区 = Valheim）"
