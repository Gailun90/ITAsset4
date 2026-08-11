namespace ITAsset4.Common
{
    /// <summary>
    /// 远程桌面（屏幕/输入）连接请求的结果。
    /// Success=false 时 Message 给出明确的可区分原因，便于前端精确报错：
    ///   - 无人登录 / 锁屏
    ///   - Tray 未运行（运维问题）
    ///   - 连接超时（真正的网络/管道异常）
    /// </summary>
    public class ScreenRequestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}
