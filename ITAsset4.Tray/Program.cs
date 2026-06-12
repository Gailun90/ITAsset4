using System;
using System.Linq;
using System.Windows.Forms;
using ITAsset4.Common;

namespace ITAsset4.Tray
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // v6.0: 解析命令行参数获取 TCP 认证 Token
            string authToken = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--auth-token" && i + 1 < args.Length)
                    {
                        authToken = args[i + 1];
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(authToken))
            {
                Logger.Warn("[Tray] 未提供 --auth-token 参数，TCP 认证将被禁用（不安全）");
            }
            else
            {
                Logger.Info($"[Tray] TCP 认证 Token 已接收: {authToken.Substring(0, Math.Min(8, authToken.Length))}...");
            }

            // v6.0: 传递 authToken 给 TrayApplicationContext
            Application.Run(new TrayApplicationContext(authToken));
        }
    }
}
