using System;
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
    }
}
