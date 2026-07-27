using System;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;

namespace ITAsset4.Common.Tasks
{
    /// <summary>
    /// 任务执行上下文（策略模式分派载体）。
    /// 由 TaskExecutor.ExecuteAsync 构造并交给 TaskHandlerFactory 分派给具体 Handler。
    /// 第三个参数 services 在手动构造路径下为 null（DI 容器中的 IServiceProvider 等可选依赖）。
    /// </summary>
    public class TaskContext
    {
        public TaskInfo Task { get; }
        public AppConfig Cfg { get; }
        public object? Services { get; }
        public CancellationToken Ct { get; }
        public Func<PipeRequest, Task<PipeResponse>> UiSender { get; }
        public Func<string, string, int?, int?, DateTime, Task> AuditReporter { get; }
        public string Serial { get; }
        public string DeviceSecret { get; }

        public TaskContext(TaskInfo task, AppConfig cfg, object? services, CancellationToken ct,
            Func<PipeRequest, Task<PipeResponse>> uiSender,
            Func<string, string, int?, int?, DateTime, Task> auditReporter,
            string serial, string deviceSecret)
        {
            Task = task;
            Cfg = cfg;
            Services = services;
            Ct = ct;
            UiSender = uiSender;
            AuditReporter = auditReporter;
            Serial = serial;
            DeviceSecret = deviceSecret;
        }
    }
}
