using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TelemetryMonitor
{
    public sealed class DailyJudgmentRecord
    {
        public string Key="",Kind="",Time="",ObservedAt="",Code="",Name="",Level="",ParameterId="",SourceIP="",RuleId="",RunId="",Result="",Value="",EngineeringValue="",Rule="",Detail="",Mode="";
        public long IncidentId;
    }
    public sealed class DailyJudgmentArchive
    {public int SchemaVersion=1;public string Date="";public List<DailyJudgmentRecord> Records=new List<DailyJudgmentRecord>();}
    public sealed class JudgmentArchiveSummary
    {
        public string id="",type="",date="",name="",status="",startedAt="",endedAt="",runId="";
        public int normal,abnormal,variables,total;
        internal string Path;
    }
    internal sealed class ArchivedSequence
    {internal JudgmentArchiveSummary Summary;internal readonly List<string> Hashes=new List<string>();}

    // A compact atomic JSON per calendar day, and one append-only file per sequence run.
    internal sealed class JudgmentArchiveStore
    {
        private readonly object gate=new object();private readonly string directory;
        private readonly Dictionary<string,JudgmentArchiveSummary> index=new Dictionary<string,JudgmentArchiveSummary>();
        private readonly Dictionary<string,ArchivedSequence> sequences=new Dictionary<string,ArchivedSequence>();
        private DailyJudgmentArchive daily;private bool dirty;
        private readonly HashSet<string> dailyVariables=new HashSet<string>(),dailyIncidents=new HashSet<string>();
        internal readonly List<string> RecoveryMessages=new List<string>();
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128};}
        private static string Encode(object value){return Serializer().Serialize(value);}
        private static T Decode<T>(string value){return Serializer().Deserialize<T>(value);}
        internal static string DateText(string value)
        {
            DateTime date;if(!DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date))throw new ArgumentException("日期须为YYYY-MM-DD");return value;
        }
        private static string Day(string value){return DateText((value??"").Length>=10?value.Substring(0,10):value);}
        private static string VariableKey(string parameter,string code,string source){return Encode(new[]{String.IsNullOrEmpty(parameter)?code:parameter,source??""});}
        private static string Fingerprint(SequenceHistoryEvent entry)
        {return CanonicalJson.Hash(entry);}
        internal JudgmentArchiveStore(string root)
        {
            directory=Path.Combine(root,"data","archives");Directory.CreateDirectory(Path.Combine(directory,"continuous"));Directory.CreateDirectory(Path.Combine(directory,"sequences"));
            foreach(string path in Directory.GetFiles(Path.Combine(directory,"continuous"),"*.json"))
            {string date=DateText(Path.GetFileNameWithoutExtension(path));var value=ReadDaily(path,date);index["continuous:"+date]=DailySummary(value,path);}
            foreach(string folder in Directory.GetDirectories(Path.Combine(directory,"sequences")))
            {
                string date=DateText(Path.GetFileName(folder));foreach(string path in Directory.GetFiles(folder,"*.jsonl"))
                {
                    string run=Path.GetFileNameWithoutExtension(path);if(!Regex.IsMatch(run,@"^[a-f0-9]{32}$"))throw new InvalidDataException("序列存档运行编号无效："+path);
                    var state=new ArchivedSequence{Summary=new JudgmentArchiveSummary{id="sequence:"+date+":"+run,date=date,runId=run,Path=path}};
                    foreach(var entry in ReadSequenceFile(path,true))
                    {if(entry.RunId!=run)throw new InvalidDataException("序列存档运行编号不一致："+path);UpdateSequence(state,entry);state.Hashes.Add(Fingerprint(entry));}
                    if(sequences.ContainsKey(run))throw new InvalidDataException("序列存档存在重复运行编号，原文件保留："+run+"；"+sequences[run].Summary.Path+"；"+path);
                    sequences.Add(run,state);if(state.Hashes.Count>0)index[state.Summary.id]=state.Summary;
                }
            }
        }
        private string DailyPath(string date){return Path.Combine(directory,"continuous",date+".json");}
        private static DailyJudgmentArchive ReadDaily(string path,string date)
        {
            var value=Decode<DailyJudgmentArchive>(File.ReadAllText(path,Encoding.UTF8));
            if(value==null || value.SchemaVersion!=1 || value.Date!=date || value.Records==null || value.Records.Any(r=>r==null || String.IsNullOrEmpty(r.Key) || (r.Kind!="normal" && r.Kind!="abnormal") || (r.Kind=="normal" && (r.IncidentId!=0 || r.Result!="Normal")) || (r.Kind=="abnormal" && (r.IncidentId<1 || String.IsNullOrEmpty(r.Result) || r.Result=="Normal")) || String.IsNullOrEmpty(r.Time) || String.IsNullOrEmpty(r.ObservedAt) || Day(r.ObservedAt)!=date))throw new InvalidDataException("常态化判读存档格式异常，原文件保留："+path);
            if(value.Records.GroupBy(r=>r.Key).Any(g=>g.Count(r=>r.Kind=="normal")>1 || (g.Any(r=>r.Kind=="normal") && g.Any(r=>r.Kind=="abnormal"))) || value.Records.Where(r=>r.Kind=="abnormal").GroupBy(r=>r.Key+"/"+r.IncidentId).Any(g=>g.Count()>1))throw new InvalidDataException("常态化判读存档含重复或矛盾的汇总："+path);
            return value;
        }
        private static JudgmentArchiveSummary DailySummary(DailyJudgmentArchive value,string path)
        {
            return new JudgmentArchiveSummary{id="continuous:"+value.Date,type="continuous",date=value.Date,name="常态化判读 "+value.Date,status=value.Records.Any(r=>r.Kind=="abnormal")?"含异常":"通过",normal=value.Records.Count(r=>r.Kind=="normal"),abnormal=value.Records.Count(r=>r.Kind=="abnormal"),variables=value.Records.Select(r=>r.Key).Distinct().Count(),total=value.Records.Count,startedAt=value.Records.Select(r=>r.ObservedAt).OrderBy(v=>v,StringComparer.Ordinal).FirstOrDefault()??"",endedAt=value.Records.Select(r=>r.ObservedAt).OrderByDescending(v=>v,StringComparer.Ordinal).FirstOrDefault()??"",Path=path};
        }
        private void SelectDay(string date)
        {
            if(daily!=null && daily.Date==date)return;DateText(date);FlushDaily();string path=DailyPath(date);daily=File.Exists(path)?ReadDaily(path,date):new DailyJudgmentArchive{Date=date};
            dailyVariables.Clear();dailyIncidents.Clear();foreach(var record in daily.Records){dailyVariables.Add(record.Key);if(record.IncidentId>0)dailyIncidents.Add(record.Key+"/"+record.IncidentId);}dirty=false;
        }
        internal void RecordNormal(ContinuousRule rule,RuleBinding binding,string runId,string time,string mode)
        {
            lock(gate)
            {
                SelectDay(Day(time));string key=binding.ArchiveKey??(binding.ArchiveKey=VariableKey(binding.Main.Id,rule.Code,binding.Source));if(dailyVariables.Contains(key))return;
                daily.Records.Add(new DailyJudgmentRecord{Key=key,Kind="normal",Time=time,ObservedAt=time,Code=rule.Code,Name=rule.Name,Level=rule.Level,ParameterId=binding.Main.Id,SourceIP=binding.Source,RuleId=rule.Id,RunId=runId,Result="Normal",Rule=rule.Script,Mode=mode});dailyVariables.Add(key);dirty=true;
            }
        }
        internal void RecordAbnormal(JudgmentAbnormalEntry entry,string observedAt)
        {
            lock(gate)
            {
                SelectDay(Day(observedAt));string key=VariableKey(entry.ParameterId,entry.Code,entry.SourceIP),incident=key+"/"+entry.Id;if(dailyIncidents.Contains(incident))return;
                daily.Records.RemoveAll(r=>r.Key==key && r.Kind=="normal");daily.Records.Add(new DailyJudgmentRecord{Key=key,Kind="abnormal",Time=entry.Time,ObservedAt=observedAt,Code=entry.Code,Name=entry.Name,Level=entry.Level??"",ParameterId=entry.ParameterId,SourceIP=entry.SourceIP,RuleId=entry.RuleId,RunId=entry.RunId,Result=entry.Result,Value=entry.ValueText,EngineeringValue=entry.EngineeringValue,Rule=entry.Script,Detail=entry.Detail,Mode=entry.Mode,IncidentId=entry.Id});dailyVariables.Add(key);dailyIncidents.Add(incident);dirty=true;
            }
        }
        internal void FlushDaily()
        {
            lock(gate)
            {
                if(!dirty || daily==null)return;string path=DailyPath(daily.Date),temporary=path+".tmp";
                try
                {
                    byte[] bytes=new UTF8Encoding(false).GetBytes(Encode(daily));using(var file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                    if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
                    index["continuous:"+daily.Date]=DailySummary(daily,path);dirty=false;
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
        }
        private List<SequenceHistoryEvent> ReadSequenceFile(string path,bool recover)
        {
            var rows=new List<SequenceHistoryEvent>();byte[] data=File.ReadAllBytes(path);int start=0;var utf8=new UTF8Encoding(false,true);
            for(int at=0;at<data.Length;at++)if(data[at]==10)
            {string line=utf8.GetString(data,start,at-start).TrimStart('\uFEFF').TrimEnd('\r');if(!String.IsNullOrWhiteSpace(line))rows.Add(Decode<SequenceHistoryEvent>(line));start=at+1;}
            if(start<data.Length)
            {
                SequenceHistoryEvent tail=null;try{tail=Decode<SequenceHistoryEvent>(utf8.GetString(data,start,data.Length-start).TrimEnd('\r'));}catch(ArgumentException){}catch(InvalidOperationException){}
                if(!recover)throw new InvalidDataException("序列存档末条未写完，请重启恢复："+path);
                if(tail!=null)
                {
                    if(tail.RunId!=Path.GetFileNameWithoutExtension(path) || String.IsNullOrEmpty(tail.Event) || (tail.Mode!="execute" && tail.Mode!="judge"))throw new InvalidDataException("序列存档末条格式异常，原文件保留："+path);
                    rows.Add(tail);using(var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)){file.WriteByte(10);file.Flush(true);}
                }
                else{File.Copy(path,path+".recovered-"+Guid.NewGuid().ToString("N")+".bak");using(var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Read)){file.SetLength(start);file.Flush(true);}RecoveryMessages.Add("序列存档末条中断，已备份并从原始历史恢复："+Path.GetFileName(path));}
            }
            if(rows.Any(e=>e==null || String.IsNullOrEmpty(e.RunId) || String.IsNullOrEmpty(e.Event) || (e.Mode!="execute" && e.Mode!="judge")))throw new InvalidDataException("序列存档格式异常，原文件保留："+path);return rows;
        }
        private static void UpdateSequence(ArchivedSequence state,SequenceHistoryEvent entry)
        {
            var summary=state.Summary;summary.type=entry.Mode=="execute"?"sequence-execute":"sequence-judge";summary.name=entry.Sequence;
            if(summary.startedAt.Length==0)summary.startedAt=entry.Time;
            if(entry.Event=="结束"){summary.status=entry.Status;summary.endedAt=entry.Time;}
            else if(summary.endedAt.Length==0)summary.status="运行中";
            if(entry.Event=="步骤结果" && entry.Status=="通过")summary.normal++;if(entry.Event=="异常")summary.abnormal++;summary.total++;
        }
        private void AppendSequenceCore(SequenceHistoryEvent entry)
        {
            if(!Regex.IsMatch(entry.RunId??"",@"^[a-f0-9]{32}$"))throw new InvalidDataException("序列存档运行编号无效");ArchivedSequence state;
            if(!sequences.TryGetValue(entry.RunId,out state))
            {
                string date=Day(entry.Time),folder=Path.Combine(directory,"sequences",date);Directory.CreateDirectory(folder);
                state=new ArchivedSequence{Summary=new JudgmentArchiveSummary{id="sequence:"+date+":"+entry.RunId,date=date,runId=entry.RunId,Path=Path.Combine(folder,entry.RunId+".jsonl")}};
                if(File.Exists(state.Summary.Path))throw new InvalidDataException("序列存档文件已存在但未正确载入，原文件保留");
                sequences.Add(entry.RunId,state);
            }
            byte[] bytes=new UTF8Encoding(false).GetBytes(Encode(entry)+"\n");using(var file=new FileStream(state.Summary.Path,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read))
            {long length=file.Length;file.Position=length;try{file.Write(bytes,0,bytes.Length);file.Flush(true);}catch{file.SetLength(length);file.Flush(true);throw;}}
            state.Hashes.Add(Fingerprint(entry));UpdateSequence(state,entry);index[state.Summary.id]=state.Summary;
        }
        internal void AppendSequence(SequenceHistoryEvent entry){lock(gate)AppendSequenceCore(entry);}
        internal void Reconcile(IEnumerable<JudgmentAbnormalEntry> incidents,IEnumerable<SequenceHistoryEvent> events)
        {
            lock(gate)
            {
                foreach(var entry in incidents)RecordAbnormal(entry,entry.Time);FlushDaily();
                var consumed=new Dictionary<string,int>();foreach(var entry in events)
                {
                    int count;consumed.TryGetValue(entry.RunId,out count);ArchivedSequence state;
                    if(sequences.TryGetValue(entry.RunId,out state) && count<state.Hashes.Count)
                    {if(state.Hashes[count]!=Fingerprint(entry))throw new InvalidDataException("序列存档与原始历史不一致（第"+(count+1)+"条记录），原文件保留："+entry.RunId);}
                    else AppendSequenceCore(entry);consumed[entry.RunId]=count+1;
                }
                foreach(var run in consumed)
                    if(sequences[run.Key].Hashes.Count>run.Value)throw new InvalidDataException("序列存档与原始历史条数不一致，原文件保留："+run.Key+"；原始历史"+run.Value+"条，存档"+sequences[run.Key].Hashes.Count+"条");
            }
        }
        internal object List(int page,string from,string to,string name,string type)
        {
            if(page<1 || page>10000 || (name??"").Length>128 || !new[]{"all","continuous","sequence-execute","sequence-judge"}.Contains(type))throw new ArgumentException("存档筛选或页码无效");
            if(from.Length>0)DateText(from);if(to.Length>0)DateText(to);if(from.Length>0 && to.Length>0 && StringComparer.Ordinal.Compare(from,to)>0)throw new ArgumentException("开始日期不能晚于结束日期");
            lock(gate)
            {
                string search=(name??"").Trim();var filtered=index.Values.Where(e=>(from.Length==0 || StringComparer.Ordinal.Compare(e.date,from)>=0) && (to.Length==0 || StringComparer.Ordinal.Compare(e.date,to)<=0) && (type=="all" || e.type==type)).Where(e=>search.Length==0 || e.name.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0 || (e.type=="continuous" && ReadDaily(e.Path,e.date).Records.Any(r=>(r.Code+" "+r.Name).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0))).OrderByDescending(e=>e.date,StringComparer.Ordinal).ThenByDescending(e=>e.startedAt,StringComparer.Ordinal).ThenBy(e=>e.id,StringComparer.Ordinal).ToArray();int pages=Math.Max(1,(filtered.Length+49)/50);page=Math.Min(page,pages);
                return new{page,pages,total=filtered.Length,rows=filtered.Skip((page-1)*50).Take(50).ToArray()};
            }
        }
        private JudgmentArchiveSummary Find(string id)
        {JudgmentArchiveSummary value;if(!index.TryGetValue(id??"",out value))throw new ArgumentException("判读存档不存在，请刷新检索结果");return value;}
        internal Tuple<JudgmentArchiveSummary,DailyJudgmentRecord[],SequenceHistoryEvent[]> Read(string id)
        {lock(gate){var summary=Find(id);return Tuple.Create(Decode<JudgmentArchiveSummary>(Encode(summary)),summary.type=="continuous"?ReadDaily(summary.Path,summary.date).Records.ToArray():new DailyJudgmentRecord[0],summary.type=="continuous"?new SequenceHistoryEvent[0]:ReadSequenceFile(summary.Path,false).ToArray());}}
        internal SequenceHistoryEvent[] ReadSequenceRun(string runId)
        {lock(gate){ArchivedSequence run;if(!sequences.TryGetValue(runId,out run))throw new ArgumentException("本次运行存档不存在");return ReadSequenceFile(run.Summary.Path,false).ToArray();}}
        internal byte[] Download(string id){lock(gate)return File.ReadAllBytes(Find(id).Path);}
    }
    public sealed partial class JudgmentAbnormalJournal
    {
        internal IEnumerable<JudgmentAbnormalEntry> ArchiveEntries()
        {long[] positions;lock(gate)positions=offsets.ToArray();using(var reader=OpenRead())foreach(long at in positions)yield return ReadAt(reader,at);}
        internal Dictionary<long,JudgmentAbnormalEntry> ArchiveReleases(){lock(gate)return new Dictionary<long,JudgmentAbnormalEntry>(releases);}
    }
    public sealed partial class Monitor
    {
        private JudgmentArchiveStore judgmentArchives;private string judgmentArchiveError="";
        private void InitializeJudgmentArchives()
        {
            try
            {
                judgmentArchives=new JudgmentArchiveStore(root);judgmentArchives.Reconcile(judgmentAbnormals==null?Enumerable.Empty<JudgmentAbnormalEntry>():judgmentAbnormals.ArchiveEntries(),ReadSequenceHistory());
                foreach(string message in judgmentArchives.RecoveryMessages)Note("warn",message);
            }
            catch(Exception ex){judgmentArchives=null;judgmentArchiveError="判读存档不可用，原文件保留，请检查data/archives后重启："+ex.Message;Note("error",judgmentArchiveError);}
        }
        private bool ArchiveNormal(ContinuousRule rule,RuleBinding binding,RuleRunState state)
        {try{if(judgmentArchives==null)throw new IOException(judgmentArchiveError);judgmentArchives.RecordNormal(rule,binding,judgmentRunId,state.Time,AlarmMode());return true;}catch(Exception ex){ArchiveFailed(ex);return false;}}
        private void ArchiveFailed(Exception ex)
        {judgmentArchiveError="判读存档保存失败，判读已停止，请检查磁盘空间和目录权限："+ex.Message;StopJudgmentCore(judgmentArchiveError);RecordAlarm("error","判读存档异常",judgmentArchiveError);}
        private void FlushJudgmentArchive(){try{if(judgmentArchives!=null)judgmentArchives.FlushDaily();}catch(Exception ex){ArchiveFailed(ex);}}
        internal object ArchiveList(int page,string from,string to,string name,string type)
        {return new{error=judgmentArchiveError,history=judgmentArchives==null?new{page=1,pages=1,total=0,rows=new object[0]}:(object)judgmentArchives.List(page,from,to,name,type)};}
        private object ArchiveRow(DailyJudgmentRecord row,Dictionary<long,JudgmentAbnormalEntry> releases)
        {
            JudgmentAbnormalEntry release=null;if(row.IncidentId>0)releases.TryGetValue(row.IncidentId,out release);
            return new{kind=row.Kind,time=row.Time,observedAt=row.ObservedAt,code=row.Code,name=row.Name,level=row.Level,source=row.SourceIP,result=row.Result,value=row.Value,engineeringValue=row.EngineeringValue,rule=row.Rule,detail=row.Detail,runId=row.RunId,incidentId=row.IncidentId,released=release!=null,reason=release==null?"":release.Reason,releasedAt=release==null?"":release.Time,releasedBy=release==null?"":release.Actor};
        }
        internal object ArchiveDetail(string id,int page)
        {
            if(page<1 || page>10000)throw new ArgumentException("存档明细页码无效");if(judgmentArchives==null)throw new InvalidOperationException(judgmentArchiveError);
            var data=judgmentArchives.Read(id);int total=data.Item1.type=="continuous"?data.Item2.Length:data.Item3.Length,pages=Math.Max(1,(total+49)/50);page=Math.Min(page,pages);
            var releases=judgmentAbnormals==null?new Dictionary<long,JudgmentAbnormalEntry>():judgmentAbnormals.ArchiveReleases();
            return new{summary=data.Item1,page,pages,total,records=data.Item2.Skip((page-1)*50).Take(50).Select(r=>ArchiveRow(r,releases)).ToArray(),events=data.Item3.Skip((page-1)*50).Take(50).ToArray(),definition=data.Item3.Where(e=>e.Definition!=null).Select(e=>e.Definition).FirstOrDefault(),error=judgmentArchiveError};
        }
        internal byte[] ArchiveDownload(string id){if(judgmentArchives==null)throw new InvalidOperationException(judgmentArchiveError);return judgmentArchives.Download(id);}
        internal void ExportJudgmentArchive(TextWriter writer,string id)
        {
            if(judgmentArchives==null)throw new InvalidOperationException(judgmentArchiveError);var data=judgmentArchives.Read(id);writer.NewLine="\r\n";
            if(data.Item1.type!="continuous"){WriteSequenceHistoryCsv(writer,data.Item3);return;}
            writer.WriteLine("存档日期,参数代号,参数名称,重要程度,判读结论,本日记录时间,异常起始时间,异常值,工程值,判读规则,来源IP,运行编号,异常记录号,是否解除,解除原因,解除时间,解除人");
            var releases=judgmentAbnormals==null?new Dictionary<long,JudgmentAbnormalEntry>():judgmentAbnormals.ArchiveReleases();foreach(var row in data.Item2)
            {
                JudgmentAbnormalEntry release=null;if(row.IncidentId>0)releases.TryGetValue(row.IncidentId,out release);
                var values=new[]{data.Item1.date,row.Code,row.Name,row.Level,row.Kind=="normal"?"通过":row.Result,row.ObservedAt,row.Kind=="normal"?"":row.Time,row.Value,row.EngineeringValue,row.Rule,row.SourceIP,row.RunId,row.IncidentId==0?"":"judge:"+row.IncidentId,row.Kind=="normal"?"":release==null?"否":"是",release==null?"":release.Reason,release==null?"":release.Time,release==null?"":release.Actor}.Select(CsvCell).ToArray();
                decimal number;foreach(int at in new[]{7,8}){string raw=at==7?row.Value:row.EngineeringValue;if(Decimal.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out number))values[at]="\""+raw+"\"";}writer.WriteLine(String.Join(",",values));
            }
        }
    }
}
