using System;

namespace ITAsset4.Common
{
    /// <summary>
    /// 自更新防循环闸门（纯逻辑、可单测、无外部依赖）。
    ///
    /// 背景：客户端版本真相是编译期常量 <see cref="ClientVersion.Current"/>，自更新靠"发版时手动
    /// bump 该常量"。若发布的更新包漏 bump（历史 1.2.10→1.2.11 事故），更新后本地版本仍小于
    /// 服务端，会触发**无限自更新循环**——每次都下载、重应用、重启 Service，打满带宽与 CPU。
    /// 该循环跨 Service 重启不自愈（每次重启都是新的 UpdateChecker 实例）。
    ///
    /// 本类通过持久化的尝试记录（由调用方序列化到磁盘），对同一目标版本在冷却窗口内的重试
    /// 次数设上限，超限即拒绝并大声报错，从而打破死循环，同时保留正常版本（已正确 bump）的
    /// 平滑升级路径。
    /// </summary>
    public static class UpdateLoopGuard
    {
        /// <summary>默认最大允许尝试次数（冷却窗口内）。第 (MaxAttempts+1) 次起被拒。</summary>
        public const int DefaultMaxAttempts = 3;

        /// <summary>默认冷却窗口：超过该时长后计数重置，允许再次尝试。</summary>
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromHours(24);

        /// <summary>持久化状态（由调用方序列化为 JSON 落盘）。</summary>
        public class State
        {
            /// <summary>最近一次尝试更新的目标版本。</summary>
            public string LastTargetVersion { get; set; } = "";
            /// <summary>当前冷却窗口内针对 LastTargetVersion 的累计尝试次数。</summary>
            public int AttemptsForTarget { get; set; }
            /// <summary>当前冷却窗口起点（UTC ticks）。</summary>
            public long WindowStartUtcTicks { get; set; }
        }

        /// <summary>评估结果。</summary>
        public class Decision
        {
            /// <summary>是否允许本次更新尝试。</summary>
            public bool Allowed { get; set; }
            /// <summary>调用方应持久化的下一状态（无论 Allowed 与否都应写回）。</summary>
            public State Next { get; set; }
            /// <summary>人类可读原因（便于日志/运维）。</summary>
            public string Reason { get; set; }
        }

        /// <summary>
        /// 评估是否允许对 <paramref name="targetVersion"/> 发起一次更新尝试。
        /// 纯函数：相同输入必得相同输出，不触碰任何 IO。
        /// </summary>
        /// <param name="targetVersion">服务端要求更新的目标版本。</param>
        /// <param name="current">上次持久化的状态；可为 null（视为全新）。</param>
        /// <param name="nowUtc">当前 UTC 时间（注入以便单测）。</param>
        /// <param name="maxAttempts">冷却窗口内最大允许尝试次数。</param>
        /// <param name="cooldown">冷却窗口长度。</param>
        public static Decision Evaluate(string targetVersion, State current, DateTime nowUtc,
            int maxAttempts = DefaultMaxAttempts, TimeSpan? cooldown = null)
        {
            cooldown ??= DefaultCooldown;
            var cur = current ?? new State();

            // 1) 目标版本切换 → 全新窗口，首次尝试放行
            if (cur.LastTargetVersion != targetVersion)
            {
                return new Decision
                {
                    Allowed = true,
                    Next = new State
                    {
                        LastTargetVersion = targetVersion,
                        AttemptsForTarget = 1,
                        WindowStartUtcTicks = nowUtc.Ticks,
                    },
                    Reason = $"目标版本切换为 {targetVersion}，允许首次尝试",
                };
            }

            // 2) 同一目标版本：冷却窗口已过 → 重置计数后放行
            var windowStart = new DateTime(cur.WindowStartUtcTicks, DateTimeKind.Utc);
            if (nowUtc - windowStart > cooldown.Value)
            {
                return new Decision
                {
                    Allowed = true,
                    Next = new State
                    {
                        LastTargetVersion = targetVersion,
                        AttemptsForTarget = 1,
                        WindowStartUtcTicks = nowUtc.Ticks,
                    },
                    Reason = $"冷却窗口已过期，重置计数后放行 {targetVersion}",
                };
            }

            // 3) 冷却窗口内：累加计数
            int nextCount = cur.AttemptsForTarget + 1;
            if (nextCount > maxAttempts)
            {
                // 超限：拒绝，但保留累计值便于运维观察仍在重试
                return new Decision
                {
                    Allowed = false,
                    Next = new State
                    {
                        LastTargetVersion = targetVersion,
                        AttemptsForTarget = nextCount,
                        WindowStartUtcTicks = cur.WindowStartUtcTicks,
                    },
                    Reason = $"目标版本 {targetVersion} 在冷却窗口内已重试 {nextCount} 次（上限 {maxAttempts}），" +
                             $"拒绝继续以避免无限自更新循环（疑似更新包未正确 bump 版本）",
                };
            }

            // 4) 冷却窗口内但未超限：放行
            return new Decision
            {
                Allowed = true,
                Next = new State
                {
                    LastTargetVersion = targetVersion,
                    AttemptsForTarget = nextCount,
                    WindowStartUtcTicks = cur.WindowStartUtcTicks,
                },
                Reason = $"冷却窗口内第 {nextCount} 次尝试（上限 {maxAttempts}）",
            };
        }
    }
}
