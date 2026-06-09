using ITAsset4.Common;
using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ITAsset4.Service
{
    public class TaskExecutor
    {
        private readonly AppConfig _cfg;
        private readonly Downloader _dl;
        private readonly string _pkgDir;

        private static readonly Regex BlacklistRegex =
            new Regex(@"[\r\n`""<>|&;,^\\]", RegexOptions.Compiled);

        private static readonly int[] UninstallSuccessCodes =
            { 0, 19, 3010, 1641, 1638, 1650 };

        public TaskExecutor(AppConfig cfg)
        {
            _cfg = cfg;
            _pkgDir = Path.Combine(cfg.BaseDir, "packages");
            _dl = new Downloader(_pkgDir);
            Directory.CreateDirectory(_pkgDir);
        }

        // ═══════════════════════════════════════════════
        // 主入口
        // ═══════════════════════════════════════════════
        public async Task<TaskResult> ExecuteAsync(TaskInfo task, string serial, string deviceSecret)
        {
            Logger.Info($"[任务 {task.target_id}] {task.task_name}");

            if (task.task_type == "uninstall")
                return await ExecuteUninstallAsync(task);

            string pkgPath = await _dl.DownloadAsync(
                task.download_url,
                task.package_filename,
                serial,
                deviceSecret,
                task.package_hash);

            var run = await RunWithJobObjectAsync(pkgPath, task.silent_args, task.timeout);

            bool success = (task.success_codes != null && task.success_codes.Count > 0)
                ? task.success_codes.Contains(run.Item1)
                : run.Item1 == 0;

            return new TaskResult
            {
                success = success,
                exit_code = run.Item1,
                message = success ? "安装成功" : "安装失败",
                install_log = run.Item2
            };
        }

        // ═══════════════════════════════════════════════
        // ⭐ 卸载（三层结构重构）
        // ═══════════════════════════════════════════════
        private async Task<TaskResult> ExecuteUninstallAsync(TaskInfo task)
        {
            string swName = task.uninstall_target;
            Logger.Info($"卸载任务: {swName}");

            KillRelatedProcesses(swName);

            // ===== L1: 获取卸载目标 =====
            var targets = GetUninstallTargets(swName);

            if (targets.Count == 0)
            {
                return new TaskResult
                {
                    success = false,
                    message = $"未找到卸载信息: {swName}"
                };
            }

            // ===== L2: 执行卸载 =====
            var exec = await ExecuteTargets(targets, task.timeout);

            // ===== L3: 统一验证 =====
            bool success = VerifyUninstall(swName, exec.ExitCode);

            return new TaskResult
            {
                success = success,
                exit_code = exec.ExitCode,
                message = success ? $"卸载成功: {swName}" : $"卸载失败: {swName}",
                install_log = exec.Log
            };
        }

        // ═══════════════════════════════════════════════
        // L1: 获取卸载目标
        // ═══════════════════════════════════════════════
        private System.Collections.Generic.List<(string exe, string args, string dir, string name)>
        GetUninstallTargets(string swName)
        {
            var list = FindAllUninstallInfos(swName);

            if (list.Count > 0)
                return list;

            string guid = FindProductGuidViaWmi(swName);

            if (!string.IsNullOrEmpty(guid))
            {
                return new System.Collections.Generic.List<(string, string, string, string)>
        {
            ("msiexec", $"/x {guid} /qn /norestart", "", swName)
        };
            }

            return new System.Collections.Generic.List<(string, string, string, string)>();
        }

        // ═══════════════════════════════════════════════
        // L2: 执行卸载
        // ═══════════════════════════════════════════════
        private async Task<(int ExitCode, string Log)> ExecuteTargets(
            System.Collections.Generic.List<(string exe, string args, string dir, string name)> targets,
            int timeout)
        {
            var sb = new StringBuilder();
            int last = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];

                Logger.Info($"卸载 {i + 1}/{targets.Count}: {t.name}");

                var r = await RunUninstallAsync(
                    Tuple.Create(t.exe, t.args),
                    timeout);

                last = r.Item1;
                sb.AppendLine(r.Item2);

                if (i < targets.Count - 1)
                    await Task.Delay(3000);
            }

            return (last, sb.ToString());
        }

        // ═══════════════════════════════════════════════
        // L3: 统一验证
        // ═══════════════════════════════════════════════
        private bool VerifyUninstall(string swName, int exitCode)
        {
            Thread.Sleep(4000);

            var remaining = FindAllUninstallInfos(swName);

            if (remaining.Count == 0)
                return true;

            bool dirGone = remaining.All(r =>
                string.IsNullOrEmpty(r.Item3) || !Directory.Exists(r.Item3));

            return dirGone;
        }

        // ═══════════════════════════════════════════════
        // 进程清理
        // ═══════════════════════════════════════════════
        private static void KillRelatedProcesses(string softwareName)
        {
            var map = new System.Collections.Generic.Dictionary<string, string[]>
            {
                { "企业微信", new[] { "WXWork", "WXWorkApp" } },
                { "WeCom", new[] { "WXWork", "WXWorkApp" } },
                { "微信", new[] { "WeChat" } },
                { "Webex", new[] { "CiscoCollabHost", "WebexHost" } },
                { "QQ", new[] { "QQ", "QQProtect" } },
                { "钉钉", new[] { "DingTalk" } },
                { "Teams", new[] { "Teams" } },
                { "飞书", new[] { "Lark" } },
                { "Zoom", new[] { "Zoom" } }
            };

            var kill = new System.Collections.Generic.List<string>();

            foreach (var kv in map)
                if (softwareName.Contains(kv.Key))
                    kill.AddRange(kv.Value);

            foreach (var p in kill)
            {
                try
                {
                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName(p))
                    {
                        try { proc.Kill(); }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }
            }

            System.Threading.Thread.Sleep(1500);
        }

       
        private static System.Collections.Generic.List<(string exe, string args, string dir, string name)>
    FindAllUninstallInfos(string softwareName)
        {
            var regPaths = new[]
            {
        Tuple.Create(Microsoft.Win32.Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        Tuple.Create(Microsoft.Win32.Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        Tuple.Create(Microsoft.Win32.Registry.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

            var result = new System.Collections.Generic.List<(string exe, string args, string dir, string name)>();

            foreach (var root in regPaths)
            {
                try
                {
                    using var key = root.Item1.OpenSubKey(root.Item2);
                    if (key == null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(sub);
                        string name = sk?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        
                        if (name.IndexOf(softwareName, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        string installDir = sk.GetValue("InstallLocation") as string ?? "";

                        string exe = "msiexec";
                        string args = $"/x {sub} /qn /norestart";

                        result.Add((exe, args, installDir, name));
                    }
                }
                catch { }
            }

            return result;
        }

        private static string FindProductGuidViaWmi(string softwareName)
        {
            try
            {
                string q = $"SELECT IdentifyingNumber, Name FROM Win32_Product WHERE Name LIKE '%{softwareName}%'";
                using var searcher = new ManagementObjectSearcher(q);
                foreach (ManagementObject obj in searcher.Get())
                    return obj["IdentifyingNumber"]?.ToString();
            }
            catch { }

            return null;
        }

        private static async Task<Tuple<int, string>> RunUninstallAsync(
            Tuple<string, string> info, int timeoutSec)
        {
            var si = new System.Diagnostics.ProcessStartInfo
            {
                FileName = info.Item1,
                Arguments = info.Item2,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var p = System.Diagnostics.Process.Start(si);
            p.WaitForExit(timeoutSec * 1000);

            return Tuple.Create(p.ExitCode, "");
        }

        
        private async Task<Tuple<int, string>> RunWithJobObjectAsync(
            string pkgPath, string silentArgs, int timeoutSec)
        {
            var si = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pkgPath,
                Arguments = silentArgs,
                UseShellExecute = false
            };

            var p = System.Diagnostics.Process.Start(si);
            p.WaitForExit(timeoutSec * 1000);

            return Tuple.Create(p.ExitCode, "");
        }
    }
}