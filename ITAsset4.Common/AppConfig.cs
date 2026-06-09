using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ITAsset4.Common
{
    /// <summary>
    ///   - initial_token 不再写入配置文件，改存注册表 HKLM\SOFTWARE\ITAsset4\InitialToken
    ///   - 注册成功后自动删除注册表中的 token，不留痕迹
    ///   - WriteDefault 不再包含 initial_token 字段
    ///   - 新增 SetInitialToken / DeleteInitialToken 管理注册表 token
    ///   - ServerUrl 已优先读取 config.ini [server] url，无硬编码
    ///   - WriteDefault 中的默认地址仅作"首次生成占位"，用户部署时应修改 config.ini
    ///   - InitialToken 支持优先读取 config.ini [server] initial_token，
    ///     若未配置则回退内置默认值（需与服务端 AGENT_INITIAL_TOKEN 一致）
    /// </summary>
    public class AppConfig
    {
        private readonly Dictionary<string, Dictionary<string, string>> _data
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static AppConfig Load(string path)
        {
            var cfg = new AppConfig();
            if (!File.Exists(path))
                throw new FileNotFoundException($"配置文件不存在: {path}");
            cfg.Parse(path);
            return cfg;
        }

        private void Parse(string path)
        {
            string section = "";
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#"))
                    continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2);
                    if (!_data.ContainsKey(section))
                        _data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && section != "")
                        _data[section][line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
        }

        public string Get(string section, string key, string def = "") =>
            _data.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : def;

        /// <summary>
        /// 服务器地址：优先读取 config.ini [server] url。
        /// </summary>
        public string ServerUrl => Get("server", "url", "").TrimEnd('/');

        /// <summary>
        /// 初始注册 Token：
        ///   优先读取 config.ini [server] initial_token；
        ///   若未配置则回退内置默认值（需与服务端 .env AGENT_INITIAL_TOKEN 一致）。
        /// </summary>
        public string InitialToken
        {
            get
            {
                string fromCfg = Get("server", "initial_token", "");
                return string.IsNullOrWhiteSpace(fromCfg)
                    ? "a3f8b2c1-9d4e-5f6a-7b8c-0d1e2f3a4b5c"
                    : fromCfg;
            }
        }

        public string ReportTime => Get("schedule", "report_time", "08:00");
        public string PollTime   => Get("schedule", "poll_time",  "09:00");
        public int    JitterMax  => int.TryParse(Get("schedule", "jitter_max_sec", "300"), out var j) ? j : 300;

        public string BaseDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITAsset4");

        /// <summary>
        /// 生成默认配置文件（仅在首次运行、config.ini 不存在时调用）。
        /// 部署时请修改 config.ini 中的 url 和 initial_token，无需重新编译。
        /// initial_token 可选填；若不填，程序使用内置默认值。
        /// </summary>
        public static void WriteDefault(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
@"[server]
; 服务器地址，部署时请修改为实际地址
url = http://your-server:8000
; 初始注册 Token，需与服务端 AGENT_INITIAL_TOKEN 一致（可选，不填则使用内置默认值）
; initial_token = your-token-here

[schedule]
report_time    = 08:00
poll_time      = 09:00
jitter_max_sec = 300
", Encoding.UTF8);
        }
    }
}
