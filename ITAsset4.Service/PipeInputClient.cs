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
    /// PipeInputClient — 通过命名管道发送鼠标输入到 Tray
    /// 替换 TcpInputClient，长连接复用。
    /// 
    /// 使用帧协议: [4字节大端长度][JSON数据]
    /// </summary>
    public class PipeInputClient : IDisposable
    {
        private NamedPipeClientStream _pipe;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;
        private int _sessionId = -1;
        private const int CONNECT_TIMEOUT_MS = 3000;
        private const int SEND_TIMEOUT_MS = 3000;

        private int _sendCount;
        private int _sendFail;

        public bool IsConnected => _pipe?.IsConnected ?? false;

        public async Task ConnectAsync(int sessionId)
        {
            _sessionId = sessionId;
            string pipeName = $"ITAsset4_{sessionId}_Input";
            Logger.Info($"[PipeInputClient] 连接 \\\\.\\pipe\\{pipeName}");

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(CONNECT_TIMEOUT_MS);
            Logger.Info("[PipeInputClient] 已连接");
        }

        /// <summary>
        /// 发送输入请求（兼容 TcpInputClient.SendInputAsync）
        /// </summary>
        public async Task SendInputAsync(PipeRequest req)
        {
            if (_disposed) return;

            bool lockAcquired = await _lock.WaitAsync(SEND_TIMEOUT_MS);
            if (!lockAcquired)
            {
                Logger.Warn($"[PipeInput] 锁等待超时 ({req?.event_type}/{req?.button})，主动断开重建连接");
                DropConnection();
                _sendFail++;
                return;
            }

            try
            {
                await EnsureConnectedAsync();

                string json = JsonConvert.SerializeObject(req);
                var buf = System.Text.Encoding.UTF8.GetBytes(json);

                using (var sendCts = new CancellationTokenSource(SEND_TIMEOUT_MS))
                {
                    await TcpFrameHelper.WriteFrameAsync(_pipe, buf, sendCts.Token);
                }

                _sendCount++;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn($"[PipeInput] 发送超时 {SEND_TIMEOUT_MS}ms ({req?.event_type}/{req?.button})，断开重建");
                DropConnection();
                _sendFail++;
            }
            catch (IOException)
            {
                Logger.Warn($"[PipeInput] IO 异常 ({req?.event_type}/{req?.button})，断开重建");
                DropConnection();
                _sendFail++;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PipeInput] send FAIL ({req?.event_type}/{req?.button}): {ex.GetType().Name} {ex.Message}");
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
            try { _pipe?.Close(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        private async Task EnsureConnectedAsync()
        {
            if (_pipe?.IsConnected == true) return;

            Logger.Info($"[PipeInput] 重新连接... (已发={_sendCount} 失败={_sendFail})");
            _pipe?.Dispose();

            _pipe = new NamedPipeClientStream(".", $"ITAsset4_{_sessionId}_Input", PipeDirection.Out, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(CONNECT_TIMEOUT_MS);
            Logger.Info("[PipeInput] 已重新连接");
        }

        public void Disconnect()
        {
            try { _pipe?.Close(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _pipe?.Close(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _lock?.Dispose();
            Logger.Info($"[PipeInput] 已释放 (总发={_sendCount} 失败={_sendFail})");
        }
    }
}
