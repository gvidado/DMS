using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private string sequenceHistoryError="";
        private string SequenceHistoryPath {get{return Path.Combine(root,"data","sequence-history.jsonl");}}
        private void InitializeSequenceHistory()
        {
            if(!File.Exists(SequenceHistoryPath))return;
            try
            {
                using(var file=new FileStream(SequenceHistoryPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){if(file.Length>0){file.Seek(-1,SeekOrigin.End);if(file.ReadByte()!=10)throw new InvalidDataException("末条历史写入不完整，请保留原文件并修复后再运行");}}
                var unfinished=new Dictionary<string,SequenceHistoryEvent>();
                foreach(var entry in ReadSequenceHistory())
                {if(entry.Event=="开始")unfinished[entry.RunId]=entry;else if(entry.Event=="结束")unfinished.Remove(entry.RunId);}
                foreach(var previous in unfinished.Values)
                {previous.Event="结束";previous.Status="终止";previous.Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");previous.Reason="进程上次退出时未完成，重启后不自动继续或重发";previous.Definition=null;previous.ShipTime=new SequenceShipTime{Status="重启时无实时数据"};AppendSequenceHistory(previous);}
            }
            catch(Exception ex){sequenceHistoryError="序列历史读取失败，原文件保留："+ex.Message;}
        }
        private IEnumerable<SequenceHistoryEvent> ReadSequenceHistory()
        {
            if(!File.Exists(SequenceHistoryPath))yield break;
            using(var stream=new FileStream(SequenceHistoryPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var bytes=new MemoryStream())
            {
                long limit=stream.Length,recordId=0;
                while(stream.Position<limit)
                {
                    int next=stream.ReadByte();if(next<0)yield break;if(next!=10){bytes.WriteByte((byte)next);continue;}
                    string line=new UTF8Encoding(false,true).GetString(bytes.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');bytes.SetLength(0);
                    if(String.IsNullOrWhiteSpace(line))continue;
                    var entry=JsonFile.Decode<SequenceHistoryEvent>(line);if(entry==null||String.IsNullOrEmpty(entry.RunId))throw new InvalidDataException("序列历史记录格式无效");entry.HistoryId=++recordId;yield return entry;
                }
            }
        }
        private void AppendSequenceHistory(SequenceHistoryEvent entry)
        {
            if(sequenceHistoryError.Length>0)throw new IOException(sequenceHistoryError);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SequenceHistoryPath));byte[] bytes=new UTF8Encoding(false).GetBytes(JsonFile.Encode(entry)+Environment.NewLine);
                using(var file=new FileStream(SequenceHistoryPath,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read)){long start=file.Length;file.Position=start;try{file.Write(bytes,0,bytes.Length);file.Flush(true);}catch{file.SetLength(start);file.Flush(true);throw;}}
                if(judgmentArchives!=null)judgmentArchives.AppendSequence(entry);
            }
            catch(Exception ex){sequenceHistoryError="序列结果写入失败，运行已停止："+ex.Message;sequenceRunning=false;sequenceClock.Stop();sequenceReason=sequenceHistoryError;throw;}
        }
        private void SequenceAudit(string kind,SequenceStepRun row,string status,string reason)
        {
            var shipTime=CaptureSequenceShipTime();if(row!=null)row.ShipTime=shipTime;
            var audit=new SequenceHistoryEvent{Event=kind,RunId=sequenceRunId,ParentRunId=CurrentSequence.ParentRunId,Origin=CurrentSequence.Origin,Result=kind=="步骤结果"&&row!=null?SequenceRowView(row):null,Mode=sequenceDefinition.Mode,Code=sequenceDefinition.Code,Sequence=sequenceDefinition.Name,Sheet=sequenceDefinition.Sheet,Operator=sequenceActor,
                Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),ShipTime=shipTime,Status=status,Reason=reason,Row=row==null?0:row.Step.Row.Row,Input=row==null?null:SequenceClone(row.Step.Row),Timeout=row==null?null:(double?)row.Step.Timeout,
                Planned=row==null?null:row.Planned,Actual=row==null?null:row.Actual,Delay=row==null?null:row.Delay,Elapsed=SequenceNow,Paused=sequencePausedSeconds+(sequencePaused?RuleSeconds()-sequencePauseBegan:0),
                CommandName=row==null?"":row.Step.CommandName,Destinations=row==null?"":String.Join(";",row.Step.Destinations),Values=row==null?"":JsonFile.Encode(row.Values),Definition=kind=="开始"||kind=="配置切换"?SequenceClone(sequenceDefinition):null,Start=kind=="开始"?SequenceClone(CurrentSequence.Start):null,
                Diagnostic=row!=null && row.Step.Predicate!=null && (kind=="异常" || kind=="步骤结果")?CaptureSequenceEvaluationDiagnostic(row):null};
            AppendSequenceHistory(audit);
        }
        private void SequenceRecordRow(SequenceStepRun row)
        {if(row.Recorded)return;SequenceAudit("步骤结果",row,row.Status,row.Reason);row.Recorded=true;}
        internal object SequenceHistoryPage(int page,string runId,string search="",string mode="",string result="all")
        {
            if(page<1 || page>10000)throw new ArgumentException("历史页码须为1至10000");
            CheckSequenceHistoryMode(mode);CheckSequenceResultFilter(result);var plan=UnifiedPlan();var incidents=SequenceIncidentIndex(plan);var tail=new Queue<SequenceHistoryEvent>();int total=0,retain=page*50;
            var matched=MatchingSequenceRuns(search);
            foreach(var entry in ReadSequenceHistory()){if((mode.Length>0 && entry.Mode!=mode) || (runId.Length>0 && entry.RunId!=runId) || (matched!=null && !matched.Contains(entry.RunId)) || !SequenceResultMatches(entry,result,incidents))continue;total++;tail.Enqueue(entry);while(tail.Count>retain)tail.Dequeue();}
            page=Math.Min(page,Math.Max(1,(total+49)/50));
            return new{page,pages=Math.Max(1,(total+49)/50),total,error=String.Join("；",new[]{sequenceHistoryError,plan.Error}.Where(s=>!String.IsNullOrEmpty(s))),rows=tail.Reverse().Skip((page-1)*50).Take(50).Select(e=>SequenceHistoryResponse(e,plan,incidents)).ToArray()};
        }
        internal void ExportSequenceHistory(TextWriter writer,string runId,string search="",string mode="",string result="all")
        {CheckSequenceHistoryMode(mode);CheckSequenceResultFilter(result);var matched=MatchingSequenceRuns(search);var incidents=SequenceIncidentIndex(UnifiedPlan());WriteSequenceHistoryCsv(writer,ReadSequenceHistory().Where(e=>(mode.Length==0||e.Mode==mode) && (runId.Length==0 || e.RunId==runId) && (matched==null || matched.Contains(e.RunId)) && SequenceResultMatches(e,result,incidents)));}
        private void WriteSequenceHistoryCsv(TextWriter writer,IEnumerable<SequenceHistoryEvent> entries)
        {
            var names=SequenceRunSummaries().ToDictionary(r=>r.RunId,r=>r.Name);
            writer.WriteLine("模式,工作名称,原始行号,事件,实际时间,状态,测试工作内容及要求,参数实际值,指令名称,实际开始序列秒,异常原因");
            Func<double?,string> n=value=>value.HasValue?value.Value.ToString("0.######",CultureInfo.InvariantCulture):"";
            foreach(var e in entries)
            {
                var row=e.Input??new SequenceRow();
                string workName=e.Sequence;if(String.IsNullOrEmpty(workName))names.TryGetValue(e.RunId,out workName);else names[e.RunId]=workName;
                writer.WriteLine(String.Join(",",new[]{e.Mode=="execute"?"序列执行":"序列判读",workName,e.Row==0?"":e.Row.ToString(),e.Event,e.Time,e.Status,row.Description,e.Values,e.CommandName,n(e.Actual),e.Reason}.Select(CsvCell)));
            }
        }
    }
}
