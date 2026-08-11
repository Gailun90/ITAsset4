using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using ITAsset4.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ITAsset4.Service
{
    /// <summary>
    /// Windows Service 主体（.NET Framework 4.8 ServiceBase）
    ///   - OnStart 线程启动增加 try/catch，防止 Thread.Start 崩溃导致 SCM 异常
    ///   - OnStop/OnShutdown 用 RequestAdditionalTime 向 SCM 申报超时，避免"停止失败"
    ///   - SessionManager 新增 OnTrayNeeded 事件，用户登录时立即通知主循环
    /// 
    /// v7.0: 使用命名管道替代 TCP（per-session，无需 auth token/端口管理）
    /// </summary>
    public class AgentService : ServiceBase
    {
        private static readonly Random _rand = new Random();
        private static readonly object _randLock = new object();

        private CancellationTokenSource _cts = default!;
        private Thread _workerThread = default!;
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);

        // ── DI ──
        private readonly IServiceProvider _services;
        private readonly ILogger<AgentService> _logger;

        private AppConfig        _cfg = default!;
        private ApiClient        _api = default!;
        private SystemCollector  _collector = default!;
        private TaskExecutor     _executor = default!;
        private WsClient         _wsClient = default!;
        private RemoteScreen     _remoteScreen = default!;
        private PipeScreenClient _screenClient = default!;
        private PipeInputClient   _inputClient = default!;
        private SessionManager   _sessionMgr = default!;
        private UpdateChecker     _updateChecker = default!;

        // 推迟中的任务：target_id → 到期时间
        private readonly Dictionary<int, DateTime> _deferred = new Dictionary<int, DateTime>();
        private readonly object _deferLock = new object();

        // C3：任务去重。WS 推送与 10 分钟轮询可能下发同一任务，重复执行会导致双重安装/卸载。
        // 窗口期内同一 task_id 仅执行一次（含正在执行中）。
        private readonly TaskDedup _taskDedup = new TaskDedup(TimeSpan.FromMinutes(15));

        // C3：限制任务执行并发度，避免一次拉取大量任务时无限并发打爆机器（线程安全、可降级）。
        private readonly SemaphoreSlim _taskConcurrency = new SemaphoreSlim(4, 4);

        private DateTime _lastReportDate = DateTime.MinValue;
        private DateTime _lastTaskPoll   = DateTime.MinValue;
        private readonly SemaphoreSlim _taskPushSignal = new SemaphoreSlim(0, 1);

        // SessionManager 触发立即检查 Tray（用户登录时唤醒主循环）
        private readonly SemaphoreSlim _trayCheckSignal = new SemaphoreSlim(0, 1);

        //  停止超时常量，统一管理
        private const int STOP_WAIT_MS = 15_000;

        public AgentService(IServiceProvider services)
        {
            ServiceName         = "ITAsset4Agent";
            CanStop             = true;
            CanPauseAndContinue = false;
            AutoLog             = true;

            _services = services;
            _logger   = services.GetRequiredService<ILogger<AgentService>>();
        }

        // ── ServiceBase 生命周期 ──────────────────────────────────────────────
        protected override void OnStart(string[] args)
        {
            try
            {
                _cts = new CancellationTokenSource();
                _stopped.Reset();
                _workerThread = new Thread(() => RunAsync(_cts.Token).GetAwaiter().GetResult())
                {
                    IsBackground = true,
                    Name         = "ITAsset4Worker"
                };
                _workerThread.Start();
            }
            catch (Exception ex)
            {
                Logger.Error($"[FATAL] OnStart 启动工作线程失败: {ex.Message}");
                Logger.Error($"[FATAL] 堆栈: {ex.StackTrace}");
                throw; // 让 SCM 知道启动失败
            }
        }

        protected override void OnStop()
        {
            Logger.Info("收到停止信号，开始优雅退出...");

            //  向 SCM 申报额外等待时间，防止 SCM 在等待期间判定"停止失败"
            // SCM 默认超时 ~20s，我们申报 STOP_WAIT_MS + 3s 缓冲
            RequestAdditionalTime(STOP_WAIT_MS + 3_000);

            _cts?.Cancel();               // 唤醒所有 await ct 的等待
            PipeHelper.CancelAll();       // 取消所有 Pipe 等待

            // 不在 OnStop 里单独 Dispose inputClient，统一在 RunAsync finally 里清理
            // 避免双重 Dispose 竞态（RunAsync finally 也会 Dispose）

            if (!_stopped.Wait(STOP_WAIT_MS))
                Logger.Warn($"OnStop {STOP_WAIT_MS / 1000}s 超时，强制退出");
        }

        protected override void OnShutdown()
        {
            Logger.Info("收到系统关机信号，优雅停止...");
            // 关机时 SCM 给的窗口更短，申报同样的额外时间
            RequestAdditionalTime(STOP_WAIT_MS + 3_000);
            _cts?.Cancel();
            PipeHelper.CancelAll();
            _stopped.Wait(STOP_WAIT_MS);
        }

        // ── 控制台调试模式入口 ────────────────────────────────────────────────
        public void StartConsole(string[] args) => OnStart(args);
        public void StopConsole()               => OnStop();
        public void WaitForStop()               => _stopped.Wait();

        // ── 主逻辑 ────────────────────────────────────────────────────────────
        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                Logger.Info("==== ITAsset4 Agent v1.0.0 启动 ====");

                _cfg = _services.GetRequiredService<AppConfig>();
                _api = _services.GetRequiredService<ApiClient>();
                _collector = new SystemCollector();
                // v6.1: 传入 UI 委托（用于 interactive/deferred/reboot 弹窗）
                // _screenClient 可能在 WS remote_start 之后才创建，所以用延迟委托
                // v6.2: 传入审计上报委托（修复 8B：ReportAuditAsync 死代码）
                _executor  = new TaskExecutor(
                    _cfg,
                    uiSender: req => SendToTrayAsync(req),
                    auditReporter: (path, args, pid, exitCode, at) => ReportAuditSafeAsync(path, args, pid, exitCode, at));
                _updateChecker = new UpdateChecker(_cfg, _api);

                //  启动 Session 管理器（用户登录时自动拉起 Tray）
                // 订阅事件，用户登录时立即唤醒主循环检查 Tray，不再等最长 1 分钟
                _sessionMgr = new SessionManager();
                _sessionMgr.OnTrayNeeded += () =>
                {
                    Logger.Info("[SessionMgr] 收到 OnTrayNeeded 信号，唤醒主循环立即检查 Tray");
                    if (_trayCheckSignal.CurrentCount == 0)
                        _trayCheckSignal.Release();
                };
                _sessionMgr.Start();

                // ── 注册（首次或重置后）：失败则每 5 分钟重试 ────────────────
                while (!_api.IsRegistered && !ct.IsCancellationRequested)
                {
                    var info = _collector.Collect();
                    Logger.Info("首次运行，开始注册设备...");
                    bool ok = await _api.RegisterAsync(info.serial, info.hostname, info.ip, info.bios_serial, info.machine_guid);
                    if (ok)
                    {
                        Logger.Info("注册成功！");
                        break;
                    }
                    Logger.Error("注册失败，5 分钟后重试...");
                    await DelayAsync(TimeSpan.FromMinutes(5), ct);
                }

                if (ct.IsCancellationRequested) return;

                // ── 初始化 PipeInputClient（持久输入连接）────────────────────
                _inputClient = new PipeInputClient();
                Logger.Info("PipeInputClient 已创建");

                // ── 注册成功，先建 WS（远程桌面立即可用），再上报 ─────
                {
                    var info = _collector.Collect();
                    string secret = DeviceAuth.LoadDeviceSecret();
                    if (secret != null)
                    {
                        _wsClient = new WsClient(_cfg);
                        _wsClient.OnTaskPush += async (taskName) =>
                        {
                            Logger.Info($"WS 推送触发立即拉取任务: {taskName}");
                            _taskPushSignal.Release();
                            try { await FetchAndRunTasksAsync(info.serial, ct); }
                            catch (Exception ex) { Logger.Error($"WS 触发拉取失败: {ex.Message}"); }
                        };

                        _wsClient.OnMessage += async (msgType, rawJson) =>
                        {
                            try
                            {
                                if (msgType == "remote_start")
                                {
                                    Logger.Info("[Remote] received remote_start, preparing pipe connection");

                                    _screenClient = new PipeScreenClient();
                                    if (_remoteScreen == null)
                                        _remoteScreen = new RemoteScreen(_wsClient, req => _screenClient.SendAsync(req));
                                }
                                else if (msgType == "viewer_connected")
                                {
                                    // 命名管道 per-session，无需 token 验证。
                                    // viewer 已连接，启动远程桌面截图。
                                    Logger.Info("[Remote] viewer_connected，启动远程桌面");
                                    if (_remoteScreen != null && !_remoteScreen.IsRunning)
                                    {
                                        ScreenRequestResult result = await TryStartRemoteScreenAsync();
                                        if (!result.Success)
                                        {
                                            Logger.Warn($"[Remote] {result.Message}");
                                            // 明确告知前端：不是超时，而是当前就不可用（锁屏/未登录/Tray未运行）
                                            await _wsClient.SendAsync(JsonConvert.SerializeObject(new
                                            {
                                                type = "remote_unavailable",
                                                message = result.Message,
                                            }));
                                        }
                                    }
                                }
                                else if (msgType == "remote_stop")
                                {
                                    Logger.Info("[Remote] received remote_stop command");
                                    _remoteScreen?.Stop();
                                    _screenClient?.Disconnect();
                                    _inputClient?.Disconnect();
                                }
                                else if (msgType == "remote_input")
                                {
                                    var rim = Newtonsoft.Json.JsonConvert.DeserializeObject<RemoteInputMsg>(rawJson);
                                    if (rim != null)
                                    {
                                        if (rim.event_type == "click") return;

                                        
                                        var pr = new PipeRequest
                                        {
                                            type         = "remote_input",
                                            event_type   = rim.event_type,
                                            button       = rim.button,
                                            mouse_x      = rim.mouse_x,
                                            mouse_y      = rim.mouse_y,
                                            scroll_delta = rim.scroll_delta,
                                        };

                                        // 通知 RemoteScreen 升帧率
                                        _remoteScreen?.NotifyInput();

                                        // 走专用输入 PipeClient（持久连接，不阻塞截图）
                                        await _inputClient.SendInputAsync(pr);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"[Remote] handler error: {ex}");
                            }
                        };

                        // WS fire-and-forget：不等握手，远程桌面立即可用
                        //  ContinueWith 捕获未观察的异常
                        _ = _wsClient.ConnectAsync(info.serial, secret).ContinueWith(t =>
                        {
                            if (t.IsFaulted && t.Exception != null)
                                Logger.Error($"WS 后台连接失败: {t.Exception.InnerException?.Message}");
                        }, TaskContinuationOptions.OnlyOnFaulted);

                        Logger.Info("WS 启动中（后台），开始防惊群延迟上报...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                int jitter;
                                lock (_randLock) { jitter = _rand.Next(0, _cfg.JitterMax); }
                                await Task.Delay(TimeSpan.FromSeconds(jitter));
                                await ReportAndFetchTasksAsync(info, _cts.Token);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"延迟上报失败: {ex.Message}");
                            }
                        });
                    }
                }

                // ── 主循环 ─────────────────────────────────────────────────────
                //  Delay 改为 30s（原 1 分钟），同时监听三路唤醒信号：
                //   1. 定时 30s（保底轮询）
                //   2. _taskPushSignal（WS 推送任务）
                //   3. _trayCheckSignal（SessionManager 检测到用户登录）
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.WhenAny(
                            Task.Delay(TimeSpan.FromSeconds(30), ct),
                            _taskPushSignal.WaitAsync(ct),
                            _trayCheckSignal.WaitAsync(ct)
                        ).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    if (ct.IsCancellationRequested) break;

                    try { await TickAsync(ct); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Logger.Error($"主循环异常: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Error($"Agent 异常退出: {ex.Message}");
                Logger.Error($"堆栈: {ex.StackTrace}");
            }
            finally
            {
                //  统一清理顺序
                // 1. 先停截图循环（避免它继续占用 PipeScreenClient）
                // 2. 再关屏幕客户端和输入客户端
                // 3. 再停 WS（截图/输入都停了才关 WS）
                // 4. 最后停 SessionMgr、Api
                Logger.Info("==== ITAsset4 Agent 开始清理 ====");
                try { _remoteScreen?.Stop(); } catch (Exception ex) { Logger.Warn($"RemoteScreen Stop 异常: {ex.Message}"); }
                try { _screenClient?.Dispose(); } catch (Exception ex) { Logger.Warn($"ScreenClient Dispose 异常: {ex.Message}"); }
                try { _inputClient?.Dispose(); } catch (Exception ex) { Logger.Warn($"InputClient Dispose 异常: {ex.Message}"); }
                try { PipeHelper.CancelAll(); } catch { }
                try { _sessionMgr?.Dispose(); } catch (Exception ex) { Logger.Warn($"SessionMgr Dispose 异常: {ex.Message}"); }
                try { _wsClient?.Dispose(); } catch (Exception ex) { Logger.Warn($"WsClient Dispose 异常: {ex.Message}"); }
                try { _api?.Dispose(); } catch (Exception ex) { Logger.Warn($"Api Dispose 异常: {ex.Message}"); }
                Logger.Info("==== ITAsset4 Agent 停止 ====");
                _stopped.Set();
            }
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            try { await Task.Delay(delay, ct); }
            catch (TaskCanceledException) { }
        }

        /// <summary>
        /// 延迟 UI 请求委托：通过 PipeScreenClient 发送 ASK_INSTALL/ASK_REBOOT 到 Tray
        /// _screenClient 可能在 remote_start 之后才创建，所以需要动态检查
        /// </summary>
        private async Task<PipeResponse> SendToTrayAsync(PipeRequest req)
        {
            // 优先走 Pipe Screen（如果已初始化）
            if (_screenClient != null)
            {
                var resp = await _screenClient.SendAsync(req);
                if (resp != null) return resp;
            }

            // Pipe Screen 不可用 → 尝试 Named Pipe（兼容 Tray 已启动但 WS 未连接的场景）
            Logger.Info($"[UI] Pipe ScreenClient 不可用，尝试 Pipe: {req.type}");
            return await PipeHelper.SendAsync(req);
        }

        /// <summary>
        /// 安全审计上报（修复 8B：TaskExecutor 执行进程后回调此方法）
        /// 内部调用 ApiClient.ReportAuditAsync，失败不抛异常
        /// </summary>
        private async Task ReportAuditSafeAsync(string processPath, string args,
            int? pid, int? exitCode, DateTime executedAt)
        {
            if (!_api.IsRegistered) return;
            try
            {
                string serial = _collector?.Collect()?.serial;
                if (string.IsNullOrEmpty(serial)) return;
                await _api.ReportAuditAsync(serial, processPath, args, pid, exitCode, executedAt);
                Logger.Info($"[审计] 已上报: {System.IO.Path.GetFileName(processPath)} exit={exitCode}");
            }
            catch (Exception ex)
            {
                // 审计上报失败不影响任务流程
                Logger.Warn($"[审计] 上报失败: {ex.Message}");
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            // 每次循环检查 Tray 是否在正确 Session 中运行
            _sessionMgr?.CheckAndLaunchTray();

            var now          = DateTime.Now;
            var targetReport = ParseTime(_cfg.ReportTime);

            // 每日资产上报（固定时间点，1 分钟窗口内触发一次）
            bool shouldReport = (now.TimeOfDay >= targetReport
                                  && now.TimeOfDay <= targetReport.Add(TimeSpan.FromMinutes(1)))
                                  && _lastReportDate.Date != now.Date;
            if (shouldReport)
            {
                _lastReportDate = now;
                Logger.Info("======== 每日资产上报 ========");
                var info = _collector.Collect();
                await _api.ReportAsync(info);
            }

            // 每 10 分钟拉取一次任务
            if ((now - _lastTaskPoll).TotalMinutes >= 10)
            {
                if (await IsServerReachableAsync(_cfg))
                {
                    _lastTaskPoll = now;
                    
                    // 🔒 问题16 修复：重试之前失败的上报
                    await _api.RetryPendingResults();
                    
                    // ⭐ 每10分钟上报软件清单（捕获非本系统安装的软件变更）
                    var info = _collector.Collect();
                    try
                    {
                        await _api.ReportAsync(info);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"定时上报软件清单失败: {ex.Message}");
                    }
                    
                    await FetchAndRunTasksAsync(info.serial, ct);

                    // 客户端自更新检查（每 10 分钟随任务轮询一起）
                    try { await _updateChecker.CheckAndApplyAsync(info.serial, ct); }
                    catch (Exception ex) { Logger.Error($"更新检查异常: {ex.Message}"); }
                }
                else
                {
                    Logger.Warn("服务器不可达，跳过本次任务拉取");
                }
            }

            await CheckDeferredAsync(ct);
        }

        private static async Task<bool> IsServerReachableAsync(AppConfig cfg)
        {
            try
            {
                var uri = new Uri(cfg.ServerUrl);
                string host = uri.Host;
                int    port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);

                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var task = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(task, Task.Delay(3000)) == task)
                    {
                        client.Close();
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        private static TimeSpan ParseTime(string time)
        {
            var parts = time.Split(':');
            int h = parts.Length >= 1 && int.TryParse(parts[0], out var hh) ? hh : 0;
            int m = parts.Length >= 2 && int.TryParse(parts[1], out var mm) ? mm : 0;
            return new TimeSpan(h, m, 0);
        }

        private async Task ReportAndFetchTasksAsync(SystemInfo info, CancellationToken ct)
        {
            await _api.ReportAsync(info);
            await FetchAndRunTasksAsync(info.serial, ct);
        }

        private async Task FetchAndRunTasksAsync(string serial, CancellationToken ct)
        {
            var tasks = await _api.FetchTasksAsync(serial);
            if (tasks.Count > 0) Logger.Info($"拉取到 {tasks.Count} 个任务");

            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) break;

                lock (_deferLock)
                {
                    if (_deferred.ContainsKey(task.target_id))
                    {
                        Logger.Info($"[任务 {task.target_id}] 推迟中，跳过");
                        continue;
                    }
                }

                // C3：任务去重——同一 task_id 在窗口期内（含正在执行）只跑一次，
                // 避免 WS 推送与定时轮询重复下发导致双重执行。
                int key = TaskKey(task);
                if (_taskDedup.TryAcquire(key))
                {
                    Logger.Info($"[任务 {task.target_id}] task_id={key} 去重命中，跳过重复执行");
                    continue;
                }

                // C3：限制并发度，避免无限并发。注意：RunTaskAsync 内部会据成败调用
                // _taskDedup.MarkCompleted/MarkFailed 维护去重状态。
                _ = Task.Run(async () =>
                {
                    await _taskConcurrency.WaitAsync(ct);
                    try { await RunTaskAsync(task, serial, ct); }
                    finally { _taskConcurrency.Release(); }
                }).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        Logger.Error($"[任务 {task.target_id}] 未处理异常: {t.Exception.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        /// <summary>
        /// 任务去重键：优先用 task_id（服务端唯一标识），缺失时退化为 target_id。
        /// </summary>
        private static int TaskKey(TaskInfo task) =>
            task.task_id > 0 ? task.task_id : task.target_id;

        private async Task RunTaskAsync(TaskInfo task, string serial, CancellationToken ct)
        {
            string deviceSecret = DeviceAuth.LoadDeviceSecret();
            if (deviceSecret == null)
            {
                Logger.Warn("DeviceSecret 未找到，跳过任务");
                return;
            }

            TaskResult result;
            try
            {
                result = await _executor.ExecuteAsync(task, serial, deviceSecret);
            }
            catch (Exception ex)
            {
                Logger.Error($"[任务 {task.target_id}] 执行异常: {ex.Message}");
                result = new TaskResult { success = false, message = ex.Message };
                // C3：失败的任务清除去重记录，允许服务端后续重新派发重试
                _taskDedup.MarkFailed(TaskKey(task));
                return;
            }
            // ── 附带 Agent 版本号 ──
            result.executor_version = ClientVersion.Current;

            if (result.deferred)
            {
                int deferMins = task.defer_minutes > 0 ? task.defer_minutes : 60;
                lock (_deferLock)
                    _deferred[task.target_id] = DateTime.Now.AddMinutes(deferMins);
                Logger.Info($"[任务 {task.target_id}] 已推迟，{deferMins} 分钟后重试");
                // 推迟的任务稍后会由 CheckDeferredAsync 重新拉取执行，这里清除去重记录以便其能再次运行
                // （注意：推迟窗口内仍由 _deferred 字典拦截，不会立即重跑）
                _taskDedup.MarkFailed(TaskKey(task));
            }
            else
            {
                await _api.ReportResultAsync(task.target_id, result, serial);

                if (!string.IsNullOrEmpty(result.install_log))
                    await _api.UploadLogAsync(task.target_id, result.install_log, serial);

                string auditPath = task.task_type == "uninstall"
                    ? (task.uninstall_target ?? "uninstall")
                    : (task.package_filename ?? "unknown");
                string auditArgs = task.task_type == "uninstall" ? "uninstall" : task.silent_args;
                await _api.ReportAuditAsync(serial, auditPath, auditArgs,
                    null, result.exit_code, DateTime.UtcNow);

                // ⭐ 任务完成后立即同步软件清单
                try
                {
                    var reportInfo = _collector.Collect();
                    await _api.ReportAsync(reportInfo);
                    Logger.Info($"[任务 {task.target_id}] 软件清单已同步上报");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[任务 {task.target_id}] 同步上报软件清单失败: {ex.Message}");
                }
            }

            // C3：任务已终态（成功/推迟/报告完成），刷新去重时间戳，窗口期内不再重复执行
            _taskDedup.MarkCompleted(TaskKey(task));
        }

        /// <summary>
        /// 远程桌面连接前的前置检查 + 连接。
        /// 使用 WtsSessionHelper 单一真相源（与 Tray 端完全一致），避免两端 session 错配导致的超时。
        /// 返回 ScreenRequestResult，Success=false 时 Message 明确区分三种不可用原因。
        /// </summary>
        private async Task<ScreenRequestResult> TryStartRemoteScreenAsync()
        {
            // 1) 当前是否存在"活跃且已解锁"的物理控制台会话
            if (!WtsSessionHelper.IsPhysicalDesktopActiveAndUnlocked(out int sid))
            {
                // 明确告诉调用方：不是超时，是当前就不可用
                return new ScreenRequestResult
                {
                    Success = false,
                    Message = "远程桌面不可用：当前无人登录或处于锁屏状态",
                };
            }

            // 2) 该 session 下是否存在 Tray 进程（管道服务端）
            var tray = Process.GetProcessesByName("ITAsset4.Tray")
                               .FirstOrDefault(p => p.SessionId == sid);
            if (tray == null)
            {
                Logger.Warn($"Session {sid} 处于活跃解锁状态，但未发现 Tray 进程，运维问题");
                return new ScreenRequestResult
                {
                    Success = false,
                    Message = "远程桌面不可用：Tray 未运行",
                };
            }

            // 3) 连接管道（屏幕 + 输入），3 秒超时
            try
            {
                if (_screenClient == null) _screenClient = new PipeScreenClient();
                await _screenClient.ConnectAsync(sid, TimeSpan.FromSeconds(3));
                await _inputClient.ConnectAsync(sid);
                _remoteScreen.Start();
                return new ScreenRequestResult { Success = true };
            }
            catch (System.TimeoutException)
            {
                return new ScreenRequestResult
                {
                    Success = false,
                    Message = "远程桌面不可用：连接超时",
                };
            }
        }

        private async Task CheckDeferredAsync(CancellationToken ct)
        {
            var ready = new List<int>();
            lock (_deferLock)
            {
                var now = DateTime.Now;
                foreach (var kv in _deferred)
                    if (now >= kv.Value) ready.Add(kv.Key);
                foreach (var id in ready) _deferred.Remove(id);
            }

            foreach (int targetId in ready)
            {
                Logger.Info($"推迟任务 target_id={targetId} 到期，重新拉取执行");
                var serial = new SystemCollector().Collect().serial;
                await FetchAndRunTasksAsync(serial, ct);
                break;
            }
        }
    }
}
