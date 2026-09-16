# Changelog

## 1.0.1

首个公开发布版本（1.0.0 提交时版本号被平台风控流程占用，顺延为 1.0.1）。

- 主菜单新增「共享存档」页签：与「开始游戏 / 加入游戏」并列，列出已添加为共享的存档（未下载的显示“未下载”），选中后可直接开始游戏；进过世界再退回主菜单后页签正常保留。
- 「添加共享」（下载）：粘贴 GitHub 链接（仓库主页 / 世界文件夹 / raw / API 链接均可，支持中文世界名）下载他人分享的世界，带进度与取消；同名本地世界覆盖前自动 zip 备份；游戏版本不一致时先询问。
- 「开始游戏」页签「返回」下方新增「共享存档」按钮：把当前选中的世界整组文件（`.fwl2/.db2/.ok/.chunks/.chunk` + 自动生成的 manifest）打包为单个 `world.zip` 一次请求上传到你的 GitHub 仓库，上传成功后分享链接自动复制到剪贴板。
- 支持 Steam 云存档（云端 `worlds/` 里的世界通过游戏自带 FileReader / Steam 云存储读取，与本地存档一样可上传）；下载的世界落地为本地存档 `worlds_local/`。
- 支持私有仓库下载（配置 Token）与可选 HTTP 代理（`[GitHub] Proxy`），适配国内直连 GitHub 困难的网络。
- 配置项：GitHub Repo / Branch / Token / PathPrefix / Proxy，位于 `BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg`（cfg 内含 Token 生成教程与安全提示）。
