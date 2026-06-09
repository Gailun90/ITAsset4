using System;
using System.Collections.Generic;
using System.Management;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace ITAsset4.Common
{
    public class SystemCollector
    {
        public SystemInfo Collect()
        {
           
            var info = new SystemInfo
            {
                hostname     = Environment.MachineName,
                ip           = GetLocalIP(),
                serial       = GetBiosSerial(),
                bios_serial  = GetRawBiosSerial(),
                machine_guid = GetRawMachineGuid(),
                current_user = GetCurrentUser(),
                os           = GetOsName(),
                cpu          = GetCpu(),
                memory_gb    = GetMemoryGb(),
                disk_info    = GetDisks(),
                software     = GetSoftware(),
                patches      = GetPatches(),
            };
            
            return info;
        }

        private static string GetLocalIP()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    s.Connect("8.8.8.8", 80);
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        /// <summary>
        /// 组合 SHA256(BiosSerial + MachineGuid) 作为唯一设备标识
        /// - BiosSerial：硬件标识，物理机全局唯一，但 VM/OEM 可能相同或为空
        /// - MachineGuid：Windows 安装时生成，永不变更，弥补 Serial 不可靠问题
        /// - 两者组合 SHA256 → 32位十六进制，完全排除重复/空序列号场景
        /// </summary>
        private static string GetBiosSerial()
        {
            string biosSerial = "";
            string machineGuid = "";

            // 读取 BIOS Serial
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var v = o["SerialNumber"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(v)
                            && v != "To Be Filled By O.E.M."
                            && v != "Default string"
                            && v.Length > 3)
                            biosSerial = v;
                    }
                }
            }
            catch { }

            // 读取 MachineGuid
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    machineGuid = key?.GetValue("MachineGuid")?.ToString()?.Trim() ?? "";
            }
            catch { }

            // 两者都为空 → 回退到主机名（最后保险）
            if (string.IsNullOrEmpty(biosSerial) && string.IsNullOrEmpty(machineGuid))
                return "UNKNOWN-" + Environment.MachineName;

            // SHA256(biosSerial + ":" + machineGuid) → 前32位十六进制
            string raw = biosSerial + ":" + machineGuid;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 32).ToLower();
            }
        }

        private static string GetRawBiosSerial()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        var v = o["SerialNumber"]?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(v)
                            && v != "To Be Filled By O.E.M."
                            && v != "Default string"
                            && v.Length > 3)
                            return v;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string GetRawMachineGuid()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    return key?.GetValue("MachineGuid")?.ToString()?.Trim();
            }
            catch { return null; }
        }

        private static string GetCurrentUser()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                        return o["UserName"]?.ToString();
                }
            }
            catch { }
            return Environment.UserName;
        }

        private static string GetOsName()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Caption,Version FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject o in s.Get())
                        return $"{o["Caption"]} {o["Version"]}";
                }
            }
            catch { }
            return Environment.OSVersion.ToString();
        }

        private static string GetCpu()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject o in s.Get())
                        return o["Name"]?.ToString()?.Trim();
                }
            }
            catch { }
            return null;
        }

        private static int? GetMemoryGb()
        {
            try
            {
                long total = 0;
                using (var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject o in s.Get())
                        if (long.TryParse(o["Capacity"]?.ToString(), out long cap))
                            total += cap;
                }
                return total > 0 ? (int?)(total / 1024 / 1024 / 1024) : null;
            }
            catch { return null; }
        }

        private static List<DiskInfo> GetDisks()
        {
            var list = new List<DiskInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Model,Size,MediaType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        long.TryParse(o["Size"]?.ToString(), out long bytes);
                        string media = o["MediaType"]?.ToString();
                        string diskType = "Unknown";
                        if (media != null)
                        {
                            if (media.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0) diskType = "SSD";
                            else if (media.IndexOf("HDD", StringComparison.OrdinalIgnoreCase) >= 0) diskType = "HDD";
                        }
                        list.Add(new DiskInfo
                        {
                            model   = o["Model"]?.ToString()?.Trim(),
                            size_gb = bytes > 0 ? (int?)(bytes / 1024 / 1024 / 1024) : null,
                            type    = diskType,
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<SoftwareInfo> GetSoftware()
        {
            var list = new List<SoftwareInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ScanKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list, seen);
            ScanKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", list, seen);
            ScanKey(Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", list, seen);

           
            return list;
        }

        private static void ScanKey(RegistryKey root, string keyPath,
            List<SoftwareInfo> list, HashSet<string> seen)
        {
            try
            {
                using (var key = root?.OpenSubKey(keyPath))
                {
                    if (key == null) return;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (var sk = key.OpenSubKey(sub))
                        {
                            string name = sk?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                            list.Add(new SoftwareInfo
                            {
                                name         = name,
                                version      = sk?.GetValue("DisplayVersion") as string,
                                publisher    = sk?.GetValue("Publisher") as string,
                                install_date = sk?.GetValue("InstallDate") as string,
                                install_dir  = sk?.GetValue("InstallLocation") as string,
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private static List<PatchInfo> GetPatches()
        {
            var list = new List<PatchInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT HotFixID,Description,InstalledOn FROM Win32_QuickFixEngineering"))
                {
                    foreach (ManagementObject o in s.Get())
                    {
                        list.Add(new PatchInfo
                        {
                            hotfix_id    = o["HotFixID"]?.ToString() ?? "",
                            description  = o["Description"]?.ToString(),
                            installed_on = o["InstalledOn"]?.ToString(),
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"补丁采集失败: {ex.Message}");
            }
            return list;
        }
    }
}
