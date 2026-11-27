using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Monitor : IDisposable
    {
        public readonly object Gate = new object();
        private readonly object lifecycleGate = new object();
        private bool stoppingTogether;
        public Settings Config;
        public List<Parameter> Catalog { get; private set; }
        public readonly Dictionary<string, Reading> Values = new Dictionary<string, Reading>();
        private readonly Dictionary<string, SourceState> sources = new Dictionary<string, SourceState>();
        private readonly Dictionary<string, SequenceTracker> sequences = new Dictionary<string, SequenceTracker>();
        private readonly Queue<Notice> events = new Queue<Notice>();
        private readonly string root;
        private UdpClient socket;
        private Thread receiver;
        private List<string> multicastInterfaces = new List<string>();
        private readonly Timer health;
        private DateTime started;
        private byte[] lastPacket;
        private byte[] lastDatagram;
        private object lastDatagramInfo;
        private object packetInfo;
        private long generation;
        private long noticeId;
        private long liveReadingRevision;
        private readonly HashSet<string> recentRejectionMessages = new HashSet<string>(StringComparer.Ordinal);
        private double rejectionWindowEnd;
        public bool Listening { get; private set; }
        public bool Demo { get; private set; }
        public long Received, Accepted, Invalid, Duplicates, Lost, OutOfOrder, Filtered, Bytes;
        private double processingDelayMs,maxProcessingDelayMs;
        public string Error = "";
        public NetworkReplay NetworkSender { get; private set; }
        public SendMonitor Outgoing { get; private set; }

        public Monitor(string directory) : this(directory, false) {}

        internal Monitor(string directory, bool configurationEditing)
        {
            root = directory;
            ConfigurationEditing = configurationEditing;
            CommandEditFiles.Recover(Path.Combine(root,"config"));
            string settingsPath = Path.Combine(root, "config", "server.json");
            Config = JsonFile.Load<Settings>(settingsPath);
            bool checksumChanged = Config.ChecksumMode != SppChecksum.Mode || Config.ChecksumStart != 0;
            bool timeoutMissing = Config.DataTimeoutMs == 0;
            Config.Validate(); if (checksumChanged || timeoutMissing) JsonFile.Save(settingsPath, Config);
            Catalog = JsonFile.Load<List<Parameter>>(Path.Combine(root, "config", "parameters.json"));
            if (Catalog.Select(p => p.Id).Distinct().Count() != Catalog.Count) throw new ArgumentException("参数ID重复");
            LoadDecoder();
            if (!ConfigurationEditing)
            {
                alarms = new AlarmJournal(Path.Combine(root, "data", "alarm-history.jsonl"));
                OpenJudgmentAbnormalHistory();
                OpenUnifiedAbnormalReleases();
            }
            NetworkSender = new NetworkReplay((level, message) => Note(level, message));
            Outgoing = new SendMonitor(root, OutgoingNote);
            LoadTCBindings();
            LoadContinuousRules();
            InitializeDefaultRules();
            InitializeSequences();
            MigratePssCommandNumbers();
            if (ConfigurationEditing) return;
            InitializeJudgmentArchives();
            health = new Timer(CheckHealth, null, 1000, 250);
            InitializeJudgmentTimer();
            Note("info", "系统已启动；UDP端口需按实际设备填写，当前未监听。");
            if (alarms.RecoveryMessage.Length > 0) Note("warn", alarms.RecoveryMessage);
        }

        public void Note(string level, string message, bool recordAlarm = true)
        {
            if (ConfigurationEditing) return;
            lock (Gate)
            {
                Notice n = new Notice { Id = ++noticeId, Time = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), Level = level, Message = message };
                events.Enqueue(n); while (events.Count > 100) events.Dequeue();
                if (recordAlarm && (level == "warn" || level == "error")) RecordAlarm(level, "运行告警", message);
                try
                {
                    string dir = Path.Combine(root, "logs"); Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, BeijingTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), JsonFile.Encode(n) + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (IOException) { Error = "日志写入失败，请检查磁盘空间及目录权限"; RecordAlarm("error", "日志异常", Error); }
                catch (UnauthorizedAccessException) { Error = "日志目录不可写"; RecordAlarm("error", "日志异常", Error); }
            }
        }

        private void Reset()
        {
            Values.Clear(); engineeringHistory.Clear(); ResetContinuous(); sources.Clear(); sequences.Clear(); lastPacket = null; packetInfo = null; lastDatagram = null; lastDatagramInfo = null;
            packetCounters.Clear(); packetHealth.Clear(); packetStatisticsMode = "";
            generation++;
            Received = Accepted = Invalid = Duplicates = Lost = OutOfOrder = Filtered = Bytes = 0;
            processingDelayMs=maxProcessingDelayMs=0;
            started = DateTime.UtcNow; Error = ""; recentRejectionMessages.Clear(); rejectionWindowEnd=0;
        }

        public void Start()
        {
            lock (lifecycleGate)
            {
                lock (Gate) if (Listening || Demo || Outgoing.IsActive) throw new InvalidOperationException("请先停止当前监听或演示");
                // Finish a failed worker's final log before beginning another generation.
                Outgoing.Stop();
                UdpClient pending = null; bool captureStarted = false;
                try
                {
                    lock (Gate)
                    {
                        if (Listening || Demo || Outgoing.IsActive) throw new InvalidOperationException("请先停止当前监听或演示");
                        Config.Validate();
                        if (Config.UdpPort == 0) throw new ArgumentException("请先填写设备实际UDP目的端口，并保存发送监视的IP-A、IP-B和网卡配置");
                        pending = new UdpClient(AddressFamily.InterNetwork);
                        bool multicast = !String.IsNullOrEmpty(Config.MulticastGroup);
                        pending.ExclusiveAddressUse = !multicast;
                        if (multicast) pending.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        pending.Client.ReceiveBufferSize = 4 * 1024 * 1024;
                        pending.Client.ReceiveTimeout = 0;
                        pending.Client.Bind(new IPEndPoint(IPAddress.Parse(Config.BindAddress), Config.UdpPort));
                        List<string> joined = UdpMulticast.Join(pending, Config);
                        BeginHistorySession("LIVE_UDP");
                        Outgoing.Start(); captureStarted = true;
                        if (!Outgoing.IsRunning) throw new InvalidOperationException("发送抓包未成功启动，本次监听已撤回，请检查发送监视诊断");
                        socket = pending; hotSocketSettings=null;multicastInterfaces = joined; Reset(); recordedRunAlarms.Clear(); packetStatisticsMode = "live"; Listening = true;
                        foreach (string ip in Config.SourceIPs) sources[ip] = new SourceState { IP = ip };
                        UdpClient receiving = pending;
                        receiver = new Thread(delegate() { Receive(receiving); }) { IsBackground = true, Name = "UDP receiver" }; receiver.Start();
                        Note("info", "UDP接收与发送报文监视已同时启动；UDP " + Config.BindAddress + ":" + Config.UdpPort + "；来源白名单 " + String.Join(", ", Config.SourceIPs));
                        if (joined.Count > 0) Note("info", "已加入组播组 " + Config.MulticastGroup + ":" + Config.UdpPort + "；本机网卡 " + String.Join(", ", joined));
                    }
                }
                catch
                {
                    lock (Gate)
                    {
                        if (pending != null && socket == pending) { Listening = false; socket = null; multicastInterfaces.Clear(); generation++; }
                        if (pending != null) pending.Close();
                    }
                    lock (Gate) EndHistorySession("监听启动失败");
                    // Capture logging uses Gate; join only after releasing it.
                    if (captureStarted) Outgoing.Stop();
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (lifecycleGate)
            {
                Thread old, oldReplay;
                lock (Gate)
                {
                    stoppingTogether = true;
                    Listening = false; Demo = false; generation++; StopJudgmentCore("监听停止，判读已自动停止"); StopSequence("监听停止，序列已自动终止");
                    replayCancel.Set(); oldReplay = replayThread; replayThread = null;
                    if (replayState == "playing") replayState = "stopped";
                    if (replayClock != null) replayClock.Stop();
                    if (socket != null) { socket.Close(); socket = null; }
                    multicastInterfaces.Clear(); old = receiver; receiver = null;
                }
                try
                {
                    if (old != null && old != Thread.CurrentThread) old.Join(1500);
                    if (oldReplay != null && oldReplay != Thread.CurrentThread) oldReplay.Join(1500);
                    if (Outgoing != null) Outgoing.Stop();
                    Note("info", "UDP接收与发送报文监视已全部停止，保留本次参数、统计和最近200条发送记录；再次启动将重置。");
                }
                finally { lock (Gate) { stoppingTogether = false; EndHistorySession("接收已停止"); } }
            }
        }
        private void OutgoingNote(string level, string message)
        {
            Note(level, message);
            if (level == "error")
            {
                lock (Gate)
                {
                    if (Listening) { Error = message; StopAfterFailure(); }
                }
            }
        }
        // Called with Gate held. Stop on another thread so neither reader joins itself.
        private void StopAfterFailure()
        {
            long expected = generation;
            ThreadPool.QueueUserWorkItem(delegate(object unused)
            {
                lock (lifecycleGate)
                {
                    lock (Gate) if (generation != expected) return;
                    try { Stop(); }
                    catch (Exception ex) { Note("error", "统一停止失败，请再次点击停止接收：" + ex.Message); }
                }
            });
        }

        private void Receive(UdpClient client)
        {
            try
            {
                IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    try
                    {
                        byte[] data = client.Receive(ref peer);
                        EventStamp stamp = EventStamp.Now();
                        lock (Gate)
                        {
                            if (!Listening || socket != client) return;
                            ProcessReceived(data,peer.Address.ToString(),false,peer.Port,0,stamp,
                                Config.MulticastGroup.Length>0 ? Config.MulticastGroup : Config.BindAddress);
                        }
                    }
                    catch (SocketException ex)
                    {
                        lock (Gate)
                        {
                            if (!Listening || socket != client) return;
                            if (ex.SocketErrorCode != SocketError.TimedOut)
                            {
                                Error = "UDP接收异常：" + ex.SocketErrorCode;
                                Note("error", Error); Listening = false; client.Close(); socket = null; StopAfterFailure(); return;
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                lock (Gate) { if (!Listening || socket != client) return; Listening = false; Error = "监听线程退出：" + ex.Message; Note("error", Error); StopAfterFailure(); }
            }
        }

        private void Process(byte[] data,string ip,bool simulated,int sourcePort=0,long captureFrame=0)
        {ProcessReceived((byte[])data.Clone(),ip,simulated,sourcePort,captureFrame,EventStamp.Now());}
        // Socket and capture readers hand over a fresh array. Retained records share it as read-only.
        private void ProcessReceived(byte[] data, string ip, bool simulated, int sourcePort, long captureFrame, EventStamp stamp, string destination = "")
        {
            // Keep the actual receive timestamp even when processing waited for Gate.
            if (!simulated && stamp != null && stamp.DataSeconds > 0)
            {
                processingDelayMs = Math.Max(0, (RuleSeconds() - stamp.DataSeconds) * 1000);
                maxProcessingDelayMs = Math.Max(maxProcessingDelayMs, processingDelayMs);
            }
            var historyRecord = BeginHistoryPacket(data, ip, simulated, sourcePort, captureFrame, stamp, destination);
            HistoryValue[] historySamples = null;
            int historySampleCount = 0;
            try
            {
                Received++; Bytes += data.Length;
                if (!simulated && Array.IndexOf(Config.SourceIPs, ip) < 0)
                {
                    Filtered++;
                    if (historyRecord != null) historyRecord.Status = "FILTERED_SOURCE";
                    return;
                }
                SourceState src;
                if (!sources.TryGetValue(ip, out src)) sources[ip] = src = new SourceState { IP = ip };
                src.Received++; src.LastReceived = DateTime.UtcNow;
                lastDatagram = data;
                string receiveTime = BeijingTime.Format(stamp.ReceivedUtc,"HH:mm:ss.fff");
                Settings cfg = Config;
                Packet p = null;
                PacketRoute route = null;
                PacketDefinition definition = null;
                PacketDecodePlan plan = null;
                LivePacketCounters packetCount = null;
                string parseError = null;
                try
                {
                    if (NativeDecoder)
                    {
                        bool rawValid;
                        Packet rawParsed;
                        bool rawMatched = CaptureRawSpp(lastDatagram, ip, simulated, sourcePort, captureFrame, out rawValid, out rawParsed);
                        route = PacketCodec.Resolve(data, ip, RuntimeRouteCandidates(data, false));
                        if (rawMatched && route.Definition == null && !route.Ambiguous)
                        {
                            FillHistoryPacket(historyRecord, rawParsed);
                            if (historyRecord != null) historyRecord.Status = rawValid ? "RAW_VALID" : "RAW_INVALID";
                            if (rawValid) { src.LastValid = DateTime.UtcNow; src.OfflineConditionActive = false; }
                            lastDatagramInfo = new { length = data.Length, time = receiveTime, result = "SPP原包已独立留存；未参与遥测解码" };
                            return;
                        }
                        if (route.Definition == null) parseError = route.Reason;
                        else
                        {
                            definition = route.Definition;
                            packetCount = PacketCountersFor(definition.Id);
                            packetCount.Received++;
                            packetCount.LastMatch=stamp.ObservedUtc;
                            p = rawParsed != null && rawParsed.PacketId == definition.Id ? rawParsed : PacketCodec.ParseAt(data, definition, route.Offset);
                            plan = RuntimeDecodePlan(definition.Id);
                            cfg = plan.Settings;
                        }
                    }
                    else
                    {
                        p = Protocol.Parse(data, cfg);
                        packetCount = PacketCountersFor("legacy-apid-" + p.Apid);
                        packetCount.Received++;
                        packetCount.LastMatch=stamp.ObservedUtc;
                    }
                }
                catch (ArgumentException ex) { parseError = ex.Message; }

                if (parseError != null)
                {
                    PacketRoute rejectedRoute = null;
                    if (definition == null)
                        rejectedRoute = NativeDecoder ? route ?? PacketCodec.Resolve(data, ip, RuntimeRouteCandidates(data, false))
                            : PacketCodec.Resolve(data, ip, LegacyStatisticsDefinitions());
                    bool unrecognized = rejectedRoute != null && rejectedRoute.Definition == null && !rejectedRoute.Ambiguous &&
                        !(rejectedRoute.Offset >= 0 && data.Length - rejectedRoute.Offset < 16);
                    if (!unrecognized)
                        BreakJudgmentContinuity(ip, definition != null ? definition.Id : rejectedRoute != null && rejectedRoute.Definition != null ? rejectedRoute.Definition.Id : "");
                    if (!NativeDecoder && rejectedRoute.Definition != null)
                    {
                        packetCount = PacketCountersFor(rejectedRoute.Definition.Id);
                        packetCount.Received++;
                        packetCount.LastMatch=stamp.ObservedUtc;
                    }
                    if (packetCount != null)
                    {
                        packetCount.Invalid++;
                        packetCount.LastOutcome=parseError;
                    }
                    if (historyRecord != null)
                    {
                        var failedDefinition = definition ?? (rejectedRoute == null ? null : rejectedRoute.Definition);
                        if (failedDefinition != null)
                        {
                            historyRecord.PacketId = failedDefinition.Id;
                            historyRecord.PacketName = failedDefinition.Name;
                            historyRecord.Apid = failedDefinition.Apid;
                        }
                        historyRecord.Status = unrecognized ? "UNRECOGNIZED" : "INVALID";
                        historyRecord.Reason = parseError;
                    }
                    Invalid++; src.Invalid++;
                    RecordAbnormal(lastDatagram, ip, simulated, sourcePort, captureFrame, parseError, definition, !unrecognized, NativeDecoder ? route ?? rejectedRoute : null);
                    string rejection = unrecognized ? "SPP未识别（信息，不告警）：" + parseError : parseError;
                    lastDatagramInfo = new { length = data.Length, time = receiveTime, result = rejection };
                    if (stamp.DataSeconds >= rejectionWindowEnd)
                    {
                        rejectionWindowEnd = stamp.DataSeconds + 5;
                        recentRejectionMessages.Clear();
                    }
                    // Keep all counters and raw records. Bound synchronous run-log writes when invalid packets alternate.
                    if (recentRejectionMessages.Count < 32 && recentRejectionMessages.Add(parseError))
                        Note(unrecognized ? "info" : "warn", rejection, false);
                    return;
                }
                src.LastValid = DateTime.UtcNow; src.OfflineConditionActive = false;
                FillHistoryPacket(historyRecord, p);
                if (historyRecord != null) historyRecord.Status = "VALID_NEW";
                string key = ip + ":" + p.Apid + (NativeDecoder ? ":" + p.PacketId : "");
                SequenceTracker tracker;
                if (!sequences.TryGetValue(key, out tracker)) sequences[key] = tracker = new SequenceTracker();
                int disposition = tracker.Accept(p.Sequence);
                packetCount.LastSequence = p.Sequence;
                if (disposition == 1)
                {
                    if (historyRecord != null) historyRecord.Status = "DUPLICATE";
                    Duplicates++; src.Duplicates++; packetCount.Duplicates++;
                    packetCount.LastOutcome="帧计数重复，已跳过解码";
                    lastDatagramInfo = new { length = data.Length, time = receiveTime, result = packetCount.LastOutcome };
                    RecordAlarm("warn", "重复帧", "帧计数重复，已忽略：APID=" + p.Apid + "，计数=" + p.Sequence, ip, p.PacketName ?? ("APID " + p.Apid), p.Apid, p.Sequence, null, captureFrame, p.PacketId ?? "legacy");
                    return;
                }
                if (tracker.Reset)
                {
                    OutOfOrder++; src.OutOfOrder++; packetCount.OutOfOrder++;
                    packetCount.LastResyncFrom = tracker.Previous; packetCount.LastResyncTo = p.Sequence; packetCount.LastResyncAt = BeijingTime.Now;
                    string message = "帧计数回退或大幅跳变，已重新同步并继续解码：APID=" + p.Apid + "，原计数=" + tracker.Previous + "，当前计数=" + p.Sequence;
                    if (historyRecord != null) historyRecord.Reason = message;
                    bool newOrderAlarm = RecordAlarm("warn", "计数重同步", message, ip, p.PacketName ?? ("APID " + p.Apid), p.Apid, p.Sequence, null, captureFrame, p.PacketId ?? "legacy");
                    if (newOrderAlarm) Note("warn", message, false);
                    BreakJudgmentContinuity(ip, NativeDecoder ? p.PacketId : "legacy-apid-" + p.Apid);
                }
                if (tracker.Lost > 0)
                {
                    BreakJudgmentContinuity(ip, NativeDecoder ? p.PacketId : "legacy-apid-" + p.Apid);
                    Lost += tracker.Lost; src.Lost += tracker.Lost;
                    string message = "帧计数不连续：APID=" + p.Apid + "，疑似缺失" + tracker.Lost + "帧（非已确认网络丢包）";
                    bool newLossAlarm = RecordAlarm("warn", "疑似缺帧", message, ip, p.PacketName ?? ("APID " + p.Apid), p.Apid, p.Sequence, null, captureFrame, p.PacketId ?? "legacy");
                    if (newLossAlarm) Note("warn", message, false);
                    if (Config.EnableLossAlarm && tracker.Lost >= Config.LossAlarmFrames) src.LossLatched = newLossAlarm || alarms.HasPending(ip, "疑似缺帧");
                }
                Accepted++; src.Accepted++; packetCount.Accepted++;
                bool validPacket = p.ChecksumVerified && String.IsNullOrEmpty(p.Warning);
                if (historyRecord != null && !validPacket)
                {
                    historyRecord.Status = "UNVERIFIED";
                    historyRecord.Reason = p.Warning ?? "";
                }
                if (validPacket)
                {
                    packetCount.LastValidFrame=stamp.ObservedUtc;
                    RecordPacketFreshness(NativeDecoder ? definition.Id : "legacy-apid-" + p.Apid, ip);
                }
                string outcome = validPacket ? "SPP已识别；校验通过" : !String.IsNullOrEmpty(p.Warning) ? p.Warning : "SPP校验待核实";
                lastDatagramInfo = new { length = data.Length, time = receiveTime, result = outcome };
                bool decode = (cfg.MappingConfirmed || simulated) && validPacket && (NativeDecoder || p.Apid == cfg.ParameterApid);
                if (!String.IsNullOrEmpty(p.Warning)) RecordAbnormal(lastDatagram, ip, simulated, sourcePort, captureFrame, p.Warning, definition, resolved: route);
                if (validPacket)
                {
                    lastPacket = data;
                    packetInfo = new { apid = p.Apid, sequence = p.Sequence, datagramLength = data.Length, sppOffset = p.Offset, sppLength = p.Length,
                        checksum = "通过", decoding = decode, warning = p.Warning, packetId = p.PacketId, packetName = p.PacketName, time = receiveTime };
                }
                packetCount.LastOutcome = !validPacket ? p.Warning ?? "校验未通过" : "有效新帧，未启用参数解码";
                if (decode)
                {
                    if (plan == null) EnsureRuntimeIndexes();
                    var fields = plan == null ? runtimeLegacyFields : plan.Fields;
                    var changedParameters = plan == null ? runtimeLegacyChanged : plan.Changed;
                    var stream = fields.Length == 0 ? null : EngineeringStream(ip);
                    List<string> invalidFields = null;
                    bool updatedParameters = false;
                    DateTime now = stamp.ObservedUtc;
                    double observed = stamp.DataSeconds, dataTime = Demo ? replayCaptureMs / 1000 : observed;
                    string timeUtc = BeijingTime.Iso(now);
                    if (historyRecord != null && fields.Length > 0) historySamples = new HistoryValue[fields.Length];
                    string packetEventId = historyRecord == null ? Guid.NewGuid().ToString("N") : historyRecord.Id;
                    long displayRevision = fields.Length == 0 ? 0 : Interlocked.Increment(ref liveReadingRevision);
                    foreach (Parameter def in fields)
                    {
                        Reading reading = Protocol.Decode(data, p, def, cfg, now);
                        reading.DisplayRevision = displayRevision;
                        Values[def.Id] = reading;
                        reading.SourceIP = ip; reading.SourcePort = sourcePort; reading.PacketEventId = packetEventId;
                        RecordEngineering(stream, reading, observed, dataTime, timeUtc);
                        if (historySamples != null) historySamples[historySampleCount++] = HistorySample(reading);
                        if (reading.Status != "有效")
                        {
                            if (invalidFields == null) invalidFields = new List<string>();
                            if (invalidFields.Count < 8) invalidFields.Add(def.Code + "：" + reading.Diagnostic);
                        }
                        else updatedParameters = true;
                    }
                    if (updatedParameters) packetCount.LastParameterUpdate=stamp.ObservedUtc;
                    packetCount.LastOutcome = invalidFields != null
                        ? (updatedParameters ? "部分参数解码异常：" : "参数解码异常：") + invalidFields[0]
                        : updatedParameters ? "参数已更新" : "未关联可解码参数";
                    if (invalidFields != null)
                        RecordAbnormal(lastDatagram, ip, simulated, sourcePort, captureFrame, "参数解码异常：" + String.Join("；", invalidFields), definition, resolved: route);
                    EvaluateContinuous(ip, changedParameters);
                }
            }
            catch (Exception ex)
            {
                if (historyRecord != null) { historyRecord.Reason = ex.Message; historyRecord.Status = "PROCESSING_ERROR"; }
                throw;
            }
            finally { FinishHistoryPacket(historyRecord, historySamples, historySampleCount); }
        }

        private void CheckHealth(object state)
        {
            lock (Gate)
            {
                if (!Listening||ApplyingConfiguration) return;
                CheckPacketHealth(DateTime.UtcNow);
                foreach (SourceState src in sources.Values)
                {
                    if(!Config.SourceIPs.Contains(src.IP))continue;
                    DateTime since = src.LastValid == DateTime.MinValue ? started : src.LastValid;
                    DateTime configured;if(configurationSourceSince.TryGetValue(src.IP,out configured)&&configured>since)since=configured;
                    if ((DateTime.UtcNow - since).TotalMilliseconds > Config.DataTimeoutMs && !src.OfflineConditionActive)
                    {
                        src.OfflineConditionActive = true;
                        string message = "设备有效数据超时：" + src.IP + "；可直接确认告警，历史记录保留";
                        bool newTimeoutAlarm = RecordAlarm("error", "设备超时", message, src.IP);
                        src.OfflineLatched = newTimeoutAlarm || alarms.HasPending(src.IP, "设备超时");
                        Note("error", message, false);
                    }
                }
            }
        }

        public void Acknowledge(string username, string actor)
        {
            lock (Gate)
            {
                alarms.AcknowledgeAll(username, actor);
                foreach (SourceState s in sources.Values) { s.OfflineLatched = false; s.LossLatched = false; }
                Error = "";
                Note("info", "当前告警已确认，历史记录保留；确认人：" + actor);
            }
        }

        private sealed class LiveSnapshotRow
        {
            public string id, value, raw, hexSource, status, time;
            public int? sequence;
            internal DateTime ReceivedAt;
        }

        internal sealed class LiveReadingCursor
        {
            internal long Revision, Generation;
        }

        public object Snapshot(List<string> selected) { return Snapshot(selected, null); }

        internal object Snapshot(List<string> selected, LiveReadingCursor cursor)
        {
            var rows = new LiveSnapshotRow[selected.Count];
            object snapshot;
            lock (Gate)
            {
                EnsureRuntimeIndexes();
                DateTime now = DateTime.UtcNow;
                double dataNow = HistorySeconds();
                for (int i = 0; i < selected.Count; i++)
                {
                    string id = selected[i];
                    Reading v;
                    if (!Values.TryGetValue(id, out v)) rows[i] = new LiveSnapshotRow { id = id, value = "—", raw = "", hexSource = "", status = Demo || (NativeDecoder ? ReadyFor(id) : Config.MappingConfirmed) ? "待接收" : "待核实", time = "—" };
                    else rows[i] = SnapshotReading(v, dataNow, now);
                }
                if (cursor != null)
                {
                    cursor.Revision = liveReadingRevision;
                    cursor.Generation = generation;
                }
                snapshot = new { mode = Demo ? "demo" : Listening ? "live" : "stopped", serverTime = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    monitoringState = stoppingTogether ? "stopping" : Listening ? "running" : "stopped", received = Received, accepted = Accepted, invalid = Invalid, duplicates = Duplicates, lost = Lost, outOfOrder = OutOfOrder, filtered = Filtered, bytes = Bytes, sendMonitor = Outgoing.Info(),
                    elapsed = started == DateTime.MinValue ? 0 : (now - started).TotalSeconds, error = Error, packet = packetInfo, rows = rows,
                    processingDelayMs=Math.Round(processingDelayMs,3),maxProcessingDelayMs=Math.Round(maxProcessingDelayMs,3),
                    sources = sources.Values.Select(s => new { ip = s.IP, received = s.Received, accepted = s.Accepted,
                        online = s.LastValid != DateTime.MinValue && (now - s.LastValid).TotalMilliseconds <= Config.DataTimeoutMs,
                        alarm = s.OfflineLatched || s.LossLatched, lastTime = s.LastValid == DateTime.MinValue ? "—" : BeijingTime.Format(s.LastValid,"HH:mm:ss.fff") }).ToArray(),
                    events = events.Reverse().Take(30).ToArray(), profile = NativeDecoder ? "自定义SPP包" : Config.Profile, port = Config.UdpPort,
                    mappingReady = runtimeMappingReady,
                    activeParameters = runtimeActiveParameters, decoderRevision = DecoderRevision, nativeDecoder = NativeDecoder,
                    multicastGroup = Config.MulticastGroup, multicastInterfaces = Listening ? multicastInterfaces.ToArray() : new string[0],
                    packetStatistics = PacketStatisticsSnapshot(), packetStatisticsMode = packetStatisticsMode,
                    packetStatisticsStartedAt = packetStatisticsMode == "" ? "" : BeijingTime.Format(started,"yyyy-MM-dd HH:mm:ss"),
                    alarmHistory = alarms.Summary(alarmStorageError),
                    networkReplay = NetworkSender.Info(),
                    rawSppRevision=rawSppRevision,rawSppRetained=RawSppRetained,rawSppEnabled=runtimeRawEnabled,rawSppOnly=runtimeRawOnly,
                    replay = ReplayInfo(), liveAbnormalCount = liveAbnormal.Count, liveAbnormalRevision = liveAbnormalRevision };
            }
            FormatSnapshotTimes(rows);
            return snapshot;
        }

        // Skip the monitor lock when no frames containing decoded parameters have arrived.
        internal object LatestReadingUpdates(List<string> selected, LiveReadingCursor cursor)
        {
            if (Interlocked.Read(ref liveReadingRevision) == cursor.Revision) return null;
            var rows = new List<LiveSnapshotRow>();
            lock (Gate)
            {
                // The following full snapshot reestablishes state after a restart or configuration change.
                if (generation != cursor.Generation || (!Listening && !Demo)) return null;
                DateTime now = DateTime.UtcNow;
                double dataNow = HistorySeconds();
                foreach (string id in selected)
                {
                    Reading reading;
                    if (Values.TryGetValue(id, out reading) && reading.DisplayRevision > cursor.Revision)
                        rows.Add(SnapshotReading(reading, dataNow, now));
                }
                cursor.Revision = liveReadingRevision;
            }
            if (rows.Count == 0) return null;
            FormatSnapshotTimes(rows);
            return rows;
        }

        private LiveSnapshotRow SnapshotReading(Reading reading, double dataNow, DateTime now)
        {
            return new LiveSnapshotRow { id = reading.Id, value = reading.Value, raw = reading.Raw, hexSource = reading.HexSource,
                status = (!Listening && !Demo) ? "已停止" : DisplayReadingStatus(reading, dataNow, now),
                ReceivedAt = reading.Time, sequence = reading.Sequence };
        }

        private static void FormatSnapshotTimes(IEnumerable<LiveSnapshotRow> rows)
        {
            var times = new Dictionary<DateTime,string>();
            foreach (var row in rows)
            {
                if (!row.sequence.HasValue) continue;
                string time;
                if (!times.TryGetValue(row.ReceivedAt, out time)) times[row.ReceivedAt] = time = BeijingTime.Format(row.ReceivedAt,"HH:mm:ss.fff");
                row.time = time;
            }
        }

        public object PacketPreview()
        {
            object info, rawInfo;
            byte[] packet, datagram;
            lock (Gate) { info = packetInfo; rawInfo = lastDatagramInfo; packet = lastPacket; datagram = lastDatagram; }
            string hex = packet == null ? "" : BitConverter.ToString(packet).Replace('-', ' ');
            string rawHex = Object.ReferenceEquals(packet, datagram) ? hex : datagram == null ? "" : BitConverter.ToString(datagram).Replace('-', ' ');
            return new { info, hex, rawInfo, rawHex };
        }

        public void SaveSettings(Settings settings)
        {
            lock (Gate)
            {
                if (Listening || Demo || Outgoing.IsActive || stoppingTogether) throw new InvalidOperationException("请先停止接收，再修改配置");
                settings.Validate();
                settings.WebPort = Config.WebPort; settings.WebHost = Config.WebHost;
                JsonFile.Save(Path.Combine(root, "config", "server.json"), settings); Config = settings;
                if (!NativeDecoder) legacyRevision = Guid.NewGuid().ToString("N");
                Values.Clear(); engineeringHistory.Clear(); ResetContinuous(); packetInfo = null; lastPacket = null; lastDatagram = null; lastDatagramInfo = null;
                Note("info", "监听及协议配置已保存，当前值已清空");
            }
        }

        public void Dispose() { if(!ConfigurationEditing)Stop(); if(judgmentTimer!=null)judgmentTimer.Dispose(); if (Outgoing != null) Outgoing.Dispose(); if (NetworkSender != null) NetworkSender.Dispose(); if(health!=null)health.Dispose(); DisposeReplay(); ClearLiveAbnormal(); if (judgmentAbnormals != null) judgmentAbnormals.Dispose(); if (unifiedReleases != null) unifiedReleases.Dispose(); if (alarms != null) alarms.Dispose(); }
    }
}
