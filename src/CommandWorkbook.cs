using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    public sealed class BCWorkbookImport
    {
        public byte[] Csv;
        public int BCommands,BroadcastCommands,Expanded,Skipped;
        public List<string> Warnings=new List<string>(),Errors=new List<string>();
    }
    public static partial class WorkbookReader
    {
        public static BCWorkbookImport ReadBCCommands(byte[] bytes,string sourceIP)
        {
            if(bytes==null || bytes.Length==0 || bytes.Length>8*1024*1024)throw new ArgumentException("指令Excel不能为空且最大8MB");
            var result=new BCWorkbookImport();var csv=new StringBuilder("指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\r\n");var codes=new HashSet<string>();int sheetCount=0;
            using(var memory=new MemoryStream(bytes,false))using(var zip=new ZipArchive(memory,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count>3000 || zip.Entries.Sum(e=>e.Length)>64L*1024*1024)throw new ArgumentException("Excel解压体积超出限制");
                var shared=new List<string>();if(zip.GetEntry("xl/sharedStrings.xml")!=null)foreach(var item in Xml(zip,"xl/sharedStrings.xml").Descendants(S+"si"))shared.Add(String.Concat(item.Descendants(S+"t").Select(t=>t.Value)));
                var rel=Xml(zip,"xl/_rels/workbook.xml.rels").Root.Elements().ToDictionary(e=>(string)e.Attribute("Id"));var sheets=Xml(zip,"xl/workbook.xml").Descendants(S+"sheet").ToArray();
                if(sheets.Length<1 || sheets.Length>128)throw new ArgumentException("指令Excel须包含1～128张表");
                foreach(var sheet in sheets)
                {
                    string name=(string)sheet.Attribute("name");bool broadcast=name=="广播令";if(!broadcast && name!="B指令"){result.Warnings.Add("跳过未支持的工作表："+name);continue;}sheetCount++;
                    XElement relation;string rid=(string)sheet.Attribute(R+"id");if(rid==null || !rel.TryGetValue(rid,out relation) || (string)relation.Attribute("TargetMode")=="External")throw new ArgumentException("工作表关联无效："+name);
                    var uri=new Uri(new Uri("http://xlsx.local/xl/workbook.xml"),(string)relation.Attribute("Target"));if(uri.Host!="xlsx.local" || !uri.AbsolutePath.StartsWith("/xl/",StringComparison.Ordinal))throw new ArgumentException("工作表路径无效");
                    Dictionary<int,string> headers=null;int rowCount=0;
                    foreach(var row in Xml(zip,uri.AbsolutePath.TrimStart('/')).Descendants(S+"row"))
                    {
                        if(++rowCount>25000)throw new ArgumentException("单表超过25000行");int line;Int32.TryParse((string)row.Attribute("r"),out line);
                        var cells=row.Elements(S+"c").ToDictionary(c=>Column((string)c.Attribute("r")??""),c=>CellValue(c,shared).Trim());
                        if(headers==null){if(cells.Values.Contains("指令代号") && cells.Values.Contains("指令名称") && cells.Values.Contains("遥控包编码"))headers=cells;continue;}
                        Func<string,string> value=header=>{int col=headers.First(p=>p.Value==header).Key;string v;return cells.TryGetValue(col,out v)?v:"";};
                        string code=value("指令代号").ToUpperInvariant(),title=value("指令名称"),hex=value("遥控包编码");if(code.Length==0 && title.Length==0 && hex.Length==0)continue;
                        string location=name+"!"+line+" "+code+"：";
                        try
                        {
                            int number;if(!Regex.IsMatch(code,@"^BC[0-9]{4}$") || !Int32.TryParse(code.Substring(2),out number))throw new ArgumentException("指令代号应为BC加四位数字");
                            if(broadcast?(number<1 || number>99):(number<2000 || number>3999)){result.Skipped++;result.Warnings.Add(location+"编号超出"+(broadcast?"BC0001～BC0099":"BC2000～BC3999")+"，未导入");continue;}
                            if(title=="预留"){result.Skipped++;result.Warnings.Add(location+"预留项，未导入");continue;}
                            if(title.Length==0)throw new ArgumentException("指令名称为空");if(hex.Length==0 || hex.StartsWith("#EXCEL_ERROR:"))throw new ArgumentException("遥控包编码为空或公式无有效缓存");
                            byte[] spp=PacketCodec.Hex(hex);
                            if(!broadcast && spp.Length==19)
                            {
                                // This workbook compresses the 9-byte fixed field into 11 11 00 01.
                                // Only expand the verified form; reject other compact layouts.
                                if(spp[9]!=0x11 || spp[10]!=0x11 || spp[11]!=0 || spp[12]!=1)throw new ArgumentException("19字节编码不是已确认的B指令紧凑格式");
                                byte[] full=new byte[24];Buffer.BlockCopy(spp,0,full,0,9);Buffer.BlockCopy(PacketCodec.Hex("000000000001080001"),0,full,9,9);Buffer.BlockCopy(spp,13,full,18,6);spp=full;result.Expanded++;
                            }
                            if(spp.Length!=24 || spp[0]!=0x1A || spp[1]!=(broadcast?0x70:0x08) || ((spp[4]<<8)|spp[5])+7!=spp.Length)throw new ArgumentException("遥控包应为24字节，且主导头和包数据长度须与对应指令类型一致");
                            if(!CommandEnvelope.FletcherValid(spp))throw new ArgumentException("遥控包Fletcher-16校验不通过");
                            int opcode=(spp[18]<<8)|spp[19];if(opcode!=number || spp[18]!=spp[20] || spp[19]!=spp[21])throw new ArgumentException("用户数据中的两份指令编号与指令代号不一致");
                            if(!codes.Add(code))throw new ArgumentException("工作簿中存在重复指令编号");
                            byte[] packet=CommandEnvelope.BuildBC(spp,broadcast);string destinations=broadcast?"192.168.1.112;192.168.1.113":"192.168.1.113";
                            csv.AppendLine(String.Join(",",new[]{code,title,"BC",sourceIP,destinations,BitConverter.ToString(packet).Replace('-',' ')}.Select(Monitor.CsvCell)));
                            if(broadcast)result.BroadcastCommands++;else result.BCommands++;
                        }
                        catch(Exception ex){result.Errors.Add(location+ex.Message);}
                    }
                    if(headers==null)result.Errors.Add(name+"：找不到指令代号、指令名称、遥控包编码表头");
                }
            }
            if(sheetCount==0)throw new ArgumentException("工作簿中找不到B指令或广播令工作表");
            if(result.Expanded>0)result.Warnings.Insert(0,"已按给定样例将"+result.Expanded+"条B指令由19字节补齐为24字节，并逐条校验Fletcher-16");
            result.Csv=new UTF8Encoding(false).GetBytes(csv.ToString());return result;
        }
    }
}
