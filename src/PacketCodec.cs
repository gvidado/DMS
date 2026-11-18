using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed class PacketRoute
    {
        public PacketDefinition Definition;
        public int Offset = -1;
        public bool Ambiguous;
        public string Reason = "";
    }
    public static class PacketCodec
    {
        public static int Identifier(PacketDefinition d) { return (d.Version << 13) | (d.PacketType << 12) | (d.SecondaryHeader << 11) | d.Apid; }
        public static bool SameRoutingFields(PacketDefinition a, PacketDefinition b)
        { return a.OuterSourceId==b.OuterSourceId && Identifier(a) == Identifier(b) && a.SourceAddress == b.SourceAddress && a.ServiceType == b.ServiceType && a.ServiceSubtype == b.ServiceSubtype; }
        public static string RoutingDifference(byte[] data, int at, PacketDefinition d)
        {
            if (d.SecondaryHeader != 1) return "SPP没有副导头，无法匹配源地址、业务类型和业务子类型";
            if (at < 0 || data.Length - at < 16) return "SPP副导头不完整，无法匹配源地址、业务类型和业务子类型";
            int source = Protocol.U16(data, at + 12), service = data[at + 7], subtype = data[at + 8];
            if (source == d.SourceAddress && service == d.ServiceType && subtype == d.ServiceSubtype) return "";
            var reasons = new List<string>(3);
            if (source != d.SourceAddress) reasons.Add("源地址不符：收到0x" + source.ToString("X4") + "，配置0x" + d.SourceAddress.ToString("X4"));
            if (service != d.ServiceType) reasons.Add("业务类型不符：收到0x" + service.ToString("X2") + "，配置0x" + d.ServiceType.ToString("X2"));
            if (subtype != d.ServiceSubtype) reasons.Add("业务子类型不符：收到0x" + subtype.ToString("X2") + "，配置0x" + d.ServiceSubtype.ToString("X2"));
            return String.Join("；", reasons);
        }
        public static PacketRoute Resolve(byte[] data, string ip, IEnumerable<PacketDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException("source");
            PacketDefinition match = null;
            List<string> ambiguousNames = null, differences = null;
            int matchOffset = -1, firstPrimary = -1;
            foreach (PacketDefinition d in definitions)
            {
                if (ip != null && !String.IsNullOrEmpty(d.SourceIP) && d.SourceIP != ip) continue;
                if(d.OuterSourceId>=0 && (data.Length<6 || Protocol.U16(data,0)!=d.OuterSourceId))continue;
                int first = d.OuterSourceId>=0?6:d.SppOffset < 0 ? 0 : d.SppOffset, last = d.OuterSourceId>=0?6:d.SppOffset < 0 ? Math.Min(32, data.Length - 2) : d.SppOffset, found = -1;
                int identifier = Identifier(d);
                string difference = "";
                for (int at = first; at <= last && at + 2 <= data.Length; at++)
                {
                    if (Protocol.U16(data, at) != identifier) continue;
                    if (firstPrimary < 0) firstPrimary = at;
                    string reason = RoutingDifference(data, at, d);
                    if (!String.IsNullOrEmpty(reason)) { if (String.IsNullOrEmpty(difference)) difference = reason; continue; }
                    if (found < 0) found = at;
                    if (at + 6 <= data.Length && Protocol.U16(data, at + 4) + 7 == data.Length - at) { found = at; break; }
                }
                if (found >= 0)
                {
                    if (match == null) { match = d; matchOffset = found; }
                    else
                    {
                        if (ambiguousNames == null) ambiguousNames = new List<string> { match.Name };
                        ambiguousNames.Add(d.Name);
                    }
                }
                else if (!String.IsNullOrEmpty(difference))
                {
                    if (differences == null) differences = new List<string>();
                    if (differences.Count < 8) differences.Add(d.Name + "：" + difference);
                }
            }
            if (ambiguousNames != null) return new PacketRoute { Ambiguous = true, Offset = matchOffset, Reason = "匹配到多个SPP定义，请区分包标识、源地址、业务类型、业务子类型或来源IP：" + String.Join("、", ambiguousNames) };
            if (match != null) return new PacketRoute { Definition = match, Offset = matchOffset };
            return new PacketRoute { Offset = firstPrimary, Reason = differences != null ? "SPP字段未匹配；" + String.Join("；", differences) : "无匹配的SPP包标识，检查APID、偏移、来源IP及包选择" };
        }
        public static PacketDefinition AtOffset(PacketDefinition definition, int offset)
        {
            if (definition.SppOffset == offset || (definition.OuterSourceId>=0 && offset==6)) return definition;
            var copy = JsonFile.Decode<PacketDefinition>(JsonFile.Encode(definition)); copy.SppOffset = offset; return copy;
        }
        public static int FindOffset(byte[] data, PacketDefinition d)
        {
            if(d.OuterSourceId>=0)return data.Length>=6 && Protocol.U16(data,0)==d.OuterSourceId?6:-1;
            if (d.SppOffset >= 0) return d.SppOffset;
            return Resolve(data, null, new[] { d }).Offset;
        }
        public static Packet Parse(byte[] data, PacketDefinition d)
        {return Parse(data,d,null);}
        internal static Packet Parse(byte[] data,PacketDefinition d,bool? checksumVerified)
        {
            if (data == null || data.Length > 65507) throw new ArgumentException("UDP长度越界");
            if(d.OuterSourceId>=0)
            {
                if(data.Length<6 || Protocol.U16(data,0)!=d.OuterSourceId)throw new ArgumentException("外层数据源标识不匹配，要求0x"+d.OuterSourceId.ToString("X4"));
                if(Protocol.U16(data,4)+1!=data.Length-6)throw new ArgumentException("外层长度不符：字段+1="+(Protocol.U16(data,4)+1)+"，实际内容="+(data.Length-6));
            }
            if (d.ExpectedDatagramLength != 0 && d.ExpectedDatagramLength != data.Length) throw new ArgumentException("UDP长度不符：收到" + data.Length + "，要求" + d.ExpectedDatagramLength);
            int at = FindOffset(data, d), minimum = d.SecondaryHeader == 1 ? 18 : 8;
            if (at < 0) throw new ArgumentException("未找到符合包标识及长度的SPP包，检查SPP偏移与包长");
            if (data.Length - at < minimum) throw new ArgumentException("SPP包过短");
            if (Protocol.U16(data, at) != Identifier(d)) throw new ArgumentException("主导头包标识不符，要求0x" + Identifier(d).ToString("X4"));
            if ((Protocol.U16(data, at + 2) >> 14) != d.SequenceFlags) throw new ArgumentException("序列标志不符，暂不支持分段SPP");
            int field = Protocol.U16(data, at + 4);
            bool mismatch = field + 7 != data.Length - at;
            if (mismatch && d.StrictLength) throw new ArgumentException("SPP长度不符：声明" + (field + 7) + "，实际" + (data.Length - at));
            // The configured package-data length is reference information, not a fixed-length gate.
            string routingDifference = RoutingDifference(data, at, d);
            if (!String.IsNullOrEmpty(routingDifference)) throw new ArgumentException(routingDifference);
            if (!(checksumVerified??SppChecksum.Verify(data, at, data.Length - at))) throw new ArgumentException("Fletcher-16累加和校验失败");
            return new Packet { Offset = at, Length = data.Length - at, Apid = d.Apid, Sequence = Protocol.U16(data, at + 2) & 16383,
                ChecksumVerified = true, PacketId = d.Id, PacketName = d.Name,
                Warning = mismatch ? "SPP长度不一致，仅诊断接收" : "" };
        }
        public static byte[] Hex(string text)
        {
            if (text == null || text.Length > 400000) throw new ArgumentException("十六进制报文过大");
            const string invalid = "请输入完整十六进制字节，可用空格换行或逗号分隔，最大65507字节";
            var bytes = new byte[Math.Min(65507, (text.Length + 1) / 2)];
            int count = 0, high = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                { i++; continue; }
                if (ch == ',' || ch == '，' || Char.IsWhiteSpace(ch)) continue;
                int nibble = ch >= '0' && ch <= '9' ? ch - '0'
                    : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10
                    : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
                if (nibble < 0) throw new ArgumentException(invalid);
                if (high < 0) high = nibble;
                else
                {
                    if (count == bytes.Length) throw new ArgumentException(invalid);
                    bytes[count++] = (byte)((high << 4) | nibble);
                    high = -1;
                }
            }
            if (count == 0 || high >= 0) throw new ArgumentException(invalid);
            if (count != bytes.Length) Array.Resize(ref bytes, count);
            return bytes;
        }
        public static object TryDecode(DecoderConfiguration config, PacketDefinition d, byte[] bytes)
        {
            d.Validate(new HashSet<string>(config.Catalog.Parameters.Select(p => p.Group)));
            Packet packet = Parse(bytes, d); Settings settings = d.DecodeSettings();
            var rows = new List<object>();
            foreach (Parameter p in config.Catalog.Parameters.Where(p => d.Groups.Contains(p.Group)))
            {
                var field=JsonFile.Decode<Parameter>(JsonFile.Encode(p));field.TelemetryNamespace=d.TelemetryNamespace;
                Reading value = Protocol.Decode(bytes, packet, field, settings, DateTime.UtcNow);
                rows.Add(new { code = p.Code, reference = field.Reference, valueReference = field.ValueReference, name = p.Name, group = p.Group, value = value.Value, engineeringValue = value.EngineeringValue, engineeringText = value.EngineeringText, engineeringKind = value.EngineeringKind, raw = value.Raw, unit = p.Unit,
                    status = value.Status != "有效" ? value.Status : !d.MappingConfirmed ? "试解码/映射未确认" : "试解码通过",
                    diagnostic = value.Diagnostic, sourceRow = p.SourceRow, offset = p.Offset, position = p.Position });
            }
            int at = packet.Offset;
            return new { packet = new { id = d.Id, name = d.Name, apid = packet.Apid, identifier = "0x" + Identifier(d).ToString("X4"), version = d.Version,
                sequence = packet.Sequence, sequenceFlags = d.SequenceFlags, lengthField = Protocol.U16(bytes, at + 4), sppLength = packet.Length,
                sppOffset = packet.Offset, datagramLength = bytes.Length, checksum = "通过", checksumAlgorithm = SppChecksum.Mode, warning = packet.Warning,
                outerSourceId=d.OuterSourceId,telemetryNamespace=d.TelemetryNamespace,sourceAddress = d.SecondaryHeader == 1 ? "0x" + Protocol.U16(bytes, at + 12).ToString("X4") : "—",
                serviceType = bytes[at + 7], serviceSubtype = bytes[at + 8],
                destinationAddress = d.SecondaryHeader == 1 ? "0x" + Protocol.U16(bytes, at + 14).ToString("X4") : "—" },
                rows = rows, note = "仅对输入报文试解码，不发送UDP，不更新实时参数或帧计数" };
        }
    }
}
