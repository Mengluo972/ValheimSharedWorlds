using System;
using System.Collections.Concurrent;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimSaveShare
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SaveSharePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "SuperVikingDepartment.ValheimSaveShare";
        public const string PluginName = "ValheimSaveShare";
        public const string PluginVersion = "1.0.3";

        internal static ManualLogSource Log;
        internal static SaveSharePlugin Instance;

        internal static ConfigEntry<string> ConfigRepo;
        internal static ConfigEntry<string> ConfigBranch;
        internal static ConfigEntry<string> ConfigToken;
        internal static ConfigEntry<string> ConfigPathPrefix;
        internal static ConfigEntry<string> ConfigProxy;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ConfigRepo = Config.Bind("GitHub", "Repo", "",
                "上传用的 GitHub 仓库，格式 owner/repo，例如 MyName/valheim-shared-worlds。\n" +
                "还没有仓库？先到 github.com/new 新建一个空仓库（Public 公开 = 朋友无需 Token 即可下载；Private 私有 = 下载者也需在本 cfg 配置 Token）。\n" +
                "仓库不需要预先放任何文件，本模组会自动创建 worlds/<世界名>/ 目录结构。\n" +
                "GitHub repository (owner/repo) used as the upload target for shared saves.");
            ConfigBranch = Config.Bind("GitHub", "Branch", "",
                "上传目标分支。留空则自动使用仓库默认分支。Target branch; empty = repo default branch.");
            ConfigToken = Config.Bind("GitHub", "Token", "",
                "GitHub Personal Access Token —— 上传共享存档必需；下载公开仓库不需要。\n" +
                "在这里粘贴 token 后，下次启动会自动移入 %AppData%\\ValheimSaveShare\\token.dat 并把本项清空\n" +
                "（Thunderstore 要求：token 不得存放在 config 目录，否则会被 r2modman 的配置同步/分享功能带出去）。\n" +
                "生成教程（Fine-grained token，权限最小化）：\n" +
                "  1. 登录 github.com，点右上角头像 → Settings（设置）；\n" +
                "  2. 左侧栏最底部 → Developer settings（开发者设置）；\n" +
                "  3. Personal access tokens → Fine-grained tokens → Generate new token；\n" +
                "  4. Token name 随意（如 valheim-saveshare）；Expiration 到期时间可选 No expiration（永不过期）或按需选择；\n" +
                "  5. Resource owner 选你自己；Repository access 选 Only select repositories，勾选你的共享仓库（即上面 Repo 填的那个）；\n" +
                "  6. 展开 Permissions → Repository permissions → Contents，设为 Read and write（其余权限保持 No access）；\n" +
                "  7. 点 Generate token，复制生成的 github_pat_ 开头的完整字符串，粘贴到本项 Token = 后面。\n" +
                "换 token：把新 token 粘贴到本项再启动一次即可；作废旧 token：到 GitHub token 管理页 Revoke。\n" +
                "GitHub PAT used for uploading shared saves. Pasted here once, then auto-moved to\n" +
                "%AppData%\\ValheimSaveShare\\token.dat (Thunderstore rule: secrets must not live in the config\n" +
                "folder, which r2modman may sync/share).");
            ConfigPathPrefix = Config.Bind("GitHub", "PathPrefix", "worlds",
                "仓库内存放共享存档的根目录。Repo folder that holds shared saves.");
            ConfigProxy = Config.Bind("GitHub", "Proxy", "",
                "可选 HTTP 代理，例如 http://127.0.0.1:7890（Clash/SingBox 等本地代理端口）。留空 = 直连。\n" +
                "直连 api.github.com 困难或上传/下载卡住时，请填写你的本地代理端口。\n" +
                "Optional HTTP proxy for GitHub access, e.g. http://127.0.0.1:7890. Empty = direct.");
            Config.Save();
            // Token 不落 config：用户粘进 cfg 的 token 迁移到 %AppData% 后立即把 cfg 清空，
            // 迁移动作要在 Config.Save() 之后（否则 Config.Save 又会把旧值写回去）
            TokenStore.MigrateFromConfig(ConfigToken);

            new Harmony(PluginGuid).PatchAll(typeof(FejdStartupSetupGuiPatch));
            SharedSaveRegistry.Load();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded");
        }

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Log.LogError("main-thread task failed: " + e);
                }
            }
        }

        /// <summary>把一个委托排到 Unity 主线程执行（网络任务完成后回调 UI 用）。</summary>
        internal static void Post(Action action)
        {
            if (Instance != null)
            {
                Instance._mainThreadQueue.Enqueue(action);
            }
        }
    }
}
