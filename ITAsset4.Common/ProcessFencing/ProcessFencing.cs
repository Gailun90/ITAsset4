using System;

namespace ITAsset4.Common.ProcessFencing
{
    /// <summary>
    /// 进程围栏接口：保护 Agent 自身进程树不被任务子进程误杀 / 防止递归自终止。
    /// 注：本 workspace 的 checkout 中该类型仅被 DI 注册、未被业务代码直接调用，
    ///     故此处提供最小可用实现。生产环境应以权威源码中的实现为准（避免行为偏差）。
    /// </summary>
    public interface IProcessFencer
    {
        void Guard();
        bool IsFenced { get; }
    }

    public class ProcessFencer : IProcessFencer
    {
        public bool IsFenced { get; private set; } = false;

        public void Guard()
        {
            // 真实实现（如通过 Job Object 限制子进程树、提升自身优先级等）
            // 应在恢复完整源码后回填。当前为空实现，不影响编译与现有行为。
            //
            // 修复：此前这里无条件把 IsFenced 设为 true，等于在什么都没做的情况下
            // 谎报围栏已生效——一旦以后有代码依据 IsFenced 判断可以放心做某个操作，
            // 会拿到一个假的安全信号。在真实实现补上之前，如实保持 false。
            IsFenced = false;
        }
    }
}
