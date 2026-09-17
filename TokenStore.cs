using System;
using System.IO;
using System.Text;

namespace ValheimSaveShare
{
    /// <summary>
    /// GitHub Token 的本地存储。Thunderstore 审核要求：秘密（token 等）禁止存放在
    /// BepInEx config 目录 —— profile 同步/分享功能会把 config 一起带走，导致泄露。
    /// 因此 Token 存到用户目录下不受同步的位置：%AppData%\ValheimSaveShare\token.dat。
    /// 文件内容为 UTF-8 明文 token，与一般配置文件同等级（本机用户可读）。
    /// </summary>
    internal static class TokenStore
    {
        /// <summary>token.dat 完整路径（用于提示文案）。</summary>
        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ValheimSaveShare", "token.dat");

        /// <summary>读取 Token；文件不存在或为空时返回空串。每次都读盘（文件只有一行，开销可忽略），便于用户手工修改立即生效。</summary>
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
        /// 首次运行时生成带说明的空 token.dat（已有文件则不动），
        /// 让用户不用翻文档就知道 token 该填在哪、怎么生成。
        /// </summary>
        public static void EnsureTemplate()
        {
            if (File.Exists(FilePath))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    "把 GitHub Personal Access Token 粘贴到本行下面单独一行（上传共享存档必需；下载公开仓库不需要）。\n" +
                    "生成步骤：github.com → 右上角头像 → Settings → 左侧最底部 Developer settings →\n" +
                    "Personal access tokens → Fine-grained tokens → Generate new token：\n" +
                    "  Repository access 选 Only select repositories 并勾选你 cfg 里 Repo 填的仓库；\n" +
                    "  Permissions → Repository permissions → Contents 设为 Read and write（其余保持 No access）；\n" +
                    "  Generate 后复制 github_pat_ 开头的完整字符串，粘贴到本说明下方单独一行，保存文件即可（无需重启）。\n" +
                    "换 token：直接覆盖粘贴；作废 token：到 GitHub token 管理页 Revoke。\n" +
                    "本文件在 %AppData% 下，不会被 r2modman 的配置同步/分享带出，请勿主动分享给他人。\n" +
                    "Paste your GitHub Personal Access Token on its own line below this block.\n",
                    Encoding.UTF8);
            }
            catch (Exception e)
            {
                SaveSharePlugin.Log.LogWarning("failed to create token template: " + e.Message);
            }
        }

        /// <summary>Load 的宽松版：跳过说明行，取文件里第一行像 token 的内容（github_pat_ / ghp_ 开头，或首个非空非注释行）。</summary>
        public static string LoadUserToken()
        {
            string raw = Load();
            if (raw.Length == 0 || raw.Contains('\n'))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) ||
                            t.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase))
                        {
                            return t;
                        }
                    }
                }
                catch
                {
                    // fall through
                }
                return "";
            }
            return raw;
        }
    }
}
