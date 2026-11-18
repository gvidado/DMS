using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed class LivePacketCounters
    {
        public DateTime LastMatch,LastValidFrame,LastParameterUpdate;
        public string LastOutcome="";
        public long Received, Accepted, Duplicates, Invalid, OutOfOrder;
        public int LastSequence=-1,LastResyncFrom=-1,LastResyncTo=-1;
        public System.DateTime LastResyncAt=System.DateTime.MinValue;
    }

    public sealed partial class Monitor
    {
        private readonly Dictionary<string, LivePacketCounters> packetCounters = new Dictionary<string, LivePacketCounters>();
        private string packetStatisticsMode = "";

        private LivePacketCounters PacketCountersFor(string id)
        {
            LivePacketCounters counters;
            if (!packetCounters.TryGetValue(id, out counters)) packetCounters[id] = counters = new LivePacketCounters();
            return counters;
        }

        private List<PacketDefinition> LegacyStatisticsDefinitions()
        {
            return Config.AllowedApids.Select(apid => new PacketDefinition { Id = "legacy-apid-" + apid,
                Name = apid == Config.ParameterApid ? "原有SPP参数包" : "SPP接收包 APID " + apid, Apid = apid,
                SppOffset = Config.SppOffset, SourceAddress = Config.SourceAddress, ServiceType = Config.ServiceType,
                ServiceSubtype = Config.ServiceSubtype, MappingConfirmed = apid == Config.ParameterApid && Config.MappingConfirmed }).ToList();
        }

        private static string PacketDiagnosticTime(DateTime value)
        {return value==DateTime.MinValue?"":BeijingTime.Format(value,"HH:mm:ss.fff");}

        private List<object> PacketStatisticsSnapshot()
        {
            IEnumerable<PacketDefinition> definitions = MonitoredPackets();
            var rows = new List<object>();
            foreach (PacketDefinition definition in definitions)
            {
                LivePacketCounters counters;
                if (!packetCounters.TryGetValue(definition.Id, out counters)) counters = new LivePacketCounters();
                string status = !NativeDecoder && definition.Apid != Config.ParameterApid ? "仅接收" :
                    definition.MappingConfirmed ? "解码已启用" : packetStatisticsMode == "demo" ? "演示预览" : "映射待确认";
                rows.Add(new { id = definition.Id, name = definition.Name, apid = definition.Apid,
                    outerSourceId=definition.OuterSourceId,telemetryNamespace=definition.TelemetryNamespace,sourceAddress = definition.SourceAddress, serviceType = definition.ServiceType, serviceSubtype = definition.ServiceSubtype,
                    decodingEnabled = definition.MappingConfirmed, status = status,
                    received = counters.Received, accepted = counters.Accepted, duplicates = counters.Duplicates,
                    lastMatch = PacketDiagnosticTime(counters.LastMatch), lastValidFrame = PacketDiagnosticTime(counters.LastValidFrame),
                    lastParameterUpdate = PacketDiagnosticTime(counters.LastParameterUpdate), lastOutcome = counters.LastOutcome,
                    invalid = counters.Invalid, outOfOrder = counters.OutOfOrder,lastSequence=counters.LastSequence,
                    lastResyncFrom=counters.LastResyncFrom,lastResyncTo=counters.LastResyncTo,
                    lastResyncAt=counters.LastResyncAt==System.DateTime.MinValue?"":counters.LastResyncAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),health=PacketHealthSnapshot(definition) });
            }
            return rows;
        }
    }
}
