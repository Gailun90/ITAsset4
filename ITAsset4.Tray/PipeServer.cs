using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using ITAsset4.Common;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ITAsset4.Tray
{
    public class PipeServer
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private static string PipeName => $"ITAsset4Pipe_{Process.GetCurrentProcess().SessionId}";

        public void Start() { Logger.Info($"PipeServer 启动: TraySession={Process.GetCurrentProcess().SessionId} Pipe={PipeName}"); Task.Run(() => ListenLoop(_cts.Token)); }
        public void Stop() => _cts.Cancel();

        // ══════════════════════════════════════════════════════════════════
        // 主 Pipe 监听循环
        // ══════════════════════════════════════════════════════════════════
        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous))
                    {
                        await server.WaitForConnectionAsync(ct);
                        string json;
                        using (var ms = new System.IO.MemoryStream())
                        {
                            byte[] tmp = new byte[65536];
                            do
                            {
                                int n = await server.ReadAsync(tmp, 0, tmp.Length, ct);
                                if (n == 0) break;
                                ms.Write(tmp, 0, n);
                            } while (!server.IsMessageComplete);
                            json = Encoding.UTF8.GetString(ms.ToArray());
                        }
                        var req = JsonConvert.DeserializeObject<PipeRequest>(json);
                        if (req == null) continue;
                        var resp = await ProcessRequestAsync(req);
                        if (resp != null)
                        {
                            byte[] respBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resp));
                            await server.WriteAsync(respBytes, 0, respBytes.Length, ct);

                            if (resp.rawJpeg != null && resp.rawJpeg.Length > 0)
                            {
                                await server.WriteAsync(resp.rawJpeg, 0, resp.rawJpeg.Length, ct);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.Warn($"Pipe err: {ex.Message}"); try { await Task.Delay(3000, ct); } catch { } }
            }
        }

        private static Task<PipeResponse> ProcessRequestAsync(PipeRequest req)
        {
            var tcs = new TaskCompletionSource<PipeResponse>();

            if (req.type == "remote_screen")
            {
                var resp = CaptureScreenBinary(req);
                tcs.TrySetResult(resp);
                return tcs.Task;
            }

            if (req.type == "screen_state")
            {
                tcs.TrySetResult(new PipeResponse { result = GetScreenState() });
                return tcs.Task;
            }

            if (req.type == "remote_input")
            {
                string result = HandleMouseInputPublic(req);
                tcs.TrySetResult(new PipeResponse { result = result });
                return tcs.Task;
            }

            var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            Action uiAction = () =>
            {
                string result = req.type switch
                {
                    "ASK_INSTALL" => UserDialog.AskInstall(req.app_name ?? "app", req.defer_count, req.max_defer_count),
                    "ASK_REBOOT" => UserDialog.AskReboot(req.app_name ?? "app"),
                    "NOTIFY" => UserDialog.Notify(req.title ?? "", req.message ?? ""),
                    _ => "UNKNOWN",
                };
                tcs.TrySetResult(new PipeResponse { result = result });
            };
            if (form != null && form.InvokeRequired) form.BeginInvoke(uiAction); else uiAction();
            return tcs.Task;
        }

        #region SendInput & Input Worker

        // ── Win32 API 声明 ──
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
            IntPtr pvInfo, uint nLength, out uint lpnLengthNeeded);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseWindowStation(IntPtr hWinSta);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        private const int UOI_NAME = 2;
        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const uint DESKTOP_WRITEOBJECTS = 0x0080;
        private const uint DESKTOP_ALL_ACCESS = 0x01FF;

        private static string GetDesktopName(IntPtr hDesktop)
        {
            if (hDesktop == IntPtr.Zero) return "NULL";
            try
            {
                uint needed;
                GetUserObjectInformationW(hDesktop, UOI_NAME, IntPtr.Zero, 0, out needed);
                if (needed == 0) return $"(err:0x{Marshal.GetLastWin32Error():X8})";
                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (GetUserObjectInformationW(hDesktop, UOI_NAME, buf, needed, out _))
                        return Marshal.PtrToStringUni(buf);
                    return $"(err:0x{Marshal.GetLastWin32Error():X8})";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return "(exception)"; }
        }

        // ── 结构定义 ──
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION u; }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy, mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_MOUSE = 0;
        private const int MOUSEEVENTF_MOVE = 0x0001;
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;
        private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const int MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const int MOUSEEVENTF_WHEEL = 0x0800;
        private const int MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const int MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const int WHEEL_DELTA = 120;
        private const int ABS_FLAGS = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

        // ── 诊断帮助 ──
        private static string GetDesktopDiagnostics()
        {
            try
            {
                uint tid = GetCurrentThreadId();
                IntPtr hThreadDesktop = GetThreadDesktop(tid);
                string threadDesktopName = GetDesktopName(hThreadDesktop);

                IntPtr hInputDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                string inputDesktopName = hInputDesktop != IntPtr.Zero
                    ? GetDesktopName(hInputDesktop)
                    : $"(err:0x{Marshal.GetLastWin32Error():X8})";

                IntPtr hWinSta = GetProcessWindowStation();
                string winStaName = hWinSta != IntPtr.Zero
                    ? GetDesktopName(hWinSta)
                    : $"(err:0x{Marshal.GetLastWin32Error():X8})";

                bool match = threadDesktopName == inputDesktopName;

                string diag = $"ThreadID={tid} IsThreadPool={Thread.CurrentThread.IsThreadPoolThread} IsBg={Thread.CurrentThread.IsBackground} " +
                              $"ThreadDesktop={threadDesktopName}(h=0x{hThreadDesktop.ToInt64():X}) InputDesktop={inputDesktopName}(h=0x{hInputDesktop.ToInt64():X}) " +
                              $"WinSta={winStaName}(h=0x{hWinSta.ToInt64():X}) DesktopMatch={match} ProcSession={Process.GetCurrentProcess().SessionId}";

                if (hInputDesktop != IntPtr.Zero) CloseDesktop(hInputDesktop);
                return diag;
            }
            catch (Exception ex)
            {
                return "DesktopDiag Exception: " + ex.Message;
            }
        }

        // ── 状态与队列 ──
        private static DateTime _lastInputOk = DateTime.MinValue;
        private static DateTime _lastMoveLog = DateTime.MinValue;
        private static int _moveLogSkipCount = 0;
        private static readonly object _moveLogLock = new object();

        private static readonly BlockingCollection<PipeRequest> _inputQueue =
            new BlockingCollection<PipeRequest>(new ConcurrentQueue<PipeRequest>());
        private static Thread _inputWorkerThread = default!;
        private static CancellationTokenSource _inputWorkerCts = default!;
        private static string _inputWorkerDesktopName = "";
        private static readonly object _inputWorkerLock = new object();

        // ── Worker 生命周期 ──
        public static void StartInputWorker()
        {
            if (_inputWorkerThread != null && _inputWorkerThread.IsAlive) return;
            _inputWorkerCts = new CancellationTokenSource();
            _inputWorkerThread = new Thread(InputWorkerLoop)
            {
                Name = "ITAsset4InputWorker",
                IsBackground = true,
            };
            _inputWorkerThread.SetApartmentState(ApartmentState.MTA);
            _inputWorkerThread.Start();
        }

        public static void StopInputWorker()
        {
            _inputWorkerCts?.Cancel();
            _inputQueue.CompleteAdding();
        }

        public static string HandleMouseInputPublic(PipeRequest req)
        {
            try
            {
                if (_inputQueue.IsAddingCompleted)
                    return "error: worker stopped";
                _inputQueue.Add(req);
                return "queued";
            }
            catch (Exception ex)
            {
                return "error: " + ex.Message;
            }
        }

        // ── 工作线程主体（已修复） ──
        private static void InputWorkerLoop()
        {
            try
            {
                // 1. 初始绑定到当前输入桌面
                if (!BindToCurrentInputDesktop(out string desktopName))
                {
                    Logger.Error("[InputWorker] 初始绑定桌面失败，退出");
                    return;
                }
                lock (_inputWorkerLock) { _inputWorkerDesktopName = desktopName; }
                Logger.Info($"[InputWorker] 绑定到桌面: {desktopName} (线程ID={GetCurrentThreadId()})");

                // 2. 主消息循环
                DateTime lastDesktopCheck = DateTime.MinValue;
                while (!_inputWorkerCts.Token.IsCancellationRequested)
                {
                    PipeRequest req = null;
                    try
                    {
                        // ★ 用短超时从队列取消息，保证低延迟
                        if (!_inputQueue.TryTake(out req, 100))
                        {
                            // 超时未取到请求 → 检查桌面是否切换（限频率）
                            if ((DateTime.Now - lastDesktopCheck).TotalMilliseconds >= 500)
                            {
                                lastDesktopCheck = DateTime.Now;
                                string currentDesktop = GetCurrentInputDesktopName();
                                if (currentDesktop != null && currentDesktop != desktopName)
                                {
                                    Logger.Info($"[InputWorker] 桌面切换: {desktopName} → {currentDesktop}，重新绑定...");
                                    if (BindToCurrentInputDesktop(out string newName))
                                    {
                                        desktopName = newName;
                                        lock (_inputWorkerLock) { _inputWorkerDesktopName = newName; }
                                        Logger.Info($"[InputWorker] 重新绑定成功: {newName}");
                                    }
                                    else
                                    {
                                        Logger.Warn($"[InputWorker] 重新绑定失败，仍尝试使用原桌面");
                                    }
                                }
                            }
                            continue; // 回去继续取消息
                        }
                    }
                    catch (InvalidOperationException) { break; } // 队列完成
                    catch (OperationCanceledException) { break; }

                    // 处理取到的请求
                    if (req != null)
                    {
                        string result = SendMouseInputInternal(req);
                        bool isMove = req.event_type?.ToLower() == "move";

                        // 日志节流（move 事件只每 2 秒输出一次）
                        if (isMove)
                        {
                            lock (_moveLogLock)
                            {
                                _moveLogSkipCount++;
                                if ((DateTime.Now - _lastMoveLog).TotalSeconds >= 2)
                                {
                                    Logger.Info($"[InputWorker] move 事件 (过去2秒共 {_moveLogSkipCount} 条)");
                                    _moveLogSkipCount = 0;
                                    _lastMoveLog = DateTime.Now;
                                }
                            }
                        }
                        else
                        {
                            Logger.Info($"[InputWorker] {req.event_type}({req.button}) → {result}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[InputWorker] Fatal: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Logger.Info("[InputWorker] Loop ended");
            }
        }

        // ── 桌面绑定帮助方法 ──
        /// <summary>
        /// 打开当前输入桌面并 SetThreadDesktop，成功返回 true 和桌面名称
        /// </summary>
        private static bool BindToCurrentInputDesktop(out string desktopName)
        {
            desktopName = "";
            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
            if (hDesktop == IntPtr.Zero)
            {
                Logger.Warn($"[InputWorker] OpenInputDesktop 失败: 0x{Marshal.GetLastWin32Error():X8}");
                return false;
            }
            desktopName = GetDesktopName(hDesktop);
            bool ok = SetThreadDesktop(hDesktop);
            int err = Marshal.GetLastWin32Error();
            CloseDesktop(hDesktop);

            if (!ok)
            {
                Logger.Warn($"[InputWorker] SetThreadDesktop 失败: 桌面={desktopName}, err=0x{err:X8}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 获取当前输入桌面的名称（不进行绑定）
        /// </summary>
        private static string GetCurrentInputDesktopName()
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS); // 只读权限足够
            if (hDesktop == IntPtr.Zero) return null;
            string name = GetDesktopName(hDesktop);
            CloseDesktop(hDesktop);
            return name;
        }

        // ── 状态查询 ──
        public static string GetInputWorkerDesktop()
        {
            lock (_inputWorkerLock) { return _inputWorkerDesktopName; }
        }

        public static string GetInputStatus() =>
            _lastInputOk == DateTime.MinValue
                ? "无输入记录"
                : $"最后成功: {_lastInputOk:HH:mm:ss.fff}, 距今 {(DateTime.Now - _lastInputOk).TotalSeconds:F0}s";

        /// <summary>
        /// 检测当前屏幕状态：锁屏/登录/UAC 安全桌面(Winlogon) / 屏保 / 正常。
        /// 供远程桌面在连接时及时告知前端操作者。
        /// </summary>
        public static string GetScreenState()
        {
            try
            {
                IntPtr h = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                if (h == IntPtr.Zero)
                    return ScreenStateMsg.NoDesktop;
                string name = GetDesktopName(h);
                CloseDesktop(h);
                if (name == "Winlogon")
                    return ScreenStateMsg.Locked;       // 锁屏 / 登录 / UAC 安全桌面
                if (name.IndexOf("Screen-saver", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ScreenStateMsg.ScreenSaver;
                return ScreenStateMsg.Active;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[ScreenState] 检测异常: {ex.Message}");
                return ScreenStateMsg.Active;
            }
        }

        // ── 核心 SendInput 实现 ──
        private static string SendMouseInputInternal(PipeRequest req)
        {
            try
            {
                var bounds = Screen.PrimaryScreen.Bounds;
                foreach (var s in Screen.AllScreens)
                    bounds = System.Drawing.Rectangle.Union(bounds, s.Bounds);

                if (bounds.Width <= 1 || bounds.Height <= 1)
                {
                    Logger.Error($"[Input] 屏幕尺寸异常: {bounds.Width}x{bounds.Height}");
                    return "error: invalid screen bounds";
                }

                double nx = Math.Max(0.0, Math.Min(1.0, req.mouse_x));
                double ny = Math.Max(0.0, Math.Min(1.0, req.mouse_y));

                int px = bounds.X + (int)(nx * bounds.Width);
                int py = bounds.Y + (int)(ny * bounds.Height);

                int absX = (int)Math.Round((px - bounds.X) * 65535.0 / (bounds.Width - 1));
                int absY = (int)Math.Round((py - bounds.Y) * 65535.0 / (bounds.Height - 1));

                var inputs = new System.Collections.Generic.List<INPUT>();
                // 总是先发送一次绝对移动，确保鼠标位置正确
                inputs.Add(MakeMouseInput(absX, absY, MOUSEEVENTF_MOVE | ABS_FLAGS, 0));

                int downFlag = 0, upFlag = 0;
                switch (req.button?.ToLower())
                {
                    case "right": downFlag = MOUSEEVENTF_RIGHTDOWN; upFlag = MOUSEEVENTF_RIGHTUP; break;
                    case "middle": downFlag = MOUSEEVENTF_MIDDLEDOWN; upFlag = MOUSEEVENTF_MIDDLEUP; break;
                    default: downFlag = MOUSEEVENTF_LEFTDOWN; upFlag = MOUSEEVENTF_LEFTUP; break;
                }

                switch (req.event_type?.ToLower())
                {
                    case "down":
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | downFlag, 0));
                        break;
                    case "up":
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | upFlag, 0));
                        break;
                    case "click":
                        Logger.Info("[Input] click ignored (use down+up separately)");
                        return "ignored";
                    case "dblclick":
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | downFlag, 0));
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | upFlag, 0));
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | downFlag, 0));
                        inputs.Add(MakeMouseInput(absX, absY, ABS_FLAGS | upFlag, 0));
                        break;
                    case "scroll":
                        int wheelData = req.scroll_delta * WHEEL_DELTA;
                        inputs.Add(MakeMouseInput(absX, absY,
                            MOUSEEVENTF_MOVE | ABS_FLAGS | MOUSEEVENTF_WHEEL, wheelData));
                        break;
                    default:
                        // 默认就是 move（已经加入了前面的移动事件）
                        break;
                }

                uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
                int win32err = Marshal.GetLastWin32Error();

                if (sent != inputs.Count)
                {
                    string failDiag = GetDesktopDiagnostics();
                    Logger.Error($"[Input] FAIL: sent={sent}/{inputs.Count} win32err=0x{win32err:X8} type={req.event_type} btn={req.button} norm=({nx:F3},{ny:F3}) Desktop=[{failDiag}]");
                    return $"error: SendInput sent {sent}/{inputs.Count}, win32err=0x{win32err:X8}";
                }

                _lastInputOk = DateTime.Now;
                return "ok";
            }
            catch (Exception ex)
            {
                Logger.Error($"[Input] exception: {ex.Message}\n{ex.StackTrace}");
                return $"error: {ex.Message}";
            }
        }

        private static INPUT MakeMouseInput(int x, int y, int flags, int data) => new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags }
            }
        };

        #endregion

        #region Screen Capture

        private static PipeResponse CaptureScreenBinary(PipeRequest req)
        {
            try
            {
                int quality = 55, maxW = 1280;
                if (int.TryParse(req.app_name, out int q)) quality = Math.Max(10, Math.Min(100, q));
                if (int.TryParse(req.description, out int mw)) maxW = Math.Max(320, mw);
                var bounds = Screen.PrimaryScreen.Bounds;
                foreach (var s in Screen.AllScreens)
                    bounds = System.Drawing.Rectangle.Union(bounds, s.Bounds);
                using (var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    System.Drawing.Bitmap output = bmp;
                    if (maxW > 0 && bmp.Width > maxW) { int nh = (int)((double)bmp.Height / bmp.Width * maxW); output = new System.Drawing.Bitmap(bmp, maxW, nh); }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        var enc = GetJpegEncoder();
                        var ep = new System.Drawing.Imaging.EncoderParameters(1);
                        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                        int outW = output.Width, outH = output.Height;
                        output.Save(ms, enc, ep);
                        if (output != bmp) output.Dispose();

                        byte[] jpegBytes = ms.ToArray();
                        string base64 = Convert.ToBase64String(jpegBytes);

                        return new PipeResponse
                        {
                            result = outW + "|" + outH + "|" + base64,
                            rawJpeg = jpegBytes,
                        };
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Screen capture err: {ex.Message}"); return new PipeResponse { result = "" }; }
        }

        private static System.Drawing.Imaging.ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        #endregion
    }

    // ══════════════════════════════════════════════════════════════════════
    // InputPipeServer — 专用输入 Pipe（保持不变）
    // ══════════════════════════════════════════════════════════════════════
    public class InputPipeServer
    {
        private static string InputPipeName =>
            $"ITAsset4Input_{Process.GetCurrentProcess().SessionId}";

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private static int _connectedClients;
        private static DateTime _lastClientConnect = DateTime.MinValue;
        private static DateTime _lastClientDisconnect = DateTime.MinValue;
        private static readonly object _statusLock = new object();

        public void Start()
        {
            Logger.Info($"InputPipeServer 启动: TraySession={Process.GetCurrentProcess().SessionId} Pipe={InputPipeName}");
            Task.Run(() => AcceptLoop(_cts.Token));
            Task.Run(() => SelfCheckLoop(_cts.Token));
        }
        public void Stop() => _cts.Cancel();

        private async Task SelfCheckLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(60_000, ct); } catch { break; }
                if (ct.IsCancellationRequested) break;

                int active;
                lock (_statusLock) { active = _connectedClients; }
                Logger.Info($"[InputPipe] SELFCHECK: 活跃连接={active} | {PipeServer.GetInputStatus()} | WorkerDesktop='{PipeServer.GetInputWorkerDesktop()}'");
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        InputPipeName, PipeDirection.In, 4,
                        PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);

                    lock (_statusLock)
                    {
                        _connectedClients++;
                        _lastClientConnect = DateTime.Now;
                    }
                    Logger.Info($"[InputPipe] 客户端已连接 (当前活跃={_connectedClients})");
                    _ = Task.Run(() => ServeClientAsync(server, ct));
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    Logger.Warn($"[InputPipe] accept err: {ex.Message}");
                    try { await Task.Delay(500, ct); } catch { }
                }
            }
        }

        private static async Task ServeClientAsync(NamedPipeServerStream server, CancellationToken ct)
        {
            var connectTime = DateTime.Now;
            int handledCount = 0;

            using (server)
            {
                var buf = new byte[16384];
                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    try
                    {
                        int n = await server.ReadAsync(buf, 0, buf.Length, ct);
                        if (n == 0)
                        {
                            Logger.Info($"[InputPipe] 客户端断开 (连接时长={(DateTime.Now - connectTime).TotalSeconds:F0}s, 处理了{handledCount}条)");
                            break;
                        }
                        string json = Encoding.UTF8.GetString(buf, 0, n);
                        var pr = JsonConvert.DeserializeObject<PipeRequest>(json);
                        if (pr != null && pr.type == "remote_input")
                        {
                            string result = PipeServer.HandleMouseInputPublic(pr);
                            handledCount++;
                            if (pr.event_type != "move")
                                Logger.Info($"[InputPipe] #{handledCount} {pr.event_type}({pr.button}) → {result}");
                        }
                    }
                    catch (System.IO.IOException)
                    {
                        Logger.Info($"[InputPipe] IO 断开 (连接时长={(DateTime.Now - connectTime).TotalSeconds:F0}s, 处理了{handledCount}条)");
                        break;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[InputPipe] read err: {ex.Message}");
                        break;
                    }
                }
            }

            lock (_statusLock)
            {
                _connectedClients = Math.Max(0, _connectedClients - 1);
                _lastClientDisconnect = DateTime.Now;
            }
            Logger.Info($"[InputPipe] 客户端会话结束 (活跃连接={_connectedClients})");
        }
    }
}