using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ITAsset4.Common;
using Newtonsoft.Json;

namespace ITAsset4.Tray
{
    /// <summary>
    /// TcpScreenServer — 在 localhost:15900 提供截图+弹窗服务
    /// 协议: TcpFrameHelper (4字节大端长度前缀帧)
    /// 短连接模式，每次请求→响应后关闭
    /// </summary>
    public class TcpScreenServer
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private const int PORT = 15900;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => AcceptLoop(_cts.Token));
            Logger.Info($"[TcpScreen] 已启动 127.0.0.1:{PORT} TraySession={Process.GetCurrentProcess().SessionId}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
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
                    _ = Task.Run(() => HandleClientAsync(client, ct));
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.Warn($"[TcpScreen] accept err: {ex.Message}");
                    try { await Task.Delay(1000, ct); } catch { }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    string reqJson = await TcpFrameHelper.ReadFrameAsync(stream, ct);
                    if (string.IsNullOrEmpty(reqJson)) return;

                    var req = JsonConvert.DeserializeObject<PipeRequest>(reqJson);
                    if (req == null) return;

                    PipeResponse resp;

                    if (req.type == "remote_screen")
                        resp = CaptureScreen(req);
                    else
                        resp = await ProcessUiRequestAsync(req);

                    if (resp != null)
                    {
                        string respJson = JsonConvert.SerializeObject(resp);
                        await TcpFrameHelper.WriteFrameAsync(stream, respJson, ct);
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException))
                        Logger.Warn($"[TcpScreen] handle err: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 截图 — GDI CopyFromScreen → JPEG base64
        // ═══════════════════════════════════════════════════════════════════

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
                        // ★ 先记录尺寸再 Dispose，避免 Dispose 后访问属性
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

        // ═══════════════════════════════════════════════════════════════════
        // UI 弹窗
        // ═══════════════════════════════════════════════════════════════════

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
