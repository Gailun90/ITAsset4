using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading.Tasks;
using ITAsset4.Common;
using Microsoft.Extensions.DependencyInjection;

namespace ITAsset4.Service
{
    internal static class Program
    {
        private const string ServiceName = "ITAsset4Agent";
        private const string ServiceDisplayName = "ITAsset4 资产管理代理";
        private const string ServiceDesc = "ITAsset4 终端资产采集与软件部署代理服务";

        /// <summary>
        /// 启动诊断：在所有依赖（Serilog / DI / AppConfig）加载之前，
        /// 把每一步执行记录写入 ProgramData 下的 startup.log，
        /// 确保即使后续初始化崩溃也能留下最后一步线索。
        /// </summary>
        private static void StartupTrace(string msg)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ITAsset4");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "startup.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}\n");
            }
            catch { /* 连写文件都失败就真的没办法了 */ }
        }

        static void Main(string[] args)
        {
            StartupTrace("Main entered");

            string exePath = Assembly.GetExecutingAssembly().Location;

            // ── 命令行参数处理（需要管理员权限）──────────────────────────────
            if (args.Length > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--install":
                    case "-install":
                        InstallService();
                        return;

                    case "--uninstall":
                    case "-uninstall":
                        UninstallService();
                        return;

                    case "--console":
                    case "-console":
                        RunConsole(args);
                        return;
                }
            }

            try
            {
                StartupTrace("init log + config begin");

                // ── Updater 自更新（解决鸡蛋问题：旧 Updater 无法覆盖自身 exe）──
                // 更新包里把新版 Updater 命名为 ITAsset4.Updater.new.exe（robocopy 不会锁），
                // Service 启动时（此时 Updater.exe 未运行）将其替换为正式 ITAsset4.Updater.exe。
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string newUpdater = Path.Combine(baseDir, "ITAsset4.Updater.new.exe");
                    string curUpdater = Path.Combine(baseDir, "ITAsset4.Updater.exe");
                    string oldUpdater = Path.Combine(baseDir, "ITAsset4.Updater.exe.old");
                    if (File.Exists(newUpdater))
                    {
                        if (File.Exists(oldUpdater)) File.Delete(oldUpdater);
                        if (File.Exists(curUpdater)) File.Move(curUpdater, oldUpdater);
                        File.Move(newUpdater, curUpdater);
                        StartupTrace("Updater 自更新完成");
                    }
                }
                catch (Exception ex) { StartupTrace($"Updater 自更新失败（不阻断）: {ex.Message}"); }

                // ── 初始化日志 ──
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ITAsset4", "logs");
                StartupTrace($"logDir={logDir}");
                LogFactory.Initialize(logDir, "Service");
                StartupTrace("LogFactory.Initialize OK");

                // 日志已就绪 → 注册全局异常捕获（用于后台线程 / 未观察 Task 的诊断）
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    var ex = e.ExceptionObject as Exception;
                    StartupTrace($"AppDomain.UnhandledException: {ex}");
                };
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    StartupTrace($"UnobservedTaskException: {e.Exception}");
                    e.SetObserved();
                };

                // ── 加载配置 ──
                string cfgPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ITAsset4", "config.ini");
                StartupTrace($"cfgPath={cfgPath} exists={File.Exists(cfgPath)}");
                if (!File.Exists(cfgPath))
                {
                    AppConfig.WriteDefault(cfgPath);
                    Logger.Info($"已生成默认配置: {cfgPath}");
                    StartupTrace("default config written");
                }
                var cfg = AppConfig.Load(cfgPath);
                StartupTrace($"AppConfig loaded (ServerUrl={cfg.ServerUrl})");

                // ── 自保护（最终形态·一）：校验 Agent 二进制签名 + 配置签名 ──
                StartupTrace("self-protection begin");
                if (!SelfProtection.VerifyAuthenticode(exePath))
                {
                    string msg = "[自保护] Authenticode 校验失败：Agent 二进制可能已被篡改。";
                    StartupTrace(msg);
                    if (SelfProtection.Enforce)
                    {
                        Logger.Error(msg);
                        return;
                    }
                }
                if (!SelfProtection.VerifyConfigSignature(cfgPath))
                {
                    string msg = "[自保护] 配置文件签名校验失败：config.ini 可能被篡改。";
                    StartupTrace(msg);
                    if (SelfProtection.Enforce)
                    {
                        Logger.Error(msg);
                        return;
                    }
                }
                StartupTrace("self-protection OK");

                // ── 构建 DI 容器 ──
                StartupTrace("build DI begin");
                var provider = ServiceSetup.BuildProvider(cfg);
                StartupTrace("build DI OK");

                // ── 非交互式（SCM 启动）→ Windows Service 模式 ──
                if (!Environment.UserInteractive)
                {
                    StartupTrace("starting as Windows Service");
                    try
                    {
                        ServiceBase.Run(new ServiceBase[] { new AgentService(provider) });
                    }
                    catch (Exception ex)
                    {
                        StartupTrace($"ServiceBase.Run FAILED: {ex}");
                        Logger.Error($"ServiceBase.Run 异常退出: {ex.Message}");
                        Logger.Error($"堆栈: {ex.StackTrace}");
                    }
                    return;
                }

                // ── 交互式（双击或无参数）→ 控制台调试模式 ──
                StartupTrace("starting as console");
                RunConsole(args, provider);
            }
            catch (Exception ex)
            {
                StartupTrace($"FATAL startup: {ex}");
                // 最后尝试走一次 Logger（如果已初始化）
                Logger.Error($"启动异常: {ex.Message}");
                Logger.Error($"堆栈: {ex.StackTrace}");
            }
        }

        private static void RunConsole(string[] args, ServiceProvider provider = null)
        {
            try
            {
                // 控制台模式下如果尚未初始化日志（直接从 -console 入参进入）
                if (provider == null)
                {
                    StartupTrace("console: init log begin");
                    var logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "ITAsset4", "logs");
                    LogFactory.Initialize(logDir, "Service");
                    StartupTrace("console: LogFactory OK");

                    string cfgPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "ITAsset4", "config.ini");
                    StartupTrace($"console: cfgPath={cfgPath} exists={File.Exists(cfgPath)}");
                    if (!File.Exists(cfgPath))
                    {
                        AppConfig.WriteDefault(cfgPath);
                        Logger.Info($"已生成默认配置: {cfgPath}");
                    }
                    var cfg = AppConfig.Load(cfgPath);
                    StartupTrace($"console: AppConfig loaded (ServerUrl={cfg.ServerUrl})");

                    StartupTrace("console: build DI begin");
                    provider = ServiceSetup.BuildProvider(cfg);
                    StartupTrace("console: build DI OK");
                }

                Console.Title = "ITAsset4.Service [控制台模式]";
                Console.WriteLine("==== ITAsset4 Agent [控制台模式] ====");
                Console.WriteLine("按 Ctrl+C 退出");

                var service = new AgentService(provider);
                service.StartConsole(args);

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    service.StopConsole();
                };

                service.WaitForStop();
            }
            catch (Exception ex)
            {
                Logger.Error($"控制台模式异常退出: {ex.Message}");
                Logger.Error($"堆栈: {ex.StackTrace}");
                Console.WriteLine($"\n[错误] {ex.Message}");
                Console.WriteLine("按任意键退出...");
                Console.ReadKey();
            }
        }

        private static void InstallService()
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            RunSc($"create {ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"{ServiceDisplayName}\"");
            RunSc($"description {ServiceName} \"{ServiceDesc}\"");
            RunSc($"start {ServiceName}");
            LockdownServiceAcl();
            Console.WriteLine($"[安装完成] 服务 '{ServiceName}' 已安装并启动。");
        }

        /// <summary>
        /// 最终形态·一：收紧 Windows 服务 ACL（服务自保护）。
        /// 仅 SYSTEM 与 Administrators 拥有完全控制权；普通交互用户（IU）仅可查询，
        /// 不可停止/暂停/修改服务，防止低权限用户借 Agent 服务提权或停用采集。
        /// 等价 SDDL：
        ///   D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)        — SYSTEM 完全控制
        ///     (A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA) — Administrators 完全控制
        ///     (A;;CCLCSWLOCRRC;;;IU)              — 交互用户仅查询/启动
        /// </summary>
        private static void LockdownServiceAcl()
        {
            try
            {
                RunSc($"sdset {ServiceName} " +
                      "\"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)" +
                      "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
                      "(A;;CCLCSWLOCRRC;;;IU)\"");
                Console.WriteLine($"[加固] 服务 '{ServiceName}' ACL 已收紧（仅 SYSTEM/Administrators 可控制）。");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[加固] 服务 ACL 收紧失败: {ex.Message}");
            }
        }

        private static void UninstallService()
        {
            RunSc($"stop {ServiceName}");
            System.Threading.Thread.Sleep(2000);
            RunSc($"delete {ServiceName}");
            Console.WriteLine($"[卸载完成] 服务 '{ServiceName}' 已删除。");
        }

        /// <summary>
        /// 增强 sc.exe 调用的健壮性
        ///   - Process.Start 返回 null
        ///   - UnauthorizedAccessException
        ///   - Win32Exception（sc.exe 未找到）
        /// </summary>
        private static void RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", arguments)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Logger.Error($"sc.exe 启动失败（返回null）: {arguments}");
                        Console.Error.WriteLine($"[错误] sc.exe 启动失败: {arguments}");
                        return;
                    }

                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.Trim());
                    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr.Trim());

                    if (p.ExitCode != 0)
                    {
                        Logger.Warn($"sc.exe 返回码 {p.ExitCode}: {arguments}");
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error($"sc.exe 权限不足: {ex.Message}");
                Console.Error.WriteLine($"[错误] 权限不足，请以管理员身份运行。");
            }
            catch (Win32Exception ex)
            {
                Logger.Error($"sc.exe 启动失败: {ex.Message}");
                Console.Error.WriteLine($"[错误] 无法启动 sc.exe：{ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Error($"sc.exe 异常: {ex.Message}");
                Console.Error.WriteLine($"[错误] sc.exe 执行失败：{ex.Message}");
            }
        }
    }
}
