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
    /// 
    /// v6.0: 支持 TCP 认证（连接后先发送 AUTH &lt;token&gt;）
    /// </summary>
    public class TcpScreenClient : IDisposable
    {
        private const int PORT = 15900;
        private const int CONNECT_TIMEOUT_MS = 3000;
        private const int ASK_TIMEOUT_MS = 300_000;

        // v6.0: TCP 认证 Token
        private readonly string _authToken;

        /// <summary>
        /// v6.0: 创建 TcpScreenClient 实例（支持认证）
        /// </summary>
        public TcpScreenClient(string authToken = null)
        {
            _authToken = authToken;
        }

        /// <summary>
        /// v6.0: 修改为实例方法，支持认证
        /// </summary>
        public async Task<PipeResponse> SendAsync(PipeRequest request)
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
                    
                    // v6.0: 认证（如果配置了 token）
                    if (!string.IsNullOrEmpty(_authToken))
                    {
                        string authMsg = $"AUTH {_authToken}\n";
                        byte[] authBytes = System.Text.Encoding.UTF8.GetBytes(authMsg);
                        await stream.WriteAsync(authBytes, 0, authBytes.Length, cts.Token);
                        
                        // 读取认证响应
                        string authResp = await TcpFrameHelper.ReadFrameAsync(stream, cts.Token);
                        if (string.IsNullOrEmpty(authResp) || !authResp.StartsWith("OK"))
                        {
                            Logger.Warn($"[TcpScreen] 认证失败: {authResp}");
                            return null;
                        }
                        Logger.Info("[TcpScreen] TCP 认证成功");
                    }

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
