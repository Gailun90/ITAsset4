using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using ITAsset4.Common;
using ITAsset4.Common.Tasks;
using ITAsset4.Common.ProcessFencing;
using ITAsset4.Service.TaskHandlers;

namespace ITAsset4.Service
{
    public static class ServiceSetup
    {
        public static ServiceProvider BuildProvider(AppConfig cfg)
        {
            var services = new ServiceCollection();

            // ── 日志 ──
            services.AddLogging(b => b.AddSerilog(dispose: true));

            // ── 配置 ──
            services.AddSingleton(cfg);

            // ── 基础设施 ──
            services.AddSingleton<IProcessFencer, ProcessFencer>();
            services.AddSingleton<ApiClient>(sp => new ApiClient(cfg));

            // ── 任务处理器（策略模式）──
            services.AddSingleton<ITaskHandler, InstallHandler>();
            services.AddSingleton<ITaskHandler, UninstallHandler>();
            services.AddSingleton<ITaskHandler, RunCommandHandler>();
            services.AddSingleton<ITaskHandler, RegistryHandler>();
            services.AddSingleton<ITaskHandler, CleanupHandler>();

            // ── 工厂 ──
            services.AddSingleton(sp =>
            {
                var factory = new TaskHandlerFactory();
                foreach (var h in sp.GetServices<ITaskHandler>())
                    factory.Register(h);
                return factory;
            });

            return services.BuildServiceProvider();
        }
    }
}
