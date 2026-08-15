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

            // ── 防循环闸门（H1，§4.8 #1）：同一目标版本在冷却窗口内重试超限即拒绝 ──
            // 持久化状态落盘，确保跨 Service 重启也能打破"漏 bump 版本"导致的无限自更新循环。
            var guardDecision = UpdateLoopGuard.Evaluate(info.version, LoadGuardState(), DateTime.UtcNow);
            SaveGuardState(guardDecision.Next);
            if (!guardDecision.Allowed)
            {
                Logger.Error($"[更新] {guardDecision.Reason}");
                return;
            }

            // ── 更新包策略（H2，§4.8 #2）：必须携带非空 version 与 SHA256 ──
            // 旧逻辑 hash 为空直接跳过校验并应用；现统一为"空 hash 即拒绝"，与任务包策略一致。
            var policy = UpdatePolicy.ValidateUpdatePackage(info.version, info.hash);
            if (!policy.ok)
            {
                Logger.Error($"[更新] {policy.reason}");
                return;
            }

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

            // SHA256 校验（H2：到此处 info.hash 必非空；仍做实际比对以拦截传输损坏）
            string actual = ComputeSha256(zipPath);
            if (!string.Equals(actual, info.hash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error($"[更新] 校验失败: 期望 {info.hash} 实际 {actual}");
                try { File.Delete(zipPath); } catch { }
                return;
            }
            Logger.Info("[更新] SHA256 校验通过");

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

        // ── 防循环闸门状态持久化（H1）──
        // 状态必须落盘：Service 每次更新后会被 Updater 停止/重启，进程内存无法跨重启保留，
        // 而"漏 bump 版本"的无限循环恰恰发生在跨重启场景，故用文件持久化打破它。

        private string GuardStateFile =>
            Path.Combine(_cfg.BaseDir, "updates", ".update_guard.json");

        private UpdateLoopGuard.State LoadGuardState()
        {
            try
            {
                if (File.Exists(GuardStateFile))
                {
                    var s = JsonConvert.DeserializeObject<UpdateLoopGuard.State>(File.ReadAllText(GuardStateFile));
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新] 读取防循环状态失败，按全新状态处理: {ex.Message}");
            }
            return new UpdateLoopGuard.State();
        }

        private void SaveGuardState(UpdateLoopGuard.State state)
        {
            try
            {
                string dir = Path.GetDirectoryName(GuardStateFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(GuardStateFile, JsonConvert.SerializeObject(state), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[更新] 写入防循环状态失败（不影响本次更新）: {ex.Message}");
            }
        }
    }
}
