using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private DecoderConfiguration decoder;
        private string legacyRevision = "legacy";
        public bool NativeDecoder { get { return decoder != null; } }
        public string DecoderRevision { get { return decoder == null ? legacyRevision : decoder.Revision; } }
        private void LoadDecoder()
        {
            string path = Path.Combine(root, "config", "decoder.json");
            if (!File.Exists(path)) return;
            decoder = JsonFile.Load<DecoderConfiguration>(path);
            bool checksumChanged = decoder.Packets != null && decoder.Packets.Any(p => p != null && (p.ChecksumMode != SppChecksum.Mode || p.ChecksumStart != 0));
            bool namespaceChanged=false;
            foreach(var packet in decoder.Packets??new List<PacketDefinition>())if(packet.OuterSourceId<0 && (packet.Enabled || packet.MappingConfirmed)){packet.Enabled=false;packet.MappingConfirmed=false;namespaceChanged=true;}
            decoder.Validate();
            if (namespaceChanged || checksumChanged || String.IsNullOrEmpty(decoder.Revision))
            { decoder.Revision = Guid.NewGuid().ToString("N"); JsonFile.Save(path, decoder); }
            RebuildCatalog();
        }
        public DecoderConfiguration DecoderSnapshot()
        {
            lock (Gate)
            {
                if (decoder != null) return decoder.Copy();
                var config = new DecoderConfiguration { Revision = DecoderRevision, Catalog = new TelemetryCatalog { SourceFile = "旧版parameters.json", GeneratedAt = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss") } };
                config.Catalog.Parameters = Catalog.Select(p => p.CopyForMapping()).ToList();
                foreach (Parameter p in config.Catalog.Parameters)
                {
                    p.SourceId = p.Id;
                    if (p.BitStart >= 0) { p.Length = (p.BitStart + p.BitCount + 7) / 8; p.CrossByte = p.Length > 1; }
                }
                foreach (var group in Catalog.GroupBy(p => p.Group)) config.Catalog.Sheets.Add(new SheetSummary { Name = group.Key, Parameters = group.Count(), Supported = group.Count(p => p.Supported) });
                config.Packets.Add(new PacketDefinition { Id = "legacy", Name = "原有SPP参数包", Enabled=false,Apid = Config.ParameterApid, ExpectedLengthField = -1,
                    ExpectedDatagramLength = Config.ExpectedDatagramLength, SppOffset = Config.SppOffset, PayloadOffset = Config.PayloadOffset,
                    StrictLength = Config.StrictLength, ChecksumMode = Config.ChecksumMode, ChecksumStart = Config.ChecksumStart,
                    SourceAddress = Config.SourceAddress, ServiceType = Config.ServiceType, ServiceSubtype = Config.ServiceSubtype,
                    BitOrder = Config.BitOrder, MappingConfirmed = Config.MappingConfirmed,
                    Groups = Catalog.Where(p => Protocol.InProfile(p, Config.Profile)).Select(p => p.Group).Distinct().ToList() });
                return config;
            }
        }
        internal object ReadParameterDefinition(string id)
        {
            lock (Gate)
            {
                var configuration = decoder ?? DecoderSnapshot();
                var parameter = configuration.Catalog.Parameters.FirstOrDefault(p => p.Id == id);
                if (parameter == null) throw new ArgumentException("参数不存在，请刷新");
                return new
                {
                    revision = configuration.Revision,
                    parameter = parameter.CopyForMapping(),
                    packets = configuration.Packets.Where(p => p.Groups.Contains(parameter.Group)).Select(p => p.Name).ToArray()
                };
            }
        }
        private void RebuildCatalog()
        { Catalog = BuildCatalog(decoder); }
        private static List<Parameter> BuildCatalog(DecoderConfiguration configuration)
        {
            var output = new List<Parameter>();
            foreach (PacketDefinition packet in configuration.Packets.Where(p=>p.OuterSourceId>=0))
            {
                var groups=new HashSet<string>(packet.Groups);
                foreach (Parameter raw in configuration.Catalog.Parameters.Where(p => groups.Contains(p.Group)))
                {
                    Parameter copy = raw.CopyForMapping();
                    copy.SourceId = raw.Id; copy.TelemetryNamespace=packet.TelemetryNamespace;copy.Id = packet.Id + "::" + packet.TelemetryNamespace+"::"+raw.Id; copy.PacketId = packet.Id; copy.PacketName = packet.Name; copy.PacketEnabled = packet.Enabled;
                    output.Add(copy);
                }
            }
            return output;
        }
        private bool ReadyFor(string parameterId)
        {
            int split = parameterId.IndexOf("::", StringComparison.Ordinal);
            if (split < 1) return false;
            string id = parameterId.Substring(0, split);
            EnsureRuntimeIndexes();PacketDecodePlan plan;return runtimeDecodePlans.TryGetValue(id,out plan)&&plan.Enabled&&plan.Settings.MappingConfirmed;
        }
        public void ApplyDecoder(DecoderConfiguration config, string revision)
        {
            lock (Gate)
            {
                if (Listening || Demo) throw new InvalidOperationException("请先停止接收，再应用解码配置");
                if (revision != DecoderRevision) throw new InvalidOperationException("其他用户已修改配置，请刷新后重新预览或保存");
                config.Validate(); config.Revision = Guid.NewGuid().ToString("N");
                var nextDecoder=config.Copy();
                var nextCatalog=BuildCatalog(nextDecoder);
                var defaults=PlanDefaultRules(nextCatalog);
                // Save both files before publishing either configuration to running readers.
                string decoderPath = Path.Combine(root, "config", "decoder.json");
                byte[] previous = File.Exists(decoderPath) ? File.ReadAllBytes(decoderPath) : null;
                JsonFile.Save(decoderPath, config);
                try { if (defaults != null) JsonFile.Save(RulePath, defaults); }
                catch { if (previous == null) File.Delete(decoderPath); else File.WriteAllBytes(decoderPath, previous); throw; }
                if (defaults != null) continuous = defaults;
                decoder=nextDecoder; Catalog=nextCatalog; Reset();
                Note("info", "解码配置已应用：" + decoder.Catalog.Parameters.Count + "项源参数，" + decoder.Packets.Count + "个SPP包定义");
            }
        }
        private PacketDefinition ResolveDefinition(byte[] data, string ip)
        {
            PacketRoute route = PacketCodec.Resolve(data, ip, decoder.Packets.Where(p => p.Enabled));
            if (route.Definition == null) throw new ArgumentException(route.Reason);
            return PacketCodec.AtOffset(route.Definition, route.Offset);
        }
    }
}
