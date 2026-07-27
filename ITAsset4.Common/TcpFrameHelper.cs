using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ITAsset4.Common
{
    /// <summary>
    /// 长度前缀帧协议工具（Service 和 Tray 共用）
    /// 协议: [4字节大端长度][数据]
    /// 支持 NetworkStream 和任意 Stream（NamedPipe 等）
    /// </summary>
    public static class TcpFrameHelper
    {
        // ── NetworkStream 重载（兼容旧代码）──────────────────────────────

        /// <summary>
        /// 读一帧文本: [4字节大端长度][UTF-8数据]
        /// </summary>
        public static async Task<string> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            return await ReadFrameAsync((Stream)stream, ct);
        }

        /// <summary>
        /// 写一帧文本: [4字节大端长度][UTF-8数据]
        /// </summary>
        public static async Task WriteFrameAsync(NetworkStream stream, string text, CancellationToken ct)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            await WriteFrameBytesAsync(stream, data, ct);
        }

        // ── Stream 重载（NamedPipe / 通用流）─────────────────────────────

        /// <summary>
        /// 读一帧文本（Stream 版本）
        /// </summary>
        public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken ct)
        {
            byte[] lenBuf = new byte[4];
            int got = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (got < 4) return null;

            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBuf);
            int len = BitConverter.ToInt32(lenBuf, 0);

            if (len <= 0 || len > 50 * 1024 * 1024)
            {
                if (len == 0) return null;
                throw new IOException($"Invalid frame length: {len}");
            }

            byte[] data = new byte[len];
            got = await ReadExactAsync(stream, data, 0, len, ct);
            if (got < len) return null;
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// 写一帧文本（Stream 版本）
        /// </summary>
        public static async Task WriteFrameAsync(Stream stream, string text, CancellationToken ct)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            await WriteFrameBytesAsync(stream, data, ct);
        }

        /// <summary>
        /// 读一帧原始字节: [4字节大端长度][数据] → byte[]
        /// </summary>
        public static async Task<byte[]> ReadRawFrameAsync(Stream stream, CancellationToken ct)
        {
            byte[] lenBuf = new byte[4];
            int got = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (got < 4) return null;

            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBuf);
            int len = BitConverter.ToInt32(lenBuf, 0);

            if (len <= 0 || len > 50 * 1024 * 1024)
            {
                if (len == 0) return null;
                throw new IOException($"Invalid frame length: {len}");
            }

            byte[] data = new byte[len];
            got = await ReadExactAsync(stream, data, 0, len, ct);
            if (got < len) return null;
            return data;
        }

        /// <summary>
        /// 写一帧字节数组: [4字节大端长度][数据]
        /// </summary>
        public static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken ct)
        {
            await WriteFrameBytesAsync(stream, data, ct);
        }

        // ── 内部实现 ─────────────────────────────────────────────────────

        private static async Task WriteFrameBytesAsync(Stream stream, byte[] data, CancellationToken ct)
        {
            byte[] lenBuf = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBuf);

            await stream.WriteAsync(lenBuf, 0, 4, ct);
            await stream.WriteAsync(data, 0, data.Length, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>
        /// 精确读满 count 字节（NetworkStream 版本）
        /// </summary>
        public static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            return await ReadExactAsync((Stream)stream, buf, offset, count, ct);
        }

        /// <summary>
        /// 精确读满 count 字节（Stream 版本）
        /// </summary>
        public static async Task<int> ReadExactAsync(Stream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buf, offset + total, count - total, ct);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }
    }
}
