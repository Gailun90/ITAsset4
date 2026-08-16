using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ITAsset4.Common
{

    ///   - ReportAuditAsync: process_path 仅取 GetFileName(), args 限制长度 4096
    ///   - RegisterAsync: initial_token 优先用运行时 token
    ///   - 🔒 问题16 修复：ReportResultAsync 失败时本地持久化，下次启动重试
    
    public class ApiClient : IDisposable
    {
        private readonly AppConfig _cfg;
        private readonly HttpClient _http;
        private string _deviceSecret;
        private int? _clientId;

        private static readonly string ClientIdFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITAsset4", "client_id");
        
        // 🔒 问题16 修复：未上报结果持久化目录
        private static readonly string PendingResultsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITAsset4", "pending_results");

        public ApiClient(AppConfig cfg)
        {
            _cfg = cfg;
            _http = new HttpClient(BuildHandler()) { Timeout = TimeSpan.FromSeconds(30) };
            _deviceSecret = DeviceAuth.LoadDeviceSecret();
            LoadClientId();
            Directory.CreateDirectory(PendingResultsDir); // 确保目录存在
        }

        /// <summary>
        /// 构造 HttpClientHandler（最终形态·一：mTLS 客户端证书第二因子）。
        /// 仅当存在客户端证书(.pfx)时才附加；否则行为与旧版完全一致，不砖化。
        /// [server] tls_verify=off（仅测试）时关闭服务端证书校验，容忍自签 nginx 证书。
        /// </summary>
        private HttpClientHandler BuildHandler()
        {
            var handler = new HttpClientHandler();
            string certPath = ResolveClientCertPath();
            if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
            {
                try
                {
                    // 修复 K4：EphemeralKeySet 私钥仅驻留进程内存，不写磁盘，
                    // 进程退出即消失，彻底消除 PersistKeySet 导致的私钥落盘泄漏风险。
                    var cert = new X509Certificate2(certPath, "", X509KeyStorageFlags.EphemeralKeySet);
                    handler.ClientCertificates.Add(cert);
                    Logger.Info($"mTLS：已加载客户端证书 {certPath} (Subject={cert.Subject})");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"mTLS：客户端证书加载失败，将不使用客户端证书: {ex.Message}");
                }
            }
            if (_cfg.TlsVerifyOff)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (sender, cert, chain, sslPolicyErrors) => true;
                Logger.Warn("mTLS：tls_verify=off，已关闭服务端证书校验（仅测试用，生产请勿开启）");
            }
            return handler;
        }

        /// <summary>
        /// 解析客户端证书路径：config [server] client_cert_path 优先；否则默认 CommonAppData/ITAsset4/agent.pfx。
        /// 文件不存在则返回空（不附加证书）。
        /// </summary>
        private string ResolveClientCertPath()
        {
            string fromCfg = (_cfg.ClientCertPath ?? "").Trim();
            if (!string.IsNullOrEmpty(fromCfg))
                return fromCfg;
            string def = Path.Combine(_cfg.BaseDir, "agent.pfx");
            return File.Exists(def) ? def : "";
        }

        private void LoadClientId()
        {
            if (File.Exists(ClientIdFile) && int.TryParse(File.ReadAllText(ClientIdFile).Trim(), out int id))
                _clientId = id;
        }

        private void SaveClientId(int id)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClientIdFile));
            File.WriteAllText(ClientIdFile, id.ToString());
            _clientId = id;
        }

        public bool IsRegistered => _deviceSecret != null;
        public int? ClientId => _clientId;

        private static StringContent ToJson(object obj)
        {
            string json = JsonConvert.SerializeObject(obj);
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        private static T FromJson<T>(string json) => JsonConvert.DeserializeObject<T>(json);

        private void AddAgentAuthHeaders(HttpRequestMessage req, string serial)
        {
            string ts  = DeviceAuth.NowTimestamp();
            string sig = DeviceAuth.Sign(serial, ts, _deviceSecret);
            req.Headers.Add("X-Serial",    serial);
            req.Headers.Add("X-Timestamp", ts);
            req.Headers.Add("X-Signature", sig);
        }

        // ── POST /api/clients/register ───────────────────────────────────────
        public async Task<bool> RegisterAsync(string serial, string hostname, string ip,
            string biosSerial = null, string machineGuid = null)
        {
            try
            {
                string token = _cfg.InitialToken;
                var body = new {
                    hash_serial   = serial,
                    bios_serial   = biosSerial,
                    machine_guid  = machineGuid,
                    hostname, ip,
                    initial_token = token
                };
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/clients/register")
                {
                    Content = ToJson(body)
                })
                {
                    req.Headers.Add("X-Initial-Token", token);
                    var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    string respJson = await resp.Content.ReadAsStringAsync();
                    var reg = FromJson<RegisterResponse>(respJson);
                    if (reg == null || string.IsNullOrEmpty(reg.device_secret))
                        throw new Exception("服务端未返回 DeviceSecret");

                    DeviceAuth.SaveDeviceSecret(reg.device_secret);
                    _deviceSecret = reg.device_secret;
                    SaveClientId(reg.client_id);
                    Logger.Info($"注册成功: client_id={reg.client_id}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"注册失败: {ex.Message}");
                return false;
            }
        }

        // ── POST /api/clients/report ──────────────────────────────────────────
        public async Task<bool> ReportAsync(SystemInfo info)
        {
            if (_deviceSecret == null)
            {
                Logger.Warn("尚未注册，跳过上报");
                return false;
            }
            try
            {
                // 服务端字段名为 hash_serial；bios_serial/machine_guid 原始值随上报一起发出
                var reportBody = new {
                    hash_serial  = info.serial,
                    bios_serial  = info.bios_serial,
                    machine_guid = info.machine_guid,
                    hostname     = info.hostname,
                    ip           = info.ip,
                    os           = info.os,
                    cpu          = info.cpu,
                    memory_gb    = info.memory_gb,
                    disk_info    = info.disk_info,
                    current_user = info.current_user,
                    software     = info.software,
                    patches      = info.patches,
                    timestamp    = info.timestamp,
                    signature    = info.signature,
                };
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/clients/report")
                {
                    Content = ToJson(reportBody)
                })
                {
                    AddAgentAuthHeaders(req, info.serial);
                    var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    string respJson = await resp.Content.ReadAsStringAsync();
                    var result = FromJson<ReportResponse>(respJson);
                    if (result != null && result.client_id > 0) SaveClientId(result.client_id);
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"上报失败: {ex.Message}");
                return false;
            }
        }

        // ── GET /api/tasks ────────────────────────────────────────────────────
        public async Task<List<TaskInfo>> FetchTasksAsync(string serial)
        {
            if (_deviceSecret == null) return new List<TaskInfo>();
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, $"{_cfg.ServerUrl}/api/tasks"))
                {
                    AddAgentAuthHeaders(req, serial);
                    var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    string respJson = await resp.Content.ReadAsStringAsync();
                    return FromJson<List<TaskInfo>>(respJson) ?? new List<TaskInfo>();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"拉取任务失败: {ex.Message}");
                return new List<TaskInfo>();
            }
        }

        // ── POST /api/tasks/{target_id}/result ───────────────────────────────
        /// <summary>
        /// 🔒 问题16 修复：上报任务结果，失败时本地持久化
        /// </summary>
        public async Task ReportResultAsync(int targetId, TaskResult result, string serial)
        {
            if (_deviceSecret == null) return;
            
            // 先尝试上报
            try
            {
                var body = new
                {
                    success       = result.success,
                    exit_code     = result.exit_code,
                    message       = result.message,
                    reboot_action = result.reboot_action,
                    deferred      = result.deferred,
                    executor_version = result.executor_version,
                    verify_snapshot  = result.verify_snapshot,
                };
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{_cfg.ServerUrl}/api/tasks/{targetId}/result")
                {
                    Content = ToJson(body)
                })
                {
                    AddAgentAuthHeaders(req, serial);
                    await _http.SendAsync(req);
                }
                
                // 上报成功：删除可能的本地持久化文件
                DeletePendingResult(targetId);
                Logger.Info($"上报结果成功 [target={targetId}]: success={result.success}");
            }
            catch (Exception ex)
            {
                // 🔒 问题16 修复：上报失败，持久化到本地
                Logger.Error($"上报结果失败 [target={targetId}]: {ex.Message}, 将持久化到本地");
                SavePendingResult(targetId, result, serial);
            }
        }

        // 🔒 问题16 修复：持久化未上报的结果到本地文件
        private void SavePendingResult(int targetId, TaskResult result, string serial)
        {
            try
            {
                var pending = new PendingResult
                {
                    target_id   = targetId,
                    serial      = serial,
                    success     = result.success,
                    exit_code   = result.exit_code,
                    message     = result.message,
                    reboot_action = result.reboot_action,
                    deferred    = result.deferred,
                    executor_version = result.executor_version,
                    verify_snapshot  = result.verify_snapshot,
                    saved_at    = DateTime.Now,
                };
                string json = JsonConvert.SerializeObject(pending);
                string filePath = Path.Combine(PendingResultsDir, $"{targetId}.json");
                File.WriteAllText(filePath, json);
                Logger.Info($"结果已持久化 [target={targetId}]: {filePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"持久化结果失败 [target={targetId}]: {ex.Message}");
            }
        }

        // 🔒 问题16 修复：删除已上报的结果文件
        private void DeletePendingResult(int targetId)
        {
            try
            {
                string filePath = Path.Combine(PendingResultsDir, $"{targetId}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Logger.Info($"已删除持久化结果 [target={targetId}]");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"删除持久化结果失败 [target={targetId}]: {ex.Message}");
            }
        }

        // 🔒 问题16 修复：重试所有未上报的结果
        public async Task RetryPendingResults()
        {
            try
            {
                var files = Directory.GetFiles(PendingResultsDir, "*.json");
                if (files.Length == 0) return;

                Logger.Info($"发现 {files.Length} 个未上报结果，开始重试...");
                foreach (var file in files)
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var pending = JsonConvert.DeserializeObject<PendingResult>(json);
                        if (pending == null) continue;

                        // 重试上报
                        var result = new TaskResult
                        {
                            success      = pending.success,
                            exit_code    = pending.exit_code,
                            message      = pending.message,
                            reboot_action = pending.reboot_action,
                            deferred     = pending.deferred,
                            executor_version = pending.executor_version,
                            verify_snapshot  = pending.verify_snapshot,
                        };
                        
                        await ReportResultDirectAsync(pending.target_id, result, pending.serial);
                        File.Delete(file);
                        Logger.Info($"重试上报成功 [target={pending.target_id}]");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"重试上报失败 [{file}]: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"重试未上报结果异常: {ex.Message}");
            }
        }

        // 内部方法：直接上报（不持久化，避免递归）
        private async Task ReportResultDirectAsync(int targetId, TaskResult result, string serial)
        {
            var body = new
            {
                success       = result.success,
                exit_code     = result.exit_code,
                message       = result.message,
                reboot_action = result.reboot_action,
                deferred      = result.deferred,
                executor_version = result.executor_version,
                verify_snapshot  = result.verify_snapshot,
            };
            using (var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_cfg.ServerUrl}/api/tasks/{targetId}/result")
            {
                Content = ToJson(body)
            })
            {
                AddAgentAuthHeaders(req, serial);
                await _http.SendAsync(req);
            }
        }

        // ── POST /api/tasks/{target_id}/log ──────────────────────────────────
        public async Task UploadLogAsync(int targetId, string log, string serial)
        {
            if (_deviceSecret == null || string.IsNullOrEmpty(log)) return;
            try
            {
                int maxLen = 524000;
                var body = new { log = log.Length > maxLen ? log.Substring(0, maxLen) : log };
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{_cfg.ServerUrl}/api/tasks/{targetId}/log")
                {
                    Content = ToJson(body)
                })
                {
                    AddAgentAuthHeaders(req, serial);
                    await _http.SendAsync(req);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"上传日志失败 [target={targetId}]: {ex.Message}");
            }
        }

        // ── POST /api/audit/action ────────────────────────────────────────────
        public async Task ReportAuditAsync(string serial, string processPath,
            string args, int? pid, int? exitCode, DateTime executedAt)
        {
            if (_deviceSecret == null) return;
            try
            {
                // process_path 仅取文件名，防止路径注入
                string safePath = Path.GetFileName(processPath);
                if (!string.Equals(safePath, processPath, StringComparison.OrdinalIgnoreCase))
                    Logger.Warn($"[安全] audit process_path 被重写: {processPath} -> {safePath}");

                // args 长度限制 4096
                string safeArgs = args;
                if (!string.IsNullOrEmpty(safeArgs) && safeArgs.Length > 4096)
                {
                    Logger.Warn($"[安全] audit args 超长截断: {safeArgs.Length} -> 4096");
                    safeArgs = safeArgs.Substring(0, 4096);
                }

                var body = new
                {
                    serial,
                    process_path = safePath,
                    arguments    = safeArgs ?? "",
                    pid,
                    exit_code    = exitCode,
                    executed_at  = executedAt.ToString("o"),
                };
                using (var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/audit/action")
                {
                    Content = ToJson(body)
                })
                {
                    AddAgentAuthHeaders(req, serial);
                    await _http.SendAsync(req);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"审计上报失败: {ex.Message}");
            }
        }

        public void Dispose() => _http.Dispose();

        // ── 客户端自更新 ──────────────────────────────────────────
        public class ClientUpdateInfo
        {
            public bool   available { get; set; }
            public string version   { get; set; } = "";
            public string url       { get; set; } = "";
            public string hash      { get; set; } = "";   // SHA256 hex 小写
            public int?   size      { get; set; }
            public bool   mandatory { get; set; }
            public string notes     { get; set; } = "";
        }

        /// <summary>
        /// 查询服务端是否有新版本客户端（GET /api/client/update）
        /// </summary>
        public async Task<ClientUpdateInfo> GetClientUpdateAsync(string serial)
        {
            if (_deviceSecret == null) return new ClientUpdateInfo();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{_cfg.ServerUrl}/api/client/update");
                AddAgentAuthHeaders(req, serial);
                var resp = await _http.SendAsync(req);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync();
                var info = FromJson<ClientUpdateInfo>(json);
                return info ?? new ClientUpdateInfo();
            }
            catch (Exception ex)
            {
                Logger.Warn($"查询更新失败: {ex.Message}");
                return new ClientUpdateInfo();
            }
        }

        /// <summary>
        /// 下载文件到本地（流式写入），用于更新包
        /// </summary>
        public async Task<bool> DownloadFileAsync(string url, string destPath, string serial)
        {
            if (_deviceSecret == null) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddAgentAuthHeaders(req, serial);
                using var resp = await _http.SendAsync(req,
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                using var fs = new FileStream(destPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, false);
                using var rs = await resp.Content.ReadAsStreamAsync();
                await rs.CopyToAsync(fs);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"下载文件失败 [{url}]: {ex.Message}");
                return false;
            }
        }
    }

    // 🔒 问题16 修复：持久化的未上报结果模型
    public class PendingResult
    {
        public int target_id { get; set; }
        public string serial { get; set; } = default!;
        public bool success { get; set; }
        public int? exit_code { get; set; }
        public string message { get; set; } = default!;
        public string reboot_action { get; set; } = default!;
        public bool deferred { get; set; }
        public string executor_version { get; set; } = default!;
        public object verify_snapshot { get; set; } = default!;
        public DateTime saved_at { get; set; }
    }


}
