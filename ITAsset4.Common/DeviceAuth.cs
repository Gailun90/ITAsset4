using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ITAsset4.Common
{
    /// <summary>
    ///   - DPAPI 改用 LocalMachine 范围。
    ///     原因：Service 以 SYSTEM 账户运行，而注册可能由不同账户触发，
    ///     CurrentUser 范围导致 SYSTEM 账户无法解密其他账户加密的数据。
    ///     LocalMachine 允许同一台机器上的所有账户访问，适合 Service 场景。
    /// </summary>
    public static class DeviceAuth
    {
        private const string SecretFile = "device_secret.dat";

        private static string SecretPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITAsset4", SecretFile);

        public static void SaveDeviceSecret(string secret)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SecretPath));
            byte[] plain = Encoding.UTF8.GetBytes(secret);
            // v4.3: LocalMachine — Service（SYSTEM）与安装时账户均可解密
            byte[] enc = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
            File.WriteAllBytes(SecretPath, enc);
            Logger.Info("DeviceSecret 已保存（DPAPI LocalMachine 加密）");
        }

        public static string LoadDeviceSecret()
        {
            if (!File.Exists(SecretPath)) return null;
            try
            {
                byte[] enc   = File.ReadAllBytes(SecretPath);
                byte[] plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                Logger.Error($"DeviceSecret 解密失败: {ex.Message}");
                return null;
            }
        }

        public static bool HasDeviceSecret() => File.Exists(SecretPath);

        public static void DeleteDeviceSecret()
        {
            if (File.Exists(SecretPath)) File.Delete(SecretPath);
        }

        /// <summary>生成一次性初始令牌（仅用于首次注册，不持久化存储）</summary>
        public static string GenerateInitialToken()
        {
            byte[] rand = new byte[24];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(rand);
            return Convert.ToBase64String(rand);
        }

        public static string Sign(string serial, string timestamp, string deviceSecret)
        {
            byte[] deviceSecretBytes = Encoding.UTF8.GetBytes(deviceSecret);
            byte[] hmacKey;
            using (var sha = SHA256.Create())
                hmacKey = sha.ComputeHash(deviceSecretBytes);

            string message = $"{timestamp}:{serial}";
            using (var hmac = new HMACSHA256(hmacKey))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string NowTimestamp() =>
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    }
}
