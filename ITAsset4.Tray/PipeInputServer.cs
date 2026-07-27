using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Tray
{
    /// <summary>
    /// PipeInputServer — 通过命名管道 \\.\pipe\ITAsset4_{sessionId}_Input
    /// 接收鼠标输入，替换 TcpInputServer。
    /// 
    /// 协议: TcpFrameHelper 二进制帧
    /// 单连接模式，断开后自动重连。
    /// </summary>
    public class PipeInputServer
    {
        private NamedPipeServerStream _pipe;
        private CancellationTokenSource _cts;
        private readonly int _sessionId;

        public PipeInputServer()
        {
            _sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(_cts.Token));
            Logger.Info($"[PipeInput] 已启动 \\\\.\\pipe\\ITAsset4_{_sessionId}_Input");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _pipe?.Dispose(); } catch { }
            Logger.Info("[PipeInput] 已停止");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _pipe = new NamedPipeServerStream(
                        $"ITAsset4_{_sessionId}_Input",
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await _pipe.WaitForConnectionAsync(ct);
                    Logger.Info("[PipeInput] Service 已连接");
                    await ServeClientAsync(_pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.Warn($"[PipeInput] accept err: {ex.Message}"); }
                finally
                {
                    try { _pipe?.Dispose(); } catch { }
                    _pipe = null;
                }
                if (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, ct); } catch { break; }
                }
            }
        }

        private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            int handledCount = 0;
            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    try
                    {
                        string json = await TcpFrameHelper.ReadFrameAsync((Stream)pipe, ct);
                        if (string.IsNullOrEmpty(json)) break;

                        var pr = JsonConvert.DeserializeObject<PipeRequest>(json);
                        if (pr != null && pr.type == "remote_input")
                        {
                            string result = PipeServer.HandleMouseInputPublic(pr);
                            handledCount++;

                            if (pr.event_type != "move")
                                Logger.Info($"[PipeInput] #{handledCount} {pr.event_type}({pr.button}) → {result}");
                        }
                    }
                    catch (IOException) { break; }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[PipeInput] read err: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                    Logger.Warn($"[PipeInput] serve err: {ex.Message}");
            }
            finally
            {
                Logger.Info($"[PipeInput] Service 断开 (处理了{handledCount}条)");
            }
        }
    }
}
