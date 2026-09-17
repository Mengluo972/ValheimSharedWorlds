# ValheimSaveShare 存档共享

通过 GitHub 分享 Valheim 世界存档的 BepInEx 模组。

Share Valheim worlds between friends via your own GitHub repository.

## 功能 / Features

- **「共享存档」页签**：在主菜单"开始游戏 / 加入游戏"旁新增第三个页签，列出你已添加为共享的存档（与开始游戏页签同样的世界列表样式）。选中即可开始游戏。
- **添加共享（下载）**：页签内「添加共享」按钮 → 粘贴 GitHub 链接（仓库主页或世界文件夹链接均可）→ 自动下载 `manifest.json` + 全部存档文件到 `worlds_local/<世界名>/`，**带逐文件进度与取消**。下载完成自动出现在世界列表。
（见下）
- 支持中文世界名、私有仓库（下载时需配置 Token）、同名存档覆盖前自动 zip 备份、游戏版本不一致提示。

## 首次配置 / Setup

上传需要你有一个 GitHub 仓库和令牌（**下载别人的公开分享不需要任何配置**）：

1. 在 [github.com/new](https://github.com/new) 新建一个空仓库（如 `valheim-shared-worlds`，公开或私有均可）。
2. 编辑 `BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg`，填入 `[GitHub] Repo`：

```ini
[GitHub]
Repo = 你的用户名/valheim-shared-worlds
Branch =            ; 留空自动用默认分支
PathPrefix = worlds
```

3. 把 token 填进 `%AppData%\ValheimSaveShare\token.dat`（资源管理器地址栏粘贴该路径即可打开；首次运行游戏会自动生成带说明的模板文件）：github.com → Settings → Developer settings → Fine-grained tokens → Generate，只勾选该仓库的 **Contents: Read and write**，把 `github_pat_` 开头的字符串粘贴到说明下方单独一行保存。

**Token 存放位置 / Token storage**：token 存放在 `%AppData%\ValheimSaveShare\token.dat`，不在 cfg 里。想换 token 直接覆盖文件里的 token 行；想作废 token 到 GitHub token 管理页 Revoke。

已共享的存档记录（世界名 → 链接）保存在 `BepInEx/config/ValheimSaveShare.shared.json`。

## 链接格式 / Accepted URLs

- `https://github.com/<user>/<repo>` —— 自动在 `worlds/` 下找（仅一个世界时直接用）
- `https://github.com/<user>/<repo>/tree/main/worlds/我的世界` —— 指定世界文件夹
- `https://raw.githubusercontent.com/<user>/<repo>/main/worlds/我的世界/manifest.json`

## 注意事项 / Notes

- **分块存档与上传速度**：分块存档的世界（`.db2` 拆成几十上百个 `.chunk`）自动打包为单个 `world.zip` 一次请求上传（zip 内部已压缩），上传期间弹窗显示已用时间，不是卡死。
- **国内网络/代理**：直连 api.github.com 困难时，在 cfg 的 `[GitHub] Proxy` 填本地代理端口（如 `http://127.0.0.1:7890`），上传下载全走该代理。
- **Steam 云存档**：开启 Steam 云的世界（存档在 `worlds/` 云端而非本地磁盘）上传时，通过游戏自带 FileReader/Steam 云存储读取，与本地存档（`worlds_local/`）一样支持；下载的世界一律落地为**本地存档**（`worlds_local/`，列表中带“本地”图标）。若云端已有同名世界，下载时会提示无法自动覆盖，请先在游戏内删除或改名。
- 共享的是**世界快照**（手递手式共享，不是实时同步）。建议一个世界只由"房主"上传，其他人只下载，避免互相覆盖进度。
- 游戏扫描器会删除不成组的存档文件，因此本 mod 总是整组上传/下载 `.fwl2/.db2/.ok/.chunks/.chunk`；手动搬运时也请整组复制。
- 打包后超过 90 MB 时上传会被拒绝（GitHub Contents API 100 MB 限制），后续版本考虑改走 Release Assets。
- 第三个页签基于原版控件克隆，手柄对列表行的导航暂未适配（页签左右切换、按钮点击正常），欢迎反馈。
- Token 保存在 `%AppData%\ValheimSaveShare\token.dat`（cfg 中无 Token 字段）；请勿把 token 本身分享给他人。

## 构建 / Build

```bash
./build-package.sh        # 产物 dist/SuperVikingDepartment-ValheimSaveShare-<ver>.zip
./sync-install.sh         # 同步到游戏 BepInEx/plugins 与 r2modman profile（需先退出游戏）
```
