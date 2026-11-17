using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed class CommandCategory
    {public string Kind="",Code="";public List<string> DestinationIPs=new List<string>();}
    public sealed class CommandImportPlan
    {
        public ObservedCommandConfiguration Configuration;
        public long Revision;
        public int Rows,AddedCommands,AddedCategories,AddedDestinations,Renamed;
        public List<object> Preview=new List<object>();
        public List<string> Errors=new List<string>();
        public List<string> Warnings=new List<string>();
        public int BCommands,BroadcastCommands,Expanded,Skipped;
        public int XmlEmptyRows,XmlExistingRows,XmlBCRows;
        public bool XmlCheckBC;
        public int InjectionBytes;
        public string InjectionHex="",InjectionChecksum="";
        public List<STCImportRow> XmlRows;
        internal byte[] XmlSource;
        internal string XmlKind,XmlDestination,XmlCode;
        internal ushort XmlCounter;
    }
    public sealed partial class ObservedCommands
    {
        internal static readonly StringComparer CommandNameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
        public static string DestinationKind(string ip)
        {switch(ip){case "192.168.1.112":case "192.168.1.113":return "BC";case "192.168.1.111":return "PSS";case "192.168.1.137":return "KC";default:return "";}}
        internal static string[] KindDestinations(string kind)
        {return new[]{"192.168.1.112","192.168.1.113","192.168.1.111","192.168.1.137"}.Where(ip=>DestinationKind(ip)==kind).ToArray();}
        internal static bool IsInjectionKind(string kind){return kind=="YKE" || kind=="YKM";}
        internal static bool IsFlexibleKind(string kind){return IsInjectionKind(kind) || kind=="ZK";}
        internal static bool CategoryMatches(ObservedCommand command,CommandCategory category,string destination)
        {return category.DestinationIPs.Contains(destination) && (command.STC || IsFlexibleKind(category.Kind) || category.Kind==DestinationKind(destination));}
        private static CommandCategory CloneCategory(CommandCategory c)
        {return new CommandCategory{Kind=c.Kind,Code=c.Code,DestinationIPs=c.DestinationIPs.ToList()};}
        private static string[] CategoryDestinations(ObservedCommand command,string kind)
        {return command.DestinationIPs.Where(ip=>command.STC || IsFlexibleKind(kind) || DestinationKind(ip)==kind).ToArray();}
        internal static string CategoryCode(string kind,long number)
        {return kind+number.ToString(kind=="PSS"?"D3":"D4",CultureInfo.InvariantCulture);}
        // Observing a new command persists the catalog while holding its write lock.
        // Unchanged telemetry must not wait for that disk write just to read a version.
        internal long BindingRevision{get{return System.Threading.Interlocked.Read(ref revision);}}
        private static long CodeNumber(string kind,string code,bool allowLegacy=false)
        {
            long number;string prefix=kind??"";
            if(!new[]{"BC","PSS","KC","YKE","YKM","ZK"}.Contains(prefix) || !Regex.IsMatch(code??"","^"+prefix+"[0-9]{"+(prefix=="PSS"?"3":"4")+",}$") || !Int64.TryParse(code.Substring(prefix.Length),NumberStyles.None,CultureInfo.InvariantCulture,out number) || number<1 || number==Int64.MaxValue || (code!=CategoryCode(prefix,number) && !(allowLegacy && prefix=="PSS" && code==prefix+number.ToString("D4",CultureInfo.InvariantCulture))))throw new ArgumentException("指令编号格式无效："+code+"，例如ZK0001、BC0001、PSS001、KC0001、YKE0001或YKM0001");
            return number;
        }
        private static bool EnsureCategories(ObservedCommandConfiguration config,ObservedCommand command)
        {
            bool changed=false;
            var injections=command.Categories.Where(c=>IsFlexibleKind(c.Kind)).Select(c=>c.Kind).ToArray();
            var required=command.STC?command.Categories.Select(c=>c.Kind).ToArray():injections.Length>0?injections:command.DestinationIPs.Select(DestinationKind).Where(k=>k.Length>0).Distinct().ToArray();
            foreach(string kind in required)
            {
                var category=command.Categories.FirstOrDefault(c=>c.Kind==kind);
                if(category==null){long next=config.NextCategoryNumbers[kind];if(kind=="BC" && next>=2000 && next<=3999)next=4000;if(next<1 || next==Int64.MaxValue)throw new ArgumentException("分类指令编号已超出范围");category=new CommandCategory{Kind=kind,Code=CategoryCode(kind,next)};command.Categories.Add(category);config.NextCategoryNumbers[kind]=next+1;changed=true;}
            }
            foreach(var category in command.Categories){var ips=CategoryDestinations(command,category.Kind).ToList();if(!category.DestinationIPs.SequenceEqual(ips)){category.DestinationIPs=ips;changed=true;}}
            return changed;
        }
        private static bool NormalizeCategories(ObservedCommandConfiguration config)
        {
            bool changed=false;if(config.NextCategoryNumbers==null){config.NextCategoryNumbers=new Dictionary<string,long>();changed=true;}
            foreach(string kind in new[]{"BC","PSS","KC"})if(!config.NextCategoryNumbers.ContainsKey(kind)){config.NextCategoryNumbers[kind]=1;changed=true;}
            foreach(string kind in new[]{"YKE","YKM","ZK"})if(config.Commands.Any(c=>c.Categories!=null && c.Categories.Any(k=>k!=null && k.Kind==kind)) && !config.NextCategoryNumbers.ContainsKey(kind)){config.NextCategoryNumbers[kind]=1;changed=true;}
            var codes=new HashSet<string>();
            foreach(var command in config.Commands)
            {
                if(command.Categories==null){command.Categories=new List<CommandCategory>();changed=true;}
                if(command.Categories.Any(c=>c==null) || command.Categories.Select(c=>c.Kind).Distinct().Count()!=command.Categories.Count)throw new ArgumentException("同一源码含重复或无效指令类别");
                foreach(var category in command.Categories)
                {
                    long number=CodeNumber(category.Kind,category.Code,true);if(!codes.Add(category.Code))throw new ArgumentException("指令编号重复："+category.Code);
                    if(command.STC?!new[]{"ZK","BC","KC","PSS"}.Contains(category.Kind):IsInjectionKind(category.Kind)?!CommandEnvelope.IsInjection(PacketCodec.Hex(command.PayloadHex)):category.Kind!="ZK"&&!command.DestinationIPs.Any(ip=>DestinationKind(ip)==category.Kind))throw new ArgumentException(category.Code+"的类别与目的IP或注入模板不符");
                    if(!(category.Kind=="BC" && number>=2000 && number<=3999) && config.NextCategoryNumbers[category.Kind]<=number){config.NextCategoryNumbers[category.Kind]=number+1;changed=true;}
                    if(category.DestinationIPs==null){category.DestinationIPs=new List<string>();changed=true;}
                }
            }
            foreach(string kind in new[]{"BC","PSS","KC","YKE","YKM","ZK"})if(config.NextCategoryNumbers.ContainsKey(kind) && config.NextCategoryNumbers[kind]<1)throw new ArgumentException("分类指令计数器无效："+kind);
            foreach(var command in config.Commands)changed |= EnsureCategories(config,command);
            return changed;
        }
        public void ExportCsv(TextWriter writer)
        {
            ObservedCommand[] entries;string source;lock(gate){if(!ready)throw new InvalidOperationException(error);entries=configuration.Commands.ToArray();source=configuration.SourceIP;}
            bool stc=entries.Any(e=>e.STC);writer.NewLine="\r\n";writer.WriteLine("指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX"+(stc?",指令格式":""));
            foreach(var entry in entries)
            {
                string format=entry.STC?(String.IsNullOrEmpty(entry.FormatIssue)?"STC":"STC-LEGACY"+(entry.InterfaceBytes==2?"2":"4")):"";
                foreach(var category in entry.Categories)WriteCommandCsvRow(writer,category.Code,entry.Name,category.Kind,source,category.DestinationIPs,entry.PayloadHex,stc?format:null);
                var other=entry.DestinationIPs.Where(ip=>!entry.Categories.Any(c=>c.DestinationIPs.Contains(ip))).ToList();if(other.Count>0)WriteCommandCsvRow(writer,"",entry.Name,"未分类",source,other,entry.PayloadHex,stc?format:null);
            }
        }
        private static void WriteCommandCsvRow(TextWriter writer,string code,string name,string kind,string source,List<string> destinations,string payload,string format=null)
        {writer.WriteLine(String.Join(",",new[]{code,name,kind,source,String.Join(";",destinations),payload}.Concat(format==null?new string[0]:new[]{format}).Select(Monitor.CsvCell)));}
        private static List<string[]> ReadCommandCsv(byte[] bytes)
        {
            string text;try{text=new UTF8Encoding(false,true).GetString(bytes);}catch(DecoderFallbackException){text=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}text=text.TrimStart('\uFEFF');var rows=new List<string[]>();var cells=new List<string>();var field=new StringBuilder();bool quoted=false,closed=false;
            for(int i=0;i<text.Length;i++)
            {
                char c=text[i];if(quoted){if(c=='"'){if(i+1<text.Length && text[i+1]=='"'){field.Append('"');i++;}else{quoted=false;closed=true;}}else field.Append(c);continue;}
                if(c=='"'){if(field.Length>0 || closed)throw new ArgumentException("CSV引号位置无效");quoted=true;continue;}
                if(c==',' || c=='\r' || c=='\n')
                {cells.Add(field.ToString());field.Length=0;closed=false;if(c!=','){rows.Add(cells.ToArray());cells.Clear();if(c=='\r' && i+1<text.Length && text[i+1]=='\n')i++;}continue;}
                if(closed)throw new ArgumentException("CSV闭引号后只能是分隔符或换行");field.Append(c);
            }
            if(quoted)throw new ArgumentException("CSV存在未闭合的引号");if(field.Length>0 || cells.Count>0 || closed){cells.Add(field.ToString());rows.Add(cells.ToArray());}
            var headers=new[]{"指令编号","指令名称","指令类别","来源IP","目的IP","完整UDP载荷HEX"};
            if(rows.Count==0 || !(rows[0].Select(v=>v.Trim()).SequenceEqual(headers) || rows[0].Select(v=>v.Trim()).SequenceEqual(headers.Concat(new[]{"指令格式"}))))throw new ArgumentException("CSV表头应为：指令编号、指令名称、指令类别、来源IP、目的IP、完整UDP载荷HEX；可附加指令格式列");
            return rows.Skip(1).Where(r=>r.Any(v=>!String.IsNullOrWhiteSpace(v))).ToList();
        }
        public CommandImportPlan PreviewCsv(byte[] bytes)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);var rows=ReadCommandCsv(bytes);if(rows.Count==0 || rows.Count>20000)throw new ArgumentException("CSV应包含1～20000条指令记录");
                var plan=new CommandImportPlan{Revision=revision,Rows=rows.Count,Configuration=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(configuration))};
                var candidate=plan.Configuration;var codes=candidate.Commands.SelectMany(c=>c.Categories.Select(k=>new{entry=c,category=k})).ToDictionary(c=>c.category.Code);
                var exactPayloads=candidate.Commands.ToDictionary(c=>c.PayloadHex);var names=new Dictionary<string,string>();
                var skippedBC=new HashSet<int>();
                var incomingBCData=new List<string>();
                foreach(var row in rows.Where(r=>r.Length>=6&&r[2].Trim().Equals("BC",StringComparison.OrdinalIgnoreCase)))
                {
                    try{var bytesBC=PacketCodec.Hex(row[5]);if(CommandEnvelope.Valid(bytesBC)){var dataBC=CommandEnvelope.DataContent(bytesBC);if(dataBC.Length>0)incomingBCData.Add(MatchBytes.GetString(dataBC));}}catch(ArgumentException){}
                }
                var incomingBCPatterns=new CommandBytePatterns(incomingBCData);
                for(int rowIndex=0;rowIndex<rows.Count;rowIndex++)
                {
                    try
                    {
                        var row=rows[rowIndex];if(row.Length!=6 && row.Length!=7)throw new ArgumentException("应为六列或带指令格式的七列");string format=row.Length==7?row[6].Trim().ToUpperInvariant():"";if(format.Length>0 && format!="STC" && format!="STC-LEGACY4" && format!="STC-LEGACY2")throw new ArgumentException("指令格式只能为空、STC、STC-LEGACY4或STC-LEGACY2");int legacyWidth=format=="STC-LEGACY4"?4:format=="STC-LEGACY2"?2:0;bool stc=format=="STC"||legacyWidth!=0;string code=row[0].Trim().ToUpperInvariant(),name=row[1],kind=row[2].Trim().ToUpperInvariant(),source=IPv4(row[3].Trim());if(code.Length>32)throw new ArgumentException("指令编号过长");
                        if(source!=candidate.SourceIP)throw new ArgumentException("来源IP与当前指令库不一致："+source);if(String.IsNullOrWhiteSpace(name) || name.Length>256)throw new ArgumentException("指令名称应为1～256字符");
                        var destinations=row[4].Split(new[]{';','；',',','，',' ','\r','\n','\t'},StringSplitOptions.RemoveEmptyEntries).Select(IPv4).Distinct().ToList();if(destinations.Count==0)throw new ArgumentException("目的IP不能为空");
                        byte[] payload=String.IsNullOrWhiteSpace(row[5])?new byte[0]:PacketCodec.Hex(row[5]);string hex=Hex(payload),previousName;
                        int contentOffset=CommandEnvelope.DataOffset(payload,legacyWidth);
                        if(kind=="ZK"&&contentOffset>=0&&(MatchingBCContent(payload.Skip(contentOffset).Take(payload.Length-contentOffset-4).ToArray()).Length>0 || incomingBCPatterns.Find(MatchBytes.GetString(payload,contentOffset,payload.Length-contentOffset-4)).Length>0))
                        {skippedBC.Add(rowIndex);plan.Skipped++;plan.Warnings.Add("第"+(rowIndex+2)+"行ZK数据区包含库内完整BC令数据区，跳过入库");continue;}
                        if(legacyWidth!=0&&CommandEnvelope.Valid(payload,legacyWidth)&&legacyWidth==CommandEnvelope.InterfaceTypeBytes((ushort)(payload[20]|payload[21]<<8)))legacyWidth=0;
                        if(kind=="未分类"){if(code.Length>0 || destinations.Any(ip=>DestinationKind(ip)!=""))throw new ArgumentException("未分类指令的编号应为空，目的IP不能属于BC、PSS或KC");}
                        else{if(!new[]{"BC","PSS","KC","YKE","YKM","ZK"}.Contains(kind) || (stc?(!new[]{"ZK","BC","KC","PSS"}.Contains(kind) || !CommandEnvelope.Valid(payload,legacyWidth)):IsInjectionKind(kind)?!CommandEnvelope.IsInjection(payload):kind!="ZK"&&destinations.Any(ip=>DestinationKind(ip)!=kind)))throw new ArgumentException("指令类别与目的IP或封装格式不符");if(code.Length>0){long number=CodeNumber(kind,code,true);code=CategoryCode(kind,number);}}
                        ObservedCommand command;
                        // File imports identify commands by every byte of the full UDP payload.
                        if(code.Length>0 && codes.ContainsKey(code))
                        {command=codes[code].entry;if(command.PayloadHex!=hex)throw new ArgumentException("编号已被另一份完整UDP源码占用："+code);}
                        else if(!exactPayloads.TryGetValue(hex,out command)){if(candidate.NextNumber<1 || candidate.NextNumber==Int64.MaxValue)throw new ArgumentException("内部指令编号超出范围");command=new ObservedCommand{Number=candidate.NextNumber++,Name=name,DestinationIP=destinations[0],PayloadHex=hex,Length=payload.Length,STC=stc,InterfaceBytes=legacyWidth,FormatIssue=legacyWidth!=0?"旧STC接口长度需要核对，原始源码保留，请更正后重新入库":"",FirstSeenUtc=DateTime.UtcNow.ToString("o")};candidate.Commands.Add(command);exactPayloads[hex]=command;plan.AddedCommands++;}
                        if(stc && !command.STC)throw new ArgumentException("该完整UDP源码已属于其他格式指令，不能通过CSV改为STC格式");
                        string nameKey=command.Number.ToString(CultureInfo.InvariantCulture);
                        if(names.TryGetValue(nameKey,out previousName) && previousName!=name)throw new ArgumentException("同一源码在CSV中的指令名称不一致");names[nameKey]=name;
                        if(code.Length>0 && codes.ContainsKey(code) && codes[code].entry!=command)throw new ArgumentException("编号已被另一份源码占用："+code);
                        var category=command.Categories.FirstOrDefault(c=>c.Kind==kind);
                        if(category!=null && code.Length>0 && category.Code!=code)throw new ArgumentException("该源码已有"+kind+"编号"+category.Code+"，添加不会重新编号");
                        if((IsFlexibleKind(kind)||stc) && category==null && code.Length==0){long next;candidate.NextCategoryNumbers.TryGetValue(kind,out next);if(next==0)next=1;if(next<1 || next==Int64.MaxValue)throw new ArgumentException(kind+"编号已超出范围");code=CategoryCode(kind,next);while(codes.ContainsKey(code)){if(++next==Int64.MaxValue)throw new ArgumentException(kind+"编号已超出范围");code=CategoryCode(kind,next);}candidate.NextCategoryNumbers[kind]=next+1;}
                        if(kind!="未分类" && category==null && code.Length>0){category=new CommandCategory{Kind=kind,Code=code};command.Categories.Add(category);codes[code]=new{entry=command,category};}
                        if(command.Name!=name){command.Name=name;plan.Renamed++;}
                        foreach(string ip in destinations)if(!command.DestinationIPs.Contains(ip)){command.DestinationIPs.Add(ip);plan.AddedDestinations++;}
                        if(plan.Preview.Count<100)plan.Preview.Add(new{row=rowIndex+2,code,name,kind,destinations,payloadLength=payload.Length});
                    }
                    catch(Exception ex){plan.Errors.Add("第"+(rowIndex+2)+"行："+ex.Message);}
                }
                if(plan.Errors.Count==0)
                {
                    int before=configuration.Commands.Sum(c=>c.Categories.Count);try{NormalizeCategories(candidate);plan.AddedCategories=candidate.Commands.Sum(c=>c.Categories.Count)-before;plan.Preview.Clear();foreach(var row in rows.Where((r,i)=>!skippedBC.Contains(i)).Take(100)){byte[] payload=String.IsNullOrWhiteSpace(row[5])?new byte[0]:PacketCodec.Hex(row[5]);string hex=Hex(payload);string requestedCode=row[0].Trim().ToUpperInvariant();var command=requestedCode.Length>0 && codes.ContainsKey(requestedCode)?codes[requestedCode].entry:exactPayloads[hex];string kind=row[2].Trim().ToUpperInvariant();string code=kind=="未分类"?"":command.Categories.First(c=>c.Kind==kind).Code;plan.Preview.Add(new{code,name=command.Name,kind,subtype=CommandEnvelope.BCSubtype(code),destinations=row[4],payloadLength=command.Length});}}catch(Exception ex){plan.Errors.Add(ex.Message);}
                }
                return plan;
            }
        }
        public CommandImportPlan PreviewBCWorkbook(byte[] bytes)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);var book=WorkbookReader.ReadBCCommands(bytes,configuration.SourceIP);
                var plan=book.BCommands+book.BroadcastCommands==0?new CommandImportPlan{Revision=revision,Configuration=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(configuration))}:PreviewCsv(book.Csv);
                plan.BCommands=book.BCommands;plan.BroadcastCommands=book.BroadcastCommands;plan.Expanded=book.Expanded;plan.Skipped=book.Skipped;plan.Warnings.AddRange(book.Warnings);plan.Errors.AddRange(book.Errors);
                if(book.BCommands+book.BroadcastCommands==0)plan.Errors.Add("没有可导入的有效指令");return plan;
            }
        }
        public CommandImportPlan PreviewInjection(byte[] bytes,string filename,string name,string code,string destination,string kind="")
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);
                byte[] packet=CommandEnvelope.BuildInjection(bytes);destination=IPv4(String.IsNullOrWhiteSpace(destination)?"192.168.1.113":destination.Trim());
                string stem=Path.GetFileNameWithoutExtension(Path.GetFileName((filename??"").Replace('\\','/')));
                var identifiers=Regex.Matches(stem,@"(?<![A-Za-z0-9])(?:YKE|YKM)[0-9]+(?![A-Za-z0-9])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
                bool ambiguous=identifiers.Count>1 && String.IsNullOrWhiteSpace(code);
                if(identifiers.Count==1)
                {
                    var identifier=identifiers[0];
                    if(String.IsNullOrWhiteSpace(code))code=identifier.Value.ToUpperInvariant();
                    if(String.IsNullOrWhiteSpace(name))name=stem.Substring(identifier.Index+identifier.Length).TrimStart(' ','\t','-','_','－','—','–').Trim();
                }
                if(String.IsNullOrWhiteSpace(name))name=stem;
                code=(code??"").Trim().ToUpperInvariant();kind=(kind??"").Trim().ToUpperInvariant();
                if(kind.Length==0)kind=code.StartsWith("YKM",StringComparison.Ordinal)?"YKM":"YKE";
                if(!IsInjectionKind(kind))throw new ArgumentException("注入指令类别应为YKE或YKM");
                var csv=new StringBuilder("指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\r\n");
                string hex=Hex(packet);csv.AppendLine(String.Join(",",new[]{code??"",name??"",kind,configuration.SourceIP,destination,hex}.Select(Monitor.CsvCell)));
                var plan=PreviewCsv(new UTF8Encoding(false).GetBytes(csv.ToString()));
                if(ambiguous)plan.Errors.Add("文件名含有多个YKE/YKM编号，请手动指定指令编号后重新预览");
                plan.InjectionBytes=bytes.Length;plan.InjectionHex=hex;plan.InjectionChecksum=Hex(packet.Skip(packet.Length-4).ToArray());return plan;
            }
        }
        public void ApplyCsv(CommandImportPlan plan)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);if(plan.Errors.Count>0)throw new ArgumentException("CSV存在错误，未导入");if(plan.Revision!=revision)throw new InvalidOperationException("指令库已变化，请重新读取预览");
                var previous=configuration;configuration=plan.Configuration;
                try{SaveFile(true);}catch{configuration=previous;throw;}
                index.Clear();foreach(var entry in configuration.Commands)AddIndex(entry,entry.Length==0?new byte[0]:PacketCodec.Hex(entry.PayloadHex));revision++;error="";
            }
        }
        internal CommandImportPlan PreviewClear(long expectedRevision)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);if(expectedRevision!=revision)throw new InvalidOperationException("指令库已变化，请刷新后重新确认清空");
                // Keep internal IDs monotonic so an old manual binding cannot point to a new command.
                return new CommandImportPlan{Revision=revision,Configuration=new ObservedCommandConfiguration{SourceIP=configuration.SourceIP,NextNumber=configuration.NextNumber}};
            }
        }
        internal object ClearPreview(string tcRevision,int bindings)
        {lock(gate){if(!ready)throw new InvalidOperationException(error);return new{revision,tcRevision,count=configuration.Commands.Count,bindings};}}
        public object CatalogPage(int page,string kind,string search)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);if(!new[]{"","BC","PSS","KC","YKE","YKM","ZK","未分类","B指令","广播令"}.Contains(kind))throw new ArgumentException("指令类别筛选无效");var rows=new List<CommandCatalogRow>();
                foreach(var entry in configuration.Commands)
                {
                    foreach(var category in entry.Categories)rows.Add(new CommandCatalogRow{number=entry.Number,code=category.Code,name=entry.Name,kind=category.Kind,stc=entry.STC,formatIssue=entry.FormatIssue,destinations=category.DestinationIPs.ToArray(),length=entry.Length,hexPreview=entry.PayloadHex.Length>95?entry.PayloadHex.Substring(0,95)+" …":entry.PayloadHex});
                var other=entry.DestinationIPs.Where(ip=>!entry.Categories.Any(c=>c.DestinationIPs.Contains(ip))).ToArray();if(other.Length>0)rows.Add(new CommandCatalogRow{number=entry.Number,name=entry.Name,kind="未分类",destinations=other,length=entry.Length,hexPreview=entry.PayloadHex.Length>95?entry.PayloadHex.Substring(0,95)+" …":entry.PayloadHex});
                }
                rows=rows.OrderBy(r=>String.IsNullOrEmpty(r.code)?1:0)
                    .ThenBy(r=>r.kind,StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r=>String.IsNullOrEmpty(r.code)?0:CodeNumber(r.kind,r.code,true))
                    .ThenBy(r=>r.name,CommandNameOrder).ThenBy(r=>r.number).ToList();
                var filtered=rows.Where(r=>(kind=="" || r.kind==kind || r.subtype==kind) && (r.code+" "+r.name+" "+String.Join(" ",r.destinations)).IndexOf(search??"",StringComparison.OrdinalIgnoreCase)>=0).ToArray();int pages=Math.Max(1,(filtered.Length+49)/50);page=Math.Max(1,Math.Min(page,pages));return new{revision,total=rows.Count,sourceCount=configuration.Commands.Count,matching=filtered.Length,page,pages,sourceIP=configuration.SourceIP,rows=filtered.Skip((page-1)*50).Take(50).ToArray()};
            }
        }
    }
    public sealed class CommandCatalogRow
            {public long number;public string code="",name,kind,hexPreview,formatIssue="";public string[] destinations;public int length;public bool stc;public string subtype{get{return CommandEnvelope.BCSubtype(code);}}}
    public sealed partial class Monitor
    {
        internal object CommandClearPreview()
        {lock(Gate)return Outgoing.Commands.ClearPreview(commandBindingRevision,tcBindings.Bindings.Count);}
        internal void ClearCommandCatalog(long revision,string tcRevision)
        {
            lock(Gate)
            {
                if(Listening || Demo || Outgoing.IsActive || judgmentRunning)throw new InvalidOperationException("请先停止接收和判读，再清空指令配置");
                if(tcStorageError.Length>0)throw new InvalidOperationException(tcStorageError);
                if(tcRevision!=commandBindingRevision)throw new InvalidOperationException("TC绑定已变化，请重新确认清空");
                var plan=Outgoing.Commands.PreviewClear(revision);var previous=tcBindings;var empty=new TCBindingConfig{Revision=Guid.NewGuid().ToString("N")};
                JsonFile.Save(TCPath,empty);
                try{Outgoing.Commands.ApplyCsv(plan);}
                catch
                {
                    try{JsonFile.Save(TCPath,previous);}catch(Exception ex){tcStorageError="清空失败且TC绑定恢复失败，请重启前检查配置："+ex.Message;}
                    throw;
                }
                tcBindings=empty;commandBindingRevision=empty.Revision;ruleBindings.Clear();
            }
        }
        internal string CommandTCRevision {get{lock(Gate)return commandBindingRevision;}}
        internal string CommandImportError(CommandImportPlan plan)
        {
            lock(Gate)
            {
                foreach(var entry in plan.Configuration.Commands)foreach(var category in entry.Categories)if(tcBindings.Bindings.Any(b=>b.Code==category.Code && !entry.MatchesNumber(b.Number)))return category.Code+"已作为其他指令的手动TC代号使用，请先修改该手动绑定";return "";
            }
        }
        internal void ImportCommandCatalog(CommandImportPlan plan)
        {
            lock(Gate)
            {
                if(Listening || Demo || Outgoing.IsActive || judgmentRunning)throw new InvalidOperationException("请先停止接收和判读，再导入指令库；读取预览可以提前进行");string issue=CommandImportError(plan);if(issue.Length>0)throw new ArgumentException(issue);Outgoing.Commands.ApplyCsv(plan);ruleBindings.Clear();
            }
        }
    }
}
