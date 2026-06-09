// 1. SendAsync 支持读取二进制第二帧（raw JPEG，用于 RemoteScreen）
// 2. 按请求类型分超时：remote_input/remote_screen 5s，ASK_INSTALL/ASK_REBOOT 300s
// 3. Connect 用 ConnectAsync(ct) 可被取消

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
    public static class PipeHelper
    {
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

        private static string GetPipeName()
        {
            int sid = GetActiveUserSessionId();
            if (sid < 0)
            {
                Logger.Warn("WTS 未找到活跃用户 Session，降级使用 SessionId=1");
                sid = 1;
            }
            return $"ITAsset4Pipe_{sid}";
        }

        private const int PIPE_TIMEOUT_SHORT = 5_000;   // remote_input / remote_screen
        private const int PIPE_TIMEOUT_LONG  = 300_000; // ASK_INSTALL / ASK_REBOOT

        private static int TimeoutMsFor(PipeRequest req)
        {
            return req.type switch
            {
                "remote_input"  => PIPE_TIMEOUT_SHORT,
                "remote_screen" => PIPE_TIMEOUT_SHORT,
                _               => PIPE_TIMEOUT_LONG,
            };
        }

        private static CancellationTokenSource _globalPipeCts = new CancellationTokenSource();
        public static void CancelAll() { _globalPipeCts.Cancel(); _globalPipeCts = new CancellationTokenSource(); }

        /// <summary>
        /// 返回 PipeResponse，其中 rawJpeg 可能为非 null（截图 Pipe 的第二帧二进制）
        /// </summary>
        public static async Task<PipeResponse> SendAsync(PipeRequest request)
        {
            string pipeName = GetPipeName();
            int timeoutMs = TimeoutMsFor(request);

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalPipeCts.Token))
            {
                linkedCts.CancelAfter(timeoutMs);
                var ct = linkedCts.Token;

                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        if (request.type != "remote_input" && request.type != "remote_screen")
                            Logger.Info($"Pipe 尝试连接: {pipeName} (timeout={timeoutMs}ms)");

                        try { await pipe.ConnectAsync(ct); }
                        catch (OperationCanceledException)
                        {
                            if (_globalPipeCts.IsCancellationRequested)
                                Logger.Info($"Pipe 连接被取消 (全局Stop): {pipeName}");
                            else
                                Logger.Warn($"Pipe 连接超时 ({timeoutMs}ms): {pipeName}");
                            return null;
                        }

                        pipe.ReadMode = PipeTransmissionMode.Message;

                        string json = JsonConvert.SerializeObject(request);
                        byte[] buf  = Encoding.UTF8.GetBytes(json);
                        await pipe.WriteAsync(buf, 0, buf.Length, ct);

                        // 读取第一帧：JSON 文本响应
                        string respJson;
                        using (var ms = new System.IO.MemoryStream())
                        {
                            byte[] tmp = new byte[65536];
                            try
                            {
                                do
                                {
                                    int n = await pipe.ReadAsync(tmp, 0, tmp.Length, ct);
                                    if (n == 0)
                                    {
                                        Logger.Warn($"Pipe 通信异常（{pipeName}）：服务端关闭了连接");
                                        return null;
                                    }
                                    ms.Write(tmp, 0, n);
                                } while (!pipe.IsMessageComplete);
                            }
                            catch (OperationCanceledException)
                            {
                                Logger.Warn($"Pipe 读取被取消（{pipeName}）");
                                return null;
                            }
                            respJson = Encoding.UTF8.GetString(ms.ToArray());
                        }

                        var result = JsonConvert.DeserializeObject<PipeResponse>(respJson);
                        if (result == null) return null;

                        // 读取第二帧 — raw JPEG 二进制（仅 remote_screen）
                        bool expectBinary = request.type == "remote_screen"
                            && result.result != null
                            && result.result.StartsWith("1") // "W|H|..." W >= 1
                            && pipe.IsConnected;
                        if (expectBinary)
                        {
                            try
                            {
                                using (var binMs = new System.IO.MemoryStream())
                                {
                                    byte[] tmp2 = new byte[65536];
                                    do
                                    {
                                        int n = await pipe.ReadAsync(tmp2, 0, tmp2.Length, ct);
                                        if (n == 0) break;
                                        binMs.Write(tmp2, 0, n);
                                    } while (!pipe.IsMessageComplete);
                                    if (binMs.Length > 0)
                                        result.rawJpeg = binMs.ToArray();
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"Pipe 读取二进制帧失败: {ex.Message}");
                                // rawJpeg 保持 null，调用方 fallback 到 base64
                            }
                        }

                        var preview = result.result ?? "";
                        if (preview.Length > 40) preview = preview.Substring(0, 40) + "...";
                        if (request.type != "remote_input" && request.type != "remote_screen")
                            Logger.Info($"Pipe 通信成功（{pipeName}）：{preview}");
                        return result;
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.Info($"Pipe SendAsync 被全局取消: {pipeName}");
                    return null;
                }
                catch (TimeoutException)
                {
                    Logger.Warn($"Pipe 超时（{pipeName}），{timeoutMs / 1000}s 内未连接");
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Pipe 通信失败（{pipeName}）: {ex.GetType().Name} {ex.Message}");
                    return null;
                }
            }
        }
    }
}
