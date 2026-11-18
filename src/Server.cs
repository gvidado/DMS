using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Server : IDisposable
    {
        private readonly Monitor liveMonitor;
        private Monitor monitor {get{return configurationContext!=null&&configurationContext.Owner==this?configurationContext.Monitor:liveMonitor;}}
        private readonly Auth auth;
        private readonly string root;
        private readonly HttpListener http = new HttpListener();
        private readonly object commandGate = new object();
        private volatile bool running;
        public Server(string directory, bool local, int port)
        {
            root = directory; liveMonitor = new Monitor(root); auth = new Auth(root); liveTransmitter = new CommandTransmitter(root, liveMonitor.Outgoing.Commands, (level,message)=>liveMonitor.Note(level,message));
            int webPort = port > 0 ? port : monitor.Config.WebPort;
            if (local)
            {
                http.Prefixes.Add("http://127.0.0.1:" + webPort + "/");
                http.Prefixes.Add("http://localhost:" + webPort + "/");
            }
            else http.Prefixes.Add("http://" + monitor.Config.WebHost + ":" + webPort + "/");
        }
        public void Start()
        {
            ThreadPool.SetMinThreads(32, 32); http.Start(); running = true;
            InitializeSequenceService(); InitializeConfigurationSchedule();new Thread(Accept) { IsBackground = true, Name = "HTTP accept" }.Start();
        }
        private void Accept()
        {
            while (running)
            {
                try { HttpListenerContext context = http.GetContext(); ThreadPool.QueueUserWorkItem(delegate(object unused) { Handle(context); }); }
                catch (HttpListenerException) { if (!running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        private static void Json(HttpListenerResponse response, object value, int status)
        {
            if(configurationRequest!=null)value=configurationRequest.BeforeResponse(response,value,status);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonFile.Encode(value));
            response.StatusCode = status; response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        private static Dictionary<string, object> Body(HttpListenerRequest request, int limit = 65536)
        {
            if (request.ContentLength64 > limit) throw new ArgumentException("请求过大");
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                var text = new StringBuilder();
                var buffer = new char[Math.Min(4096, limit + 1)];
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (text.Length + count > limit) throw new ArgumentException("请求过大");
                    text.Append(buffer, 0, count);
                }
                return JsonFile.Decode<Dictionary<string, object>>(text.ToString()) ?? new Dictionary<string, object>();
            }
        }
        private static string Text(Dictionary<string, object> body, string key)
        { object v; return body.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }

        private void Handle(HttpListenerContext ctx)
        {
            bool stream = false;ConfigurationRequestScope configurationScope=null;
            try
            {
                HttpListenerRequest req = ctx.Request; HttpListenerResponse res = ctx.Response;
                res.Headers["X-Content-Type-Options"] = "nosniff";
                res.Headers["X-Frame-Options"] = "DENY";
                res.Headers["Referrer-Policy"] = "same-origin";
                res.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                res.Headers["Cache-Control"] = "no-store";
                string path = req.Url.AbsolutePath;
                if (req.HttpMethod != "GET" && req.HttpMethod != "POST") { Json(res, new { error = "不支持的请求方法" }, 405); return; }
                if (req.HttpMethod == "GET" && new[] { "/", "/index.html", "/app.js", "/layout-clipboard.js", "/layout-arrange.js", "/decoder.js", "/configuration.js", "/telemetry-import.js", "/telemetry-import.css", "/capture.js", "/runtime.js", "/raw-spp.js", "/alarms.js", "/network-replay.js", "/send-monitor.js", "/command-xml.js", "/command-send.js", "/command-panels.js", "/command-panels.css", "/sequence.js", "/sequence.css", "/continuous.js", "/namespace-migration.js", "/unified-abnormals.js", "/judgment-archives.js", "/workspace.js", "/display-size.js", "/parameter-display.js", "/parameter-display.css", "/realtime.js", "/realtime-worker.js", "/style.css", "/forms.css", "/logo.svg" }.Contains(path))
                {
                    string name = path == "/" ? "index.html" : path.Substring(1);
                    ServeWebAsset(ctx, name);
                    return;
                }
                string token = req.Cookies["tm_session"] == null ? null : req.Cookies["tm_session"].Value;
                Session session = auth.Find(token);
                if (path == "/api/auth/me" && req.HttpMethod == "GET")
                { Json(res, new { setupRequired = auth.SetupRequired, local = req.IsLocal, user = session == null ? null : auth.PublicUser(session) }, 200); return; }
                if (req.HttpMethod == "POST")
                {
                    string origin = req.Headers["Origin"];
                    if (!String.IsNullOrEmpty(origin) && !String.Equals(origin, req.Url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                    { Json(res, new { error = "拒绝跨站请求" }, 403); return; }
                }
                if (path == "/api/auth/setup" && req.HttpMethod == "POST")
                {
                    if (!req.IsLocal) { Json(res, new { error = "首次初始化请在服务器本机浏览器完成" }, 403); return; }
                    var body = Body(req);
                    lock (commandGate) auth.Add(Text(body, "username"), Text(body, "name"), Text(body, "password"), true, true, monitor.Catalog.Where(p => p.PacketEnabled).Select(p => p.Id).ToList());
                    monitor.Note("info", "本机已完成管理员初始化"); Json(res, new { ok = true }, 200); return;
                }
                if (path == "/api/auth/login" && req.HttpMethod == "POST")
                {
                    var body = Body(req); session = auth.Login(Text(body, "username"), Text(body, "password"));
                    res.Headers.Add("Set-Cookie", "tm_session=" + session.Token + "; Path=/; HttpOnly; SameSite=Strict" + (req.IsSecureConnection ? "; Secure" : ""));
                    Json(res, auth.PublicUser(session), 200); return;
                }
                if (session == null) { Json(res, new { error = "请先登录" }, 401); return; }
                if (req.HttpMethod == "POST" && !auth.CheckCsrf(session, req.Headers["X-CSRF-Token"]))
                { Json(res, new { error = "会话校验失败，请刷新页面" }, 403); return; }
                if(HandleConfigurationSchedule(ctx,session))return;
                configurationScope=EnterConfigurationRequest(ctx);
                if (HandleDshPlugin(ctx, session)) return;
                if (path == "/api/auth/logout" && req.HttpMethod == "POST")
                { auth.Logout(session); res.Headers.Add("Set-Cookie", "tm_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0"); Json(res, new { ok = true }, 200); return; }
                if (path == "/api/auth/password" && req.HttpMethod == "POST")
                { var body = Body(req); auth.ChangePassword(session, Text(body, "oldPassword"), Text(body, "password")); Json(res, new { ok = true }, 200); return; }
                if (path == "/api/catalog" && req.HttpMethod == "GET")
                { ServeCatalog(ctx); return; }
                if (path == "/api/selection" && req.HttpMethod == "GET")
                { Json(res, auth.ResolveSelection(session, monitor.Catalog), 200); return; }
                if (HandleParameterDisplays(ctx, session)) return;
                if (HandleCommandPanels(ctx, session)) return;
                if (HandleRawSpp(ctx, session)) return;
                if (HandleDecoder(ctx, session)) return;
                if (HandleContinuousRules(ctx, session)) return;
                if (HandleSequences(ctx, session)) return;
                if (HandleReplay(ctx, session)) return;
                if (HandleNetworkReplay(ctx, session)) return;
                if (HandleSendMonitor(ctx, session)) return;
                if (HandleCommandSending(ctx, session)) return;
                if (HandleAlarms(ctx, session)) return;
                if (HandleUnifiedAbnormals(ctx, session)) return;
                if (HandleJudgmentArchives(ctx, session)) return;
                if (HandleCapture(ctx, session)) return;
                if (path == "/api/config" && req.HttpMethod == "GET")
                { lock (monitor.Gate) Json(res, monitor.Config, 200); return; }
                if (path == "/api/state" && req.HttpMethod == "GET")
                { Json(res, monitor.Snapshot(auth.ResolveSelection(session, monitor.Catalog)), 200); return; }
                if (path == "/api/telemetry/history" && req.HttpMethod == "GET")
                { Json(res, monitor.ReadEngineeringHistory(Query(req, "reference"), Query(req, "parameterId"), Query(req, "sourceIP")), 200); return; }
                if (path == "/api/packet" && req.HttpMethod == "GET")
                { Json(res, monitor.PacketPreview(), 200); return; }
                if (path == "/api/selection" && req.HttpMethod == "POST")
                {
                    var body = Body(req, 8 * 1024 * 1024); object input;
                    if (!body.TryGetValue("ids", out input)) throw new ArgumentException("缺少参数列表");
                    var items = input as System.Collections.IEnumerable;
                    if (items == null || input is string) throw new ArgumentException("参数列表格式错误");
                    var valid = new HashSet<string>(monitor.Catalog.Select(p => p.Id)); var ids = new List<string>();
                    foreach (object item in items) { string id = Convert.ToString(item); if (!valid.Contains(id)) throw new ArgumentException("未知参数：" + id); ids.Add(id); }
                    auth.Select(session, ids); Json(res, new { ok = true }, 200); return;
                }
                if (path == "/api/events" && req.HttpMethod == "GET")
                {
                    if (!auth.OpenStream(session)) { Json(res, new { error = "实时连接数量过多" }, 429); return; }
                    stream = true;
                    try
                    {
                        res.ContentType = "text/event-stream; charset=utf-8"; res.SendChunked = true; res.KeepAlive = true;
                        while (running && auth.Find(session.Token) != null)
                        {
                            byte[] frame = Encoding.UTF8.GetBytes("data: " + JsonFile.Encode(monitor.Snapshot(auth.ResolveSelection(session, monitor.Catalog))) + "\n\n");
                            res.OutputStream.Write(frame, 0, frame.Length); res.OutputStream.Flush(); Thread.Sleep(monitor.Demo ? 50 : 250);
                        }
                    }
                    finally { auth.CloseStream(session); }
                    return;
                }
                if (path == "/api/config" && req.HttpMethod == "POST")
                {
                    Settings cfg = JsonFile.Decode<Settings>(JsonFile.Encode(Body(req)));
                    lock (commandGate) monitor.SaveSettings(cfg);
                    monitor.Note("info", "配置修改人：" + auth.User(session).DisplayName); Json(res, new { ok = true }, 200); return;
                }
                if (path == "/api/control" && req.HttpMethod == "POST")
                {
                    string action = Text(Body(req), "action");
                    lock(configurationEditGate)lock (commandGate)
                    {
                        try
                        {
                            if(action=="start"||action=="demo")if(!ApplyIdleConfiguration())throw new InvalidOperationException(configurationError);
                            if (action == "start") monitor.Start();
                            else if (action == "stop") {monitor.Stop();ApplyIdleConfiguration();}
                            else if (action == "demo") monitor.StartDemo();
                            else if (action == "ack") monitor.Acknowledge(session.Username, auth.User(session).DisplayName);
                            else throw new ArgumentException("未知操作");
                        }
                        catch (Exception ex)
                        {
                            if (action == "start" || action == "demo") monitor.Note("error", (action == "start" ? "启动UDP监听失败：" : "启动文件演示失败：") + ex.Message);
                            throw;
                        }
                    }
                    monitor.Note("info", "操作：" + action + "；操作员：" + auth.User(session).DisplayName);
                    Json(res, new { ok = true }, 200); return;
                }
                if (path == "/api/users" || path == "/api/users/reset")
                {
                    if (!auth.User(session).Admin) { Json(res, new { error = "仅管理员可管理账号" }, 403); return; }
                    if (req.HttpMethod == "GET" && path == "/api/users") { Json(res, auth.ListUsers(), 200); return; }
                    if (req.HttpMethod == "POST")
                    {
                        var body = Body(req);
                        if (path == "/api/users/reset") auth.ResetPassword(Text(body, "username"), Text(body, "password"));
                        else auth.Add(Text(body, "username"), Text(body, "name"), Text(body, "password"), false, false, monitor.Catalog.Where(p => p.PacketEnabled).Select(p => p.Id).ToList());
                        monitor.Note("info", "管理员已更新账号：" + Text(body, "username")); Json(res, new { ok = true }, 200); return;
                    }
                }
                Json(res, new { error = "接口不存在" }, 404);
            }
            catch (ArgumentException ex) { if (!stream) SafeError(ctx, ex.Message, 400); }
            catch (System.Xml.XmlException) { if (!stream) SafeError(ctx, "Excel内的XML格式损坏或不受支持", 400); }
            catch (InvalidDataException) { if (!stream) SafeError(ctx, "文件不是有效xlsx，或压缩内容已损坏", 400); }
            catch (InvalidOperationException ex) { if (!stream) SafeError(ctx, ex.Message, 409); }
            catch (SocketException ex) { if (!stream) SafeError(ctx, "端口绑定失败：" + ex.SocketErrorCode + "，请检查端口占用与监听地址", 409); }
            catch (HttpListenerException) { }
            catch (IOException) { if (!stream) SafeError(ctx, "文件读取或写入失败，请检查目录权限及空间", 500); }
            catch (Exception ex)
            {
                monitor.Note("error", "Web请求异常：" + ex.GetType().Name + " " + ex.Message);
                if (!stream) SafeError(ctx, "服务器处理失败，请查看运行日志", 500);
            }
            finally { if(configurationScope!=null)configurationScope.Dispose();try { ctx.Response.Close(); } catch { } }
        }
        private static void SafeError(HttpListenerContext ctx, string text, int code)
        { try { Json(ctx.Response, new { error = text }, code); } catch { } }
        public void Dispose() { running = false; if(sequenceTimer!=null)sequenceTimer.Dispose(); if(configurationTimer!=null)configurationTimer.Dispose();http.Close(); DisposeCaptureChecks(); lock(configurationEditGate)lock(commandGate){DisposeConfigurationEditor();liveMonitor.Dispose();} }
    }
}
