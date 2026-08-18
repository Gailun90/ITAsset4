using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ITAsset4.Common;

namespace ITAsset4.Service
{
    /// <summary>
    /// SessionManager — 负责确保 Tray 在用户登录时自动启动。
    ///
    /// 机制变更（2026-08）：不再由 Service 用 CreateProcessAsUser / 计划任务拉起 Tray，
    /// 改为在公共 Startup 文件夹（C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup）
    /// 创建一个指向 ITAsset4.Tray.exe 的快捷方式。Windows 会在任意用户登录时自动启动它，
    /// 天然运行在正确用户会话、拥有交互桌面访问权，彻底绕开跨会话桌面令牌问题，
    /// 也无需服务常驻监听 WTS 事件。
    ///
    /// 保留 GetActiveUserSessionId() 供 TaskExecutor 连接 Tray 命名管道时使用。
    /// </summary>
    public class SessionManager : IDisposable
    {
        private string _trayExePath;
        // 快捷方式已确认存在后，后续轮询跳过分支不再打日志，避免每 30s 刷屏。
        private bool _shortcutConfirmed;

        public SessionManager()
        {
            string serviceDir = AppDomain.CurrentDomain.BaseDirectory;
            _trayExePath = Path.Combine(serviceDir, "ITAsset4.Tray.exe");
            if (!File.Exists(_trayExePath))
            {
                string alt = Path.Combine(serviceDir, "..", "ITAsset4.Tray.exe");
                if (File.Exists(alt)) _trayExePath = Path.GetFullPath(alt);
            }
            Logger.Info($"[SessionMgr] 已初始化, Tray={_trayExePath}");
        }

        /// <summary>
        /// 获取当前活动用户 Session ID（供 TaskExecutor 连接 Pipe 使用）
        /// </summary>
        public int GetActiveSessionId() => GetActiveUserSessionId();

        /// <summary>
        /// 启动：确保公共 Startup 快捷方式存在（已存在则跳过）。
        /// 同时主循环也会周期性调用本方法作为兜底（防快捷方式被误删）。
        /// </summary>
        public void Start()
        {
            EnsureStartupShortcut();
        }

        /// <summary>
        /// 在公共 Startup 文件夹创建指向 ITAsset4.Tray.exe 的快捷方式。
        /// 已存在则跳过（不覆盖，保留用户可能手动修改的快捷方式）。
        /// 使用 IShellLink + IPersistFile COM 互操作，自包含无需额外引用。
        /// </summary>
        public void EnsureStartupShortcut()
        {
            try
            {
                string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                if (string.IsNullOrEmpty(startupDir))
                {
                    Logger.Warn("[SessionMgr] 无法解析公共 Startup 路径，跳过快捷方式创建");
                    return;
                }
                string linkPath = Path.Combine(startupDir, "ITAsset4 Tray.lnk");
                if (File.Exists(linkPath))
                {
                    if (!_shortcutConfirmed)
                    {
                        _shortcutConfirmed = true;
                        Logger.Info($"[SessionMgr] Startup 快捷方式已存在，跳过: {linkPath}");
                    }
                    return;
                }

                _shortcutConfirmed = false;
                if (!File.Exists(_trayExePath))
                {
                    Logger.Warn($"[SessionMgr] Tray EXE 不存在，无法创建快捷方式: {_trayExePath}");
                    return;
                }

                var link = (IShellLink)new ShellLinkObject();
                try
                {
                    link.SetPath(_trayExePath);
                    link.SetWorkingDirectory(Path.GetDirectoryName(_trayExePath) ?? string.Empty);
                    link.SetDescription("ITAsset4 Agent Tray");
                    link.SetShowCmd(1); // SW_SHOWNORMAL
                    var persist = (IPersistFile)link;
                    persist.Save(linkPath, true);
                }
                finally
                {
                    Marshal.ReleaseComObject(link);
                }
                Logger.Info($"[SessionMgr] ✅ 已在公共 Startup 创建快捷方式: {linkPath}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SessionMgr] 创建 Startup 快捷方式失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // WTS：仅用于解析当前活动用户会话（TaskExecutor 连接 Tray 管道用）
        // ═══════════════════════════════════════════════════════════════

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sid, out IntPtr phToken);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(IntPtr h, uint r, uint v,
            out IntPtr ppInfo, out uint pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr p);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const int WTSActive = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        { public int SessionId; public IntPtr pWinStationName; public int State; }

        public static int GetActiveUserSessionId()
        {
            IntPtr buf = IntPtr.Zero; uint count = 0;
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out count))
                    return -1;
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                IntPtr cur = buf;

                // 第一遍：优先返回 WTSActive 且 SessionId != 0 的会话（标准交互前台会话）
                for (uint i = 0; i < count; i++, cur = IntPtr.Add(cur, size))
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(cur);
                    if (info.State == WTSActive && info.SessionId != 0)
                        return info.SessionId;
                }

                // 第二遍（兜底）：返回第一个能取到用户令牌的已登录会话。
                cur = buf;
                for (uint i = 0; i < count; i++, cur = IntPtr.Add(cur, size))
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(cur);
                    if (info.SessionId == 0) continue;
                    if (WTSQueryUserToken((uint)info.SessionId, out IntPtr hTok))
                    {
                        CloseHandle(hTok);
                        Logger.Info($"[SessionMgr] 兜底层命中会话 S={info.SessionId}（非 WTSActive，但有用户令牌）");
                        return info.SessionId;
                    }
                }
                return -1;
            }
            finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
        }

        // ═══════════════════════════════════════════════════════════════
        // IShellLink / IPersistFile COM 互操作：创建 .lnk 快捷方式
        // （自包含，无需额外引用）
        // ═══════════════════════════════════════════════════════════════

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        [ClassInterface(ClassInterfaceType.None)]
        private class ShellLinkObject { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxDir);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxArgs);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFileName);
        }

        public void Dispose()
        {
            // 已无后台线程或句柄需要释放；保持 IDisposable 契约以便 AgentWorker finally 清理。
        }
    }
}
