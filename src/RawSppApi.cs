using System;
using System.IO;
using System.Net;
using System.Text;
namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleRawSpp(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;if(!path.StartsWith("/api/raw-spp/",StringComparison.Ordinal))return false;
            string packetId=Query(ctx.Request,"packetId");
            if(path=="/api/raw-spp/list"&&method=="GET")
            {int page;if(!Int32.TryParse(Query(ctx.Request,"page"),out page))page=1;Json(ctx.Response,monitor.RawSppList(packetId,page),200);}
            else if(path=="/api/raw-spp/detail"&&method=="GET")
            {long id;if(!Int64.TryParse(Query(ctx.Request,"id"),out id))throw new ArgumentException("请选择原包记录");Json(ctx.Response,monitor.RawSppDetail(id),200);}
            else if(path=="/api/raw-spp/clear"&&method=="POST")
            {var body=Body(ctx.Request);monitor.ClearRawSpp(Text(body,"packetId"));Json(ctx.Response,new{ok=true},200);}
            else if(path=="/api/raw-spp/export.csv"&&method=="GET")
            {ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=raw-spp.csv";using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))monitor.WriteRawSppCsv(writer,packetId);}
            else Json(ctx.Response,new{error="SPP原包监测接口不存在"},404);return true;
        }
    }
}
