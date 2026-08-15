using System;

namespace ITAsset4.Common
{
    /// <summary>
    /// 自保护启动裁决（纯逻辑，可单测，net9 测试工程直接编译本文件）。
    ///
    /// 规则（§4.8 #3 方案 A：config 签名降级为告警）：
    ///   ① 二进制 Authenticode 失败 且 Enforce=true → 拒绝启动（真实威胁：二进制被替换/篡改）。
    ///   ② 配置签名（HMAC）失败 → 永远不拒绝启动，仅作篡改检测的告警信号。
    ///
    /// 为什么 config 失败不参与拒绝（关键不变式，已被单测+变异锁定）：
    ///   签名密钥硬编码在同一二进制内，防不住能替换二进制的攻击者；而硬性拒绝会因
    ///   「首次启动无 config.ini.sig / 运维改配置 / 密钥未注入」导致全部机器 brick（断联）。
    ///   故 config 签名只记日志、不放行拦截；硬拒仅留给 Authenticode（二进制完整性）。
    /// </summary>
    public static class SelfProtectDecision
    {
        public static bool ShouldRejectStart(bool authenticodeOk, bool configSigOk, bool enforce)
        {
            // 唯一拒绝条件：二进制被篡改 且 处于强制模式。
            if (!authenticodeOk && enforce) return true;

            // configSigOk 不参与拒绝裁决（仅告警）。无论 Enforce 与否，config 失败都不拦截。
            return false;
        }
    }
}
