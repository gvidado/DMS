using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        internal HistoryStorage History;
        private sealed class HistoryContext { internal string Session,Experiment,Configuration; }
        private volatile HistoryContext historyContext=new HistoryContext{Session="",Experiment="",Configuration=""};
        private void PublishHistoryContext(){historyContext=new HistoryContext{Session=historySession,Experiment=historySessionExperiment,Configuration=historyConfiguration};}
        private string historySession="",historySessionExperiment="",historySessionMode="",historyConfiguration="",historyDecoderRevision="",historyRulesRevision="",historyTCRevision="";
        private Settings historySettings;
        private long historyCommandRevision=-1;
        private string historyImportId="",historyImportConfig="",historyImportFile="",historyImportHash="";
        private long historyImportOffset,historyImportSection=-1;
        private readonly object historyCaptureGate=new object();
        private sealed class HistoryTimingEntry
        {
            internal string Id,Time,DestinationIP,InterfaceId;
            internal long CaptureTicks,ReceivedTicks,MonotonicTicks;
            internal bool Confirmed;
        }
        private readonly Dictionary<string,List<HistoryTimingEntry>> historyReceipts=new Dictionary<string,List<HistoryTimingEntry>>();
        private readonly Queue<Tuple<string,HistoryTimingEntry>> historyReceiptOrder=new Queue<Tuple<string,HistoryTimingEntry>>();
        private readonly Dictionary<string,List<HistoryTimingEntry>> historyPendingReceipts=new Dictionary<string,List<HistoryTimingEntry>>();
        private readonly Queue<Tuple<string,HistoryTimingEntry>> historyPendingReceiptOrder=new Queue<Tuple<string,HistoryTimingEntry>>();
        internal void AttachHistory(HistoryStorage value)
        {
            lock(Gate){History=value;if(value==null)return;Outgoing.Captured+=HistoryCapturedCommand;value.PreparingRecord+=FinalizeHistoryTiming;}
        }
        private void EnsureHistoryConfiguration(bool force=false)
        {
            if(History==null||historySession.Length==0)return;long commandRevision=Outgoing.Commands.BindingRevision;
            if(!force&&historySettings==Config&&historyDecoderRevision==DecoderRevision&&historyRulesRevision==continuous.Revision&&historyTCRevision==commandBindingRevision&&historyCommandRevision==commandRevision)return;
            var record=History.Record("CONFIG",session:historySession);if(historyImportConfig.Length>0)record.Id=historyImportConfig;record.Experiment=historySessionExperiment;record.Data=HistoryJson.Decode<HistoryConfigurationSnapshot>(HistoryJson.Encode(new HistoryConfigurationSnapshot{Listener=Config,Decoder=decoder,Rules=continuous,TCBindings=tcBindings,Commands=Outgoing.Commands.BindingEntries(),Parameters=Catalog.ToArray()}));record.EstimatedBytes=Catalog.Count*1024+65536;
            History.Enqueue(record);historyConfiguration=record.Id;historySettings=Config;historyDecoderRevision=DecoderRevision;historyRulesRevision=continuous.Revision;historyTCRevision=commandBindingRevision;historyCommandRevision=commandRevision;PublishHistoryContext();
        }
        private void BeginHistorySession(string mode,string experiment="")
        {
            if(History==null||(!History.RecordingEnabled&&historyImportId.Length==0))return;if(historySession.Length>0)EndHistorySession("已切换采集会话");historySessionExperiment=experiment.Length>0?experiment:History.EnsureExperiment();historySessionMode=mode;historySession=historyImportId.Length>0?historyImportId:Guid.NewGuid().ToString("N");EnsureHistoryConfiguration(true);
            var record=History.Record("SESSION",session:historySession,config:historyConfiguration);record.Experiment=historySessionExperiment;record.Mode=mode;record.Status="START";record.Data=new{action="start",mode,sourceIPs=Config.SourceIPs,bind=Config.BindAddress,port=Config.UdpPort,multicast=Config.MulticastGroup,monotonicFrequency=System.Diagnostics.Stopwatch.Frequency,serverClockOrigin=HistoryText.Utc(EventStamp.ServerClockOrigin),serverCounterOrigin=EventStamp.ServerCounterOrigin.ToString(),sourceFile=historyImportFile,sourceSha256=historyImportHash};History.Enqueue(record);
        }
        private void EndHistorySession(string reason)
        {
            if(History==null||historySession.Length==0)return;
            var record=History.Record("SESSION",session:historySession,config:historyConfiguration);
            record.Experiment=historySessionExperiment;record.Mode=historySessionMode;record.Status="END";record.Reason=reason;
            record.Data=new{action="end",received=Received,accepted=Accepted,invalid=Invalid,duplicates=Duplicates,lost=Lost,filtered=Filtered};
            historySession="";PublishHistoryContext();
            try { History.Enqueue(record); }
            catch(System.IO.IOException) { /* StorageFault already reports the reason. */ }
        }
        private static bool IsHistoryCaptureBasis(string basis)
        {return basis=="NPCAP_CAPTURE"||basis=="NPCAP_RECEIVE"||basis=="PCAP_CAPTURE";}
        private static string HistoryTimingKey(HistoryRecord record,bool sending)
        {return (sending?"SEND/":"RECEIVE/")+record.Session+"/"+record.SourceIP+":"+record.SourcePort+">"+record.DestinationPort+"/"+record.PayloadSha;}
        private static void SetHistoryCaptureTime(HistoryRecord record,bool confirmed,DateTime? captured=null,string captureId="",string interfaceId="")
        {
            record.CaptureTimeConfirmed=confirmed;record.CaptureTime=confirmed?HistoryText.Utc(captured.Value):"";record.CaptureTicks=confirmed?captured.Value.ToUniversalTime().Ticks:0;record.CaptureEventId=confirmed?captureId:"";
            if(!new[]{"PACKET","PACKET_CAPTURE","COMMAND_CAPTURE","COMMAND_SEND","NETWORK_FRAME"}.Contains(record.Kind))return;
            var data=record.Data==null?new Dictionary<string,object>():HistoryJson.Decode<Dictionary<string,object>>(HistoryJson.Encode(record.Data));
            data["captureTimeConfirmed"]=confirmed;data["canonicalCaptureTime"]=record.CaptureTime;data["captureEventId"]=record.CaptureEventId;data["captureAssociated"]=confirmed;
            if(interfaceId.Length>0)data["captureInterfaceId"]=interfaceId;
            data["accuracyVerified"]=false;data["diagnosticTime"]=record.Time;data["diagnosticTimeBasis"]=record.TimeBasis;
            bool fileTime=confirmed&&record.TimeBasis=="PCAP_CAPTURE";
            data["eventTimeBasis"]=fileTime?"PCAP_CAPTURE":"SERVER_OBSERVATION";
            data["eventTime"]=fileTime?record.CaptureTime:record.Received;
            data["timingStatus"]=fileTime?"原文件捕获时间":"服务器接收或登记时间";
            data["timingNote"]=fileTime?"采用原文件时间保持历史时序":"采用服务器观察事件时记录的连续时钟时间";record.Data=data;
        }
        private void AddHistoryTiming(HistoryRecord record,bool sending,bool captured,string interfaceId="")
        {
            string key=HistoryTimingKey(record,sending);var entry=new HistoryTimingEntry{Id=record.Id,Time=record.CaptureTime,DestinationIP=record.DestinationIP,InterfaceId=interfaceId,CaptureTicks=record.CaptureTicks,ReceivedTicks=record.ReceivedTicks,MonotonicTicks=record.MonotonicTicks};
            lock(historyCaptureGate)
            {
                var map=captured?historyReceipts:historyPendingReceipts;var order=captured?historyReceiptOrder:historyPendingReceiptOrder;
                List<HistoryTimingEntry> entries;if(!map.TryGetValue(key,out entries))map[key]=entries=new List<HistoryTimingEntry>();entries.Add(entry);order.Enqueue(Tuple.Create(key,entry));
                long now=System.Diagnostics.Stopwatch.GetTimestamp();while(order.Count>6000||(order.Count>0&&now-order.Peek().Item2.MonotonicTicks>System.Diagnostics.Stopwatch.Frequency*5))
                {var old=order.Dequeue();if(map.TryGetValue(old.Item1,out entries)){entries.Remove(old.Item2);if(entries.Count==0)map.Remove(old.Item1);}}
            }
        }
        private HistoryRecord BeginHistoryPacket(byte[] data,string ip,bool simulated,int port,long captureFrame,EventStamp stamp,string destination)
        {
            if(History==null||historySession.Length==0||(!History.RecordingEnabled&&historyImportId.Length==0))return null;EnsureHistoryConfiguration();
            var record=History.Record("PACKET",stamp,historySession,historyConfiguration);
            if(historyImportId.Length>0)record.Id=HistoryText.Hash(historyImportId+"/"+historyImportOffset).Substring(0,32);
            record.Experiment=historySessionExperiment;record.SourceIP=ip;record.SourcePort=port;record.DestinationIP=destination??"";record.DestinationPort=Config.UdpPort;record.Direction="RECEIVE";record.Mode=simulated?historySessionMode:"LIVE_UDP";
            record.PayloadBytes=data;record.EstimatedBytes=data.Length*2+1024;record.Status="UNRECOGNIZED";record.CaptureFrame=captureFrame;record.Data=new{payloadLength=data.Length,listenerBind=Config.BindAddress,captureFrame,sourceFile=historyImportFile,sourceSha256=historyImportHash,sourceOffset=historyImportOffset.ToString()};
            if(data.Length>=2)record.OuterSourceId=Protocol.U16(data,0);
            return record;
        }
        private void FinalizeHistoryTiming(HistoryRecord record)
        {
            if (record.PayloadBytes != null)
            {
                record.PayloadBase64 = Convert.ToBase64String(record.PayloadBytes);
                record.PayloadSha = HistoryText.Hash(record.PayloadBytes);
                record.PayloadBytes = null;
            }
            if(record.CaptureTimeConfirmed==true)return;
            if(IsHistoryCaptureBasis(record.TimeBasis)){SetHistoryCaptureTime(record,true,new DateTime(record.ObservedTicks,DateTimeKind.Utc),record.Id);return;}
            SetHistoryCaptureTime(record,false);
            bool sending=record.Kind=="COMMAND_SEND";
            if(!sending||record.Mode!="LIVE_UDP"||record.Status!="SENT")return;
            string key=HistoryTimingKey(record,sending);
            lock(historyCaptureGate)
            {
                List<HistoryTimingEntry> receipts;if(!historyReceipts.TryGetValue(key,out receipts))return;
                Func<HistoryTimingEntry,bool> sameDestination=r=>String.IsNullOrEmpty(record.DestinationIP)||record.DestinationIP=="0.0.0.0"||r.DestinationIP==record.DestinationIP;
                var matches=receipts.Where(r=>sameDestination(r)&&Math.Abs(record.ReceivedTicks-r.CaptureTicks)<TimeSpan.TicksPerSecond).ToArray();if(matches.Length!=1)return;
                List<HistoryTimingEntry> pending;if(historyPendingReceipts.TryGetValue(key,out pending)&&pending.Count(r=>!r.Confirmed&&sameDestination(r)&&Math.Abs(record.ReceivedTicks-r.ReceivedTicks)<TimeSpan.TicksPerSecond)>1)return;
                var found=matches[0];SetHistoryCaptureTime(record,true,new DateTime(found.CaptureTicks,DateTimeKind.Utc),found.Id,found.InterfaceId);
                if(pending!=null){var own=pending.FirstOrDefault(r=>r.Id==record.Id);if(own!=null)own.Confirmed=true;}
                // Each physical capture may confirm at most one socket receipt/send attempt.
                receipts.Remove(found);
            }
        }
        private void FillHistoryPacket(HistoryRecord record,Packet packet)
        {
            if(record==null||packet==null)return;record.PacketId=packet.PacketId??"";record.PacketName=packet.PacketName??"";record.Apid=packet.Apid;record.Sequence=packet.Sequence;
        }
        private static HistoryValue HistorySample(Reading reading)
        {return new HistoryValue{ParameterId=reading.Id,Value=reading.EngineeringText,ValueKind=reading.EngineeringKind,Hex=reading.HexSource,Raw=reading.Raw,Quality=reading.Status,Diagnostic=reading.Diagnostic};}
        private void FinishHistoryPacket(HistoryRecord record, HistoryValue[] samples, int count)
        {
            if (record == null) return;
            // A processing failure can occur after only part of the packet has been decoded.
            if (count == 0) samples = null;
            else if (samples.Length != count) Array.Resize(ref samples, count);
            record.Samples = samples;
            record.EstimatedBytes += count * 220;
            History.Enqueue(record);
        }
        private void HistoryCapturedCommand(SendRecord capture)
        {RecordHistoryCapturedCommand(capture,"LIVE_UDP","NPCAP_CAPTURE");}
        private void RecordHistoryCapturedCommand(SendRecord capture,string mode,string evidence)
        {
            var storage=History;if(storage==null||(!storage.RecordingEnabled&&historyImportId.Length==0))return;var stamp=EventStamp.At(capture.ObservedMonotonic,capture.Time,capture.CaptureTimeKnown?(evidence=="PCAP_IMPORT_CAPTURE"?"PCAP_CAPTURE":"NPCAP_CAPTURE"):"UNKNOWN_CAPTURE_TIME");
            var context=historyContext;var record=storage.Record("COMMAND_CAPTURE",stamp,context.Session,context.Configuration);record.Id=capture.HistoryEventId;
            record.Experiment=context.Experiment;record.Direction="SEND_CAPTURE";record.Mode=mode;record.SourceIP=capture.SourceIP;record.SourcePort=capture.SourcePort;record.DestinationIP=capture.DestinationIP;record.DestinationPort=capture.DestinationPort;record.PayloadBase64=Convert.ToBase64String(capture.Payload);record.PayloadSha=HistoryText.Hash(capture.Payload);record.EstimatedBytes=capture.Payload.Length*2+2048;record.Status="OBSERVED";
            var matches=Outgoing.Commands.MatchingEntries(capture.Payload);string kind=ObservedCommands.DestinationKind(capture.DestinationIP);
            var codes=new HashSet<string>();var names=new HashSet<string>();
            foreach(var command in matches)
            {
                foreach(var category in command.Categories.Where(c=>ObservedCommands.CategoryMatches(command,c,capture.DestinationIP)))codes.Add(category.Code);
                foreach(var binding in tcBindings.Bindings.Where(b=>command.MatchesNumber(b.Number)))codes.Add(binding.Code);names.Add(command.Name);
            }
            record.Data=new{evidence,verified=true,codes=codes.ToArray(),names=names.ToArray(),payloadLength=capture.Payload.Length,captureTimestamp=capture.CaptureTimeKnown?HistoryText.Utc(capture.Time):"",capturedPrecision=capture.CaptureTimeKnown?"SOURCE_CAPTURE":"UNKNOWN",captureInterfaceId=capture.CaptureInterfaceId,sourceFile=historyImportFile,sourceSha256=historyImportHash,sourceOffset=historyImportOffset.ToString()};
            SetHistoryCaptureTime(record,capture.CaptureTimeKnown,capture.CaptureTimeKnown?(DateTime?)capture.Time:null,capture.CaptureTimeKnown?record.Id:"",capture.CaptureInterfaceId);
            if(mode=="LIVE_UDP"&&capture.CaptureTimeKnown)AddHistoryTiming(record,true,true,capture.CaptureInterfaceId);storage.Enqueue(record);
        }
        internal void HistoryTransmission(CommandTransmissionRecord transmission)
        {
            var storage=History;if(storage==null||!storage.RecordingEnabled)return;var stamp=transmission.ObservedMonotonic>0?EventStamp.At(transmission.ObservedMonotonic,basis:"LOCAL_SEND_RESULT"):EventStamp.Now(basis:"LOCAL_SEND_RESULT");
            var context=historyContext;var record=storage.Record("COMMAND_SEND",stamp,context.Session,context.Configuration);if(context.Session.Length>0)record.Experiment=context.Experiment;record.Id=transmission.historyEventId;record.Direction="SEND_ATTEMPT";record.Mode="LIVE_UDP";record.SourceIP=transmission.sourceIP;record.SourcePort=transmission.sourcePort;record.DestinationIP=transmission.destinationIP;record.DestinationPort=transmission.destinationPort;record.PayloadBase64=transmission.length==0?"":Convert.ToBase64String(PacketCodec.Hex(transmission.hex));record.Status=transmission.success?"SENT":"SEND_FAILED";record.Reason=transmission.error??"";record.EstimatedBytes=transmission.length*2+2048;
            record.PayloadSha=HistoryText.Hash(Convert.FromBase64String(record.PayloadBase64));record.Data=new{evidence="LOCAL_SEND_RESULT",verified=transmission.success,success=transmission.success,codes=String.IsNullOrEmpty(transmission.code)?new string[0]:new[]{transmission.code},names=new[]{transmission.name},actor=transmission.actor,payloadLength=transmission.length,sentBytes=transmission.sentBytes};
            SetHistoryCaptureTime(record,false);if(transmission.success)AddHistoryTiming(record,true,false);storage.Enqueue(record);
        }
        internal void HistoryExperimentChanged()
        {lock(Gate){if(Listening||Demo){EndHistorySession("切换试验");BeginHistorySession(Demo?"INTERNAL_PREVIEW":"LIVE_UDP");}}}
        internal void HistoryRecordingChanged()
        {
            lock(Gate)
            {
                if(History==null)return;
                if(!History.RecordingEnabled)EndHistorySession("历史采集已关闭");
                else if(historySession.Length==0&&(Listening||Demo))BeginHistorySession(Demo?"INTERNAL_PREVIEW":"LIVE_UDP");
            }
        }
        internal void ExportHistoryImportConfiguration(string directory)
        {
            lock(Gate)
            {
                string config=System.IO.Path.Combine(directory,"config");System.IO.Directory.CreateDirectory(config);JsonFile.Save(System.IO.Path.Combine(config,"server.json"),Config);JsonFile.Save(System.IO.Path.Combine(config,"parameters.json"),Catalog);
                if(decoder!=null)JsonFile.Save(System.IO.Path.Combine(config,"decoder.json"),decoder);
                JsonFile.Save(System.IO.Path.Combine(config,"continuous-rules.json"),continuous);JsonFile.Save(System.IO.Path.Combine(config,"tc-bindings.json"),tcBindings);
                var commands=Outgoing.Commands.BindingEntries();JsonFile.Save(System.IO.Path.Combine(config,"observed-commands.json"),new ObservedCommandConfiguration{SourceIP=Outgoing.Commands.BindingSource,Commands=commands.ToList(),NextNumber=commands.Length==0?1:commands.Max(c=>c.Number)+1});
                JsonFile.Save(System.IO.Path.Combine(config,"send-monitor.json"),Outgoing.ConfigurationCopy());
            }
        }
        internal void BeginHistoryImport(HistoryStorage storage,string id,string configuration,string file,string hash,string experiment)
        {lock(Gate){History=storage;historyImportId=id;historyImportConfig=configuration;historyImportFile=file;historyImportHash=hash;BeginHistorySession("PCAP_IMPORT",experiment);}}
        internal void ImportHistoryFrame(CaptureFrame frame)
        {
            lock(Gate)
            {
                if(historyImportSection!=frame.Section){sequences.Clear();historyImportSection=frame.Section;}historyImportOffset=frame.FileOffset;
                var stamp=frame.TimestampTicks>=0?EventStamp.Now(new DateTime(frame.TimestampTicks,DateTimeKind.Utc),"PCAP_CAPTURE"):EventStamp.Now(basis:"UNKNOWN_CAPTURE_TIME");
                if(frame.Udp&&frame.Payload!=null)
                {
                    var outgoing=Outgoing.ConfigurationCopy();
                    if(outgoing.SourceIPs.Contains(frame.SourceIP)&&outgoing.DestinationIPs.Contains(frame.DestinationIP)&&(outgoing.DestinationPort==0||outgoing.DestinationPort==frame.DestinationPort))
                    {RecordHistoryCapturedCommand(new SendRecord{HistoryEventId=HistoryText.Hash(historyImportId+"/"+frame.FileOffset).Substring(0,32),Time=frame.TimestampTicks>=0?new DateTime(frame.TimestampTicks,DateTimeKind.Utc):EventStamp.PreciseUtcNow(),CaptureTimeKnown=frame.TimestampTicks>=0,SourceIP=frame.SourceIP,SourcePort=frame.SourcePort,DestinationIP=frame.DestinationIP,DestinationPort=frame.DestinationPort,Payload=frame.Payload},"PCAP_IMPORT","PCAP_IMPORT_CAPTURE");return;}
                    ProcessReceived(frame.Payload,frame.SourceIP,true,frame.SourcePort,frame.Number,stamp,frame.DestinationIP);
                }
                else
                {
                    var record=History.Record("NETWORK_FRAME",stamp,historySession,historyConfiguration);record.Id=HistoryText.Hash(historyImportId+"/"+frame.FileOffset).Substring(0,32);record.Experiment=historySessionExperiment;record.Mode="PCAP_IMPORT";record.Direction="RECEIVE_CAPTURE";record.Status="UNSUPPORTED_NETWORK";record.Reason=frame.Issue??"非完整IPv4 UDP报文";record.Data=new{sourceFile=historyImportFile,sourceSha256=historyImportHash,sourceOffset=frame.FileOffset.ToString(),networkOffset=frame.DataFileOffset.ToString(),networkLength=frame.CapturedLength,captureFrame=frame.Number};History.Enqueue(record);
                }
            }
        }
        internal void EndHistoryImport(string reason)
        {lock(Gate){EndHistorySession(reason);History=null;}}
    }
}
