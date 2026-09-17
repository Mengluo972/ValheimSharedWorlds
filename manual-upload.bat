@echo off
rem ============================================================
rem  Valheim 手动共享存档批处理（不依赖游戏内上传）
rem  用法：把本文件放到任意位置双击运行，按提示操作。
rem  前提：Windows 10/11 自带的 curl，无需安装任何东西。
rem  说明：本脚本直接读取游戏目录下的 ValheimSaveShare 配置
rem        （Repo / Branch / Token / PathPrefix），每次运行都
rem        重新读文件，改完 cfg 立即生效，无需重启游戏。
rem ============================================================
setlocal enabledelayedexpansion
chcp 65001 >nul
title Valheim 手动共享存档

rem ---- 定位游戏目录（本脚本假定放在游戏根目录或其子目录运行） ----
set "GAMEDIR=%~dp0"
:findgame
if exist "%GAMEDIR%valheim.exe" goto foundgame
if "%GAMEDIR:~-2%"=="\\" goto nogame
for %%i in ("%GAMEDIR:~0,-1%") do set "PARENT=%%~dpi"
set "GAMEDIR=%PARENT%"
goto findgame
:foundgame
set "CFG=%GAMEDIR%BepInEx\config\SuperVikingDepartment.ValheimSaveShare.cfg"
if not exist "%CFG%" (
  echo [错误] 找不到配置文件: %CFG%
  echo 请确认本脚本放在 Valheim 游戏目录（或其子目录）里再运行。
  pause
  exit /b 1
)

rem ---- 从 cfg 读取配置 ----
set "REPO="
set "BRANCH="
set "TOKEN="
set "PREFIX=worlds"
for /f "usebackq tokens=1,* delims==" %%a in ("%CFG%") do (
  set "key=%%a"
  set "val=%%b"
  if "!key:~0,5!"=="Repo " set "REPO=%%b"
  if "!key:~0,7!"=="Branch " set "BRANCH=%%b"
  if "!key:~0,6!"=="Token " set "TOKEN=%%b"
  if "!key:~0,11!"=="PathPrefix " set "PREFIX=%%b"
)
rem 去掉值前后的空格
:trim_repo
if "%REPO:~0,1%"==" " set "REPO=%REPO:~1%" & goto trim_repo
:trim_branch
if defined BRANCH if "%BRANCH:~0,1%"==" " set "BRANCH=%BRANCH:~1%" & goto trim_branch
:trim_token
if "%TOKEN:~0,1%"==" " set "TOKEN=%TOKEN:~1%" & goto trim_token
:trim_prefix
if "%PREFIX:~0,1%"==" " set "PREFIX=%PREFIX:~1%" & goto trim_prefix

echo ============================================
echo   Valheim 手动共享存档
echo ============================================
echo 读取到的配置:
echo   仓库 Repo   : %REPO%
echo   分支 Branch : %BRANCH% (空=自动用默认分支)
echo   Token      : 已读取 (github_pat_ 开头: %TOKEN:~0,12%...)
echo   路径前缀    : %PREFIX%
echo.
if "%REPO%"=="" echo [警告] cfg 里没有配置 Repo
if "%TOKEN%"=="" echo [警告] cfg 里没有配置 Token（上传必需，请编辑 cfg 后重跑本脚本）
if "%REPO%"=="" goto end
if "%TOKEN%"=="" goto end

rem ---- 选择世界文件夹 ----
echo 请输入要共享的世界名称（就是游戏里显示的世界名，支持中文）：
set /p WORLD=世界名: 
if "%WORLD%"=="" echo [错误] 世界名不能为空 & goto end

rem ---- 读取存档文件（支持本地与云同步到本地的 worlds 目录） ----
set "SAVEDATA=%USERPROFILE%\AppData\LocalLow\IronGate\Valheim"
set "WORLDDIR="
if exist "%SAVEDATA%\worlds_local\%WORLD%" set "WORLDDIR=%SAVEDATA%\worlds_local\%WORLD%"
if not defined WORLDDIR if exist "%SAVEDATA%\worlds\%WORLD%" set "WORLDDIR=%SAVEDATA%\worlds\%WORLD%"
if not defined WORLDDIR (
  echo [错误] 在以下位置都没有找到世界 "%WORLD%" 的存档文件夹：
  echo   %SAVEDATA%\worlds_local\%WORLD%
  echo   %SAVEDATA%\worlds\%WORLD%
  echo 注意：存档文件夹里必须已有存档文件（_main.*.fwl2 等）。
  echo 可用世界列表（worlds_local 下）：
  dir /b "%SAVEDATA%\worlds_local" 2>nul
  echo 可用世界列表（worlds 下）：
  dir /b "%SAVEDATA%\worlds" 2>nul
  goto end
)
echo.
echo 找到存档文件夹: %WORLDDIR%
dir /b "%WORLDDIR%"
echo.

rem ---- 确认并打包 ----
echo 将把上述文件打包上传到 GitHub 仓库 %REPO% 的 %PREFIX%/%WORLD%/ 目录。
set /p CONFIRM=确认上传? (y/n): 
if /i not "%CONFIRM%"=="y" echo 已取消。 & goto end

