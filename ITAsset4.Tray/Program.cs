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

            Application.Run(new TrayApplicationContext());
        }
    }
}
