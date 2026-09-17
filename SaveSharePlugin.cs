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
        public const string PluginVersion = "1.0.4";

        internal static ManualLogSource Log;
        internal static SaveSharePlugin Instance;

        internal static ConfigEntry<string> ConfigRepo;
        internal static ConfigEntry<string> ConfigBranch;
        internal static ConfigEntry<string> ConfigPathPrefix;
        internal static ConfigEntry<string> ConfigProxy;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ConfigRepo = Config.Bind("GitHub", "Repo", "",
                "上传用的 GitHub 仓库，格式 owner/repo，例如 MyName/valheim-shared-worlds。\n" +
                "还没有仓库？先到 github.com/new 新建一个空仓库（Public 公开 = 朋友无需 Token 即可下载；Private 私有 = 下载者也需配置 Token）。\n" +
                "仓库不需要预先放任何文件，本模组会自动创建 worlds/<世界名>/ 目录结构。\n" +
                "GitHub repository (owner/repo) used as the upload target for shared saves.");
            ConfigBranch = Config.Bind("GitHub", "Branch", "",
                "上传目标分支。留空则自动使用仓库默认分支。Target branch; empty = repo default branch.");
            ConfigPathPrefix = Config.Bind("GitHub", "PathPrefix", "worlds",
                "仓库内存放共享存档的根目录。Repo folder that holds shared saves.");
            ConfigProxy = Config.Bind("GitHub", "Proxy", "",
                "可选 HTTP 代理，例如 http://127.0.0.1:7890（Clash/SingBox 等本地代理端口）。留空 = 直连。\n" +
                "直连 api.github.com 困难或上传/下载卡住时，请填写你的本地代理端口。\n" +
                "Optional HTTP proxy for GitHub access, e.g. http://127.0.0.1:7890. Empty = direct.\n" +
                "GitHub Token 不在本 cfg：存放在 %AppData%\\ValheimSaveShare\\token.dat\n" +
                "(GitHub Token is NOT stored in this cfg; it lives in %AppData%\\ValheimSaveShare\\token.dat\n" +
                "so profile sync/share can never leak it. See that file for setup instructions.)");
            Config.Save();
            // Token 不落 config（Thunderstore 审核要求）：首次运行在 %AppData% 下生成带说明的 token.dat
            TokenStore.EnsureTemplate();

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
