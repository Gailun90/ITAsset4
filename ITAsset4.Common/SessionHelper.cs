using System;
using System.Runtime.InteropServices;

namespace ITAsset4.Common
{
    /// <summary>
    /// 会话辅助：获取当前“活动”用户 Session Id。
    /// 放在 Common 中，使 Tray 也能判断自己是否仍处于活动 Session
    /// （用于多用户登录时，非活动 Session 的 Tray 主动退出、释放端口）。
    /// </summary>
    public static class SessionHelper
    {
        private const int WTSActive = 0;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer, uint reserved, uint version,
            out IntPtr ppSessionInfo, out uint pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pSessionInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public int State;
        }

        /// <summary>
        /// 返回活动（WTSActive 且非 Session 0）用户 Session Id；无则返回 -1。
        /// </summary>
        public static int GetActiveUserSessionId()
        {
            IntPtr buf = IntPtr.Zero;
            uint count = 0;
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out count))
                    return -1;
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                IntPtr cur = buf;
                for (uint i = 0; i < count; i++, cur = IntPtr.Add(cur, size))
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(cur);
                    if (info.State == WTSActive && info.SessionId != 0)
                        return info.SessionId;
                }
                return -1;
            }
            finally
            {
                if (buf != IntPtr.Zero) WTSFreeMemory(buf);
            }
        }
    }
}
