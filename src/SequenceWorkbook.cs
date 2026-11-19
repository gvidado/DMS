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
        private static int[] SequenceSourceColumns(Dictionary<int,string> cells)
        {
            Func<int,string> heading=col=>System.Text.RegularExpressions.Regex.Replace(
                cells.ContainsKey(col)?cells[col]:"",@"\s+","");
            if(heading(1)!="关键字"
                ||!new[]{"测试工作程序内容及要求","测试工作内容及要求","测试工作程序内容","测试工作内容"}.Contains(heading(2))
                ||!new[]{"指令","指令或参数"}.Contains(heading(3))
                ||!new[]{"判读要求","判读"}.Contains(heading(4)))return null;
            // Identify the layout by its named action/time columns, never by used-range
            // width or by the contents of the four deliberately ignored columns.
            if(heading(6)=="标志"&&heading(7)=="期望执行时间"&&new[]{"超时时间","超时"}.Contains(heading(8)))
                return new[]{1,2,3,4,6,7,8};
            if(heading(5)=="标志"&&heading(6)=="期望执行时间"&&new[]{"超时时间","超时"}.Contains(heading(7)))
                return new[]{1,2,3,4,5,6,7};
            return null;
        }
        private static IEnumerable<KeyValuePair<string,XDocument>> SequenceSheets(ZipArchive zip)
        {
            var relations=Xml(zip,"xl/_rels/workbook.xml.rels").Root.Elements().ToDictionary(e=>(string)e.Attribute("Id"));
            var sheets=Xml(zip,"xl/workbook.xml").Descendants(S+"sheet").ToArray();
            if(sheets.Length>64)throw new ArgumentException("序列工作表超过64张");
            foreach(var sheet in sheets)
            {
                string name=(string)sheet.Attribute("name"),rid=(string)sheet.Attribute(R+"id");
                XElement relation;
                if(String.IsNullOrWhiteSpace(name)||rid==null||!relations.TryGetValue(rid,out relation)||(string)relation.Attribute("TargetMode")=="External")
                    throw new ArgumentException("工作表关联无效");
                var uri=new Uri(new Uri("http://xlsx.local/xl/workbook.xml"),(string)relation.Attribute("Target"));
                if(uri.Host!="xlsx.local" || !uri.AbsolutePath.StartsWith("/xl/",StringComparison.Ordinal))
                    throw new ArgumentException("工作表路径无效");
                yield return new KeyValuePair<string,XDocument>(name,Xml(zip,uri.AbsolutePath.TrimStart('/')));
            }
        }

        private static List<string> SequenceSharedStrings(ZipArchive zip)
        {
            if(zip.Entries.Count>3000 || zip.Entries.Sum(e=>e.Length)>64L*1024*1024)
                throw new ArgumentException("序列Excel解压体积过大");
            return zip.GetEntry("xl/sharedStrings.xml")==null ? new List<string>() :
                Xml(zip,"xl/sharedStrings.xml").Descendants(S+"si")
                    .Select(item=>String.Concat(item.Descendants(S+"t").Select(t=>t.Value))).ToList();
        }

        public static List<SequenceDefinition> ReadSequences(byte[] bytes,string filename,string mode)
        {
            var result=new List<SequenceDefinition>();
            using(var memory=new MemoryStream(bytes))using(var zip=new ZipArchive(memory,ZipArchiveMode.Read))
            {
                var shared=SequenceSharedStrings(zip);
                foreach(var sheet in SequenceSheets(zip))
                {
                    string name=sheet.Key;
                    var definition=new SequenceDefinition{Id=Guid.NewGuid().ToString("N"),Name=Path.GetFileNameWithoutExtension(filename)+(name.Trim()=="默认分段"?"":" / "+name),Filename=filename,Sheet=name,Mode=mode};
                    bool header=false;int count=0;int[] sourceColumns=null;
                    var keys=new[]{"Keyword","Description","Command","Condition","Mark","Expected","Timeout"};
                    var sourceFields=new Dictionary<int,string>();
                    foreach(var row in sheet.Value.Descendants(S+"row"))
                    {
                        if(++count>10000)throw new ArgumentException("单张序列表超过10000行");int rowNumber;if(!Int32.TryParse((string)row.Attribute("r"),out rowNumber))rowNumber=count;
                        var cellErrors=new Dictionary<string,string>();
                        var cells=new Dictionary<int,string>();foreach(var cell in row.Elements(S+"c"))
                        {
                            int col=Column((string)cell.Attribute("r")??"");string field=null;
                            if(header){if(!sourceFields.TryGetValue(col,out field))continue;}
                            else if(col<1||col>8)continue;
                            var formula=cell.Element(S+"f");
                            if(formula!=null){cells[col]="="+formula.Value;if(field!=null)cellErrors[field]="第"+col+"列含Excel计算公式，请在预览中改为文本内容";}
                            else try{cells[col]=CellValue(cell,shared);if(field!=null&&(string)cell.Attribute("t")=="e")cellErrors[field]="第"+col+"列为Excel错误值："+cells[col];}
                            catch(Exception ex)
                            {
                                if(!(ex is ArgumentException)&&!(ex is FormatException)&&!(ex is OverflowException))throw;
                                cells[col]=cell.Value;if(field!=null)cellErrors[field]="第"+col+"列单元格内容无法解析，请在预览中修改";
                            }
                        }
                        Func<int,string> get=col=>cells.ContainsKey(col)?cells[col].Trim():"";
                        if(!header)
                        {
                            if(count>50)break;
                            sourceColumns=SequenceSourceColumns(cells);
                            if(sourceColumns!=null)
                            {header=true;for(int field=0;field<keys.Length;field++)sourceFields[sourceColumns[field]]=keys[field];}
                            continue;
                        }
                        if(cells.Values.All(String.IsNullOrWhiteSpace))continue;
                        definition.Rows.Add(new SequenceRow{Row=rowNumber,Keyword=get(sourceColumns[0]),Description=get(sourceColumns[1]),Command=get(sourceColumns[2]),Condition=get(sourceColumns[3]),Mark=get(sourceColumns[4]),Expected=get(sourceColumns[5]),Timeout=get(sourceColumns[6]),CellErrors=cellErrors});
                        if(definition.Rows.Count>5000)throw new ArgumentException("单个序列超过5000项");
                    }
                    if(header)result.Add(definition);
                }
            }
            if(result.Count==0)throw new ArgumentException("未找到序列表头。支持七列A～G，或十一列中的A/B/C/D/F/G/H：关键字、测试工作内容、指令、判读要求、标志、期望执行时间、超时时间；十一列的E/I/J/K不读取");return result;
        }
    }
}
