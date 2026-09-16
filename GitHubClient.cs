using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ValheimSaveShare
{
    /// <summary>带 HTTP 状态码的 GitHub 错误。</summary>
    internal class GitHubException : Exception
    {
        public readonly HttpStatusCode? Status;

        public GitHubException(string message, HttpStatusCode? status) : base(message)
        {
            Status = status;
        }
    }

    /// <summary>业务性错误（弹窗展示原始消息即可）。</summary>
    internal class UserException : Exception
    {
        public UserException(string message) : base(message)
        {
        }
    }

    internal class GitHubItem
    {
        public string Name;
        public string Path;
        public string Type; // "file" | "dir"
        public long Size;
    }

    /// <summary>
    /// GitHub REST API v3 客户端（同步方法，全部在后台线程调用，不要在主线程用）。
    /// 下载走 raw.githubusercontent.com（公开仓库免认证），私有仓库自动回退到
    /// Contents API + Accept: application/vnd.github.raw。
    /// 上传统一走 Contents API（PUT /repos/{o}/{r}/contents/{path}）。
    /// </summary>
    internal class GitHubClient
    {
        private const string ApiBase = "https://api.github.com";
        private const string RawBase = "https://raw.githubusercontent.com";

        private readonly HttpClient _http;
        private readonly bool _hasToken;

        public GitHubClient(string token, string proxy = null)
        {
            _hasToken = !string.IsNullOrEmpty(token);
            var handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxy))
            {
                try
                {
                    handler.Proxy = new WebProxy(proxy);
                    handler.UseProxy = true;
                    SaveSharePlugin.Log.LogInfo("GitHubClient using proxy: " + proxy);
                }
                catch (Exception e)
                {
                    SaveSharePlugin.Log.LogWarning("invalid proxy config, using direct connection: " + e.Message);
                }
            }
            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromMinutes(10);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ValheimSaveShare/1.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (_hasToken)
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        // ---------- 仓库 ----------

        /// <summary>校验仓库可访问并返回默认分支。</summary>
        public string GetDefaultBranch(string owner, string repo, CancellationToken ct)
        {
            var json = GetJson($"{ApiBase}/repos/{owner}/{repo}", ct);
            string branch = json?["default_branch"]?.ToString();
            return string.IsNullOrEmpty(branch) ? "main" : branch;
        }

        // ---------- 列目录 ----------

        public List<GitHubItem> ListDirectory(string owner, string repo, string path, string branch, CancellationToken ct)
        {
            string url = $"{ApiBase}/repos/{owner}/{repo}/contents/{ShareUrl.EscapePath(path ?? "")}?per_page=100";
            if (!string.IsNullOrEmpty(branch))
            {
                url += "&ref=" + Uri.EscapeDataString(branch);
            }
            var resp = Send(HttpMethod.Get, url, ct);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var arr = JArray.Parse(body);
            var items = new List<GitHubItem>();
            foreach (var t in arr)
            {
                items.Add(new GitHubItem
                {
                    Name = t.Value<string>("name"),
                    Path = t.Value<string>("path"),
                    Type = t.Value<string>("type"),
                    Size = t.Value<long?>("size") ?? 0
                });
            }
            return items;
        }

        // ---------- 下载 ----------

        /// <summary>下载文件；404 时返回 null（Try 语义）。</summary>
        public byte[] TryDownloadFile(string owner, string repo, string path, string branch,
            Action<long, long> progress, CancellationToken ct)
        {
            try
            {
                return DownloadFile(owner, repo, path, branch, progress, ct);
            }
            catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public byte[] DownloadFile(string owner, string repo, string path, string branch,
            Action<long, long> progress, CancellationToken ct)
        {
            string url = $"{RawBase}/{owner}/{repo}/{branch}/{ShareUrl.EscapePath(path)}";
            try
            {
                return GetWithProgress(url, progress, ct);
            }
            catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound && _hasToken)
            {
                // 私有仓库：raw 会 404，改走 Contents API 的 raw 媒体类型
                string apiUrl = $"{ApiBase}/repos/{owner}/{repo}/contents/{ShareUrl.EscapePath(path)}?ref={Uri.EscapeDataString(branch)}";
                var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("application/vnd.github.raw");
                return GetWithProgress(req, progress, ct);
            }
        }

        // ---------- 上传（Contents API） ----------

        /// <summary>文件已存在时返回其 blob sha（更新时必须携带），不存在返回 null。</summary>
        public string TryGetFileSha(string owner, string repo, string path, string branch, CancellationToken ct)
        {
            string url = $"{ApiBase}/repos/{owner}/{repo}/contents/{ShareUrl.EscapePath(path)}?ref={Uri.EscapeDataString(branch)}";
            try
            {
                var json = GetJson(url, ct);
                return json?["sha"]?.ToString();
            }
            catch (GitHubException e) when (e.Status == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public void UploadFile(string owner, string repo, string path, string branch, byte[] data,
            string message, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                string sha = TryGetFileSha(owner, repo, path, branch, ct);
                var body = new JObject
                {
                    ["message"] = message,
                    ["branch"] = branch,
                    ["content"] = Convert.ToBase64String(data)
                };
                if (!string.IsNullOrEmpty(sha))
                {
                    body["sha"] = sha;
                }
                var req = new HttpRequestMessage(HttpMethod.Put,
                    $"{ApiBase}/repos/{owner}/{repo}/contents/{ShareUrl.EscapePath(path)}")
                {
                    Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
                };
                try
                {
                    Send(req, ct);
                    return;
                }
                catch (GitHubException e) when ((e.Status == HttpStatusCode.Conflict || e.Status == (HttpStatusCode)422) && attempt == 0)
                {
                    // sha 过期（远端刚被更新）：重取 sha 重试一次
                }
            }
        }

        // ---------- 基础 ----------

        private JObject GetJson(string url, CancellationToken ct)
        {
            var resp = Send(HttpMethod.Get, url, ct);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JObject.Parse(body);
        }

        private HttpResponseMessage Send(HttpMethod method, string url, CancellationToken ct)
        {
            return Send(new HttpRequestMessage(method, url), ct);
        }

        private HttpResponseMessage Send(HttpRequestMessage req, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = _http.SendAsync(req, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new GitHubException("网络错误：" + e.Message, null);
            }
            if ((int)resp.StatusCode >= 300)
            {
                string body = "";
                try
                {
                    body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // ignore
                }
                string msg = ExtractMessage(body) ?? $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                resp.Dispose();
                throw new GitHubException(msg, resp.StatusCode);
            }
            return resp;
        }

        private byte[] GetWithProgress(string url, Action<long, long> progress, CancellationToken ct)
        {
            return GetWithProgress(new HttpRequestMessage(HttpMethod.Get, url), progress, ct);
        }

        private byte[] GetWithProgress(HttpRequestMessage req, Action<long, long> progress, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new GitHubException("网络错误：" + e.Message, null);
            }

            if ((int)resp.StatusCode >= 300)
            {
                string body = "";
                try
                {
                    body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // ignore
                }
                string msg = ExtractMessage(body) ?? $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                long code = (long)resp.StatusCode;
                resp.Dispose();
                throw new GitHubException(msg, (HttpStatusCode)code);
            }

            long total = resp.Content.Headers.ContentLength ?? -1;
            using (resp)
            using (var stream = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[65536];
                long read = 0;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    ms.Write(buffer, 0, n);
                    read += n;
                    progress?.Invoke(read, total);
                }
                return ms.ToArray();
            }
        }

        private static string ExtractMessage(string body)
        {
            try
            {
                var obj = JObject.Parse(body);
                string msg = obj.Value<string>("message");
                return string.IsNullOrEmpty(msg) ? null : msg;
            }
            catch
            {
                return null;
            }
        }
    }
}