set "ZIP=%TEMP%\valheim_share_%RANDOM%.zip"
echo 正在打包...
powershell -NoProfile -Command "Compress-Archive -Path '%WORLDDIR:\=\\%\*' -DestinationPath '%ZIP:\=\\%' -Force"
if not exist "%ZIP%" echo [错误] 打包失败。 & goto end
for %%z in ("%ZIP%") do set "SIZE=%%~zz"
echo 打包完成: !SIZE! 字节
if !SIZE! GTR 94000000 (
  echo [错误] 打包后超过 90 MB，GitHub 不支持直接上传这么大的文件。
  del "%ZIP%"
  goto end
)

rem ---- 解析分支：未配置则查询默认分支 ----
if "%BRANCH%"=="" (
  echo 正在查询仓库默认分支...
  curl -s -H "Authorization: Bearer %TOKEN%" -H "User-Agent: ValheimSaveShare" "https://api.github.com/repos/%REPO%" -o "%TEMP%\vs_repo.json"
  powershell -NoProfile -Command "try{ (Get-Content '%TEMP:\=\\%\vs_repo.json' -Raw | ConvertFrom-Json).default_branch | Set-Content -Path '%ZIP:\=\\%.branch' }catch{ 'main' | Set-Content -Path '%ZIP:\=\\%.branch' }"
  set /p BRANCH=<"%ZIP%.branch"
  del "%ZIP%.branch" 2>nul
)
echo 目标分支: %BRANCH%

rem ---- 上传 world.zip ----
echo 正在上传 world.zip（大文件可能需要几分钟，请勿关闭本窗口）...
powershell -NoProfile -Command "$c=[IO.File]::ReadAllBytes('%ZIP:\=\\%'); $b=[Convert]::ToBase64String($c); @{message='ValheimSaveShare: update world \"%WORLD%\"';branch='%BRANCH%';content=$b} | ConvertTo-Json -Depth 3 | Set-Content -Path '%ZIP:\=\\%.b64' -Encoding UTF8"
curl -s -X PUT -H "Authorization: Bearer %TOKEN%" -H "User-Agent: ValheimSaveShare" -H "Content-Type: application/json" --data-binary "@%ZIP%.b64" "https://api.github.com/repos/%REPO%/contents/%PREFIX%/%WORLD%/world.zip" -o "%TEMP%\vs_up.json" -w "HTTP %%{http_code}\n"
del "%ZIP%.b64" 2>nul
findstr /c:"commit" "%TEMP%\vs_up.json" >nul 2>&1
if errorlevel 1 (
  echo [错误] world.zip 上传失败，GitHub 返回：
  type "%TEMP%\vs_up.json"
  del "%ZIP%" 2>nul
  goto end
)
echo world.zip 上传成功!

rem ---- 上传 manifest.json ----
echo 正在上传 manifest.json...
powershell -NoProfile -Command "$files = Get-ChildItem -File '%WORLDDIR:\=\\%' | ForEach-Object { @{path=$_.Name; size=$_.Length} }; $m = @{name='%WORLD%'; gameVersion=''; savedAt=(Get-Date -Format s); tool='ValheimSaveShare manual-upload'; format='zip'; files=$files}; $m | ConvertTo-Json -Depth 3 | Set-Content -Path '%ZIP:\=\\%.manifest' -Encoding UTF8"
powershell -NoProfile -Command "$b=[Convert]::ToBase64String([IO.File]::ReadAllBytes('%ZIP:\=\\%.manifest')); @{message='ValheimSaveShare: update manifest for \"%WORLD%\"';branch='%BRANCH%';content=$b} | ConvertTo-Json | Set-Content -Path '%ZIP:\=\\%.mreq' -Encoding UTF8"
curl -s -X PUT -H "Authorization: Bearer %TOKEN%" -H "User-Agent: ValheimSaveShare" -H "Content-Type: application/json" --data-binary "@%ZIP%.mreq" "https://api.github.com/repos/%REPO%/contents/%PREFIX%/%WORLD%/manifest.json" -o "%TEMP%\vs_mf.json" -w "HTTP %%{http_code}\n"
del "%ZIP%.manifest" 2>nul & del "%ZIP%.mreq" 2>nul & del "%ZIP%" 2>nul
findstr /c:"commit" "%TEMP%\vs_mf.json" >nul 2>&1
if errorlevel 1 (
  echo [警告] manifest 上传失败（存档本身已上传成功，不影响使用）：
  type "%TEMP%\vs_mf.json"
) else (
  echo manifest.json 上传成功!
)

echo.
echo ============================================
echo   共享完成!
echo ============================================
echo 分享链接（发给朋友，在游戏内「共享存档」页签 →「添加共享」粘贴即可下载）:
echo   https://github.com/%REPO%/tree/%BRANCH%/%PREFIX%/%WORLD%
echo.
set "LINK=https://github.com/%REPO%/tree/%BRANCH%/%PREFIX%/%WORLD%"
echo %LINK% | clip
echo (链接已复制到剪贴板，Ctrl+V 粘贴即可)
echo.
:end
pause
