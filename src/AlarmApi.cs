using System;
using System.IO;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleAlarms(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (!path.StartsWith("/api/alarms/", StringComparison.Ordinal)) return false;
            if (path == "/api/alarms/list" && ctx.Request.HttpMethod == "GET")
            {
                int page = 1, size = 50; long through = 0; string value = Query(ctx.Request, "page");
                if (value != "" && !Int32.TryParse(value, out page)) throw new ArgumentException("告警页码无效");
                value = Query(ctx.Request, "pageSize"); if (value != "" && !Int32.TryParse(value, out size)) throw new ArgumentException("告警分页数量无效");
                value = Query(ctx.Request, "throughId"); if (value != "" && !Int64.TryParse(value, out through)) throw new ArgumentException("告警记录游标无效");
                string status = Query(ctx.Request, "status");
                Json(ctx.Response, monitor.AlarmHistoryPage(page, size, status == "" ? "all" : status, through), 200);
            }
            else if(path=="/api/alarms/ack" && ctx.Request.HttpMethod=="POST")
            {
                var body=Body(ctx.Request);long id;if(!Int64.TryParse(Text(body,"id"),out id) || id<1)throw new ArgumentException("告警记录号无效");
                monitor.AcknowledgeAlarm(id,session.Username,auth.User(session).DisplayName);Json(ctx.Response,new{ok=true},200);
            }
            else if (path == "/api/alarms/export" && ctx.Request.HttpMethod == "GET")
            {
                ctx.Response.ContentType = "text/csv; charset=utf-8"; ctx.Response.SendChunked = true;
                ctx.Response.Headers["Content-Disposition"] = "attachment; filename=alarm-history.csv";
                using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(true), 65536, true)) monitor.WriteAlarmHistoryCsv(writer);
            }
            else Json(ctx.Response, new { error = "告警历史接口不存在" }, 404);
            return true;
        }
    }
}
