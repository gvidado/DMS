using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed class ReplaySource : IDisposable
    {
        public string Filename;
        public FileStream Stream;
        public long Size { get { return Stream.Length; } }
        public void Dispose() { if (Stream != null) { Stream.Dispose(); Stream = null; } }
    }
    public sealed partial class Monitor
    {
        private ReplaySource replaySource;
        private Thread replayThread;
        private readonly ManualResetEvent replayCancel = new ManualResetEvent(false);
        private string replayState = "empty", replayError = "";
        private long replayFrames, replayDelivered, replaySkipped, replayBytes;
        private double replayCaptureMs, replayLagMs;
        private Stopwatch replayClock;
        private int replaySpeed = 1;
        private double replayAnchorRealMs, replayAnchorCaptureMs;
        private double ReplayPositionMs()
        { return replayAnchorCaptureMs + ((replayClock == null ? 0 : replayClock.Elapsed.TotalMilliseconds) - replayAnchorRealMs) * replaySpeed; }
        public void SetReplaySpeed(int speed)
        {
            if (speed < 1 || speed > 5) throw new ArgumentException("演示速度应为1～5倍的整数");
            lock (Gate)
            {
                double now = replayClock == null ? 0 : replayClock.Elapsed.TotalMilliseconds;
                replayAnchorCaptureMs += (now - replayAnchorRealMs) * replaySpeed;
                replayAnchorRealMs = now;
                replaySpeed = speed;
            }
        }
        public object ReplayInfo()
        {
            lock (Gate) return new { ready = replaySource != null, filename = replaySource == null ? "" : replaySource.Filename, size = replaySource == null ? 0 : replaySource.Size,
                state = replayState, error = replayError, frames = replayFrames, delivered = replayDelivered, skipped = replaySkipped, bytes = replayBytes,
                progress = replaySource == null || replaySource.Size == 0 ? 0 : (int)Math.Min(100m, (decimal)replayBytes * 100m / replaySource.Size),
                captureElapsedMs = replayCaptureMs, elapsedMs = replayClock == null ? 0 : replayClock.Elapsed.TotalMilliseconds, lagMs = replayLagMs, speed = replaySpeed, positionMs = ReplayPositionMs() };
        }
        public void SetReplaySource(ReplaySource source)
        {
            lock (Gate)
            {
                if (Listening || Demo) throw new InvalidOperationException("请先停止监听或演示，再更换演示文件");
                if (source != null)
                {
                    source.Stream.Position = 0; byte[] magic = new byte[4];
                    if (source.Stream.Read(magic, 0, 4) != 4 || magic[0] != 10 || magic[1] != 13 || magic[2] != 13 || magic[3] != 10) throw new ArgumentException("文件不是有效的pcapng节头");
                    source.Stream.Position = 0;
                }
                if (replaySource != null) replaySource.Dispose(); replaySource = source;
                replayState = source == null ? "empty" : "ready"; replayError = ""; replayFrames = replayDelivered = replaySkipped = replayBytes = 0;
                replayCaptureMs = replayLagMs = 0; replayClock = null;
                replayAnchorRealMs = replayAnchorCaptureMs = 0;
            }
        }
        public void StartDemo()
        {
            lock (Gate)
            {
                if (Listening || Demo || Outgoing.IsActive) throw new InvalidOperationException("请先停止当前监听或演示");
                if (NetworkSender.IsSending) throw new InvalidOperationException("请先停止网络回放发送；内部预览不能作为网络接收验证");
                if (replaySource == null) throw new ArgumentException("请先选择pcapng演示文件，或在文件检验中设为演示数据源");
                if (NativeDecoder && !decoder.Packets.Any(p => p.Enabled||p.MonitorRaw)) throw new ArgumentException("请先启用至少一个SPP包的遥测解码或原包监测");
                Reset(); recordedRunAlarms.Clear(); packetStatisticsMode = "demo"; Demo = true; replayCancel.Reset(); replayState = "playing"; replayError = "";
                replayFrames = replayDelivered = replaySkipped = replayBytes = 0; replayCaptureMs = replayLagMs = 0;
                replayClock = new Stopwatch(); long epoch = generation; ReplaySource source = replaySource;
                replayAnchorRealMs = replayAnchorCaptureMs = 0;
                replayThread = new Thread(() => RunReplay(source, epoch)) { IsBackground = true, Name = "Pcapng replay" }; replayThread.Start();
                Note("info", "开始pcapng " + replaySpeed + "倍速演示：" + source.Filename + "；仅在软件内部解码，不发送UDP。");
            }
        }
        private void RunReplay(ReplaySource source, long epoch)
        {
            try
            {
                long first = -1, previous = -1, previousSection = -1;
                CaptureReader.ReadPcapng(source.Stream, delegate(CaptureFrame frame, long position)
                {
                    if (replayCancel.WaitOne(0)) return;
                    if (!frame.Udp || frame.Payload == null || frame.Unsupported || !String.IsNullOrEmpty(frame.Issue))
                    { lock (Gate) { if (generation != epoch) return; replayFrames++; replaySkipped++; replayBytes = position; } return; }
                    if (frame.TimestampTicks < 0) throw new ArgumentException("抓包第" + frame.Number + "帧UDP没有有效时间戳，无法按原始时间间隔演示");
                    if (previous >= 0 && frame.TimestampTicks < previous) throw new ArgumentException("抓包第" + frame.Number + "帧时间戳倒退，无法按原始时间间隔演示");
                    if (first < 0) { first = frame.TimestampTicks; replayClock.Start(); }
                    previous = frame.TimestampTicks; double due = (frame.TimestampTicks - first) / 10000.0;
                    while (true)
                    {
                        double remaining;
                        lock (Gate) remaining = (due - ReplayPositionMs()) / replaySpeed;
                        if (remaining <= 0) break;
                        if (replayCancel.WaitOne((int)Math.Min(50, Math.Max(1, Math.Ceiling(remaining))))) return;
                    }
                    lock (Gate)
                    {
                        if (!Demo || generation != epoch) return;
                        replayFrames++; replayDelivered++; replayBytes = position; replayCaptureMs = due;
                        replayLagMs = Math.Max(0, (ReplayPositionMs() - due) / replaySpeed);
                        if (previousSection != frame.Section) { sequences.Clear(); previousSection = frame.Section; }
                        ProcessReceived(frame.Payload,frame.SourceIP,true,frame.SourcePort,frame.Number,EventStamp.Now(new DateTime(frame.TimestampTicks,DateTimeKind.Utc),"PCAP_CAPTURE"),frame.DestinationIP);
                    }
                }, () => replayCancel.WaitOne(0));
                lock (Gate)
                {
                    if (generation != epoch) return;
                    replayClock.Stop();replayAnchorRealMs=replayClock.Elapsed.TotalMilliseconds;replayAnchorCaptureMs=replayCaptureMs;
                    if(judgmentRunning)ProcessJudgmentEvent(new JudgmentEvent{Timer=true});
                    Demo = false; StopJudgmentCore("解码预览结束，判读已自动停止");
                    if (replayDelivered == 0) throw new ArgumentException("文件没有可回放的完整IPv4 UDP报文");
                    replayState = "completed"; replayBytes = source.Size; Note("info", "pcapng演示完成，保留最后参数值；可重新从头播放。");
                }
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    if (generation != epoch) return;
                    Demo = false; StopJudgmentCore("解码预览异常，判读已自动停止"); if (replayClock != null) replayClock.Stop(); replayState = "failed";
                    replayError = ex is ArgumentException || ex is IOException ? ex.Message : "演示读取失败：" + ex.GetType().Name;
                    Error = replayError; RecordAlarm("error", "演示异常", replayError, mode: "文件演示"); Note("error", replayError, false);
                }
            }
        }
        private void DisposeReplay()
        { if (replaySource != null) { replaySource.Dispose(); replaySource = null; } replayCancel.Dispose(); }
    }
}
