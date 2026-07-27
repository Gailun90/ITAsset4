using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    /// PipeScreenClient — 通过命名管道连接 Tray 截屏/弹窗服务
    /// 替换 TcpScreenClient，使用长连接复用。
    /// 
    /// 支持 SendAsync(PipeRequest) → Task&lt;PipeResponse&gt;（兼容 RemoteScreen）
    /// 支持 RequestScreenStateAsync() 快捷方法
    /// </summary>
    public class PipeScreenClient : IDisposable
    {
        private NamedPipeClientStream _pipe;
        private int _sessionId = -1;
        private readonly object _lock = new object();
        private const int CONNECT_TIMEOUT_MS = 3000;
        private const int ASK_TIMEOUT_MS = 300_000;

        public bool IsConnected => _pipe?.IsConnected ?? false;

        public async Task ConnectAsync(int sessionId)
        {
            _sessionId = sessionId;
            string pipeName = $"ITAsset4_{sessionId}_Screen";
            Logger.Info($"[PipeScreenClient] 连接 \\\\.\\pipe\\{pipeName}");

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(CONNECT_TIMEOUT_MS);
            _pipe.ReadMode = PipeTransmissionMode.Byte;
            Logger.Info("[PipeScreenClient] 已连接");
        }

        /// <summary>
        /// 发送请求并返回响应（兼容 TcpScreenClient.SendAsync）
        /// </summary>
        public async Task<PipeResponse> SendAsync(PipeRequest request)
        {
            int timeoutMs = request.type switch
            {
                "remote_screen" => 5000,
                _               => ASK_TIMEOUT_MS,
            };

            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    lock (_lock)
                    {
                        if (_pipe == null || !_pipe.IsConnected)
                            return null;
                    }

                    string reqJson = JsonConvert.SerializeObject(request);
                    await TcpFrameHelper.WriteFrameAsync(_pipe, reqJson, cts.Token);

                    string respJson = await TcpFrameHelper.ReadFrameAsync(_pipe, cts.Token);
                    if (string.IsNullOrEmpty(respJson))
                    {
                        Logger.Warn("[PipeScreen] 响应为空");
                        Disconnect();
                        return null;
                    }

                    return JsonConvert.DeserializeObject<PipeResponse>(respJson);
                }
                catch (OperationCanceledException)
                {
                    Logger.Warn($"[PipeScreen] 请求超时 ({timeoutMs}ms): {request.type}");
                    return null;
                }
                catch (IOException)
                {
                    Disconnect();
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[PipeScreen] 通信失败: {ex.GetType().Name} {ex.Message}");
                    Disconnect();
                    return null;
                }
            }
        }

        /// <summary>
        /// 快捷查询屏幕状态
        /// </summary>
        public async Task<string> RequestScreenStateAsync()
        {
            var req = new PipeRequest { type = "screen_state" };
            var resp = await SendAsync(req);
            return resp?.result ?? "no_desktop";
        }

        public void Disconnect()
        {
            try { _pipe?.Close(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        public void Dispose() => Disconnect();
    }
}
