using System.IO;
using System.Threading.Tasks;
using ITAsset4.Common;
using ITAsset4.Common.Tasks;
using ITAsset4.Service;   // TaskExecutor（同程序集，internal 静态方法可访问）

namespace ITAsset4.Service.TaskHandlers
{
    /// <summary>
    /// 安装任务：下载安装包并以静默参数执行。
    /// Handler 不持有状态，仅把 TaskContext 透传给 TaskExecutor.ExecuteInstallAsync。
    /// </summary>
    public class InstallHandler : ITaskHandler
    {
        public bool CanHandle(TaskContext ctx) => (ctx.Task?.task_type ?? "") == "install";

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx)
        {
            string pkgDir = Path.Combine(ctx.Cfg.BaseDir, "packages");
            Directory.CreateDirectory(pkgDir);
            var dl = new Downloader(pkgDir);
            return await TaskExecutor.ExecuteInstallAsync(
                ctx.Task, dl, ctx.Serial, ctx.DeviceSecret, ctx.UiSender, ctx.AuditReporter);
        }
    }

    /// <summary>卸载任务：定位卸载信息并安静卸载。</summary>
    public class UninstallHandler : ITaskHandler
    {
        public bool CanHandle(TaskContext ctx) => (ctx.Task?.task_type ?? "") == "uninstall";

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx) =>
            await TaskExecutor.ExecuteUninstallAsync(ctx.Task, ctx.UiSender, ctx.AuditReporter);
    }

    /// <summary>命令类任务：将脚本写入临时文件后以 cmd/powershell 执行（含重启黑名单纵深防御）。</summary>
    public class RunCommandHandler : ITaskHandler
    {
        public bool CanHandle(TaskContext ctx) => (ctx.Task?.task_type ?? "") == "run_command";

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx) =>
            await TaskExecutor.ExecuteCommandAsync(ctx.Task, ctx.AuditReporter);
    }

    /// <summary>注册表任务：应用注册表操作并在写后读回验证（verify_snapshot 供服务端后校验）。</summary>
    public class RegistryHandler : ITaskHandler
    {
        public bool CanHandle(TaskContext ctx) => (ctx.Task?.task_type ?? "") == "registry";

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx) =>
            await TaskExecutor.ExecuteRegistryAsync(ctx.Task);
    }

    /// <summary>清理任务：删除指定文件/目录（带系统目录安全校验，防误删）。</summary>
    public class CleanupHandler : ITaskHandler
    {
        public bool CanHandle(TaskContext ctx) => (ctx.Task?.task_type ?? "") == "cleanup";

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx) =>
            await TaskExecutor.ExecuteCleanupAsync(ctx.Task);
    }
}
