using System;
using System.Collections.Generic;

namespace ITAsset4.Common
{
    /// <summary>
    /// 任务去重（纯逻辑、线程安全、可单测）。
    /// 用于解决 WS 推送与定时轮询可能下发“同一任务”导致重复执行的问题：
    ///   - 同一 task id 首次领取 → 进入“执行中”状态并授予执行权；
    ///   - 任务“执行中”再次被派发 → 视为重复，禁止并发双跑（修复 F3：原先仅靠 15 分钟时间戳，
    ///     长任务或竞态下仍可能双跑）；
    ///   - 窗口期内已完成 → 视为重复；
    ///   - 失败后 MarkFailed 清除记录，允许后续重新派发；
    ///   - 完成/推迟后 MarkCompleted 刷新时间戳，窗口期内不再重复执行。
    /// 全部加锁保证原子性，杜绝 TOCTOU 竞态。
    /// </summary>
    public sealed class TaskDedup
    {
        private sealed class Entry
        {
            public DateTime Timestamp;
            public bool InProgress;
        }

        private readonly Dictionary<int, Entry> _map = new Dictionary<int, Entry>();
        private readonly TimeSpan _window;
        private readonly object _lock = new object();

        public TaskDedup(TimeSpan window) => _window = window;

        /// <summary>
        /// 尝试“领取”任务执行权。返回 true 表示应跳过（重复），false 表示已领取可继续执行。
        /// 并发安全且原子：同一 id 在“执行中”或窗口期内只有一个线程能拿到执行权。
        /// </summary>
        public bool TryAcquire(int taskId)
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                if (_map.TryGetValue(taskId, out var st))
                {
                    if (st.InProgress) return true;                 // 正在执行 → 重复，禁止并发双跑
                    if ((now - st.Timestamp) < _window) return true; // 窗口期内 → 重复
                    // 窗口已过期 → 重新领取并执行
                    st.Timestamp = now;
                    st.InProgress = true;
                    return false;
                }
                _map[taskId] = new Entry { Timestamp = now, InProgress = true };
                return false; // 首次领取
            }
        }

        /// <summary>任务完成/推迟后刷新时间戳（窗口期内不再重复）。</summary>
        public void MarkCompleted(int taskId)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(taskId, out var st))
                {
                    st.InProgress = false;
                    st.Timestamp = DateTime.UtcNow;
                }
                else
                {
                    _map[taskId] = new Entry { Timestamp = DateTime.UtcNow, InProgress = false };
                }
            }
        }

        /// <summary>任务失败后清除记录，允许后续重新派发执行。</summary>
        public void MarkFailed(int taskId)
        {
            lock (_lock)
            {
                _map.Remove(taskId);
            }
        }

        /// <summary>仅供测试/运维：当前已知任务数。</summary>
        public int TrackedCount
        {
            get { lock (_lock) return _map.Count; }
        }
    }
}
