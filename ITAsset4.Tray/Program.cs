using System;
using System.Windows.Forms;

namespace ITAsset4.Tray
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例保护
            using (var mutex = new System.Threading.Mutex(true, "ITAsset4TrayMutex", out bool first))
            {
                if (!first)
                {
                    // 静默退出：由 SessionManager 周期检查，弹窗会干扰用户
                    return;
                }

                Application.Run(new TrayApplicationContext());
            }
        }
    }
}
