using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ITAsset4.Common
{
    /// <summary>
    /// 最终形态·一：Agent 自保护
    ///   1. VerifyAuthenticode —— 用 WinTrust 校验当前可执行文件的 Authenticode 签名
    ///      （确保 Agent 二进制未被替换/篡改）。可选 pin 发布者证书指纹。
    ///   2. Config 签名校验 —— 对 config.ini 做 HMAC-SHA256（发布时绑定的密钥），
    ///      与随包发布的 config.sig 比对，防止配置被篡改（如改为恶意服务器地址）。
    ///
    /// 注意：Authenticode 校验仅 Windows 有效；非 Windows（开发机 macOS/Linux）应跳过。
    /// </summary>
    public static class SelfProtection
    {
        // ── 发布时绑定到二进制的配置签名密钥（构建流水线注入，勿硬编码到源码仓库）──
        //    实际部署：CI 在构建时写入一个每版本不同的密钥，并生成 config.sig。
        //    这里给出占位，替换为真实发布密钥后重新生成 config.sig。
        //    ⚠️ 关键：静态字段初始化绝不能抛异常（否则类型初始化器 .cctor 抛
        //    FormatException，整个 SelfProtection 类型无法加载，连带着服务启动直接 FATAL）。
        //    故改为安全的延迟加载：占位串/非法 Base64 一律降级为空密钥（仅使 config
        //    签名校验恒为 false），由 Enforce 决定是否阻断启动，绝不让类型初始化崩溃。
        private static readonly byte[] ConfigSigningKey = LoadConfigSigningKey();

        private static byte[] LoadConfigSigningKey()
        {
            const string placeholder = "REPLACE_WITH_PER_RELEASE_BUILD_SECRET_BASE64==";
            if (string.IsNullOrWhiteSpace(placeholder) ||
                placeholder == "REPLACE_WITH_PER_RELEASE_BUILD_SECRET_BASE64==")
            {
                return new byte[0]; // 占位未替换：空密钥（config 校验恒 false，不阻断启动）
            }
            try
            {
                return Convert.FromBase64String(placeholder);
            }
            catch
            {
                return new byte[0]; // 非法 Base64 兜底：绝不抛异常
            }
        }

        /// <summary>
        /// 是否强制自保护：校验失败即拒绝启动（Program.cs 中据此 return 拒绝启动）。
        ///
        /// 安全默认（§4.8 #3）：
        ///   - Release 构建（SDK 默认不定义 DEBUG）→ 默认 true，即"出厂即强制自保护"；
        ///     此时目标机二进制必须具备有效 Authenticode 签名 + 随包 config.ini.sig，
        ///     否则校验失败会拒绝启动（失败即闭环，符合预期）。
        ///   - Debug / 本地未签名测试构建 → 默认 false（软告警、不阻断启动），
        ///     以便直接在测试机验证功能，不被签名缺失卡住。
        ///
        /// CI 发布必须：① 对二进制做 Authenticode 签名；② 注入真实 ConfigSigningKey
        /// 并生成 config.sig；③（可选）设置 ExpectedSignerThumbprint 指纹 pin。
        /// 三者齐备后 Release 的 Enforce=true 才不会误伤正常启动。
        /// </summary>
#if !DEBUG
        public static bool Enforce { get; set; } = true;
#else
        public static bool Enforce { get; set; } = false;
#endif

        // ── 可选：期望的发布者证书指纹（Authenticode 签名者 thumbprint，小写 hex 无冒号）──
        /// <summary>期望的签名者证书指纹（空=不 pin，仅验证签名有效）。</summary>
        public static string ExpectedSignerThumbprint { get; set; } = "";

        #region Authenticode (WinTrust)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;
            public IntPtr pSignatureSettings;
        }

        // 修复「无法封送处理 parameter #3: 无效的托管/非托管类型组合」：
        // 原代码用 [MarshalAs(LPStruct)] 传递嵌套 private struct，在部分 CLR 上会抛 MarshalDirectiveException，
        // 导致 VerifyAuthenticode 永远走 catch 分支、自保护签名校验形同虚设。
        // 改为对两个结构体都用 ref 传递（pinned pointer），彻底规避 LPStruct 的封送限制。
        [DllImport("wintrust.dll", SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            ref Guid pgActionID,
            ref WINTRUST_DATA pWVTData);

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("{00AAC56B-CD44-11d3-8A60-0000C0A8AD00}");

        /// <summary>校验文件 Authenticode 签名是否有效（WinTrust）。</summary>
        public static bool VerifyAuthenticode(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = filePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero,
                };
                var wtd = new WINTRUST_DATA
                {
                    cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = 2,        // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,     // WTD_CHOICE_FILE
                    pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO))),
                    dwStateAction = 0,      // WTD_STATEACTION_IGNORE
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = 0,
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero,
                };
                Marshal.StructureToPtr(fileInfo, wtd.pFile, false);
                try
                {
                    // static readonly Guid 不能作为 ref 实参，复制一份局部变量再传 ref
                    var actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                    int result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref wtd);
                    if (result != 0)
                    {
                        Logger.Warn($"[自保护] Authenticode 校验失败: 0x{result:X8}");
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(wtd.pFile);
                }

                // 可选：pin 发布者指纹
                if (!string.IsNullOrEmpty(ExpectedSignerThumbprint))
                {
                    string actual = GetSignerThumbprint(filePath);
                    if (!string.Equals(actual, ExpectedSignerThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Warn($"[自保护] 签名者指纹不匹配: 期望 {ExpectedSignerThumbprint}, 实际 {actual}");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[自保护] Authenticode 校验异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>取文件 Authenticode 签名者证书指纹（小写 hex，无冒号）。失败返回空。</summary>
        public static string GetSignerThumbprint(string filePath)
        {
            try
            {
#if NETFRAMEWORK
                using (var cert = new X509Certificate(filePath))
                {
                    return cert.GetCertHashString().ToLowerInvariant();
                }
#else
                using (var cert = X509Certificate.CreateFromSignedFile(filePath))
                {
                    return BitConverter.ToString(cert.GetCertHash()).Replace("-", "").ToLowerInvariant();
                }
#endif
            }
            catch (Exception ex)
            {
                Logger.Warn($"[自保护] 读取签名者指纹失败: {ex.Message}");
                return "";
            }
        }

        #endregion

        #region Config 签名校验 (HMAC)

        /// <summary>对文件内容计算 HMAC-SHA256 签名（小写 hex，无冒号）。</summary>
        public static string ComputeConfigSignature(string path)
        {
            using var hmac = new HMACSHA256(ConfigSigningKey);
            byte[] data = File.ReadAllBytes(path);
            return BitConverter.ToString(hmac.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>生成/写入 config.sig（发布阶段调用）。</summary>
        public static void WriteConfigSignature(string configPath)
        {
            string sig = ComputeConfigSignature(configPath);
            File.WriteAllText(configPath + ".sig", sig, Encoding.ASCII);
        }

        /// <summary>
        /// 校验 config 签名：读取 config.ini 同目录的 config.sig 比对。
        /// 缺 .sig 文件时返回 false（视为被篡改/未发布签名）。
        /// </summary>
        public static bool VerifyConfigSignature(string configPath)
        {
            string sigPath = configPath + ".sig";
            if (!File.Exists(configPath) || !File.Exists(sigPath)) return false;
            string expected = File.ReadAllText(sigPath, Encoding.ASCII).Trim();
            string actual = ComputeConfigSignature(configPath);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
