using System;
using System.Windows.Forms;
using ITAsset4.Common;

namespace ITAsset4.Tray
{
    /// <summary>
    /// Tray 应用上下文：不显示托盘图标，纯后台运行。
    ///  用 TCP localhost 替代 Named Pipes
    ///   - TcpScreenServer :15900（截图+弹窗）
    ///   - TcpInputServer  :15901（鼠标输入）
    /// 无 Session 依赖，端口固定，Service 通过 127.0.0.1 连接
    /// 
    /// v6.0: 支持 TCP 认证（接收 authToken 并传递给 Server）
    /// </summary>
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly TcpScreenServer _screenServer;
        private readonly TcpInputServer _inputServer;

        // v6.0: TCP 认证 Token
        private readonly string _authToken;

        /// <summary>
        /// v6.0: 构造函数接受 authToken
        /// </summary>
        public TrayApplicationContext(string authToken = null)
        {
            _authToken = authToken;

            //  Start dedicated input worker BEFORE servers
            PipeServer.StartInputWorker();

            _screenServer = new TcpScreenServer(_authToken);  // v6.0: 传入 authToken
            _screenServer.Start();

            _inputServer = new TcpInputServer(_authToken);    // v6.0: 传入 authToken
            _inputServer.Start();

            Logger.Info($"Tray 应用已启动 (TCP Auth: {(!string.IsNullOrEmpty(_authToken) ? "enabled" : "disabled")})");
        }

        protected override void ExitThreadCore()
        {
            _inputServer?.Stop();
            _screenServer?.Stop();
            PipeServer.StopInputWorker();
            base.ExitThreadCore();
        }

        public static void ShowBalloon(string title, string text)
        {
            var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            Action show = () =>
            {
                var dlg = new ToastForm(title, text);
                dlg.Show();
            };

            if (form != null && form.InvokeRequired)
                form.BeginInvoke(show);
            else
                show();
        }
    }

    internal class ToastForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;

        public ToastForm(string title, string text)
        {
            FormBorderStyle  = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar    = false;
            TopMost          = true;
            StartPosition    = FormStartPosition.Manual;
            Size             = new System.Drawing.Size(320, 80);

            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new System.Drawing.Point(
                screen.Right  - Width  - 12,
                screen.Bottom - Height - 12);

            var lblTitle = new Label
            {
                Text      = title,
                Font      = new System.Drawing.Font("微软雅黑", 9, System.Drawing.FontStyle.Bold),
                AutoSize  = false,
                Bounds    = new System.Drawing.Rectangle(10, 8, 295, 20),
                ForeColor = System.Drawing.Color.FromArgb(30, 30, 30),
            };
            var lblText = new Label
            {
                Text      = text,
                Font      = new System.Drawing.Font("微软雅黑", 8.5f),
                AutoSize  = false,
                Bounds    = new System.Drawing.Rectangle(10, 32, 295, 36),
                ForeColor = System.Drawing.Color.FromArgb(60, 60, 60),
            };
            Controls.AddRange(new Control[] { lblTitle, lblText });

            _timer = new System.Windows.Forms.Timer { Interval = 3000 };
            _timer.Tick += (s, e) => { _timer.Stop(); Close(); };
            _timer.Start();

            Click        += (s, e) => Close();
            lblTitle.Click += (s, e) => Close();
            lblText.Click  += (s, e) => Close();
        }

        protected override bool ShowWithoutActivation => true;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}
