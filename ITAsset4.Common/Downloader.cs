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

            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            {
                string ts  = DeviceAuth.NowTimestamp();
                string sig = DeviceAuth.Sign(serial, ts, deviceSecret);
                http.DefaultRequestHeaders.Add("X-Serial",    serial);
                http.DefaultRequestHeaders.Add("X-Timestamp", ts);
                http.DefaultRequestHeaders.Add("X-Signature", sig);

                if (startPos > 0)
                    http.DefaultRequestHeaders.Range = new RangeHeaderValue(startPos, null);

                var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    startPos = 0;
                    http.DefaultRequestHeaders.Range = null;
                    resp.Dispose();
                    resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    await SaveStreamAsync(resp, dest, 0, bandwidthLimitKb, ct);
                }
                else
                {
                    resp.EnsureSuccessStatusCode();
                    await SaveStreamAsync(resp, dest, startPos, bandwidthLimitKb, ct);
                }
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                string actual = ComputeHash(dest);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(dest);
                    throw new Exception($"SHA256 不匹配: 期望={expectedHash}, 实际={actual}");
                }
                Logger.Info($"[下载] SHA256 校验通过: {safeName}");
            }

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