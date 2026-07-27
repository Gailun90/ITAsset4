using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ITAsset4.Common;
using ITAsset4.Common.Tasks;

namespace ITAsset4.Service.TaskHandlers
{
    /// <summary>
    /// 任务处理器接口（策略模式）。每个 Handler 负责一种 task_type。
    /// </summary>
    public interface ITaskHandler
    {
        bool CanHandle(TaskContext ctx);
        Task<TaskResult> ExecuteAsync(TaskContext ctx);
    }

    /// <summary>
    /// 任务处理器工厂：注册多个 Handler，按 ctx.Task.task_type 分派到第一个能处理的 Handler。
    /// 真实修复逻辑位于 ITAsset4.Service.TaskExecutor 的静态方法，Handler 仅做薄封装分派，
    /// 因此本工厂与 Handler 均不持有业务状态。
    /// </summary>
    public class TaskHandlerFactory
    {
        private readonly List<ITaskHandler> _handlers = new List<ITaskHandler>();

        public void Register(ITaskHandler handler) => _handlers.Add(handler);

        public async Task<TaskResult> ExecuteAsync(TaskContext ctx)
        {
            var handler = _handlers.FirstOrDefault(h => h.CanHandle(ctx));
            if (handler == null)
            {
                return new TaskResult
                {
                    success = false,
                    message = $"不支持的任务类型: {(ctx.Task?.task_type ?? "?")}",
                };
            }
            return await handler.ExecuteAsync(ctx);
        }
    }
}
