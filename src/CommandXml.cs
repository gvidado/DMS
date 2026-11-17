using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    public sealed class STCImportEdit
    {public int index;public string name,kind,code,destination;}
    public sealed class STCImportRow
    {
        public int index,line,payloadLength,interfaceBytes;
        public string name="",kind="ZK",code="",destination="",functionID="",interfaceType="",dataHex="",packetHex="",status="";
        public List<string> errors=new List<string>(),matches=new List<string>();
    }
    internal sealed class STCImportPattern
    {internal string Data,Label;}

    public sealed partial class ObservedCommands
    {
        internal static uint STCNumber(string text,uint maximum,string field)
        {
            text=(text??"").Trim();uint value;
            bool hex=text.StartsWith("0x",StringComparison.OrdinalIgnoreCase);
            if(!UInt32.TryParse(hex?text.Substring(2):text,hex?NumberStyles.AllowHexSpecifier:NumberStyles.Integer,CultureInfo.InvariantCulture,out value) || value>maximum)
                throw new ArgumentException(field+"须为0～"+maximum+"的十进制数或0x十六进制数");
            return value;
        }
        private static string STCBinary(byte[] bytes)
        {return Encoding.GetEncoding(28591).GetString(bytes);}
        internal CommandImportPlan PreviewSTCManual(string name,string partID,string interfaceType,string data,string kind,string destination,string code,ushort frameCounter=0x00AA)
        {
            kind=String.IsNullOrWhiteSpace(kind)?"ZK":kind.Trim().ToUpperInvariant();
            name=String.IsNullOrWhiteSpace(name)?"手动"+kind+"指令":name.Trim();
            var document=new XDocument(new XElement("Root",new XElement("STCVars",new XElement("STCVar",
                new XAttribute("name",name),new XAttribute("bujianid",partID??""),new XAttribute("gongnengid",interfaceType??""),new XAttribute("datastr",data??"")))));
            return PreviewSTCXml(Encoding.UTF8.GetBytes(document.ToString()),kind,destination,code,frameCounter);
        }
        internal CommandImportPlan PreviewSTCXml(byte[] bytes,string kind,string destination,string firstCode,ushort frameCounter=0x00AA,List<STCImportEdit> edits=null,bool checkBC=true)
        {
            if(bytes==null || bytes.Length==0 || bytes.Length>64*1024*1024)throw new ArgumentException("STC XML文件不能为空且最大64MB");
            kind=String.IsNullOrWhiteSpace(kind)?"ZK":kind.Trim().ToUpperInvariant();
            if(!new[]{"ZK","BC","KC","PSS"}.Contains(kind))throw new ArgumentException("STC入库类别应为ZK、BC、KC或PSS");
            destination=IPv4(String.IsNullOrWhiteSpace(destination)?(kind=="KC"?"192.168.1.137":kind=="BC"?"192.168.1.113":"192.168.1.111"):destination.Trim());
            firstCode=(firstCode??"").Trim().ToUpperInvariant();
            XDocument document;
            try
            {
                var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=64L*1024*1024};
                using(var memory=new MemoryStream(bytes,false))using(var reader=XmlReader.Create(memory,settings))document=XDocument.Load(reader,LoadOptions.SetLineInfo);
            }
            catch(XmlException ex){throw new ArgumentException("XML格式错误，第"+ex.LineNumber+"行第"+ex.LinePosition+"列："+ex.Message);}
            var nodes=document.Descendants().Where(e=>e.Name.LocalName=="STCVar" && e.Parent!=null && e.Parent.Name.LocalName=="STCVars").ToArray();
            if(nodes.Length==0 || nodes.Length>20000)throw new ArgumentException("XML的STCVars部分应包含1～20000条STCVar指令");
            var overrides=new Dictionary<int,STCImportEdit>();
            foreach(var edit in edits??new List<STCImportEdit>())
            {if(edit==null || edit.index<1 || edit.index>nodes.Length || overrides.ContainsKey(edit.index))throw new ArgumentException("XML预览编辑的行号无效或重复");overrides.Add(edit.index,edit);}
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);
                var patterns=new List<STCImportPattern>();
                foreach(var command in configuration.Commands)
                {
                    var payload=command.Length==0?new byte[0]:PacketCodec.Hex(command.PayloadHex);
                    if(payload.Length==0)continue;
                    string codes=String.Join("/",command.Categories.Select(c=>c.Code));
                    patterns.Add(new STCImportPattern{Data=STCBinary(payload),Label=(codes.Length==0?"未分类#"+command.Number:codes)+" "+command.Name});
                }
                var counters=new Dictionary<string,long>(configuration.NextCategoryNumbers);
                if(firstCode.Length>0)counters[kind]=CodeNumber(kind,firstCode,true);
                var occupied=new HashSet<string>(configuration.Commands.SelectMany(c=>c.Categories.Select(k=>k.Code)));
                var rows=new List<STCImportRow>();
                var csv=new StringBuilder("指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX,指令格式\r\n");
                foreach(var node in nodes)
                {
                    var row=new STCImportRow{index=rows.Count+1,line=((IXmlLineInfo)node).LineNumber,name=((string)node.Attribute("name")??"").Trim(),kind=kind,destination=destination,
                        functionID=(string)node.Attribute("bujianid")??"",interfaceType=(string)node.Attribute("gongnengid")??""};
                    rows.Add(row);
                    string hex=(string)node.Attribute("datastr")??"";
                    if(String.IsNullOrWhiteSpace(hex)){row.status="数据内容为空，跳过";continue;}
                    try
                    {
                        STCImportEdit edit;
                        if(overrides.TryGetValue(row.index,out edit))
                        {row.name=(edit.name??"").Trim();row.kind=(edit.kind??"").Trim().ToUpperInvariant();row.code=(edit.code??"").Trim().ToUpperInvariant();row.destination=(edit.destination??"").Trim();}
                        byte[] data=PacketCodec.Hex(hex);row.dataHex=Hex(data);
                        if(checkBC)
                        {
                            var bc=MatchingBCContent(data);
                            if(bc.Length>0)
                            {
                                row.status="BC数据重复，跳过";row.matches=bc.Take(30).Select(c=>String.Join("/",c.Categories.Where(k=>k.Kind=="BC").Select(k=>k.Code))+" "+c.Name+"（数据区完整包含）").ToList();
                                if(bc.Length>30)row.matches.Add("另有"+(bc.Length-30)+"条BC匹配");continue;
                            }
                        }
                        ushort functionID=(ushort)STCNumber(row.functionID,UInt16.MaxValue,"bujianid（功能ID）");
                        row.interfaceBytes=CommandEnvelope.InterfaceTypeBytes(functionID);
                        uint interfaceType=STCNumber(row.interfaceType,row.interfaceBytes==4?UInt32.MaxValue:UInt16.MaxValue,"gongnengid（接口类型，"+row.interfaceBytes+"字节）");
                        byte[] packet=CommandEnvelope.BuildSTC(functionID,interfaceType,data,frameCounter);row.packetHex=Hex(packet);row.payloadLength=packet.Length;
                        string binary=STCBinary(packet);
                        // Structured file/manual imports require complete UDP byte equality.
                        var matches=patterns.Where(p=>binary==p.Data).ToArray();
                        if(matches.Length>0)
                        {row.status="已存在，跳过";row.matches=matches.Take(30).Select(p=>p.Label).ToList();if(matches.Length>30)row.matches.Add("另有"+(matches.Length-30)+"条匹配");continue;}
                        if(row.name.Length==0 || row.name.Length>256 || row.name.IndexOf('\0')>=0)throw new ArgumentException("指令名称须为1～256个字符");
                        if(!new[]{"ZK","BC","KC","PSS"}.Contains(row.kind))throw new ArgumentException("类别应为ZK、BC、KC或PSS");
                        row.destination=IPv4(row.destination);
                        if(row.code.Length==0)
                        {
                            long next;if(!counters.TryGetValue(row.kind,out next))next=1;
                            if(row.kind=="BC" && next>=2000 && next<=3999)next=4000;
                            if(next<1 || next==Int64.MaxValue)throw new ArgumentException(row.kind+"编号已超出范围");
                            row.code=CategoryCode(row.kind,next);
                            while(occupied.Contains(row.code))
                            {if(++next==Int64.MaxValue)throw new ArgumentException(row.kind+"编号已超出范围");if(row.kind=="BC" && next==2000)next=4000;row.code=CategoryCode(row.kind,next);}
                            counters[row.kind]=next+1;
                        }
                        else
                        {long number=CodeNumber(row.kind,row.code,true);row.code=CategoryCode(row.kind,number);long next;counters.TryGetValue(row.kind,out next);counters[row.kind]=Math.Max(next,number+1);}
                        if(!occupied.Add(row.code))throw new ArgumentException("指令编号已被占用："+row.code);
                        csv.AppendLine(String.Join(",",new[]{row.code,row.name,row.kind,configuration.SourceIP,row.destination,row.packetHex,"STC"}.Select(Monitor.CsvCell)));
                        row.status="待入库";patterns.Add(new STCImportPattern{Data=binary,Label=row.code+" "+row.name+"（本次导入）"});
                    }
                    catch(ArgumentException ex){row.status="读取错误";row.errors.Add(ex.Message);}
                }
                var accepted=rows.Where(r=>r.status=="待入库").ToArray();
                var plan=accepted.Length>0?PreviewCsv(new UTF8Encoding(false).GetBytes(csv.ToString())):
                    new CommandImportPlan{Revision=revision,Configuration=Serializer().Deserialize<ObservedCommandConfiguration>(Serializer().Serialize(configuration))};
                plan.XmlEmptyRows=rows.Count(r=>r.status=="数据内容为空，跳过");plan.XmlExistingRows=rows.Count(r=>r.status=="已存在，跳过");
                plan.XmlBCRows=rows.Count(r=>r.status=="BC数据重复，跳过");plan.XmlCheckBC=checkBC;
                plan.Rows=rows.Count;plan.Skipped=plan.XmlEmptyRows+plan.XmlExistingRows+plan.XmlBCRows;plan.XmlRows=rows;plan.XmlSource=(byte[])bytes.Clone();
                plan.XmlKind=kind;plan.XmlDestination=destination;plan.XmlCode=firstCode;plan.XmlCounter=frameCounter;
                foreach(var row in rows)foreach(string issue in row.errors)plan.Errors.Add("STC第"+row.index+"项（XML第"+row.line+"行）："+issue);
                if(plan.XmlEmptyRows>0)plan.Warnings.Add("已跳过"+plan.XmlEmptyRows+"项datastr为空的STC指令，不入库、不占用编号");
                if(plan.XmlBCRows>0)plan.Warnings.Add("已优先跳过"+plan.XmlBCRows+"项数据区包含完整BC令数据区的XML指令，不占用编号，不修改已有BC名称、源码或目的IP");
                if(plan.XmlExistingRows>0)plan.Warnings.Add("已跳过"+plan.XmlExistingRows+"项完整UDP源码与已有指令完全相同的STC指令，未修改已有名称、源码、目的IP或编号");
                return plan;
            }
        }
    }
}
