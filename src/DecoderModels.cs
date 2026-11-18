using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed class EnumRange { public double Min; public double Max; public string Text; }
    public sealed class ImportIssue
    {
        public string Level;
        public string Sheet;
        public int Row;
        public string Code;
        public string Message;
        internal ImportIssue Copy() { return (ImportIssue)MemberwiseClone(); }
    }
    public sealed class SheetSummary
    {
        public int Index;
        public string Name;
        public int HeaderRow;
        public int Parameters;
        public int Supported;
        public int Skipped;
        public int ReservedSkipped;
        internal SheetSummary Copy() { return (SheetSummary)MemberwiseClone(); }
    }
    public sealed class TelemetryCatalog
    {
        public string SourceFile;
        public List<string> SourceFiles = new List<string>();
        public string GeneratedAt;
        public string CrossByteOrder = "firstMsb";
        public List<Parameter> Parameters = new List<Parameter>();
        public List<SheetSummary> Sheets = new List<SheetSummary>();
        public List<ImportIssue> Issues = new List<ImportIssue>();
        public List<ImportIssue> ReferenceIssues { get { return TelemetryReferences.Issues(Parameters); } }
        public List<string> Assumptions = new List<string>();
        internal TelemetryCatalog Copy()
        {
            var copy = (TelemetryCatalog)MemberwiseClone();
            copy.SourceFiles = SourceFiles == null ? null : new List<string>(SourceFiles);
            copy.Parameters = Parameters == null ? null : Parameters.Select(p => p == null ? null : p.CopyForMapping()).ToList();
            copy.Sheets = Sheets == null ? null : Sheets.Select(s => s == null ? null : s.Copy()).ToList();
            copy.Issues = Issues == null ? null : Issues.Select(i => i == null ? null : i.Copy()).ToList();
            copy.Assumptions = Assumptions == null ? null : new List<string>(Assumptions);
            return copy;
        }
    }
    public sealed class DecoderConfiguration
    {
        public int SchemaVersion = 2;
        public string Revision = "";
        public TelemetryCatalog Catalog = new TelemetryCatalog();
        public List<PacketDefinition> Packets = new List<PacketDefinition>();
        internal DecoderConfiguration Copy()
        {
            var copy = (DecoderConfiguration)MemberwiseClone();
            copy.Catalog = Catalog == null ? null : Catalog.Copy();
            copy.Packets = Packets == null ? null : Packets.Select(p => p == null ? null : p.Copy()).ToList();
            return copy;
        }
        public void Validate()
        {
            if (SchemaVersion != 2 || Catalog == null || Catalog.Parameters == null || Packets == null) throw new ArgumentException("不支持的解码配置格式，要求SchemaVersion=2");
            if (Catalog.Sheets == null) Catalog.Sheets = new List<SheetSummary>();
            if (Catalog.Issues == null) Catalog.Issues = new List<ImportIssue>();
            if (Catalog.Assumptions == null) Catalog.Assumptions = new List<string>();
            if (Catalog.SourceFiles == null) Catalog.SourceFiles = new List<string>();
            if (Catalog.SourceFiles.Count == 0 && !String.IsNullOrWhiteSpace(Catalog.SourceFile)) Catalog.SourceFiles.Add(Catalog.SourceFile);
            if (Catalog.Sheets.Any(s => s == null) || Catalog.Issues.Any(i => i == null)) throw new ArgumentException("工作表或问题列表含空记录");
            if (Catalog.Parameters.Count > 20000 || Packets.Count > 128) throw new ArgumentException("参数或SPP包数量超出限制");
            var ids = new HashSet<string>();
            foreach (Parameter p in Catalog.Parameters)
            {
                if(p!=null)p.TelemetryNamespace="";
                if (p == null || String.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 240 || !ids.Add(p.Id) || String.IsNullOrWhiteSpace(p.Group) || String.IsNullOrWhiteSpace(p.Code) || String.IsNullOrWhiteSpace(p.Name)) throw new ArgumentException("参数标识重复或缺少名称、分组");
                if (p.Offset < 0 || p.Offset > 65507 || p.Length < 1 || p.Length > 9 || p.BitStart < -1 || p.BitStart > 7 || p.BitCount < 0 || p.BitCount > 64 || p.Precision < 0 || p.Precision > 12) throw new ArgumentException("参数地址/宽度无效：" + p.Id);
                if (Double.IsNaN(p.Scale) || Double.IsInfinity(p.Scale) || Double.IsNaN(p.Bias) || Double.IsInfinity(p.Bias)) throw new ArgumentException("非有限换算系数：" + p.Id);
                if (p.Supported && !new[] { "uint", "int", "float32", "float64", "ascii", "bcd" }.Contains(p.Type)) throw new ArgumentException("未知参数类型：" + p.Id);
                if (p.Supported && (p.Length > 8 || (p.Type == "float32" && p.Length != 4) || (p.Type == "float64" && p.Length != 8))) throw new ArgumentException("参数长度与类型不符：" + p.Id);
                if (p.BitStart >= 0 && (p.BitCount < 1 || p.Length != (p.BitStart + p.BitCount + 7) / 8)) throw new ArgumentException("位字段占用长度不一致：" + p.Id);
                if (p.Enums == null) p.Enums = new Dictionary<string, string>();
                if (p.EnumRanges == null) p.EnumRanges = new List<EnumRange>();
                if (p.Enums.Count > 4096 || p.EnumRanges.Count > 4096) throw new ArgumentException("枚举过多：" + p.Id);
                foreach (EnumRange r in p.EnumRanges) if (r == null || !Finite(r.Min) || !Finite(r.Max) || r.Min > r.Max) throw new ArgumentException("枚举范围无效：" + p.Id);
            }
            var groups = new HashSet<string>(Catalog.Parameters.Select(p => p.Group));
            var packetIds = new HashSet<string>();
            foreach (PacketDefinition p in Packets)
            {
                if (p == null || !packetIds.Add(p.Id ?? "")) throw new ArgumentException("SPP包ID重复或为空");
                p.Validate(groups);
            }
            for (int i = 0; i < Packets.Count; i++)
                for (int j = i + 1; j < Packets.Count; j++)
                {
                    PacketDefinition a = Packets[i], b = Packets[j];
                    bool sourceOverlap=String.IsNullOrEmpty(a.SourceIP)||String.IsNullOrEmpty(b.SourceIP)||a.SourceIP==b.SourceIP;
                    if(a.MonitorRaw&&b.MonitorRaw&&(a.OuterSourceId<0||b.OuterSourceId<0)&&a.Apid==b.Apid&&sourceOverlap)
                        throw new ArgumentException("监测原包路由冲突："+a.Name+" / "+b.Name+"，外层来源为/的包仅按APID匹配，不能与同APID且来源IP重叠的其他监测包同时启用");
                    if (((a.Enabled && b.Enabled)||(a.MonitorRaw && b.MonitorRaw)) && PacketCodec.SameRoutingFields(a, b) && sourceOverlap)
                        throw new ArgumentException((a.Enabled&&b.Enabled?"启用的解码包":"监测原包")+"定义路由冲突：" + a.Name + " / " + b.Name + "，外层来源、包标识、源地址、业务类型、业务子类型相同且来源IP重叠");
                }
            var parametersByGroup = Catalog.Parameters.ToLookup(p => p.Group);
            if (Packets.Sum(p => p.Groups.Sum(g => parametersByGroup[g].Count())) > 100000) throw new ArgumentException("包映射展开后参数数量过大");
            var duplicate=Packets.Where(p=>p.Enabled&&p.OuterSourceId>=0)
                .SelectMany(packet=>packet.Groups.SelectMany(group=>parametersByGroup[group])
                    .Select(p=>TelemetryReferences.Name(p.Code,packet.TelemetryNamespace)))
                .GroupBy(reference=>reference).FirstOrDefault(g=>g.Count()>1);
            if(duplicate!=null)throw new ArgumentException("多个启用的SPP同时更新同一来源变量："+duplicate.Key+"；请调整参数表关联或停用冲突包");
        }
        private static bool Finite(double d) { return !Double.IsNaN(d) && !Double.IsInfinity(d); }
    }
    public sealed class PacketDefinition
    {
        public string Id = "";
        public string Name = "新SPP包";
        public bool Enabled = true;
        public bool MonitorRaw = false;
        public string SourceIP = "";
        public int OuterSourceId = -1;
        public string TelemetryNamespace {get{return TelemetryReferences.NamespaceFor(OuterSourceId);}}
        public string SppSourceFile = "";
        public string SppSourceSheet = "";
        public int Version = 0;
        public int PacketType = 0;
        public int SecondaryHeader = 1;
        public int Apid = 8;
        public int SequenceFlags = 3;
        public int ExpectedLengthField = 1017;
        public int ExpectedDatagramLength = 0;
        public int SppOffset = -1;
        public int PayloadOffset = 16;
        public bool StrictLength = true;
        public int PusVersion = 3;
        public int PusFlags = 0;
        public int ServiceType = 3;
        public int ServiceSubtype = 25;
        public int PacketCounter = 0;
        public int DestinationApid = 0;
        public int SourceAddress = 4100;
        public int DestinationAddress = 0;
        public string ByteOrder = "big";
        public string BitOrder = "msb0";
        public string CrossByteOrder = "firstMsb";
        public string ChecksumMode = SppChecksum.Mode;
        public int ChecksumStart = 0;
        public bool MappingConfirmed = false;
        public List<string> Groups = new List<string>();
        internal PacketDefinition Copy()
        {
            var copy = (PacketDefinition)MemberwiseClone();
            copy.Groups = Groups == null ? null : new List<string>(Groups);
            return copy;
        }
        public void Validate(HashSet<string> groups)
        {
            ChecksumMode = SppChecksum.Mode; ChecksumStart = 0;
            if(OuterSourceId < -1 || OuterSourceId > 65535)throw new ArgumentException("外层数据源标识应为0x0000～0xFFFF；未设置为-1");
            if(OuterSourceId<0&&!Enabled&&!MonitorRaw)MappingConfirmed=false;
            if(Enabled && OuterSourceId<0)throw new ArgumentException("遥测解码必须设置具体外层数据源；/仅用于不参与解码的原包监测");
            if(OuterSourceId>=0 && SppOffset!=-1 && SppOffset!=6)throw new ArgumentException("带六字节外层头的SPP起始偏移固定为6，可填写6或自动");
            if (!Regex.IsMatch(Id ?? "", "^[A-Za-z0-9_-]{1,64}$") || String.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new ArgumentException("SPP包ID使用1～64位字母数字下划线短横线，且需填写包名称");
            IPAddress ip;
            if (!String.IsNullOrEmpty(SourceIP) && (!IPAddress.TryParse(SourceIP, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)) throw new ArgumentException("包来源IP无效");
            if (Version < 0 || Version > 7 || PacketType < 0 || PacketType > 1 || SecondaryHeader < 0 || SecondaryHeader > 1 || Apid < 0 || Apid > 2047 || SequenceFlags != 3) throw new ArgumentException("SPP主导头字段无效；当前仅支持未分包序列标志3");
            if ((Enabled||MonitorRaw) && SecondaryHeader != 1) throw new ArgumentException("启用的SPP包必须有副导头，以匹配源地址、业务类型和业务子类型");
            if (ExpectedLengthField < -1 || ExpectedLengthField > 65535 || ExpectedDatagramLength < 0 || ExpectedDatagramLength > 65507 || SppOffset < -1 || SppOffset > 4096 || PayloadOffset < (SecondaryHeader == 1 ? 16 : 6) || PayloadOffset > 65505 || ChecksumStart < 0 || ChecksumStart > 4096) throw new ArgumentException("SPP长度或偏移无效");
            if (PusVersion < 0 || PusVersion > 15 || PusFlags < 0 || PusFlags > 15 || ServiceType < 0 || ServiceType > 255 || ServiceSubtype < 0 || ServiceSubtype > 255 || PacketCounter < 0 || PacketCounter > 255 || DestinationApid < 0 || DestinationApid > 2047 || SourceAddress < 0 || SourceAddress > 65535 || DestinationAddress < 0 || DestinationAddress > 65535) throw new ArgumentException("SPP副导头字段越界");
            if (!new[] { "big", "little" }.Contains(ByteOrder) || !new[] { "lsb0", "msb0" }.Contains(BitOrder) || !new[] { "unverified", "firstLsb", "firstMsb" }.Contains(CrossByteOrder)) throw new ArgumentException("不支持的字节序或位序");
            if (Groups == null || Groups.Count > 128 || Groups.Distinct().Count() != Groups.Count) throw new ArgumentException("参数表选择无效");
            if (Enabled && (Groups.Count == 0 || Groups.Any(g => !groups.Contains(g)))) throw new ArgumentException("请为启用的包选择存在的Excel参数表");
        }
        public Settings DecodeSettings()
        {
            return new Settings { ByteOrder = ByteOrder, BitOrder = BitOrder, CrossByteOrder = CrossByteOrder, PayloadOffset = PayloadOffset, MappingConfirmed = MappingConfirmed };
        }
    }
    public sealed class ImportPreview
    {
        public string Token;
        public string Username;
        public DateTime Created;
        public string Revision;
        public DecoderConfiguration Configuration;
        public DecoderConfiguration AddConfiguration;
        public CatalogAddition Addition;
        public DecoderConfiguration MatchingConfiguration;
        public CatalogMatchingReplacement Matching;
        public string SourceType;
        public DecoderConfiguration BaseConfiguration;
        public TelemetryImportReview Review;
        public int EditRevision;
        public bool CanReplace = true;
        public List<string> ReviewErrors = new List<string>();
    }
}
