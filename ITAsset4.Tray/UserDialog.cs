using System;
using System.Windows.Forms;

namespace ITAsset4.Tray
{
    public static class UserDialog
    {
        private const int DIALOG_TIMEOUT_SEC = 300;

        public static string AskInstall(string appName, int deferCount, int maxDefer)
        {
            int remaining = maxDefer - deferCount;
            bool canDefer = remaining > 0;
            bool isLastChance = remaining == 1;  // 本次推迟后下次强制

            string deferHint;
            if (remaining <= 0)
                deferHint = "\n\n⚠️ 已达最大推迟次数，本次必须安装。";
            else if (isLastChance)
                deferHint = "\n\n⚠️ 这是最后一次推迟机会，推迟后下次将自动静默安装！";
            else
                deferHint = $"\n\n（还可推迟 {remaining} 次）";

            using (var dlg = new InstallConfirmForm(appName, deferHint, canDefer, isLastChance, DIALOG_TIMEOUT_SEC))
            {
                var dr = dlg.ShowDialog();
                return dr == DialogResult.Yes ? "OK" : "CANCEL";
            }
        }

        public static string AskReboot(string appName)
        {
            var result = MessageBox.Show(
                $"应用程序 [{appName}] 安装完成，需要重启计算机才能生效。\n\n是否立即重启？",
                "安装完成 - 需要重启",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            return result switch
            {
                DialogResult.Yes => "now",
                DialogResult.No  => "later",
                _                => "cancel",
            };
        }

        public static string Notify(string title, string message)
        {
            TrayApplicationContext.ShowBalloon(title, message);
            return "OK";
        }
    }

    public class InstallConfirmForm : Form
    {
        private readonly System.Windows.Forms.Timer _countdown;
        private int _remainSeconds;
        private readonly Label _lblCountdown;
        private readonly bool _isLastChance;

        public InstallConfirmForm(string appName, string deferHint, bool canDefer,
                                   bool isLastChance, int timeoutSec = 300)
        {
            _remainSeconds = timeoutSec;
            _isLastChance  = isLastChance;

            Text            = isLastChance
                ? $"⚠️ 最后推迟机会 - {appName}"
                : $"软件安装请求 - {FormatTime(_remainSeconds)}";
            Size            = new System.Drawing.Size(460, 260);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            TopMost         = true;

            // 主提示文字
            var lbl = new Label
            {
                Text     = $"IT 管理员推送了软件安装任务：\n\n【{appName}】{deferHint}",
                AutoSize = false,
                Bounds   = new System.Drawing.Rectangle(20, 20, 420, 130),
                Font     = new System.Drawing.Font("微软雅黑", 9),
            };

            // 倒计时标签（最后一次变红）
            _lblCountdown = new Label
            {
                Text      = $"将在 {FormatTime(_remainSeconds)} 后自动关闭",
                AutoSize  = false,
                Bounds    = new System.Drawing.Rectangle(20, 158, 420, 20),
                ForeColor = isLastChance
                    ? System.Drawing.Color.Crimson
                    : System.Drawing.Color.Gray,
                Font      = new System.Drawing.Font("微软雅黑", 8,
                    isLastChance ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            };

            var btnInstall = new Button
            {
                Text         = "立即安装",
                DialogResult = DialogResult.Yes,
                Bounds       = new System.Drawing.Rectangle(60, 190, 130, 36),
                BackColor    = System.Drawing.Color.FromArgb(0, 120, 212),
                ForeColor    = System.Drawing.Color.White,
                FlatStyle    = FlatStyle.Flat,
            };

            var btnDefer = new Button
            {
                Text         = canDefer ? (isLastChance ? "⚠️ 最后一次推迟" : "推迟安装") : "关闭",
                DialogResult = DialogResult.No,
                Bounds       = new System.Drawing.Rectangle(250, 190, 150, 36),

                // Update the problematic line
                ForeColor    = isLastChance ? System.Drawing.Color.Crimson : System.Drawing.SystemColors.ControlText,
            };

            AcceptButton = btnInstall;
            Controls.AddRange(new Control[] { lbl, _lblCountdown, btnInstall, btnDefer });

            _countdown = new System.Windows.Forms.Timer { Interval = 1000 };
            _countdown.Tick += (s, e) =>
            {
                _remainSeconds--;
                if (_remainSeconds <= 0)
                {
                    _countdown.Stop();
                    DialogResult = DialogResult.Cancel;
                    return;
                }
                string t = FormatTime(_remainSeconds);
                if (!isLastChance) Text = $"软件安装请求 - {t}";
                _lblCountdown.Text = $"将在 {t} 后自动关闭";

                // 最后 60 秒全部变红
                if (_remainSeconds <= 60 && !isLastChance)
                    _lblCountdown.ForeColor = System.Drawing.Color.Crimson;
            };
            _countdown.Start();
        }

        private static string FormatTime(int sec) =>
            sec >= 60 ? $"{sec / 60}分{sec % 60}秒" : $"{sec}秒";

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _countdown?.Stop();
            base.OnFormClosed(e);
        }
    }
}
