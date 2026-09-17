using System;
using System.IO;
using System.Text;
using BepInEx.Configuration;

namespace ValheimSaveShare
{
    /// <summary>
    /// GitHub Token 的本地存储。Thunderstore 审核要求：秘密（token 等）禁止存放在
    /// BepInEx config 目录 —— profile 同步/分享功能会把 config 一起带走，导致泄露。
    /// 因此 Token 存到用户目录下不受同步的位置：%AppData%\ValheimSaveShare\token.dat。
    /// 文件内容为 UTF-8 明文 token，与旧 cfg 时代的保密等级一致（本机用户可读）。
    /// </summary>
    internal static class TokenStore
    {
        /// <summary>token.dat 完整路径（用于提示文案）。</summary>
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ValheimSaveShare", "token.dat");

        /// <summary>读取 Token；从未保存过时返回空串。每次都读盘（文件只有一行，开销可忽略），便于用户中途手工修改立即生效。</summary>
        public static string Load()
        {
            try
            {
                return File.Exists(FilePath) ? File.ReadAllText(FilePath, Encoding.UTF8).Trim() : "";
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("failed to read token file: " + e.Message);
                return "";
            }
        }

        /// <summary>保存 Token。写临时文件后原子替换，避免写一半留下半截 token。</summary>
        public static void Save(string token)
        {
            token = (token ?? "").Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, token, Encoding.UTF8);
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
            File.Move(tmp, FilePath);
        }

        /// <summary>
        /// 启动迁移：cfg 里若还留有旧版写入的 [GitHub] Token，搬到 token.dat 并把 cfg
        /// 里的值清空（r2modman 的 config editor/同步不再能看到 token）。
        /// </summary>
        public static void MigrateFromConfig(ConfigEntry<string> cfgToken)
        {
            if (cfgToken == null)
            {
                return;
            }
            string legacy = cfgToken.Value?.Trim() ?? "";
            if (legacy.Length == 0)
            {
                return;
            }
            if (Load().Length == 0)
            {
                Save(legacy);
                SaveSharePlugin.Log.LogInfo("migrated GitHub token from cfg to " + FilePath);
            }
            else
            {
                SaveSharePlugin.Log.LogInfo("ignored token left in cfg (token.dat already exists)");
            }
            // 无论是否采用，都把 cfg 里的明文清掉，防止随 profile 同步外泄
            cfgToken.Value = "";
            cfgToken.ConfigFile.Save();
        }
    }
}
