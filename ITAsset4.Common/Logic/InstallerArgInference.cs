using System;
using System.IO;

namespace ITAsset4.Common
{
    /// <summary>
    /// 安装包/卸载程序类型枚举。
    /// </summary>
    public enum InstallerKind
    {
        Msi,
        Nsis,
        InnoSetup,
        Other
    }

    /// <summary>
    /// 静默参数推断（纯逻辑、可单测）。
    /// 依据可执行文件扩展名 / 文件名推断安装器类型，并补充对应的静默参数：
    ///   - MSI      → msiexec，补 /quiet /norestart
    ///   - NSIS     → 卸载器通常叫 uninstaller.exe / uninstall.exe / helper.exe，补 /S
    ///   - InnoSetup→ unins000.exe 等，补 /SILENT /VERYSILENT /SUPPRESSMSGBOXES
    ///   - Other    → 兜底补 /quiet /norestart（InstallShield 等常见）
    /// 若调用方已经带了静默标记，则原样返回（去重，不再重复追加）。
    /// </summary>
    public static class InstallerArgInference
    {
        public static string InferSilentArgs(string originalArgs, string exe)
        {
            if (string.IsNullOrWhiteSpace(originalArgs)) originalArgs = "";

            // 1. 已经包含静默标记 → 原样返回（修复原先重复的 /SILENT 分支）
            if (HasSilentToken(originalArgs))
                return originalArgs;

            // 2. 按类型补充静默参数
            switch (DetectInstallerKind(exe))
            {
                case InstallerKind.Msi:
                    return originalArgs + " /quiet /norestart";
                case InstallerKind.Nsis:
                    return originalArgs + " /S";
                case InstallerKind.InnoSetup:
                    return originalArgs + " /SILENT /VERYSILENT /SUPPRESSMSGBOXES";
                default:
                    return originalArgs + " /quiet /norestart";
            }
        }

        public static InstallerKind DetectInstallerKind(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return InstallerKind.Other;

            string lower = exe.ToLowerInvariant();
            if (lower.EndsWith(".msi") || lower.Contains("msiexec"))
                return InstallerKind.Msi;

            string name = Path.GetFileName(lower);
            // NSIS 卸载器常见命名
            if (name == "uninstaller.exe" || name == "uninstall.exe" || name == "helper.exe")
                return InstallerKind.Nsis;

            // Inno Setup 卸载器通常叫 unins000.exe / unins001.exe
            if (name.StartsWith("unins") && name.EndsWith(".exe"))
                return InstallerKind.InnoSetup;

            return InstallerKind.Other;
        }

        private static bool HasSilentToken(string args)
        {
            // 按空白切分为 token 精确匹配，避免 "/S " 尾随空格才能识别导致的裸 "/S" 漏判
            // （旧实现 InferSilentArgs("/S", exe) 会输出 "/S /S"，见 F1 修复）。
            foreach (var raw in args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim();
                if (t.Equals("/S", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.StartsWith("/SILENT", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.StartsWith("/VERYSILENT", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.Equals("/quiet", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.Equals("/qn", StringComparison.OrdinalIgnoreCase)) return true;
                if (t.StartsWith("/quiet", StringComparison.OrdinalIgnoreCase)) return true; // /quiet /norestart
            }
            return false;
        }
    }
}
