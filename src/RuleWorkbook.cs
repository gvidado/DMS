using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace TelemetryMonitor
{
    public static partial class WorkbookReader
    {
        public static List<ContinuousRule> ReadRules(byte[] bytes,string filename)
        {
            var rules=new List<ContinuousRule>();int matched=0;
            using(var memory=new MemoryStream(bytes))using(var zip=new ZipArchive(memory,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count>3000 || zip.Entries.Sum(e=>e.Length)>64L*1024*1024)throw new ArgumentException("规则Excel解压体积过大");
                var shared=new List<string>();if(zip.GetEntry("xl/sharedStrings.xml")!=null)foreach(var item in Xml(zip,"xl/sharedStrings.xml").Descendants(S+"si"))shared.Add(String.Concat(item.Descendants(S+"t").Select(t=>t.Value)));
                var rel=Xml(zip,"xl/_rels/workbook.xml.rels").Root.Elements().ToDictionary(e=>(string)e.Attribute("Id"));
                var sheets=Xml(zip,"xl/workbook.xml").Descendants(S+"sheet").ToArray();if(sheets.Length>128)throw new ArgumentException("规则工作表超过128张");
                foreach(var sheet in sheets)
                {
                    string sheetName=(string)sheet.Attribute("name"),rid=(string)sheet.Attribute(R+"id");XElement relation;
                    if(rid==null || !rel.TryGetValue(rid,out relation) || (string)relation.Attribute("TargetMode")=="External")throw new ArgumentException("规则工作表关联无效");
                    var uri=new Uri(new Uri("http://xlsx.local/xl/workbook.xml"),(string)relation.Attribute("Target"));
                    if(uri.Host!="xlsx.local" || !uri.AbsolutePath.StartsWith("/xl/",StringComparison.Ordinal))throw new ArgumentException("规则工作表路径无效");
                    var headers=new Dictionary<int,string>();int number=0;
                    foreach(var row in Xml(zip,uri.AbsolutePath.TrimStart('/')).Descendants(S+"row"))
                    {
                        if(++number>25000)throw new ArgumentException("单张规则表超过25000行");
                        int rowNumber;if(!Int32.TryParse((string)row.Attribute("r"),out rowNumber))rowNumber=number;
                        var cells=new Dictionary<int,string>();foreach(var cell in row.Elements(S+"c")){int col=Column((string)cell.Attribute("r")??"");if(col>0 && col<=256)cells[col]=CellValue(cell,shared);}
                        if(headers.Count==0)
                        {
                            if(number>50)break;
                            var keys=cells.ToDictionary(c=>c.Key,c=>Key(c.Value));
                            if(new[]{"序号","参数代号","参数名称","判读级别","是否判读","判读规则"}.All(keys.Values.Contains)){headers=keys;matched++;}
                            continue;
                        }
                        var values=new Dictionary<string,string>();foreach(var header in headers){string value;if(header.Value.Length>0)values[header.Value]=cells.TryGetValue(header.Key,out value)?value:"";}
                        Func<string,string> get=name=>values.ContainsKey(name)?values[name]:"";
                        if(String.IsNullOrWhiteSpace(get("参数代号")) && String.IsNullOrWhiteSpace(get("参数名称")) && String.IsNullOrWhiteSpace(get("判读规则")))continue;
                        string flag=get("是否判读").Trim();bool enabled=flag=="是" || flag=="1" || flag.Equals("true",StringComparison.OrdinalIgnoreCase) || flag=="启用";
                        bool validFlag=enabled || flag=="否" || flag=="0" || flag.Equals("false",StringComparison.OrdinalIgnoreCase) || flag=="停用" || flag=="";
                        rules.Add(new ContinuousRule { Id=Guid.NewGuid().ToString("N"),Code=get("参数代号").Trim(),Name=get("参数名称").Trim(),
                            Level=String.IsNullOrWhiteSpace(get("判读级别"))?"一般":get("判读级别").Trim(),Enabled=enabled,
                            Script=get("判读规则"),ImportError=validFlag?"":"是否判读列无效："+flag,Description=get("描述"),SourceFile=filename,SourceSheet=sheetName,SourceRow=rowNumber,ModifiedAt=BeijingTime.Now.ToString("o") });
                        if(rules.Count>5000)throw new ArgumentException("规则超过5000条");
                    }
                }
            }
            if(matched==0)throw new ArgumentException("未找到完整六列表头：序号、参数代号、参数名称、判读级别、是否判读、判读规则");
            if(rules.Count==0)throw new ArgumentException("规则表没有数据行");return rules;
        }
    }
}
