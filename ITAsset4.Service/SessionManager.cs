using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ITAsset4.Common;

namespace ITAsset4.Service
{
    /// <summary>
    /// SessionManager — 在用户 Session 中启动 Tray 进程
    /// - 使用 WTSQueryUserToken + CreateProcessAsUser 确保 Tray 运行在正确 Session
    /// - 新增 OnTrayNeeded 事件，由后台线程监听 WTS Session 变化（登录/解锁），
    ///   发生时立即触发事件通知 AgentWorker 主循环，不再等最长 1 分钟的轮询间隔
    /// - 后台线程改用 WTSWaitSystemEvent 阻塞等待，CPU 占用为零
    /// - CheckAndLaunchTray() 保留，供主循环兜底轮询
    /// </summary>
    public class SessionManager : IDisposable
    {
        private string _trayExePath;
        private readonly object _lock = new object();
        private int _lastLaunchedSession = -1;
        private DateTime _lastLaunchTime = DateTime.MinValue;
        private static readonly TimeSpan MinLaunchInterval = TimeSpan.FromSeconds(90);

        // 用户登录/Session 切换时触发，通知 AgentWorker 立即调用 CheckAndLaunchTray
        public event Action OnTrayNeeded;

        private CancellationTokenSource _watchCts;
        private Thread _watchThread;

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
        /// 启动：立即检查一次，再启动后台线程监听 WTS 事件
        /// </summary>
        public void Start()
        {
            CheckAndLaunchTray();
            StartWatchThread();
        }

        /// <summary>
        /// 由 AgentService 主循环调用，检查是否需要启动 Tray（兜底轮询）
        /// </summary>
        public void CheckAndLaunchTray()
        {
            int sid = GetActiveUserSessionId();
            if (sid <= 0) return;

            if (IsTrayAlreadyRunning(sid))
            {
                lock (_lock) { _lastLaunchedSession = sid; }
                return;
            }

            // 节流：避免 Tray 反复崩溃时疯狂重启
            lock (_lock)
            {
                if (sid == _lastLaunchedSession
                    && (DateTime.Now - _lastLaunchTime) < MinLaunchInterval)
                    return;
            }

            if (!File.Exists(_trayExePath))
            {
                Logger.Warn($"[SessionMgr] Tray EXE 不存在: {_trayExePath}");
                return;
            }

            Logger.Info($"[SessionMgr] Tray 未运行（Session={sid}），准备启动...");
            bool launched = LaunchProcessInSession(sid, _trayExePath);
            if (launched)
            {
                lock (_lock) { _lastLaunchedSession = sid; _lastLaunchTime = DateTime.Now; }
                Logger.Info($"[SessionMgr] ✅ Tray Session={sid} 已启动");
            }
            else
            {
                Logger.Warn($"[SessionMgr] ❌ Tray Session={sid} 启动失败");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 后台监听 WTS Session 事件（登录 / 解锁 / Session 切换）
        //
        // WTSWaitSystemEvent 阻塞直到指定事件发生，CPU 占用为零。
        // 相比 Task.Delay(1分钟) 轮询，响应时间从最坏 60s 降到 ~1s。
        // ═══════════════════════════════════════════════════════════════════

        // 监听的事件掩码（WTS_EVENT_*）
        // 0x00000002 = WTS_EVENT_LOGON（用户登录）
        // 0x00000008 = WTS_EVENT_CONNECT（Session 连接/RDP 接入）
        // 0x00000020 = WTS_EVENT_UNLOCK（解锁）
        private const uint WTS_WATCH_FLAGS = 0x00000002 | 0x00000008 | 0x00000020;

        private void StartWatchThread()
        {
            _watchCts = new CancellationTokenSource();
            _watchThread = new Thread(WatchLoop)
            {
                Name         = "ITAsset4SessionWatch",
                IsBackground = true,
            };
            _watchThread.Start();
        }

        private void WatchLoop()
        {
            Logger.Info("[SessionMgr] WTS 监听线程已启动");
            while (!_watchCts.IsCancellationRequested)
            {
                try
                {
                    // 阻塞等待：有登录/连接/解锁事件时立即返回
                    uint eventFlags = 0;
                    bool ok = WTSWaitSystemEvent(IntPtr.Zero, WTS_WATCH_FLAGS, out eventFlags);

                    if (_watchCts.IsCancellationRequested) break;

                    if (ok)
                    {
                        Logger.Info($"[SessionMgr] WTS 事件触发 flags=0x{eventFlags:X8}，通知主循环检查 Tray");
                        try { OnTrayNeeded?.Invoke(); }
                        catch (Exception ex) { Logger.Warn($"[SessionMgr] OnTrayNeeded 回调异常: {ex.Message}"); }
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        // ERROR_INVALID_HANDLE(6) / ERROR_CANCELLED(1223) 是正常停止
                        if (err != 6 && err != 1223)
                            Logger.Warn($"[SessionMgr] WTSWaitSystemEvent 失败 err=0x{err:X8}，2s 后重试");
                        // 短暂等待，防止出错时 CPU 空转
                        Thread.Sleep(2000);
                    }
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    if (!_watchCts.IsCancellationRequested)
                        Logger.Warn($"[SessionMgr] WatchLoop 异常: {ex.Message}");
                    Thread.Sleep(2000);
                }
            }
            Logger.Info("[SessionMgr] WTS 监听线程已退出");
        }

        // ═══════════════════════════════════════════════════════════════════
        // CreateProcessAsUser
        // ═══════════════════════════════════════════════════════════════════

        public static bool LaunchProcessInSession(int sessionId, string exePath, string args = "")
        {
            IntPtr hUserToken = IntPtr.Zero;
            IntPtr hDupToken  = IntPtr.Zero;
            IntPtr hEnvBlock  = IntPtr.Zero;

            try
            {
                if (!WTSQueryUserToken((uint)sessionId, out hUserToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn($"[SessionMgr] WTSQueryUserToken 失败 S={sessionId} err=0x{err:X8}");
                    return false;
                }

                var sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(sa);
                if (!DuplicateTokenEx(hUserToken, MAXIMUM_ALLOWED, ref sa,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                    TOKEN_TYPE.TokenPrimary, out hDupToken))
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn($"[SessionMgr] DuplicateTokenEx 失败 err=0x{err:X8}");
                    return false;
                }

                CreateEnvironmentBlock(out hEnvBlock, hDupToken, false);

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default";

                var pi = new PROCESS_INFORMATION();
                uint flags = NORMAL_PRIORITY_CLASS;
                if (hEnvBlock != IntPtr.Zero) flags |= CREATE_UNICODE_ENVIRONMENT;

                string cmdLine = $"\"{exePath}\" {args}".Trim();
                bool ok = CreateProcessAsUser(hDupToken, null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    flags, hEnvBlock, null, ref si, out pi);

                if (ok)
                {
                    Logger.Info($"[SessionMgr] PID={pi.dwProcessId} S={sessionId}");
                    CloseHandle(pi.hProcess);
                    CloseHandle(pi.hThread);
                    return true;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn($"[SessionMgr] CreateProcessAsUser 失败 err=0x{err:X8}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[SessionMgr] 异常: {ex.Message}");
                return false;
            }
            finally
            {
                if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
                if (hDupToken  != IntPtr.Zero) CloseHandle(hDupToken);
                if (hEnvBlock  != IntPtr.Zero) DestroyEnvironmentBlock(hEnvBlock);
            }
        }

        private static bool IsTrayAlreadyRunning(int sessionId)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("ITAsset4.Tray");
                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited && p.SessionId == sessionId)
                            return true;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SessionMgr] IsTrayAlreadyRunning err: {ex.Message}");
            }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════
        // P/Invoke
        // ═══════════════════════════════════════════════════════════════════

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sid, out IntPtr phToken);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(IntPtr h, uint r, uint v,
            out IntPtr ppInfo, out uint pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr p);

