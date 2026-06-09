using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace ITAsset4.Common
{
    /// <summary>
    /// WebSocket 长连接客户端 
    /// - OnMessage 事件签名从 Action&lt;string,string&gt; 改为 Func&lt;string,string,Task&gt;
    ///   原 async (type,json) => ... 注册到 Action 上是 async void，
    ///   任何未 await 的异常会被静默吞掉，且 SendInputAsync 的 await 不被等待，
    ///   导致第二条消息到来时上一个还没发完就继续执行，锁竞争或静默丢弃。
    /// - HandleMessageAsync 改为 async，顺序 await 每个订阅者的 Task
    /// - ReceiveLoopAsync 中 await HandleMessageAsync，确保每条消息处理完再读下一条
    /// </summary>
    public class WsClient : IDisposable
    {
        private readonly AppConfig _cfg;
        private string _serial;
        private string _deviceSecret;

        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private CancellationTokenSource _sessionCts;
        private readonly ConcurrentQueue<string> _sendQueue = new ConcurrentQueue<string>();

        private volatile byte[] _latestFrame;
        private readonly SemaphoreSlim _sendLock   = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0, 1);
        private ClientWebSocket _currentWs;

        private Task _connectLoopTask;

        public event Action<string> OnTaskPush;

        // 改为 Func<Task>，让订阅者可以正确 await 异步操作
        // 原 Action<string,string> 导致 async lambda 变成 async void，异常被吞，await 被丢弃
        public event Func<string, string, Task> OnMessage;

        public WsClient(AppConfig cfg) { _cfg = cfg; }

        public async Task SendAsync(string message)
        {
            _sendQueue.Enqueue(message);
            if (_frameSignal.CurrentCount == 0)
                _frameSignal.Release();
        }

        public void SendBytesAsync(byte[] data)
        {
            if (data != null && data.Length > 0)
            {
                _latestFrame = data;
                if (_frameSignal.CurrentCount == 0)
                    _frameSignal.Release();
            }
        }

        public Task ConnectAsync(string serial, string deviceSecret)
        {
            _serial       = serial;
            _deviceSecret = deviceSecret;
            _connectLoopTask = ConnectLoopAsync(_lifetimeCts.Token);
            return _connectLoopTask;
        }

        // ── 主循环 ───────────────────────────────────────────────────────────
        private async Task ConnectLoopAsync(CancellationToken lifetimeCt)
        {
            int[] backoff = { 10, 10, 30, 30, 60, 60, 120 };
            int   attempt = 0;

            while (!lifetimeCt.IsCancellationRequested)
            {
                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCt);
                var sessionCt = _sessionCts.Token;

                ClientWebSocket ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(BuildWsUrl()), sessionCt);
                    _currentWs = ws;
                    Logger.Info($"WS 已连接: {_serial}");
                    attempt = 0;

                    string hello = await ReceiveOneAsync(ws, sessionCt);
                    if (hello != null) Logger.Info($"WS connected: {hello}");

                    var hbTask   = HeartbeatLoopAsync(ws, sessionCt);
                    var recvTask = ReceiveLoopAsync(ws, sessionCt);
                    var sendTask = SendLoopAsync(ws, sessionCt);

                    await Task.WhenAny(hbTask, recvTask, sendTask);
                    _sessionCts.Cancel();

                    await Task.WhenAll(
                        SafeWaitAsync(hbTask,   TimeSpan.FromSeconds(3)),
                        SafeWaitAsync(recvTask, TimeSpan.FromSeconds(3)),
                        SafeWaitAsync(sendTask, TimeSpan.FromSeconds(3))
                    );
                }
                catch (OperationCanceledException)
                {
                    if (lifetimeCt.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"WS 连接失败: {ex.Message}");
                }
                finally
                {
                    _currentWs = null;
                    try { ws?.Dispose(); } catch { }
                    try { _sessionCts?.Dispose(); } catch { }
                }

                if (lifetimeCt.IsCancellationRequested) break;

                int wait = backoff[Math.Min(attempt, backoff.Length - 1)];
                attempt++;
                Logger.Info($"WS 重连中... ({wait}s 后)");
                try { await Task.Delay(TimeSpan.FromSeconds(wait), lifetimeCt); }
                catch (OperationCanceledException) { break; }
            }

            Logger.Info("WS 连接循环退出");
        }

        private static async Task SafeWaitAsync(Task task, TimeSpan timeout)
        {
            try { await Task.WhenAny(task, Task.Delay(timeout)); }
            catch { }
        }

        // ── 心跳 ─────────────────────────────────────────────────────────────
        private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await Task.Delay(TimeSpan.FromSeconds(50), ct);
                    if (ws.State != WebSocketState.Open) break;
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { type = "heartbeat" }));
                    await _sendLock.WaitAsync(ct);
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    }
                    finally { _sendLock.Release(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn($"WS 心跳异常: {ex.Message}"); }
        }

        // ── 接收 ─────────────────────────────────────────────────────────────
        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[32768];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using (var ms = new System.IO.MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct); }
                            catch (OperationCanceledException) { return; }
                            catch (Exception ex) { Logger.Warn($"WS 接收异常: {ex.Message}"); return; }

                            if (result.MessageType == WebSocketMessageType.Close)
                            { Logger.Info("WS 服务端关闭"); return; }

                            ms.Write(buf, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            //  await 消息处理，确保上一条处理完再读下一条
                            // 对于 remote_input 场景：保证 down 发完再发 up，顺序不乱
                            await HandleMessageAsync(Encoding.UTF8.GetString(ms.ToArray()), ct);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn($"WS 接收循环异常: {ex.Message}"); }
        }

        //  async，顺序 await 每个订阅者
        private async Task HandleMessageAsync(string text, CancellationToken ct)
        {
            try
            {
                var msg = JObject.Parse(text);
                string type = msg["type"]?.ToString() ?? "";

                if (type == "task_push")
                {
                    string taskName = msg["task_name"]?.ToString();
                    Logger.Info($"WS 收到任务推送: {taskName}");
                    OnTaskPush?.Invoke(taskName);
                }

                //  Func<Task> 事件，逐个 await 每个订阅者
                if (OnMessage != null)
                {
                    foreach (Func<string, string, Task> handler in OnMessage.GetInvocationList())
                    {
                        try
                        {
                            await handler(type, text);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"[WsClient] OnMessage handler 异常 type={type}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"WS HandleMessage 解析异常: {ex.Message}");
            }
        }

        private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    bool sent = false;

                    await _sendLock.WaitAsync(ct);
                    try
                    {
                        byte[] frame = _latestFrame;
                        if (frame != null)
                        {
                            await ws.SendAsync(new ArraySegment<byte>(frame),
                                WebSocketMessageType.Binary, true, ct);
                            _latestFrame = null;
                            sent = true;
                        }
                        else if (_sendQueue.TryDequeue(out string msg))
                        {
                            var bytes = Encoding.UTF8.GetBytes(msg);
                            int total = bytes.Length, offset = 0;
                            while (offset < total && !ct.IsCancellationRequested)
                            {
                                int chunk = Math.Min(total - offset, 32768);
                                bool isLast = (offset + chunk >= total);
                                await ws.SendAsync(new ArraySegment<byte>(bytes, offset, chunk),
                                    WebSocketMessageType.Text, isLast, ct);
                                offset += chunk;
                            }
                            sent = true;
                        }
                    }
                    finally { _sendLock.Release(); }

                    if (!sent)
                    {
                        try { await _frameSignal.WaitAsync(100, ct); }
                        catch (OperationCanceledException) { break; }
                        catch (ObjectDisposedException)    { break; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn($"WS send loop err: {ex.Message}"); }
        }

        private static async Task<string> ReceiveOneAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16384];
            try
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                return r.MessageType == WebSocketMessageType.Text
                    ? Encoding.UTF8.GetString(buf, 0, r.Count) : null;
            }
            catch { return null; }
        }

        private string BuildWsUrl()
        {
            string ts  = DeviceAuth.NowTimestamp();
            string sig = DeviceAuth.Sign(_serial, ts, _deviceSecret);
            string wsBase = _cfg.ServerUrl
                .Replace("https://", "wss://")
                .Replace("http://",  "ws://");
            return $"{wsBase.TrimEnd('/')}/ws/agent/{_serial}?timestamp={ts}&signature={Uri.EscapeDataString(sig)}";
        }

        public void Dispose()
        {
            _lifetimeCts.Cancel();
            _sessionCts?.Cancel();
            try { _connectLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _sendLock?.Dispose();
            _lifetimeCts.Dispose();
            _sessionCts?.Dispose();
        }
    }
}
