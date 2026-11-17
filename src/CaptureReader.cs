using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace TelemetryMonitor
{
    public sealed class CaptureFrame
    {
        public long Number, Section;
        public int LinkType, CapturedLength;
        public long FileOffset;
        public long TimestampTicks = -1;
        public long DataFileOffset, PayloadFileOffset;
        public int PayloadLength;
        public string Time = "", SourceIP = "", DestinationIP = "", Issue = "";
        public int SourcePort, DestinationPort;
        public bool Udp, Unsupported;
        public byte[] Payload;
    }
    public static class CaptureReader
    {
        private sealed class CaptureInterface { public int Link; public uint Snap; public double Resolution = 0.000001; public long Offset; }
        private static uint U32(byte[] b, int p, bool little)
        { return little ? (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24) : (uint)(b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]); }
        private static int U16(byte[] b, int p, bool little) { return little ? b[p] | b[p + 1] << 8 : b[p] << 8 | b[p + 1]; }
        private static byte[] Read(Stream stream, int length)
        {
            var bytes = new byte[length]; int done = 0, count;
            while (done < length && (count = stream.Read(bytes, done, length - done)) > 0) done += count;
            if (done != length) throw new ArgumentException("pcapng文件在字节" + stream.Position + "处截断");
            return bytes;
        }
        private static DateTime? Timestamp(uint hi, uint lo, CaptureInterface face)
        {
            try
            {
                decimal resolution = (decimal)face.Resolution; if (resolution == 0) return null;
                decimal ticks = (decimal)(((ulong)hi << 32) | lo) * resolution * 10000000m + (decimal)face.Offset * 10000000m;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(checked((long)Decimal.Round(ticks, 0, MidpointRounding.AwayFromZero)));
            }
            catch (ArgumentException) { return null; }
            catch (OverflowException) { return null; }
        }
        // Streaming reader; no Wireshark, Npcap driver, Office or online service needed at runtime.
        public static void ReadPcapng(string path, Action<CaptureFrame, long> consume, Func<bool> cancelled)
        {
            using (var file = CaptureCheckJob.OpenTemporaryRead(path))
                ReadPcapng(file, consume, cancelled);
        }
        public static void ReadPcapng(Stream file, Action<CaptureFrame, long> consume, Func<bool> cancelled)
        {
            file.Position = 0;
            {
                bool little = true, sectionSeen = false; long section = 0, frames = 0;
                var faces = new List<CaptureInterface>();
                while (file.Position < file.Length)
                {
                    if (cancelled()) return;
                    long start = file.Position; byte[] head = Read(file, 12);
                    bool shb = head[0] == 10 && head[1] == 13 && head[2] == 13 && head[3] == 10;
                    if (shb)
                    {
                        if (U32(head, 8, true) == 0x1A2B3C4D) little = true;
                        else if (U32(head, 8, false) == 0x1A2B3C4D) little = false;
                        else throw new ArgumentException("pcapng节头字节序标识无效，字节位置" + start);
                    }
                    else if (!sectionSeen) throw new ArgumentException("文件不是pcapng，请在Wireshark中导出为pcapng格式");
                    uint length = U32(head, 4, little), type = U32(head, 0, little);
                    if (length < (shb ? 28u : 12u) || length % 4 != 0 || length > 4 * 1024 * 1024 || start + length > file.Length)
                        throw new ArgumentException("pcapng块长度无效或文件截断，字节位置" + start);
                    var block = new byte[(int)length]; Array.Copy(head, block, 12);
                    byte[] tail = Read(file, (int)length - 12); Array.Copy(tail, 0, block, 12, tail.Length);
                    if (U32(block, (int)length - 4, little) != length) throw new ArgumentException("pcapng块首尾长度不一致，字节位置" + start);
                    if (shb)
                    {
                        if (U16(block, 12, little) != 1) throw new ArgumentException("不支持的pcapng主版本");
                        sectionSeen = true; section++; faces.Clear(); continue;
                    }
                    if (type == 1)
                    {
                        if (length < 20 || faces.Count >= 128) throw new ArgumentException("pcapng接口块损坏或每节接口数超过128");
                        var face = new CaptureInterface { Link = U16(block, 8, little), Snap = U32(block, 12, little) };
                        for (int p = 16; p < block.Length - 4; )
                        {
                            if (p + 4 > block.Length - 4) throw new ArgumentException("pcapng接口选项截断");
                            int code = U16(block, p, little), size = U16(block, p + 2, little); p += 4;
                            if (p + ((size + 3) & ~3) > block.Length - 4) throw new ArgumentException("pcapng接口选项长度越界");
                            if (code == 0) { if (size != 0) throw new ArgumentException("pcapng选项结束标志无效"); break; }
                            if (code == 9)
                            { if (size != 1) throw new ArgumentException("pcapng时间精度选项长度无效"); byte power = block[p]; face.Resolution = Math.Pow((power & 128) != 0 ? 2 : 10, -(power & 127)); }
                            if (code == 14)
                            { if (size != 8) throw new ArgumentException("pcapng时间偏移选项长度无效"); face.Offset = unchecked((long)(little ? ((ulong)U32(block, p + 4, true) << 32) | U32(block, p, true) : ((ulong)U32(block, p, false) << 32) | U32(block, p + 4, false))); }
                            p += (size + 3) & ~3;
                        }
                        faces.Add(face); continue;
                    }
                    if (type != 6 && type != 3 && type != 2) continue;
                    int dataAt, captured; uint original, faceId, hi = 0, lo = 0;
                    if (type == 3)
                    {
                        if (length < 16 || faces.Count == 0) throw new ArgumentException("pcapng简单报文块缺少接口或内容");
                        faceId = 0; original = U32(block, 8, little);
                        uint size = faces[0].Snap == 0 ? original : Math.Min(original, faces[0].Snap);
                        if (size > Int32.MaxValue || 16L + ((size + 3L) & ~3L) != length) throw new ArgumentException("pcapng简单报文块长度无效");
                        captured = (int)size; dataAt = 12;
                    }
                    else
                    {
                        if (length < 32) throw new ArgumentException("pcapng报文块过短");
                        faceId = type == 6 ? U32(block, 8, little) : (uint)U16(block, 8, little);
                        hi = U32(block, 12, little); lo = U32(block, 16, little);
                        uint size = U32(block, 20, little); original = U32(block, 24, little);
                        if (size > Int32.MaxValue || 32L + ((size + 3L) & ~3L) > length || size > original) throw new ArgumentException("pcapng报文数据长度无效");
                        captured = (int)size; dataAt = 28;
                    }
                    if (faceId >= faces.Count) throw new ArgumentException("pcapng报文引用不存在的接口");
                    DateTime? timestamp = type == 3 ? null : Timestamp(hi, lo, faces[(int)faceId]);
                    var frame = new CaptureFrame { Number = ++frames, Section = section, FileOffset = start, DataFileOffset = start + dataAt, LinkType = faces[(int)faceId].Link, CapturedLength = captured,
                        Time = timestamp.HasValue ? BeijingTime.Format(timestamp.Value,"yyyy-MM-dd HH:mm:ss.fffffff") : "", TimestampTicks = timestamp.HasValue ? timestamp.Value.Ticks : -1 };
                    byte[] packet = new byte[captured]; Array.Copy(block, dataAt, packet, 0, captured);
                    if ((uint)captured < original) frame.Issue = "抓包截断：保存的字节少于原报文长度";
                    else Extract(frame, packet);
                    consume(frame, file.Position);
                }
                if (!sectionSeen) throw new ArgumentException("pcapng文件为空或缺少节头");
            }
        }
        public static void ExtractUdp(CaptureFrame frame, byte[] b) { Extract(frame, b); }
        private static void Extract(CaptureFrame frame, byte[] b)
        {
            int p = 0, protocol = 0;
            switch (frame.LinkType)
            {
                case 1:
                    if (b.Length < 14) { frame.Issue = "以太网帧过短"; return; }
                    protocol = Protocol.U16(b, 12); p = 14;
                    for (int tags = 0; protocol == 0x8100 || protocol == 0x88A8 || protocol == 0x9100; tags++)
                    { if (tags >= 4 || b.Length - p < 4) { frame.Issue = "VLAN头截断或嵌套过多"; return; } protocol = Protocol.U16(b, p + 2); p += 4; }
                    break;
                case 101: if (b.Length > 0) protocol = (b[0] >> 4) == 4 ? 0x800 : 0x86dd; break;
                case 228: protocol = 0x800; break;
                case 0: case 108:
                    if (b.Length < 4) { frame.Issue = "回环头过短"; return; }
                    protocol = U32(b, 0, true) == 2 || U32(b, 0, false) == 2 ? 0x800 : 0x86dd; p = 4; break;
                case 113:
                    if (b.Length < 16) { frame.Issue = "Linux SLL头过短"; return; } protocol = Protocol.U16(b, 14); p = 16; break;
                case 276:
                    if (b.Length < 20) { frame.Issue = "Linux SLL2头过短"; return; } protocol = Protocol.U16(b, 0); p = 20; break;
                default: frame.Unsupported = true; frame.Issue = "暂不支持链路类型" + frame.LinkType; return;
            }
            if (protocol == 0x86dd) { frame.Unsupported = true; frame.Issue = "IPv6报文未检验（当前支持IPv4 UDP）"; return; }
            if (protocol != 0x800) return;
            if (b.Length - p < 20 || (b[p] >> 4) != 4) { frame.Issue = "IPv4头截断或版本无效"; return; }
            int header = (b[p] & 15) * 4, total = Protocol.U16(b, p + 2);
            if (header < 20 || total < header || total > b.Length - p) { frame.Issue = "IPv4长度无效或报文截断"; return; }
            frame.SourceIP = new IPAddress(new byte[] { b[p + 12], b[p + 13], b[p + 14], b[p + 15] }).ToString();
            frame.DestinationIP = new IPAddress(new byte[] { b[p + 16], b[p + 17], b[p + 18], b[p + 19] }).ToString();
            if ((Protocol.U16(b, p + 6) & 0x3fff) != 0) { frame.Unsupported = true; frame.Issue = "IP分片未重组，请先在Wireshark确认完整UDP报文"; return; }
            if (b[p + 9] != 17) return;
            frame.Udp = true; int udp = p + header;
            if (total - header < 8) { frame.Issue = "UDP头截断"; return; }
            frame.SourcePort = Protocol.U16(b, udp); frame.DestinationPort = Protocol.U16(b, udp + 2);
            int length = Protocol.U16(b, udp + 4);
            if (length < 8 || length != total - header) { frame.Issue = "UDP长度字段与IPv4数据长度不一致"; return; }
            frame.Payload = new byte[length - 8]; Array.Copy(b, udp + 8, frame.Payload, 0, length - 8);
            frame.PayloadFileOffset = frame.DataFileOffset + udp + 8; frame.PayloadLength = length - 8;
        }
    }
}
