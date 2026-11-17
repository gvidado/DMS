using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class PacketDecodePlan
    {internal Settings Settings;internal Parameter[] Fields;internal HashSet<string> Changed;internal bool Enabled;}
    public sealed partial class Monitor
    {
        // Configuration models are replaced atomically. Indexes follow model identity, never live data.
        private List<Parameter> indexedCatalog;
        private DecoderConfiguration indexedDecoder;
        private Settings indexedSettings;
        private Dictionary<string,Parameter> runtimeFields;
        private Dictionary<string,Parameter[]> runtimeReferences,runtimeReferencesIgnoreCase;
        private Dictionary<string,PacketDecodePlan> runtimeDecodePlans;
        private Dictionary<long,PacketDefinition[]> runtimeDecodeRoutes,runtimeRawRoutes;
        private Dictionary<int,PacketDefinition[]> runtimeRawApidRoutes;
        private int[] runtimeRawApidOffsets;
        private PacketDefinition[] runtimeLooseDecode,runtimeLooseRaw;
        private Parameter[] runtimeShipTimeFields;
        private Parameter[] runtimeLegacyFields;
        private HashSet<string> runtimeLegacyChanged;
        private int runtimeActiveParameters,runtimeRawEnabled;
        private bool runtimeMappingReady,runtimeRawOnly;
        private static readonly Parameter[] NoParameters=new Parameter[0];
        private static readonly PacketDefinition[] NoPackets=new PacketDefinition[0];
        private void EnsureRuntimeIndexes()
        {
            if(Object.ReferenceEquals(indexedCatalog,Catalog)&&Object.ReferenceEquals(indexedDecoder,decoder)&&Object.ReferenceEquals(indexedSettings,Config))return;
            var fields=Catalog.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            var references=Catalog.GroupBy(p=>p.Reference,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var insensitive=Catalog.GroupBy(p=>p.Reference,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
            var plans=new Dictionary<string,PacketDecodePlan>();var groups=Catalog.Where(p=>p.PacketId!=null).ToLookup(p=>p.PacketId);
            if(decoder!=null)foreach(var packet in decoder.Packets){var rows=groups[packet.Id].ToArray();plans[packet.Id]=new PacketDecodePlan{Settings=packet.DecodeSettings(),Fields=rows,Changed=new HashSet<string>(rows.Select(p=>p.Id)),Enabled=packet.Enabled};}
            Func<bool,Dictionary<long,PacketDefinition[]>> routes=raw=>decoder==null?new Dictionary<long,PacketDefinition[]>():decoder.Packets.Where(p=>p.OuterSourceId>=0&&(raw?p.MonitorRaw:p.Enabled)).GroupBy(p=>((long)p.OuterSourceId<<16)|(uint)PacketCodec.Identifier(p)).ToDictionary(g=>g.Key,g=>g.ToArray());
            runtimeFields=fields;runtimeReferences=references;runtimeReferencesIgnoreCase=insensitive;runtimeDecodePlans=plans;runtimeDecodeRoutes=routes(false);runtimeRawRoutes=routes(true);
            runtimeLooseDecode=decoder==null?NoPackets:decoder.Packets.Where(p=>p.Enabled&&p.OuterSourceId<0).ToArray();runtimeLooseRaw=NoPackets;
            var rawApid=decoder==null?NoPackets:decoder.Packets.Where(p=>p.MonitorRaw&&p.OuterSourceId<0).ToArray();
            runtimeRawApidRoutes=rawApid.GroupBy(p=>p.Apid).ToDictionary(g=>g.Key,g=>g.ToArray());runtimeRawApidOffsets=rawApid.Select(p=>p.SppOffset<0?6:p.SppOffset).Distinct().ToArray();
            runtimeShipTimeFields=Catalog.Where(p=>String.Equals(TelemetryReferences.CodeOf(p.Code),"CCYK00001",StringComparison.OrdinalIgnoreCase)).ToArray();
            runtimeLegacyFields=decoder==null?Catalog.Where(p=>Protocol.InProfile(p,Config.Profile)).ToArray():NoParameters;
            runtimeLegacyChanged=decoder==null?new HashSet<string>(runtimeLegacyFields.Select(p=>p.Id)):null;
            var enabled=decoder==null?NoPackets:decoder.Packets.Where(p=>p.Enabled).ToArray();runtimeActiveParameters=decoder==null?runtimeLegacyFields.Length:Catalog.Count(p=>p.PacketEnabled);runtimeMappingReady=decoder==null?Config.MappingConfirmed:enabled.Length>0&&enabled.All(p=>p.MappingConfirmed);runtimeRawEnabled=decoder==null?0:decoder.Packets.Count(p=>p.MonitorRaw);runtimeRawOnly=runtimeRawEnabled>0&&enabled.Length==0;
            indexedCatalog=Catalog;indexedDecoder=decoder;indexedSettings=Config;
        }
        private Dictionary<string,Parameter> RuntimeFields(){EnsureRuntimeIndexes();return runtimeFields;}
        private Parameter RuntimeField(string id){Parameter field;return RuntimeFields().TryGetValue(id??"",out field)?field:null;}
        private Parameter[] RuntimeReferences(string reference,bool ignoreCase=false)
        {EnsureRuntimeIndexes();Parameter[] fields;return (ignoreCase?runtimeReferencesIgnoreCase:runtimeReferences).TryGetValue(reference,out fields)?fields:NoParameters;}
        private PacketDecodePlan RuntimeDecodePlan(string id){EnsureRuntimeIndexes();return runtimeDecodePlans[id];}
        private IEnumerable<PacketDefinition> RuntimeRouteCandidates(byte[] data,bool raw)
        {
            EnsureRuntimeIndexes();var loose=raw?runtimeLooseRaw:runtimeLooseDecode;PacketDefinition[] exact=NoPackets;
            if(data.Length>=8){long key=((long)Protocol.U16(data,0)<<16)|(uint)Protocol.U16(data,6);(raw?runtimeRawRoutes:runtimeDecodeRoutes).TryGetValue(key,out exact);}
            return loose.Length==0?(exact??NoPackets):exact==null||exact.Length==0?loose:exact.Concat(loose);
        }
        private IEnumerable<PacketDefinition> RuntimeRawApidCandidates(byte[] data)
        {
            EnsureRuntimeIndexes();foreach(int offset in runtimeRawApidOffsets)
            {
                if(offset<0||data.Length-offset<2)continue;PacketDefinition[] entries;
                if(runtimeRawApidRoutes.TryGetValue(Protocol.U16(data,offset)&2047,out entries))foreach(var packet in entries)if((packet.SppOffset<0?6:packet.SppOffset)==offset)yield return packet;
            }
        }
    }
}
