using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Net;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private int replayUploading;
        private bool HandleReplay(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath, method = ctx.Request.HttpMethod;
            if (path.StartsWith("/api/live-abnormal/", StringComparison.Ordinal))
            {
                if (path == "/api/live-abnormal/list" && method == "GET") Json(ctx.Response, monitor.LiveAbnormalList(), 200);
                else if (path == "/api/live-abnormal/detail" && method == "GET")
                { long id; if (!Int64.TryParse(Query(ctx.Request, "id"), out id)) throw new ArgumentException("请选择异常记录"); Json(ctx.Response, monitor.LiveAbnormalDetail(id), 200); }
                else if (path == "/api/live-abnormal/clear" && method == "POST") { monitor.ClearLiveAbnormal(); Json(ctx.Response, new { ok = true }, 200); }
                else if (path == "/api/live-abnormal/export" && method == "GET")
                {
                    ctx.Response.ContentType = "text/csv; charset=utf-8"; ctx.Response.SendChunked = true;
                    ctx.Response.Headers["Content-Disposition"] = "attachment; filename=live-abnormal-spp.csv";
                    using (var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(true), 65536, true)) monitor.WriteLiveAbnormalCsv(writer);
                }
                else Json(ctx.Response, new { error = "实时异常接口不存在" }, 404);
                return true;
            }
            if (!path.StartsWith("/api/replay/", StringComparison.Ordinal)) return false;
            if (path == "/api/replay/source" && method == "GET") { Json(ctx.Response, monitor.ReplayInfo(), 200); return true; }
            if (path == "/api/replay/speed" && method == "POST")
            {
                int speed;
                if (!Int32.TryParse(Text(Body(ctx.Request), "speed"), out speed)) throw new ArgumentException("演示速度应为1～5倍的整数");
                monitor.SetReplaySpeed(speed); Json(ctx.Response, monitor.ReplayInfo(), 200); return true;
            }
            if (path == "/api/replay/clear" && method == "POST") { monitor.SetReplaySource(null); Json(ctx.Response, monitor.ReplayInfo(), 200); return true; }
            if (path == "/api/replay/use-capture" && method == "POST")
            {
                ReplaySource source;
                lock (captureGate)
                {
                    CaptureCheckJob job = FindCapture(Query(ctx.Request, "id"), session);
                    if (job.State != "completed") throw new InvalidOperationException("请先等待文件检验完整完成，再设为演示源");
                    source = new ReplaySource { Filename = job.Filename, Stream = CaptureCheckJob.OpenTemporaryRead(job.Path) };
                }
                try { monitor.SetReplaySource(source); }
                catch { source.Dispose(); throw; }
                Json(ctx.Response, monitor.ReplayInfo(), 200); return true;
            }
            if (path == "/api/replay/upload" && method == "POST")
            {
                string filename = Path.GetFileName(Query(ctx.Request, "filename"));
                if (!String.Equals(Path.GetExtension(filename), ".pcapng", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择.pcapng演示文件");
                lock (monitor.Gate) if (monitor.Listening || monitor.Demo) throw new InvalidOperationException("请先停止监听或演示，再上传演示文件");
                if (Interlocked.CompareExchange(ref replayUploading, 1, 0) != 0) throw new InvalidOperationException("已有演示文件正在上传，请稍后重试");
                ReplaySource candidate = null;
                try
                {
                    string directory = Path.Combine(root, "data", "replay"); Directory.CreateDirectory(directory);
                    candidate = new ReplaySource { Filename = filename, Stream = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pcapng"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.DeleteOnClose) };
                    byte[] buffer = new byte[65536]; int count;
                    while ((count = ctx.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0) candidate.Stream.Write(buffer, 0, count);
                    if (ctx.Request.ContentLength64 >= 0 && candidate.Stream.Length != ctx.Request.ContentLength64) throw new ArgumentException("文件上传未完成，原演示源保留，请重新读取文件");
                    candidate.Stream.Flush(); monitor.SetReplaySource(candidate); candidate = null;
                    Json(ctx.Response, monitor.ReplayInfo(), 200);
                }
                finally { if (candidate != null) candidate.Dispose(); Interlocked.Exchange(ref replayUploading, 0); }
                return true;
            }
            Json(ctx.Response, new { error = "演示文件接口不存在" }, 404); return true;
        }
    }
}
