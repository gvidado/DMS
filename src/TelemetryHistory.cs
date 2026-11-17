using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed class EngineeringSample
    {
        public object value;
        public string valueText, kind, display, raw, hexSource, sourceIP, timeUtc,eventId;
        public int sourcePort, sequence;
        public long sampleNumber;
        public double observedMonotonic;
    }

    // Access is serialized by Monitor.Gate. Samples are immutable after insertion.
    public sealed class ParameterHistory
    {
        public const int Capacity = 10;
        public Reading Latest;
        public double LatestMonotonic;
        public double LatestDataTime;
        public long LatestObservation;
        public readonly List<EngineeringSample> Samples = new List<EngineeringSample>(Capacity);
        private long serial;
        public void Observe(Reading reading)
        {Observe(reading,Monitor.RuleSeconds(),BeijingTime.Iso(reading.Time));}
        internal void Observe(Reading reading,double observed,string timeUtc)
        {
            Latest = reading;
            LatestMonotonic = observed;
            if (reading.Status != "有效" || reading.EngineeringValue == null) return;
            if (Samples.Count == Capacity) Samples.RemoveAt(Capacity - 1);
            Samples.Insert(0, new EngineeringSample { value = reading.EngineeringValue, valueText = reading.EngineeringText,
                kind = reading.EngineeringKind, display = reading.Value, raw = reading.Raw, hexSource = reading.HexSource,
                sourceIP = reading.SourceIP, sourcePort = reading.SourcePort, timeUtc = timeUtc,
                sequence = reading.Sequence, sampleNumber = ++serial, observedMonotonic=LatestMonotonic,eventId=reading.PacketEventId });
        }
    }

    public sealed partial class Monitor
    {
        private readonly Dictionary<string, Dictionary<string, ParameterHistory>> engineeringHistory = new Dictionary<string, Dictionary<string, ParameterHistory>>();
        private long engineeringObservationSerial;
        private double HistoryAgeMs(ParameterHistory history)
        {return history==null?Double.PositiveInfinity:Math.Max(0,HistorySeconds()-history.LatestDataTime)*1000;}
        private string DisplayReadingStatus(Reading reading){return DisplayReadingStatus(reading,HistorySeconds(),DateTime.UtcNow);}
        private string DisplayReadingStatus(Reading reading,double dataNow,DateTime now)
        {
            if(reading.Status!="有效")return reading.Status;
            var history=RuleHistoryById(reading.Id,reading.SourceIP);
            bool expired=history!=null && Object.ReferenceEquals(history.Latest,reading)?Math.Max(0,dataNow-history.LatestDataTime)*1000>Config.DataTimeoutMs:(now-reading.Time).TotalMilliseconds>Config.DataTimeoutMs;
            return expired?"过期":reading.Status;
        }
        private ParameterHistory RuleHistoryById(string id,string source)
        {Dictionary<string,ParameterHistory> stream;ParameterHistory h;return source!=null && engineeringHistory.TryGetValue(source,out stream) && stream.TryGetValue(id,out h)?h:null;}
        private Dictionary<string, ParameterHistory> EngineeringStream(string source)
        {
            Dictionary<string, ParameterHistory> stream;
            if (!engineeringHistory.TryGetValue(source, out stream)) engineeringHistory[source] = stream = new Dictionary<string, ParameterHistory>();
            return stream;
        }
        private void RecordEngineering(Dictionary<string, ParameterHistory> stream, Reading reading, double observed, double dataTime, string timeUtc)
        {
            ParameterHistory history;
            if (!stream.TryGetValue(reading.Id, out history)) stream[reading.Id] = history = new ParameterHistory();
            history.Observe(reading,observed,timeUtc);
            history.LatestObservation=++engineeringObservationSerial;
            history.LatestDataTime=dataTime;
        }

        public object ReadEngineeringHistory(string reference, string parameterId, string sourceIP)
        {
            lock (Gate)
            {
                reference = (reference ?? "").Trim(); parameterId = (parameterId ?? "").Trim(); sourceIP = (sourceIP ?? "").Trim();
                int index = 0;
                Match suffix = Regex.Match(reference, @"\.(value|pre[0-9])$");
                if (suffix.Success && (reference.StartsWith("this.", StringComparison.Ordinal) || reference.Count(c => c == '.') == 2))
                { if (suffix.Groups[1].Value != "value") index = suffix.Groups[1].Value[3] - '0'; reference = reference.Substring(0, suffix.Index); }
                else if (reference.Length > 0 && reference != "this" && !Regex.IsMatch(reference, @"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$"))
                    throw new ArgumentException("工程值引用使用TM.参数代号.value或pre0至pre9");
                Parameter field = null;
                if (parameterId.Length > 0)
                {
                    field = Catalog.FirstOrDefault(p => p.Id == parameterId);
                    if (field == null) throw new ArgumentException("未知参数标识");
                    if (reference.Length > 0 && reference != "this" && reference != field.Reference) throw new ArgumentException("参数标识与TM引用不一致");
                }
                else
                {
                    if (reference == "this" || reference.Length == 0) throw new ArgumentException("this引用需要parameterId指定当前参数");
                    Parameter[] candidates = Catalog.Where(p => p.Reference == reference).ToArray();
                    if (candidates.Length != 1) throw new ArgumentException(candidates.Length == 0 ? "未知TM参数引用" : "TM引用对应多个参数或SPP，请使用parameterId明确绑定");
                    field = candidates[0];
                }
                if (sourceIP.Length == 0)
                {
                    string[] configured = Config.SourceIPs.Distinct().ToArray();
                    if (configured.Length != 1) throw new ArgumentException("存在多个来源，请指定sourceIP读取独立历史");
                    sourceIP = configured[0];
                }
                IPAddress address;
                if (!IPAddress.TryParse(sourceIP, out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !Config.SourceIPs.Contains(sourceIP)) throw new ArgumentException("sourceIP必须是已配置的来源IPv4地址");
                Dictionary<string, ParameterHistory> stream; ParameterHistory history = null;
                if (engineeringHistory.TryGetValue(sourceIP, out stream)) stream.TryGetValue(field.Id, out history);
                Reading latest = history == null ? null : history.Latest;
                string quality = latest == null ? "待接收" : latest.Status;
                if (latest != null && quality == "有效" && HistoryAgeMs(history) > Config.DataTimeoutMs) quality = "过期";
                bool running = Listening || Demo;
                var slots = new EngineeringSample[ParameterHistory.Capacity];
                if (history != null) history.Samples.CopyTo(slots, 0);
                EngineeringSample selected = slots[index];
                return new { reference = field.Reference, valueReference = field.ValueReference, parameterId = field.Id, sourceIP = sourceIP,
                    capacity = ParameterHistory.Capacity, count = history == null ? 0 : history.Samples.Count, slots = slots,
                    quality = quality, running = running, mode = Demo ? "demo" : Listening ? "live" : "stopped",
                    requestedIndex = index, value = selected == null ? null : selected.value, valueText = selected == null ? null : selected.valueText,
                    usable = running && quality == "有效" && selected != null,
                    current = latest == null ? null : new { engineeringValue = latest.EngineeringValue, engineeringText = latest.EngineeringText,
                        kind = latest.EngineeringKind, display = latest.Value, status = quality, diagnostic = latest.Diagnostic, sequence = latest.Sequence,
                        timeUtc = BeijingTime.Iso(latest.Time), sourcePort = latest.SourcePort } };
            }
        }
    }
}
