using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using System.Threading.Tasks;
using ITAsset4.Common;

namespace ITAsset4.Service
{
    internal static class Program
    {
        private const string ServiceName = "ITAsset4Agent";
        private const string ServiceDisplayName = "ITAsset4 资产管理代理";
        private const string ServiceDesc = "ITAsset4 终端资产采集与软件部署代理服务";

        /// <summary>
        /// 静态构造注册全局未处理异常捕获，防止后台线程/未观察Task
        /// 导致进程静默崩溃而丢失诊断信息。
        /// </summary>
        static Program()
        {
            // 1. 域级未处理异常（后台线程、线程池等）
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Logger.Error($"[FATAL] AppDomain 未处理异常: {ex?.Message}");
                if (ex != null) Logger.Error($"[FATAL] 堆栈: {ex.StackTrace}");
            };

            // 2. 未观察的 Task 异常（fire-and-forget 中未 await 的 Task）
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Logger.Error($"[FATAL] 未观察Task异常: {e.Exception?.Message}");
                if (e.Exception != null) Logger.Error($"[FATAL] 堆栈: {e.Exception.StackTrace}");
                e.SetObserved(); // 标记已观察，防止进程崩溃
            };
        }

        static void Main(string[] args)
        {
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

            // ── 非交互式（SCM 启动）→ Windows Service 模式 ─────────────────
            if (!Environment.UserInteractive)
            {
                try
                {
                    ServiceBase.Run(new ServiceBase[] { new AgentService() });
                }
                catch (Exception ex)
                {
                    Logger.Error($"ServiceBase.Run 异常退出: {ex.Message}");
                    Logger.Error($"堆栈: {ex.StackTrace}");
                }
                return;
            }

            // ── 交互式（直接双击/无参数）→ 控制台调试模式 ──────────────────
            RunConsole(args);
        }

        private static void RunConsole(string[] args)
        {
            try
            {
                Console.Title = "ITAsset4.Service [控制台模式]";
                Console.WriteLine("==== ITAsset4 Agent [控制台模式] ====");
                Console.WriteLine("按 Ctrl+C 退出");

                var service = new AgentService();
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
            Console.WriteLine($"[安装完成] 服务 '{ServiceName}' 已安装并启动。");
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
