using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed class LiveAbnormalRecord
    {
        public long Id, CaptureFrame;
        public DateTime Time;
        public string Source, Mode, PacketName, Reason;
        public int Apid = -1, Sequence = -1, Offset = -1;
        public byte[] Bytes;
        public bool IsAlarm = true;
    }
    public sealed partial class Monitor
    {
        private readonly Queue<LiveAbnormalRecord> liveAbnormal = new Queue<LiveAbnormalRecord>();
        private long liveAbnormalId, liveAbnormalRevision;
        private static readonly string[] LengthAlarmPrefixes = { "UDP长度不符：", "SPP长度字段不符：", "SPP长度不符：", "包数据长度字段不符：" };
        private void RecordAbnormal(byte[] data, string ip, bool replay, int port, long captureFrame, string reason, PacketDefinition known, bool recordAlarm = true, PacketRoute resolved = null)
        {
            var row = new LiveAbnormalRecord { Id = ++liveAbnormalId, Time = DateTime.UtcNow, Source = ip + (port > 0 ? ":" + port : ""),
                Mode = replay ? "文件演示" : "实时UDP", CaptureFrame = captureFrame, Reason = reason, PacketName = "未识别", Bytes = data, IsAlarm = recordAlarm };
            PacketRoute route = resolved;
            if (route == null)
            {
                IEnumerable<PacketDefinition> definitions = known != null ? new[] { known } : NativeDecoder
                    ? decoder.Packets.Where(p => p.Enabled && (String.IsNullOrEmpty(p.SourceIP) || p.SourceIP == ip))
                    : Config.AllowedApids.Select(a => new PacketDefinition { Name = "APID " + a, Apid = a, SppOffset = Config.SppOffset, SourceAddress = Config.SourceAddress, ServiceType = Config.ServiceType, ServiceSubtype = Config.ServiceSubtype });
                route = PacketCodec.Resolve(data, ip, definitions);
            }
            if (route.Offset >= 0)
            {
                row.Offset = route.Offset; row.Apid = Protocol.U16(data, row.Offset) & 2047;
                row.PacketName = route.Definition == null ? "未匹配SPP" : route.Definition.Name;
                if (row.Offset + 4 <= data.Length) row.Sequence = Protocol.U16(data, row.Offset + 2) & 16383;
            }
            liveAbnormal.Enqueue(row); while (liveAbnormal.Count > 200) liveAbnormal.Dequeue(); liveAbnormalRevision++;
            if (recordAlarm)
            {
                string alarmKind = reason;
                foreach (string prefix in LengthAlarmPrefixes)
                    if (reason.StartsWith(prefix, StringComparison.Ordinal)) { alarmKind = prefix; break; }
                RecordAlarm("warn", "SPP异常", reason, row.Source, row.PacketName, row.Apid, row.Sequence, row.Mode, row.CaptureFrame,
                    JsonFile.Encode(new[] { route.Definition == null ? "" : route.Definition.Id, alarmKind }));
            }
        }
        private static object AbnormalSummary(LiveAbnormalRecord r)
        { return new { id = r.Id, time = BeijingTime.Format(r.Time,"yyyy-MM-dd HH:mm:ss.fff"), source = r.Source, mode = r.Mode, captureFrame = r.CaptureFrame, packet = r.PacketName, apid = r.Apid, sequence = r.Sequence, length = r.Bytes.Length, reason = r.Reason, alarm = r.IsAlarm }; }
        public object LiveAbnormalList()
        {
            long revision;
            LiveAbnormalRecord[] rows;
            lock (Gate) { revision = liveAbnormalRevision; rows = liveAbnormal.ToArray(); }
            Array.Reverse(rows);
            return new { revision, count = rows.Length, records = rows.Select(AbnormalSummary).ToArray() };
        }
        private static string RawSpp(LiveAbnormalRecord r)
        { return r.Offset < 0 ? "" : BitConverter.ToString(r.Bytes, r.Offset).Replace('-', ' '); }
        public object LiveAbnormalDetail(long id)
        {
            LiveAbnormalRecord r;
            lock (Gate) r = liveAbnormal.FirstOrDefault(x => x.Id == id);
            if (r == null) throw new ArgumentException("该异常记录已清除或超出最近200条");
            return new { record = AbnormalSummary(r), sppHex = RawSpp(r), udpHex = BitConverter.ToString(r.Bytes).Replace('-', ' '),
                note = r.Offset < 0 ? "无法可靠定位SPP，保留全部实际UDP载荷" : "从已识别SPP起点到UDP末尾的全部实际字节，含包头和校验；长度异常时不补齐或截断" };
        }
        internal static string CsvCell(string text)
        { text = text ?? ""; if (text.TrimStart().Length > 0 && "=+-@".IndexOf(text.TrimStart()[0]) >= 0) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
        public void WriteLiveAbnormalCsv(TextWriter writer)
        {
            LiveAbnormalRecord[] rows; lock (Gate) rows = liveAbnormal.ToArray();
            writer.WriteLine("记录号,接收时间（北京时间）,来源,模式,抓包帧号,SPP包,APID,帧计数,异常原因,UDP字节数,SPP在UDP内偏移,SPP完整内容HEX,完整UDP载荷HEX,诊断级别");
            foreach (LiveAbnormalRecord r in rows)
                writer.WriteLine(String.Join(",", new[] { r.Id.ToString(), CsvCell(BeijingTime.Format(r.Time,"yyyy-MM-dd HH:mm:ss.fff")), CsvCell(r.Source), CsvCell(r.Mode), r.CaptureFrame > 0 ? r.CaptureFrame.ToString() : "", CsvCell(r.PacketName), r.Apid < 0 ? "" : r.Apid.ToString(), r.Sequence < 0 ? "" : r.Sequence.ToString(), CsvCell(r.Reason), r.Bytes.Length.ToString(), r.Offset < 0 ? "" : r.Offset.ToString(), CsvCell(RawSpp(r)), CsvCell(BitConverter.ToString(r.Bytes).Replace('-', ' ')), CsvCell(r.IsAlarm ? "异常" : "未匹配信息") }));
        }
        public void ClearLiveAbnormal() { lock (Gate) { liveAbnormal.Clear(); liveAbnormalRevision++; } }
    }
}
