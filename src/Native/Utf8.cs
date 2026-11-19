using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CuRmlGUI.Native
{
    /// <summary>
    /// net472 没有 <c>Marshal.PtrToStringUTF8</c>（.NET Core 1.1+ 才有），
    /// 这里用最朴素的方式读取以 NUL 结尾的 UTF-8 字符串。
    /// 原生侧字符串都是短小的元素 id / 事件名，性能可忽略。
    /// </summary>
    internal static class Utf8
    {
        public static string FromPtr(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;

            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return string.Empty;

            var buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        /// <summary>把托管字符串转为以 NUL 结尾的 UTF-8 字节数组（可直接传给 const char*）。</summary>
        public static byte[] ToNullTerminated(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[] { 0 };
            var body = Encoding.UTF8.GetBytes(s);
            var buf = new byte[body.Length + 1];
            Buffer.BlockCopy(body, 0, buf, 0, body.Length);
            buf[body.Length] = 0;
            return buf;
        }
    }
}
