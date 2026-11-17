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
    internal sealed class SequenceConversionEntry
    {public string sheet,before,after,status,reason,keywordBefore,keywordAfter,markBefore,markAfter;public int row;public bool warning;}
    public sealed partial class Monitor
    {
        internal List<SequenceConversionEntry> ConvertSequenceCommands(IEnumerable<SequenceDefinition> definitions)
        {
            lock(Gate)
            {
                var entries=Outgoing.Commands.BindingEntries();
                var commandCodes=new HashSet<string>(StringComparer.Ordinal);
                foreach(var entry in entries)foreach(var category in entry.Categories)commandCodes.Add(category.Code);
                foreach(var binding in tcBindings.Bindings)if(entries.Any(e=>e.MatchesNumber(binding.Number)))commandCodes.Add(binding.Code);
                var pattern=commandCodes.Count==0?null:new Regex(@"(?<![A-Za-z0-9_])(?:[A-Za-z_][A-Za-z0-9_]*\.)*Y?(?<code>"+String.Join("|",commandCodes.OrderByDescending(c=>c.Length).Select(Regex.Escape))+@")(?![A-Za-z0-9_])",RegexOptions.IgnoreCase);
                Func<string,List<string>> match=text=>
                {
                    // Only catalog codes qualify. Command names never establish a match.
                    string exact=text.Trim();if(exact.StartsWith("TC.",StringComparison.Ordinal))exact=exact.Substring(3);
                    if(commandCodes.Contains(exact))return new List<string>{exact};
                    var codes=new HashSet<string>();
                    if(pattern!=null)foreach(Match found in pattern.Matches(text))foreach(string code in commandCodes.Where(c=>String.Equals(c,found.Groups["code"].Value,StringComparison.OrdinalIgnoreCase)))codes.Add(code);
                    return codes.OrderBy(c=>c,StringComparer.Ordinal).ToList();
                };
                var report=new List<SequenceConversionEntry>();
                foreach(var definition in definitions)foreach(var row in definition.Rows)
                {
                    if(String.Equals((row.Keyword??"").Trim(),"CALL",StringComparison.OrdinalIgnoreCase))continue;
                    var misplaced=new[]{"Keyword","Command","Condition","Expected","Timeout"}.Where(field=>String.Equals(((string)typeof(SequenceRow).GetField(field).GetValue(row)??"").Trim(),"T0",StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(misplaced.Length>0)
                    {
                        string previousMark=row.Mark??"",previousKeyword=row.Keyword;
                        bool conflict=!String.IsNullOrWhiteSpace(previousMark) && !previousMark.Trim().Equals("T0",StringComparison.OrdinalIgnoreCase);
                        if(!conflict)
                        {
                            row.Mark="T0";if(row.CellErrors!=null)row.CellErrors.Remove("Mark");
                            foreach(string field in misplaced){typeof(SequenceRow).GetField(field).SetValue(row,"");if(row.CellErrors!=null)row.CellErrors.Remove(field);}
                            if(String.IsNullOrWhiteSpace(row.Keyword) && String.IsNullOrWhiteSpace(row.Command) && String.IsNullOrWhiteSpace(row.Condition))row.Keyword="TEXT";
                        }
                        report.Add(new SequenceConversionEntry{sheet=definition.Sheet,row=row.Row,before=row.Command,after=row.Command,keywordBefore=previousKeyword,keywordAfter=row.Keyword,markBefore=previousMark,markAfter=row.Mark,status=conflict?"T0位置冲突":"已移至标志列",reason=conflict?"标志列已有其他内容，未覆盖；请在预览中修改后重新检测":"独立T0已写入标志列并清除原位置；无指令和判据的空关键字行设为TEXT，运行到达时建立T0"});
                        if(conflict)continue;
                    }
                    string before=(row.Command??"").Trim(),keywordBefore=row.Keyword;
                    string keyword=(row.Keyword??"").Trim().ToUpperInvariant();
                    bool pause=keyword=="PLUSE"||keyword=="PAUSE";
                    bool control=new[]{"PAR","CHANGE","HOLD"}.Contains(keyword);
                    bool parameter=Regex.IsMatch(before,@"^(TM|TME|TMB|this)\.",RegexOptions.IgnoreCase)
                        || (before.Length==0&&!String.IsNullOrWhiteSpace(row.Condition));
                    var commandCandidates=parameter?new List<string>():match(before);
                    var descriptionCandidates=match(row.Description??"")
                        .Where(code=>Regex.IsMatch(code,@"^(BC[0-9]+|YKE[0-9]+|PSS[0-9]+|ZK[0-9]+)$",RegexOptions.IgnoreCase)).ToList();
                    string status="",reason="";bool warning=false;
                    if(pause)row.Keyword="PLUSE";
                    var missing=Regex.Matches(before,@"(?<![A-Za-z0-9_])(?:[A-Za-z_][A-Za-z0-9_]*\.)*(?<code>(?:YKE|YKM)[0-9]*)(?![A-Za-z0-9_])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)
                        .Cast<Match>().Select(m=>m.Groups["code"].Value.ToUpperInvariant()).Distinct()
                        .Where(code=>!commandCodes.Contains(code)).ToArray();
                    if(missing.Length>0&&!parameter)
                        report.Add(new SequenceConversionEntry{sheet=definition.Sheet,row=row.Row,before=before,after=row.Command,
                            status="注入指令未入库",reason="第"+row.Row+"行YKE/YKM指令未入库请确认指令代号："+String.Join("、",missing),
                            keywordBefore=keywordBefore,keywordAfter=row.Keyword,warning=true});
                    if(parameter&&!pause)
                    {status="保留参数判读";reason="纯参数判读行保留参数、判据和关键字，不从说明中推断发令";}
                    else if(commandCandidates.Count==1)
                    {
                        try
                        {
                            ResolveCommandCode(commandCandidates[0]);row.Command=commandCandidates[0];
                            if(row.CellErrors!=null)row.CellErrors.Remove("Command");
                            if(!pause&&keyword=="TEXT")row.Keyword="";
                            status=before==row.Command?"已是库内编号":"已替换";
                            reason="优先采用指令列中的库内编号，无需说明列重复出现";
                            if(pause)reason+="；PLUSE先暂停，人工继续后发送一次指令";
                        }
                        catch(ArgumentException ex)
                        {warning=true;reason=ex.Message;status="指令无法解析";if(!pause&&!control)row.Keyword="TEXT";}
                    }
                    else if(control)
                    {status="保留控制行";reason="保留"+keyword+"控制逻辑；未唯一匹配的原指令保留供人工核对";warning=before.Length>0;}
                    else if(pause)
                    {
                        status="保留PLUSE";reason=before.Length==0?"PLUSE保留人工暂停，不从说明列自动补填指令":"PLUSE保留人工暂停，指令列未唯一匹配，请在预览中修正";
                        warning=before.Length>0;
                    }
                    else if(commandCandidates.Count>1)
                    {row.Keyword="TEXT";status="已设为TEXT";warning=true;reason="指令列存在多个匹配，请手动选择："+String.Join("、",commandCandidates);}
                    else if(descriptionCandidates.Count==1)
                    {
                        try
                        {
                            ResolveCommandCode(descriptionCandidates[0]);row.Command=descriptionCandidates[0];
                            if(row.CellErrors!=null)row.CellErrors.Remove("Command");
                            if(keyword=="TEXT")row.Keyword="";
                            status=before.Length==0?"已补填":"已从说明替换";
                            reason="指令列为空或未命中；说明列包含唯一库内BC/YKE/PSS/ZK编号，已填入指令列，无需“发送”字样";
                        }
                        catch(ArgumentException ex){row.Keyword="TEXT";status="已设为TEXT";warning=true;reason=ex.Message;}
                    }
                    else
                    {
                        row.Keyword="TEXT";status="已设为TEXT";
                        reason=descriptionCandidates.Count==0?"指令列及说明列未找到可用库内编号，关键字置TEXT，保留原内容"
                            :"说明列存在多个匹配，请手动选择："+String.Join("、",descriptionCandidates);
                        warning=descriptionCandidates.Count>1;
                    }
                    if(row.CellErrors!=null&&row.Keyword!=keywordBefore)row.CellErrors.Remove("Keyword");
                    report.Add(new SequenceConversionEntry{sheet=definition.Sheet,row=row.Row,before=before,after=row.Command,
                        status=status,reason=reason,keywordBefore=keywordBefore,keywordAfter=row.Keyword,warning=warning});
                }
                return report;
            }
        }
    }
    public static partial class WorkbookReader
    {
        // Export literal strings only: a sequence expression must never become an Excel formula.
        internal static byte[] ExportConvertedSequences(List<SequenceDefinition> definitions)
        {
            XNamespace s="http://schemas.openxmlformats.org/spreadsheetml/2006/main",r="http://schemas.openxmlformats.org/officeDocument/2006/relationships",p="http://schemas.openxmlformats.org/package/2006/relationships",c="http://schemas.openxmlformats.org/package/2006/content-types";
            var book=new XElement(s+"workbook",new XAttribute(XNamespace.Xmlns+"r",r),new XElement(s+"sheets"));var rels=new XElement(p+"Relationships");
            var types=new XElement(c+"Types",new XElement(c+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(c+"Default",new XAttribute("Extension","xml"),new XAttribute("ContentType","application/xml")),new XElement(c+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));
            using(var memory=new MemoryStream())
            {
                using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
                {
                    Action<string,XElement> write=(name,xml)=>{using(var stream=zip.CreateEntry(name).Open())new XDocument(new XDeclaration("1.0","utf-8","yes"),xml).Save(stream);};
                    for(int i=0;i<definitions.Count;i++)
                    {
                        var definition=definitions[i];string id="rId"+(i+1),target="worksheets/sheet"+(i+1)+".xml";
                        book.Element(s+"sheets").Add(new XElement(s+"sheet",new XAttribute("name",definition.Sheet),new XAttribute("sheetId",i+1),new XAttribute(r+"id",id)));
                        rels.Add(new XElement(p+"Relationship",new XAttribute("Id",id),new XAttribute("Type",r.NamespaceName+"/worksheet"),new XAttribute("Target",target)));
                        types.Add(new XElement(c+"Override",new XAttribute("PartName","/xl/"+target),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
                        var data=new XElement(s+"sheetData");
                        Action<int,string[]> add=(number,values)=>{var row=new XElement(s+"row",new XAttribute("r",number));for(int col=0;col<7;col++)row.Add(new XElement(s+"c",new XAttribute("r",((char)('A'+col)).ToString()+number),new XAttribute("t","inlineStr"),new XElement(s+"is",new XElement(s+"t",new XAttribute(XNamespace.Xml+"space","preserve"),values[col]??""))));data.Add(row);};
                        add(1,new[]{"关键字","测试工作程序内容及要求","指令或参数","判读要求","标志","期望执行时间","超时时间"});
                        foreach(var row in definition.Rows)add(row.Row,new[]{row.Keyword,row.Description,row.Command,row.Condition,row.Mark,row.Expected,row.Timeout});
                        var cols=new XElement(s+"cols");for(int col=1;col<=7;col++)cols.Add(new XElement(s+"col",new XAttribute("min",col),new XAttribute("max",col),new XAttribute("width",col==2?50:col==4?55:20),new XAttribute("customWidth",1)));
                        write("xl/"+target,new XElement(s+"worksheet",cols,data));
                    }
                    write("xl/workbook.xml",book);write("xl/_rels/workbook.xml.rels",rels);write("[Content_Types].xml",types);
                    write("_rels/.rels",new XElement(p+"Relationships",new XElement(p+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",r.NamespaceName+"/officeDocument"),new XAttribute("Target","xl/workbook.xml"))));
                }
                return memory.ToArray();
            }
        }
    }
}
