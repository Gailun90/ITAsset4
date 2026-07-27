using System;
using Serilog;
using Serilog.Events;

namespace ITAsset4.Common
{
    public static class Logger
    {
        private static Serilog.ILogger _log;
        private static readonly object _lock = new();

        public static void Init(string component) { /* no-op, use LogFactory.Initialize instead */ }

        public static void SetLogger(Serilog.ILogger log)
        {
            lock (_lock) { _log = log; }
        }

        public static void Info(string msg) => _log?.Information(msg);
        public static void Warn(string msg) => _log?.Warning(msg);
        public static void Error(string msg) => _log?.Error(msg);
        public static void Debug(string msg) => _log?.Debug(msg);
    }
}
