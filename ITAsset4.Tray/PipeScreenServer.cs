using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Tray
{
    /// <summary>
    /// PipeScreenServer — 通过命名管道 \\.\pipe\ITAsset4_{sessionId}_Screen
    /// 提供截图+弹窗服务，替换 TcpScreenServer。
    /// 
    /// 协议: TcpFrameHelper 二进制帧（兼容旧客户端）
    /// 单连接模式：一个 Service 连接，断开后自动重连。
    /// </summary>
    public class PipeScreenServer
    {
        private NamedPipeServerStream _pipe;
        private CancellationTokenSource _cts;
        private readonly int _sessionId;
        private const int MaxFrameRetries = 3;

        public PipeScreenServer()
        {
            _sessionId = Process.GetCurrentProcess().SessionId;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(_cts.Token));
            Logger.Info($"[PipeScreen] 已启动 \\\\.\\pipe\\ITAsset4_{_sessionId}_Screen");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _pipe?.Dispose(); } catch { }
            Logger.Info("[PipeScreen] 已停止");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _pipe = new NamedPipeServerStream(
                        $"ITAsset4_{_sessionId}_Screen",
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await _pipe.WaitForConnectionAsync(ct);
                    Logger.Info("[PipeScreen] Service 已连接");
                    await ServeClientAsync(_pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.Warn($"[PipeScreen] accept err: {ex.Message}"); }
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

                        var req = JsonConvert.DeserializeObject<PipeRequest>(json);
                        if (req == null) continue;

                        PipeResponse resp;

                        if (req.type == "remote_screen")
                            resp = CaptureScreen(req);
                        else if (req.type == "screen_state")
                            resp = new PipeResponse { result = PipeServer.GetScreenState() };
                        else
                            resp = await ProcessUiRequestAsync(req);

                        if (resp != null)
                        {
                            string respJson = JsonConvert.SerializeObject(resp);
                            await TcpFrameHelper.WriteFrameAsync((Stream)pipe, respJson, ct);
                        }

                        handledCount++;
                    }
                    catch (IOException) { break; }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[PipeScreen] handle err: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                    Logger.Warn($"[PipeScreen] serve err: {ex.Message}");
            }
            finally
            {
                Logger.Info($"[PipeScreen] Service 断开 (处理了{handledCount}条请求)");
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // 截图 — GDI CopyFromScreen → JPEG（与 TcpScreenServer 一致）
        // ═════════════════════════════════════════════════════════════════

        private static PipeResponse CaptureScreen(PipeRequest req)
        {
            try
            {
                int quality = 75, maxW = 1920;
                if (int.TryParse(req.app_name, out int q)) quality = Math.Max(10, Math.Min(100, q));
                if (int.TryParse(req.description, out int mw)) maxW = Math.Max(320, mw);

                var bounds = Screen.PrimaryScreen.Bounds;
                foreach (var s in Screen.AllScreens)
                    bounds = Rectangle.Union(bounds, s.Bounds);

                using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

                    Bitmap output = bmp;
                    if (maxW > 0 && bmp.Width > maxW)
                    {
                        int nh = (int)((double)bmp.Height / bmp.Width * maxW);
                        output = new Bitmap(bmp, maxW, nh);
                    }

                    using (var ms = new MemoryStream())
                    {
                        var jpegEncoder = GetJpegEncoder();
                        var ep = new EncoderParameters(1);
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                        int outW = output.Width, outH = output.Height;
                        output.Save(ms, jpegEncoder, ep);
                        if (output != bmp) output.Dispose();

                        byte[] jpegBytes = ms.ToArray();
                        string base64 = Convert.ToBase64String(jpegBytes);

                        return new PipeResponse
                        {
                            result  = $"{outW}|{outH}|{base64}",
                            rawJpeg = jpegBytes,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Screen capture err: {ex.Message}");
                return new PipeResponse { result = "" };
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        // ═════════════════════════════════════════════════════════════════
        // UI 弹窗
        // ═════════════════════════════════════════════════════════════════

        private static Task<PipeResponse> ProcessUiRequestAsync(PipeRequest req)
        {
            var tcs = new TaskCompletionSource<PipeResponse>();
            var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;

            Action uiAction = () =>
            {
                string result = req.type switch
                {
                    "ASK_INSTALL" => UserDialog.AskInstall(req.app_name ?? "app", req.defer_count, req.max_defer_count),
                    "ASK_REBOOT"  => UserDialog.AskReboot(req.app_name ?? "app"),
                    "NOTIFY"      => UserDialog.Notify(req.title ?? "", req.message ?? ""),
                    _             => "UNKNOWN",
                };
                tcs.TrySetResult(new PipeResponse { result = result });
            };

            if (form != null && form.InvokeRequired)
                form.BeginInvoke(uiAction);
            else
                uiAction();

            return tcs.Task;
        }
    }
}
