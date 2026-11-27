using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class HistoryLocalState
    {
        public string Installation=Guid.NewGuid().ToString("N"),CurrentExperiment="";
        public List<string> ArchiveRoots=new List<string>();
        public List<HistoryExperiment> Experiments=new List<HistoryExperiment>();
    }
    internal sealed partial class HistoryStorage : IDisposable
    {
        private readonly string root,stateDirectory;
        private readonly object gate=new object(),stateGate=new object(),settingsGate=new object();
        private readonly Queue<HistoryRecord> queue=new Queue<HistoryRecord>();
        private readonly AutoResetEvent wake=new AutoResetEvent(false),databaseWake=new AutoResetEvent(false),maintenanceWake=new AutoResetEvent(false);
        private readonly Thread archiveThread,databaseThread,maintenanceThread;
        private volatile HistoryStorageConfiguration configuration;
        private HistoryStorageConfiguration pending;
        private HistoryLocalState local;
        private volatile bool closed;
        private long queuedBytes,queuedRecords,archivedEvents,archivedSegments,lastArchivedOrdinal;
        private string archiveError="",archiveWarning="",databaseError="",lastArchive="",lastCommit="",lastDatabaseRevision="",pendingAt="";
        private readonly string boot=Guid.NewGuid().ToString("N");
        private long ordinal;
        private readonly Semaphore querySlots=new Semaphore(4,4);
        internal event Action<string> StorageFault;
        internal event Action<HistoryRecord> PreparingRecord;
        private const long QueueLimit=128L*1024*1024;
        private const long ArchiveBatchBytes=8L*1024*1024;
        private const int ArchiveBatchRecords=500;
        internal HistoryStorage(string directory)
        {
            root=directory;stateDirectory=Path.Combine(root,"data","history-state");Directory.CreateDirectory(stateDirectory);
            string path=Path.Combine(root,"config","history-storage.json");configuration=File.Exists(path)?JsonFile.Load<HistoryStorageConfiguration>(path):new HistoryStorageConfiguration();configuration.Validate();
            string state=Path.Combine(stateDirectory,"state.json");local=File.Exists(state)?JsonFile.Load<HistoryLocalState>(state):new HistoryLocalState();
            if(configuration.RecordingEnabled)RegisterArchive(configuration.DirectoryFor(root));
            var rebuilt=RecoverGlobalManifest();
            foreach(string directoryPath in local.ArchiveRoots.ToArray())RecoverSegment(directoryPath,rebuilt);
            string pendingPath=Path.Combine(stateDirectory,"pending-settings.json");if(File.Exists(pendingPath)){pending=JsonFile.Load<HistoryStorageConfiguration>(pendingPath);pending.Validate();RegisterArchive(pending.DirectoryFor(root));ApplyPending(true);}
            archiveThread=new Thread(ArchiveLoop){IsBackground=true,Name="Permanent history archive"};databaseThread=new Thread(DatabaseLoop){IsBackground=true,Name="PostgreSQL history writer"};maintenanceThread=new Thread(HistoryMaintenanceLoop){IsBackground=true,Name="PostgreSQL history maintenance"};archiveThread.Start();databaseThread.Start();maintenanceThread.Start();
        }
        internal bool RecordingEnabled { get { return configuration.RecordingEnabled; } }
        internal string Installation {get{return local.Installation;}}
        internal string ArchiveDirectory {get{lock(gate)return configuration.DirectoryFor(root);}}
        internal void WaitForImportWindow(Func<bool> cancelled)
        {while(true){lock(gate){if(closed||archiveError.Length>0)throw new IOException(archiveError.Length>0?archiveError:"历史归档正在停止");if(queuedBytes<24L*1024*1024)return;}if(cancelled())throw new OperationCanceledException();Thread.Sleep(50);}}
        internal HistoryStorageConfiguration Configuration {get{HistoryStorageConfiguration value;lock(gate)value=pending??configuration;return HistoryJson.Decode<HistoryStorageConfiguration>(HistoryJson.Encode(value));}}
        private void RegisterArchive(string directory)
        {
            string path=Path.GetFullPath(directory);Directory.CreateDirectory(Path.Combine(path,"segments"));Directory.CreateDirectory(Path.Combine(path,"manifests"));
            lock(stateGate){if(!local.ArchiveRoots.Any(p=>String.Equals(p,path,StringComparison.OrdinalIgnoreCase))){local.ArchiveRoots.Add(path);JsonFile.Save(Path.Combine(stateDirectory,"state.json"),local);}}
        }
        internal HistoryRecord Record(string kind,EventStamp stamp=null,string session="",string config="")
        {
            stamp=stamp??EventStamp.Now();string experiment;lock(stateGate)experiment=local.CurrentExperiment;
            return new HistoryRecord{Kind=kind,Experiment=experiment,Session=session??"",Configuration=config??"",Boot=boot,
                Time=HistoryText.Utc(stamp.ObservedUtc),Received=HistoryText.Utc(stamp.ReceivedUtc),TimeBasis=stamp.Basis,
                ObservedTicks=stamp.ObservedUtc.Ticks,ReceivedTicks=stamp.ReceivedUtc.Ticks,MonotonicTicks=stamp.Monotonic,Ordinal=Interlocked.Increment(ref ordinal)};
        }
        internal string EnsureExperiment()
        {
            lock(stateGate)
            {
                var active=local.Experiments.FirstOrDefault(e=>e.Id==local.CurrentExperiment&&e.EndedAt.Length==0);if(active!=null)return active.Id;
            }
            return CreateExperiment("试验 "+BeijingTime.NowText(),"").Id;
        }
        internal HistoryExperiment CreateExperiment(string name,string notes)
        {
            name=(name??"").Trim();if(name.Length<1||name.Length>200||notes.Length>4000)throw new ArgumentException("试验名称应为1～200字符，说明最多4000字符");
            var experiment=new HistoryExperiment{Name=name,Notes=notes,StartedAt=HistoryText.Utc(DateTime.UtcNow)};
            lock(stateGate){local.Experiments.Add(experiment);local.CurrentExperiment=experiment.Id;JsonFile.Save(Path.Combine(stateDirectory,"state.json"),local);}
            var record=Record("EXPERIMENT");record.Data=experiment;Enqueue(record);return experiment;
        }
        internal object Experiments()
        {lock(stateGate)return new{current=local.CurrentExperiment,rows=local.Experiments.Select(e=>new{e.Id,e.Name,e.StartedAt,e.EndedAt,e.Notes}).Reverse().ToArray()};}
        internal void SelectExperiment(string id)
        {lock(stateGate){if(!local.Experiments.Any(e=>e.Id==id&&e.EndedAt.Length==0))throw new ArgumentException("试验不存在或已结束");local.CurrentExperiment=id;JsonFile.Save(Path.Combine(stateDirectory,"state.json"),local);}}
        internal void EndExperiment(string id)
        {
            HistoryExperiment experiment;lock(stateGate){experiment=local.Experiments.FirstOrDefault(e=>e.Id==id);if(experiment==null)throw new ArgumentException("试验不存在");if(experiment.EndedAt.Length>0)return;experiment.EndedAt=HistoryText.Utc(DateTime.UtcNow);if(local.CurrentExperiment==id)local.CurrentExperiment="";JsonFile.Save(Path.Combine(stateDirectory,"state.json"),local);}
            var record=Record("EXPERIMENT");record.Experiment=id;record.Data=experiment;Enqueue(record);
        }
        internal void Enqueue(HistoryRecord record)
        {
            // Judgment results remain in the existing application file archives only.
            if(record.Kind=="JUDGMENT")return;
            lock(gate)
            {
                if(closed)throw new InvalidOperationException("历史归档正在关闭");
                if(archiveError.Length>0)throw new IOException(archiveError);
                if(queuedBytes>QueueLimit)
                {
                    archiveError="历史归档队列达到保护上限，已停止采集以避免静默丢弃；请检查磁盘";
                    var handler=StorageFault;
                    if(handler!=null)ThreadPool.QueueUserWorkItem(_=>handler(archiveError));
                    throw new IOException(archiveError);
                }
                long nextBytes=queuedBytes+Math.Max(512,record.EstimatedBytes);
                bool notify=queue.Count==0||queue.Count==ArchiveBatchRecords-1||(queuedBytes<ArchiveBatchBytes&&nextBytes>=ArchiveBatchBytes);
                queue.Enqueue(record);
                queuedBytes=nextBytes;
                queuedRecords++;
                if(notify)wake.Set();
            }
        }
        internal void SaveConfiguration(HistoryStorageConfiguration value,string password,bool monitoring,string expectedRevision)
        {
            value.Validate();
            lock(settingsGate)
            {
                HistoryStorageConfiguration current;
                lock(gate)current=pending??configuration;
                if(expectedRevision!=current.Revision)throw new InvalidOperationException("历史存储配置已被其他用户修改，请重新载入");
                value.EncryptedPassword=password==null?current.EncryptedPassword:HistoryStorageConfiguration.Protect(password);
                value.Revision=Guid.NewGuid().ToString("N");
                RegisterArchive(value.DirectoryFor(root));
                JsonFile.Save(Path.Combine(stateDirectory,"pending-settings.json"),value);
                lock(gate)
                {
                    pending=value;
                    pendingAt=HistoryText.Utc(DateTime.UtcNow);
                }
                if(!monitoring)ApplyPending(true);
            }
        }
        internal bool ApplyPending(bool allowed)
        {
            if(!allowed)return false;
            lock(settingsGate)
            {
                HistoryStorageConfiguration next;
                lock(gate)next=pending;
                if(next==null)return false;

                JsonFile.Save(Path.Combine(root,"config","history-storage.json"),next);
                lock(gate)
                {
                    configuration=next;
                    pending=null;
                    pendingAt="";
                }
                wake.Set();
                databaseWake.Set();
                maintenanceWake.Set();
                File.Delete(Path.Combine(stateDirectory,"pending-settings.json"));
                return true;
            }
        }
        internal object Status()
        {
            HistoryStorageConfiguration settings;lock(gate)settings=configuration;long free=-1,total=-1;try{var drive=new DriveInfo(Path.GetPathRoot(settings.DirectoryFor(root)));free=drive.AvailableFreeSpace;total=drive.TotalSize;}catch(IOException){}catch(UnauthorizedAccessException){}
            lock(gate)return new{enabled=configuration.RecordingEnabled,permanent=true,databaseEnabled=configuration.DatabaseEnabled,archiveDirectory=configuration.DirectoryFor(root),pending=pending!=null,pendingAt,
                queued=queue.Count,queuedBytes,received=queuedRecords,archivedEvents,archivedSegments,lastArchivedOrdinal,lastArchive,lastCommit,archiveError,archiveWarning,databaseError,
                parameterDefinitionsRevision=Interlocked.Read(ref historyDefinitionGeneration),
                databaseReady=configuration.DatabaseEnabled&&databaseError.Length==0&&lastCommit.Length>0,progress=new{segments=databaseCursor.Segments,events=databaseCursor.Events,samples=databaseCursor.Samples},freeBytes=free,totalBytes=total,lowSpace=free>=0&&free<Math.Max(10L*1024*1024*1024,total/10),
                timing=new{clock="UTC + Stopwatch",monotonicFrequency=System.Diagnostics.Stopwatch.Frequency,boot,accuracyVerified=false,note="时间戳分辨率不等同实际时钟误差；精度需现场校验"}};
        }
        private void ArchiveLoop()
        {
            var records=new List<HistoryRecord>(ArchiveBatchRecords);
            string directory;
            long bytes;
            while(TakeArchiveBatch(records,out directory,out bytes))
            {
                bool written=false,retry=false;
                HistoryArchiveSegment segment=null;
                WaitForCaptureReceipts(records);
                while(true)
                {
                    try
                    {
                        if(!written)
                        {
                            if(retry)
                            {
                                string currentDirectory;
                                lock(gate)currentDirectory=configuration.DirectoryFor(root);
                                if(!String.Equals(directory,currentDirectory,StringComparison.OrdinalIgnoreCase))
                                {
                                    directory=currentDirectory;
                                    segment=null;
                                }
                            }
                            if(segment==null)segment=NewSegment(records,directory);
                            WriteSegment(records,segment,directory);
                            written=true;
                        }
                        CommitSegment(directory,segment,retry);
                        string archivedAt=HistoryText.Utc(DateTime.UtcNow);
                        lock(gate)
                        {
                            foreach(var record in records)
                            {
                                if(!Object.ReferenceEquals(queue.Peek(),record))throw new InvalidOperationException("历史队列顺序异常");
                                queue.Dequeue();
                            }
                            queuedBytes-=bytes;
                            archivedEvents+=records.Count;
                            lastArchivedOrdinal=records[records.Count-1].Ordinal;
                            archivedSegments++;
                            lastArchive=archivedAt;
                            archiveError="";
                        }
                        databaseWake.Set();
                        break;
                    }
                    catch(Exception ex)
                    {
                        string message="历史档案写入失败，原始队列保留："+ex.Message;
                        lock(gate)archiveError=message;
                        var fault=StorageFault;
                        if(fault!=null)ThreadPool.QueueUserWorkItem(_=>fault(message));
                        if(closed)return;
                        retry=true;
                        wake.WaitOne(1000);
                    }
                }
                records.Clear();
            }
        }
        private bool TakeArchiveBatch(List<HistoryRecord> records,out string directory,out long bytes)
        {
            long deadline=0;
            directory="";
            bytes=0;
            while(true)
            {
                int waitMilliseconds;
                lock(gate)
                {
                    if(queue.Count==0)
                    {
                        if(closed)return false;
                        waitMilliseconds=configuration.FlushMilliseconds;
                    }
                    else
                    {
                        long now=Stopwatch.GetTimestamp();
                        if(deadline==0)
                        {
                            directory=configuration.DirectoryFor(root);
                            deadline=now;
                            if(!closed&&queue.Count<128&&queuedBytes<1024*1024)
                                deadline+=configuration.FlushMilliseconds*Stopwatch.Frequency/1000;
                        }

                        if(closed||queue.Count>=ArchiveBatchRecords||queuedBytes>=ArchiveBatchBytes||now>=deadline)
                        {
                            long estimated=0;
                            foreach(var record in queue)
                            {
                                if(records.Count>0&&(estimated>ArchiveBatchBytes||records.Count>=ArchiveBatchRecords))break;
                                records.Add(record);
                                estimated+=record.EstimatedBytes;
                                bytes+=Math.Max(512,record.EstimatedBytes);
                            }
                            return true;
                        }
                        waitMilliseconds=(int)Math.Ceiling((deadline-now)*1000.0/Stopwatch.Frequency);
                    }
                }
                wake.WaitOne(waitMilliseconds);
            }
        }
        private void WaitForCaptureReceipts(List<HistoryRecord> records)
        {
            long latest=Int64.MinValue;
            foreach(var record in records)
                if((record.Kind=="COMMAND_SEND"&&record.Status=="SENT")&&record.MonotonicTicks>latest)
                    latest=record.MonotonicTicks;
            if(closed||latest==Int64.MinValue)return;
            double age=(Stopwatch.GetTimestamp()-latest)*1000.0/Stopwatch.Frequency;
            if(age<120)Thread.Sleep((int)Math.Ceiling(120-age));
        }
        private HistoryArchiveSegment NewSegment(List<HistoryRecord> records,string directory)
        {
            DateTime now=DateTime.UtcNow;
            string id=Guid.NewGuid().ToString("N");
            string folder=Path.Combine(directory,"segments",now.ToString("yyyy-MM-dd"),now.ToString("HH"));
            Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,now.ToString("HHmmssfffffff")+"-"+id+".hst.gz");
            return new HistoryArchiveSegment{Id=id,File=file,Count=records.Count,FirstTime=records[0].Time,LastTime=records[records.Count-1].Time};
        }
        private void WriteSegment(List<HistoryRecord> records,HistoryArchiveSegment segment,string directory)
        {
            string temporary=segment.File+".tmp";
            JsonFile.Save(Path.Combine(directory,"active-segment.json"),segment);
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            using(var sha=SHA256.Create())
            {
                using(var hashing=new ArchiveHashWriter(output,sha))
                using(var gzip=new GZipStream(hashing,CompressionLevel.Fastest,true))
                using(var writer=new StreamWriter(gzip,new UTF8Encoding(false),65536))
                {
                    foreach(var record in records)
                    {
                        var prepare=PreparingRecord;
                        if(prepare!=null)prepare(record);
                        if(record.Kind=="CONFIG")record.Reason=HistoryText.Hash(HistoryJson.Encode(record.Data));
                        writer.WriteLine(HistoryJson.Encode(record));
                    }
                    writer.WriteLine(HistoryJson.Encode(new{end=records.Count}));
                }
                sha.TransformFinalBlock(EmptyArchiveBytes,0,0);
                output.Flush(true);
                segment.Bytes=output.Length;
                segment.Hash=FormatArchiveHash(sha.Hash);
            }
            File.Move(temporary,segment.File);
        }
        private static readonly byte[] EmptyArchiveBytes=new byte[0];
        private static string FormatArchiveHash(byte[] value)
        {return BitConverter.ToString(value).Replace("-","").ToLowerInvariant();}
        private sealed class ArchiveHashWriter : Stream
        {
            private readonly Stream output;
            private readonly HashAlgorithm hash;
            internal ArchiveHashWriter(Stream output,HashAlgorithm hash)
            {this.output=output;this.hash=hash;}
            public override bool CanRead {get{return false;}}
            public override bool CanSeek {get{return false;}}
            public override bool CanWrite {get{return true;}}
            public override long Length {get{throw new NotSupportedException();}}
            public override long Position {get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override void Flush(){output.Flush();}
            public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}
            public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
            public override void SetLength(long value){throw new NotSupportedException();}
            public override void Write(byte[] buffer,int offset,int count)
            {
                output.Write(buffer,offset,count);
                hash.TransformBlock(buffer,offset,count,null,0);
            }
        }
        private void CommitSegment(string directory,HistoryArchiveSegment info,bool retry,bool globalAlreadyIndexed=false)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(HistoryJson.Encode(info)+"\n");
            AppendArchiveIndex(Path.Combine(directory,"archive-index.jsonl"),info.Id,bytes,retry);
            if(!globalAlreadyIndexed)AppendArchiveIndex(Path.Combine(stateDirectory,"archive-index.jsonl"),info.Id,bytes,retry);
            File.Delete(Path.Combine(directory,"active-segment.json"));
        }
        private static void AppendArchiveIndex(string path,string segmentId,byte[] bytes,bool retry)
        {
            if(!retry)
            {
                using(var journal=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read))
                {
                    journal.Write(bytes,0,bytes.Length);
                    journal.Flush(true);
                }
                return;
            }

            // An interrupted append may have completed on one journal only.
            // Keep every complete row and its byte offset, and replace only a torn tail.
            using(var journal=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read))
            {
                long end=LastArchiveIndexNewline(journal,journal.Length)+1;
                if(end<journal.Length)journal.SetLength(end);
                bool exists=false;
                if(end>0)
                {
                    long start=LastArchiveIndexNewline(journal,end-1)+1;
                    if(end-start>1024*1024)throw new InvalidDataException("历史分段索引单行过大："+path);
                    var line=new byte[(int)(end-start-1)];
                    journal.Position=start;
                    ReadArchiveIndexBytes(journal,line,line.Length);
                    var last=HistoryJson.Decode<HistoryArchiveSegment>(Encoding.UTF8.GetString(line));
                    exists=last!=null&&last.Id==segmentId;
                }
                if(!exists)
                {
                    journal.Position=end;
                    journal.Write(bytes,0,bytes.Length);
                }
                journal.Flush(true);
            }
        }
        private static long LastArchiveIndexNewline(FileStream journal,long end)
        {
            if(end==0)return -1;
            byte[] block=new byte[4096];
            while(end>0)
            {
                int count=(int)Math.Min(block.Length,end);
                long start=end-count;
                journal.Position=start;
                ReadArchiveIndexBytes(journal,block,count);
                for(int i=count-1;i>=0;i--)if(block[i]==10)return start+i;
                end=start;
            }
            return -1;
        }
        private static void ReadArchiveIndexBytes(FileStream journal,byte[] buffer,int count)
        {
            int offset=0;
            while(offset<count)
            {
                int read=journal.Read(buffer,offset,count-offset);
                if(read==0)throw new EndOfStreamException("历史分段索引在读取期间缩短");
                offset+=read;
            }
        }
        private void RecoverSegment(string directory,Dictionary<string,HistoryArchiveSegment> rebuilt)
        {
            string path=Path.Combine(directory,"active-segment.json");if(!File.Exists(path))return;
            try
            {
                var segment=JsonFile.Load<HistoryArchiveSegment>(path);string file=Path.GetFullPath(segment.File);if(!file.StartsWith(Path.GetFullPath(directory).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("待恢复分段不在登记的档案目录内");
                if(!File.Exists(file)&&File.Exists(file+".tmp"))
                {
                    int count=0;bool complete=false;using(var input=File.OpenRead(file+".tmp"))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(gzip,Encoding.UTF8))
                    {string line;while((line=reader.ReadLine())!=null){if(count==segment.Count){var end=HistoryJson.Decode<Dictionary<string,object>>(line);if(Convert.ToInt32(end["end"])!=count)throw new InvalidDataException("结束标记不一致");complete=true;if(reader.ReadLine()!=null)throw new InvalidDataException("结束标记后还有多余内容");break;}HistoryJson.Decode<HistoryRecord>(line);count++;}if(count!=segment.Count||!complete)throw new InvalidDataException("未完成的历史分段");}
                    File.Move(file+".tmp",file);
                }
                if(File.Exists(file)){SetArchiveDigest(file,segment);CommitSegment(directory,segment,true,rebuilt!=null&&rebuilt.ContainsKey(segment.Id));archiveWarning="上次退出存在已落盘未登记分段，已恢复索引并保留原文件";}
                else archiveWarning="上次退出时存在未完成分段，原文件保留；请核对采集完整性："+file;
            }
            catch(Exception ex){archiveWarning="上次退出的历史分段未完整落盘，文件保留待核对："+ex.Message;}
            if(File.Exists(path))File.Move(path,path+".interrupted-"+Guid.NewGuid().ToString("N"));
        }
        private Dictionary<string,HistoryArchiveSegment> RecoverGlobalManifest()
        {
            string manifest=Path.Combine(stateDirectory,"archive-index.jsonl");
            if(File.Exists(manifest))return null;
            var segments=new Dictionary<string,HistoryArchiveSegment>();
            foreach(string directory in local.ArchiveRoots)
            {
                string index=Path.Combine(directory,"archive-index.jsonl");
                if(!File.Exists(index))continue;
                using(var file=File.OpenRead(index))
                {
                    bool tornTail=false;
                    if(File.Exists(Path.Combine(directory,"active-segment.json"))&&file.Length>0)
                    {
                        file.Position=file.Length-1;
                        tornTail=file.ReadByte()!=10;
                        file.Position=0;
                    }
                    using(var reader=new StreamReader(file,Encoding.UTF8))
                    {
                        string line;
                        while((line=reader.ReadLine())!=null)
                        {
                            if(tornTail&&reader.EndOfStream)break;
                            if(String.IsNullOrWhiteSpace(line))continue;
                            var item=HistoryJson.Decode<HistoryArchiveSegment>(line);
                            segments[item.Id]=item;
                        }
                    }
                }
            }
            if(segments.Count==0)return null;
            var ordered=segments.Values.OrderBy(v=>File.GetLastWriteTimeUtc(v.File)).ThenBy(v=>v.File,StringComparer.Ordinal).ToArray();
            string temporary=manifest+".recover-"+Guid.NewGuid().ToString("N");
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                using(var writer=new StreamWriter(output,new UTF8Encoding(false),65536,true))
                {
                    foreach(var segment in ordered)writer.WriteLine(HistoryJson.Encode(segment));
                    writer.Flush();
                }
                output.Flush(true);
            }
            File.Move(temporary,manifest);
            foreach(string checkpoint in Directory.GetFiles(stateDirectory,"database-*.json"))
                File.Move(checkpoint,checkpoint+".reindexed-"+Guid.NewGuid().ToString("N"));
            archiveWarning="全局历史索引已由原包目录索引重建，数据库将按事件ID核对补写，不删除原包";
            return segments;
        }
        private static void SetArchiveDigest(string file,HistoryArchiveSegment segment)
        {
            using(var input=File.OpenRead(file))
            using(var sha=SHA256.Create())
            {
                segment.Bytes=input.Length;
                segment.Hash=FormatArchiveHash(sha.ComputeHash(input));
            }
        }
        private HistoryRecord[] ReadSegment(HistoryArchiveSegment segment)
        {
            string full=Path.GetFullPath(segment.File);lock(stateGate)if(!local.ArchiveRoots.Any(p=>full.StartsWith(p.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("历史档案不在已登记目录内");
            using(var input=File.OpenRead(full))using(var sha=System.Security.Cryptography.SHA256.Create())if(BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant()!=segment.Hash)throw new InvalidDataException("历史档案哈希不一致，原文件保留："+segment.File);
            var result=new List<HistoryRecord>();using(var file=File.OpenRead(full))using(var gzip=new GZipStream(file,CompressionMode.Decompress))using(var reader=new StreamReader(gzip,Encoding.UTF8))
            {for(int i=0;i<segment.Count;i++){string line=reader.ReadLine();if(line==null)throw new InvalidDataException("历史档案截断："+segment.File);result.Add(HistoryJson.Decode<HistoryRecord>(line));}var end=HistoryJson.Decode<Dictionary<string,object>>(reader.ReadLine());if(end==null||!end.ContainsKey("end")||Convert.ToInt32(end["end"])!=segment.Count||reader.ReadLine()!=null)throw new InvalidDataException("历史档案结束标记异常："+segment.File);}
            return result.ToArray();
        }
        public void Dispose()
        {
            closed=true;wake.Set();databaseWake.Set();maintenanceWake.Set();CloseDatasets();CancelHistoryMaintenance();archiveThread.Join();databaseThread.Join(20000);if(!maintenanceThread.Join(5000))Console.Error.WriteLine("History database maintenance did not finish cancellation before exit.");lock(gate)if(queue.Count>0)Console.Error.WriteLine("History archive could not flush "+queue.Count+" queued records: "+archiveError);
        }
    }
}
