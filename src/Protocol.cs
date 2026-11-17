using System;
using System.Collections.Generic;
using System.Globalization;

namespace TelemetryMonitor
{
    public sealed class Packet
    {
        public int Offset;
        public int Length;
        public int Apid;
        public int Sequence;
        public bool ChecksumVerified;
        public string Warning;
        public string PacketId;
        public string PacketName;
    }

    public static class Protocol
    {
        private static readonly string[] HexFormats = {
            "X1", "X2", "X3", "X4", "X5", "X6", "X7", "X8",
            "X9", "X10", "X11", "X12", "X13", "X14", "X15", "X16"
        };

        public static int U16(byte[] data, int at) { return (data[at] << 8) | data[at + 1]; }

        public static Packet Parse(byte[] data, Settings cfg)
        {
            if (cfg.ExpectedDatagramLength > 0 && data.Length != cfg.ExpectedDatagramLength)
                throw new ArgumentException("UDP长度不符：收到" + data.Length + "，配置为" + cfg.ExpectedDatagramLength);
            int offset = cfg.SppOffset;
            if (offset == -1)
            {
                var definitions = new List<PacketDefinition>();
                foreach (int a in cfg.AllowedApids) definitions.Add(new PacketDefinition { Name = "APID " + a, Apid = a, SppOffset = -1, SourceAddress = cfg.SourceAddress, ServiceType = cfg.ServiceType, ServiceSubtype = cfg.ServiceSubtype });
                PacketRoute route = PacketCodec.Resolve(data, null, definitions);
                if (route.Definition == null) throw new ArgumentException(route.Reason);
                offset = route.Offset;
            }
            if (offset < 0 || data.Length - offset < 18) throw new ArgumentException("SPP包过短");
            int header = U16(data, offset);
            int apid = header & 2047;
            if ((header & 0xF800) != 0x0800 || Array.IndexOf(cfg.AllowedApids, apid) < 0) throw new ArgumentException("SPP包标识或APID不匹配");
            if ((U16(data, offset + 2) >> 14) != 3) throw new ArgumentException("暂不支持SPP分段包");
            int declared = U16(data, offset + 4) + 7;
            bool mismatch = declared != data.Length - offset;
            if (mismatch && cfg.StrictLength) throw new ArgumentException("SPP长度字段不符：声明" + declared + "，实际" + (data.Length - offset));
            string routingDifference = PacketCodec.RoutingDifference(data, offset, new PacketDefinition { SourceAddress = cfg.SourceAddress, ServiceType = cfg.ServiceType, ServiceSubtype = cfg.ServiceSubtype });
            if (!String.IsNullOrEmpty(routingDifference)) throw new ArgumentException(routingDifference);
            if (!SppChecksum.Verify(data, offset, data.Length - offset)) throw new ArgumentException("Fletcher-16累加和校验失败");
            return new Packet { Offset = offset, Length = data.Length - offset, Apid = apid, Sequence = U16(data, offset + 2) & 16383, ChecksumVerified = true,
                Warning = mismatch ? "SPP长度不一致，仅诊断接收" : "" };
        }

        public static bool InProfile(Parameter p, string profile)
        {
            if (p.Group == "工仪全程") return true;
            if (profile == "IMU标定") return p.Group == "工仪IMU标定";
            if (profile == "全程") return false;
            return p.Group == "工仪RVD共用" || p.Group == "工仪" + profile;
        }

