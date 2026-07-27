using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    /// 客户端自更新引擎：
    ///   1. 定时查询服务端 /api/client/update
    ///   2. 若服务端版本比本地 ClientVersion.Current 新，则下载更新包 zip
    ///   3. 校验 SHA256 后，写出 manifest JSON 并启动独立的 ITAsset4.Updater.exe
    ///      （Updater 以 UseShellExecute 方式脱离 Service 进程组运行，
    ///       会先停止 ITAsset4Agent 服务 → 解压 → 覆盖文件 → 重启服务）
    ///
    /// ⚠️ 更新器以 SYSTEM 权限运行（与 Service 同上下文），请确保更新包来源可信。
    /// </summary>
    public class UpdateChecker
    {
        private readonly AppConfig _cfg;
        private readonly ApiClient _api;
        private const string SERVICE_NAME = "ITAsset4Agent";

        public UpdateChecker(AppConfig cfg, ApiClient api)
        {
            _cfg = cfg;
            _api = api;
        }

        public async Task CheckAndApplyAsync(string serial, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (!_api.IsRegistered) return;

            var info = await _api.GetClientUpdateAsync(serial);
            if (!info.available || string.IsNullOrEmpty(info.version))
            {
                Logger.Info("[更新] 服务端无可用更新");
                return;
            }

            if (!ClientVersion.IsNewer(info.version, ClientVersion.Current))
            {
                Logger.Info($"[更新] 已是最新 (本地 {ClientVersion.Current} >= 服务端 {info.version})");
                return;
            }

            Logger.Info($"[更新] 发现新版本 {info.version}（本地 {ClientVersion.Current}），mandatory={info.mandatory}");

            string staging = Path.Combine(_cfg.BaseDir, "updates", info.version);
            try { Directory.CreateDirectory(staging); } catch { }
            string zipPath = Path.Combine(staging, "update.zip");

            Logger.Info($"[更新] 下载更新包 -> {zipPath}");
            bool ok = await _api.DownloadFileAsync(info.url, zipPath, serial);
            if (!ok || !File.Exists(zipPath))
            {
                Logger.Error("[更新] 下载失败，跳过本次更新");
                return;
            }

            // SHA256 校验
            if (!string.IsNullOrEmpty(info.hash))
            {
                string actual = ComputeSha256(zipPath);
                if (!string.Equals(actual, info.hash, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error($"[更新] 校验失败: 期望 {info.hash} 实际 {actual}");
                    try { File.Delete(zipPath); } catch { }
                    return;
                }
                Logger.Info("[更新] SHA256 校验通过");
            }

            ApplyUpdate(info, zipPath, staging);
        }

        private void ApplyUpdate(ApiClient.ClientUpdateInfo info, string zipPath, string staging)
        {
            try
            {
                string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string updaterExe   = Path.Combine(installDir, "ITAsset4.Updater.exe");
                string manifestPath = Path.Combine(staging, "update_manifest.json");

                // 将参数写入 JSON manifest，避免命令行字符串拼接 / 引号注入
                var manifest = new
                {
                    service     = SERVICE_NAME,
                    extract     = zipPath,
                    install_dir = installDir,
                    timeout     = 30,
                    version     = info.version,       // ← 供 Updater 日志记录，不改行为
                };
                File.WriteAllText(manifestPath,
                    JsonConvert.SerializeObject(manifest, Formatting.Indented),
                    Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName        = updaterExe,
                    // 唯一命令行参数 — 只传 manifest 路径，路径不含特殊字符
                    Arguments       = $"--manifest \"{manifestPath.Replace("\"", "")}\"",
                    UseShellExecute = true,
                    CreateNoWindow  = true,
                };
                Process.Start(psi);

                Logger.Info("[更新] 独立更新器已启动，Service 即将退出");
            }
            catch (Exception ex)
            {
                Logger.Error($"[更新] 启动更新器失败: {ex.Message}");
            }
        }

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
