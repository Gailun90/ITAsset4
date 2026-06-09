using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    /// TcpScreenClient — 通过 TCP localhost:15900 请求截图/弹窗
    /// 短连接模式，方法签名与 PipeHelper.SendAsync 完全兼容
    /// </summary>
    public class TcpScreenClient : IDisposable
    {
        private const int PORT = 15900;
        private const int CONNECT_TIMEOUT_MS = 3000;
        private const int ASK_TIMEOUT_MS = 300_000;

        public static async Task<PipeResponse> SendAsync(PipeRequest request)
        {
            int timeoutMs = request.type switch
            {
                "remote_screen" => 5000,
                _               => ASK_TIMEOUT_MS,
            };

            var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync("127.0.0.1", PORT);
                    var connectTimeout = Task.Delay(CONNECT_TIMEOUT_MS, cts.Token);
                    var completed = await Task.WhenAny(connectTask, connectTimeout);
                    if (completed == connectTimeout)
                    {
                        Logger.Warn($"[TcpScreen] 连接超时 127.0.0.1:{PORT}");
                        return null;
                    }
                    await connectTask;

                    var stream = client.GetStream();
                    string reqJson = JsonConvert.SerializeObject(request);
                    await TcpFrameHelper.WriteFrameAsync(stream, reqJson, cts.Token);

                    string respJson = await TcpFrameHelper.ReadFrameAsync(stream, cts.Token);
                    if (string.IsNullOrEmpty(respJson))
                    {
                        Logger.Warn("[TcpScreen] 响应为空");
                        return null;
                    }

                    return JsonConvert.DeserializeObject<PipeResponse>(respJson);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"[TcpScreen] 请求超时 ({timeoutMs}ms): {request.type}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TcpScreen] 通信失败: {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        public void Dispose() { }
    }
}
