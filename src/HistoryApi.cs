using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private HistoryStorage historyStorage;
        private string historyStartupError="";
        private sealed class HistoryExportTicket
        {internal string Username,Format="csv";internal DateTime Created;internal HistoryQuery Query;}
        private readonly object historyExportGate=new object();
        private readonly Dictionary<string,HistoryExportTicket> historyExportTickets=new Dictionary<string,HistoryExportTicket>();
        private void InitializeHistoryStorage()
        {
            try { historyStorage=new HistoryStorage(root); }
            catch(Exception error)
            {
                if(!IsHistoryInitializationFailure(error))throw;
                historyStartupError="历史存储初始化失败："+error.Message;
                liveMonitor.Note("error",historyStartupError);
                return;
            }
            liveMonitor.AttachHistory(historyStorage);liveTransmitter.Recorded+=liveMonitor.HistoryTransmission;
            historyStorage.StorageFault+=message=>{try{liveMonitor.Stop();liveMonitor.Note("error",message);}catch{}};
            try { RecoverHistoryImport(); }
            catch(Exception error)
            { if(!IsHistoryInitializationFailure(error))throw;liveMonitor.Note("warn","旧导入记录读取失败："+error.Message); }
        }
        private static bool IsHistoryInitializationFailure(Exception error)
        {return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException;}
        private HistoryQuery ReadHistoryQuery(HttpListenerRequest request)
        {
            int size;Int32.TryParse(Query(request,"size"),out size);return new HistoryQuery{Type=String.IsNullOrEmpty(Query(request,"type"))?"samples":Query(request,"type"),AllTime=HistoryTrue(Query(request,"allTime")),Start=Query(request,"start"),End=Query(request,"end"),Experiment=Query(request,"experiment"),Source=Query(request,"source"),Destination=Query(request,"destination"),Direction=Query(request,"direction"),Namespace=Query(request,"namespace"),Parameter=Query(request,"parameter"),Packet=Query(request,"packet"),Command=Query(request,"command"),Status=Query(request,"status"),Session=Query(request,"session"),TimeBasis=Query(request,"timeBasis"),Quality=Query(request,"quality"),Cursor=Query(request,"cursor"),Size=size>0?size:100};
        }
        private static bool HistoryTrue(string value){return String.Equals(value,"true",StringComparison.OrdinalIgnoreCase)||value=="1";}
        private static string HistoryQueryValue(Dictionary<string,object> body,string key)
        {object value;if(!body.TryGetValue(key,out value)||value==null)return "";return value is string?(string)value:value is System.Collections.IEnumerable?HistoryJson.Encode(value):Convert.ToString(value,CultureInfo.InvariantCulture);}
        private HistoryQuery ReadHistoryBody(HttpListenerRequest request)
        {
            var body=Body(request,4*1024*1024);int size;Int32.TryParse(HistoryQueryValue(body,"size"),out size);
            return new HistoryQuery{Type=HistoryQueryValue(body,"type")==""?"samples":HistoryQueryValue(body,"type"),AllTime=HistoryTrue(HistoryQueryValue(body,"allTime")),Start=HistoryQueryValue(body,"start"),End=HistoryQueryValue(body,"end"),Experiment=HistoryQueryValue(body,"experiment"),Source=HistoryQueryValue(body,"source"),Destination=HistoryQueryValue(body,"destination"),Direction=HistoryQueryValue(body,"direction"),Namespace=HistoryQueryValue(body,"namespace"),Parameter=HistoryQueryValue(body,"parameter"),Packet=HistoryQueryValue(body,"packet"),Command=HistoryQueryValue(body,"command"),Status=HistoryQueryValue(body,"status"),Session=HistoryQueryValue(body,"session"),TimeBasis=HistoryQueryValue(body,"timeBasis"),Quality=HistoryQueryValue(body,"quality"),Cursor=HistoryQueryValue(body,"cursor"),Size=size>0?size:100};
        }
        private object PrepareHistoryExport(HttpListenerRequest request,Session session)
        {
            var query=ReadHistoryBody(request);query.Cursor="";query.Validate();string ticket=Auth.RandomToken();DateTime now=DateTime.UtcNow;
            string format=query.Type=="wide"?"xlsx":"csv";
            lock(historyExportGate)
            {
                foreach(string key in historyExportTickets.Where(p=>(now-p.Value.Created).TotalMinutes>10).Select(p=>p.Key).ToArray())historyExportTickets.Remove(key);
                foreach(string key in historyExportTickets.Where(p=>p.Value.Username==session.Username).OrderByDescending(p=>p.Value.Created).Skip(15).Select(p=>p.Key).ToArray())historyExportTickets.Remove(key);
                while(historyExportTickets.Count>=64)historyExportTickets.Remove(historyExportTickets.OrderBy(p=>p.Value.Created).First().Key);
                historyExportTickets[ticket]=new HistoryExportTicket{Username=session.Username,Created=now,Query=query.Copy(),Format=format};
            }
            return new{url="/api/history/export."+format+"?ticket="+Uri.EscapeDataString(ticket),expiresInSeconds=600};
        }
        private HistoryQuery ResolveHistoryExport(HttpListenerRequest request,Session session,string format="csv")
        {
            string token=Query(request,"ticket");if(token.Length==0)return ReadHistoryQuery(request);
            lock(historyExportGate){HistoryExportTicket ticket;if(!historyExportTickets.TryGetValue(token,out ticket)||ticket.Username!=session.Username||ticket.Format!=format||(DateTime.UtcNow-ticket.Created).TotalMinutes>10)throw new ArgumentException("导出请求已过期或不属于当前账号，请重新点击导出");return ticket.Query.Copy();}
        }
        private bool HandleHistoryStorage(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;if(!path.StartsWith("/api/history/",StringComparison.Ordinal))return false;
            if(historyStorage==null)throw new InvalidOperationException(historyStartupError);
            if(HandleHistoryImport(ctx,session))return true;
            if(HandleHistoryDataset(ctx,session))return true;
            if(path=="/api/history/status"&&method=="GET"){Json(ctx.Response,historyStorage.Status(),200);return true;}
            if(path=="/api/history/query"&&(method=="GET"||method=="POST")){Json(ctx.Response,historyStorage.QueryHistory(method=="POST"?ReadHistoryBody(ctx.Request):ReadHistoryQuery(ctx.Request)),200);return true;}
            if(path=="/api/history/options"&&method=="GET"){Json(ctx.Response,historyStorage.QueryHistoryOptions(Query(ctx.Request,"refresh")=="true"),200);return true;}
            if(path=="/api/history/export-ticket"&&method=="POST"){Json(ctx.Response,PrepareHistoryExport(ctx.Request,session),200);return true;}
            if(path=="/api/history/detail"&&method=="GET"){Json(ctx.Response,historyStorage.HistoryDetail(Query(ctx.Request,"id")),200);return true;}
            if(path=="/api/history/parameters"&&method=="GET"){Json(ctx.Response,historyStorage.QueryParameters(Query(ctx.Request,"q"),Query(ctx.Request,"namespace"),200),200);return true;}
            if(path=="/api/history/export.csv"&&method=="GET")
            {
                var query=ResolveHistoryExport(ctx.Request,session);query.Validate();ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=history-"+query.Type+".csv";
                try{using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))historyStorage.ExportHistory(query,writer);}catch{ctx.Response.Abort();throw;}return true;
            }
            if(path=="/api/history/export.xlsx"&&method=="GET")
            {
                var query=ResolveHistoryExport(ctx.Request,session,"xlsx");query.Type="wide";query.Validate();
                ctx.Response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";ctx.Response.SendChunked=true;
                ctx.Response.Headers["Content-Disposition"]="attachment; filename=parameter-history.xlsx";
                try{historyStorage.ExportHistoryWorkbook(query,ctx.Response.OutputStream);}
                catch(Exception error){Console.Error.WriteLine("History Excel export: "+error);ctx.Response.Abort();throw;}
                return true;
            }
            if(path=="/api/history/experiments"&&method=="GET"){Json(ctx.Response,historyStorage.Experiments(),200);return true;}
            if(path=="/api/history/experiments"&&method=="POST")
            {
                if(!historyStorage.RecordingEnabled)throw new InvalidOperationException("请先启用历史采集与归档");
                var body=Body(ctx.Request);string action=Text(body,"action");lock(liveMonitor.Gate)
                {
                    if(action=="create")historyStorage.CreateExperiment(Text(body,"name"),Text(body,"notes"));
                    else if(action=="select")historyStorage.SelectExperiment(Text(body,"id"));
                    else if(action=="end")historyStorage.EndExperiment(Text(body,"id"));else throw new ArgumentException("试验操作无效");
                    liveMonitor.HistoryExperimentChanged();
                }
                Json(ctx.Response,historyStorage.Experiments(),200);return true;
            }
            if(path=="/api/history/config")
            {
                if(!auth.User(session).Admin){Json(ctx.Response,new{error="历史存储配置仅管理员可修改和查看"},403);return true;}
                if(method=="GET")
                {var value=historyStorage.Configuration;Json(ctx.Response,new{value.Revision,value.ArchiveDirectory,value.Host,value.Port,value.Database,value.Username,value.DatabaseEnabled,ArchiveEnabled=value.RecordingEnabled,value.FlushMilliseconds,hasPassword=value.EncryptedPassword.Length>0,recommendedDirectory=@"D:\TelemetryHistory",permanent=true},200);return true;}
                var body=Body(ctx.Request);object input;if(!body.TryGetValue("configuration",out input))throw new ArgumentException("缺少历史存储配置");var settings=HistoryJson.Decode<HistoryStorageConfiguration>(HistoryJson.Encode(input));
                if(Text(body,"revision")!=historyStorage.Configuration.Revision)throw new InvalidOperationException("历史存储配置已被其他用户修改，请重新载入");
                string password=body.ContainsKey("password")?Text(body,"password"):null;
                bool monitoring;
                lock(configurationEditGate)lock(commandGate)
                {
                    monitoring=liveMonitor.ConfigurationMonitoringActive;
                    historyStorage.SaveConfiguration(settings,password,monitoring,Text(body,"revision"));
                    if(!monitoring)liveMonitor.HistoryRecordingChanged();
                }
                Json(ctx.Response,new{ok=true,pending=monitoring},200);return true;
            }
            if(path=="/api/history/test"&&method=="POST")
            {
                if(!auth.User(session).Admin){Json(ctx.Response,new{error="仅管理员可测试数据库连接"},403);return true;}var body=Body(ctx.Request);var settings=historyStorage.Configuration;
                Json(ctx.Response,historyStorage.TestConnection(settings,Text(body,"initialize")=="True"),200);return true;
            }
            throw new ArgumentException("历史查询接口不存在");
        }
    }
}
