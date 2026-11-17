using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class UnifiedAbnormalRecord
    {
        public string key="",type="",typeName="",time="",code="",name="",value="",engineeringValue="",result="",rule="",detail="",source="",packet="",mode="",level="",category="",runId="";
        public long id,captureFrame; public int apid=-1,sequence=-1;
        public bool released,legacyConfirmed;
        public string reason="",releasedAt="",releasedBy="",releasedUsername="",legacyConfirmedAt="",legacyConfirmedBy="";
        internal readonly List<long> RelatedAlarms=new List<long>();
        internal UnifiedAbnormalSnapshot Origin;
        internal int SequenceRow;
        internal string SequenceIdentity="";
    }
    internal sealed class UnifiedAbnormalSnapshot
    {
        internal long Revision,Through;
        internal Func<IEnumerable<UnifiedAbnormalRecord>> Rows;
        internal Func<long,UnifiedAbnormalRecord> Read;
    }
    public sealed partial class JudgmentAbnormalJournal
    {
        internal long UnifiedRevision { get { lock(gate)return revision; } }
        private static UnifiedAbnormalRecord UnifiedRow(JudgmentAbnormalEntry e,Dictionary<long,JudgmentAbnormalEntry> confirmations)
        {
            JudgmentAbnormalEntry release;confirmations.TryGetValue(e.Id,out release);
            return new UnifiedAbnormalRecord{key="judge:"+e.Id,id=e.Id,type="continuous",typeName="常态化判读",time=e.Time,code=e.Code,name=e.Name,value=e.ValueText,engineeringValue=e.EngineeringValue,result=e.Result,rule=e.Script,detail=e.Detail,
                source=e.SourceIP,packet=e.Packet,mode=e.Mode,runId=e.RunId,sequence=e.Sequence,category="常态化判读",level=e.Result=="Unusual"?"warn":"error",released=release!=null,
                reason=release==null?"":release.Reason,releasedAt=release==null?"":release.Time,releasedBy=release==null?"":release.Actor,releasedUsername=release==null?"":release.Username};
        }
        internal UnifiedAbnormalSnapshot UnifiedSnapshot()
        {
            long[] positions;Dictionary<long,JudgmentAbnormalEntry> confirmations;long version;
            lock(gate){positions=offsets.ToArray();confirmations=new Dictionary<long,JudgmentAbnormalEntry>(releases);version=revision;}
            return new UnifiedAbnormalSnapshot{Revision=version,Through=positions.Length,Rows=()=>ReadUnified(positions,confirmations),Read=id=>{
                if(id<1 || id>positions.Length)throw new ArgumentException("异常判读记录不存在");
                using(var reader=OpenRead())return UnifiedRow(ReadAt(reader,positions[(int)id-1]),confirmations);
            }};
        }
        private IEnumerable<UnifiedAbnormalRecord> ReadUnified(long[] positions,Dictionary<long,JudgmentAbnormalEntry> confirmations)
        {using(var reader=OpenRead())foreach(long position in positions)yield return UnifiedRow(ReadAt(reader,position),confirmations);}
    }
    public sealed partial class AlarmJournal
    {
        internal long UnifiedRevision { get { lock(gate)return revision; } }
        private static string UnifiedType(string category)
        {
            if(category=="序列执行异常")return "sequence-execute";
            if(category=="序列判读异常")return "sequence-judge";
            if(new[]{"SPP异常","SPP超时","设备超时","重复帧","乱序/旧帧","计数重同步","疑似缺帧"}.Contains(category))return "spp";
            if(category=="常态化判读" || category=="判读规则异常")return "continuous";
            return "system";
        }
        private static UnifiedAbnormalRecord UnifiedRow(AlarmEntry e,List<AlarmAcknowledgement> confirmations)
        {
            var ack=Confirmation(e.Id,confirmations);string kind=UnifiedType(e.Category);
            string title=kind=="spp"?"SPP接收":kind=="sequence-execute"?"序列执行":kind=="sequence-judge"?"序列判读":kind=="continuous"?"常态化判读":"系统运行";
            return new UnifiedAbnormalRecord{key="alarm:"+e.Id,id=e.Id,type=kind,typeName=title,time=e.Time,code=e.ContextCode??(e.Apid<0?e.Category:"APID 0x"+e.Apid.ToString("X3")),
                name=e.ContextName??(!String.IsNullOrEmpty(e.Packet)?e.Packet:!String.IsNullOrEmpty(e.Source)?e.Source:e.Category),value=e.ContextValue??(e.Sequence<0?"":"帧计数 "+e.Sequence),result=e.Category,rule=e.ContextRule??e.Message,
                detail=e.Message,source=e.Source,packet=e.Packet,mode=e.Mode,runId=e.RunId??"",sequence=e.Sequence,apid=e.Apid,captureFrame=e.CaptureFrame,category=e.Category,level=e.Level,
                legacyConfirmed=ack!=null,legacyConfirmedAt=ack==null?"":ack.Time,legacyConfirmedBy=ack==null?"":ack.Actor};
        }
        internal UnifiedAbnormalSnapshot UnifiedSnapshot()
        {
            long[] positions;List<AlarmAcknowledgement> confirmations;long version;
            lock(gate){positions=offsets.ToArray();confirmations=acknowledgements.ToList();version=revision;}
            return new UnifiedAbnormalSnapshot{Revision=version,Through=positions.Length,Rows=()=>ReadUnified(positions,confirmations),Read=id=>{
                if(id<1 || id>positions.Length)throw new ArgumentException("异常判读记录不存在");
                using(var reader=OpenRead()){var entry=ReadAt(reader,positions[(int)id-1]);if(!AlarmPolicy.IsVisible(entry))throw new ArgumentException("该记录不属于异常记录");return UnifiedRow(entry,confirmations);}
            }};
        }
        private IEnumerable<UnifiedAbnormalRecord> ReadUnified(long[] positions,List<AlarmAcknowledgement> confirmations)
        {using(var reader=OpenRead())foreach(long position in positions){var entry=ReadAt(reader,position);if(AlarmPolicy.IsVisible(entry))yield return UnifiedRow(entry,confirmations);}}
    }
    public sealed class UnifiedAbnormalRelease
    {public string Key="",Reason="",Time="",Username="",Actor="";}
    internal sealed class UnifiedAbnormalReleases : IDisposable
    {
        private readonly object gate=new object();private readonly string path;private readonly FileStream file;
        private readonly Dictionary<string,UnifiedAbnormalRelease> entries=new Dictionary<string,UnifiedAbnormalRelease>(StringComparer.Ordinal);
        private readonly UTF8Encoding utf8=new UTF8Encoding(false,true);
        internal string RecoveryMessage="";
        internal UnifiedAbnormalReleases(string path)
        {
            this.path=path;Directory.CreateDirectory(Path.GetDirectoryName(path));file=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read,65536);
            try{Load();}catch{file.Dispose();throw;}
        }
        private UnifiedAbnormalRelease Decode(byte[] bytes)
        {return JsonFile.Decode<UnifiedAbnormalRelease>(utf8.GetString(bytes).TrimStart('\uFEFF').TrimEnd('\r'));}
        private void Index(UnifiedAbnormalRelease entry)
        {
            long id;var parts=(entry==null?"":entry.Key??"").Split(':');
            if(entry==null || parts.Length!=2 || !new[]{"alarm","sequence"}.Contains(parts[0]) || !Int64.TryParse(parts[1],out id) || id<1 || entry.Key!=parts[0]+":"+id || String.IsNullOrWhiteSpace(entry.Reason) || entry.Reason.Length>2000 || entries.ContainsKey(entry.Key))
                throw new InvalidDataException("统一异常解除历史格式异常，原文件保留，请检查data/abnormal-releases.jsonl");
            entries.Add(entry.Key,entry);
        }
        private void Load()
        {
            byte[] buffer=new byte[65536];long position=0,start=0;
            using(var line=new MemoryStream())
            {
                int read;while((read=file.Read(buffer,0,buffer.Length))>0)for(int i=0;i<read;i++)
                {position++;if(buffer[i]==10){if(line.Length>0)Index(Decode(line.ToArray()));line.SetLength(0);start=position;}else line.WriteByte(buffer[i]);}
                if(line.Length>0)
                {
                    UnifiedAbnormalRelease tail=null;try{tail=Decode(line.ToArray());}catch(ArgumentException){}catch(InvalidOperationException){}
                    if(tail!=null){Index(tail);file.Position=file.Length;file.WriteByte(10);file.Flush(true);}
                    else
                    {
                        string backup=path+".recovered-"+BeijingTime.Now.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N")+".bak";
                        file.Position=0;using(var copy=new FileStream(backup,FileMode.CreateNew,FileAccess.Write))file.CopyTo(copy);
                        file.SetLength(start);file.Position=start;file.Flush(true);RecoveryMessage="统一异常解除历史末条未写完，已保留备份并恢复完整记录";
                    }
                }
            }
            file.Position=file.Length;
        }
        internal long Revision {get{lock(gate)return entries.Count;}}
        internal Dictionary<string,UnifiedAbnormalRelease> Snapshot(){lock(gate)return new Dictionary<string,UnifiedAbnormalRelease>(entries,StringComparer.Ordinal);}
        internal void Release(string key,string reason,string username,string actor)
        {
            lock(gate)
            {
                if(entries.ContainsKey(key))throw new InvalidOperationException("该异常已解除，原解除原因保留，请刷新记录");
                var entry=new UnifiedAbnormalRelease{Key=key,Reason=reason,Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Username=username,Actor=actor};
                byte[] bytes=utf8.GetBytes(JsonFile.Encode(entry)+"\n");long start=file.Position;
                try{file.Write(bytes,0,bytes.Length);file.Flush(true);}catch{file.SetLength(start);file.Position=start;throw;}
                Index(entry);
            }
        }
        public void Dispose(){lock(gate)file.Dispose();}
    }
    internal sealed class UnifiedAbnormalPlan
    {
        internal string Revision="",Error="";
        internal long JudgmentThrough,AlarmThrough,SequenceThrough;
        internal readonly List<UnifiedAbnormalRecord> Rows=new List<UnifiedAbnormalRecord>();
        internal readonly Dictionary<string,UnifiedAbnormalRelease> Releases=new Dictionary<string,UnifiedAbnormalRelease>();
        internal readonly Dictionary<string,string> Aliases=new Dictionary<string,string>();
    }
    public sealed partial class Monitor
    {
        private UnifiedAbnormalReleases unifiedReleases;
        private string unifiedReleaseError="";
        private void OpenUnifiedAbnormalReleases()
        {
            try{unifiedReleases=new UnifiedAbnormalReleases(Path.Combine(root,"data","abnormal-releases.jsonl"));if(unifiedReleases.RecoveryMessage.Length>0)Note("warn",unifiedReleases.RecoveryMessage);}
            catch(Exception ex){unifiedReleaseError="统一异常解除历史不可用，原文件保留，请检查后重启："+ex.Message;}
        }
        private string UnifiedRevision()
        {return (judgmentAbnormals==null?0:judgmentAbnormals.UnifiedRevision)+"-"+alarms.UnifiedRevision+"-"+(unifiedReleases==null?0:unifiedReleases.Revision)+"-"+SequenceHistoryVersion();}
        private static string JudgmentFingerprint(UnifiedAbnormalRecord row,bool runtime)
        {return AlarmSource(row.source)+"\0 / "+(runtime?row.code+"："+row.detail:TelemetryReferences.Name(row.code)+" / "+row.name+"："+row.result+"，工程值="+row.engineeringValue);}
        private static long UnifiedTicks(string time)
        {DateTime value;return DateTime.TryParseExact(time,"yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture,DateTimeStyles.None,out value)?value.Ticks:0;}
        private static void ApplyRelease(UnifiedAbnormalRecord row,Dictionary<string,UnifiedAbnormalRelease> releases)
        {
            UnifiedAbnormalRelease release;if(!releases.TryGetValue(row.key,out release))return;
            row.released=true;row.reason=release.Reason;row.releasedAt=release.Time;row.releasedBy=release.Actor;row.releasedUsername=release.Username;
        }
        private UnifiedAbnormalPlan UnifiedPlan()
        {
            UnifiedAbnormalSnapshot judgment,alarm,sequence;Dictionary<string,UnifiedAbnormalRelease> releases;string error,sequenceError;
            lock(Gate)
            {
                judgment=judgmentAbnormals==null?null:judgmentAbnormals.UnifiedSnapshot();alarm=alarms.UnifiedSnapshot();
                sequence=SequenceAbnormalSnapshot(out sequenceError);
                releases=unifiedReleases==null?new Dictionary<string,UnifiedAbnormalRelease>():unifiedReleases.Snapshot();
                error=String.Join("；",new[]{judgmentAbnormalStorageError,alarmStorageError,unifiedReleaseError,sequenceError}.Where(e=>!String.IsNullOrEmpty(e)));
            }
            var plan=new UnifiedAbnormalPlan{JudgmentThrough=judgment==null?0:judgment.Through,AlarmThrough=alarm.Through,SequenceThrough=sequence.Through,Revision=(judgment==null?0:judgment.Revision)+"-"+alarm.Revision+"-"+releases.Count+"-"+sequence.Revision,Error=error};
            foreach(var release in releases)plan.Releases.Add(release.Key,release.Value);
            var fingerprints=new Dictionary<string,List<UnifiedAbnormalRecord>>(StringComparer.Ordinal);
            if(judgment!=null)foreach(var row in judgment.Rows())
            {
                row.Origin=judgment;
                // The alarm summary uses the original engineering value, which may differ from VAL.
                string fingerprint=JudgmentFingerprint(row,row.result=="执行异常");
                List<UnifiedAbnormalRecord> bucket;if(!fingerprints.TryGetValue(fingerprint,out bucket))fingerprints[fingerprint]=bucket=new List<UnifiedAbnormalRecord>();bucket.Add(row);
                row.rule="";plan.Rows.Add(row);
            }
            var sequenceRows=sequence.Rows().ToArray();
            foreach(var row in sequenceRows){row.Origin=sequence;ApplyRelease(row,releases);plan.Rows.Add(row);}
            var sequenceRuns=sequenceRows.GroupBy(r=>r.runId).ToDictionary(g=>g.Key,g=>g.ToArray());
            foreach(var row in alarm.Rows())
            {
                row.Origin=alarm;
                bool linked=false;
                if(row.category=="常态化判读" || row.category=="判读规则异常")
                {
                    int separator=(row.detail??"").IndexOf(" / ",StringComparison.Ordinal);List<UnifiedAbnormalRecord> candidates;
                    string key=separator<0?"":AlarmSource(row.source)+"\0"+row.detail.Substring(separator);
                    if(fingerprints.TryGetValue(key,out candidates))
                    {
                        long time=UnifiedTicks(row.time);var match=candidates.Where(r=>Math.Abs(UnifiedTicks(r.time)-time)<=TimeSpan.FromSeconds(2).Ticks).OrderBy(r=>Math.Abs(UnifiedTicks(r.time)-time)).FirstOrDefault();
                        if(match!=null){match.RelatedAlarms.Add(row.id);linked=true;}
                    }
                }
                if(row.type=="sequence-execute"||row.type=="sequence-judge")
                {
                    UnifiedAbnormalRecord[] candidates;
                    if(sequenceRuns.TryGetValue(row.runId,out candidates))
                    {
                        var match=candidates.Where(r=>r.type==row.type&&r.code==row.code&&r.detail==row.detail&&r.RelatedAlarms.Count==0&&Math.Abs(UnifiedTicks(r.time)-UnifiedTicks(row.time))<=TimeSpan.FromSeconds(2).Ticks).OrderBy(r=>Math.Abs(UnifiedTicks(r.time)-UnifiedTicks(row.time))).FirstOrDefault();
                        if(match!=null)
                        {
                            match.RelatedAlarms.Add(row.id);plan.Aliases[row.key]=match.key;match.source=row.source;
                            match.legacyConfirmed=row.legacyConfirmed;match.legacyConfirmedAt=row.legacyConfirmedAt;match.legacyConfirmedBy=row.legacyConfirmedBy;
                            UnifiedAbnormalRelease oldRelease;if(!plan.Releases.ContainsKey(match.key)&&releases.TryGetValue(row.key,out oldRelease))plan.Releases[match.key]=oldRelease;
                            ApplyRelease(match,plan.Releases);linked=true;
                        }
                    }
                }
                if(linked)continue;
                ApplyRelease(row,releases);row.rule="";plan.Rows.Add(row);
            }
            plan.Rows.Sort((a,b)=>{int order=StringComparer.Ordinal.Compare(b.time,a.time);if(order==0)order=StringComparer.Ordinal.Compare(a.key.Split(':')[0],b.key.Split(':')[0]);return order==0?b.id.CompareTo(a.id):order;});
            return plan;
        }
        private static UnifiedAbnormalRecord UnifiedDetail(UnifiedAbnormalRecord row,UnifiedAbnormalPlan plan)
        {var detail=row.Origin.Read(row.id);ApplyRelease(detail,plan.Releases);detail.legacyConfirmed=row.legacyConfirmed;detail.legacyConfirmedAt=row.legacyConfirmedAt;detail.legacyConfirmedBy=row.legacyConfirmedBy;if(String.IsNullOrEmpty(detail.source))detail.source=row.source;return detail;}
        public object UnifiedAbnormalPage(int page,int size,string type,string status,string search,long judgmentThrough,long alarmThrough,string knownRevision,string level="all",long sequenceThrough=0)
        {
            if(page<1 || page>10000 || size<1 || size>100 || judgmentThrough<0 || alarmThrough<0 || sequenceThrough<0 || !new[]{"all","continuous","sequence-execute","sequence-judge","spp","system"}.Contains(type) || !new[]{"all","pending","released"}.Contains(status) || !new[]{"all","warn","error"}.Contains(level) || (search??"").Length>240)throw new ArgumentException("统一异常记录筛选或分页无效");
            lock(Gate)if(!String.IsNullOrEmpty(knownRevision) && knownRevision==UnifiedRevision())return new{unchanged=true,revision=knownRevision};
            var plan=UnifiedPlan();
            long jc=judgmentThrough==0?plan.JudgmentThrough:Math.Min(judgmentThrough,plan.JudgmentThrough),ac=alarmThrough==0?plan.AlarmThrough:Math.Min(alarmThrough,plan.AlarmThrough);
            long sc=sequenceThrough==0?plan.SequenceThrough:Math.Min(sequenceThrough,plan.SequenceThrough);
            string query=(search??"").Trim();
            var filtered=plan.Rows.Where(r=>r.id<=(r.key.StartsWith("judge:",StringComparison.Ordinal)?jc:r.key.StartsWith("sequence:",StringComparison.Ordinal)?sc:ac) && (type=="all" || r.type==type) && (status=="all" || r.released==(status=="released")) && (level=="all" || r.level==level) &&
                (r.code+" "+r.name+" "+r.source+" "+r.packet+" "+r.detail+" "+r.category).IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            int pages=Math.Max(1,(filtered.Length+size-1)/size);page=Math.Min(page,pages);
            return new{unchanged=false,revision=plan.Revision,total=plan.Rows.Count,pending=plan.Rows.Count(r=>!r.released),released=plan.Rows.Count(r=>r.released),matching=filtered.Length,page,pages,pageSize=size,judgmentThrough=jc,alarmThrough=ac,storageError=plan.Error,
                sequenceThrough=sc,records=filtered.Skip((page-1)*size).Take(size).Select(r=>UnifiedDetail(r,plan)).ToArray()};
        }
        public void ReleaseUnifiedAbnormal(string key,string reason,string username,string actor)
        {
            reason=(reason??"").Trim();long id;string[] parts=(key??"").Split(':');
            if(parts.Length!=2 || !new[]{"judge","alarm","sequence"}.Contains(parts[0]) || !Int64.TryParse(parts[1],out id) || id<1 || key!=parts[0]+":"+id)throw new ArgumentException("异常记录号无效");
            if(reason.Length==0 || reason.Length>2000)throw new ArgumentException("请填写解除原因，长度为1～2000个字符");
            lock(Gate)
            {
                var plan=UnifiedPlan();string canonical;if(plan.Aliases.TryGetValue(key,out canonical)){key=canonical;parts=key.Split(':');id=Int64.Parse(parts[1]);}var row=plan.Rows.FirstOrDefault(r=>r.key==key);
                if(row==null)throw new ArgumentException("异常记录不存在，或已合并到对应的常态化判读记录");
                if(row.released)throw new InvalidOperationException("该异常已由其他操作解除，原解除原因保留，请刷新查看");
                ReleaseUnifiedAbnormalCore(row,reason,username,actor);
                Note("info","异常判读记录 "+key+" 已填写原因并解除；操作人："+actor);
            }
        }
        private void ReleaseUnifiedAbnormalCore(UnifiedAbnormalRecord row,string reason,string username,string actor)
        {
            var parts=row.key.Split(':');long id=Int64.Parse(parts[1]);
            if(parts[0]=="judge")
            {foreach(long alarmId in row.RelatedAlarms)AcknowledgeAlarm(alarmId,username,actor);judgmentAbnormals.Release(id,reason,username,actor);}
            else
            {
                if(unifiedReleases==null)throw new InvalidOperationException(unifiedReleaseError);
                if(parts[0]=="alarm")AcknowledgeAlarm(id,username,actor);else foreach(long alarmId in row.RelatedAlarms)AcknowledgeAlarm(alarmId,username,actor);
                unifiedReleases.Release(row.key,reason,username,actor);
            }
        }
        public object ReleaseAllUnifiedAbnormals(string reason,string username,string actor)
        {
            reason=(reason??"").Trim();
            if(reason.Length==0||reason.Length>2000)throw new ArgumentException("请填写共同解除原因，长度为1～2000个字符");
            var plan=UnifiedPlan();
            if(plan.Error.Length>0)throw new InvalidOperationException("异常记录读取不完整，未执行全部解除："+plan.Error);
            var pending=plan.Rows.Where(row=>!row.released).ToArray();string error="";
            // Build the merged plan once. Release the receive lock between records so that
            // a large history is not an O(n²) scan or one uninterrupted receive blockade.
            foreach(var row in pending)
            {
                try{lock(Gate)ReleaseUnifiedAbnormalCore(row,reason,username,actor);}
                catch(Exception ex){error="解除 "+row.key+" 时失败："+ex.Message;break;}
                System.Threading.Thread.Yield();
            }
            var after=UnifiedPlan();var releasedKeys=new HashSet<string>(after.Rows.Where(row=>row.released).Select(row=>row.key));
            int count=pending.Count(row=>releasedKeys.Contains(row.key));
            lock(Gate)Note("info","批量解除异常 "+count+" 条；操作人："+actor+(error.Length>0?"；"+error:""));
            return new{ok=error.Length==0,total=pending.Length,released=count,remaining=after.Rows.Count(row=>!row.released),error};
        }
        public void ExportUnifiedAbnormals(TextWriter writer)
        {
            var plan=UnifiedPlan();writer.NewLine="\r\n";
            writer.WriteLine("参数代号,参数名称,异常值,是否解除,解除原因,判读规则,记录号,异常类型,发生时间,异常类别,异常说明,来源,SPP包,APID,帧计数,运行模式,运行编号,解除时间,解除人,解除账号,历史确认状态,历史确认时间,历史确认人");
            foreach(var header in plan.Rows.AsEnumerable().Reverse())
            {
                var r=UnifiedDetail(header,plan);
                var cells=new[]{r.code,r.name,r.value,r.released?"是":"否",r.reason,r.rule,r.key,r.typeName,r.time,r.category,r.detail,r.source,r.packet,r.apid<0?"":r.apid.ToString(),r.sequence<0?"":r.sequence.ToString(),r.mode,r.runId,r.releasedAt,r.releasedBy,r.releasedUsername,r.legacyConfirmed?"已确认":"未确认",r.legacyConfirmedAt,r.legacyConfirmedBy}.Select(CsvCell).ToArray();
                decimal number;if(Decimal.TryParse(r.value,NumberStyles.Float,CultureInfo.InvariantCulture,out number))cells[2]="\""+r.value+"\"";
                writer.WriteLine(String.Join(",",cells));
            }
        }
    }
}
