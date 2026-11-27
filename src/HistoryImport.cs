using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class HistoryImportJob
    {
        public string Id=Guid.NewGuid().ToString("N"),ConfigurationId=Guid.NewGuid().ToString("N"),Filename="",File="",Sha256="",ConfigurationHash="",Experiment="",Work="",State="uploading",Error="",StartedAt=HistoryText.Utc(DateTime.UtcNow),FinishedAt="",FirstSourceTime="",LastSourceTime="";
        public long Bytes,Frames,Delivered,Accepted,Invalid,Duplicates;
    }
    public sealed partial class Server
    {
        private readonly object historyImportGate=new object();
        private HistoryImportJob historyImport;
        private readonly ManualResetEvent historyImportCancel=new ManualResetEvent(false);
        private Thread historyImportThread;
        private string HistoryImportDirectory {get{return Path.Combine(root,"data","history-state","imports");}}
        private void RecoverHistoryImport()
        {
            if(!Directory.Exists(HistoryImportDirectory))return;string path=Directory.GetFiles(HistoryImportDirectory,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();if(path==null)return;
            historyImport=JsonFile.Load<HistoryImportJob>(path);if(new[]{"uploading","reading"}.Contains(historyImport.State)){historyImport.State="interrupted";historyImport.Error="上次进程退出时导入未完成；原始文件保留，可以重试。已提交报文按原ID去重。";SaveHistoryImport(historyImport);}
        }
        private void SaveHistoryImport(HistoryImportJob job)
        {Directory.CreateDirectory(HistoryImportDirectory);JsonFile.Save(Path.Combine(HistoryImportDirectory,job.Id+".json"),job);}
        private object HistoryImportStatus()
        {lock(historyImportGate)return new{job=historyImport==null?null:HistoryJson.Decode<HistoryImportJob>(HistoryJson.Encode(historyImport))};}
        private bool HistoryImportBusy {get{return historyImport!=null&&new[]{"uploading","reading"}.Contains(historyImport.State);}}
        private object UploadHistoryImport(HttpListenerRequest request)
        {
            if(!historyStorage.RecordingEnabled)throw new InvalidOperationException("请先启用历史采集与归档");
            string filename=Path.GetFileName(Query(request,"filename"));if(!filename.EndsWith(".pcapng",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请选择pcapng文件");
            HistoryImportJob job;lock(historyImportGate){if(HistoryImportBusy)throw new InvalidOperationException("已有历史文件正在上传或导入，请等待完成或取消");historyImportCancel.Reset();job=historyImport=new HistoryImportJob{Filename=filename,Experiment=historyStorage.EnsureExperiment()};}
            try
            {
                string folder=Path.Combine(historyStorage.ArchiveDirectory,"source-files",DateTime.UtcNow.ToString("yyyy-MM-dd"));Directory.CreateDirectory(folder);job.File=Path.Combine(folder,job.Id+".pcapng");job.Work=Path.Combine(HistoryImportDirectory,job.Id+"-work");
                liveMonitor.ExportHistoryImportConfiguration(job.Work);var files=Directory.GetFiles(Path.Combine(job.Work,"config"),"*.json").OrderBy(p=>p,StringComparer.Ordinal).Select(p=>new{name=Path.GetFileName(p),content=File.ReadAllText(p,Encoding.UTF8)}).ToArray();
                // This suffix identifies decoding semantics. Performance-only releases must
                // keep the same import identity so retrying a capture cannot duplicate it.
                job.ConfigurationHash=HistoryText.Hash(HistoryJson.Encode(files)+"/0.10.4");
                SaveHistoryImport(job);
                using(var sha=SHA256.Create())using(var output=new FileStream(job.File+".partial",FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                {
                    byte[] buffer=new byte[1024*1024];int count;while((count=request.InputStream.Read(buffer,0,buffer.Length))>0)
                    {if(historyImportCancel.WaitOne(0))throw new OperationCanceledException();output.Write(buffer,0,count);sha.TransformBlock(buffer,0,count,null,0);Interlocked.Add(ref job.Bytes,count);}
                    sha.TransformFinalBlock(new byte[0],0,0);output.Flush(true);job.Sha256=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                }
                if(job.Bytes==0)throw new ArgumentException("文件内容为空");File.Move(job.File+".partial",job.File);
                foreach(string previous in Directory.GetFiles(HistoryImportDirectory,"*.json"))
                {
                    var old=JsonFile.Load<HistoryImportJob>(previous);if(old.Id!=job.Id&&old.State=="completed"&&old.Sha256==job.Sha256&&old.ConfigurationHash==job.ConfigurationHash)
                    {job.State="duplicate";job.Error="该文件已按相同配置导入，原导入编号："+old.Id;job.FinishedAt=HistoryText.Utc(DateTime.UtcNow);SaveHistoryImport(job);return HistoryImportStatus();}
                }
                StartHistoryImport(job);return HistoryImportStatus();
            }
            catch(Exception ex)
            {lock(historyImportGate){job.State=ex is OperationCanceledException?"cancelled":"failed";job.Error=ex.Message;job.FinishedAt=HistoryText.Utc(DateTime.UtcNow);SaveHistoryImport(job);}throw;}
        }
        private void StartHistoryImport(HistoryImportJob job)
        {
            lock(historyImportGate){job.State="reading";job.Error="";job.Frames=job.Delivered=job.Accepted=job.Invalid=job.Duplicates=0;SaveHistoryImport(job);historyImportThread=new Thread(()=>RunHistoryImport(job)){IsBackground=true,Name="Historical pcapng import"};historyImportThread.Start();}
        }
        private void RunHistoryImport(HistoryImportJob job)
        {
            Monitor importer=null;
            try
            {
                importer=new Monitor(job.Work);importer.BeginHistoryImport(historyStorage,job.Id,job.ConfigurationId,job.File,job.Sha256,job.Experiment);
                CaptureReader.ReadPcapng(job.File,(frame,position)=>
                {
                    if(historyImportCancel.WaitOne(0))throw new OperationCanceledException();historyStorage.WaitForImportWindow(()=>historyImportCancel.WaitOne(0));importer.ImportHistoryFrame(frame);
                    lock(historyImportGate){job.Frames++;if(frame.TimestampTicks>=0){string sourceTime=HistoryText.Utc(new DateTime(frame.TimestampTicks,DateTimeKind.Utc));if(job.FirstSourceTime.Length==0||String.CompareOrdinal(sourceTime,job.FirstSourceTime)<0)job.FirstSourceTime=sourceTime;if(job.LastSourceTime.Length==0||String.CompareOrdinal(sourceTime,job.LastSourceTime)>0)job.LastSourceTime=sourceTime;}if(frame.Udp&&frame.Payload!=null)job.Delivered++;job.Accepted=importer.Accepted;job.Invalid=importer.Invalid;job.Duplicates=importer.Duplicates;}
                },()=>historyImportCancel.WaitOne(0));
                if(historyImportCancel.WaitOne(0))throw new OperationCanceledException();
                importer.EndHistoryImport("历史文件导入完成");lock(historyImportGate){job.State="completed";job.FinishedAt=HistoryText.Utc(DateTime.UtcNow);SaveHistoryImport(job);}
            }
            catch(Exception ex)
            {if(importer!=null)try{importer.EndHistoryImport("历史文件导入中断："+ex.Message);}catch{}lock(historyImportGate){job.State=ex is OperationCanceledException?"cancelled":"failed";job.Error=ex.Message;job.FinishedAt=HistoryText.Utc(DateTime.UtcNow);SaveHistoryImport(job);}}
            finally{if(importer!=null)importer.Dispose();}
        }
        private bool HandleHistoryImport(HttpListenerContext context,Session session)
        {
            string path=context.Request.Url.AbsolutePath,method=context.Request.HttpMethod;
            if(path=="/api/history/import/status"&&method=="GET"){Json(context.Response,HistoryImportStatus(),200);return true;}
            if(path=="/api/history/import"&&method=="POST"){Json(context.Response,UploadHistoryImport(context.Request),200);return true;}
            if(path=="/api/history/import/cancel"&&method=="POST"){historyImportCancel.Set();Json(context.Response,new{ok=true},200);return true;}
            if(path=="/api/history/import/retry"&&method=="POST")
            {
                if(!historyStorage.RecordingEnabled)throw new InvalidOperationException("请先启用历史采集与归档");
                var body=Body(context.Request);Guid id;if(!Guid.TryParse(Text(body,"id"),out id))throw new ArgumentException("导入编号无效");lock(historyImportGate){if(HistoryImportBusy)throw new InvalidOperationException("已有导入任务正在运行");var job=JsonFile.Load<HistoryImportJob>(Path.Combine(HistoryImportDirectory,id.ToString("N")+".json"));if(!File.Exists(job.File))throw new ArgumentException("原始导入文件尚未完整上传，请重新选择文件");historyImportCancel.Reset();historyImport=job;StartHistoryImport(job);}Json(context.Response,HistoryImportStatus(),200);return true;
            }
            return false;
        }
        private void DisposeHistoryImport()
        {historyImportCancel.Set();if(historyImportThread!=null)historyImportThread.Join();}
    }
}
