using System;

namespace ITAsset4.Common
{
    /// <summary>
    /// 客户端版本号（单一可信来源）。
    /// 自更新流程会比较本常量与服务端 /api/client/update 返回的 version，
    /// 若服务端版本更新则下载并更新自身。
    ///
    /// ⚠️ 每次发布新版本时，请同步修改此常量，并在服务端更新目录放置
    ///    新的更新包与 version.json。
    /// </summary>
    public static class ClientVersion
    {
        public const string Current = "1.2.9";

        /// <summary>
        /// 将 "1.1.0" 形式的版本号解析为可比较的 (major,minor,build) 元组。
        /// 解析失败返回 (-1,-1,-1)。
        /// </summary>
        public static (int major, int minor, int build) Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return (-1, -1, -1);
            var parts = version.Trim().Split('.');
            int major = 0, minor = 0, build = 0;
            if (parts.Length > 0 && !int.TryParse(parts[0], out major)) return (-1, -1, -1);
            if (parts.Length > 1 && !int.TryParse(parts[1], out minor)) return (-1, -1, -1);
            if (parts.Length > 2 && !int.TryParse(parts[2], out build)) return (-1, -1, -1);
            return (major, minor, build);
        }

        /// <summary>
        /// 比较 a 是否严格大于 b（返回 true 表示 a 更新）。
        /// </summary>
        public static bool IsNewer(string a, string b)
        {
            var ta = Parse(a);
            var tb = Parse(b);
            if (ta.major != tb.major) return ta.major > tb.major;
            if (ta.minor != tb.minor) return ta.minor > tb.minor;
            return ta.build > tb.build;
        }
    }
}
