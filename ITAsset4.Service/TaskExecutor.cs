using ITAsset4.Common;
using ITAsset4.Common.Tasks;
using ITAsset4.Service.TaskHandlers;
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
using Microsoft.Win32;

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
        // 修复：此前把双引号也列入黑名单，导致 INSTALLDIR="C:\Program Files\XXX" 这类
        // 路径含空格必须加引号的正常安装/卸载参数被直接拒绝执行。这里走的是
        // Process.Start(UseShellExecute=false)，参数不经过 cmd.exe/powershell 解析，
        // 双引号本身不构成注入风险，真正需要拦的是换行/反引号/<>|&^ 这类只有
        // "目标本身是解释器"时才有意义的字符。
        private static readonly Regex BlacklistRegex =
            new Regex(@"[\r\n`<>|&^]", RegexOptions.Compiled);

        // P0 安全：禁止重启/关机关键词黑名单（服务端+客户端两端共享规则）
        // 纵深防御：服务端已经拦过一次，但客户端作为最后防线必须独立检查
        // 注意：Restart-Computer/Stop-Computer 必须在 \brestart\b 前面，
        //       否则 bare restart 会先捕获 Restart-Computer 中的 restart 前缀
        private static readonly Regex RebootBlacklist =
            new Regex(
                @"Restart-Computer|Stop-Computer" +
                @"|wuauclt\s+/restart" +
                @"|shutdown\.exe" +
                @"|\bshutdown\b" +
                @"|\breboot\b" +
                @"|\brestart\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 去掉脚本里的注释行/行尾注释，避免"注释里提到 reboot/restart"被 RebootBlacklist 误判为
        /// 真实的重启命令。覆盖 PowerShell/CMD 常见的 # / REM / :: 注释写法。
        /// </summary>
        private static string StripComments(string commandText)
        {
            if (string.IsNullOrEmpty(commandText)) return commandText ?? "";
            var lines = new List<string>();
            foreach (var raw in commandText.Split('\n'))
            {
                string line = raw;
                string trimmed = line.Trim();
                string upper = trimmed.ToUpperInvariant();
                if (trimmed.StartsWith("#") || upper.StartsWith("REM ") || upper == "REM" || trimmed.StartsWith("::"))
                    continue;
                int hashPos = line.IndexOf('#');
                if (hashPos >= 0) line = line.Substring(0, hashPos);
                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

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
        // 主入口 — 使用 TaskHandlerFactory 策略模式分发
        // ═══════════════════════════════════════════════
        public async Task<TaskResult> ExecuteAsync(TaskInfo task, string serial, string deviceSecret)
        {
            Logger.Info($"[任务 {task.target_id}] {task.task_name} (interactive={task.interactive}, type={task.task_type})");

            var factory = new TaskHandlerFactory();
            factory.Register(new InstallHandler());
            factory.Register(new UninstallHandler());
            factory.Register(new RunCommandHandler());
            factory.Register(new RegistryHandler());
            factory.Register(new CleanupHandler());

            var ctx = new TaskContext(task, _cfg, null!, default,
                _uiSender, _auditReporter, serial, deviceSecret);

            return await factory.ExecuteAsync(ctx);
        }

        // ═══════════════════════════════════════════════
        // Install（提取自原 ExecuteAsync 安装分支）
        // ═══════════════════════════════════════════════
        internal static async Task<TaskResult> ExecuteInstallAsync(
            TaskInfo task, Downloader dl, string serial, string deviceSecret,
            Func<PipeRequest, Task<PipeResponse>> uiSender,
            Func<string, string, int?, int?, DateTime, Task> auditReporter)
        {
            // ── 安装任务：interactive 检查 ──
            if (task.interactive)
            {
                var userChoice = await AskUserInstallAsync(task, uiSender);
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
                Logger.Info($"[任务 {task.target_id}] 用户确认，开始安装");
            }

            // ── 下载 + 执行安装 ───────────────────────────────
            string pkgPath = await dl.DownloadAsync(
                task.download_url,
                task.package_filename,
                serial,
                deviceSecret,
                task.package_hash);

            var run = await RunProcessAsync(pkgPath, task.silent_args, task.timeout, auditReporter);

            // ── 判断成功/失败（含重启退出码检测）────────────────
            bool success;
            bool needsReboot = RebootRequiredCodes.Contains(run.ExitCode);

            if (task.success_codes != null && task.success_codes.Count > 0)
                success = task.success_codes.Contains(run.ExitCode) || needsReboot;
            else
                success = run.ExitCode == 0 || needsReboot;

            // ── 需要重启时询问用户 ──
            if (success && needsReboot)
            {
                Logger.Info($"[任务 {task.target_id}] 安装需要重启 (exit code {run.ExitCode})");

                var rebootChoice = await AskUserRebootAsync(
                    task.package_filename ?? task.task_name ?? "软件", uiSender);
                var rebootAction = rebootChoice switch
                {
                    "now"    => "reboot_now",
                    "later"  => "reboot_required",
                    _        => "reboot_required",
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
        internal static async Task<TaskResult> ExecuteUninstallAsync(
            TaskInfo task,
            Func<PipeRequest, Task<PipeResponse>> uiSender,
            Func<string, string, int?, int?, DateTime, Task> auditReporter)
        {
            string swName = task.uninstall_target;
            Logger.Info($"卸载任务: {swName}");

            // ── interactive 检查 ──
            if (task.interactive)
            {
                var userChoice = await AskUserInstallAsync(task, uiSender);
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
            var exec = await ExecuteTargets(targets, task.timeout, auditReporter);

            // ===== L3: 统一验证 =====
            bool needsReboot = RebootRequiredCodes.Contains(exec.ExitCode);
            bool success = VerifyUninstall(swName, exec.ExitCode);

            // ── 需要重启时询问用户 ──
            if (success && needsReboot)
            {
                Logger.Info($"[任务 {task.target_id}] 卸载需要重启 (exit code {exec.ExitCode})");

                var rebootChoice = await AskUserRebootAsync(swName, uiSender);
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
        // ⭐ 命令类任务（run_command / registry / cleanup）
        //    由服务端下发，已通过设备认证，属于受信管理指令。
        // ═══════════════════════════════════════════════

        #region 命令类任务 DTO
        private class RegistryOpDto
        {
            public string action { get; set; } = "set";
            public string root   { get; set; } = "HKLM";
            public string subkey { get; set; } = "";
            public string name   { get; set; } = "";
            public string value  { get; set; } = "";
            public string type   { get; set; } = "string";
            // 写入前原值快照（读原值时挂到本 op，避免与 ops 列表按索引错位配对）
            public object before { get; set; } = null;
        }
        private class CleanupPathDto
        {
            public string path      { get; set; } = "";
            public bool   recursive { get; set; }
        }
        #endregion

        // ── run_command：写入临时脚本并以 cmd/powershell 执行 ──────
        internal static async Task<TaskResult> ExecuteCommandAsync(
            TaskInfo task,
            Func<string, string, int?, int?, DateTime, Task> auditReporter)
        {
            Logger.Info($"[命令任务 {task.target_id}] 执行脚本 (interpreter={task.interpreter}, run_as={task.run_as})");

            string ext = (task.interpreter ?? "").ToLower() == "powershell" ? "ps1" : "bat";
            string file = Path.Combine(Path.GetTempPath(), $"itasset_cmd_{Guid.NewGuid():N}.{ext}");
            try
            {
                File.WriteAllText(file, task.command ?? "", Encoding.UTF8);

                // ── P0 安全纵深防御：客户端独立扫描重启/关机关键词 ──
                var rebootHit = RebootBlacklist.Match(StripComments(task.command ?? ""));
                if (rebootHit.Success)
                {
                    Logger.Error($"[安全 命令任务 {task.target_id}] 命令包含禁止的重启/关机操作，拒绝执行。命中: {rebootHit.Value}");
                    return new TaskResult
                    {
                        success    = false,
                        exit_code  = -1,
                        message    = $"SECURITY_BLOCKED: 命令包含禁止的重启/关机操作（命中: {rebootHit.Value}）",
                    };
                }

                string exe, args;
                if ((task.interpreter ?? "").ToLower() == "powershell")
                {
                    exe  = "powershell.exe";
                    args = $"-ExecutionPolicy Bypass -NoProfile -File \"{file}\"";
                }
                else
                {
                    exe  = "cmd.exe";
                    args = $"/c \"{file}\"";
                }

                var run = await RunProcessAsync(exe, args, task.timeout, auditReporter,
                    applySecurityCheck: false);

                bool success = task.success_codes != null && task.success_codes.Count > 0
                    ? task.success_codes.Contains(run.ExitCode)
                    : run.ExitCode == 0;

                return new TaskResult
                {
                    success    = success,
                    exit_code  = run.ExitCode,
                    message    = success ? "脚本执行成功" : $"脚本执行失败 (exit={run.ExitCode})",
                    install_log = run.Log,
                };
            }
            catch (Exception ex)
            {
                return new TaskResult { success = false, message = $"脚本准备失败: {ex.Message}" };
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }

        // ── registry：应用注册表操作（HKLM 或 目标用户 HKCU）────
        internal static async Task<TaskResult> ExecuteRegistryAsync(TaskInfo task)
        {
            Logger.Info($"[注册表任务 {task.target_id}] 应用注册表操作 (run_as={task.run_as})");

            List<RegistryOpDto> ops;
            try
            {
                ops = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RegistryOpDto>>(
                    task.registry_ops ?? "[]") ?? new List<RegistryOpDto>();
            }
            catch (Exception ex)
            {
                return new TaskResult { success = false, message = $"注册表操作解析失败: {ex.Message}" };
            }

            if (ops.Count == 0)
                return new TaskResult { success = false, message = "注册表操作为空" };

            // ── 读取写入前的原值，直接挂到对应 op（避免按索引配对错位）──
            foreach (var op in ops)
            {
                try
                {
                    RegistryKey root = op.root == "HKCU" ? OpenTargetHkcu(task.run_as) : Registry.LocalMachine;
                    if (root == null) continue;
                    using (var sk = root.OpenSubKey(op.subkey))
                    {
                        if (sk != null && !string.IsNullOrEmpty(op.name))
                        {
                            var beforeVal = sk.GetValue(op.name);
                            var beforeKind = sk.GetValueKind(op.name);
                            op.before = new
                            {
                                op = "read_before",
                                root = op.root,
                                subkey = op.subkey,
                                name = op.name,
                                value = beforeVal?.ToString() ?? "",
                                type = beforeKind.ToString(),
                            };
                        }
                        // name 为空（删除整个子键）或键不存在时 before 保持 null；
                        // 回滚/校验按 before==null 处理，不再与 ops 索引错位配对。
                    }
                }
                catch { /* 读原值失败不阻塞主流程 */ }
            }

            var sb = new StringBuilder();
            bool allOk = true;
            foreach (var op in ops)
            {
                try
                {
                    RegistryKey root = op.root == "HKCU" ? OpenTargetHkcu(task.run_as) : Registry.LocalMachine;
                    if (root == null) { sb.AppendLine($"[SKIP] 无法打开根键 {op.root}"); allOk = false; continue; }
                    bool writableRoot = op.root != "HKCU";
                    using (var actualRoot = writableRoot ? root : root)
                    {
                        sb.AppendLine("  " + ApplyRegistryOp(op, actualRoot));
                    }
                }
                catch (Exception ex)
                {
                    allOk = false;
                    sb.AppendLine($"[ERROR] {op.subkey}\\{op.name}: {ex.Message}");
                }
            }

            // ── 写入后读回验证（遍历所有 ops，使用 op.before 配对，不再按索引）──
            object verifySnapshot = null;
            {
                var verifiedOps = new List<object>();
                foreach (var op in ops)
                {
                    try
                    {
                        RegistryKey root = op.root == "HKCU" ? OpenTargetHkcu(task.run_as) : Registry.LocalMachine;
                        if (root != null && !string.IsNullOrEmpty(op.name))
                        {
                            using (var sk = root.OpenSubKey(op.subkey))
                            {
                                var afterVal = sk?.GetValue(op.name);
                                verifiedOps.Add(new
                                {
                                    before = op.before,
                                    after = new
                                    {
                                        root = op.root,
                                        subkey = op.subkey,
                                        name = op.name,
                                        value = afterVal?.ToString() ?? "",
                                    },
                                });
                            }
                        }
                    }
                    catch { /* 读回失败不阻塞 */ }
                }
                if (verifiedOps.Count > 0)
                {
                    verifySnapshot = new { ops = verifiedOps };
                }
            }

            return new TaskResult
            {
                success    = allOk,
                exit_code  = allOk ? 0 : 1,
                message    = allOk ? "注册表操作全部成功" : "部分注册表操作失败",
                install_log = sb.ToString(),
                verify_snapshot = verifySnapshot,
            };
        }

        private static string ApplyRegistryOp(RegistryOpDto op, RegistryKey root)
        {
            string target = $"{op.root}\\{op.subkey}" + (string.IsNullOrEmpty(op.name) ? "" : $"\\{op.name}");

            if ((op.action ?? "set").ToLower() == "delete")
            {
                using var sk = root.OpenSubKey(op.subkey, writable: true);
                if (sk == null) return $"[DELETE 跳过] 不存在: {target}";
                if (string.IsNullOrEmpty(op.name))
                {
                    root.DeleteSubKeyTree(op.subkey, throwOnMissingSubKey: false);
                    return $"[DELETE 子键] {target}";
                }
                sk.DeleteValue(op.name, throwOnMissingValue: false);
                return $"[DELETE 值] {target}";
            }

            // set
            using var wsk = root.CreateSubKey(op.subkey, writable: true);
            if (wsk == null) return $"[SET 失败] 无法创建子键: {op.root}\\{op.subkey}";

            string kind = (op.type ?? "string").ToLower();
            if (kind == "dword")
            {
                if (int.TryParse(op.value, out var dv)) wsk.SetValue(op.name, dv, RegistryValueKind.DWord);
                else return $"[SET 失败] 非整数 dword: {target}={op.value}";
            }
            else if (kind == "qword")
            {
                if (long.TryParse(op.value, out var qv)) wsk.SetValue(op.name, qv, RegistryValueKind.QWord);
                else return $"[SET 失败] 非整数 qword: {target}={op.value}";
            }
            else if (kind == "expand")
            {
                wsk.SetValue(op.name, op.value ?? "", RegistryValueKind.ExpandString);
            }
            else
            {
                wsk.SetValue(op.name, op.value ?? "", RegistryValueKind.String);
            }
            return $"[SET] {target} = {op.value} ({kind})";
        }

        // ── cleanup：删除指定文件/目录（带安全校验）────────────
        internal static async Task<TaskResult> ExecuteCleanupAsync(TaskInfo task)
        {
            Logger.Info($"[清理任务 {task.target_id}] 删除文件/目录");

            List<CleanupPathDto> items;
            try
            {
                items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<CleanupPathDto>>(
                    task.cleanup_paths ?? "[]") ?? new List<CleanupPathDto>();
            }
            catch (Exception ex)
            {
                return new TaskResult { success = false, message = $"清理列表解析失败: {ex.Message}" };
            }

            if (items.Count == 0)
                return new TaskResult { success = false, message = "清理列表为空" };

            var sb = new StringBuilder();
            bool allOk = true;
            foreach (var it in items)
            {
                string p = (it.path ?? "").Trim();
                if (!IsPathSafeToDelete(p))
                {
                    sb.AppendLine($"[SKIP 不安全] {p}");
                    allOk = false;
                    continue;
                }
                try
                {
                    if (File.Exists(p))
                    {
                        File.Delete(p);
                        sb.AppendLine($"[删除文件] {p}");
                    }
                    else if (Directory.Exists(p))
                    {
                        Directory.Delete(p, it.recursive);
                        sb.AppendLine($"[删除目录{(it.recursive ? "(递归)" : "")}] {p}");
                    }
                    else
                    {
                        sb.AppendLine($"[跳过 不存在] {p}");
                    }
                }
                catch (Exception ex)
                {
                    allOk = false;
                    sb.AppendLine($"[ERROR] {p}: {ex.Message}");
                }
            }

            return new TaskResult
            {
                success    = allOk,
                exit_code  = allOk ? 0 : 1,
                message    = allOk ? "清理完成" : "部分清理失败",
                install_log = sb.ToString(),
            };
        }

        /// <summary>
        /// 清理路径安全校验：拒绝系统/程序目录、盘符根、空路径等，防止误删系统。
        /// </summary>
        internal static bool IsPathSafeToDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string driveRoot = Path.GetPathRoot(full);
                if (full.TrimEnd('\\', '/') == driveRoot.TrimEnd('\\', '/')) return false;

                string[] protectedDirs =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetEnvironmentVariable("SystemRoot") ?? "",
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                };
                foreach (var d in protectedDirs)
                {
                    if (!string.IsNullOrEmpty(d) &&
                        full.StartsWith(d.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        // ── HKCU 上下文解析：run_as=user 时打开交互登录用户的配置单元 ──
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ConvertSidToStringSid(IntPtr pSid, out IntPtr ptr);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr hToken, int tokenInformationClass,
            IntPtr tokenInformation, int tokenInfoLength, out int returnLength);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        internal static RegistryKey OpenTargetHkcu(string runAs)
        {
            if (string.IsNullOrEmpty(runAs) || runAs.ToLower() != "user")
                return Registry.CurrentUser;

            try
            {
                int sid = SessionManager.GetActiveUserSessionId();
                if (sid < 0)
                {
                    Logger.Warn("[Registry] 无活动会话，HKCU 回退到 SYSTEM 配置单元");
                    return Registry.CurrentUser;
                }
                if (!WTSQueryUserToken((uint)sid, out IntPtr hToken))
                {
                    Logger.Warn($"[Registry] WTSQueryUserToken 失败 0x{Marshal.GetLastWin32Error():X8}");
                    return Registry.CurrentUser;
                }
                try
                {
                    const int TOKEN_USER = 1;
                    GetTokenInformation(hToken, TOKEN_USER, IntPtr.Zero, 0, out int needed);
                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!GetTokenInformation(hToken, TOKEN_USER, buf, needed, out _))
                        {
                            Logger.Warn("[Registry] GetTokenInformation 失败");
                            return Registry.CurrentUser;
                        }
                        IntPtr pSid = Marshal.ReadIntPtr(buf);
                        if (!ConvertSidToStringSid(pSid, out IntPtr pSidStr))
                        {
                            Logger.Warn("[Registry] ConvertSidToStringSid 失败");
                            return Registry.CurrentUser;
                        }
                        string sidStr = Marshal.PtrToStringAuto(pSidStr);
                        LocalFree(pSidStr);
                        var key = Registry.Users.OpenSubKey(sidStr, writable: true);
                        if (key != null)
                        {
                            Logger.Info($"[Registry] 已打开交互用户 HKCU: {sidStr}");
                            return key;
                        }
                        Logger.Warn($"[Registry] 打开用户 HKCU 失败(配置单元可能未加载): {sidStr}");
                        return Registry.CurrentUser;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(hToken); }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Registry] 解析用户 HKCU 异常: {ex.Message}");
                return Registry.CurrentUser;
            }
        }

        // ═══════════════════════════════════════════════
        // L1: 获取卸载目标
        // ═══════════════════════════════════════════════
        internal static List<(string exe, string args, string dir, string name)>
            GetUninstallTargets(string swName)
        {
            var list = FindAllUninstallInfos(swName);

            if (list.Count > 0)
                return list;

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
        // 从注册表查找卸载信息
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

                        string quietUninstall = sk.GetValue("QuietUninstallString") as string;
                        if (!string.IsNullOrEmpty(quietUninstall))
                        {
                            var parsed = ParseUninstallString(quietUninstall);
                            result.Add((parsed.exe, parsed.args, installDir, name));
                            continue;
                        }

                        string uninstallString = sk.GetValue("UninstallString") as string;
                        if (!string.IsNullOrEmpty(uninstallString))
                        {
                            var parsed = ParseUninstallString(uninstallString);
                            string silentArgs = InferSilentArgs(parsed.args, parsed.exe);
                            result.Add((parsed.exe, silentArgs, installDir, name));
                            continue;
                        }

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
        internal static (string exe, string args) ParseUninstallString(string uninstallString)
        {
            uninstallString = uninstallString.Trim();

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

            if (originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                originalArgs.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs;

            if (originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                originalArgs.IndexOf("/VERYSILENT", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs;

            if (ext == ".msi" || exe.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0)
                return originalArgs + " /quiet /norestart";

            if (originalArgs.IndexOf("/quiet", StringComparison.OrdinalIgnoreCase) < 0 &&
                originalArgs.IndexOf("/S ", StringComparison.OrdinalIgnoreCase) < 0 &&
                originalArgs.IndexOf("/SILENT", StringComparison.OrdinalIgnoreCase) < 0)
                return originalArgs + " /quiet /norestart";

            return originalArgs;
        }

        private static bool IsGuid(string s)
        {
            return Guid.TryParse(s, out _);
        }

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
                        if (!IsGuid(sub)) continue;

                        using var sk = key.OpenSubKey(sub);
                        string name = sk?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrEmpty(name) &&
                            name.IndexOf(softwareName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return sub;
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
        internal static async Task<(int ExitCode, string Log)> ExecuteTargets(
            List<(string exe, string args, string dir, string name)> targets,
            int timeout,
            Func<string, string, int?, int?, DateTime, Task> auditReporter)
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
                    auditReporter);

                last = r.ExitCode;
                sb.AppendLine(r.Log);

                if (i < targets.Count - 1)
                    await Task.Delay(3000);
            }

            return (last, sb.ToString());
        }

        // ═══════════════════════════════════════════════
        // L3: 统一验证
        // ═══════════════════════════════════════════════
        internal static bool VerifyUninstall(string swName, int exitCode)
        {
            bool exitCodeOk = UninstallSuccessCodes.Contains(exitCode);

            if (exitCode == 3010)
            {
                Logger.Info($"卸载需要重启才能完成 (exit code 3010)");
                return true;
            }

            Thread.Sleep(10000);

            var remaining = FindAllUninstallInfos(swName);
            if (remaining.Count == 0)
                return true;

            bool dirGone = remaining.All(r =>
                string.IsNullOrEmpty(r.dir) || !Directory.Exists(r.dir));

            if (exitCodeOk && dirGone)
                return true;

            if (exitCodeOk && !IsSoftwareStillInstalled(swName))
                return true;

            return false;
        }

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
                            return true;
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
        internal static void KillRelatedProcesses(string softwareName)
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
        // 运行进程
        // ═══════════════════════════════════════════════
        internal static async Task<(int ExitCode, string Log)> RunProcessAsync(
            string fileName, string arguments, int timeoutSec,
            Func<string, string, int?, int?, DateTime, Task> auditReporter = null,
            bool applySecurityCheck = true)
        {
            if (applySecurityCheck)
            {
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
                    try
                    {
                        p.Kill();
                        Logger.Warn($"进程超时已被终止: {fileName} {arguments}");
                    }
                    catch { }

                    try { p.WaitForExit(); } catch { }

                    string log = $"TIMEOUT after {timeoutSec}s (exit code not available)\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                    _ = FireAuditAsync(auditReporter, fileName, arguments, null, null);
                    return (-1, log);
                }

                try { p.WaitForExit(5000); } catch { }

                string finalLog = $"EXIT CODE: {p.ExitCode}\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                _ = FireAuditAsync(auditReporter, fileName, arguments, p.Id, p.ExitCode);
                return (p.ExitCode, finalLog);
            }
            catch (Exception ex)
            {
                string errorLog = $"EXCEPTION: {ex.GetType().Name}: {ex.Message}\nSTDOUT:\n{output}\nSTDERR:\n{error}";
                _ = FireAuditAsync(auditReporter, fileName, arguments, null, null);
                return (-1, errorLog);
            }
        }

        // ═══════════════════════════════════════════════
        // 审计上报
        // ═══════════════════════════════════════════════
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
                Logger.Warn($"[审计] 上报失败（非致命）: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════
        // UI 交互：通过 Pipe/TCP 与 Tray 弹窗通信
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 向 Tray 发送安装确认弹窗（ASK_INSTALL）
        /// 返回: "OK"=用户确认, "DEFERRED"=用户推迟, "CANCEL"=用户取消/超时/Tray不可达
        /// </summary>
        internal static async Task<string> AskUserInstallAsync(TaskInfo task,
            Func<PipeRequest, Task<PipeResponse>> uiSender)
        {
            if (uiSender == null)
            {
                Logger.Warn("[UI] uiSender 未设置，跳过交互弹窗（静默执行）");
                return "OK";
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
                var resp = await uiSender(req);

                if (resp == null || string.IsNullOrEmpty(resp.result))
                {
                    Logger.Warn("[UI] ASK_INSTALL 无响应（Tray 可能未运行），降级为静默执行");
                    return "OK";
                }

                Logger.Info($"[UI] ASK_INSTALL 用户响应: {resp.result}");

                if (resp.result == "OK")
                    return "OK";
                else
                    return "DEFERRED";
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
        internal static async Task<string> AskUserRebootAsync(string appName,
            Func<PipeRequest, Task<PipeResponse>> uiSender)
        {
            if (uiSender == null)
            {
                Logger.Info("[UI] uiSender 未设置，标记需要重启但不弹窗");
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
                var resp = await uiSender(req);

                if (resp == null || string.IsNullOrEmpty(resp.result))
                {
                    Logger.Warn("[UI] ASK_REBOOT 无响应，默认稍后重启");
                    return "later";
                }

                Logger.Info($"[UI] ASK_REBOOT 用户响应: {resp.result}");
                return resp.result;
            }
            catch (Exception ex)
            {
                Logger.Error($"[UI] ASK_REBOOT 异常: {ex.Message}，默认稍后重启");
                return "later";
            }
        }
    }
}
