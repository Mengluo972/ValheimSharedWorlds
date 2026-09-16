using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ValheimSaveShare
{
    internal class ShareFileInfo
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
    }

    /// <summary>manifest.json：随存档一起上传/下载的元数据。</summary>
    internal class ShareManifest
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("seed")] public string Seed { get; set; }
        [JsonProperty("gameVersion")] public string GameVersion { get; set; }
        [JsonProperty("worldGenVersion")] public int WorldGenVersion { get; set; }
        [JsonProperty("savedAt")] public string SavedAt { get; set; }
        [JsonProperty("uploader")] public string Uploader { get; set; }
        [JsonProperty("tool")] public string Tool { get; set; }
        /// <summary>"zip" = 存档文件打包在 world.zip 内（1.0.2+）；空 = 旧版散文件格式。</summary>
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("files")] public List<ShareFileInfo> Files { get; set; } = new List<ShareFileInfo>();
    }

    /// <summary>解析后的 GitHub 分享链接。</summary>
    internal class ParsedShareUrl
    {
        public string Owner;
        public string Repo;
        public string Branch; // 可为空（使用默认分支）
        public string Path;   // 仓库内路径，"" 表示仓库根目录

        public string RepoSlug => Owner + "/" + Repo;
    }

    internal static class ShareUrl
    {
        /// <summary>
        /// 支持的输入形式：
        ///  https://github.com/{owner}/{repo}
        ///  https://github.com/{owner}/{repo}/tree/{branch}/{path...}
        ///  https://github.com/{owner}/{repo}/blob/{branch}/{path...}/manifest.json
        ///  https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path...}
        ///  https://api.github.com/repos/{owner}/{repo}/contents/{path...}?ref={branch}
        /// </summary>
        public static bool TryParse(string input, out ParsedShareUrl parsed, out string error)
        {
            parsed = null;
            error = null;
            input = input?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                error = "链接为空";
                return false;
            }
            if (!input.Contains("://"))
            {
                input = "https://" + input;
            }
            Uri uri;
            try
            {
                uri = new Uri(input);
            }
            catch
            {
                error = "无法解析的 URL：" + input;
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            string[] parts = uri.AbsolutePath.Trim('/').Split('/')
                .Select(Uri.UnescapeDataString).ToArray();
            string queryBranch = GetQueryValue(uri.Query, "ref");

            switch (host)
            {
                case "github.com":
                case "www.github.com":
                {
                    if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                    {
                        error = "链接里缺少 owner/repo：" + input;
                        return false;
                    }
                    parsed = new ParsedShareUrl { Owner = parts[0], Repo = parts[1].EndsWith(".git") ? parts[1].Substring(0, parts[1].Length - 4) : parts[1] };
                    if (parts.Length >= 4 && (parts[2] == "tree" || parts[2] == "blob"))
                    {
                        parsed.Branch = parts[3];
                        parsed.Path = string.Join("/", parts.Skip(4));
                    }
                    return true;
                }
                case "raw.githubusercontent.com":
                {
                    if (parts.Length < 3)
                    {
                        error = "raw 链接缺少 owner/repo/branch：" + input;
                        return false;
                    }
                    parsed = new ParsedShareUrl
                    {
                        Owner = parts[0],
                        Repo = parts[1],
                        Branch = parts[2],
                        Path = string.Join("/", parts.Skip(3))
                    };
                    return true;
                }
                case "api.github.com":
                {
                    if (parts.Length < 2)
                    {
                        error = "API 链接缺少 owner/repo：" + input;
                        return false;
                    }
                    parsed = new ParsedShareUrl { Owner = parts[0], Repo = parts[1], Branch = queryBranch };
                    int ci = Array.IndexOf(parts, "contents");
                    if (ci >= 0)
                    {
                        parsed.Path = string.Join("/", parts.Skip(ci + 1));
                    }
                    return true;
                }
                default:
                    error = "仅支持 github.com 链接，收到：" + host;
                    return false;
            }
        }

        /// <summary>把路径收拢为“世界文件夹路径”：去掉末尾 manifest.json、去掉仓库根目录的空路径。</summary>
        public static string NormalizeWorldPath(string path, string prefix)
        {
            path = (path ?? "").Trim('/').Replace('\\', '/');
            if (path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - "/manifest.json".Length);
            }
            if (path.EndsWith("/manifest.json/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - "/manifest.json/".Length);
            }
            return path;
        }

        public static string LastSegment(string path)
        {
            path = (path ?? "").Trim('/');
            int idx = path.LastIndexOf('/');
            return idx >= 0 ? path.Substring(idx + 1) : path;
        }

        /// <summary>生成可分享的世界文件夹链接（供上传成功后写进注册表）。</summary>
        public static string BuildWorldUrl(string owner, string repo, string branch, string worldPath)
        {
            return $"https://github.com/{owner}/{repo}/tree/{branch}/{EscapePath(worldPath)}";
        }

        public static string EscapePath(string path)
        {
            return string.Join("/", (path ?? "").Split('/')
                .Where(s => s.Length > 0)
                .Select(Uri.EscapeDataString));
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return null;
        }
    }
}
