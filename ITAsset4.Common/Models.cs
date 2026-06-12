using System;
using System.Collections.Generic;

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
