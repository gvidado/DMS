using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class HistoryDatasetJob
    {
        internal string Id=Guid.NewGuid().ToString("N"),Username,State="waiting",Phase="等待归档",Error="",File="",Snapshot="";
        internal long Rows,Bytes;
        internal readonly ManualResetEvent Cancel=new ManualResetEvent(false);
        internal HistoryPostgresClient Client;
        internal Thread Worker;
    }

    internal sealed partial class HistoryStorage
    {
        private readonly object datasetGate=new object();
        private readonly List<HistoryDatasetJob> datasetJobs=new List<HistoryDatasetJob>();
        private static bool DatasetActive(HistoryDatasetJob job){return job.State=="waiting"||job.State=="exporting";}
        internal object DatasetStatus(string username)
        {
            lock(datasetGate)
            {
                var job=datasetJobs.LastOrDefault(value=>value.Username==username);
                return new{job=job==null?null:new{job.Id,job.State,job.Phase,job.Error,job.Rows,job.Bytes,job.Snapshot,
                    downloadUrl=job.State=="ready"?"/api/history/foundation/download?id="+job.Id:""}};
            }
        }
        internal object StartDataset(string username)
        {
            lock(datasetGate)
            {
                if(datasetJobs.Any(DatasetActive))throw new InvalidOperationException("已有数据底座导出正在进行，请等待完成或取消");
                HistoryStorageConfiguration settings;lock(gate)settings=configuration;
                if(!settings.DatabaseEnabled)throw new InvalidOperationException("请先启用历史数据库连接");
                var job=new HistoryDatasetJob{Username=username};
                string directory=Path.Combine(root,"data","history-exports");Directory.CreateDirectory(directory);
                job.File=Path.Combine(directory,job.Id+".zip");datasetJobs.Add(job);
                job.Worker=new Thread(()=>RunDataset(job,settings)){IsBackground=true,Name="Full history dataset export"};
                try{job.Worker.Start();}
                catch{datasetJobs.Remove(job);job.Cancel.Dispose();throw;}
            }
            return DatasetStatus(username);
        }
        internal void CancelDataset(string username)
        {
            HistoryDatasetJob job;HistoryPostgresClient client;
            lock(datasetGate){job=datasetJobs.LastOrDefault(value=>value.Username==username&&DatasetActive(value));if(job==null)return;job.Cancel.Set();client=job.Client;}
            if(client!=null)client.Cancel();
        }
        internal FileStream OpenDataset(string username,string id)
        {
            lock(datasetGate)
            {
                var job=datasetJobs.FirstOrDefault(value=>value.Id==id&&value.Username==username&&value.State=="ready");
                if(job==null)throw new ArgumentException("导出文件不存在、尚未完成或不属于当前账号");
                return new FileStream(job.File,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            }
        }
        private void DatasetCheck(HistoryDatasetJob job)
        {
            if(closed||job.Cancel.WaitOne(0))throw new OperationCanceledException("数据底座导出已取消");
        }
        private void DatasetPhase(HistoryDatasetJob job,string phase)
        {lock(datasetGate)job.Phase=phase;}
        private void DatasetBarrier(HistoryDatasetJob job,HistoryStorageConfiguration settings)
        {
            long target;lock(gate)target=queuedRecords;
            wake.Set();
            while(true)
            {
                DatasetCheck(job);
                lock(gate)
                {
                    if(archiveError.Length>0)throw new IOException(archiveError);
                    if(archivedEvents>=target)break;
                }
                job.Cancel.WaitOne(100);
            }
            string index=Path.Combine(stateDirectory,"archive-index.jsonl");
            long offset=0;
            if(File.Exists(index))
                using(var file=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                    offset=LastArchiveIndexNewline(file,file.Length)+1;
            DatasetPhase(job,"等待快照范围内的数据补写入库");
            while(true)
            {
                DatasetCheck(job);
                lock(gate)
                {
                    if(configuration.Revision!=settings.Revision)throw new InvalidOperationException("历史存储配置已切换，请重新导出");
                    if(databaseError.Length>0)throw new InvalidOperationException("历史补写尚未完成："+databaseError);
                    long indexed;databaseCursor.Offsets.TryGetValue(index,out indexed);
                    if(lastDatabaseRevision==settings.Revision&&indexed>=offset)break;
                }
                job.Cancel.WaitOne(200);
            }
        }
        private void RunDataset(HistoryDatasetJob job,HistoryStorageConfiguration settings)
        {
            HistoryPostgresClient database=null;
            bool slot=false;
            try
            {
                DatasetBarrier(job,settings);
                while(!(slot=querySlots.WaitOne(100)))DatasetCheck(job);
                database=new HistoryPostgresClient(root,settings,true);
                lock(datasetGate){job.Client=database;job.State="exporting";job.Snapshot=HistoryText.Utc(EventStamp.PreciseUtcNow());}
                database.Query("SET statement_timeout=300000");
                database.Query("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY");
                var counts=new Dictionary<string,long>();
                using(var output=new FileStream(job.File,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
                {
                    var tables=new[]{
                        new[]{"events.jsonl","SELECT (to_jsonb(e)||jsonb_build_object('event_time',"+HistoryEventTimeSql+",'event_ticks',"+HistoryEventTicksSql+"::text))::text AS item FROM td_history.event e WHERE e.kind<>'JUDGMENT'"},
                        new[]{"samples.jsonl","SELECT (to_jsonb(s)||jsonb_build_object('event_time',"+HistoryEventTimeSql+",'event_ticks',"+HistoryEventTicksSql+"::text))::text AS item FROM td_history.sample s JOIN td_history.event e ON e.id=s.packet_event_id"},
                        new[]{"parameter_definitions.jsonl","SELECT to_jsonb(p)::text AS item FROM td_history.parameter_definition p"},
                        new[]{"configurations.jsonl","SELECT to_jsonb(c)::text AS item FROM td_history.configuration c"},
                        new[]{"commands.jsonl","SELECT to_jsonb(c)::text AS item FROM td_history.command_event c"},
                        new[]{"incidents.jsonl","SELECT to_jsonb(i)::text AS item FROM td_history.incident i"},
                        new[]{"experiments.jsonl","SELECT to_jsonb(x)::text AS item FROM td_history.experiment x"}
                    };
                    foreach(var table in tables)counts[table[0]]=DatasetTable(job,database,zip,table[0],table[1]);
                    counts["raw_packets.jsonl"]=DatasetRaw(job,database,zip);
                    counts["source_files.jsonl"]=DatasetSources(job,database,zip);
                    DatasetText(zip,"manifest.json",HistoryJson.Encode(new{
                        format="telemetry-dataset-jsonl-v1",snapshot=job.Snapshot,allTime=true,counts,excludedEventKinds=new[]{"JUDGMENT"},
                        joins=new{samples="samples.packet_event_id = events.id; samples.parameter_id = parameter_definitions.id",
                            configurations="parameter_definitions.configuration_id = configurations.id",raw="raw_packets.event_id = events.id"},
                        valuePolicy="Use value_text and value_kind to preserve exact engineering values. Missing samples are not interpolated.",
                        timePolicy="event_time is server-local observation time normalized as a timezone-aware timestamp; historical PCAP retains original capture time.",
                        rawEncoding="payload_base64",softwareVersion="0.10.16"}));
                    DatasetText(zip,"README.txt",
                        "数据底座全量快照\n\n每个JSONL文件每行一个JSON对象，UTF-8编码。events包含遥测、通信、指令、异常、配置和会话等事件，不包含判读记录，samples保存解码样本，raw_packets保存完整UDP载荷。\n"+
                        "samples通过packet_event_id关联events，通过parameter_id关联parameter_definitions；配置、指令与异常保留原关联键。\n"+
                        "工程值以value_text为准，结合value_kind转换，长整数可直接使用Python int或Decimal。未提供的参数保持缺失，勿按零填充。\n"+
                        "时间使用event_time；event_ticks保留100ns计时整数文本。指令事件保留代号、端点和原始载荷关联。\n"+
                        "本快照在已入队记录归档并补写后建立数据库一致性视图，不限日期、不限当前页、也不受近期200条窗口限制。\n"+
                        "source_files.jsonl将原始pcapng文件路径映射到ZIP中的文件，并提供SHA256。\n"+
                        "可用以下Python代码逐行读取，无额外依赖：\n"+
                        "import json, zipfile\nwith zipfile.ZipFile('dataset.zip') as z:\n    with z.open('samples.jsonl') as f:\n        for line in f:\n            sample = json.loads(line)\n            # sample['value_text'], sample['parameter_id'], sample['packet_event_id']\n");
                }
                database.Query("COMMIT");
                lock(datasetGate){job.Bytes=new FileInfo(job.File).Length;job.State="ready";job.Phase="导出完成";}
            }
            catch(Exception error)
            {
                lock(datasetGate){job.State=job.Cancel.WaitOne(0)||closed?"cancelled":"failed";job.Error=error.Message;job.Phase=job.State=="cancelled"?"已取消":"导出失败";}
                try{if(File.Exists(job.File))File.Delete(job.File);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            finally
            {
                lock(datasetGate)job.Client=null;
                if(database!=null)database.Dispose();
                if(slot)querySlots.Release();
            }
        }
        private long DatasetTable(HistoryDatasetJob job,HistoryPostgresClient database,ZipArchive zip,string name,string sql)
        {
            DatasetPhase(job,"导出 "+name);
            long count=0;
            database.Query("DECLARE dataset_rows NO SCROLL CURSOR FOR "+sql);
            try
            {
                using(var stream=zip.CreateEntry(name,CompressionLevel.Fastest).Open())
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false),65536))
                {
                    while(true)
                    {
                        DatasetCheck(job);
                        var rows=database.Query("FETCH FORWARD 512 FROM dataset_rows");
                        foreach(var row in rows)writer.WriteLine(row["item"]);
                        count+=rows.Count;lock(datasetGate)job.Rows+=rows.Count;
                        if(rows.Count<512)break;
                    }
                }
            }
            finally{database.Query("CLOSE dataset_rows");}
            return count;
        }
        private long DatasetRaw(HistoryDatasetJob job,HistoryPostgresClient database,ZipArchive zip)
        {
            DatasetPhase(job,"导出完整UDP载荷");
            long count=0;database.Query("DECLARE dataset_segments NO SCROLL CURSOR FOR SELECT id::text,archive_file,sha256,event_count::text FROM td_history.segment ORDER BY committed_at,id");
            try
            {
                using(var stream=zip.CreateEntry("raw_packets.jsonl",CompressionLevel.Fastest).Open())
                using(var writer=new StreamWriter(stream,new UTF8Encoding(false),65536))
                {
                    while(true)
                    {
                        DatasetCheck(job);var segments=database.Query("FETCH FORWARD 16 FROM dataset_segments");
                        foreach(var row in segments)
                        {
                            DatasetCheck(job);
                            var segment=new HistoryArchiveSegment{Id=row["id"],File=row["archive_file"],Hash=row["sha256"],Count=Int32.Parse(row["event_count"])};
                            var records=ReadSegment(segment);
                            string ids="{"+String.Join(",",records.Select(record=>record.Id))+"}";
                            var owners=database.Query("SELECT id::text,payload_sha256,payload_length::text FROM td_history.event WHERE id=ANY($1::uuid[]) AND segment_id=$2::uuid AND kind<>'JUDGMENT'",ids,segment.Id)
                                .ToDictionary(value=>Guid.Parse(value["id"]).ToString("N"));
                            foreach(var record in records)
                            {
                                Dictionary<string,string> owner;
                                if(record.PayloadBase64.Length==0||!owners.TryGetValue(Guid.Parse(record.Id).ToString("N"),out owner))continue;
                                writer.WriteLine(HistoryJson.Encode(new{event_id=Guid.Parse(record.Id).ToString(),payload_base64=record.PayloadBase64,
                                    payload_sha256=owner["payload_sha256"],payload_length=Int32.Parse(owner["payload_length"]),configuration_id=record.Configuration.Length>0?Guid.Parse(record.Configuration).ToString():null,
                                    packet_id=record.PacketId,source_ip=record.SourceIP,destination_ip=record.DestinationIP,source_port=record.SourcePort,destination_port=record.DestinationPort}));
                                count++;
                            }
                        }
                        if(segments.Count<16)break;
                    }
                }
            }
            finally{database.Query("CLOSE dataset_segments");}
            lock(datasetGate)job.Rows+=count;return count;
        }
        private long DatasetSources(HistoryDatasetJob job,HistoryPostgresClient database,ZipArchive zip)
        {
            DatasetPhase(job,"导出原始捕获文件");
            var files=database.Query(@"SELECT DISTINCT data->'Data'->>'sourceFile' AS file,data->'Data'->>'sourceSha256' AS sha
FROM td_history.event WHERE NULLIF(data->'Data'->>'sourceFile','') IS NOT NULL");
            var mappings=new List<object>();var copied=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var source in files)
            {
                DatasetCheck(job);string file=Path.GetFullPath(source["file"]);
                lock(stateGate)if(!local.ArchiveRoots.Any(directory=>file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("原始捕获文件不在登记的历史目录内："+file);
                string entry;
                if(!copied.TryGetValue(file,out entry))
                {
                    entry="source-files/"+copied.Count.ToString("D6")+"-"+Path.GetFileName(file);
                    using(var input=File.OpenRead(file))
                    using(var output=zip.CreateEntry(entry,CompressionLevel.Fastest).Open())
                    using(var sha=SHA256.Create())
                    {
                        byte[] buffer=new byte[1024*1024];int read;
                        while((read=input.Read(buffer,0,buffer.Length))>0){DatasetCheck(job);output.Write(buffer,0,read);sha.TransformBlock(buffer,0,read,null,0);}
                        sha.TransformFinalBlock(new byte[0],0,0);
                        string actual=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                        if(!String.IsNullOrEmpty(source["sha"])&&actual!=source["sha"])throw new InvalidDataException("原始捕获文件SHA256不一致："+file);
                        mappings.Add(new{original_path=file,entry,sha256=actual});
                    }
                    copied[file]=entry;
                }
            }
            DatasetText(zip,"source_files.jsonl",String.Join("\n",mappings.Select(HistoryJson.Encode))+(mappings.Count>0?"\n":""));
            lock(datasetGate)job.Rows+=mappings.Count;
            return mappings.Count;
        }
        private static void DatasetText(ZipArchive zip,string name,string text)
        {using(var stream=zip.CreateEntry(name).Open())using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);}
        private void CloseDatasets()
        {
            HistoryDatasetJob[] jobs;lock(datasetGate){jobs=datasetJobs.ToArray();foreach(var job in jobs)job.Cancel.Set();}
            foreach(var job in jobs)
            {
                HistoryPostgresClient client;lock(datasetGate)client=job.Client;
                if(client!=null)try{client.Cancel();}catch{}
                if(job.Worker!=null)job.Worker.Join(5000);
                if(job.Worker==null||!job.Worker.IsAlive)try{if(File.Exists(job.File))File.Delete(job.File);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    public sealed partial class Server
    {
        private bool HandleHistoryDataset(System.Net.HttpListenerContext context,Session session)
        {
            string path=context.Request.Url.AbsolutePath,method=context.Request.HttpMethod;
            if(path=="/api/history/foundation/status"&&method=="GET"){Json(context.Response,historyStorage.DatasetStatus(session.Username),200);return true;}
            if(path=="/api/history/foundation/start"&&method=="POST"){Json(context.Response,historyStorage.StartDataset(session.Username),200);return true;}
            if(path=="/api/history/foundation/cancel"&&method=="POST"){historyStorage.CancelDataset(session.Username);Json(context.Response,new{ok=true},200);return true;}
            if(path=="/api/history/foundation/download"&&method=="GET")
            {
                using(var input=historyStorage.OpenDataset(session.Username,Query(context.Request,"id")))
                {
                    context.Response.ContentType="application/zip";context.Response.ContentLength64=input.Length;
                    context.Response.Headers["Content-Disposition"]="attachment; filename=telemetry-dataset.zip";
                    input.CopyTo(context.Response.OutputStream,65536);
                }
                return true;
            }
            return false;
        }
    }
}
