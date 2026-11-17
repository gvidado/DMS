using System;
using System.IO;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleSendMonitor(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath, method = ctx.Request.HttpMethod;
            if (!path.StartsWith("/api/send-monitor/", StringComparison.Ordinal)) return false;
            if(HandleCommandCatalog(ctx,session))return true;
            var target = monitor.Outgoing;
            if (path == "/api/send-monitor/interfaces" && method == "GET") Json(ctx.Response, SendMonitor.Interfaces(), 200);
            else if (path == "/api/send-monitor/state" && method == "GET") Json(ctx.Response, SendMonitorConfigurationView(target), 200);
            else if (path == "/api/send-monitor/list" && method == "GET") Json(ctx.Response, target.List(), 200);
            else if (path == "/api/send-monitor/commands/export" && method == "GET")
            {
                ctx.Response.ContentType = "application/json; charset=utf-8"; ctx.Response.SendChunked = true;
                ctx.Response.Headers["Content-Disposition"] = "attachment; filename=observed-commands.json";
                using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false), 65536, true)) target.Commands.Export(writer);
            }
            else if (path == "/api/send-monitor/detail" && method == "GET")
            {
                long id; if (!Int64.TryParse(Query(ctx.Request, "id"), out id)) throw new ArgumentException("记录号无效");
                Json(ctx.Response, target.Detail(id), 200);
            }
            else if (path == "/api/send-monitor/export" && method == "GET")
            {
                ctx.Response.ContentType = "text/csv; charset=utf-8"; ctx.Response.SendChunked = true;
                ctx.Response.Headers["Content-Disposition"] = "attachment; filename=outgoing-udp-recent-200.csv";
                using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(true), 65536, true)) target.WriteCsv(writer);
            }
            else if (path == "/api/send-monitor/config" && method == "POST")
            {
                var configuration = JsonFile.Decode<SendMonitorSettings>(JsonFile.Encode(Body(ctx.Request)));
                lock (commandGate)
                {
                    if (monitor.Listening || monitor.Demo || target.IsActive) throw new InvalidOperationException("请先点击页面顶部“停止接收”，再修改发送监视配置");
                    target.Save(configuration);
                }
                monitor.Note("info", "发送监视配置已保存，计数和最近记录已清空；操作员：" + auth.User(session).DisplayName);
                Json(ctx.Response, SendMonitorConfigurationView(target), 200);
            }
            else if (path == "/api/send-monitor/control" && method == "POST")
            {
                string action = Text(Body(ctx.Request), "action");
                lock(configurationEditGate)lock (commandGate)
                {
                    try
                    {
                        // Older clients use these aliases; both now operate the complete live session.
                        if(action=="start"&&!ApplyIdleConfiguration())throw new InvalidOperationException(configurationError);
                        if (action == "start") monitor.Start();
                        else if (action == "stop") {monitor.Stop();ApplyIdleConfiguration();}
                        else if (action == "clear")
                        {
                            if (monitor.Listening || monitor.Demo || target.IsActive) throw new InvalidOperationException("请先点击页面顶部“停止接收”，再清零计数和记录");
                            target.Clear();
                        }
                        else throw new ArgumentException("未知发送监视操作");
                    }
                    catch (Exception ex) { if (action == "start") monitor.Note("error", "UDP接收与发送监视统一启动失败：" + ex.Message); throw; }
                }
                Json(ctx.Response, target.Info(), 200);
            }
            else Json(ctx.Response, new { error = "发送监视接口不存在" }, 404);
            return true;
        }
        private object SendMonitorConfigurationView(SendMonitor target)
        {
            if(configurationContext==null)return target.Info();
            var view=JsonFile.Decode<System.Collections.Generic.Dictionary<string,object>>(JsonFile.Encode(liveMonitor.Outgoing.Info()));view["configuration"]=target.ConfigurationCopy();return view;
        }
    }
}
