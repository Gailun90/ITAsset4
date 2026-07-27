using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ITAsset4.Updater
{
    class Program
    {
        static string _logPath;
        static int Main(string[] args)
        {
            var opts = ParseArgs(args);
            opts.TryGetValue("manifest", out var manifestPath);
            manifestPath = manifestPath ?? "";

            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
            {
                Console.Error.WriteLine("Usage: ITAsset4.Updater --manifest <path-to-update-manifest.json>");
                return 1;
            }

            string svcName, zipPath, installDir;
            int timeout = 30;
            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonConvert.DeserializeAnonymousType(json,
                    new { service = "", extract = "", install_dir = "", timeout = 30, version = "" });
                if (manifest == null) throw new InvalidDataException("manifest JSON 为空");
                svcName = manifest.service;
                zipPath = manifest.extract;
                installDir = (manifest.install_dir ?? "").TrimEnd('\\');
                timeout = manifest.timeout;
            }
            catch (Exception ex)
            {
                _logPath = Path.Combine(Path.GetDirectoryName(manifestPath) ?? ".", "update.log");
                Log($"无法解析 manifest: {ex.Message}");
                return 1;
            }

            try { File.Delete(manifestPath); } catch { }

            string stagingDir = Path.GetDirectoryName(zipPath) ?? ".";
            _logPath = Path.Combine(stagingDir, "update.log");

            Log($"ITAsset4.Updater 启动: svc={svcName} zip={zipPath} install={installDir}");

            try
            {
                // a. Wait for calling process to exit
                Log("等待 3 秒让 Service 退出…");
                Thread.Sleep(3000);

                // b. Stop service
                StopService(svcName, timeout);

                // b2. 等 DLL 句柄释放（Windows 停服务后 DLL 不会立即卸载）
                Log("等待 5 秒让 DLL 句柄释放…");
                Thread.Sleep(5000);

                // c. Kill tray processes in all sessions
                KillTrayProcesses();

                // d. Extract zip
                string extractDir = Path.Combine(stagingDir, "extracted");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                Log($"解压完成: {extractDir}");

                // e. Backup current install dir (rename to .bak)
                string backupDir = installDir + ".bak";
                try
                {
                    if (Directory.Exists(backupDir))
                        Directory.Delete(backupDir, true);
                    if (Directory.Exists(installDir))
                    {
                        Directory.Move(installDir, backupDir);
                        Log($"备份原目录: {installDir} -> {backupDir}");
                    }
                }
                catch (Exception ex) { Log($"备份警告（不阻断）: {ex.Message}"); }

                // f. Robocopy (copy from extracted to install dir)
                bool ok = Robocopy(extractDir, installDir);
                if (!ok)
                {
                    Log("Robocopy 失败，尝试回滚…");
                    Rollback(installDir, backupDir);
                    StartServiceWithRetry(svcName, timeout, 3);
                    return 2;
                }

                // g. Start service (retry 3 times + verify + net start fallback)
                bool started = StartServiceWithRetry(svcName, timeout, 3);

                // h. If service won't start, rollback and retry
                if (!started)
                {
                    Log("服务启动失败，回滚到上一版本…");
                    StopService(svcName, timeout);
                    Rollback(installDir, backupDir);
                    Thread.Sleep(2000);
                    bool rollbackStarted = StartServiceWithRetry(svcName, timeout, 2);
                    Log(rollbackStarted ? "回滚后服务启动成功" : "回滚后服务仍无法启动！");
                    WriteStatusFile(stagingDir, rollbackStarted ? "rollback_ok" : "rollback_failed",
                        svcName, installDir);
                    return rollbackStarted ? 4 : 5;
                }

                // i. Verify service stays running for 5 seconds
                Thread.Sleep(5000);
                if (!IsServiceRunning(svcName))
                {
                    Log("服务启动后 5 秒内停止，尝试回滚…");
                    StopService(svcName, timeout);
                    Rollback(installDir, backupDir);
                    Thread.Sleep(2000);
                    StartServiceWithRetry(svcName, timeout, 2);
                    WriteStatusFile(stagingDir, "crash_after_start", svcName, installDir);
                    return 6;
                }

                // j. Cleanup
                try { Directory.Delete(extractDir, true); } catch { }
                try { File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
                WriteStatusFile(stagingDir, "ok", svcName, installDir);
                Log("Updater 完成，服务已启动并验证稳定");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"FATAL: {ex}");
                WriteStatusFile(stagingDir, "fatal", svcName, installDir);
                return 3;
            }
        }

        /// <summary>
        /// 重试启动服务：最多 maxRetry 次，每次间隔 5 秒。
        /// 先用 ServiceController，失败后用 net start 兜底。
        /// </summary>
        static bool StartServiceWithRetry(string name, int timeoutSec, int maxRetry)
        {
            for (int i = 1; i <= maxRetry; i++)
            {
                Log($"启动服务 {name}（第 {i}/{maxRetry} 次）…");
                if (StartService(name, timeoutSec))
                {
                    Log($"服务 {name} 第 {i} 次启动成功");
                    return true;
                }
                if (i < maxRetry)
                {
                    Log($"第 {i} 次失败，等待 5 秒后重试…");
                    Thread.Sleep(5000);
                }
            }

            // net start 兜底
            Log("ServiceController 启动失败，尝试 net start…");
            try
            {
                var psi = new ProcessStartInfo("net.exe", $"start \"{name}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p.WaitForExit(timeoutSec * 1000);
                string outText = p.StandardOutput.ReadToEnd();
                Log($"net start exit={p.ExitCode} out={outText.Substring(0, Math.Min(outText.Length, 200))}");
                if (p.ExitCode == 0 && IsServiceRunning(name)) return true;
            }
            catch (Exception ex) { Log($"net start 失败: {ex.Message}"); }

            // sc.exe 兜底
            Log("尝试 sc.exe start…");
            try
            {
                var psi = new ProcessStartInfo("sc.exe", $"start \"{name}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi);
                p.WaitForExit(timeoutSec * 1000);
                Thread.Sleep(3000);
                if (IsServiceRunning(name)) { Log("sc.exe 启动成功"); return true; }
            }
            catch (Exception ex) { Log($"sc.exe 失败: {ex.Message}"); }

            return false;
        }

        static bool StartService(string name, int timeoutSec)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    Log($"服务 {name} 已在运行");
                    return true;
                }
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(timeoutSec));
                Log($"服务 {name} 已启动 (status={sc.Status})");
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex) { Log($"启动服务失败: {ex.Message}"); return false; }
        }

        static bool IsServiceRunning(string name)
        {
            try
            {
                using var sc = new ServiceController(name);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        static void StopService(string name, int timeoutSec)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    Log($"服务 {name} 已停止");
                    return;
                }
                if (sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(timeoutSec));
                    Log($"服务 {name} 已停止 (status={sc.Status})");
                }
                else
                {
                    Log($"服务 {name} 无法停止 (CanStop=false)，尝试 taskkill…");
                    // 兜底：taskkill /F /IM 强杀服务进程
                    try
                    {
                        var psi = new ProcessStartInfo("taskkill.exe", $"/F /PID {GetServicePid(name)}")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true,
                        };
                        using var p = Process.Start(psi);
                        p.WaitForExit(10000);
                        Log($"taskkill exit={p.ExitCode}");
                        Thread.Sleep(2000);
                    }
                    catch (Exception ex) { Log($"taskkill 失败: {ex.Message}"); }
                }
            }
            catch (InvalidOperationException) { Log($"服务 {name} 不存在"); }
            catch (Exception ex) { Log($"停止服务失败: {ex.Message}"); }
        }

        static int GetServicePid(string name)
        {
            // 用 sc.exe queryex 获取 PID
            try
            {
                var psi = new ProcessStartInfo("sc.exe", $"queryex \"{name}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi);
                p.WaitForExit(10000);
                var output = p.StandardOutput.ReadToEnd();
                var match = System.Text.RegularExpressions.Regex.Match(output, @"PID\s*:\s*(\d+)");
                return match.Success ? int.Parse(match.Groups[1].Value) : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 回滚：把 .bak 目录恢复为安装目录。
        /// </summary>
        static void Rollback(string installDir, string backupDir)
        {
            try
            {
                if (!Directory.Exists(backupDir))
                {
                    Log("回滚失败：无备份目录");
                    return;
                }
                // 删除可能损坏的新版本
                if (Directory.Exists(installDir))
                    Directory.Delete(installDir, true);
                Directory.Move(backupDir, installDir);
                Log($"回滚完成: {backupDir} -> {installDir}");
            }
            catch (Exception ex) { Log($"回滚失败: {ex.Message}"); }
        }

        static bool Robocopy(string source, string dest)
        {
            try
            {
                // /XF 排除 Updater 自身及其变体（运行中不可覆盖）
                // 不重定向 stdout/stderr（避免 .NET 缓冲区死锁导致 robocopy 挂起）
                var psi = new ProcessStartInfo("robocopy.exe",
                    $"\"{source}\" \"{dest}\" /E /R:2 /W:1 /NFL /NDL " +
                    $"/XF ITAsset4.Updater.exe ITAsset4.Updater.exe.old ITAsset4.Updater.exe.bak ITAsset4.Updater.new.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                bool exited = proc.WaitForExit(60000);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    Log("Robocopy 超时（60s），已强制终止");
                    return false;
                }
                int rc = proc.ExitCode;
                bool ok = rc >= 0 && rc <= 7;
                Log($"Robocopy exit={rc} ok={ok}");
                return ok;
            }
            catch (Exception ex)
            {
                Log($"Robocopy 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 写状态文件供 Service 启动后读取并上报服务端。
        /// </summary>
        static void WriteStatusFile(string stagingDir, string status, string svcName, string installDir)
        {
            try
            {
                string statusFile = Path.Combine(installDir, "update_status.json");
                var obj = new
                {
                    status = status,
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    service = svcName,
                };
                File.WriteAllText(statusFile, JsonConvert.SerializeObject(obj), Encoding.UTF8);
                Log($"状态文件写入: {statusFile} status={status}");
            }
            catch (Exception ex) { Log($"状态文件写入失败: {ex.Message}"); }
        }

        static void KillTrayProcesses()
        {
            foreach (var proc in Process.GetProcessesByName("ITAsset4.Tray"))
            {
                try
                {
                    Log($"终止 Tray PID={proc.Id} Session={proc.SessionId}");
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
                catch (Exception ex) { Log($"终止 Tray PID={proc.Id} 失败: {ex.Message}"); }
                finally { proc.Dispose(); }
            }
        }

        static void Log(string msg)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
            Console.WriteLine(line);
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }

        static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    string key = args[i].Substring(2);
                    string val = "true";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        val = args[++i];
                    }
                    else if (key.Contains("="))
                    {
                        var parts = key.Split(new[] { '=' }, 2);
                        key = parts[0];
                        val = parts[1];
                    }
                    d[key] = val;
                }
            }
            return d;
        }
    }
}
