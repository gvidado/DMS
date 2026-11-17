using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed class SendMonitorSettings
    {
        public string SourceIP = "192.168.1.16", DestinationIP, InterfaceId = "";
        public string[] SourceIPs, DestinationIPs;
        public int DestinationPort;
        public void Validate()
        {
            SourceIPs = Hosts(SourceIPs ?? new[] { SourceIP }, "来源IP列表"); DestinationIPs = Hosts(DestinationIPs ?? (DestinationIP == null ? new[] { "192.168.1.111", "192.168.1.112", "192.168.1.113", "192.168.1.137" } : new[] { DestinationIP }), "目的IP列表");
            SourceIP = SourceIPs[0]; DestinationIP = DestinationIPs[0]; InterfaceId = (InterfaceId ?? "").Trim();
            if (SourceIPs.Any(ip => IPAddress.Parse(ip).GetAddressBytes()[0] >= 224)) throw new ArgumentException("来源IP列表应填写本机单播IPv4地址");
            if (DestinationPort < 0 || DestinationPort > 65535) throw new ArgumentException("目的端口应为0（全部UDP端口）或1～65535");
        }
        private static string[] Hosts(string[] values, string field)
        {
            var result = new List<string>(); var seen = new HashSet<string>();
            foreach (string value in values)
                foreach (string item in (value ?? "").Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ip = Host(item, field); if (seen.Add(ip)) result.Add(ip);
                    if (result.Count > 64) throw new ArgumentException(field + "最多64个不同IPv4地址");
                }
            if (result.Count == 0) throw new ArgumentException(field + "至少填写一个IPv4地址");
            return result.ToArray();
        }
        private static string Host(string value, string field)
        {
            IPAddress ip;
            if (!IPAddress.TryParse((value ?? "").Trim(), out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any)) throw new ArgumentException(field + "应为有效IPv4地址");
            return ip.ToString();
        }
    }
    internal sealed class SendRecord
    {
        internal string HistoryEventId=Guid.NewGuid().ToString("N");
        internal long ObservedMonotonic=System.Diagnostics.Stopwatch.GetTimestamp();
        internal string CaptureInterfaceId="";
        internal bool CaptureTimeKnown=true;
        public long Id;
        public DateTime Time;
        public string SourceIP, DestinationIP;
        public int SourcePort, DestinationPort;
        public byte[] Payload, NetworkFrame;
    }
    public sealed partial class SendMonitor : IDisposable
    {
        public const int RecentRecordLimit = 200;
        private readonly object gate = new object();
        private readonly ManualResetEvent cancel = new ManualResetEvent(false);
        private readonly Queue<SendRecord> recent = new Queue<SendRecord>();
        private readonly string settingsPath;
        private readonly Action<string, string> note;
        public readonly ObservedCommands Commands;
        internal event Action<SendRecord> Captured;
        internal bool CanObserve(string source,string destination){lock(gate)return settings.SourceIPs.Contains(source) && settings.DestinationIPs.Contains(destination);}
        internal bool CanObserveSource(string source){lock(gate)return settings.SourceIPs.Contains(source);}
        private SendMonitorSettings settings;
        private Thread worker;
        private string state = "stopped", error = "", started = "", adapterName = "";
        private long total, bytes, revision, unsupported, truncated, epoch, nextRecordId;
        private uint[] statistics;
        public SendMonitor(string root, Action<string, string> logger)
        {
            note = logger; settingsPath = Path.Combine(root, "config", "send-monitor.json");
            settings = File.Exists(settingsPath) ? JsonFile.Load<SendMonitorSettings>(settingsPath) : new SendMonitorSettings(); settings.Validate();
            Commands = new ObservedCommands(root);
            if (Commands.Error != "") note("warn", Commands.Error);
        }
        private bool Active { get { return state == "running" || state == "stopping"; } }
        public bool IsActive { get { lock (gate) return Active; } }
        public bool IsRunning { get { lock (gate) return state == "running"; } }
        public static object Interfaces()
        {
            try { CaptureAdapter[] adapters = NativeCapture.Adapters(); return new { available = true, driver = "Npcap/pcap", library = NativeCapture.LibraryPath, error = "", interfaces = adapters }; }
            catch (InvalidOperationException ex) { return new { available = false, driver = "", library = "", error = ex.Message, interfaces = new CaptureAdapter[0] }; }
        }
        private static SendMonitorSettings Clone(SendMonitorSettings value) { return JsonFile.Decode<SendMonitorSettings>(JsonFile.Encode(value)); }
        private static bool Belongs(CaptureAdapter adapter, string ip)
        { return adapter.addresses.Contains(ip) || (adapter.loopback && IPAddress.IsLoopback(IPAddress.Parse(ip))); }
        public object Info()
        {
            lock (gate) return new { state = state, error = error, configuration = Clone(settings), total = total, bytes = bytes, retained = recent.Count, capacity = RecentRecordLimit, revision = revision,
                startedAt = started, adapter = adapterName, unsupported = unsupported, truncated = truncated, commands = Commands.Info(),
                dropped = statistics == null ? (long?)null : statistics[1], interfaceDropped = statistics == null ? (long?)null : statistics[2] };
        }
        private void Reset()
        { total = bytes = unsupported = truncated = 0; recent.Clear(); statistics = null; started = ""; error = ""; revision++; }
        public void Save(SendMonitorSettings value)
        {
            if (value == null) throw new ArgumentException("缺少发送监视配置"); value = Clone(value); value.Validate();
            lock (gate)
            {
                if (Active) throw new InvalidOperationException("请先点击页面顶部“停止接收”，再修改配置");
                JsonFile.Save(settingsPath, value); settings = value; Reset(); adapterName = ""; state = "stopped";
            }
        }
        public void Start(bool preserveRecords=false)
        {
            SendMonitorSettings options; lock (gate) { if (Active) throw new InvalidOperationException("发送监视已启动或正在停止"); options = Clone(settings); }
            CaptureAdapter[] adapters = NativeCapture.Adapters();
            CaptureAdapter adapter = options.InterfaceId == "" ? adapters.FirstOrDefault(a => options.SourceIPs.All(ip => Belongs(a, ip))) : adapters.FirstOrDefault(a => a.id == options.InterfaceId);
            if (adapter == null) throw new InvalidOperationException("找不到覆盖全部来源IP的抓包网卡。来源IP列表须属于本机同一块网卡，请检查地址或选择网卡");
            string[] missing = options.SourceIPs.Where(ip => !Belongs(adapter, ip)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("以下来源IP不属于所选网卡：" + String.Join(", ", missing) + "。请使用同一网卡的本机地址");
            string expression = "ip and udp and (" + String.Join(" or ", options.SourceIPs.Select(ip => "src host " + ip)) + ") and (" + String.Join(" or ", options.DestinationIPs.Select(ip => "dst host " + ip)) + ")";
            if (options.DestinationPort != 0) expression += " and dst port " + options.DestinationPort;
            IntPtr handle = NativeCapture.Open(adapter.id, expression);
            int link = NativeCapture.Link(handle);
            // pcap_datalink uses DLT_RAW=12 on Windows; pcapng uses LINKTYPE_RAW=101.
            if (link == 12) link = 101;
            if (!new[] { 0, 1, 101, 108, 113, 228, 276 }.Contains(link)) { NativeCapture.Close(handle); throw new InvalidOperationException("暂不支持该网卡的链路类型：" + link); }
            options.InterfaceId=adapter.id;
            lock (gate)
            {
                if (Active) { NativeCapture.Close(handle); throw new InvalidOperationException("发送监视已启动"); }
                if(!preserveRecords)Reset(); cancel.Reset(); state = "running"; adapterName = adapter.name; if(!preserveRecords||started.Length==0)started = BeijingTime.Format(DateTime.UtcNow,"yyyy-MM-dd HH:mm:ss.fff"); long current = ++epoch;
                worker = new Thread(() => Run(handle, link, options, current)) { IsBackground = true, Name = "Outgoing UDP packet capture" };
                try { worker.Start(); } catch { state = "stopped"; NativeCapture.Close(handle); throw; }
            }
            note("info", "发送报文监视已启动：" + String.Join(", ", options.SourceIPs) + " → " + String.Join(", ", options.DestinationIPs) + "；网卡：" + adapter.name + (preserveRecords?"；保留原计数和记录":"；计数已清零"));
        }
        private void Run(IntPtr handle, int link, SendMonitorSettings options, long current)
        {
            DateTime nextStats = DateTime.UtcNow; bool warnedDrop = false, warnedUnsupported = false; string commandWarning = "";
            var sources = new HashSet<string>(options.SourceIPs); var destinations = new HashSet<string>(options.DestinationIPs);
            try
            {
                while (!cancel.WaitOne(0))
                {
                    IntPtr header, data; int result = NativeCapture.Next(handle, out header, out data);
                    if (result < 0) throw new InvalidOperationException("抓包读取中断：" + NativeCapture.Error(handle));
                    if (result == 0) cancel.WaitOne(10);
                    else
                    {
                        long observedMonotonic=System.Diagnostics.Stopwatch.GetTimestamp();
                        // Windows timeval consists of two 32-bit values on both x86 and x64.
                        uint captured = unchecked((uint)Marshal.ReadInt32(header, 8)), original = unchecked((uint)Marshal.ReadInt32(header, 12));
                        if (captured > 262144) throw new InvalidOperationException("抓包记录长度超出保护范围");
                        byte[] packet = new byte[(int)captured]; Marshal.Copy(data, packet, 0, packet.Length);
                        var frame = new CaptureFrame { LinkType = link }; CaptureReader.ExtractUdp(frame, packet);
                        if (captured < original || frame.Issue != "" || frame.Unsupported || !frame.Udp || frame.Payload == null)
                        {
                            lock (gate) { if (captured < original) truncated++; else unsupported++; }
                            if (!warnedUnsupported) { warnedUnsupported = true; note("warn", "发送监视发现截断、分片或无法解析的UDP报文，未计入完整报文数；请查看发送监视诊断"); }
                        }
                        else
                        {
                            uint seconds=unchecked((uint)Marshal.ReadInt32(header,0));int microseconds=Marshal.ReadInt32(header,4);bool captureTimeKnown=seconds>0&&microseconds>=0&&microseconds<1000000;
                            DateTime timestamp=captureTimeKnown?new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds).AddTicks((long)microseconds*10):EventStamp.PreciseUtcNow();
                            if(!sources.Contains(frame.SourceIP)||!destinations.Contains(frame.DestinationIP)||(options.DestinationPort!=0&&frame.DestinationPort!=options.DestinationPort))continue;
                            // Publish recent rows after the catalog has resolved/collected the command.
                            string commandError = Commands.Observe(frame.SourceIP, frame.DestinationIP, frame.Payload, timestamp);
                            lock (gate)
                            {
                                if (epoch != current) return;
                                total++;
                                recent.Enqueue(new SendRecord { Id = ++nextRecordId, Time = timestamp, ObservedMonotonic=observedMonotonic,CaptureInterfaceId=options.InterfaceId,CaptureTimeKnown=captureTimeKnown,SourceIP = frame.SourceIP, SourcePort = frame.SourcePort, DestinationIP = frame.DestinationIP, DestinationPort = frame.DestinationPort, Payload = frame.Payload, NetworkFrame = packet });
                                while (recent.Count > RecentRecordLimit) recent.Dequeue(); bytes += frame.Payload.Length; revision++;
                            }
                            if (commandError != "" && commandError != commandWarning) note("warn", commandError);
                            commandWarning = commandError;
                            var onCaptured=Captured;if(onCaptured!=null)onCaptured(new SendRecord{Time=timestamp,ObservedMonotonic=observedMonotonic,CaptureInterfaceId=options.InterfaceId,CaptureTimeKnown=captureTimeKnown,SourceIP=frame.SourceIP,SourcePort=frame.SourcePort,DestinationIP=frame.DestinationIP,DestinationPort=frame.DestinationPort,Payload=frame.Payload});
                        }
                    }
                    if (DateTime.UtcNow >= nextStats)
                    {
                        uint[] value = NativeCapture.Statistics(handle); lock (gate) statistics = value; nextStats = DateTime.UtcNow.AddSeconds(1);
                        if (value != null && (value[1] > 0 || value[2] > 0) && !warnedDrop)
                        { warnedDrop = true; note("warn", "发送监视抓包驱动报告丢包，当前计数可能少于实际发送数，请查看抓包诊断"); }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!cancel.WaitOne(0))
                {
                    lock (gate) { if (epoch == current) { state = "failed"; error = ex.Message; revision++; } }
                    note("error", "发送监视异常：" + ex.Message);
                }
            }
            finally
            {
                uint[] value = NativeCapture.Statistics(handle); NativeCapture.Close(handle);
                lock (gate) { if (epoch == current) { statistics = value; if (state != "failed") state = "stopped"; revision++; } }
            }
        }
        public void Stop()
        {
            Thread old; lock (gate) { if (!Active && (worker == null || !worker.IsAlive)) return; if (state != "failed") state = "stopping"; cancel.Set(); old = worker; }
            if (old != null && !old.Join(3000)) throw new InvalidOperationException("发送监视正在停止，请稍后重试");
            note("info", "发送监视已停止，保留总计数和最近200条数据");
        }
        public void Clear()
        { lock (gate) { if (Active) throw new InvalidOperationException("请先点击页面顶部“停止接收”，再清零计数和记录"); Reset(); } }
        private object Summary(SendRecord record)
        {
            var command=Commands.Describe(record.SourceIP,record.DestinationIP,record.Payload);
            return new { id = record.Id, time = BeijingTime.Format(record.Time,"yyyy-MM-dd HH:mm:ss.ffffff"), source = record.SourceIP + ":" + record.SourcePort,
                destination = record.DestinationIP + ":" + record.DestinationPort, length = record.Payload.Length,
                commandMatched=command.Matched,commandCode=command.Code,commandName=command.Name,
                preview = Hex(record.Payload.Take(32).ToArray()) + (record.Payload.Length > 32 ? " …" : "") };
        }
        private static string Hex(byte[] data) { return BitConverter.ToString(data).Replace('-', ' '); }
        public object List()
        { lock (gate) return new { revision = revision, commandRevision=Commands.DisplayRevision, total = total, records = recent.Reverse().Select(Summary).ToArray() }; }
        public object Detail(long id)
        {
            lock (gate)
            {
                SendRecord record = recent.FirstOrDefault(r => r.Id == id);
                if (record == null) throw new ArgumentException("记录已清除或超出最近200条");
                return new { record = Summary(record), udpHex = Hex(record.Payload), frameHex = Hex(record.NetworkFrame) };
            }
        }
        public void WriteCsv(TextWriter writer)
        {
            SendRecord[] records; lock (gate) records = recent.Reverse().ToArray();
            writer.WriteLine("记录号,抓包时间（北京时间）,来源IP,来源端口,目的IP,目的端口,UDP载荷字节数,完整UDP载荷HEX,完整抓包帧HEX,指令编号,指令名称");
            foreach (SendRecord record in records)
            {
                var command=Commands.Describe(record.SourceIP,record.DestinationIP,record.Payload);
                writer.WriteLine(String.Join(",", new[] { record.Id.ToString(CultureInfo.InvariantCulture), Monitor.CsvCell(BeijingTime.Format(record.Time,"yyyy-MM-dd HH:mm:ss.ffffff")), Monitor.CsvCell(record.SourceIP), record.SourcePort.ToString(), Monitor.CsvCell(record.DestinationIP), record.DestinationPort.ToString(), record.Payload.Length.ToString(), Monitor.CsvCell(Hex(record.Payload)), Monitor.CsvCell(Hex(record.NetworkFrame)),Monitor.CsvCell(command.Code),Monitor.CsvCell(command.Name) }));
            }
        }
        public void Dispose() { Stop(); cancel.Dispose(); lock (gate) recent.Clear(); }
    }
}