        // v5.4: WTSWaitSystemEvent — 阻塞直到 WTS 事件发生
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSWaitSystemEvent(IntPtr hServer, uint EventMask, out uint pEventFlags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr hExisting, uint access,
            ref SECURITY_ATTRIBUTES sa, SECURITY_IMPERSONATION_LEVEL level,
            TOKEN_TYPE type, out IntPtr phNew);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr env);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd,
            IntPtr procAttr, IntPtr threadAttr, bool inherit, uint flags,
            IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        private const uint MAXIMUM_ALLOWED = 0x02000000;
        private const uint NORMAL_PRIORITY_CLASS = 0x0020;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int WTSActive = 0;

        private enum SECURITY_IMPERSONATION_LEVEL { SecurityIdentification = 1 }
        private enum TOKEN_TYPE { TokenPrimary = 1 }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars;
            public uint dwFillAttribute, dwFlags; public ushort wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

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
                for (uint i = 0; i < count; i++, cur = IntPtr.Add(cur, size))
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(cur);
                    if (info.State == WTSActive && info.SessionId != 0)
                        return info.SessionId;
                }
                return -1;
            }
            finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
        }

        public void Dispose()
        {
            try
            {
                _watchCts?.Cancel();
                // WTSWaitSystemEvent 是阻塞调用，Cancel 信号无法直接中断它。
                // IsBackground=true 保证进程退出时线程自动结束，这里不做 Join 避免死锁。
                _watchCts?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[SessionMgr] Dispose 异常: {ex.Message}");
            }
        }
    }
}
