using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ITAsset4.Common
{
    /// <summary>
    ///  自适应 FPS + 二进制 JPEG 帧（减少 33% 传输量）
    /// - 空闲时 5fps，收到鼠标输入时自动升到 10fps（持续 3s）
    /// - 优先走 rawJpeg 二进制，fallback 到 base64
    /// </summary>
    public class RemoteScreen
    {
        private readonly WsClient _ws;
        private readonly Func<PipeRequest, Task<PipeResponse>> _pipeSend;
        private CancellationTokenSource _cts = default!;
        private Task _loopTask = default!;
        private volatile bool _running;
        private int _frameSeq;

        public int Quality { get; set; } = 75;
        public int MaxWidth { get; set; } = 1920;

        // ★ v4.8: 自适应 FPS
        public int FpsIdle { get; set; } = 5;
        public int FpsActive { get; set; } = 10;
        private volatile int _activeUntilTick = 0;

        // 屏幕状态（锁屏/登录界面）检测
        private string _lastScreenState = "";
        private int _lastStateCheckTick = 0;

        public bool IsRunning => _running;
        public int StopTimeoutMs { get; set; } = 3000;

        public RemoteScreen(WsClient ws, Func<PipeRequest, Task<PipeResponse>> pipeSend)
        {
            _ws = ws;
            _pipeSend = pipeSend;
        }

        /// <summary>
        /// 收到鼠标输入时调用，接下来 3 秒升帧率
        /// </summary>
        public void NotifyInput()
        {
            _activeUntilTick = Environment.TickCount + 3000;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _frameSeq = 0;
            _activeUntilTick = 0;
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
            Logger.Info($"[Remote] STARTED: idle={FpsIdle}fps active={FpsActive}fps quality={Quality}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _cts?.Cancel();
            try { _loopTask?.Wait(StopTimeoutMs); } catch { }
            Logger.Info($"[Remote] STOPPED (frames={_frameSeq})");
        }

        /// <summary>
        /// 将屏幕状态映射为面向前端操作者的提示文案
        /// </summary>
        private static string ScreenStateMessage(string state)
        {
            switch (state)
            {
                case ScreenStateMsg.Locked:
                    return "客户端处于锁屏 / 登录 / UAC 安全桌面，无法远程输入，请让终端用户解锁后再操作";
                case ScreenStateMsg.ScreenSaver:
                    return "客户端处于屏幕保护界面，远程输入可能无效";
                case ScreenStateMsg.NoDesktop:
                    return "客户端当前无可交互桌面（如仅显示登录欢迎界面）";
                default:
                    return "";
            }
        }

        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                //  自适应间隔
                bool active = Environment.TickCount < _activeUntilTick;
                int fps = active ? FpsActive : FpsIdle;
                int interval = 1000 / fps;

                try
                {
                    var req = new PipeRequest
                    {
                        type = "remote_screen",
                        app_name = Quality.ToString(),
                        description = MaxWidth.ToString(),
                    };
                    var resp = await _pipeSend(req);

                    if (resp == null || string.IsNullOrEmpty(resp.result))
                    {
                        try { await Task.Delay(2000, ct); } catch { break; }
                        continue;
                    }

                    _frameSeq++;

                    //  优先走 rawJpeg 二进制，fallback 到 base64
                    if (resp.rawJpeg != null && resp.rawJpeg.Length > 0)
                    {
                        // 发二进制帧：先发文本头（尺寸），再发二进制
                        string[] parts = resp.result.Split('|');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            await _ws.SendAsync(JsonConvert.SerializeObject(new
                            {
                                type = "remote_frame_bin",
                                width = w,
                                height = h,
                            }));
                            _ws.SendBytesAsync(resp.rawJpeg);
                        }
                    }
                    else
                    {
                        // Fallback: base64（兼容旧 Tray）
                        string[] parts = resp.result.Split('|');
                        if (parts.Length >= 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            string b64 = parts[2];
                            await _ws.SendAsync(JsonConvert.SerializeObject(new
                            {
                                type = "remote_frame",
                                data = b64,
                                width = w,
                                height = h,
                                size = (int)(b64.Length * 0.75),
                            }));
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.Error($"[Remote] frame#{_frameSeq}: {ex.Message}"); }

                // ── 屏幕状态检测（约每秒一次）──
                // 检测客户端是否处于锁屏/登录/UAC 安全桌面，及时通知前端操作者
                if (Environment.TickCount - _lastStateCheckTick > 1000)
                {
                    _lastStateCheckTick = Environment.TickCount;
                    try
                    {
                        var sreq = new PipeRequest { type = "screen_state" };
                        var sresp = await _pipeSend(sreq);
                        if (sresp != null && !string.IsNullOrEmpty(sresp.result))
                        {
                            string st = sresp.result;
                            if (st != _lastScreenState)
                            {
                                _lastScreenState = st;
                                string msg = ScreenStateMessage(st);
                                await _ws.SendAsync(JsonConvert.SerializeObject(new
                                {
                                    type = "remote_screen_state",
                                    state = st,
                                    message = msg,
                                }));
                                Logger.Info($"[Remote] screen_state 变更: {st}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[Remote] screen_state 查询失败: {ex.Message}");
                    }
                }

                try { await Task.Delay(interval, ct); } catch { break; }
            }
        }
    }
}
