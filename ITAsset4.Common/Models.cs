using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ITAsset4.Common
{
    // ── 系统信息（上报给 FastAPI）────────────────────────────────────────────
    public class SystemInfo
    {
        public string serial { get; set; } = "";
        public string bios_serial { get; set; } = default!;       // 原始 BIOS 序列号
        public string machine_guid { get; set; } = default!;     // Windows MachineGuid
        public string hostname { get; set; } = "";
        public string ip { get; set; } = default!;
        public string os { get; set; } = default!;
        public string cpu { get; set; } = default!;
        public int? memory_gb { get; set; }
        public List<DiskInfo> disk_info { get; set; } = default!;
        public string current_user { get; set; } = default!;
        public List<SoftwareInfo> software { get; set; } = default!;
        public List<PatchInfo> patches { get; set; } = default!;
        public string timestamp { get; set; } = default!;
        public string signature { get; set; } = default!;
    }

    public class DiskInfo
    {
        public string model { get; set; } = default!;
        public int? size_gb { get; set; }
        public string type { get; set; } = default!;
    }

    public class SoftwareInfo
    {
        public string name { get; set; } = "";
        public string version { get; set; } = default!;
        public string publisher { get; set; } = default!;
        public string install_date { get; set; } = default!;
        public string install_dir { get; set; } = default!;
    }

    public class PatchInfo
    {
        public string hotfix_id { get; set; } = "";
        public string description { get; set; } = default!;
        public string installed_on { get; set; } = default!;
    }

    // ── 注册响应 ──────────────────────────────────────────────────────────────
    public class RegisterResponse
    {
        public string device_secret { get; set; } = "";
        public int client_id { get; set; }
        public string message { get; set; } = "";
    }

    // ── 上报响应 ──────────────────────────────────────────────────────────────
    public class ReportResponse
    {
        public int client_id { get; set; }
        public string status { get; set; } = "ok";
        public int jitter_seconds { get; set; }
    }

    // ── 任务 ──────────────────────────────────────────────────────────────────
    public class TaskInfo
    {
        public int target_id { get; set; }
        public int task_id { get; set; }
        public string task_name { get; set; } = "";
        public string task_type { get; set; } = "install";
        public string uninstall_target { get; set; } = default!;
        public string package_filename { get; set; } = "";
        public string package_hash { get; set; } = default!;
        public long? package_size { get; set; }
        public string silent_args { get; set; } = default!;
        public bool interactive { get; set; }
        public bool need_reboot { get; set; }
        public int timeout { get; set; } = 600;
        public List<int> success_codes { get; set; } = new List<int> { 0 };
        public int defer_count { get; set; }
        public int max_defer_count { get; set; } = 3;
        public bool silent_override { get; set; }
        public string prompt_text { get; set; } = default!;
        public int defer_minutes { get; set; } = 60;
        public string download_url { get; set; } = "";

        // ── 命令类任务（run_command / registry / cleanup）──────
        // run_command: 下发的脚本内容（bat / powershell / cmd）
        public string command { get; set; } = "";
        // 解释器：bat | cmd | powershell（默认按扩展名推断）
        public string interpreter { get; set; } = "";
        // registry: JSON 字符串，元素 {action:set|delete, root:HKLM|HKCU,
        //          subkey, name, value, type} 列表
        public string registry_ops { get; set; } = "";
        // cleanup: JSON 字符串，元素 {path, recursive:bool} 列表（要删除的文件/目录）
        public string cleanup_paths { get; set; } = "";
        // 部署前需要清理的进程名列表（由服务端下发，格式 ["wechat","wxwork"]（不含.exe））
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string process_fence { get; set; } = "";
        // 执行身份：system（默认，SYSTEM 权限）| user（当前登录用户）
        public string run_as { get; set; } = "system";
    }

    // ── 屏幕状态（远程桌面锁屏/登录界面检测）──────────────────
    // Tray 通过 type="screen_state" 的 PipeRequest 返回，result 为下列值之一：
    //   active      — 正常交互桌面（Default），可操作
    //   locked      — 锁屏 / 登录 / UAC 安全桌面（Winlogon），无法输入
    //   screensaver — 屏幕保护中
    //   no_desktop  — 无可交互桌面（如仅 Welcome 界面）
    public class ScreenStateMsg
    {
        public const string Active     = "active";
        public const string Locked     = "locked";
        public const string ScreenSaver = "screensaver";
        public const string NoDesktop  = "no_desktop";
    }

    // ── 任务结果 ──────────────────────────────────────────────────────────────
    public class TaskResult
    {
        public bool success { get; set; }
        public int? exit_code { get; set; }
        public string message { get; set; } = default!;
        public string reboot_action { get; set; } = "none";
        public bool deferred { get; set; }
        public string install_log { get; set; } = default!;
        // ── 状态机补全：Agent 版本 + 后校验快照 ──
        public string executor_version { get; set; } = default!;
        // registry_fix: 写入后读回的实际值，用于服务端后校验比对
        // 结构: { "before": {...}, "after": {...} } 或 null
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object verify_snapshot { get; set; } = default!;
    }

    // ── Named Pipe 消息协议 ───────────────────────────────────────────────────
    public class PipeRequest
    {
        public string type { get; set; } = "";
        // 通用字段（弹窗/通知）
        public string app_name { get; set; } = default!;
        public string description { get; set; } = default!;
        public string title { get; set; } = default!;
        public string message { get; set; } = default!;
        public int defer_count { get; set; }
        public int max_defer_count { get; set; }
        // 鼠标输入字段（归一化坐标 0..1）
        public double mouse_x { get; set; }
        public double mouse_y { get; set; }
        public string button { get; set; } = "left";   // left | right | middle
        public string event_type { get; set; } = "move"; // move | down | up | click | scroll
        public int scroll_delta { get; set; }            // 正数=向上，负数=向下
    }

    public class PipeResponse
    {
        public string result { get; set; } = "";
        //  raw JPEG 二进制（JsonIgnore，不参与 JSON 序列化，单独走 Pipe 第二帧）
        [Newtonsoft.Json.JsonIgnore]
        public byte[] rawJpeg { get; set; } = default!;
    }

    // ── 二进制帧消息（Service → FastAPI via WS binary）────────────────────
    public class BinaryFrameMsg
    {
        public int width { get; set; }
        public int height { get; set; }
    }

    // ── WS 鼠标事件消息（浏览器 → FastAPI → Agent）───────────────────────────
    public class RemoteInputMsg
    {
        public string type         { get; set; } = "remote_input";
        public string event_type   { get; set; } = "move";   // move | down | up | click | scroll
        public string button       { get; set; } = "left";   // left | right | middle
        public double mouse_x      { get; set; }             // 归一化 0..1
        public double mouse_y      { get; set; }             // 归一化 0..1
        public int    scroll_delta { get; set; }             // 正=向上 负=向下
    }
}
