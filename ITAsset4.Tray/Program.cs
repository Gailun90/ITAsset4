using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ITAsset4.Common;


namespace ITAsset4.Tray
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // ── 全局未处理异常捕获 ──
            // Tray 是后台进程，此前既未初始化日志、也未装异常处理器，
            // 任何崩溃都"进程凭空消失、哪都不报错"。这里统一兜底：
            // 先写日志（若已初始化），再写用户可写的临时文件，最后写 Event Log，三重留痕。
            AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;

            // ── 初始化日志（此前 Tray 从不调用 LogFactory.Initialize，Logger 恒为 null → 所有日志 no-op）──
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                LogFactory.Initialize(logDir, "Tray");
                Logger.Info("Tray 日志已初始化");
            }
            catch (Exception ex)
            {
                WriteCrashFallback("LogInit", "日志初始化失败: " + ex);
            }

            // 同一会话内的单实例锁，避免重复拉起 Tray 导致管道名/资源争用。
            // 注意：故意不用 "Global\" 前缀——该前缀需要 SeCreateGlobalPrivilege 特权，
            // 普通交互式用户进程没有，会导致 Mutex 构造抛 UnauthorizedAccessException 而 Tray 无法启动；
            // 且本架构是"每会话一个 Tray"（各自服务本会话的屏幕/输入），会话级单实例才是正确语义。
            using var mutex = new Mutex(true, @"ITAsset4_Tray_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                Logger.Info("已有 Tray 实例在运行，本实例退出");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.Run(new TrayApplicationContext());
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            string detail = $"[Tray Fatal] UnhandledException (IsTerminating={e.IsTerminating}): {e.ExceptionObject}";
            Logger.Error(detail);
            WriteCrashFallback("Unhandled", detail);
            try { LogFactory.Shutdown(); } catch { }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            string detail = $"[Tray Fatal] ThreadException: {e.Exception}";
            Logger.Error(detail);
            WriteCrashFallback("ThreadException", detail);
            try { LogFactory.Shutdown(); } catch { }
        }

        /// <summary>
        /// 崩溃兜底写盘：日志目录可能因 ACL 不可写（Tray 以普通用户运行），
        /// 用用户可写的临时文件保证崩溃一定留痕（配合 Service 端的"假启动"检测）。
        /// </summary>
        private static void WriteCrashFallback(string tag, string detail)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "ITAsset4Tray_crash.log");
                File.AppendAllText(tmp, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {tag}: {detail}\n");
            }
            catch { }
            try { EventLog.WriteEntry("ITAsset4Tray", detail, EventLogEntryType.Error); } catch { }
        }
    }
}
