using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed class NetworkReplayOptions
    {
        public string Mode = "local", LocalAddress = "", SourcePolicy = "actual", DestinationFilter = "224.10.2.24", DestinationOverride = "";
        public int PortFilter = 30008, DestinationPort = 0, Speed = 1;
        public bool PreserveSourcePort;
    }
    internal sealed class NetworkReplayBinding { public string Address; public int Port; }
    internal sealed class NetworkReplayPlan
    {
        public string Token;
        public NetworkReplayOptions Options;
        public long Selected, Unsupported, Filtered, Other;
        public double DurationMs;
        public bool UsesMulticast;
        public readonly Dictionary<string, NetworkReplayBinding> Bindings = new Dictionary<string, NetworkReplayBinding>();
        public readonly Dictionary<string, long> Flows = new Dictionary<string, long>();
    }

    // This class never calls Monitor.Process or the decoder. Every payload goes through a real UDP socket.
    public sealed class NetworkReplay : IDisposable
    {
        private readonly object gate = new object();
        private readonly Action<string, string> note;
        private readonly ManualResetEvent cancel = new ManualResetEvent(false);
        private ReplaySource source;
        private NetworkReplayPlan plan;
        private Thread worker;
        private string state = "empty", error = "";
        private long epoch, frames, readBytes, sent, sentBytes;
        private int speed = 1;
        private Stopwatch clock;
        private double anchorReal, anchorCapture, captureMs;
        private readonly List<UdpClient> activeSockets = new List<UdpClient>();
        private readonly List<string> actualBindings = new List<string>();
        public NetworkReplay(Action<string, string> logger) { note = logger; }
        private bool Busy { get { return state == "analyzing" || state == "sending" || state == "stopping"; } }
        public bool IsSending { get { lock (gate) return state == "sending"; } }
        private static bool Multicast(IPAddress address) { byte first = address.GetAddressBytes()[0]; return first >= 224 && first <= 239; }
        public static object[] Interfaces()
        {
            var rows = new List<object>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.Equals(IPAddress.Any)))
                    rows.Add(new { ip = address.Address.ToString(), name = adapter.Name, loopback = IPAddress.IsLoopback(address.Address), multicast = adapter.SupportsMulticast });
            return rows.ToArray();
        }
        private static List<string> InterfaceAddresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.Equals(IPAddress.Any))
                .Select(a => a.Address.ToString()).Distinct().OrderBy(a => IPAddress.IsLoopback(IPAddress.Parse(a))).ThenBy(a => a).ToList();
        }
        private static IPAddress IPv4(string text, string field)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(text, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException(field + "应填写IPv4地址");
            return ip;
        }
        private static NetworkReplayOptions Validate(NetworkReplayOptions options, List<string> addresses)
        {
            if (options == null) throw new ArgumentException("缺少网络回放配置");
            options = JsonFile.Decode<NetworkReplayOptions>(JsonFile.Encode(options));
            if (options.Mode != "local" && options.Mode != "lan") throw new ArgumentException("请选择本机闭环或局域网发送");
            if (options.SourcePolicy != "actual" && options.SourcePolicy != "original") throw new ArgumentException("来源IP策略无效");
            options.LocalAddress = (options.LocalAddress ?? "").Trim();
            if (options.LocalAddress == "") options.LocalAddress = addresses.FirstOrDefault(a => !IPAddress.IsLoopback(IPAddress.Parse(a))) ?? addresses.FirstOrDefault() ?? "";
            options.LocalAddress = IPv4(options.LocalAddress, "发送网卡IP").ToString();
            if (!addresses.Contains(options.LocalAddress)) throw new ArgumentException("发送网卡IP不是本机已启用的IPv4地址");
            if (options.Mode == "lan" && IPAddress.IsLoopback(IPAddress.Parse(options.LocalAddress))) throw new ArgumentException("局域网发送不能使用回环网卡，请选择实际网卡IP");
            options.DestinationFilter = (options.DestinationFilter ?? "").Trim();
            options.DestinationOverride = (options.DestinationOverride ?? "").Trim();
            if (options.DestinationFilter != "") options.DestinationFilter = IPv4(options.DestinationFilter, "抓包目的IP筛选").ToString();
            if (options.DestinationOverride != "") options.DestinationOverride = IPv4(options.DestinationOverride, "改发目的IP").ToString();
            if (options.PortFilter < 1 || options.PortFilter > 65535 || options.DestinationPort < 0 || options.DestinationPort > 65535 || options.Speed < 1 || options.Speed > 5)
                throw new ArgumentException("抓包目的端口应为1～65535，改发端口为0或1～65535，倍速为1～5的整数");
            return options;
        }
        private static string BindingKey(string address, int port) { return address + ":" + port; }
        private static bool Selected(CaptureFrame frame, NetworkReplayOptions options)
        { return frame.DestinationPort == options.PortFilter && (options.DestinationFilter == "" || frame.DestinationIP == options.DestinationFilter); }
        private static NetworkReplayBinding Binding(CaptureFrame frame, NetworkReplayOptions options)
        { return new NetworkReplayBinding { Address = options.SourcePolicy == "original" ? frame.SourceIP : options.LocalAddress, Port = options.PreserveSourcePort ? frame.SourcePort : 0 }; }
        private static IPEndPoint Destination(CaptureFrame frame, NetworkReplayOptions options, List<string> addresses)
        {
            IPAddress destination = IPv4(options.DestinationOverride == "" ? frame.DestinationIP : options.DestinationOverride, "UDP目的IP");
            if (options.Mode == "local" && !Multicast(destination) && !IPAddress.IsLoopback(destination) && !addresses.Contains(destination.ToString()))
                throw new ArgumentException("本机闭环不允许发送到其他电脑：" + destination + "。请选择本机地址/组播地址，或明确切换局域网发送");
            return new IPEndPoint(destination, options.DestinationPort == 0 ? frame.DestinationPort : options.DestinationPort);
        }
        public void SetSource(ReplaySource candidate)
        {
            lock (gate)
            {
                if (Busy) throw new InvalidOperationException("请先停止网络回放或预检，再更换文件");
                if (candidate != null)
                {
                    candidate.Stream.Position = 0; byte[] magic = new byte[4];
                    if (candidate.Stream.Read(magic, 0, 4) != 4 || magic[0] != 10 || magic[1] != 13 || magic[2] != 13 || magic[3] != 10) throw new ArgumentException("文件不是有效pcapng");
                    candidate.Stream.Position = 0;
                }
                if (source != null) source.Dispose(); source = candidate; plan = null; error = "";
                state = source == null ? "empty" : "ready"; frames = readBytes = sent = sentBytes = 0; captureMs = anchorCapture = anchorReal = 0; clock = null; actualBindings.Clear();
            }
        }
        private double PositionMs()
        { return anchorCapture + ((clock == null ? 0 : clock.Elapsed.TotalMilliseconds) - anchorReal) * speed; }
        public void SetSpeed(int value)
        {
            if (value < 1 || value > 5) throw new ArgumentException("网络回放倍速应为1～5的整数");
            lock (gate) { double now = clock == null ? 0 : clock.Elapsed.TotalMilliseconds; anchorCapture += (now - anchorReal) * speed; anchorReal = now; speed = value; }
        }
        public object Info()
        {
            lock (gate) return new { ready = source != null, filename = source == null ? "" : source.Filename, size = source == null ? 0 : source.Size,
                state = state, error = error, token = plan == null ? "" : plan.Token, options = plan == null ? null : plan.Options,
                selected = plan == null ? 0 : plan.Selected, unsupported = plan == null ? 0 : plan.Unsupported, filtered = plan == null ? 0 : plan.Filtered, other = plan == null ? 0 : plan.Other,
                durationMs = plan == null ? 0 : plan.DurationMs, flows = plan == null ? new object[0] : plan.Flows.Select(p => (object)new { route = p.Key, frames = p.Value }).ToArray(),
                framesRead = frames, sent = sent, sentBytes = sentBytes, speed = speed, captureElapsedMs = captureMs,
                progress = source == null || source.Size == 0 ? 0 : (int)Math.Min(100m, (decimal)readBytes * 100m / source.Size),
                elapsedMs = clock == null ? 0 : clock.Elapsed.TotalMilliseconds, actualBindings = actualBindings.ToArray(),
                canStart = source != null && plan != null && !Busy };
        }
        public void Analyze(NetworkReplayOptions options)
        {
            List<string> addresses = InterfaceAddresses(); options = Validate(options, addresses);
            lock (gate)
            {
                if (Busy) throw new InvalidOperationException("请先停止当前网络回放或预检");
                if (source == null) throw new ArgumentException("请先读取pcapng网络回放文件");
                plan = null; state = "analyzing"; error = ""; frames = readBytes = sent = sentBytes = 0; actualBindings.Clear();
                captureMs = anchorCapture = anchorReal = 0; clock = null;
                speed = options.Speed; cancel.Reset(); long current = ++epoch;
                worker = new Thread(() => AnalyzeWorker(options, addresses, current)) { IsBackground = true, Name = "Network replay preflight" }; worker.Start();
            }
        }
        private void AnalyzeWorker(NetworkReplayOptions options, List<string> addresses, long current)
        {
            try
            {
                var candidate = new NetworkReplayPlan { Token = Guid.NewGuid().ToString("N"), Options = options }; long first = -1, previous = -1;
                CaptureReader.ReadPcapng(source.Stream, (frame, bytes) =>
                {
                    lock (gate) { if (epoch != current) return; frames++; readBytes = bytes; }
                    if (frame.Unsupported || !String.IsNullOrEmpty(frame.Issue)) { candidate.Unsupported++; return; }
                    if (!frame.Udp) { candidate.Other++; return; }
                    if (frame.Payload == null) { candidate.Unsupported++; return; }
                    if (!Selected(frame, options)) { candidate.Filtered++; return; }
                    if (frame.TimestampTicks < 0 || (previous >= 0 && frame.TimestampTicks < previous)) throw new ArgumentException("抓包第" + frame.Number + "帧没有有效时间戳或时间倒退，无法按原时间间隔回放");
                    if (first < 0) first = frame.TimestampTicks; previous = frame.TimestampTicks;
                    if (frame.Payload.Length > 65507) throw new ArgumentException("UDP载荷超过IPv4可发送长度");
                    if (options.PreserveSourcePort && frame.SourcePort == 0) throw new ArgumentException("抓包来源端口为0，普通UDP套接字不能原样发送0源端口；请选择临时源端口");
                    NetworkReplayBinding binding = Binding(frame, options);
                    if (!addresses.Contains(binding.Address)) throw new ArgumentException("抓包来源IP " + binding.Address + " 不是本机地址，无法冒用。请选择“使用发送网卡实际IP”，或先在隔离测试机配置该本机IP");
                    if (options.Mode == "lan" && IPAddress.IsLoopback(IPAddress.Parse(binding.Address))) throw new ArgumentException("局域网回放不能使用抓包中的回环来源IP");
                    IPEndPoint destination = Destination(frame, options, addresses);
                    candidate.UsesMulticast |= Multicast(destination.Address);
                    candidate.Bindings[BindingKey(binding.Address, binding.Port)] = binding;
                    if (candidate.Bindings.Count > 512) throw new ArgumentException("模拟源套接字超过512个，请使用临时源端口或缩小抓包筛选范围");
                    candidate.Selected++; candidate.DurationMs = (frame.TimestampTicks - first) / 10000.0;
                    string route = frame.SourceIP + ":" + frame.SourcePort + " → " + frame.DestinationIP + ":" + frame.DestinationPort + "；实际发送 " + binding.Address + ":" + (binding.Port == 0 ? "临时端口" : binding.Port.ToString()) + " → " + destination;
                    if (candidate.Flows.ContainsKey(route)) candidate.Flows[route]++; else if (candidate.Flows.Count < 64) candidate.Flows[route] = 1;
                }, () => cancel.WaitOne(0));
                if (cancel.WaitOne(0)) return;
                if (candidate.Selected == 0) throw new ArgumentException("pcapng内没有符合所选目的IP/端口的完整IPv4 UDP报文");
                lock (gate) { if (epoch != current) return; plan = candidate; state = "prepared"; readBytes = source.Size; }
                note("info", "网络回放预检完成：" + candidate.Selected + "个UDP报文；尚未发送，需手动开始。");
            }
            catch (Exception ex) { Failed(current, "网络回放预检失败：", ex); }
        }
        public void Start(string token)
        {
            lock (gate)
            {
                if (Busy) throw new InvalidOperationException("请先停止当前网络回放或预检");
                if (plan == null || String.IsNullOrEmpty(token) || token != plan.Token) throw new InvalidOperationException("收发计划不存在或已改变，请重新预检后开始");
                state = "sending"; error = ""; sent = sentBytes = frames = readBytes = 0; captureMs = anchorCapture = anchorReal = 0;
                clock = new Stopwatch(); actualBindings.Clear(); cancel.Reset(); long current = ++epoch; NetworkReplayPlan prepared = plan;
                worker = new Thread(() => SendWorker(prepared, current)) { IsBackground = true, Name = "Real UDP capture sender" }; worker.Start();
            }
        }
        private void SendWorker(NetworkReplayPlan prepared, long current)
        {
            var sockets = new Dictionary<string, UdpClient>();
            try
            {
                NetworkReplayOptions options = prepared.Options; List<string> addresses = InterfaceAddresses();
                foreach (var item in prepared.Bindings)
                {
                    if (cancel.WaitOne(0)) return;
                    var client = new UdpClient(AddressFamily.InterNetwork);
                    try
                    {
                        client.ExclusiveAddressUse = true; client.Client.SendTimeout = 500; client.EnableBroadcast = true;
                        client.Client.Bind(new IPEndPoint(IPAddress.Parse(item.Value.Address), item.Value.Port));
                        if (prepared.UsesMulticast)
                        {
                            client.MulticastLoopback = true; client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(item.Value.Address).GetAddressBytes());
                            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, options.Mode == "local" ? 0 : 1);
                        }
                        sockets[item.Key] = client;
                        lock (gate) { if (epoch != current) return; activeSockets.Add(client); actualBindings.Add(client.Client.LocalEndPoint.ToString()); }
                    }
                    catch { client.Close(); throw; }
                }
                long first = -1;
                CaptureReader.ReadPcapng(source.Stream, (frame, bytes) =>
                {
                    if (cancel.WaitOne(0)) return;
                    lock (gate) { if (epoch != current) return; frames++; readBytes = bytes; }
                    if (!frame.Udp || frame.Unsupported || !String.IsNullOrEmpty(frame.Issue) || frame.Payload == null || !Selected(frame, options)) return;
                    if (first < 0) { first = frame.TimestampTicks; clock.Start(); }
                    double due = (frame.TimestampTicks - first) / 10000.0;
                    while (true)
                    {
                        double remaining; lock (gate) remaining = (due - PositionMs()) / speed;
                        if (remaining <= 0) break;
                        if (cancel.WaitOne((int)Math.Min(50, Math.Max(1, Math.Ceiling(remaining))))) return;
                    }
                    NetworkReplayBinding binding = Binding(frame, options); IPEndPoint destination = Destination(frame, options, addresses);
                    int written = sockets[BindingKey(binding.Address, binding.Port)].Send(frame.Payload, frame.Payload.Length, destination);
                    if (written != frame.Payload.Length) throw new IOException("UDP发送长度不一致");
                    lock (gate) { if (epoch != current) return; sent++; sentBytes += written; captureMs = due; }
                }, () => cancel.WaitOne(0));
                if (cancel.WaitOne(0)) return;
                lock (gate)
                {
                    if (epoch != current) return;
                    if (sent != prepared.Selected) throw new ArgumentException("发送数量与预检不一致，请重新读取并预检");
                    clock.Stop(); state = "completed"; readBytes = source.Size;
                }
                note("info", "网络回放发送完成。发送成功只表示已交给操作系统，是否收到请查看接收端统计。");
            }
            catch (Exception ex) { if (!cancel.WaitOne(0)) Failed(current, "网络回放发送失败：", ex); }
            finally
            {
                foreach (UdpClient client in sockets.Values) client.Close();
                lock (gate) { if (epoch == current) activeSockets.Clear(); }
            }
        }
        private void Failed(long current, string prefix, Exception ex)
        {
            string message = prefix + (ex is SocketException ? ((SocketException)ex).SocketErrorCode + "；检查发送网卡、源端口占用及目标地址" : ex.Message);
            lock (gate) { if (epoch != current) return; error = message; state = "failed"; if (clock != null) clock.Stop(); }
            note("error", message);
        }
        public void Stop()
        {
            Thread old;
            lock (gate)
            {
                if (!Busy) return;
                cancel.Set(); epoch++; old = worker; state = "stopping";
                foreach (UdpClient client in activeSockets) client.Close(); activeSockets.Clear();
                if (clock != null) clock.Stop();
            }
            if (old != null && !old.Join(3000)) throw new InvalidOperationException("网络回放正在停止，请稍后重试");
            lock (gate) state = "stopped";
        }
        public void Dispose()
        {
            Stop(); lock (gate) { if (source != null) source.Dispose(); source = null; cancel.Dispose(); }
        }
    }
}
