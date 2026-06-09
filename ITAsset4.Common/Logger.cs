using System;
using System.IO;
using System.Text;

namespace ITAsset4.Common
{
    public static class Logger
    {
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITAsset4", "agent.log");

        private static readonly object _lock = new object();

        // 单文件最大 5 MB，最多保留 3 个备份（agent.log.1 / .2 / .3）
        private const long  MaxFileBytes  = 5 * 1024 * 1024;
        private const int   MaxBackups    = 3;

        static Logger()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)); }
            catch { }
        }

        public static void Info(string msg)  => Write("INFO ", msg);
        public static void Warn(string msg)  => Write("WARN ", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";

            Console.WriteLine(line);
            System.Diagnostics.Debug.WriteLine(line);

            lock (_lock)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [日志写入失败: {ex.Message}]");
                }
            }
        }

        /// <summary>
        /// 超过 MaxFileBytes 时滚动：
        ///   agent.log.2 → agent.log.3
        ///   agent.log.1 → agent.log.2
        ///   agent.log   → agent.log.1
        /// 超过 MaxBackups 的最老备份直接删除。
        /// </summary>
        private static void RotateIfNeeded()
        {
            if (!File.Exists(LogFile)) return;
            if (new FileInfo(LogFile).Length < MaxFileBytes) return;

            // 删掉最老的备份（如果已满）
            string oldest = LogFile + "." + MaxBackups;
            if (File.Exists(oldest)) File.Delete(oldest);

            // 依次向后移
            for (int i = MaxBackups - 1; i >= 1; i--)
            {
                string src  = LogFile + "." + i;
                string dest = LogFile + "." + (i + 1);
                if (File.Exists(src)) File.Move(src, dest);
            }

            // 当前 log → .1
            File.Move(LogFile, LogFile + ".1");
        }
    }
}