using System;

namespace ITAsset4.Common
{
    /// <summary>
    /// 更新包应用策略（纯逻辑、可单测、无外部依赖）。
    ///
    /// 修复 §4.8 #2：旧 <c>UpdateChecker</c> 仅当 <c>info.hash</c> 非空才做 SHA256 校验，
    /// 空 hash 会直接跳过校验并 <c>ApplyUpdate</c>，等于"未校验即执行更新包"。
    /// 而 <c>Downloader</c> 对任务包已强制要求 hash（空则拒绝）。此处统一策略：
    /// 更新包同样必须携带非空 version 与 SHA256，否则拒绝应用。
    /// </summary>
    public static class UpdatePolicy
    {
        /// <summary>
        /// 校验一个待应用的更新包是否可被信任地应用。
        /// </summary>
        /// <param name="version">服务端下发的目标版本（非空才合法）。</param>
        /// <param name="hash">服务端下发的 SHA256（必须非空，否则无法校验完整性）。</param>
        /// <returns>
        /// <c>ok=true</c> 表示允许应用；<c>ok=false</c> 表示拒绝，<c>reason</c> 说明原因。
        /// </returns>
        public static (bool ok, string reason) ValidateUpdatePackage(string version, string hash)
        {
            if (string.IsNullOrWhiteSpace(version))
                return (false, "更新包未携带版本号，拒绝应用");

            if (string.IsNullOrWhiteSpace(hash))
                return (false, "更新包未提供 SHA256，拒绝应用未经完整性校验的更新包（请检查包注册流程是否漏算哈希）");

            return (true, "更新包版本与哈希齐备，允许应用");
        }
    }
}
