using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    internal sealed class CommandCsvPreview
    {public string Token,Username,TCRevision;public DateTime Created;public CommandImportPlan Plan;}
    public sealed partial class Server
    {
        private readonly Dictionary<string,CommandCsvPreview> commandCsvPreviews=new Dictionary<string,CommandCsvPreview>();
        private static object CommandPreviewResponse(CommandCsvPreview preview)
        {
            var plan=preview.Plan;
            return new{token=preview.Token,total=plan.Rows,addedCommands=plan.AddedCommands,addedCategories=plan.AddedCategories,addedDestinations=plan.AddedDestinations,renamed=plan.Renamed,bCommands=plan.BCommands,broadcastCommands=plan.BroadcastCommands,expanded=plan.Expanded,skipped=plan.Skipped,skippedEmpty=plan.XmlEmptyRows,skippedExisting=plan.XmlExistingRows,skippedBC=plan.XmlBCRows,warnings=plan.Warnings,injectionBytes=plan.InjectionBytes,packetHex=plan.InjectionHex,checksumHex=plan.InjectionChecksum,rows=plan.Preview,xmlRows=plan.XmlRows,errors=plan.Errors,canApply=plan.Errors.Count==0&&(plan.XmlRows==null||plan.AddedCommands>0)};
        }
        private static byte[] CommandCsvBody(HttpListenerRequest request)
        {
            bool injection=request.QueryString["format"]=="injection-bin";int maximum=injection?CommandEnvelope.MaximumInjectionBytes:64*1024*1024;
            string error=injection?"单条UDP注入的BIN文件最多"+maximum+"字节；更大文件需要另行配置分包规则":"指令导入文件最大64MB";
            if(request.ContentLength64>maximum)throw new ArgumentException(error);using(var memory=new MemoryStream()){var buffer=new byte[65536];int read;while((read=request.InputStream.Read(buffer,0,buffer.Length))>0){if(memory.Length+read>maximum)throw new ArgumentException(error);memory.Write(buffer,0,read);}return memory.ToArray();}
        }
        private bool HandleCommandCatalog(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;var store=monitor.Outgoing.Commands;
            if(path=="/api/send-monitor/commands/clear-preview" && method=="GET")
            {Json(ctx.Response,monitor.CommandCategoryClearPreview(Query(ctx.Request,"kind")),200);return true;}
            if(path=="/api/send-monitor/commands/clear" && method=="POST")
            {
                var body=Body(ctx.Request);long revision;
                if(Text(body,"confirm")!="clear-command-catalog" || !Int64.TryParse(Text(body,"revision"),out revision) || !body.ContainsKey("tcRevision"))throw new ArgumentException("请先读取并确认清空指令配置");
                lock(commandGate){monitor.ClearCommandCategory(Text(body,"kind"),revision,Text(body,"tcRevision"));commandCsvPreviews.Clear();}
                monitor.Note("info","已清空"+(Text(body,"kind").Length==0?"全部":Text(body,"kind"))+"指令配置及相关TC绑定，保留判读规则与发送记录；操作人："+auth.User(session).DisplayName);Json(ctx.Response,new{ok=true},200);return true;
            }
            if(path=="/api/send-monitor/commands/list" && method=="GET")
            {int page=1;string text=Query(ctx.Request,"page");if(text.Length>0 && !Int32.TryParse(text,out page))throw new ArgumentException("指令页码无效");Json(ctx.Response,store.CatalogPage(page,Query(ctx.Request,"kind"),Query(ctx.Request,"search")),200);return true;}
            if(path=="/api/send-monitor/commands/edit" && method=="POST")
            {
                var body=Body(ctx.Request);long number,revision;
                if(!Int64.TryParse(Text(body,"number"),out number) || !Int64.TryParse(Text(body,"revision"),out revision))throw new ArgumentException("指令记录号或编辑版本无效");
                object result;lock(commandGate){result=monitor.EditCommand(number,Text(body,"originalCode"),Text(body,"code"),Text(body,"name"),revision,Text(body,"destinationIPs"));commandCsvPreviews.Clear();}
                monitor.Note("info","已修改指令编号或名称，操作人："+auth.User(session).DisplayName);Json(ctx.Response,result,200);return true;
            }
            if(path=="/api/send-monitor/commands/export.csv" && method=="GET")
            {ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=command-catalog.csv";using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))store.ExportCsv(writer);return true;}
            if(path=="/api/send-monitor/commands/preview" && method=="POST")
            {
                DateTime requestedAt=DateTime.UtcNow;
                byte[] bytes=CommandCsvBody(ctx.Request);lock(commandGate)
                {
                    foreach(string token in commandCsvPreviews.Where(p=>(p.Value.Username==session.Username&&p.Value.Created<=requestedAt) || (DateTime.UtcNow-p.Value.Created).TotalMinutes>10).Select(p=>p.Key).ToArray())commandCsvPreviews.Remove(token);if(commandCsvPreviews.Count>=8)throw new InvalidOperationException("当前指令导入预览较多，请稍后重试");
                    string format=Query(ctx.Request,"format");CommandImportPlan plan;
                    if(format=="bc-xlsx")plan=store.PreviewBCWorkbook(bytes);
                    else if(format=="injection-bin")plan=store.PreviewInjection(bytes,Query(ctx.Request,"filename"),Query(ctx.Request,"name"),Query(ctx.Request,"code"),Query(ctx.Request,"destinationIP"),Query(ctx.Request,"kind"));
                    else if(format=="stc-xml")plan=store.PreviewSTCXml(bytes,Query(ctx.Request,"kind"),Query(ctx.Request,"destinationIP"),Query(ctx.Request,"code"),(ushort)ObservedCommands.STCNumber(Query(ctx.Request,"frameCounter").Length==0?"0x00AA":Query(ctx.Request,"frameCounter"),UInt16.MaxValue,"帧计数"));
                    else if(format=="stc-manual")
                    {
                        var input=JsonFile.Decode<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));
                        if(input==null)throw new ArgumentException("缺少手动指令内容");
                        plan=store.PreviewSTCManual(Text(input,"name"),Text(input,"bujianid"),Text(input,"gongnengid"),Text(input,"datastr"),Text(input,"kind"),Text(input,"destinationIP"),Text(input,"code"),(ushort)ObservedCommands.STCNumber(Text(input,"frameCounter").Length==0?"0x00AA":Text(input,"frameCounter"),UInt16.MaxValue,"帧计数"));
                    }
                    else if(format.Length==0)plan=store.PreviewCsv(bytes);
                    else throw new ArgumentException("不支持的指令导入格式");
                    string issue=monitor.CommandImportError(plan);if(issue.Length>0)plan.Errors.Add(issue);var preview=new CommandCsvPreview{Token=Auth.RandomToken(),Username=session.Username,TCRevision=monitor.CommandTCRevision,Created=DateTime.UtcNow,Plan=plan};commandCsvPreviews[preview.Token]=preview;Json(ctx.Response,CommandPreviewResponse(preview),200);
                }return true;
            }
            if(path=="/api/send-monitor/commands/stc-recheck" && method=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)
                {
                    CommandCsvPreview preview;object value;
                    if(!commandCsvPreviews.TryGetValue(Text(body,"token"),out preview) || preview.Username!=session.Username || (DateTime.UtcNow-preview.Created).TotalMinutes>10 || preview.Plan.XmlSource==null)throw new ArgumentException("XML预览已过期，请重新读取文件");
                    if(!body.TryGetValue("rows",out value))throw new ArgumentException("缺少XML预览编辑内容");
                    var old=preview.Plan;var edits=JsonFile.Decode<List<STCImportEdit>>(JsonFile.Encode(value));
                    var plan=store.PreviewSTCXml(old.XmlSource,old.XmlKind,old.XmlDestination,old.XmlCode,old.XmlCounter,edits,old.XmlCheckBC);
                    string issue=monitor.CommandImportError(plan);if(issue.Length>0)plan.Errors.Add(issue);
                    preview.Plan=plan;preview.Created=DateTime.UtcNow;preview.TCRevision=monitor.CommandTCRevision;Json(ctx.Response,CommandPreviewResponse(preview),200);
                }return true;
            }
            if(path=="/api/send-monitor/commands/preview/cancel" && method=="POST")
            {
                var body=Body(ctx.Request);string token=Text(body,"token");lock(commandGate)
                {CommandCsvPreview preview;if(commandCsvPreviews.TryGetValue(token,out preview)&&preview.Username==session.Username)commandCsvPreviews.Remove(token);}
                Json(ctx.Response,new{ok=true},200);return true;
            }
            if(path=="/api/send-monitor/commands/apply" && method=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)
                {
                    CommandCsvPreview preview;if(!commandCsvPreviews.TryGetValue(Text(body,"token"),out preview) || preview.Username!=session.Username || (DateTime.UtcNow-preview.Created).TotalMinutes>10)throw new ArgumentException("指令预览已过期，请重新读取");if(preview.Plan.XmlRows!=null&&preview.Plan.AddedCommands==0)throw new ArgumentException("本次XML没有需要入库的指令，已有指令保持原样");if(preview.TCRevision!=monitor.CommandTCRevision)throw new InvalidOperationException("TC绑定已变化，请重新读取预览");monitor.ImportCommandCatalog(preview.Plan);commandCsvPreviews.Remove(preview.Token);
                }monitor.Note("info","已批量导入指令库，操作人："+auth.User(session).DisplayName);Json(ctx.Response,new{ok=true},200);return true;
            }
            return false;
        }
    }
}
