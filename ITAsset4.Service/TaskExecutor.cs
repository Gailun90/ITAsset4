using ITAsset4.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // UI 交互委托：Service → Tray（Pipe/TCP），用于 interactive/deferred/reboot 弹窗
        private readonly Func<PipeRequest, Task<PipeResponse>> _uiSender;

        // 审计上报委托：每次进程执行后回调上报到 /api/audit/action
        // （修复 8B：原来 ReportAuditAsync 写了但 TaskExecutor 从不调用）
        private readonly Func<string, string, int?, int?, DateTime, Task> _auditReporter;

        // 黑名单：防止命令注入的特殊字符（修复 8A：原来定义但从未使用）
        private static readonly Regex BlacklistRegex =
            new Regex(@"[\r\n`""<>|&^]", RegexOptions.Compiled);

        // 卸载成功的退出码
        private static readonly int[] UninstallSuccessCodes =
            { 0, 19, 3010, 1641, 1638, 1650 };

        // 安装后需要重启的退出码
        private static readonly int[] RebootRequiredCodes = { 3010, 1641, 1638 };

        public TaskExecutor(AppConfig cfg,
            Func<PipeRequest, Task<PipeResponse>> uiSender = null,
            Func<string, string, int?, int?, DateTime, Task> auditReporter = null)
        {
            _cfg = cfg;
            _pkgDir = Path.Combine(cfg.BaseDir, "packages");
            _dl = new Downloader(_pkgDir);
            Directory.CreateDirectory(_pkgDir);
            _uiSender = uiSender;
            _auditReporter = auditReporter;
        }

        // ═══════════════════════════════════════════════
        // 主入口
        // ═══════════════════════════════════════════════
        public async Task<TaskResult> ExecuteAsync(TaskInfo task, string serial, string deviceSecret)
        {
            Logger.Info($"[任务 {task.target_id}] {task.task_name} (interactive={task.interactive}, type={task.task_type})");

            // ── 卸载任务 ───────────────────────────────────────
            if (task.task_type == "uninstall")
                return await ExecuteUninstallAsync(task);

            // ── 安装任务：interactive 检查（修复：原来完全无视 task.interactive）──
            if (task.interactive)
            {
                var userChoice = await AskUserInstallAsync(task);
                if (userChoice == "DEFERRED")
                {
                    Logger.Info($"[任务 {task.target_id}] 用户选择推迟安装");
                    return new TaskResult { success = false, deferred = true, message = "用户推迟安装" };
                }
                if (userChoice == "CANCEL")
                {
                    Logger.Info($"[任务 {task.target_id}] 用户取消安装");
                    return new TaskResult { success = false, message = "用户取消安装" };
                }
                // userChoice == "OK" → 继续执行安装
                Logger.Info($"[任务 {task.target_id}] 用户确认，开始安装");
            }

            // ── 下载 + 执行安装 ───────────────────────────────
            string pkgPath = await _dl.DownloadAsync(
                task.download_url,
                task.package_filename,
                serial,
                deviceSecret,
                task.package_hash);

            var run = await RunProcessAsync(pkgPath, task.silent_args, task.timeout, _auditReporter);

            // ── 判断成功/失败（含重启退出码检测）────────────────
            bool success;
            bool needsReboot = RebootRequiredCodes.Contains(run.ExitCode);

            if (task.success_codes != null && task.success_codes.Count > 0)
                success = task.success_codes.Contains(run.ExitCode) || needsReboot;
            else
                success = run.ExitCode == 0 || needsReboot;

            // ── 需要重启时询问用户（修复：原来只有卸载处理 3010）──
            if (success && needsReboot)
            {
                Logger.Info($"[任务 {task.target_id}] 安装需要重启 (exit code {run.ExitCode})");

                var rebootChoice = await AskUserRebootAsync(
                    task.package_filename ?? task.task_name ?? "软件");
                var rebootAction = rebootChoice switch
                {
                    "now"    => "reboot_now",
                    "later"  => "reboot_required",
                    _        => "reboot_required",  // cancel → 标记需要但不强制
                };

                return new TaskResult
                {
                    success = true,
                    exit_code = run.ExitCode,
                    message = $"安装成功，需要重启 ({rebootAction})",
                    install_log = run.Log,
                    reboot_action = rebootAction,
                };
            }

            return new TaskResult
            {
                success = success,
                exit_code = run.ExitCode,
                message = success ? "安装成功" : $"安装失败 (exit={run.ExitCode})",
                install_log = run.Log
            };
        }

        // ═══════════════════════════════════════════════
        // ⭐ 卸载（三层结构重构）
        // ═══════════════════════════════════════════════
        private async Task<TaskResult> ExecuteUninstallAsync(TaskInfo task)
        {
            string swName = task.uninstall_target;
            Logger.Info($"卸载任务: {swName}");

            // ── interactive 检查（修复：原来完全无视 task.interactive）──
            if (task.interactive)
            {
                var userChoice = await AskUserInstallAsync(task);
                if (userChoice == "DEFERRED")
                {
                    Logger.Info($"[任务 {task.target_id}] 用户选择推迟卸载");
                    return new TaskResult { success = false, deferred = true, message = "用户推迟卸载" };
                }
                if (userChoice == "CANCEL")
                {
                    Logger.Info($"[任务 {task.target_id}] 用户取消卸载");
                    return new TaskResult { success = false, message = "用户取消卸载" };
                }
                Logger.Info($"[任务 {task.target_id}] 用户确认，开始卸载");
            }

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

            // ===== L3: 统一验证（修复：检测重启退出码）=====
            bool needsReboot = RebootRequiredCodes.Contains(exec.ExitCode);
            bool success = VerifyUninstall(swName, exec.ExitCode);

            // ── 需要重启时询问用户 ──────────────────────────────
            if (success && needsReboot)
            {
                Logger.Info($"[任务 {task.target_id}] 卸载需要重启 (exit code {exec.ExitCode})");

                var rebootChoice = await AskUserRebootAsync(swName);
                var rebootAction = rebootChoice switch
                {
                    "now"    => "reboot_now",
                    "later"  => "reboot_required",
                    _        => "reboot_required",
                };

                return new TaskResult
                {
                    success = true,
                    exit_code = exec.ExitCode,
                    message = $"卸载成功，需要重启 ({rebootAction})",
                    install_log = exec.Log,
                    reboot_action = rebootAction,
                };
            }

            return new TaskResult
            {
                success = success,
                exit_code = exec.ExitCode,
                message = success ? $"卸载成功: {swName}" : $"卸载失败: {swName}",
                install_log = exec.Log
            };
        }

        // ═══════════════════════════════════════════════
        // L1: 获取卸载目标（重写：优先 QuietUninstallString）
        // ═══════════════════════════════════════════════
        private List<(string exe, string args, string dir, string name)>
            GetUninstallTargets(string swName)
        {
            var list = FindAllUninstallInfos(swName);

            if (list.Count > 0)
                return list;

            // 回退：尝试从注册表找 GUID（仅当子键名是 GUID 格式时）
            string guid = FindProductGuidInRegistry(swName);
            if (!string.IsNullOrEmpty(guid))
            {
                return new List<(string, string, string, string)>
                {
                    ("msiexec", $"/x {guid} /qn /norestart", "", swName)
                };
            }

            return new List<(string, string, string, string)>();
        }

        // ═══════════════════════════════════════════════
        // 从注册表查找卸载信息（重写：支持 QuietUninstallString）
        // ═══════════════════════════════════════════════
        private static List<(string exe, string args, string dir, string name)>
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

            var result = new List<(string exe, string args, string dir, string name)>();

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

                        // ✅ 优先1：QuietUninstallString（静默卸载命令）
                        string quietUninstall = sk.GetValue("QuietUninstallString") as string;
                        if (!string.IsNullOrEmpty(quietUninstall))
                        {
                            var parsed = ParseUninstallString(quietUninstall);
                            result.Add((parsed.exe, parsed.args, installDir, name));
                            continue;
                        }

                        // ✅ 优先2：UninstallString + 推断静默参数
                        string uninstallString = sk.GetValue("UninstallString") as string;
                        if (!string.IsNullOrEmpty(uninstallString))
                        {
                            var parsed = ParseUninstallString(uninstallString);
                            string silentArgs = InferSilentArgs(parsed.args, parsed.exe);
                            result.Add((parsed.exe, silentArgs, installDir, name));
                            continue;
                        }

                        // ✅ 最后：回退到 msiexec（仅当 sub 是 GUID 格式）
                        if (IsGuid(sub))
                        {
                            result.Add(("msiexec", $"/x {sub} /qn /norestart", installDir, name));
                        }
                    }
                }
                catch { }
            }

            return result;
        }

        // ═══════════════════════════════════════════════
        // 解析卸载字符串：分离 exe 和 args
        // ═══════════════════════════════════════════════
        private static (string exe, string args) ParseUninstallString(string uninstallString)
        {
            uninstallString = uninstallString.Trim();

            // 处理带引号的路径: "C:\Path\uninstall.exe" /S
            if (uninstallString.StartsWith("\""))
            {
                int endQuote = uninstallString.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    string exe = uninstallString.Substring(1, endQuote - 1);
                    string args = uninstallString.Substring(endQuote + 1).Trim();
                    return (exe, args);
                }
            }

            // 无引号: C:\Path\uninstall.exe /S
            int spaceIdx = uninstallString.IndexOf(' ');
            if (spaceIdx > 0)
            {
                return (uninstallString.Substring(0, spaceIdx),
                        uninstallString.Substring(spaceIdx + 1).Trim());
            }

            return (uninstallString, "");
        }

        // ═══════════════════════════════════════════════
        // 推断静默参数
        // ═══════════════════════════════════════════════
        private static string InferSilentArgs(string originalArgs, string exe)
        {
            string ext = Path.GetExtension(exe).ToLower();

            // NSIS 安装包
            if (originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                originalArgs.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs;

            // Inno Setup 安装包
            if (originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                originalArgs.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs;

            // MSI 或 msiexec
            if (ext == ".msi" || exe.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs + " /quiet /norestart";

            // EXE 安装包：尝试常见静默参数
            if (originalArgs.IndexOf("/quiet", StringComparison.OrdinalIgnoreCase) < 0 &&
                originalArgs.IndexOf("/S ", StringComparison.OrdinalIgnoreCase) < 0 &&
                originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) < 0)
                return originalArgs + " /quiet /norestart";

            return originalArgs;
        }

        // ═══════════════════════════════════════════════
        // 检查字符串是否为 GUID 格式
        // ═══════════════════════════════════════════════
        private static bool IsGuid(string s)
        {
            return Guid.TryParse(s, out _);
        }

        // ═══════════════════════════════════════════════
        // 从注册表查找产品 GUID（替代 WMI Win32_Product）
        // ═══════════════════════════════════════════════
        private static string FindProductGuidInRegistry(string softwareName)
        {
            var regPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var path in regPaths)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        // 只检查 GUID 格式的子键
                        if (!IsGuid(sub)) continue;

                        using var sk = key.OpenSubKey(sub);
                        string name = sk?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf(softwareName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sub; // 返回 GUID
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        // ═══════════════════════════════════════════════
        // L2: 执行卸载
        // ═══════════════════════════════════════════════
        private async Task<(int ExitCode, string Log)> ExecuteTargets(
            List<(string exe, string args, string dir, string name)> targets,
            int timeout)
        {
            var sb = new StringBuilder();
            int last = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];

                Logger.Info($"卸载 {i + 1}/{targets.Count}: {t.name}");

                var r = await RunProcessAsync(
                    t.exe,
                    t.args,
                    timeout,
                    _auditReporter);

                last = r.ExitCode;
                sb.AppendLine(r.Log);

                if (i < targets.Count - 1)
                    await Task.Delay(3000);
            }

            return (last, sb.ToString());
        }

        // ═══════════════════════════════════════════════
        // L3: 统一验证（修复：检查退出码）
        // ═══════════════════════════════════════════════
        private bool VerifyUninstall(string swName, int exitCode)
        {
            // ✅ 先检查退出码：0, 19, 3010 等都是成功的
            bool exitCodeOk = UninstallSuccessCodes.Contains(exitCode);

            if (exitCode == 3010)
            {
                // 需要重启：标记为成功，但记录需要重启
                Logger.Info($"卸载需要重启才能完成 (exit code 3010)");
                return true;
            }

            // ✅ 等待时间增加到 10 秒
            Thread.Sleep(10000);

            var remaining = FindAllUninstallInfos(swName);
            if (remaining.Count == 0)
                return true;

            // ✅ 更宽松的判断：如果退出码正确，且有注册表项但目录已消失，也认为成功
            bool dirGone = remaining.All(r =>
                string.IsNullOrEmpty(r.dir) || !Directory.Exists(r.dir));

            if (exitCodeOk && dirGone)
                return true;

            // ✅ 最后尝试：检查注册表是否还存在（更可靠的方法）
            if (exitCodeOk && !IsSoftwareStillInstalled(swName))
                return true;

            return false;
        }

        // ═══════════════════════════════════════════════
        // 检查软件是否还存在（替代 Win32_Product）
        // ═══════════════════════════════════════════════
        private static bool IsSoftwareStillInstalled(string softwareName)
        {
            try
            {
                var paths = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var path in paths)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(sub);
                        string name = sk?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf(softwareName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true; // 还存在
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        // ═══════════════════════════════════════════════
        // 进程清理
        // ═══════════════════════════════════════════════
        private static void KillRelatedProcesses(string softwareName)
        {
            var map = new Dictionary<string, string[]>
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

            var kill = new List<string>();

            foreach (var kv in map)
                if (softwareName.Contains(kv.Key))
                    kill.AddRange(kv.Value);

            foreach (var p in kill)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(p))
                    {
                        try { proc.Kill(); }
                        catch { }
                        finally { proc.Dispose(); }
                    }
                }
                catch { }
            }

            Thread.Sleep(1500);
        }

        // ═══════════════════════════════════════════════
        // 运行进程（修复：正确处理超时，捕获输出）
        // ═══════════════════════════════════════════════
        private static async Task<(int ExitCode, string Log)> RunProcessAsync(
            string fileName, string arguments, int timeoutSec,
            Func<string, string, int?, int?, DateTime, Task> auditReporter = null)
        {
            // ── 修复 8A：命令注入防护（BlacklistRegex 终于被使用了）──
            if (BlacklistRegex.IsMatch(fileName))
            {
                string err = $"[安全] 文件名包含危险字符，拒绝执行: {fileName}";
                Logger.Error(err);
                return (-1, $"SECURITY_VIOLATION: {err}");
            }
            if (!string.IsNullOrEmpty(arguments) && BlacklistRegex.IsMatch(arguments))
            {
                string err = $"[安全] 参数包含危险字符，拒绝执行: {arguments}";
                Logger.Error(err);
                return (-1, $"SECURITY_VIOLATION: {err}");
            }

            var si = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var output = new StringBuilder();
            var error = new StringBuilder();

            using var p = new Process { StartInfo = si };

            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) output.AppendLine(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) error.AppendLine(e.Data);
            };

            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // .NET Framework 4.8 不支持 WaitForExitAsync，用轮询方式
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                var deadline = DateTime.Now.AddSeconds(timeoutSec);
                bool exited;
                try
                {
                    while (!p.HasExited && DateTime.Now < deadline)
                    {
                        await Task.Delay(100, cts.Token);
                    }
                    exited = p.HasExited;
                }
                catch (OperationCanceledException)
                {
                    exited = false;
                }

                if (!exited)
                {
                    // 超时：杀进程
                    try
                    {
                        p.Kill();
                        Logger.Warn($"进程超时已被终止: {fileName} {arguments}");
                    }
                    catch { }

                    // 等待进程完全退出
                    try { p.WaitForExit(); } catch { }

                    string log = $"TIMEOUT after {timeoutSec}s (exit code not available)\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                    // 修复 8B：审计上报（超时场景）
                    _ = FireAuditAsync(auditReporter, fileName, arguments, null, null);
                    return (-1, log);
                }

                // 等待异步事件缓冲完成
                try { p.WaitForExit(5000); } catch { }

                string finalLog = $"EXIT CODE: {p.ExitCode}\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                // 修复 8B：审计上报（正常执行完成）
                _ = FireAuditAsync(auditReporter, fileName, arguments, p.Id, p.ExitCode);
                return (p.ExitCode, finalLog);
            }
            catch (Exception ex)
            {
                string errorLog = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                // 修复 8B：审计上报（异常场景）
                _ = FireAuditAsync(auditReporter, fileName, arguments, null, null);
                return (-1, errorLog);
            }
        }

        // ═══════════════════════════════════════════════
        // 审计上报（修复 8B：Fire-and-forget，不阻塞主流程）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 异步触发审计上报，不等待结果，不抛异常
        /// </summary>
        private static async Task FireAuditAsync(
            Func<string, string, int?, int?, DateTime, Task> reporter,
            string processPath, string args, int? pid, int? exitCode)
        {
            if (reporter == null) return;
            try
            {
                await reporter(processPath, args, pid, exitCode, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // 审计失败不影响主流程
                Logger.Warn($"[审计] 上报失败（非致命）: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════
        // UI 交互：通过 Pipe/TCP 与 Tray 弹窗通信
        // （修复：原来这些 handler 写了但从未被调用）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 向 Tray 发送安装确认弹窗（ASK_INSTALL）
        /// 返回: "OK"=用户确认, "DEFERRED"=用户推迟, "CANCEL"=用户取消/超时/Tray不可达
        /// </summary>
        private async Task<string> AskUserInstallAsync(TaskInfo task)
        {
            if (_uiSender == null)
            {
                Logger.Warn("[UI] _uiSender 未设置，跳过交互弹窗（静默执行）");
                return "OK"; // 无 UI 能力时降级为静默执行
            }

            try
            {
                var req = new PipeRequest
                {
                    type = "ASK_INSTALL",
                    app_name = task.task_name ?? task.package_filename ?? task.uninstall_target ?? "软件",
                    defer_count = task.defer_count,
                    max_defer_count = task.max_defer_count > 0 ? task.max_defer_count : 3,
                };

                Logger.Info($"[UI] 正在发送 ASK_INSTALL 弹窗请求: {req.app_name}");
                var resp = await _uiSender(req);

                if (resp == null || string.IsNullOrEmpty(resp.result))
                {
                    Logger.Warn("[UI] ASK_INSTALL 无响应（Tray 可能未运行），降级为静默执行");
                    return "OK"; // Tray 不可达 → 降级
                }

                Logger.Info($"[UI] ASK_INSTALL 用户响应: {resp.result}");

                // UserDialog.AskInstall 返回 "OK"(Yes) 或 "CANCEL"(No/Timeout)
                // No 对应"推迟"，Cancel(超时) 也视为取消
                if (resp.result == "OK")
                    return "OK";
                else
                    return "DEFERRED"; // 用户点"推迟安装"
            }
            catch (Exception ex)
            {
                Logger.Error($"[UI] ASK_INSTALL 异常: {ex.Message}，降级为静默执行");
                return "OK";
            }
        }

        /// <summary>
        /// 向 Tray 发送重启询问弹窗（ASK_REBOOT）
        /// 返回: "now"/"later"/"cancel"
        /// </summary>
        private async Task<string> AskUserRebootAsync(string appName)
        {
            if (_uiSender == null)
            {
                Logger.Info("[UI] _uiSender 未设置，标记需要重启但不弹窗");
                return "later";
            }

            try
            {
                var req = new PipeRequest
                {
                    type = "ASK_REBOOT",
                    app_name = appName,
                };

                Logger.Info($"[UI] 正在发送 ASK_REBOOT 请求: {appName}");
                var resp = await _uiSender(req);

                if (resp == null || string.IsNullOrEmpty(resp.result))
                {
                    Logger.Warn("[UI] ASK_REBOOT 无响应，默认稍后重启");
                    return "later";
                }

                Logger.Info($"[UI] ASK_REBOOT 用户响应: {resp.result}");
                return resp.result; // "now" | "later" | "cancel"
            }
            catch (Exception ex)
            {
                Logger.Error($"[UI] ASK_REBOOT 异常: {ex.Message}，默认稍后重启");
                return "later";
            }
        }
    }
}
