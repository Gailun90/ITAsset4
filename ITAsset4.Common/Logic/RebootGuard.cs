using System;
using System.Text.RegularExpressions;

namespace ITAsset4.Common
{
    /// <summary>
    /// 脚本注释/字符串清洗（纯逻辑、可单测）。
    /// 去除 PowerShell / CMD 的整行注释（# / REM / ::）与行尾注释（# ...），
    /// 仅用于把“注释里的文字”排除在安全检查之外，避免误判。
    /// 注意：它不会删除任何真实命令行，也不应误伤 Restart-Service 等正常命令。
    /// </summary>
    public static class ScriptSanitizer
    {
        public static string StripComments(string commandText)
        {
            if (string.IsNullOrEmpty(commandText)) return commandText ?? "";

            var lines = new System.Collections.Generic.List<string>();
            foreach (var raw in commandText.Split('\n'))
            {
                string trimmed = raw.Trim();
                string upper = trimmed.ToUpperInvariant();

                // 整行注释：以 # / :: 开头，或以 REM（后跟空格或行尾）开头
                if (trimmed.StartsWith("#") || trimmed.StartsWith("::") ||
                    upper.StartsWith("REM ") || upper == "REM")
                {
                    continue;
                }

                // 行尾注释：第一个未加引号的 # 之后视为注释
                int hashPos = IndexOfUnquotedHash(raw);
                string line = hashPos >= 0 ? raw.Substring(0, hashPos) : raw;

                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// 找到行中第一个“不在引号内”的 # 位置（用于裁剪行尾注释）。
        /// 同时处理双引号与单引号字符串字面量。
        /// </summary>
        private static int IndexOfUnquotedHash(string line)
        {
            bool inDouble = false;
            bool inSingle = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { inDouble = !inDouble; continue; }
                if (c == '\'') { inSingle = !inSingle; continue; }
                if (!inDouble && !inSingle && c == '#')
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// 重启/关机关键词纵深防御（纯逻辑、可单测）。
    /// 设计要点：
    ///   1) 先洗掉注释与字符串字面量里的文字，避免 Write-Host "will reboot" 这类提示文本被误判；
    ///   2) 触发词精确锚定到真实的重启/关机指令：
    ///        Restart-Computer / Stop-Computer / shutdown(.exe) / restart-computer /
    ///        wuauclt /restart / reboot
    ///      其中 bare restart 用负向先行断言排除 Restart-Service（只重启“服务”，不是机器）。
    /// </summary>
    public static class RebootGuard
    {
        // 先去掉字符串字面量与整行注释，再做关键词匹配
        private static readonly Regex StripStrings =
            new Regex(@"#.*$|""[^""\r\n]*""|'[^'\r\n]*'", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly Regex RebootTokens = new Regex(
            @"Restart-Computer|Stop-Computer|shutdown\.exe|\bshutdown\b|restart-computer" +
            @"|wuauclt\s+/restart|\breboot\b|\brestart(?!-Service)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 强重启词（仅用于 launcher 解包后的二次扫描，避免对普通脚本的字符串字面量误伤）
        private static readonly Regex StrongRebootTokens = new Regex(
            @"Restart-Computer|Stop-Computer|shutdown(?:\.exe)?|restart-computer|wuauclt\s+/restart",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CmdWrapper = new Regex(
            @"^\s*cmd\s+/c\s+(.*\S)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex PsWrapper = new Regex(
            @"^\s*powershell(?:\.exe)?\s+.*?(?:-Command|-File)\s+(.*\S)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>
        /// 解开 cmd /c "..." 或 powershell -Command/-File "..." 这类启动器包装，
        /// 还原出“真正会被执行”的命令（一层即可，内层含 restart-computer 等强词仍会被强词正则命中）。
        /// </summary>
        private static string UnwrapLauncher(string s)
        {
            var m = CmdWrapper.Match(s);
            if (m.Success) return m.Groups[1].Value;
            m = PsWrapper.Match(s);
            if (m.Success) return m.Groups[1].Value;
            return s;
        }

        /// <summary>
        /// 判断脚本/命令行是否包含真实的重启或关机指令。返回 true 表示“命中”。
        /// 双重扫描：
        ///   1) 常规脚本：先洗掉注释与字符串字面量，避免 Write-Host "will reboot" 等提示文本误判；
        ///   2) launcher 解包：若命令被 cmd /c "..." 等包装，解包后对其“真实命令”做强词扫描，
        ///      关闭 `cmd /c "shutdown /r"` 这类引号绕过（见 C2 安全修复 / F2）。
        /// </summary>
        public static bool ContainsRebootShutdown(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return false;
            string cleaned = StripStrings.Replace(ScriptSanitizer.StripComments(script), " ");
            if (RebootTokens.IsMatch(cleaned)) return true;

            string unwrapped = UnwrapLauncher(script);
            if (!string.Equals(unwrapped, script, StringComparison.OrdinalIgnoreCase))
                return StrongRebootTokens.IsMatch(unwrapped);
            return false;
        }
    }
}
