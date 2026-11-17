using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Server
    {
        private readonly object captureGate = new object();
        private readonly Dictionary<string, CaptureCheckJob> captureJobs = new Dictionary<string, CaptureCheckJob>();
        private bool captureClosing;
        private void DisposeCaptureChecks()
        {
            lock (captureGate)
            {
                captureClosing = true;
                foreach (CaptureCheckJob job in captureJobs.Values) { job.Cancel = true; job.DeleteTemporaryFiles(); }
                captureJobs.Clear();
            }
        }
        private static bool CaptureActive(CaptureCheckJob job) { return job.State == "uploading" || job.State == "queued" || job.State == "running"; }
        private CaptureCheckJob FindCapture(string id, Session session)
        {
            CaptureCheckJob job;
            if (!captureJobs.TryGetValue(id, out job) || job.Username != session.Username)
                throw new ArgumentException("检验结果不存在、已清除或不属于当前账户，请重新导入文件");
            return job;
        }
        private void CleanupCaptures()
        {
            string directory = Path.Combine(root, "data", "capture-checks");
            if (!Directory.Exists(directory)) return;
            var activePaths = new HashSet<string>(captureJobs.Values.SelectMany(j => new[] { j.Path, j.CsvPath, j.AbnormalCsvPath }), StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(directory))
            {
                string ext = Path.GetExtension(file), stem = Path.GetFileNameWithoutExtension(file); Guid unused;
                if (stem.EndsWith(".errors", StringComparison.Ordinal)) stem = stem.Substring(0, stem.Length - 7);
                if ((ext == ".pcapng" || ext == ".csv") && Guid.TryParseExact(stem, "N", out unused) && !activePaths.Contains(file) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-30))
                    try { File.Delete(file); } catch (IOException) { }
            }
        }
        private bool HandleCapture(HttpListenerContext ctx, Session session)
        {
            string path = ctx.Request.Url.AbsolutePath;
            if (!path.StartsWith("/api/captures/", StringComparison.Ordinal)) return false;
            if (path == "/api/captures/start" && ctx.Request.HttpMethod == "POST")
            {
                string filename = Path.GetFileName(Query(ctx.Request, "filename"));
                if (!String.Equals(Path.GetExtension(filename), ".pcapng", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择Wireshark导出的.pcapng文件");
                int port = 0; string portText = Query(ctx.Request, "port");
                if ((!String.IsNullOrEmpty(portText) && !Int32.TryParse(portText, out port)) || port < 0 || port > 65535) throw new ArgumentException("目的UDP端口应为0～65535，0表示全部");
                DecoderConfiguration config; Settings settings;
                lock (monitor.Gate) { config = monitor.DecoderSnapshot(); settings = JsonFile.Decode<Settings>(JsonFile.Encode(monitor.Config)); }
                var options = new CaptureOptions { Port = port, IncludeDisabled = Query(ctx.Request, "includeDisabled") != "false", UseWhitelist = Query(ctx.Request, "whitelist") == "true", PacketId = Query(ctx.Request, "packetId") };
                if (!String.IsNullOrEmpty(options.PacketId) && !config.Packets.Any(p => p.Id == options.PacketId)) throw new ArgumentException("所选SPP包已不存在，请刷新解码配置");
                CaptureCheckJob job;
                lock (captureGate)
                {
                    if (captureClosing) throw new InvalidOperationException("服务器正在关闭");
                    CleanupCaptures();
                    if (captureJobs.Count >= 8 || captureJobs.Values.Count(CaptureActive) >= 2) throw new InvalidOperationException("检验任务较多，请等待完成或清除旧检验结果后重试（同时最多2个任务）");
                    string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(root, "data", "capture-checks"); Directory.CreateDirectory(directory);
                    job = new CaptureCheckJob { Id = id, Username = session.Username, Filename = filename, State = "uploading", Configuration = config, Settings = settings, Options = options,
                        Path = Path.Combine(directory, id + ".pcapng"), CsvPath = Path.Combine(directory, id + ".csv"), AbnormalCsvPath = Path.Combine(directory, id + ".errors.csv") };
                    job.PrepareTemporaryFiles();
                    captureJobs[id] = job;
                }
                try
                {
                    using (var file = new FileStream(job.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] buffer = new byte[65536]; int n;
                        while ((n = ctx.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                            file.Write(buffer, 0, n);
                        job.Size = file.Length;
                    }
                    if (job.Size == 0) throw new ArgumentException("抓包文件为空");
                    lock (job.Gate) job.State = "queued";
                    new Thread(job.Run) { IsBackground = true, Name = "Capture check " + job.Id }.Start();
                    Json(ctx.Response, new { id = job.Id, revision = config.Revision }, 200);
                }
                catch
                { lock (captureGate) captureJobs.Remove(job.Id); job.DeleteTemporaryFiles(); throw; }
                return true;
            }
            if (path == "/api/captures/list" && ctx.Request.HttpMethod == "GET")
            {
                lock (captureGate)
                {
                    CleanupCaptures(); Json(ctx.Response, captureJobs.Values.Where(j => j.Username == session.Username).OrderByDescending(j => j.Created).Select(j => new { id = j.Id, filename = j.Filename, state = j.State }).ToArray(), 200);
                }
                return true;
            }
            CaptureCheckJob found;
            lock (captureGate) found = FindCapture(Query(ctx.Request, "id"), session);
            if (path == "/api/captures/status" && ctx.Request.HttpMethod == "GET")
            { Json(ctx.Response, found.Snapshot(Query(ctx.Request, "full") == "true"), 200); return true; }
            if (path == "/api/captures/detail" && ctx.Request.HttpMethod == "GET")
            { long frame; if (!Int64.TryParse(Query(ctx.Request, "frame"), out frame) || frame < 1) throw new ArgumentException("请选择抓包帧号"); Json(ctx.Response, found.Detail(frame), 200); return true; }
            if (path == "/api/captures/cancel" && ctx.Request.HttpMethod == "POST")
            { found.Cancel = true; Json(ctx.Response, new { ok = true }, 200); return true; }
            if (path == "/api/captures/delete" && ctx.Request.HttpMethod == "POST")
            {
                lock (captureGate)
                {
                    if (CaptureActive(found)) throw new InvalidOperationException("请先停止检验，再清除结果");
                    // These paths are generated internally under data/capture-checks, never from the uploaded name.
                    found.DeleteTemporaryFiles(); captureJobs.Remove(found.Id);
                }
                Json(ctx.Response, new { ok = true }, 200); return true;
            }
            if (path == "/api/captures/export" && ctx.Request.HttpMethod == "GET")
            {
                if (CaptureActive(found)) throw new InvalidOperationException("请等待检验完成或停止后再导出");
                string format = Query(ctx.Request, "format");
                if (format == "csv" || format == "errors")
                {
                    string csvPath = format == "errors" ? found.AbnormalCsvPath : found.CsvPath;
                    if (!File.Exists(csvPath)) throw new ArgumentException("当前没有可导出的CSV结果");
                    ctx.Response.ContentType = "text/csv; charset=utf-8";
                    ctx.Response.Headers["Content-Disposition"] = "attachment; filename=" + (format == "errors" ? "capture-abnormal-spp" : "capture-frames") + (found.State == "completed" ? "" : "-partial") + ".csv";
                    using (var file = CaptureCheckJob.OpenTemporaryRead(csvPath)) { ctx.Response.ContentLength64 = file.Length; file.CopyTo(ctx.Response.OutputStream); }
                }
                else { ctx.Response.Headers["Content-Disposition"] = "attachment; filename=capture-check.json"; Json(ctx.Response, found.Snapshot(true), 200); }
                return true;
            }
            Json(ctx.Response, new { error = "文件检验接口不存在" }, 404); return true;
        }
    }
}
