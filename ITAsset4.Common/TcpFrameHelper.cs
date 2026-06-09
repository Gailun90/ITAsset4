using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ITAsset4.Common
{
    /// <summary>
    /// TCP 长度前缀帧协议工具（Service 和 Tray 共用）
    /// </summary>
    public static class TcpFrameHelper
    {
        /// <summary>
        /// 读一帧: [4字节大端长度][数据]
        /// </summary>
        public static async Task<string> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] lenBuf = new byte[4];
            int got = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (got < 4) return null;   // 连接正常关闭，不报错

            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBuf);
            int len = BitConverter.ToInt32(lenBuf, 0);

            if (len <= 0 || len > 50 * 1024 * 1024)
            {
                // len==0 通常是对端优雅关闭后残留数据，当作断开处理
                if (len == 0) return null;
                throw new IOException($"Invalid frame length: {len}");
            }

            byte[] data = new byte[len];
            got = await ReadExactAsync(stream, data, 0, len, ct);
            if (got < len) return null;  // 数据未读完，连接断开
            return Encoding.UTF8.GetString(data);
        }

        /// <summary>
        /// 写一帧: [4字节大端长度][数据]
        /// </summary>
        public static async Task WriteFrameAsync(NetworkStream stream, string text, CancellationToken ct)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] lenBuf = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBuf);

            await stream.WriteAsync(lenBuf, 0, 4, ct);
            await stream.WriteAsync(data, 0, data.Length, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>
        /// 精确读满 count 字节
        /// </summary>
        public static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
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
