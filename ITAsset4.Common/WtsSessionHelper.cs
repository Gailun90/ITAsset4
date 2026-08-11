using System;
using System.Runtime.InteropServices;

namespace ITAsset4.Common
{
    /// <summary>
    /// WTS 会话工具：Tray 与 Service 共用同一份实现，确保两端对
    /// "当前活跃且已解锁的物理控制台会话" 的判断完全一致，消除之前
    /// "两端各算各的 session id 导致命名管道错配、连接超时" 的 bug。
    /// </summary>
    public static class WtsSessionHelper
    {
        [DllImport("kernel32.dll")]
        public static extern int WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll")]
        public static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);

        public enum WTS_INFO_CLASS { WTSConnectState = 8 }

        public enum WTS_CONNECTSTATE_CLASS
        {
            Active, Connected, ConnectQuery, Shadow,
            Disconnected, Idle, Listen, Reset, Down, Init
        }

        /// <summary>
        /// 判断当前是否存在"活跃且已解锁"的物理控制台会话。
        /// 仅当控制台会话存在且其连接状态为 Active 时返回 true（登录界面 / 锁屏 / 断开 均为 false）。
        /// out 参数返回该控制台会话 id（找不到时返回 -1）。
        /// </summary>
        public static bool IsPhysicalDesktopActiveAndUnlocked(out int sessionId)
        {
            sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == -1) // 0xFFFFFFFF：无活跃控制台会话（如登录界面）
                return false;

            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId,
                    WTS_INFO_CLASS.WTSConnectState, out IntPtr p, out _))
                return false;

            var state = (WTS_CONNECTSTATE_CLASS)Marshal.ReadInt32(p);
            WTSFreeMemory(p);

            return state == WTS_CONNECTSTATE_CLASS.Active;
        }
    }
}
