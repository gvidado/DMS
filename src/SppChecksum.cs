using System;

namespace TelemetryMonitor
{
    // Fixed protocol rule: modulo-255 Fletcher check bytes at the end of the SPP.
    // Prefix bytes outside the SPP are excluded; both full-packet sums must be zero.
    public static class SppChecksum
    {
        public const string Mode = "fletcher16";
        private static void Bounds(byte[] data, int offset, int length)
        {
            if (data == null || offset < 0 || length < 2 || offset > data.Length - length)
                throw new ArgumentException("Fletcher-16校验范围越界");
        }
        public static bool Verify(byte[] data, int offset, int length)
        {
            Bounds(data, offset, length);
            int a, b;
            Sums(data, offset, offset + length, out a, out b);
            return a == 0 && b == 0;
        }
        public static void Write(byte[] data, int offset, int length)
        {
            Bounds(data, offset, length);
            int a, b, end = offset + length - 2;
            Sums(data, offset, end, out a, out b);
            int first = (255 - (a + b) % 255) % 255;
            data[end] = (byte)first; data[end + 1] = (byte)((255 - (a + first) % 255) % 255);
        }
        private static void Sums(byte[] data, int offset, int end, out int sumA, out int sumB)
        {
            int a = 0, b = 0;
            while (offset < end)
            {
                // Both sums fit in Int32 even when all 2048 bytes in the block are 255.
                int limit = offset + Math.Min(2048, end - offset);
                while (offset < limit)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= 255;
                b %= 255;
            }
            sumA = a;
            sumB = b;
        }
    }
}
