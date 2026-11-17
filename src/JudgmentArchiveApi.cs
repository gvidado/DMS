using System;
using System.IO;
using System.Net;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private bool HandleJudgmentArchives(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath;if(!path.StartsWith("/api/judgment-archives",StringComparison.Ordinal))return false;
            if(ctx.Request.HttpMethod!="GET"){Json(ctx.Response,new{error="存档仅支持读取和导出"},405);return true;}
            int page=1;string input=Query(ctx.Request,"page");if(input.Length>0 && !Int32.TryParse(input,out page))throw new ArgumentException("存档页码无效");
            if(path=="/api/judgment-archives")
            {string type=Query(ctx.Request,"type");Json(ctx.Response,monitor.ArchiveList(page,Query(ctx.Request,"from"),Query(ctx.Request,"to"),Query(ctx.Request,"name"),type.Length==0?"all":type),200);}
            else if(path=="/api/judgment-archives/detail")Json(ctx.Response,monitor.ArchiveDetail(Query(ctx.Request,"id"),page),200);
            else if(path=="/api/judgment-archives/export.csv")
            {
                ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=judgment-archive.csv";
                using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))monitor.ExportJudgmentArchive(writer,Query(ctx.Request,"id"));
            }
            else if(path=="/api/judgment-archives/download")
            {
                string id=Query(ctx.Request,"id");byte[] data=monitor.ArchiveDownload(id);ctx.Response.ContentType="application/octet-stream";ctx.Response.Headers["Content-Disposition"]="attachment; filename="+id.Replace(':','_')+(id.StartsWith("continuous:",StringComparison.Ordinal)?".json":".jsonl");ctx.Response.ContentLength64=data.Length;ctx.Response.OutputStream.Write(data,0,data.Length);
            }
            else Json(ctx.Response,new{error="存档接口不存在"},404);return true;
        }
    }
}
