using System;
using System.IO;
using System.Net;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private int networkReplayUploading;
        private bool HandleNetworkReplay(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath, method = ctx.Request.HttpMethod;
            if (!path.StartsWith("/api/network-replay/", StringComparison.Ordinal)) return false;
            if (path == "/api/network-replay/state" && method == "GET") Json(ctx.Response, monitor.NetworkSender.Info(), 200);
            else if (path == "/api/network-replay/interfaces" && method == "GET") Json(ctx.Response, NetworkReplay.Interfaces(), 200);
            else if (path == "/api/network-replay/analyze" && method == "POST")
            { monitor.NetworkSender.Analyze(JsonFile.Decode<NetworkReplayOptions>(JsonFile.Encode(Body(ctx.Request)))); Json(ctx.Response, monitor.NetworkSender.Info(), 200); }
            else if (path == "/api/network-replay/start" && method == "POST")
            {
                string token = Text(Body(ctx.Request), "token");
                lock (monitor.Gate)
                {
                    if (monitor.Demo) throw new InvalidOperationException("请先停止内部解码预览，网络模拟应通过真实监听验证");
                    monitor.NetworkSender.Start(token);
                }
                monitor.Note("info", "手动开始真实UDP网络回放；操作员：" + auth.User(session).DisplayName);
                Json(ctx.Response, monitor.NetworkSender.Info(), 200);
            }
            else if (path == "/api/network-replay/stop" && method == "POST")
            { monitor.NetworkSender.Stop(); Json(ctx.Response, monitor.NetworkSender.Info(), 200); }
            else if (path == "/api/network-replay/clear" && method == "POST")
            { monitor.NetworkSender.SetSource(null); Json(ctx.Response, monitor.NetworkSender.Info(), 200); }
            else if (path == "/api/network-replay/speed" && method == "POST")
            {
                int speed; if (!Int32.TryParse(Text(Body(ctx.Request), "speed"), out speed)) throw new ArgumentException("倍速应为1～5的整数");
                monitor.NetworkSender.SetSpeed(speed); Json(ctx.Response, monitor.NetworkSender.Info(), 200);
            }
            else if (path == "/api/network-replay/use-capture" && method == "POST")
            {
                ReplaySource source;
                lock (captureGate)
                {
                    CaptureCheckJob job = FindCapture(Query(ctx.Request, "id"), session);
                    if (job.State != "completed") throw new InvalidOperationException("请先等待文件检验完成，再复用抓包文件");
                    source = new ReplaySource { Filename = job.Filename, Stream = CaptureCheckJob.OpenTemporaryRead(job.Path) };
                }
                try { monitor.NetworkSender.SetSource(source); } catch { source.Dispose(); throw; }
                Json(ctx.Response, monitor.NetworkSender.Info(), 200);
            }
            else if (path == "/api/network-replay/upload" && method == "POST")
            {
                string filename = Path.GetFileName(Query(ctx.Request, "filename"));
                if (!String.Equals(Path.GetExtension(filename), ".pcapng", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择pcapng文件");
                if (Interlocked.CompareExchange(ref networkReplayUploading, 1, 0) != 0) throw new InvalidOperationException("已有网络回放文件正在上传");
                ReplaySource candidate = null;
                try
                {
                    string directory = Path.Combine(root, "data", "network-replay"); Directory.CreateDirectory(directory);
                    candidate = new ReplaySource { Filename = filename, Stream = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pcapng"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.DeleteOnClose) };
                    byte[] buffer = new byte[65536]; int count;
                    while ((count = ctx.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0) candidate.Stream.Write(buffer, 0, count);
                    if (ctx.Request.ContentLength64 >= 0 && candidate.Size != ctx.Request.ContentLength64) throw new ArgumentException("抓包上传未完成，请重新读取");
                    candidate.Stream.Flush(); monitor.NetworkSender.SetSource(candidate); candidate = null;
                    Json(ctx.Response, monitor.NetworkSender.Info(), 200);
                }
                finally { if (candidate != null) candidate.Dispose(); Interlocked.Exchange(ref networkReplayUploading, 0); }
            }
            else Json(ctx.Response, new { error = "网络回放接口不存在" }, 404);
            return true;
        }
    }
}
