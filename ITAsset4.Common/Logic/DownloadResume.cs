using System;

namespace ITAsset4.Common
{
    /// <summary>
    /// 断点续传决策（纯逻辑、可单测）。
    /// 修复：原先 resume 只处理 416，若服务器对带 Range 的请求返回 200（忽略 Range 头），
    /// 会走 else 分支直接“追加”写，导致文件前半段重复、整文件损坏。
    /// 正确语义：
    ///   - 未请求 Range（无本地片段）→ 覆盖从 0 开始；
    ///   - 206 Partial Content      → 真正续传，追加写（并应校验 Content-Range 起点）；
    ///   - 416 / 200（服务器忽略 Range）→ 不能追加，必须覆盖从 0 重新开始。
    /// </summary>
    public static class DownloadResume
    {
        /// <summary>
        /// 计算续传动作。
        /// </summary>
        /// <param name="existingLength">本地已存在片段字节数（0 表示无片段）。</param>
        /// <param name="rangeRequested">本次是否带上了 Range 请求头。</param>
        /// <param name="httpStatusCode">服务器响应状态码（如 200 / 206 / 416）。</param>
        /// <returns>
        /// Item1 = 是否追加（true 追加 / false 覆盖）；Item2 = 写入起始偏移。
        /// </returns>
        public static (bool Append, long Offset) Decide(long existingLength, bool rangeRequested, int httpStatusCode)
        {
            // 没有本地片段 → 整文件覆盖
            if (!rangeRequested || existingLength <= 0)
                return (false, 0);

            // 真正的部分内容 → 追加
            if (httpStatusCode == 206)
                return (true, existingLength);

            // 416（无法满足 Range）或 200（服务器忽略 Range）→ 一律覆盖从 0 开始，禁止追加
            return (false, 0);
        }
    }
}
