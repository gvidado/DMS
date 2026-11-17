using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class AlarmEntry
    {
        public string Kind, Time, Level, Category, Message, Source, Packet, Mode, Username, Actor;
        public long Id, ThroughId, CaptureFrame;
        public int Apid = -1, Sequence = -1;
        public string ContextCode, ContextName, ContextValue, ContextRule, RunId;
    }
    internal sealed class AlarmAcknowledgement
    {
        public long From, Through;
        public string Time, Username, Actor;
    }
    internal sealed class AlarmJournalSnapshot
    {
        public long Total, Acknowledged, Revision;
        public long VisibleTotal, VisibleAcknowledged;
        public List<AlarmAcknowledgement> Acknowledgements;
    }

    // Append-only JSON lines, with an in-memory offset index rather than a database or record cap.
    public sealed partial class AlarmJournal : IDisposable
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly FileStream file;
        private readonly List<long> offsets = new List<long>();
        private readonly List<int> visibleIds = new List<int>();
        private readonly List<AlarmAcknowledgement> acknowledgements = new List<AlarmAcknowledgement>();
        private readonly UTF8Encoding utf8 = new UTF8Encoding(false, true);
        private long acknowledged, revision, visibleAcknowledged;
        public string RecoveryMessage = "";

        public AlarmJournal(string filename)
        {
            path = filename; Directory.CreateDirectory(Path.GetDirectoryName(path));
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 65536);
            try { Load(); } catch { file.Dispose(); throw; }
        }
        private AlarmEntry Decode(byte[] bytes)
        {
            string text = utf8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\r');
            return String.IsNullOrWhiteSpace(text) ? null : JsonFile.Decode<AlarmEntry>(text);
        }
        private void Index(AlarmEntry entry, long offset)
        {
            if (entry == null) return;
            if (entry.Kind == "alarm" && entry.Id == offsets.Count + 1)
            {
                offsets.Add(offset);
                if (AlarmPolicy.IsVisible(entry)) visibleIds.Add((int)entry.Id);
            }
            else if (entry.Kind == "ack" && entry.ThroughId > acknowledged && entry.ThroughId <= offsets.Count)
            {
                AddConfirmation(acknowledged+1,entry.ThroughId,entry);
                acknowledged = entry.ThroughId;
            }
            else if(entry.Kind=="ack-one" && entry.Id>0 && entry.Id<=offsets.Count && visibleIds.BinarySearch((int)entry.Id)>=0 && Confirmation(entry.Id,acknowledgements)==null)
                AddConfirmation(entry.Id,entry.Id,entry);
            else throw new ArgumentException("告警历史记录顺序或格式异常，原文件未修改，请保留data/alarm-history.jsonl排查");
            revision++;
        }
        private void Load()
        {
            byte[] buffer = new byte[65536]; long position = 0, start = 0;
            using (var line = new MemoryStream())
            {
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                    for (int i = 0; i < read; i++)
                    {
                        position++;
                        if (buffer[i] == 10)
                        {
                            Index(Decode(line.ToArray()), start); line.SetLength(0); start = position;
                        }
                        else line.WriteByte(buffer[i]);
                    }
                if (line.Length > 0)
                {
                    AlarmEntry tail = null; bool complete = false;
                    try { tail = Decode(line.ToArray()); complete = tail != null; }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    if (complete)
                    {
                        Index(tail, start); file.Position = file.Length; file.WriteByte(10); file.Flush();
                    }
                    else
                    {
                        string backup = path + ".recovered-" + BeijingTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
                        file.Position = 0;
                        using (var saved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.CopyTo(saved);
                        file.SetLength(start); file.Position = start; file.Flush();
                        RecoveryMessage = "告警历史末尾存在上次未写完的内容，已备份为" + Path.GetFileName(backup) + "，完整历史记录已保留";
                    }
                }
            }
            file.Position = file.Length;
        }
        private void Write(AlarmEntry entry)
        {
            byte[] bytes = utf8.GetBytes(JsonFile.Encode(entry) + "\n");
            long start = file.Position;
            try { file.Write(bytes, 0, bytes.Length); file.Flush(); }
            catch { file.SetLength(start); file.Position = start; throw; }
            Index(entry, start);
        }
        public void Append(AlarmEntry entry)
        {
            if (!AlarmPolicy.IsVisible(entry)) return;
            lock (gate) { entry.Kind = "alarm"; entry.Id = offsets.Count + 1; Write(entry); }
        }
        public void AcknowledgeAll(string username, string actor)
        {
            lock (gate)
            {
                if (visibleAcknowledged == visibleIds.Count) return;
                Write(new AlarmEntry { Kind = "ack", ThroughId = offsets.Count, Time = BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), Username = username, Actor = actor });
            }
        }
        private void AddConfirmation(long from,long through,AlarmEntry entry)
        {
            // Fill only unconfirmed gaps. A later bulk acknowledgement must retain the
            // original actor/time of records which were individually acknowledged first.
            var additions=new List<AlarmAcknowledgement>();long cursor=from;
            foreach(var range in acknowledgements)
            {
                if(range.Through<cursor)continue;if(range.From>through)break;
                if(range.From>cursor)additions.Add(new AlarmAcknowledgement{From=cursor,Through=range.From-1,Time=entry.Time,Username=entry.Username,Actor=entry.Actor});
                cursor=Math.Max(cursor,range.Through+1);if(cursor>through)break;
            }
            if(cursor<=through)additions.Add(new AlarmAcknowledgement{From=cursor,Through=through,Time=entry.Time,Username=entry.Username,Actor=entry.Actor});
            foreach(var range in additions)visibleAcknowledged+=VisibleThrough(range.Through)-VisibleThrough(range.From-1);
            acknowledgements.AddRange(additions);acknowledgements.Sort((a,b)=>a.From.CompareTo(b.From));
        }
        public AlarmEntry AcknowledgeOne(long id,string username,string actor,out bool changed)
        {
            lock(gate)
            {
                if(id<=0 || id>offsets.Count || visibleIds.BinarySearch((int)id)<0)throw new ArgumentException("告警记录不存在或不是可确认的异常记录");
                AlarmEntry entry;using(var reader=OpenRead())entry=ReadAt(reader,offsets[(int)id-1]);
                changed=Confirmation(id,acknowledgements)==null;
                if(changed)Write(new AlarmEntry{Kind="ack-one",Id=id,Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Username=username,Actor=actor});
                return entry;
            }
        }
        internal bool HasPending(string source,string category,string message=null)
        {
            lock(gate)using(var reader=OpenRead())
            {
                for(int i=visibleIds.Count-1;i>=0;i--)
                {
                    int id=visibleIds[i];if(Confirmation(id,acknowledgements)!=null)continue;
                    var entry=ReadAt(reader,offsets[id-1]);
                    if((source==null || entry.Source==source) && (category==null || entry.Category==category) && (message==null || entry.Message==message))return true;
                }
                return false;
            }
        }
        private AlarmJournalSnapshot TakeSnapshot()
        {
            lock (gate) return new AlarmJournalSnapshot { Total = offsets.Count, Acknowledged = acknowledged, Revision = revision, Acknowledgements = acknowledgements.ToList(), VisibleTotal = visibleIds.Count, VisibleAcknowledged = visibleAcknowledged };
        }
        private int VisibleThrough(long through)
        {
            if (through <= 0) return 0;
            if (through >= offsets.Count) return visibleIds.Count;
            int position = visibleIds.BinarySearch((int)through);
            return position < 0 ? ~position : position + 1;
        }
        public object Summary(string error)
        {
            lock (gate) { long confirmed = visibleAcknowledged; return new { total = visibleIds.Count, pending = visibleIds.Count - confirmed, confirmed = confirmed, revision = revision, storageError = error }; }
        }
        private static AlarmAcknowledgement Confirmation(long id, List<AlarmAcknowledgement> ranges)
        {
            int low = 0, high = ranges.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; AlarmAcknowledgement range = ranges[middle];
                if (id < range.From) high = middle - 1;
                else if (id > range.Through) low = middle + 1;
                else return range;
            }
            return null;
        }
        private static object Row(AlarmEntry entry, List<AlarmAcknowledgement> ranges)
        {
            AlarmAcknowledgement ack = Confirmation(entry.Id, ranges);
            return new { id = entry.Id, time = entry.Time, level = entry.Level, category = entry.Category,
                message = entry.Message, source = entry.Source, packet = entry.Packet, apid = entry.Apid, sequence = entry.Sequence,
                mode = entry.Mode, captureFrame = entry.CaptureFrame, confirmed = ack != null,
                confirmedAt = ack == null ? "" : ack.Time, confirmedBy = ack == null ? "" : ack.Actor, confirmedUsername = ack == null ? "" : ack.Username };
        }
        private FileStream OpenRead()
        { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536); }
        private AlarmEntry ReadAt(FileStream reader, long offset)
        {
            reader.Position = offset;
            using (var bytes = new MemoryStream())
            {
                int next;
                while ((next = reader.ReadByte()) >= 0 && next != 10) bytes.WriteByte((byte)next);
                return Decode(bytes.ToArray());
            }
        }
        public object Page(int page, int size, string status, long through)
        {
            if (page < 1 || size < 1 || size > 100 || through < 0 || (status != "all" && status != "pending" && status != "confirmed"))
                throw new ArgumentException("告警记录分页或筛选条件无效");
            AlarmJournalSnapshot snapshot; List<long> selected; long matching, throughId; int pages;
            lock (gate)
            {
                snapshot = TakeSnapshot(); throughId = through == 0 ? snapshot.Total : Math.Min(through, snapshot.Total);int upper=VisibleThrough(throughId),cursor=0;
                // Runs of visible IDs support sparse confirmations without scanning every
                // historical JSON record on each page request.
                var runs=new List<Tuple<int,int>>();
                if(status=="all")runs.Add(Tuple.Create(0,upper));
                else
                {
                    foreach(var range in snapshot.Acknowledgements)
                    {
                        int start=Math.Min(upper,VisibleThrough(range.From-1)),end=Math.Min(upper,VisibleThrough(range.Through));
                        if(status=="pending" && cursor<start)runs.Add(Tuple.Create(cursor,start));
                        if(status=="confirmed" && start<end)runs.Add(Tuple.Create(start,end));
                        cursor=Math.Max(cursor,end);if(cursor>=upper)break;
                    }
                    if(status=="pending" && cursor<upper)runs.Add(Tuple.Create(cursor,upper));
                }
                matching=runs.Sum(r=>(long)r.Item2-r.Item1);pages=(int)Math.Max(1,(matching+size-1)/size);page=Math.Min(page,pages);
                long skip=(long)(page-1)*size;selected=new List<long>();
                for(int r=runs.Count-1;r>=0 && selected.Count<size;r--)
                {
                    var run=runs[r];long length=run.Item2-run.Item1;if(skip>=length){skip-=length;continue;}
                    for(long i=run.Item2-1-skip;i>=run.Item1 && selected.Count<size;i--)selected.Add(offsets[visibleIds[(int)i]-1]);skip=0;
                }
            }
            var rows = new List<object>();
            using (var reader = OpenRead()) foreach (long offset in selected) rows.Add(Row(ReadAt(reader, offset), snapshot.Acknowledgements));
            return new { revision = snapshot.Revision, total = snapshot.VisibleTotal, pending = snapshot.VisibleTotal - snapshot.VisibleAcknowledged,
                confirmed = snapshot.VisibleAcknowledged, matching = matching, throughId = throughId,
                page = page, pages = pages, pageSize = size, records = rows };
        }
        public void Export(TextWriter writer)
        {
            AlarmJournalSnapshot snapshot = TakeSnapshot();
            writer.WriteLine("记录号,发生时间(服务器),级别,类别,来源,SPP包,APID,帧计数,模式,抓包帧号,异常信息,确认状态,确认时间(服务器),确认人,确认账号");
            if (snapshot.VisibleTotal == 0) return;
            using (var reader = new StreamReader(OpenRead(), utf8, true, 65536))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    AlarmEntry entry = Decode(utf8.GetBytes(line)); if (entry == null || entry.Kind != "alarm") continue;
                    if (entry.Id > snapshot.Total) break;
                    if (!AlarmPolicy.IsVisible(entry)) continue;
                    AlarmAcknowledgement ack = Confirmation(entry.Id, snapshot.Acknowledgements);
                    writer.WriteLine(String.Join(",", new[] { entry.Id.ToString(), Monitor.CsvCell(entry.Time), Monitor.CsvCell(entry.Level == "error" ? "异常" : "警告"),
                        Monitor.CsvCell(entry.Category), Monitor.CsvCell(entry.Source), Monitor.CsvCell(entry.Packet), entry.Apid < 0 ? "" : entry.Apid.ToString(),
                        entry.Sequence < 0 ? "" : entry.Sequence.ToString(), Monitor.CsvCell(entry.Mode), entry.CaptureFrame > 0 ? entry.CaptureFrame.ToString() : "",
                        Monitor.CsvCell(entry.Message), ack == null ? "未确认" : "已确认", Monitor.CsvCell(ack == null ? "" : ack.Time), Monitor.CsvCell(ack == null ? "" : ack.Actor), Monitor.CsvCell(ack == null ? "" : ack.Username) }));
                    if (entry.Id == snapshot.Total) break;
                }
            }
        }
        public void Dispose() { lock (gate) file.Dispose(); }
    }
}