        public static Reading Decode(byte[] data, Packet packet, Parameter p, Settings cfg, DateTime now)
        {
            Reading result = new Reading { Id = p.Id, Status = "有效", Time = now, Sequence = packet.Sequence };
            try
            {
                if (!p.Supported) throw new ArgumentException(p.Diagnostic ?? "不支持的参数定义");
                int at = packet.Offset + cfg.PayloadOffset + p.Offset;
                if (at < packet.Offset || p.Length < 1 || p.Length > 8 || at + p.Length > data.Length - 2) throw new ArgumentException("参数越界");
                bool littleEndian = cfg.ByteOrder == "little";
                ulong raw = 0;
                for (int i = 0; i < p.Length; i++) raw = (raw << 8) | data[at + (littleEndian ? p.Length - 1 - i : i)];
                if (p.BitStart >= 0)
                {
                    if (p.BitCount < 1 || p.BitCount > 64) throw new ArgumentException("位字段定义无效");
                    if (p.Length > 1)
                    {
                        if (cfg.CrossByteOrder == "unverified") throw new ArgumentException("跨字节位顺序待核实");
                        raw = 0;
                        for (int bit = 0; bit < p.BitCount; bit++)
                        {
                            int ordinal = p.BitStart + bit, shift = cfg.BitOrder == "lsb0" ? ordinal % 8 : 7 - ordinal % 8;
                            ulong valueBit = (ulong)((data[at + ordinal / 8] >> shift) & 1);
                            if (cfg.CrossByteOrder == "firstLsb") raw |= valueBit << bit;
                            else raw = (raw << 1) | valueBit;
                        }
                    }
                    else
                    {
                        int shift = cfg.BitOrder == "lsb0" ? p.BitStart : 8 - p.BitStart - p.BitCount;
                        if (shift < 0) throw new ArgumentException("位字段定义无效");
                        raw = ((ulong)data[at] >> shift) & ((1UL << p.BitCount) - 1);
                    }
                }
                int sourceBits = p.BitStart >= 0 ? p.BitCount : p.Length * 8;
                result.HexSource = "0x" + raw.ToString(HexFormats[(sourceBits - 1) / 4], CultureInfo.InvariantCulture);
                result.Raw = raw.ToString(CultureInfo.InvariantCulture);
                long signed = 0;
                ulong unsigned = raw;
                double value;
                if (p.Type == "float32" || p.Type == "float64")
                {
                    byte[] bytes = new byte[p.Length]; Array.Copy(data, at, bytes, 0, p.Length);
                    if (BitConverter.IsLittleEndian == (cfg.ByteOrder == "big")) Array.Reverse(bytes);
                    value = p.Type == "float32" ? BitConverter.ToSingle(bytes, 0) : BitConverter.ToDouble(bytes, 0);
                }
                else if (p.Type == "int")
                {
                    int bits = p.Length * 8;
                    signed = unchecked((long)raw);
                    if (bits < 64 && (raw & (1UL << (bits - 1))) != 0) signed -= (1L << bits);
                    value = signed;
                    result.Raw = signed.ToString(CultureInfo.InvariantCulture);
                }
                else if (p.Type == "ascii")
                {
                    for (int i = 0; i < p.Length; i++) if (data[at + i] > 127) throw new ArgumentException("非ASCII字节");
                    result.Value = System.Text.Encoding.ASCII.GetString(data, at, p.Length).TrimEnd('\0');
                    result.EngineeringValue = result.Value; result.EngineeringText = result.Value; result.EngineeringKind = "text"; return result;
                }
                else if (p.Type == "bcd")
                {
                    ulong bcd = 0;
                    for (int i = 0; i < p.Length; i++)
                    {
                        byte b = data[at + (littleEndian ? p.Length - 1 - i : i)];
                        if ((b >> 4) > 9 || (b & 15) > 9) throw new ArgumentException("无效BCD码");
                        bcd = bcd * 100 + (ulong)((b >> 4) * 10 + (b & 15));
                    }
                    unsigned = bcd; value = bcd; result.Raw = bcd.ToString(CultureInfo.InvariantCulture);
                }
                else value = raw;
                value = value * p.Scale + p.Bias;
                if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("非有限浮点数");
                bool unscaledInteger = p.Scale == 1 && p.Bias == 0 && (p.Type == "uint" || p.Type == "int" || p.Type == "bcd");
                if (unscaledInteger)
                {
                    result.EngineeringValue = p.Type == "int" ? (object)signed : (object)unsigned;
                    result.EngineeringText = result.Raw; result.EngineeringKind = "integer";
                }
                else
                {
                    result.EngineeringValue = value; result.EngineeringText = value.ToString("R", CultureInfo.InvariantCulture); result.EngineeringKind = "number";
                }
                string meaning;
                if (p.Enums != null && p.Enums.TryGetValue(result.Raw, out meaning)) result.Value = meaning;
                else
                {
                    if (p.EnumRanges != null)
                        foreach (EnumRange range in p.EnumRanges)
                            if (value >= range.Min && value <= range.Max)
                            {
                                result.Value = range.Text;
                                return result;
                            }
                    if (p.Format == "十六进制") result.Value = "0x" + raw.ToString("X");
                    else if (unscaledInteger) result.Value = result.Raw;
                    else result.Value = value.ToString(p.Precision == 0 ? "0" : "0." + new string('#', Math.Min(12, p.Precision)), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                result.Value = "—"; result.Status = "解码无效"; result.Diagnostic = ex.Message;
                result.EngineeringValue = null; result.EngineeringText = null; result.EngineeringKind = null;
            }
            return result;
        }
    }

    public sealed class SequenceTracker
    {
        private int last = -1;
        private readonly HashSet<int> recent = new HashSet<int>();
        private readonly Queue<int> order = new Queue<int>();
        public const int DuplicateWindow = 5;
        public int Lost;
        public int Previous = -1;
        public bool Reset;
        // 0=new reception, 1=duplicate. Only the most recent five received
        // sequence numbers may suppress decoding. A backward/ambiguous jump
        // rebases the counter immediately; it must never wait for catch-up.
        public int Accept(int seq)
        {
            Lost = 0; Reset = false; Previous = last;
            if (recent.Contains(seq)) { Remember(seq); return 1; }
            if (last >= 0)
            {
                int delta = (seq - last + 16384) % 16384;
                if (delta >= 8192) Reset = true;
                else Lost = Math.Max(0, delta - 1);
            }
            last = seq; Remember(seq);
            return 0;
        }
        private void Remember(int seq)
        {
            recent.Add(seq); order.Enqueue(seq);
            while (order.Count > DuplicateWindow)
            {
                int expired = order.Dequeue();
                if (!order.Contains(expired)) recent.Remove(expired);
            }
        }
    }
}
