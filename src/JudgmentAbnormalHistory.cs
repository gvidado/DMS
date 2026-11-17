using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class JudgmentAbnormalEntry
    {
        public string Kind="",Time="",Code="",Name="",ValueText="",EngineeringValue="",Result="",Script="",Detail="",Level="";
        public string RuleId="",RunId="",ParameterId="",SourceIP="",Packet="",Mode="",Reason="",Username="",Actor="";
        public long Id; public int Sequence=-1;
    }
    // Event and individual release entries share one append-only file, without a database.
    public sealed partial class JudgmentAbnormalJournal : IDisposable
    {
        private readonly object gate=new object();
        private readonly string path;
        private readonly FileStream file;
        private readonly List<long> offsets=new List<long>();
        private readonly SortedSet<long> pending=new SortedSet<long>(),released=new SortedSet<long>();
        private readonly Dictionary<long,JudgmentAbnormalEntry> releases=new Dictionary<long,JudgmentAbnormalEntry>();
        private readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
        private long revision;
        public string RecoveryMessage="";
        public JudgmentAbnormalJournal(string filename)
        {
            path=filename;Directory.CreateDirectory(Path.GetDirectoryName(path));
            file=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,65536);
            try{Load();}catch{file.Dispose();throw;}
        }
        private JudgmentAbnormalEntry Decode(byte[] bytes)
        {string text=utf8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\r');return String.IsNullOrWhiteSpace(text)?null:JsonFile.Decode<JudgmentAbnormalEntry>(text);}
        private void Index(JudgmentAbnormalEntry entry,long offset)
        {
            if(entry==null)return;
            if(entry.Kind=="abnormal" && entry.Id==offsets.Count+1 && !String.IsNullOrEmpty(entry.Code))
            {offsets.Add(offset);pending.Add(entry.Id);}
            else if(entry.Kind=="release" && entry.Id>0 && entry.Id<=offsets.Count && !releases.ContainsKey(entry.Id) && !String.IsNullOrWhiteSpace(entry.Reason))
            {releases.Add(entry.Id,entry);pending.Remove(entry.Id);released.Add(entry.Id);}
            else throw new ArgumentException("判读异常历史格式或顺序异常，请保留data/judgment-abnormal-history.jsonl排查，原文件未修改");
            revision++;
        }
        private void Load()
        {
            byte[] buffer=new byte[65536];long position=0,start=0;
            using(var line=new MemoryStream())
            {
                int read;
                while((read=file.Read(buffer,0,buffer.Length))>0)
                    for(int i=0;i<read;i++)
                    {
                        position++;
                        if(buffer[i]==10){Index(Decode(line.ToArray()),start);line.SetLength(0);start=position;}
                        else line.WriteByte(buffer[i]);
                    }
                if(line.Length>0)
                {
                    JudgmentAbnormalEntry tail=null;bool complete=false;
                    try{tail=Decode(line.ToArray());complete=tail!=null;}catch(ArgumentException){}catch(InvalidOperationException){}
                    if(complete){Index(tail,start);file.Position=file.Length;file.WriteByte(10);file.Flush(true);}
                    else
                    {
                        string backup=path+".recovered-"+BeijingTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".bak";
                        file.Position=0;using(var saved=new FileStream(backup,FileMode.CreateNew,FileAccess.Write,FileShare.Read))file.CopyTo(saved);
                        file.SetLength(start);file.Position=start;file.Flush(true);
                        RecoveryMessage="判读异常历史末尾存在未写完的内容，已备份为"+Path.GetFileName(backup)+"，完整记录已保留";
                    }
                }
            }
            file.Position=file.Length;
        }
        private void Write(JudgmentAbnormalEntry entry)
        {
            byte[] bytes=utf8.GetBytes(JsonFile.Encode(entry)+"\n");long start=file.Position;
            try{file.Write(bytes,0,bytes.Length);file.Flush(true);}catch{file.SetLength(start);file.Position=start;throw;}
            Index(entry,start);
        }
        public long Append(JudgmentAbnormalEntry entry)
        {lock(gate){entry.Kind="abnormal";entry.Id=offsets.Count+1;Write(entry);return entry.Id;}}
        public void Release(long id,string reason,string username,string actor)
        {
            reason=(reason??"").Trim();if(reason.Length==0 || reason.Length>2000)throw new ArgumentException("请填写解除原因，长度为1～2000个字符");
            lock(gate)
            {
                if(id<1 || id>offsets.Count)throw new ArgumentException("判读异常记录不存在");
                if(releases.ContainsKey(id))throw new InvalidOperationException("该异常已由其他操作解除，请刷新查看；原解除原因保留");
                Write(new JudgmentAbnormalEntry{Kind="release",Id=id,Reason=reason,Username=username,Actor=actor,Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")});
            }
        }
        private FileStream OpenRead(){return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite,65536);}
        private JudgmentAbnormalEntry ReadAt(FileStream reader,long offset)
        {
            reader.Position=offset;using(var bytes=new MemoryStream()){int next;while((next=reader.ReadByte())>=0 && next!=10)bytes.WriteByte((byte)next);return Decode(bytes.ToArray());}
        }
        private static object Row(JudgmentAbnormalEntry entry,JudgmentAbnormalEntry release)
        {return new{id=entry.Id,time=entry.Time,code=entry.Code,name=entry.Name,value=entry.ValueText,engineeringValue=entry.EngineeringValue,result=entry.Result,script=entry.Script,detail=entry.Detail,
            ruleId=entry.RuleId,runId=entry.RunId,parameterId=entry.ParameterId,sourceIP=entry.SourceIP,packet=entry.Packet,mode=entry.Mode,sequence=entry.Sequence,
            released=release!=null,reason=release==null?"":release.Reason,releasedAt=release==null?"":release.Time,releasedBy=release==null?"":release.Actor,releasedUsername=release==null?"":release.Username};}
        public object Page(int page,int size,string status,long through,string storageError)
        {
            if(page<1 || size<1 || size>100 || through<0 || !new[]{"all","pending","released"}.Contains(status))throw new ArgumentException("判读异常记录分页或筛选无效");
            lock(gate)
            {
                long limit=through==0?offsets.Count:Math.Min(through,offsets.Count),matching=0;int pages;var ids=new List<long>();
                SortedSet<long> view=null;
                if(status=="all")matching=limit;
                else if(limit>0){view=(status=="pending"?pending:released).GetViewBetween(1,limit);matching=view.Count;}
                pages=(int)Math.Max(1,(matching+size-1)/size);page=Math.Min(page,pages);long skip=(long)(page-1)*size;
                if(status=="all"){for(long id=limit-skip;id>0 && ids.Count<size;id--)ids.Add(id);}
                else if(view!=null)ids.AddRange(view.Reverse().Skip((page-1)*size).Take(size));
                var rows=new List<object>();using(var reader=OpenRead())foreach(long id in ids){JudgmentAbnormalEntry release;releases.TryGetValue(id,out release);rows.Add(Row(ReadAt(reader,offsets[(int)id-1]),release));}
                return new{revision,total=offsets.Count,pending=pending.Count,released=released.Count,matching,throughId=limit,page,pages,pageSize=size,storageError,records=rows};
            }
        }
        public void Export(TextWriter writer)
        {
            long[] positions;Dictionary<long,JudgmentAbnormalEntry> confirmations;
            lock(gate){positions=offsets.ToArray();confirmations=new Dictionary<long,JudgmentAbnormalEntry>(releases);}
            writer.NewLine="\r\n";writer.WriteLine("参数代号,参数名称,异常值,是否解除,解除原因,判读规则");
            using(var reader=OpenRead())foreach(long position in positions)
            {
                var entry=ReadAt(reader,position);JudgmentAbnormalEntry release;confirmations.TryGetValue(entry.Id,out release);
                decimal number;string value=Decimal.TryParse(entry.ValueText,NumberStyles.Float,CultureInfo.InvariantCulture,out number)?"\""+entry.ValueText+"\"":Monitor.CsvCell(entry.ValueText);
                writer.WriteLine(String.Join(",",new[]{Monitor.CsvCell(entry.Code),Monitor.CsvCell(entry.Name),value,Monitor.CsvCell(release==null?"否":"是"),Monitor.CsvCell(release==null?"":release.Reason),Monitor.CsvCell(entry.Script)}));
            }
        }
        public void Dispose(){lock(gate)file.Dispose();}
    }
    public sealed partial class Monitor
    {
        private JudgmentAbnormalJournal judgmentAbnormals;
        private string judgmentAbnormalStorageError="";
        private void OpenJudgmentAbnormalHistory()
        {
            try{judgmentAbnormals=new JudgmentAbnormalJournal(Path.Combine(root,"data","judgment-abnormal-history.jsonl"));if(judgmentAbnormals.RecoveryMessage.Length>0)Note("warn",judgmentAbnormals.RecoveryMessage);}
            catch(Exception ex){judgmentAbnormalStorageError="判读异常历史不可用，原文件保留，请检查后重启："+ex.Message;Note("error",judgmentAbnormalStorageError);}
        }
        private bool CaptureJudgmentAbnormal(ContinuousRule rule,RuleBinding binding,RuleRunState state,string result,string value,string detail)
        {
            try
            {
                if(judgmentArchives==null)throw new IOException(judgmentArchiveError);
                if(state.AbnormalMarker==result)
                {if(state.ArchivedIncident!=null)judgmentArchives.RecordAbnormal(state.ArchivedIncident,BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));return true;}
                var h=RuleHistory(binding.Main,binding.Source);
                var entry=new JudgmentAbnormalEntry{Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Code=rule.Code,Name=rule.Name,Level=rule.Level,ValueText=value??"",EngineeringValue=state.ValueText,
                    Result=result,Script=rule.Script,Detail=detail,RuleId=rule.Id,RunId=judgmentRunId,ParameterId=binding.Main.Id,SourceIP=binding.Source,Packet=binding.Main.PacketName??"",Mode=AlarmMode(),Sequence=h==null || h.Latest==null?-1:h.Latest.Sequence};
                judgmentAbnormals.Append(entry);state.ArchivedIncident=entry;judgmentArchives.RecordAbnormal(entry,entry.Time);
                state.AbnormalMarker=result;return true;
            }
            catch(Exception ex)
            {
                judgmentAbnormalStorageError="判读异常记录保存失败，判读已停止，请检查目录权限和磁盘空间："+ex.Message;
                state.Status="执行异常";state.Result="";state.Detail=judgmentAbnormalStorageError;StopJudgmentCore(judgmentAbnormalStorageError);RecordAlarm("error","判读异常记录保存失败",judgmentAbnormalStorageError);return false;
            }
        }
        public object JudgmentAbnormalPage(int page,int size,string status,long through)
        {
            if(judgmentAbnormals==null)return new{total=0,pending=0,released=0,matching=0,throughId=0,page=1,pages=1,pageSize=size,storageError=judgmentAbnormalStorageError,records=new object[0]};
            return judgmentAbnormals.Page(page,size,status,through,judgmentAbnormalStorageError);
        }
        public void ReleaseJudgmentAbnormal(long id,string reason,string username,string actor)
        {
            ReleaseUnifiedAbnormal("judge:"+id,reason,username,actor);
        }
        public void ExportJudgmentAbnormal(TextWriter writer)
        {if(judgmentAbnormals==null)throw new InvalidOperationException(judgmentAbnormalStorageError);judgmentAbnormals.Export(writer);}
    }
}
