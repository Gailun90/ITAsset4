using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    ///  TcpInputClient
    /// - SendInputAsync 加发送超时（3s），防止 WriteAsync 永久阻塞吃掉锁
    /// - Heartbeat 改为独立 TcpClient，不再与发送共用同一把锁，彻底消除锁竞争
    /// - 增加详细诊断日志：锁等待耗时、发送耗时、连接状态
    /// 
    /// v6.0: 支持 TCP 认证（连接后先发送 AUTH &lt;token&gt;）
    /// </summary>
    public class TcpInputClient : IDisposable
    {
        private TcpClient _client = default!;
        private NetworkStream _stream = default!;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _hbCts = default!;
        private Task _hbTask = default!;
        private volatile bool _disposed;
        private const int PORT = 15901;
        private const int CONNECT_TIMEOUT_MS = 3000;
        private const int SEND_TIMEOUT_MS    = 3000;   

        private static DateTime _lastSendOk = DateTime.MinValue;
        private static readonly object _okLock = new object();

        // v6.0: TCP 认证 Token       
        private readonly string _authToken;
       
        private int _sendCount;
        private int _sendFail;

        /// <summary>
        /// v6.0: 创建 TcpInputClient（支持认证）
        /// </summary>
        public TcpInputClient(string authToken = null)
        {
            _authToken = authToken;
        }

        public async Task SendInputAsync(PipeRequest req)
        {
            if (_disposed) return;

            
            var t0 = DateTime.Now;
            bool lockAcquired = await _lock.WaitAsync(SEND_TIMEOUT_MS);
            var lockWait = (DateTime.Now - t0).TotalMilliseconds;

            if (!lockAcquired)
            {
                // 锁超时说明上一个发送卡住了，主动断开让下次重连
                Logger.Warn($"[TcpInput] 锁等待超时 {lockWait:F0}ms ({req?.event_type}/{req?.button})，主动断开重建连接");
                DropConnection();
                _sendFail++;
                return;
            }

            if (lockWait > 50)
                Logger.Warn($"[TcpInput] 锁等待慢 {lockWait:F0}ms ({req?.event_type}/{req?.button})");

            try
            {
                await EnsureConnectedAsync();

                string json = JsonConvert.SerializeObject(req);

                // v5.5: 发送带超时，防止 WriteAsync 永久阻塞
                using (var sendCts = new CancellationTokenSource(SEND_TIMEOUT_MS))
                {
                    var t1 = DateTime.Now;
                    await TcpFrameHelper.WriteFrameAsync(_stream, json, sendCts.Token);
                    var sendMs = (DateTime.Now - t1).TotalMilliseconds;

                    if (sendMs > 100 && req.event_type != "move")
                        Logger.Warn($"[TcpInput] 发送慢 {sendMs:F0}ms ({req.event_type}/{req.button})");
                }

                _sendCount++;
                lock (_okLock) { _lastSendOk = DateTime.Now; }
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"[TcpInput] 发送超时 {SEND_TIMEOUT_MS}ms ({req?.event_type}/{req?.button})，断开重建");
                DropConnection();
                _sendFail++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[TcpInput] send FAIL ({req?.event_type}/{req?.button}): {ex.GetType().Name} {ex.Message}");
                DropConnection();
                _sendFail++;
            }
            finally
            {
                _lock.Release();
            }
        }

        private void DropConnection()
        {
            try { _client?.Dispose(); } catch { }
            _stream = null;
            _client = null;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_client?.Connected == true && _stream != null) return;

            Logger.Info($"[TcpInput] 连接 127.0.0.1:{PORT} ... (已发={_sendCount} 失败={_sendFail})");
            _client = new TcpClient();

            using (var cts = new CancellationTokenSource(CONNECT_TIMEOUT_MS))
            {
                try
                {
                    await _client.ConnectAsync("127.0.0.1", PORT);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[TcpInput] 连接失败: {ex.Message}");
                    _client.Dispose();
                    _client = null;
                    throw;
                }
            }

            _stream = _client.GetStream();
            Logger.Info("[TcpInput] 已连接");
            
            // v6.0: 认证（如果配置了 token）
            if (!string.IsNullOrEmpty(_authToken))
            {
                string authMsg = $"AUTH {_authToken}\n";
                byte[] authBytes = Encoding.UTF8.GetBytes(authMsg);
                await _stream.WriteAsync(authBytes, 0, authBytes.Length);
                
                // 读取认证响应
                string authResp = await TcpFrameHelper.ReadFrameAsync(_stream, CancellationToken.None);
                if (string.IsNullOrEmpty(authResp) || !authResp.StartsWith("OK"))
                {
                    Logger.Warn($"[TcpInput] 认证失败: {authResp}");
                    DropConnection();
                    throw new Exception("TCP 认证失败");
                }
                Logger.Info("[TcpInput] TCP 认证成功");
            }

            // Heartbeat 用独立连接，不竞争发送锁
            StartHeartbeat();
        }

        // Heartbeat 独立 TcpClient，完全不碰 _lock/_stream/_client
        private void StartHeartbeat()
        {
            _hbCts?.Cancel();
            _hbCts = new CancellationTokenSource();
            _hbTask = Task.Run(() => HeartbeatLoopAsync(_hbCts.Token));
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try { await Task.Delay(30_000, ct); }
                catch { break; }

                if (_disposed || ct.IsCancellationRequested) break;

                //  独立连接探活，不影响发送链路
                bool alive = false;
                try
                {
                    using (var probe = new TcpClient())
                    {
                        var t = probe.ConnectAsync("127.0.0.1", PORT);
                        if (await Task.WhenAny(t, Task.Delay(2000, ct)) == t && probe.Connected)
                        {
                            // v6.0: 认证心跳连接
                            if (!string.IsNullOrEmpty(_authToken))
                            {
                                var probeStream = probe.GetStream();
                                string authMsg = $"AUTH {_authToken}\n";
                                byte[] authBytes = Encoding.UTF8.GetBytes(authMsg);
                                await probeStream.WriteAsync(authBytes, 0, authBytes.Length);
                                string authResp = await TcpFrameHelper.ReadFrameAsync(probeStream, ct);
                                if (string.IsNullOrEmpty(authResp) || !authResp.StartsWith("OK"))
                                {
                                    continue; // 认证失败，认为不 alive
                                }
                            }
                            alive = true;
                        }
                    }
                }
                catch { }

                if (!alive)
                {
                    Logger.Warn("[TcpInput] 心跳探活失败，主动断开等待重连");
                    // 只清空连接引用，下次 SendInputAsync 会重建
                    await _lock.WaitAsync(1000);
                    try { DropConnection(); }
                    finally { _lock.Release(); }
                }
                else
                {
                    Logger.Info($"[TcpInput] 心跳 OK (已发={_sendCount} 失败={_sendFail} 最后成功={_lastSendOk:HH:mm:ss.fff})");
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _hbCts?.Cancel();
            try { _hbTask?.Wait(500); } catch { }
            _hbCts?.Dispose();
            _client?.Dispose();
            _lock?.Dispose();
            Logger.Info($"[TcpInput] 已释放 (总发={_sendCount} 失败={_sendFail})");
        }
    }
}
