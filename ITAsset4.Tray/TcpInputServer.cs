using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Tray
{
    /// <summary>
    /// TcpInputServer — 在 localhost:15901 接收鼠标输入
    /// 增强日志：连接/断开时间戳、输入计数、60s 自检
    /// </summary>
    public class TcpInputServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private const int PORT = 15901;

        // v5.1: 连接状态追踪
        private static int _connectedClients;
        private static DateTime _lastClientConnect = DateTime.MinValue;
        private static DateTime _lastInputTime = DateTime.MinValue;
        private static readonly object _statusLock = new object();

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(_cts.Token));
            // v5.1: 启动 60s 自检
            Task.Run(() => SelfCheckLoop(_cts.Token));
            Logger.Info($"[TcpInput] 已启动 127.0.0.1:{PORT} TraySession={System.Diagnostics.Process.GetCurrentProcess().SessionId}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            Logger.Info("[TcpInput] 已停止");
        }

        private async Task SelfCheckLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(60_000, ct); } catch { break; }
                if (ct.IsCancellationRequested) break;

                int active;
                DateTime lastInput;
                lock (_statusLock) { active = _connectedClients; lastInput = _lastInputTime; }

                string lastInputStr = lastInput == DateTime.MinValue
                    ? "无输入记录"
                    : $"{lastInput:HH:mm:ss.fff} ({(DateTime.Now - lastInput).TotalSeconds:F0}s前)";

                Logger.Info($"[TcpInput] SELFCHECK: 活跃={active} 最后输入={lastInputStr}");
            }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            _listener = new TcpListener(IPAddress.Loopback, PORT);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_statusLock)
                    {
                        _connectedClients++;
                        _lastClientConnect = DateTime.Now;
                    }
                    Logger.Info($"[TcpInput] 客户端已连接 (当前活跃={_connectedClients}, 时间={_lastClientConnect:HH:mm:ss})");
                    // v5.2: Desktop state snapshot at connection time
                    Logger.Info($"[TcpInput] CONNECT_INFO: TraySession={System.Diagnostics.Process.GetCurrentProcess().SessionId} AcceptThreadID={AppDomain.GetCurrentThreadId()} MgrThreadID={Thread.CurrentThread.ManagedThreadId} IsThreadPool={Thread.CurrentThread.IsThreadPoolThread} IsBg={Thread.CurrentThread.IsBackground}");

                    _ = Task.Run(() => ServeClientAsync(client, ct));
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Warn($"[TcpInput] accept err: {ex.Message}");
                    try { await Task.Delay(1000, ct); } catch { }
                }
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
        {
            var connectTime = DateTime.Now;
            int handledCount = 0;
            DateTime localLastInput = DateTime.MinValue;

            using (client)
            {
                var stream = client.GetStream();

                while (client.Connected && !ct.IsCancellationRequested)
                {
                    try
                    {
                        string json = await TcpFrameHelper.ReadFrameAsync(stream, ct);
                        if (string.IsNullOrEmpty(json)) break;

                        var pr = JsonConvert.DeserializeObject<PipeRequest>(json);
                        if (pr != null && pr.type == "remote_input")
                        {
                            string result = PipeServer.HandleMouseInputPublic(pr);
                            handledCount++;
                            localLastInput = DateTime.Now;
                            lock (_statusLock) { _lastInputTime = localLastInput; }

                            // v5.1: 非 move 事件记录（move 由 SendMouseInput 节流）
                            if (pr.event_type != "move")
                                Logger.Info($"[TcpInput] #{handledCount} {pr.event_type}({pr.button}) → {result}");
                        }
                    }
                    catch (IOException)
                    {
                        Logger.Info($"[TcpInput] IO 断开 (连接时长={(DateTime.Now-connectTime).TotalSeconds:F0}s, 处理了{handledCount}条, 最后输入={localLastInput:HH:mm:ss.fff})");
                        break;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (client.Connected)
                            Logger.Warn($"[TcpInput] read err: {ex.Message} (处理了{handledCount}条)");
                        break;
                    }
                }
            }

            lock (_statusLock)
            {
                _connectedClients = Math.Max(0, _connectedClients - 1);
            }
            Logger.Info($"[TcpInput] 客户端断开 (总处理={handledCount}条, 活跃连接={_connectedClients})");
        }
    }
}
