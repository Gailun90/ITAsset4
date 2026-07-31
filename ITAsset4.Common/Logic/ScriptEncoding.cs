using System;
using System.IO;
using System.Text;

namespace ITAsset4.Common
{
    /// <summary>
    /// 脚本文件编码选择（纯逻辑、可单测）。
    /// 关键修复：.bat / .cmd 必须以“无 BOM 的 UTF-8”写入，否则 cmd.exe 读取首行时
    /// BOM（EF BB BF）会被当成乱码前缀导致脚本第一行被吞/报错；
    /// .ps1（PowerShell）保留 BOM 以兼容旧版 PowerShell 的默认编码探测。
    /// </summary>
    public static class ScriptEncoding
    {
        /// <summary>
        /// 根据扩展名返回写入脚本时使用的 Encoding。
        /// </summary>
        public static Encoding ForExtension(string extension)
        {
            string ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
            // .ps1 / .psm1 / .psd1 保留 BOM（Encoding.UTF8 带 BOM）
            if (ext == "ps1" || ext == "psm1" || ext == "psd1")
                return new UTF8Encoding(true);

            // .bat / .cmd / 其它（含默认 .bat）一律无 BOM
            return new UTF8Encoding(false);
        }

        /// <summary>
        /// 便捷方法：根据文件名返回编码。
        /// </summary>
        public static Encoding ForFileName(string fileName)
        {
            return ForExtension(Path.GetExtension(fileName));
        }
    }
}
