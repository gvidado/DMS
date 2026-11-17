using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class CaptureOptions
    {
        public int Port;
        public bool IncludeDisabled = true, UseWhitelist;
        public string PacketId = "";
    }
    public sealed class CaptureRecord
    {
        public long Frame;
        public int Apid = -1, Sequence = -1, SppOffset = -1, Length, InvalidParameters;
        public string Time, Source, Destination, PacketId = "", PacketName = "", Status = "", Reason = "";
        public string Checksum = "—";
        public int Missing;
        public bool Detail;
    }
    public sealed class CaptureParameterStat
    {
        public string Id, Packet, Code, Name, Group, Value, Raw, Status, Diagnostic, Unit;
        public long Seen, Invalid;
    }
    public sealed class CapturePacketStat
    {
        public string Id, Name; public int Apid; public long Matched, Valid, Diagnostic, Invalid, Duplicates, OutOfOrder, Missing, Resets;
    }
    public sealed class CaptureCheckJob
    {
        public readonly object Gate = new object();
        public string Id, Username, Filename, Path, CsvPath, AbnormalCsvPath, Error = "";
        private readonly List<FileStream> temporaryFiles = new List<FileStream>();
        public volatile string State = "queued";
        public DateTime Created = DateTime.UtcNow;
        public DateTime Finished = DateTime.UtcNow;
        public volatile bool Cancel;
        public long Bytes, Size;
        public long Frames, Udp, Other, Unsupported, Filtered, Unmatched, Invalid, Valid, Diagnostic, Missing, Duplicates, OutOfOrder, Resets, NoTimestamp, Decoded, InvalidParameters;
        public int OrdinarySamples, ProblemSamples;
        public long AbnormalSaved;
        public DecoderConfiguration Configuration;
        public Settings Settings;
        public CaptureOptions Options;
        public readonly List<CaptureRecord> Records = new List<CaptureRecord>();
        public readonly Dictionary<long, CaptureFrame> Samples = new Dictionary<long, CaptureFrame>();
        public readonly Dictionary<string, CapturePacketStat> Packets = new Dictionary<string, CapturePacketStat>();
        public readonly Dictionary<string, CaptureParameterStat> Parameters = new Dictionary<string, CaptureParameterStat>();
        public readonly Dictionary<string, long> Reasons = new Dictionary<string, long>();
        public void PrepareTemporaryFiles()
        {
            try
            {
                foreach (string path in new[] { Path, CsvPath, AbnormalCsvPath })
                    temporaryFiles.Add(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose));
            }
            catch { DeleteTemporaryFiles(); throw; }
        }
        public void DeleteTemporaryFiles()
        {
            lock (Gate)
            {
                foreach (FileStream file in temporaryFiles) file.Dispose();
                temporaryFiles.Clear();
                foreach (string path in new[] { Path, CsvPath, AbnormalCsvPath })
                    if (!String.IsNullOrEmpty(path)) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        public static FileStream OpenTemporaryRead(string path)
        { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        public object Snapshot(bool full)
        {
            lock (Gate) return new { id = Id, filename = Filename, state = State, error = Error, revision = Configuration.Revision, createdUtc = Created.ToString("o"),
                progress = Size == 0 ? 0 : (int)Math.Min(100m, (decimal)Bytes * 100m / Size), bytes = Bytes, size = Size,
                summary = new { frames = Frames, udp = Udp, other = Other, unsupported = Unsupported, filtered = Filtered, unmatched = Unmatched,
                    invalid = Invalid, valid = Valid, diagnostic = Diagnostic, missing = Missing, duplicates = Duplicates, outOfOrder = OutOfOrder, resets = Resets,
                    noTimestamp = NoTimestamp, decoded = Decoded, invalidParameters = InvalidParameters, abnormalSaved = AbnormalSaved }, options = Options,
                packets = full ? Packets.Values.ToArray() : null, reasons = full ? Reasons.Select(p => new { reason = p.Key, count = p.Value }).ToArray() : null,
                records = full ? Records.ToArray() : null, parameters = full ? Parameters.Values.ToArray() : null,
                note = "按文件记录顺序检验；帧号与Wireshark对应，时间显示北京时间。页面保留前200条普通记录与前200条问题记录，完整逐帧结果可导出CSV；参数汇总最多2000项。帧计数缺口也可能来自抓包遗漏，不能直接认定设备丢包。通过只表示当前配置下的结构、累加和与解码检查通过，工程值正确性仍需已知真值核对。" };
        }
        public void Run()
        {
            try
            {
                lock (Gate) State = "running";
                var sequences = new Dictionary<string, SequenceTracker>();
                var parameters = Configuration.Packets.ToDictionary(p => p.Id, p => Configuration.Catalog.Parameters.Where(x => p.Groups.Contains(x.Group)).ToArray());
                using (var csv = new StreamWriter(new FileStream(CsvPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(true)))
                using (var abnormal = new StreamWriter(new FileStream(AbnormalCsvPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(true)))
                {
                    csv.WriteLine("帧号,时间（北京时间）,来源,目的,UDP载荷字节数,SPP包,APID,帧计数,状态,原因,累加和,参数异常数,计数缺口");
                    abnormal.WriteLine("帧号,时间（北京时间）,来源,目的,SPP包,APID,帧计数,状态,异常原因,内容说明,SPP在UDP内偏移,SPP声明字节数,实存SPP字节数,SPP完整内容HEX,完整UDP载荷HEX,抓包原始帧HEX");
                    CaptureReader.ReadPcapng(Path, delegate(CaptureFrame frame, long position)
                    {
                        if (Cancel) return;
                        lock (Gate)
                        {
                            Bytes = position; Frames++;
                            if (String.IsNullOrEmpty(frame.Time)) NoTimestamp++;
                            CaptureRecord record = Examine(frame, sequences, parameters);
                            if (record.Status == "异常" || record.Status == "部分参数异常" || (record.Status == "未匹配" && record.SppOffset >= 0)) WriteAbnormal(abnormal, frame, record);
                            bool problem = record.Status == "异常" || record.Status == "未匹配" || record.Status == "不支持" || record.Status == "待确认" || record.Status == "部分参数异常";
                            if (problem ? ProblemSamples < 200 : OrdinarySamples < 200)
                            {
                                Records.Add(record); frame.Payload = null; Samples[frame.Number] = frame;
                                if (problem) ProblemSamples++; else OrdinarySamples++;
                            }
                            if (!String.IsNullOrEmpty(record.Reason))
                            {
                                string reason = record.Reason; if (!Reasons.ContainsKey(reason) && Reasons.Count >= 100) reason = "其他原因（详细信息见CSV）";
                                long count; Reasons.TryGetValue(reason, out count); Reasons[reason] = count + 1;
                            }
                            csv.WriteLine(String.Join(",", new[] { record.Frame.ToString(), Cell(record.Time), Cell(record.Source), Cell(record.Destination), record.Length.ToString(), Cell(record.PacketName), record.Apid < 0 ? "" : record.Apid.ToString(), record.Sequence < 0 ? "" : record.Sequence.ToString(), Cell(record.Status), Cell(record.Reason), Cell(record.Checksum), record.InvalidParameters.ToString(), record.Missing.ToString() }));
                        }
                    }, () => Cancel);
                }
                lock (Gate) { Finished = DateTime.UtcNow; State = Cancel ? "cancelled" : "completed"; if (!Cancel) Bytes = Size; }
            }
            catch (Exception ex)
            {
                lock (Gate) { Finished = DateTime.UtcNow; Error = ex is ArgumentException || ex is IOException ? ex.Message : "文件检验失败：" + ex.GetType().Name; State = "failed"; }
            }
        }
        private void WriteAbnormal(StreamWriter csv, CaptureFrame frame, CaptureRecord record)
        {
            byte[] payload = frame.Payload;
            string spp = "", udp = payload == null ? "" : BitConverter.ToString(payload).Replace('-', ' '), rawFrame = "", note;
            int actual = 0, declared = -1, offset = record.SppOffset;
            if (payload != null && offset >= 0 && offset < payload.Length)
            {
                actual = payload.Length - offset;
                if (actual >= 6) declared = Protocol.U16(payload, offset + 4) + 7;
                spp = BitConverter.ToString(payload, offset, actual).Replace('-', ' ');
                note = declared == actual ? "完整SPP：含主导头、副导头、用户数据域和末尾校验；原始字节未修改" : "SPP声明长度与实际不符；保留从SPP起点到UDP载荷末尾的全部实际字节";
            }
            else
            {
                note = "抓包或网络报文异常，无法可靠定位完整SPP；仅保留实际捕获的原始帧，不补齐缺失字节";
                byte[] bytes = new byte[frame.CapturedLength];
                using (var source = OpenTemporaryRead(Path))
                {
                    source.Position = frame.DataFileOffset; int done = 0, n;
                    while (done < bytes.Length && (n = source.Read(bytes, done, bytes.Length - done)) > 0) done += n;
                    if (done != bytes.Length) throw new IOException("读取异常报文原始字节失败");
                }
                rawFrame = BitConverter.ToString(bytes).Replace('-', ' ');
            }
            csv.WriteLine(String.Join(",", new[] { record.Frame.ToString(), Cell(record.Time), Cell(record.Source), Cell(record.Destination), Cell(record.PacketName), record.Apid < 0 ? "" : record.Apid.ToString(), record.Sequence < 0 ? "" : record.Sequence.ToString(), Cell(record.Status), Cell(record.Reason), Cell(note), offset < 0 ? "" : offset.ToString(), declared < 0 ? "" : declared.ToString(), actual.ToString(), Cell(spp), Cell(udp), Cell(rawFrame) }));
            AbnormalSaved++;
        }
        private static string Cell(string text)
        {
            text = text ?? ""; string trim = text.TrimStart();
            if (trim.Length > 0 && "=+-@".IndexOf(trim[0]) >= 0) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        private PacketRoute ResolveCapture(byte[] payload, string ip)
        {
            var definitions=Configuration.Packets.Where(p=>(Options.IncludeDisabled||p.Enabled||p.MonitorRaw)&&(String.IsNullOrEmpty(Options.PacketId)||p.Id==Options.PacketId)).ToArray();
            var decode=PacketCodec.Resolve(payload,ip,definitions.Where(p=>!p.MonitorRaw||p.Enabled));
            if(decode.Definition!=null||decode.Ambiguous)return decode;
            return PacketCodec.Resolve(payload,ip,definitions.Where(p=>p.MonitorRaw&&!p.Enabled));
        }
        public PacketDefinition Match(byte[] payload, string ip, out int offset)
        {
            PacketRoute route = ResolveCapture(payload, ip); offset = route.Offset;
            return route.Definition == null ? null : PacketCodec.AtOffset(route.Definition, offset);
        }
        private CaptureRecord Examine(CaptureFrame f, Dictionary<string, SequenceTracker> sequences, Dictionary<string, Parameter[]> parameters)
        {
            var r = new CaptureRecord { Frame = f.Number, Time = f.Time, Source = f.SourceIP + (f.SourcePort > 0 ? ":" + f.SourcePort : ""), Destination = f.DestinationIP + (f.DestinationPort > 0 ? ":" + f.DestinationPort : ""), Length = f.PayloadLength };
            if (f.Udp) Udp++;
            if (f.Unsupported) { Unsupported++; r.Status = "不支持"; r.Reason = f.Issue; return r; }
            if (!String.IsNullOrEmpty(f.Issue)) { Invalid++; r.Status = "异常"; r.Reason = f.Issue; return r; }
            if (!f.Udp) { Other++; r.Status = "非UDP"; return r; }
            if ((Options.Port > 0 && f.DestinationPort != Options.Port) || (Options.UseWhitelist && !Settings.SourceIPs.Contains(f.SourceIP)))
            { Filtered++; r.Status = "筛除"; r.Reason = "不符合目的端口或来源IP筛选条件"; return r; }
            r.Detail = true;
            PacketRoute route = ResolveCapture(f.Payload, f.SourceIP); int offset = route.Offset;
            if (route.Definition == null)
            {
                Unmatched++; r.Status = "未匹配"; r.Reason = route.Reason; r.SppOffset = offset;
                if (offset >= 0)
                {
                    r.Apid = Protocol.U16(f.Payload, offset) & 2047;
                    if (offset + 4 <= f.Payload.Length) r.Sequence = Protocol.U16(f.Payload, offset + 2) & 16383;
                }
                return r;
            }
            PacketDefinition d = PacketCodec.AtOffset(route.Definition, offset);
            r.PacketId = d.Id; r.PacketName = d.Name; r.Apid = d.Apid; r.SppOffset = offset; r.Detail = true;
            CapturePacketStat stats;
            if (!Packets.TryGetValue(d.Id, out stats)) { stats = new CapturePacketStat { Id = d.Id, Name = d.Name, Apid = d.Apid }; Packets[d.Id] = stats; }
            stats.Matched++;
            if (offset + 4 <= f.Payload.Length) r.Sequence = Protocol.U16(f.Payload, offset + 2) & 16383;
            r.Checksum = "未完成";
            Packet packet;
            try
            {
                packet = PacketCodec.Parse(f.Payload, d); r.Sequence = packet.Sequence; r.Checksum = packet.ChecksumVerified ? "通过" : "未确认";
                if (packet.Length != Protocol.U16(f.Payload, packet.Offset + 4) + 7) throw new ArgumentException("SPP声明长度与实际不一致，未检验参数");
            }
            catch (ArgumentException ex) { Invalid++; stats.Invalid++; r.Status = "异常"; r.Reason = ex.Message; if (ex.Message.Contains("累加和校验失败")) r.Checksum = "失败"; return r; }
            // Failed checksums return above and cannot contaminate sequence statistics.
            if (packet.ChecksumVerified)
            {
                string stream = f.Section + "/" + r.Source + "/" + r.Destination + "/" + d.Id;
                SequenceTracker tracker;
                if (!sequences.TryGetValue(stream, out tracker))
                { if (sequences.Count >= 4096) throw new ArgumentException("UDP数据流超过4096，检验未完成，请按设备或端口分段导出"); tracker = new SequenceTracker(); sequences[stream] = tracker; }
                int disposition = tracker.Accept(packet.Sequence);
                if (disposition == 1) { Duplicates++; stats.Duplicates++; r.Status = "重复帧"; return r; }
                Missing += tracker.Lost; stats.Missing += tracker.Lost; r.Missing = tracker.Lost;
                if (tracker.Reset)
                {
                    OutOfOrder++; stats.OutOfOrder++; Resets++; stats.Resets++;
                    r.Reason="帧计数回退或大幅跳变，已重新同步并继续解码：原计数="+tracker.Previous+"，当前计数="+packet.Sequence;
                }
            }
            if(d.MonitorRaw&&!d.Enabled){Diagnostic++;stats.Diagnostic++;r.Status="原包监测";r.Reason="仅监测SPP原包，未执行遥测参数解码；文件检验不写入实时原包缓存";return r;}
            Parameter[] fields = parameters[d.Id]; Settings settings = d.DecodeSettings();
            foreach (Parameter field in fields)
            {
                Reading read = Protocol.Decode(f.Payload, packet, field, settings, DateTime.UtcNow); Decoded++;
                bool invalid = read.Status != "有效"; if (invalid) { InvalidParameters++; r.InvalidParameters++; }
                string key = d.Id + "::" + field.Id; CaptureParameterStat stat;
                if (!Parameters.TryGetValue(key, out stat) && Parameters.Count < 2000)
                { stat = new CaptureParameterStat { Id = key, Packet = d.Name, Code = TelemetryReferences.Name(field.Code,d.TelemetryNamespace), Name = field.Name, Group = field.Group, Unit = field.Unit }; Parameters[key] = stat; }
                if (stat != null)
                { stat.Seen++; if (invalid) stat.Invalid++; stat.Value = read.Value; stat.Raw = read.Raw; stat.Status = read.Status; stat.Diagnostic = read.Diagnostic; }
            }
            if (fields.Length == 0 || !d.Enabled || !d.MappingConfirmed || !packet.ChecksumVerified)
            {
                Diagnostic++; stats.Diagnostic++; r.Status = "待确认";
                var notes = new List<string>(); if (fields.Length == 0) notes.Add("未关联遥测参数表"); if (!d.Enabled) notes.Add("包尚未启用"); if (!d.MappingConfirmed) notes.Add("映射未确认");
                if (r.InvalidParameters > 0) notes.Add(r.InvalidParameters + "项参数解码异常"); r.Reason = String.Join("；", notes);
            }
            else if (r.InvalidParameters > 0) { Invalid++; stats.Invalid++; r.Status = "部分参数异常"; r.Reason = r.InvalidParameters + "项参数无法解码，请查看字段定义和报文详情"; }
            else { Valid++; stats.Valid++; r.Status = "通过"; }
            return r;
        }
        public object Detail(long frameNumber)
        {
            CaptureFrame frame; CaptureRecord record;
            lock (Gate)
            {
                record = Records.FirstOrDefault(r => r.Frame == frameNumber);
                if (record == null || !Samples.TryGetValue(frameNumber, out frame)) throw new ArgumentException("该帧不在页面保留样本中，请参照完整CSV回到Wireshark定位");
            }
            if (!record.Detail) return new { frame = record, hex = "", rows = new object[0], error = record.Reason };
            byte[] payload = new byte[frame.PayloadLength];
            using (var file = OpenTemporaryRead(Path))
            { file.Position = frame.PayloadFileOffset; int done = 0, n; while (done < payload.Length && (n = file.Read(payload, done, payload.Length - done)) > 0) done += n; if (done != payload.Length) throw new ArgumentException("临时抓包文件已不可用，请重新导入"); }
            string hex = BitConverter.ToString(payload).Replace('-', ' ');
            if (String.IsNullOrEmpty(record.PacketId)) return new { frame = record, hex = hex, rows = new object[0], error = record.Reason };
            try
            {
                PacketDefinition original = Configuration.Packets.First(p => p.Id == record.PacketId);
                var d = JsonFile.Decode<PacketDefinition>(JsonFile.Encode(original));
                int offset; Match(payload, frame.SourceIP, out offset); d.SppOffset = offset;
                // Detail deliberately decodes the selected packet even when it is disabled or unconfirmed.
                Packet packet = PacketCodec.Parse(payload, d);
                if (packet.Length != Protocol.U16(payload, packet.Offset + 4) + 7) throw new ArgumentException("SPP声明长度与实际不一致，未检验参数");
                if(d.MonitorRaw&&!d.Enabled)return new{frame=record,hex=hex,rows=new object[0],error="仅监测SPP原包，未执行遥测参数解码"};
                Settings cfg = d.DecodeSettings(); var rows = new List<object>();
                foreach (Parameter field in Configuration.Catalog.Parameters.Where(p => d.Groups.Contains(p.Group)).Take(2000))
                { Reading read = Protocol.Decode(payload, packet, field, cfg, DateTime.UtcNow); rows.Add(new { code = TelemetryReferences.Name(field.Code,d.TelemetryNamespace), reference=TelemetryReferences.Name(field.Code,d.TelemetryNamespace), name = field.Name, group = field.Group, value = read.Value, raw = read.Raw, unit = field.Unit, status = read.Status, diagnostic = read.Diagnostic }); }
                return new { frame = record, hex = hex, rows = rows, error = "" };
            }
            catch (ArgumentException ex) { return new { frame = record, hex = hex, rows = new object[0], error = ex.Message }; }
        }
    }
}
