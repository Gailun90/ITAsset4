using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    /// - 持久连接 Pipe + 30s 心跳 ping
    /// - 心跳失败自动重建 Pipe
    /// - SessionId 日志（定位 Session 不匹配问题）
    /// - 原生 WriteAsync，不用 StreamWriter，确保 Message 模式下一帧一条完整 JSON
    /// </summary>
    public class InputPipeClient : IDisposable
    {
        private NamedPipeClientStream _pipe = default!;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _hbCts = default!;
        private Task _hbTask = default!;
        private volatile bool _disposed;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer, uint Reserved, uint Version,
            out IntPtr ppSessionInfo, out uint pCount);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int    SessionId;
            public IntPtr pWinStationName;
            public int    State;
        }

        private const int WTSActive = 0;

        private static int GetActiveUserSessionId()
        {
            IntPtr buf   = IntPtr.Zero;
            uint   count = 0;
            try
            {
                if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buf, out count))
                    return -1;

                int size   = Marshal.SizeOf<WTS_SESSION_INFO>();
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

        private static string GetInputPipeName()
        {
            int sid = GetActiveUserSessionId();
            Logger.Info($"[InputPipe] ActiveSession={sid}  →  Pipe=ITAsset4Input_{sid}");
            if (sid < 0) sid = 1;
            return $"ITAsset4Input_{sid}";
        }

        /// <summary>
        /// 发送鼠标输入 JSON（一帧 = 一条完整消息）
        /// </summary>
        public async Task SendInputAsync(PipeRequest req)
        {
            if (_disposed) return;
            await _lock.WaitAsync();
            try
            {
                await EnsureConnectedAsync();
                string json = JsonConvert.SerializeObject(req) + "\n";
                byte[] buf = Encoding.UTF8.GetBytes(json);
                await _pipe.WriteAsync(buf, 0, buf.Length);
                await _pipe.FlushAsync();
            }
            catch (Exception ex)
            {
                if (req?.event_type != "move")
                    Logger.Warn($"[InputPipe] SendInputAsync failed ({req?.event_type}): {ex.Message}");
                _pipe?.Dispose();
                _pipe = null;
            }
            finally { _lock.Release(); }
        }

        private async Task EnsureConnectedAsync()
        {
            if (_pipe?.IsConnected == true) return;

            string pipeName = GetInputPipeName();
            _pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.Out, PipeOptions.Asynchronous);

            var connectTask = _pipe.ConnectAsync();
            var delayTask = Task.Delay(2000);
            var completed = await Task.WhenAny(connectTask, delayTask);
            if (completed == delayTask)
            {
                Logger.Warn($"[InputPipe] 连接超时: {pipeName}，Tray 可能未就绪");
                _pipe.Dispose();
                _pipe = null;
                throw new TimeoutException($"InputPipe connect timeout: {pipeName}");
            }
            await connectTask;

            Logger.Info($"InputPipe 已连接: {pipeName}");

            // 启动心跳
            StartHeartbeat();
        }

        // ═══════════════════════════════════════════════════════════════════
        // 心跳 — 每 30 秒发 ping，失败自动重连
        // ═══════════════════════════════════════════════════════════════════

        private void StartHeartbeat()
        {
            _hbCts?.Cancel();
            _hbCts = new CancellationTokenSource();
            _hbTask = Task.Run(() => HeartbeatLoopAsync(_hbCts.Token));
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            int failCount = 0;
            const int maxConsecutiveFails = 3;

            while (!ct.IsCancellationRequested && !_disposed)
            {
                try { await Task.Delay(30_000, ct); }
                catch { break; }

                if (_disposed || ct.IsCancellationRequested) break;

                bool ok = await SendPingAsync();
                if (ok)
                {
                    failCount = 0;
                }
                else
                {
                    failCount++;
                    

                    if (failCount >= maxConsecutiveFails)
                    {
                        
                        await _lock.WaitAsync();
                        try
                        {
                            _pipe?.Dispose();
                            _pipe = null;
                        }
                        finally { _lock.Release(); }
                        failCount = 0;
                    }
                }
            }
        }

        private async Task<bool> SendPingAsync()
        {
            try
            {
                if (_pipe?.IsConnected != true) return false;

                // 用独立锁避免与 SendInputAsync 并发
                // ping 消息极小，不限时写入
                string json = "{\"type\":\"ping\"}\n";
                byte[] buf = Encoding.UTF8.GetBytes(json);
                var writeTask = _pipe.WriteAsync(buf, 0, buf.Length);
                var timeoutTask = Task.Delay(5000);
                var completed = await Task.WhenAny(writeTask, timeoutTask);
                if (completed == timeoutTask)
                {
                    Logger.Warn("[InputPipe] 心跳写入超时");
                    return false;
                }
                await writeTask;
                await _pipe.FlushAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _hbCts?.Cancel();
            try { _hbTask?.Wait(500); } catch { }
            _hbCts?.Dispose();
            _pipe?.Dispose();
            _lock?.Dispose();
        }
    }
}
