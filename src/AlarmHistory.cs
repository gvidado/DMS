using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private AlarmJournal alarms;
        private string alarmStorageError = "";
        private readonly HashSet<string> recordedRunAlarms = new HashSet<string>(StringComparer.Ordinal);
        private static string AlarmSource(string source)
        {
            source = source ?? ""; int colon = source.LastIndexOf(':'); IPAddress address;
            return colon > 0 && IPAddress.TryParse(source.Substring(0, colon), out address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address.ToString() : source;
        }
        private string AlarmMode() { return Demo ? "文件演示" : Listening ? "实时UDP" : "系统运行"; }
        // False means an already recorded/filtered alarm; storage failures remain eligible for retry.
        private bool RecordAlarm(string level, string category, string message, string source = "", string packet = "", int apid = -1, int sequence = -1, string mode = null, long captureFrame = 0, string identity = null, JudgmentAbnormalEntry context = null)
        {
            lock (Gate)
            {
                if (alarms == null) return false;
                var entry = new AlarmEntry { Time = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), Level = level, Category = category,
                    Message = message, Source = source, Packet = packet, Apid = apid, Sequence = sequence, Mode = mode ?? AlarmMode(), CaptureFrame = captureFrame };
                if(context!=null){entry.ContextCode=context.Code;entry.ContextName=context.Name;entry.ContextValue=context.ValueText;entry.ContextRule=context.Script;entry.RunId=context.RunId;entry.Source=context.SourceIP;}
                if (!AlarmPolicy.IsVisible(entry)) return false;
                bool scoped = Listening || Demo;
                string sourceKey = category == "SPP超时" && identity != null ? "" : AlarmSource(source);
                string key = JsonFile.Encode(new object[] { level, category, sourceKey, packet, apid, identity ?? message });
                if (scoped && recordedRunAlarms.Contains(key)) return false;
                try
                {
                    alarms.Append(entry);
                    if (scoped) recordedRunAlarms.Add(key);
                    alarmStorageError = "";
                }
                catch (IOException) { alarmStorageError = Error = "告警历史保存失败，请检查data目录权限及磁盘空间，当前信息可能未写入历史文件"; }
                catch (UnauthorizedAccessException) { alarmStorageError = Error = "告警历史目录不可写，请检查data目录权限"; }
                catch (ObjectDisposedException) { return false; }
                return true;
            }
        }
        public object AlarmHistoryPage(int page, int size, string status, long through)
        { return alarms.Page(page, size, status, through); }
        public void WriteAlarmHistoryCsv(TextWriter writer) { alarms.Export(writer); }
        public void AcknowledgeAlarm(long id,string username,string actor)
        {
            lock(Gate)
            {
                bool changed;var entry=alarms.AcknowledgeOne(id,username,actor,out changed);if(!changed)return;
                SourceState source;
                if(!String.IsNullOrEmpty(entry.Source) && sources.TryGetValue(entry.Source,out source))
                {
                    if(entry.Category=="设备超时" && !alarms.HasPending(entry.Source,"设备超时"))source.OfflineLatched=false;
                    if(entry.Category=="疑似缺帧" && !alarms.HasPending(entry.Source,"疑似缺帧"))source.LossLatched=false;
                }
                if(Error.Length>0 && entry.Message==Error && !alarms.HasPending(null,null,Error))Error="";
                Note("info","告警记录#"+id+"已确认，历史记录保留；确认人："+actor);
            }
        }
    }
}
