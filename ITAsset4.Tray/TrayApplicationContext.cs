using System;
using System.Diagnostics;
using System.Windows.Forms;
using ITAsset4.Common;
using Microsoft.Win32;

namespace ITAsset4.Tray
{
    /// <summary>
    /// Tray 应用上下文：不显示托盘图标，纯后台运行。
    /// 
    /// v7.0: 使用命名管道替代 TCP localhost
    ///   - PipeScreenServer: \\.\pipe\ITAsset4_{sessionId}_Screen（截图+弹窗）
    ///   - PipeInputServer:  \\.\pipe\ITAsset4_{sessionId}_Input（鼠标输入）
    /// 
    /// 管道名中的 sessionId 使用"活跃且已解锁的物理控制台会话"（单一真相源，
    /// 与 Service 端共用 WtsSessionHelper），避免两端 session 不一致导致连接超时。
    /// 仅在当前进程确属该控制台会话时才注册管道；锁屏/注销时主动关闭，解锁后重新打开。
    /// </summary>
    public class TrayApplicationContext : ApplicationContext
    {
        private readonly PipeScreenServer _screenServer;
        private readonly PipeInputServer _inputServer;

        public TrayApplicationContext()
        {
            //  Start dedicated input worker BEFORE servers
            PipeServer.StartInputWorker();

            _screenServer = new PipeScreenServer();
            _inputServer = new PipeInputServer();

            // 仅在"活跃且已解锁的物理控制台会话"且本进程正属该 session 时才注册管道；
            // 否则进入待机监听，待解锁（SessionUnlock）再开启。
            if (!WtsSessionHelper.IsPhysicalDesktopActiveAndUnlocked(out int mySid)
                || mySid != Process.GetCurrentProcess().SessionId)
            {
                Logger.Info("当前非活跃解锁的物理桌面 session，暂不注册屏幕/输入管道，进入待机监听");
            }
            else
            {
                _screenServer.Start();
                _inputServer.Start();
            }

            Logger.Info("Tray 应用已启动（命名管道模式）");

            // 订阅锁屏/解锁/注销事件，动态开关管道：
            // 锁屏时主动断开（锁屏状态本就不该允许远程看屏幕），解锁后重新打开。
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionUnlock:
                    if (!_screenServer.IsRunning) _screenServer.Start();
                    if (!_inputServer.IsRunning) _inputServer.Start();
                    break;
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.SessionLogoff:
                    if (_screenServer.IsRunning) _screenServer.Stop();
                    if (_inputServer.IsRunning) _inputServer.Stop();
                    break;
            }
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
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
