using System;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ITAsset4.Common
{
    public static class LogFactory
    {
        private static Serilog.ILogger _serilogLogger;
        private static ILoggerFactory _factory;
        private static readonly object _lock = new();

        /// <summary>
        /// 初始化 Serilog 日志系统。
        /// </summary>
        /// <param name="logDir">日志目录（C:\ProgramData\ITAsset4\logs）</param>
        /// <param name="component">组件名（Service / Tray），用于区分日志文件</param>
        /// <param name="minLevel">最低日志级别，默认 Information</param>
        public static void Initialize(string logDir, string component, LogEventLevel minLevel = LogEventLevel.Information)
        {
            lock (_lock)
            {
                _serilogLogger = new LoggerConfiguration()
                    .MinimumLevel.Is(minLevel)
                    .WriteTo.File(
                        System.IO.Path.Combine(logDir, $"{component}-.log"),
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: 10 * 1024 * 1024,   // 10MB per file
                        retainedFileCountLimit: 30,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();

                // 设置全局静态 Logger（AddSerilog() 扩展方法从此处取实例）
                Serilog.Log.Logger = _serilogLogger;

                _factory = new SerilogLoggerFactory(_serilogLogger);

                // 向后兼容：设置旧的 Logger 静态类
                Logger.SetLogger(_serilogLogger);
            }
        }

        /// <summary>
        /// 获取 ILoggerFactory（用于 DI.AddLogging）
        /// </summary>
        public static ILoggerFactory Factory => _factory;

        /// <summary>
        /// 创建 ILogger<T>，注入给不使用 DI 的类（兼容过渡期）
        /// </summary>
        public static Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>()
        {
            lock (_lock)
            {
                if (_factory == null) throw new InvalidOperationException("LogFactory not initialized");
                return _factory.CreateLogger<T>();
            }
        }

        /// <summary>
        /// 关闭日志（flush 磁盘）
        /// </summary>
        public static void Shutdown()
        {
            Serilog.Log.CloseAndFlush();
        }
    }
}
