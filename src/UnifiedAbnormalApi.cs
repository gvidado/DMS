using System;
using System.IO;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleUnifiedAbnormals(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath;
            if(!path.StartsWith("/api/abnormal-records",StringComparison.Ordinal))return false;
            if(path=="/api/abnormal-records" && ctx.Request.HttpMethod=="GET")
            {
                int page=1,size=50;long judgment=0,alarm=0,sequence=0;string value=Query(ctx.Request,"page");
                if(value!="" && !Int32.TryParse(value,out page))throw new ArgumentException("异常记录页码无效");
                value=Query(ctx.Request,"pageSize");if(value!="" && !Int32.TryParse(value,out size))throw new ArgumentException("异常记录分页数量无效");
                value=Query(ctx.Request,"judgmentThrough");if(value!="" && !Int64.TryParse(value,out judgment))throw new ArgumentException("判读记录游标无效");
                value=Query(ctx.Request,"alarmThrough");if(value!="" && !Int64.TryParse(value,out alarm))throw new ArgumentException("告警记录游标无效");
                value=Query(ctx.Request,"sequenceThrough");if(value!="" && !Int64.TryParse(value,out sequence))throw new ArgumentException("序列异常记录游标无效");
                string type=Query(ctx.Request,"type"),status=Query(ctx.Request,"status"),level=Query(ctx.Request,"level");
                Json(ctx.Response,monitor.UnifiedAbnormalPage(page,size,type==""?"all":type,status==""?"all":status,Query(ctx.Request,"search"),judgment,alarm,Query(ctx.Request,"knownRevision"),level==""?"all":level,sequence),200);
            }
            else if(path=="/api/abnormal-records/release" && ctx.Request.HttpMethod=="POST")
            {
                var body=Body(ctx.Request);lock(commandGate)monitor.ReleaseUnifiedAbnormal(Text(body,"key"),Text(body,"reason"),session.Username,auth.User(session).DisplayName);
                Json(ctx.Response,new{ok=true},200);
            }
            else if(path=="/api/abnormal-records/release-all" && ctx.Request.HttpMethod=="POST")
            {
                var body=Body(ctx.Request);object result;
                lock(commandGate)result=monitor.ReleaseAllUnifiedAbnormals(Text(body,"reason"),session.Username,auth.User(session).DisplayName);
                Json(ctx.Response,result,200);
            }
            else if(path=="/api/abnormal-records/export.csv" && ctx.Request.HttpMethod=="GET")
            {
                ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;
                ctx.Response.Headers["Content-Disposition"]="attachment; filename=abnormal-records.csv";
                using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))monitor.ExportUnifiedAbnormals(writer);
            }
            else Json(ctx.Response,new{error="统一异常记录接口不存在"},404);
            return true;
        }
    }
}
