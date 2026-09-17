using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ValheimSaveShare
{
    /// <summary>
    /// 上传/下载编排：主线程只做 UI（弹窗、进度文本），网络与文件 IO 全部在后台线程，
    /// 通过 SaveSharePlugin.Post 回主线程。进度通过共享字段喂给 CancelableTaskPopup。
    /// </summary>
    internal static class SaveShareService
    {
        private static GitHubClient _gh;

        private static GitHubClient GH => _gh ??= new GitHubClient(
            TokenStore.Load(), SaveSharePlugin.ConfigProxy.Value?.Trim());

        // 进度弹窗共享状态（后台线程写、主线程每帧读）
        private static volatile string _status = "";
        private static volatile bool _done;
        private static volatile bool _cancelled;
        private static bool _taskOpen; // 任务弹窗是否还在 UnifiedPopup 栈上（仅主线程读写）
        private static CancellationTokenSource _cts;

        private static readonly string[] ShareExtensions = { ".fwl2", ".db2", ".fwl", ".db", ".ok", ".chunks", ".chunk" };

        // ============================ 下载 ============================

        public static void BeginDownload(string rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return;
            }
            if (!ShareUrl.TryParse(rawUrl, out var parsed, out var parseError))
            {
                Warn(L("链接无效", "Invalid URL"), parseError);
                return;
            }

            // 主线程先取好的只读快照
            string gameVersion = global::Version.GetVersionString();
            string prefix = (SaveSharePlugin.ConfigPathPrefix.Value ?? "worlds").Trim('/');

            RunBackground(() =>
            {
                try
                {
                    string branch = parsed.Branch;
                    if (string.IsNullOrEmpty(branch))
                    {
                        branch = SaveSharePlugin.ConfigBranch.Value?.Trim();
                    }
                    if (string.IsNullOrEmpty(branch))
                    {
                        SetStatus(L("正在连接 GitHub…", "Connecting to GitHub…"));
                        branch = GH.GetDefaultBranch(parsed.Owner, parsed.Repo, Token());
                    }

                    string worldPath = ShareUrl.NormalizeWorldPath(parsed.Path, prefix);

                    // 仓库根链接 / worlds 根链接 → 列目录挑世界文件夹
                    if (string.IsNullOrEmpty(worldPath) || worldPath == prefix || worldPath == prefix + "/")
                    {
                        SetStatus(L("正在获取共享存档列表…", "Listing shared saves…"));
                        var dirs = GH.ListDirectory(parsed.Owner, parsed.Repo, prefix, branch, Token())
                            .Where(i => i.Type == "dir").Select(i => i.Path).ToList();
                        if (dirs.Count == 1)
                        {
                            worldPath = dirs[0];
                        }
                        else if (dirs.Count == 0)
                        {
                            throw new UserException(L(
                                $"该仓库 {prefix}/ 下没有共享存档。",
                                $"No shared saves under {prefix}/ in this repo."));
                        }
                        else
                        {
                            string listText = string.Join("\n", dirs.Select(d => "· " + ShareUrl.LastSegment(d)));
                            throw new UserException(L(
                                $"该仓库包含多个共享存档，请复制具体世界文件夹的链接：\n{listText}",
                                $"This repo contains multiple shared saves, paste the link of a specific world folder:\n{listText}"));
                        }
                    }

                    SetStatus(L("正在读取存档信息…", "Reading save info…"));
                    ShareManifest manifest = null;
                    var manifestBytes = GH.TryDownloadFile(parsed.Owner, parsed.Repo, worldPath + "/manifest.json", branch, null, Token());
                    if (manifestBytes != null)
                    {
                        try
                        {
                            manifest = JsonConvert.DeserializeObject<ShareManifest>(Encoding.UTF8.GetString(manifestBytes));
                        }
                        catch (Exception e)
                        {
                            SaveSharePlugin.Log.LogWarning("manifest parse failed: " + e.Message);
                        }
                    }

                    List<ShareFileInfo> files;
                    if (manifest?.Files is { Count: > 0 })
                    {
                        files = manifest.Files;
                    }
                    else
                    {
                        files = GH.ListDirectory(parsed.Owner, parsed.Repo, worldPath, branch, Token())
                            .Where(i => i.Type == "file" && !i.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                            .Select(i => new ShareFileInfo { Path = i.Name, Size = i.Size })
                            .ToList();
                    }
                    if (files.Count == 0)
                    {
                        throw new UserException(L("该地址下没有找到存档文件（缺少 .fwl2）。", "No save files (.fwl2) found at this address."));
                    }

                    string worldName = SanitizeName(manifest?.Name ?? ShareUrl.LastSegment(worldPath));
                    string seed = manifest?.Seed ?? "?";
                    string saveGameVersion = manifest?.GameVersion ?? "";
                    long total = files.Sum(f => f.Size);
                    string url = ShareUrl.BuildWorldUrl(parsed.Owner, parsed.Repo, branch, worldPath);

                    SaveSharePlugin.Post(() => ConfirmDownload(parsed, branch, worldPath, worldName, seed, saveGameVersion, gameVersion, files, total, url));
                }
                catch (OperationCanceledException)
                {
                    OnCancelled();
                }
                catch (Exception e)
                {
                    SaveSharePlugin.Post(() => { CloseTaskPopup(); ShowError(L("下载失败", "Download failed"), e); });
                }
            });
        }

        private static void ConfirmDownload(ParsedShareUrl parsed, string branch, string worldPath, string worldName,
            string seed, string saveGameVersion, string gameVersion, List<ShareFileInfo> files, long total, string url)
        {
            // 1) 版本不一致 → 询问
            if (!string.IsNullOrEmpty(saveGameVersion) && saveGameVersion != gameVersion)
            {
                UnifiedPopup.Push(new YesNoPopup(
                    L("游戏版本不同", "Game version differs"),
                    L($"该存档由游戏 {saveGameVersion} 保存，当前游戏为 {gameVersion}。\n" +
                      "旧版本世界通常可以正常加载（进游戏时会自动升级），仍要下载吗？",
                      $"This save was made with game {saveGameVersion}, current game is {gameVersion}.\n" +
                      "Old worlds usually load fine (auto-upgraded in game). Download anyway?"),
                    ConfirmCore,
                    UnifiedPopup.Pop, localizeText: false));
                return;
            }
            ConfirmCore();

            void ConfirmCore()
            {
                // 2) 本地同名存档 → 询问覆盖（自动 zip 备份）
                bool exists = false;
                SaveWithBackups existing = null;
                try
                {
                    exists = SaveSystem.TryGetSaveByName(worldName, SaveDataType.World, out existing) && !existing.IsDeleted;
                }
                catch
                {
                    // ignore
                }
                if (exists)
                {
                    var src = existing.PrimaryFile?.m_source ?? FileHelpers.FileSource.Auto;
                    if ((src & FileHelpers.FileSource.Cloud) != 0 || (src & FileHelpers.FileSource.Local) == 0)
                    {
                        // 云存档/老格式存档无法用本地 zip 备份的方式安全覆盖
                        Warn(L("无法覆盖", "Cannot overwrite"), L(
                            $"本地已有同名世界「{worldName}」，但它存在 Steam 云存档（或老格式存档）中，暂不支持自动备份覆盖。\n" +
                            "请先在游戏内删除或改名该存档，再重新下载。",
                            $"A Steam-cloud (or legacy) world named \"{worldName}\" already exists.\n" +
                            "Cloud saves cannot be auto-backed-up here; delete or rename it in game first, then retry."));
                        return;
                    }
                    UnifiedPopup.Push(new YesNoPopup(
                        L("本地已存在同名存档", "A local save with this name exists"),
                        L($"本地已有世界「{worldName}」。\n继续会把旧存档打包为 zip 备份后覆盖，确定吗？",
                          $"World \"{worldName}\" already exists locally.\nContinue = zip-backup the old one, then overwrite. Proceed?"),
                        StartDownload,
                        UnifiedPopup.Pop, localizeText: false));
                    return;
                }
                StartDownload();
            }

            void StartDownload()
            {
                UnifiedPopup.Push(new YesNoPopup(
                    L("确认下载", "Confirm download"),
                    L($"世界：{worldName}\n种子：{seed}\n文件：{files.Count} 个（约 {FmtMB(total)} MB）\n下载到本地后可在共享列表中开始游戏。",
                      $"World: {worldName}\nSeed: {seed}\nFiles: {files.Count} (~{FmtMB(total)} MB)\nAfter download it appears in the Shared Saves list."),
                    RunDownloadTask,
                    UnifiedPopup.Pop, localizeText: false));
            }

            void RunDownloadTask()
            {
                OpenTaskPopup();
                var worldDirLocal = SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);
                RunBackground(() =>
                {
                    try
                    {
                        string worldDir = worldDirLocal + "/" + worldName;
                        if (Directory.Exists(worldDir))
                        {
                            SetStatus(L("正在备份旧存档…", "Backing up old save…"));
                            BackupWorldDir(worldDir, worldName);
                        }
                        Directory.CreateDirectory(worldDir);

                        // 1.0.2+ 格式：存档打包在单个 world.zip 里（一次请求）；旧格式为散文件。
                        // 远端若无 manifest 则按散文件兜底。zip 下载同样带逐字节进度。
                        var manifestBytes = GH.TryDownloadFile(parsed.Owner, parsed.Repo, worldPath + "/manifest.json", branch, null, Token());
                        bool zipFormat = false;
                        if (manifestBytes != null)
                        {
                            try
                            {
                                zipFormat = JsonConvert.DeserializeObject<ShareManifest>(Encoding.UTF8.GetString(manifestBytes))?.Format == "zip";
                            }
                            catch
                            {
                                // ignore malformed manifest, fall through to loose files
                            }
                        }

                        if (zipFormat)
                        {
                            SetStatus(L("正在下载 world.zip…", "Downloading world.zip…"));
                            byte[] zipData = GH.DownloadFile(parsed.Owner, parsed.Repo, worldPath + "/world.zip", branch,
                                (cur, tot) => SetStatus(L(
                                    $"下载中  {FmtMB(cur)} / {FmtMB(tot > 0 ? tot : 0)} MB",
                                    $"Downloading  {FmtMB(cur)} / {FmtMB(tot > 0 ? tot : 0)} MB")),
                                Token());
                            SetStatus(L("正在解包存档文件…", "Extracting save files…"));
                            using var ms = new MemoryStream(zipData);
                            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                            foreach (var entry in zip.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.Name))
                                {
                                    continue; // 目录项
                                }
                                string dest = Path.Combine(worldDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? worldDir);
                                entry.ExtractToFile(dest, true);
                            }
                        }
                        else
                        {
                            int i = 0;
                            foreach (var f in files)
                            {
                                Token().ThrowIfCancellationRequested();
                                string rel = (f.Path ?? "").Replace('\\', '/').TrimStart('/');
                                int idx = i;
                                SetStatus($"({idx + 1}/{files.Count}) {rel}  0 MB / {FmtMB(f.Size)}");
                                byte[] data = GH.DownloadFile(parsed.Owner, parsed.Repo,
                                    worldPath + "/" + rel, branch,
                                    (cur, tot) => SetStatus($"({idx + 1}/{files.Count}) {rel}  {FmtMB(cur)} / {FmtMB(tot > 0 ? tot : f.Size)}"),
                                    Token());
                                string dest = Path.Combine(worldDir, rel.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? worldDir);
                                string tmp = dest + ".download";
                                File.WriteAllBytes(tmp, data);
                                if (File.Exists(dest))
                                {
                                    File.Delete(dest);
                                }
                                File.Move(tmp, dest);
                                i++;
                            }
                        }

                        SaveSharePlugin.Post(() =>
                        {
                            _done = true;
                            CloseTaskPopup();
                            try
                            {
                                SharedSaveRegistry.AddOrUpdate(worldName, url, saveGameVersion);
                                SharedSaveRegistry.Save();
                                RefreshWorldList();
                                SaveShareUI.Refresh();
                                Warn(L("下载完成", "Download complete"),
                                    L($"共享存档「{worldName}」已加入世界列表，可在「共享存档」页签中选中并开始游戏。",
                                      $"Shared save \"{worldName}\" added to the world list. Select it in the Shared Saves tab and start."));
                            }
                            catch (Exception e)
                            {
                                ShowError(L("下载失败", "Download failed"), e);
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        OnCancelled();
                    }
                    catch (Exception e)
                    {
                        SaveSharePlugin.Post(() => { CloseTaskPopup(); ShowError(L("下载失败", "Download failed"), e); });
                    }
                });
            }
        }

        // ============================ 上传 ============================

        public static void BeginUpload()
        {
            var fejd = FejdStartup.instance;
            if (fejd == null)
            {
                return;
            }
            var world = Traverse.Create(fejd).Field("m_world").GetValue<World>();
            if (world == null)
            {
                Warn(L("共享存档", "Share save"), L("请先在列表中选择一个世界。", "Select a world in the list first."));
                return;
            }

            string repoCfg = SaveSharePlugin.ConfigRepo.Value?.Trim() ?? "";
            string token = TokenStore.Load();
            var repoParts = repoCfg.Split('/');
            if (string.IsNullOrEmpty(repoCfg) || repoParts.Length != 2 || string.IsNullOrEmpty(repoParts[0]) || string.IsNullOrEmpty(repoParts[1]))
            {
                Warn(L("尚未配置 GitHub 仓库", "GitHub repo not configured"),
                    L("上传需要先在 BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg 中配置：\n" +
                      "[GitHub] Repo = 你的用户名/仓库名（没有就先去 github.com 新建一个空仓库）\n" +
                      "[GitHub] Token = 你的 Personal Access Token（需要该仓库 Contents 读写权限）",
                      "Configure in BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg:\n" +
                      "[GitHub] Repo = yourname/your-repo (create an empty repo on github.com first)\n" +
                      "[GitHub] Token = your Personal Access Token (Contents read/write for that repo)"));
                return;
            }
            if (string.IsNullOrEmpty(token))
            {
                Warn(L("尚未配置 GitHub Token", "GitHub token not configured"),
                    L("上传需要 Token：github.com → Settings → Developer settings → Fine-grained tokens，\n" +
                      "只勾选你的共享仓库的 Contents: Read and write，然后把 token 粘贴到\n" +
                      "BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg 的 [GitHub] Token，再启动一次游戏；\n" +
                      "token 会自动移入 %AppData%\\ValheimSaveShare\\token.dat 并从 cfg 中清空。\n" +
                      "（token 不再直接存放在 cfg，避免被配置同步功能带出）",
                      "Create a fine-grained PAT (github.com → Settings → Developer settings), grant\n" +
                      "Contents: Read and write on your share repo only, paste it into [GitHub] Token in\n" +
                      "BepInEx/config/SuperVikingDepartment.ValheimSaveShare.cfg and restart the game;\n" +
                      "it will be moved to %AppData%\\ValheimSaveShare\\token.dat and cleared from the cfg.\n" +
                      "(Tokens are never stored in the config folder, so profile sync can't leak them.)"));
                return;
            }

            string owner = repoParts[0], repoName = repoParts[1];

            if (!SaveSystem.TryGetSaveByName(world.m_name, SaveDataType.World, out var save) || save.IsDeleted || save.PrimaryFile == null)
            {
                Warn(L("共享存档", "Share save"), L("无法读取该世界的存档文件（可能已损坏）。", "Cannot read this world's save files (may be corrupt)."));
                return;
            }

            // 主线程收集文件清单并读入内存（游戏 API 不能跨线程）。
            // 注意 Steam 云存档：文件不在本地磁盘，逻辑路径形如 worlds/<名字>/_main.N.fwl2
            // （Utils.GetSaveDataPath(Cloud) 返回空串），必须用游戏自带的 FileReader 读取；
            // 本地/老格式存档也统一走 FileReader（内部 File.OpenRead），一套代码两种来源。
            var fileSet = new List<(string Rel, byte[] Data, long Size)>();
            string marker = "/" + world.m_name + "/";
            foreach (var p in save.PrimaryFile.AllPaths)
            {
                string norm = p.Replace('\\', '/').TrimStart('/');
                string ext = Path.GetExtension(norm).ToLowerInvariant();
                if (!ShareExtensions.Contains(ext))
                {
                    continue;
                }
                int idx = norm.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                string rel = idx >= 0 ? norm.Substring(idx + marker.Length) : Path.GetFileName(norm);
                byte[] data;
                try
                {
                    data = ReadSaveFileBytes(p, save.PrimaryFile.m_source);
                }
                catch (Exception e)
                {
                    bool cloud = (save.PrimaryFile.m_source & FileHelpers.FileSource.Cloud) != 0;
                    Warn(L("共享存档", "Share save"), L(
                        $"读取存档文件失败：{rel}\n{e.Message}" +
                        (cloud ? "\n（该世界是 Steam 云存档，读取依赖 Steam 云存储，请确认 Steam 正常运行且云同步可用。）" : ""),
                        $"Failed to read save file {rel}: {e.Message}" +
                        (cloud ? " (Steam cloud save — make sure Steam cloud storage is available.)" : "")));
                    return;
                }
                fileSet.Add((rel, data, data.LongLength));
            }
            if (fileSet.Count == 0)
            {
                Warn(L("共享存档", "Share save"), L("没有找到可共享的存档文件。", "No shareable save files found."));
                return;
            }
            var tooBig = fileSet.FirstOrDefault(f => f.Size > 90L * 1024 * 1024);
            if (tooBig.Rel != null)
            {
                Warn(L("文件过大", "File too large"),
                    L($"文件 {tooBig.Rel} 超过 90 MB，GitHub Contents API 不支持。\n建议改用 GitHub Release 上传（后续版本支持）。",
                      $"{tooBig.Rel} exceeds 90 MB (GitHub Contents API limit).\nUse a GitHub Release instead (supported in a later version)."));
                return;
            }
            long totalSize = fileSet.Sum(f => f.Size);
            string worldNameLocal = world.m_name;
            string seedLocal = world.m_seedName;
            int worldGenVersion = world.m_worldGenVersion;
            string gameVersion = global::Version.GetVersionString();

            UnifiedPopup.Push(new YesNoPopup(
                L("共享存档到 GitHub", "Share save to GitHub"),
                L($"把世界「{worldNameLocal}」上传到仓库 {owner}/{repoName}？\n" +
                  $"路径：{SaveSharePlugin.ConfigPathPrefix.Value}/{worldNameLocal}/（{fileSet.Count} 个文件，约 {FmtMB(totalSize)} MB）\n" +
                  "已存在的旧版本会被覆盖（保留提交历史）。",
                  $"Upload world \"{worldNameLocal}\" to {owner}/{repoName}?\n" +
                  $"Path: {SaveSharePlugin.ConfigPathPrefix.Value}/{worldNameLocal}/ ({fileSet.Count} files, ~{FmtMB(totalSize)} MB)\n" +
                  "Existing versions are overwritten (git history keeps old commits)."),
                () => RunUploadTask(owner, repoName, worldNameLocal, seedLocal, worldGenVersion, gameVersion, fileSet),
                UnifiedPopup.Pop, localizeText: false));
        }

        private static void RunUploadTask(string owner, string repoName, string worldName, string seed,
            int worldGenVersion, string gameVersion, List<(string Rel, byte[] Data, long Size)> files)
        {
            string prefix = (SaveSharePlugin.ConfigPathPrefix.Value ?? "worlds").Trim('/');
            OpenTaskPopup();
            RunBackground(() =>
            {
                try
                {
                    string branch = SaveSharePlugin.ConfigBranch.Value?.Trim();
                    if (string.IsNullOrEmpty(branch))
                    {
                        SetStatus(L("正在连接 GitHub…", "Connecting to GitHub…"));
                        branch = GH.GetDefaultBranch(owner, repoName, Token());
                    }

                    // 打包为单个 zip：97 个分块文件若逐个提交需要 ~194 个 API 请求（每个都要
                    // 查 sha + PUT），既慢又容易触发限流；zip 一次请求搞定，zip 内部已压缩。
                    SetStatus(L($"正在打包 {files.Count} 个文件…", $"Packing {files.Count} files…"));
                    byte[] zipData;
                    using (var ms = new MemoryStream())
                    {
                        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                        {
                            foreach (var f in files)
                            {
                                var entry = zip.CreateEntry(f.Rel, System.IO.Compression.CompressionLevel.Optimal);
                                using var es = entry.Open();
                                es.Write(f.Data, 0, f.Data.Length);
                            }
                        }
                        zipData = ms.ToArray();
                    }
                    if (zipData.LongLength > 90L * 1024 * 1024)
                    {
                        throw new UserException(L(
                            $"打包后 {FmtMB(zipData.Length)} MB 超过 GitHub Contents API 的 100 MB 限制，无法上传。\n" +
                            "（分块存档的世界可能非常大，后续版本考虑改走 GitHub Release。）",
                            $"Packed size {FmtMB(zipData.Length)} MB exceeds the 100 MB GitHub Contents API limit.\n" +
                            "(Chunked worlds can be huge; a GitHub Release path may come later.)"));
                    }

                    SetStatus(L($"正在上传（{FmtMB(zipData.Length)} MB，上传期间没有逐字节进度，请耐心等待）…",
                        $"Uploading ({FmtMB(zipData.Length)} MB; no byte-level progress during upload, please wait)…"));
                    // 进度刻度：让用户知道程序没死（"已用时间"每 10 秒跳一格）
                    var startTicks = DateTime.UtcNow;
                    using (var timer = new System.Threading.Timer(
                        _ => SetStatus(L($"正在上传（{FmtMB(zipData.Length)} MB，已用 {((DateTime.UtcNow - startTicks).TotalSeconds / 10f) * 10f:0} 秒）…",
                            $"Uploading ({FmtMB(zipData.Length)} MB, {((DateTime.UtcNow - startTicks).TotalSeconds / 10f) * 10f:0}s elapsed)…")),
                        null, 10000, 10000))
                    {
                        GH.UploadFile(owner, repoName, $"{prefix}/{worldName}/world.zip", branch, zipData,
                            $"ValheimSaveShare: update world '{worldName}' ({gameVersion}, {files.Count} files)", Token());
                    }

                    SetStatus(L("正在上传存档信息…", "Uploading manifest…"));
                    var manifest = new ShareManifest
                    {
                        Name = worldName,
                        Seed = seed,
                        GameVersion = gameVersion,
                        WorldGenVersion = worldGenVersion,
                        SavedAt = DateTime.Now.ToString("s"),
                        Uploader = TrySteamName(),
                        Tool = "ValheimSaveShare " + SaveSharePlugin.PluginVersion,
                        Format = "zip",
                        Files = files.Select(f => new ShareFileInfo { Path = f.Rel, Size = f.Size }).ToList()
                    };
                    byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(manifest, Formatting.Indented));
                    GH.UploadFile(owner, repoName, $"{prefix}/{worldName}/manifest.json", branch, manifestBytes,
                        $"ValheimSaveShare: update manifest for '{worldName}'", Token());

                    string url = ShareUrl.BuildWorldUrl(owner, repoName, branch, prefix + "/" + worldName);
                    SaveSharePlugin.Post(() =>
                    {
                        _done = true;
                        CloseTaskPopup();
                        try
                        {
                            SharedSaveRegistry.AddOrUpdate(worldName, url, gameVersion);
                            SharedSaveRegistry.Save();
                            SaveShareUI.Refresh();
                            // 游戏内文本无法框选复制：上传成功即自动把链接写入系统剪贴板
                            GUIUtility.systemCopyBuffer = url;
                            Warn(L("共享成功", "Shared"),
                                L($"世界「{worldName}」已上传。\n分享链接已自动复制到剪贴板，点「确认」可再次复制：\n{url}",
                                  $"World \"{worldName}\" uploaded.\nShare link copied to clipboard; press OK to copy again:\n{url}"),
                                onOk: () => GUIUtility.systemCopyBuffer = url);
                        }
                        catch (Exception e)
                        {
                            ShowError(L("共享失败", "Share failed"), e);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    OnCancelled();
                }
                catch (Exception e)
                {
                    SaveSharePlugin.Post(() => { CloseTaskPopup(); ShowError(L("共享失败", "Share failed"), e); });
                }
            });
        }

        // ============================ 公共小件 ============================

        /// <summary>从“共享存档”列表移除记录（不动本地文件）。</summary>
        public static void RemoveRegistryEntry(string worldName)
        {
            SharedSaveRegistry.Remove(worldName);
            SharedSaveRegistry.Save();
            SaveShareUI.Refresh();
        }

        private static void OpenTaskPopup()
        {
            _cts = new CancellationTokenSource();
            _done = false;
            _cancelled = false;
            _taskOpen = true;
            _status = "";
            UnifiedPopup.Push(new CancelableTaskPopup(
                () => L("Valheim 存档共享", "Valheim Save Share"),
                () => _status,
                () => _done || _cancelled,
                () =>
                {
                    _cancelled = true;
                    try
                    {
                        _cts?.Cancel();
                    }
                    catch
                    {
                        // ignore
                    }
                }));
        }

        /// <summary>
        /// 在主线程上把还开着的任务弹窗从 UnifiedPopup 栈里弹掉。
        /// 必须在 Push 后续弹窗（成功/失败/已取消）之前调用，否则后续弹窗会压在任务弹窗
        /// 上面：用户点掉上层后露出任务弹窗，而它的关闭回调已失效，导致游戏假死。
        /// </summary>
        private static void CloseTaskPopup()
        {
            if (!_taskOpen)
            {
                return;
            }
            _taskOpen = false;
            if (UnifiedPopup.IsVisible())
            {
                UnifiedPopup.Pop();
            }
        }

        private static void OnCancelled()
        {
            _cancelled = true;
            SaveSharePlugin.Post(() =>
            {
                CloseTaskPopup();
                Warn(L("已取消", "Cancelled"), L("操作已取消，本地文件未受影响。", "Operation cancelled; local files untouched."));
            });
        }

        private static CancellationToken Token() => _cts?.Token ?? CancellationToken.None;

        private static void RunBackground(Action action)
        {
            Task.Run(action);
        }

        private static void SetStatus(string text) => _status = text;

        private static void RefreshWorldList()
        {
            try
            {
                SaveSystem.InvalidateCache(SaveDataType.World);
                var fejd = FejdStartup.instance;
                AccessTools.Method(typeof(FejdStartup), "UpdateWorldList")?.Invoke(fejd, new object[] { true });
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("refresh world list failed: " + e);
            }
        }

        /// <summary>
        /// 统一读取存档文件：本地/老格式存档走磁盘，Steam 云存档走游戏自带的
        /// FileReader（内部 Mount → CloudStorage.ReadFile → Unmount）。
        /// </summary>
        private static byte[] ReadSaveFileBytes(string path, FileHelpers.FileSource source)
        {
            // FileReader 有 Dispose 方法但未实现 IDisposable 接口，不能写 using
            var reader = new FileReader(path, source);
            try
            {
                var stream = reader.m_binary != null ? reader.m_binary.BaseStream : reader.m_stream.BaseStream;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            finally
            {
                reader.Dispose();
            }
        }

        /// <summary>把旧世界文件夹打包成 zip 放到 worlds 根目录，再删除原文件夹。</summary>
        private static void BackupWorldDir(string worldDir, string worldName)
        {
            string root = Directory.GetParent(worldDir).FullName;
            string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string zipPath = Path.Combine(root, $"{worldName}_backup_{ts}.zip");
            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(worldDir, "*", SearchOption.AllDirectories))
                {
                    string entryName = file.Substring(worldDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    zip.CreateEntryFromFile(file, entryName, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
            Directory.Delete(worldDir, true);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "SharedWorld";
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Trim();
        }

        private static string TrySteamName()
        {
            try
            {
                return Steamworks.SteamFriends.GetPersonaName();
            }
            catch
            {
                return "";
            }
        }

        private static string FmtMB(long bytes)
        {
            return (bytes / (1024f * 1024f)).ToString("0.##");
        }

        internal static string L(string zh, string en)
        {
            try
            {
                string lang = Localization.instance?.GetSelectedLanguage();
                if (lang == "Chinese" || lang == "ChineseSimplified" || lang == "zhCN")
                {
                    return zh;
                }
            }
            catch
            {
                // ignore
            }
            return en;
        }

        private static void Warn(string title, string text, Action onOk = null)
        {
            UnifiedPopup.Push(new WarningPopup(title, text, () =>
            {
                onOk?.Invoke();
                UnifiedPopup.Pop();
            }, localizeText: false));
        }

        internal static void ShowError(string title, Exception e)
        {
            string msg = e is UserException || e is GitHubException ? e.Message : e.ToString();
            if (e is GitHubException gh401 && gh401.Status == System.Net.HttpStatusCode.Unauthorized)
            {
                msg += "\n\n" + L(
                    "提示：GitHub 拒绝了 Token（无效或已过期）。请到 GitHub → Settings → Developer settings →\n" +
                    "Fine-grained tokens 检查/重新生成，并按模组的 Token 配置说明重新填写。",
                    "Hint: GitHub rejected the token (invalid or expired). Regenerate it under Settings →\n" +
                    "Developer settings → Fine-grained tokens and re-enter it per the mod's token instructions.");
            }
            else if (e is GitHubException gh403 && gh403.Status == System.Net.HttpStatusCode.Forbidden &&
                msg != null && msg.Contains("not accessible"))
            {
                msg += "\n\n" + L(
                    "提示：这是 Token 权限问题。请到 GitHub → Settings → Developer settings → 你的 fine-grained token，\n" +
                    "确认 Repository access 勾选了共享仓库、且 Permissions → Contents = Read and write\n" +
                    "（经典 token 则需要勾选 repo 整个 scope），保存后把新 token 粘贴到 cfg 的 [GitHub] Token，再启动一次游戏。",
                    "Hint: token permission issue. Edit your fine-grained token: select the share repo under\n" +
                    "Repository access and set Permissions → Contents = Read and write (classic tokens need the\n" +
                    "repo scope), then paste the new token into [GitHub] Token in the cfg and restart the game.");
            }
            SaveSharePlugin.Log.LogError(title + ": " + e);
            Warn(title, msg);
        }
    }
}
