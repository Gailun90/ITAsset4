using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ITAsset4.Common
{
    /// <summary>
    /// 文件下载器。
    ///   - filename 仅取 GetFileName()，拒绝目录穿越
    ///   - 仅允许白名单扩展名（.msi .exe .cab .msp ）
    ///   - ComputeHash 流式处理，避免大文件内存问题
    /// </summary>
    public class Downloader
    {
        private readonly string _cacheDir;
        private static readonly string[] AllowedExtensions = { ".msi", ".exe", ".cab", ".msp" };

        public Downloader(string cacheDir)
        {
            _cacheDir = cacheDir;
            Directory.CreateDirectory(cacheDir);
        }

        /// <summary>对 filename 做安全校验：仅取文件名，白名单扩展名</summary>
        private static string SanitizeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
                throw new ArgumentException("filename 不能为空");

            // 1. 仅取 Path.GetFileName()，丢弃任何目录部分
            string safe = Path.GetFileName(filename);

            // 2. 检查是否实际移除了目录部分（原文件名含路径分隔符）
            if (safe != filename)
            {
                Logger.Warn($"[安全] filename 目录穿越被重写: {filename} → {safe}");
            }

            // 3. 白名单扩展名
            string ext = Path.GetExtension(safe).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            {
                throw new ArgumentException($"filename 扩展名不在白名单内: {ext}，允许: {string.Join(",", AllowedExtensions)}");
            }

            

            return safe;
        }

        public async Task<string> DownloadAsync(
            string url, string filename, string serial,
            string deviceSecret,
            string expectedHash = null,
            int? bandwidthLimitKb = null,
            CancellationToken ct = default(CancellationToken))
        {
            string safeName = SanitizeFilename(filename);
            string dest = Path.Combine(_cacheDir, safeName);

            if (File.Exists(dest) && !string.IsNullOrEmpty(expectedHash))
            {
                string existing = ComputeHash(dest);
                if (string.Equals(existing, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"[下载] 缓存命中: {safeName}");
                    return dest;
                }
            }

            long startPos = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            Logger.Info($"[下载] 开始: {safeName} (offset={startPos})");

            long? contentLength = null;

            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            {
                string ts  = DeviceAuth.NowTimestamp();
                string sig = DeviceAuth.Sign(serial, ts, deviceSecret);
                http.DefaultRequestHeaders.Add("X-Serial",    serial);
                http.DefaultRequestHeaders.Add("X-Timestamp", ts);
                http.DefaultRequestHeaders.Add("X-Signature", sig);

                bool rangeRequested = startPos > 0;
                if (rangeRequested)
                    http.DefaultRequestHeaders.Range = new RangeHeaderValue(startPos, null);

                var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                int status = (int)resp.StatusCode;

                if (status == (int)System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // 416：片段已失效，覆盖重写整文件
                    startPos = 0;
                    http.DefaultRequestHeaders.Range = null;
                    resp.Dispose();
                    resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    contentLength = resp.Content.Headers.ContentLength;
                    await SaveStreamAsync(resp, dest, 0, bandwidthLimitKb, ct);
                }
                else
                {
                    resp.EnsureSuccessStatusCode();
                    contentLength = resp.Content.Headers.ContentLength;

                    // 修复（C3）：原先 resume 只处理 416，若服务器对带 Range 的请求返回 200
                    // （忽略 Range 头），会走 else 直接“追加”写，导致文件前半段重复、整文件损坏。
                    // 改用纯逻辑 DownloadResume.Decide 决策：仅当真正的 206 才追加，
                    // 416 / 200 一律覆盖从 0 开始。
                    var decision = DownloadResume.Decide(startPos, rangeRequested, status);
                    long writeOffset = decision.Append ? startPos : 0;

                    // 额外防御：206 时校验 Content-Range 起点与本地片段一致，否则覆盖重写
                    if (status == 206 && !decision.Append)
                    {
                        writeOffset = 0;
                    }
                    else if (status == 206)
                    {
                        var cr = resp.Content.Headers.ContentRange;
                        if (cr != null && cr.From.HasValue && cr.From.Value != startPos)
                        {
                            Logger.Warn($"[下载] Content-Range 起点({cr.From.Value})≠本地片段({startPos})，覆盖重写");
                            writeOffset = 0;
                        }
                    }

                    if (!decision.Append)
                        Logger.Warn($"[下载] 服务器未续传（status={status}），从 0 覆盖写入");

                    await SaveStreamAsync(resp, dest, writeOffset, bandwidthLimitKb, ct);
                }
            }

            // 修复：此前完全没有把实际写入的字节数和响应头 Content-Length 对比过，
            // 网络连接中途断开时 ReadAsync 会直接返回 0（不一定抛异常），
            // 会被当成"读到文件末尾=下载完成"，截断的文件就这么被当成完整文件放行。
            if (contentLength.HasValue)
            {
                long actualSize = new FileInfo(dest).Length;
                long expectedSize = startPos + contentLength.Value;
                if (actualSize != expectedSize)
                {
                    File.Delete(dest);
                    throw new Exception($"下载不完整: 期望大小={expectedSize}, 实际大小={actualSize}（网络可能中途断开）");
                }
            }

            // 修复：此前完整性校验完全依赖 expectedHash 是否非空——一旦服务端给的哈希是空的
            // （比如包注册流程哪次哈希计算失败留了空值），前面的大小校验之外就再没有任何
            // 手段确认文件内容正确，之前的代码会直接放行去执行安装。
            // 现在即使没有 hash，也强制拒绝执行，而不是静默信任。
            if (string.IsNullOrEmpty(expectedHash))
            {
                Logger.Warn($"[下载] {safeName} 服务端未提供 SHA256，无法校验内容完整性，拒绝执行");
                File.Delete(dest);
                throw new Exception($"服务端未提供安装包 {safeName} 的 SHA256，出于可靠性考虑拒绝执行未经校验的安装包（请检查包注册流程是否漏算了哈希）");
            }

            string actual = ComputeHash(dest);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dest);
                throw new Exception($"SHA256 不匹配: 期望={expectedHash}, 实际={actual}");
            }
            Logger.Info($"[下载] SHA256 校验通过: {safeName}");

            return dest;
        }

        private static async Task SaveStreamAsync(HttpResponseMessage resp, string dest,
            long startPos, int? limitKb, CancellationToken ct)
        {
            using (var src  = await resp.Content.ReadAsStreamAsync())
            using (var file = new FileStream(dest, startPos > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[65536];
                int limitBps = (limitKb ?? 0) > 0 ? limitKb.Value * 1024 : 0;
                long downloaded = 0;
                var  sw = System.Diagnostics.Stopwatch.StartNew();

                int read;
                while ((read = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await file.WriteAsync(buf, 0, read, ct);
                    downloaded += read;

                    if (limitBps > 0)
                    {
                        double expected = (double)downloaded / limitBps * 1000;
                        int elapsed = (int)sw.ElapsedMilliseconds;
                        if (expected > elapsed)
                            await Task.Delay((int)(expected - elapsed), ct);
                    }
                }
                Logger.Info($"[下载] 完成，共 {downloaded / 1024} KB");
            }
        }

        /// <summary>流式 SHA256（避免大文件整体加载）</summary>
        private static string ComputeHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var f   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true))
            {
                // 流式逐块处理，不一次性加载整个文件
                byte[] buffer = new byte[65536];
                int read;
                while ((read = f.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                byte[] hash = sha.Hash;
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}