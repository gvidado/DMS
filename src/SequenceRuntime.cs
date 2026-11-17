using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private bool sequenceRunning {get{return CurrentSequence.Running;}set{if(CurrentSequence.Running!=value){CurrentSequence.Running=value;sequenceRegistryDirty=true;}}}
        private bool sequencePaused {get{return CurrentSequence.Paused;}set{CurrentSequence.Paused=value;}}
        private string sequenceRunId {get{return CurrentSequence.RunId;}set{CurrentSequence.RunId=value;}}
        private string sequenceReason {get{return CurrentSequence.Reason;}set{CurrentSequence.Reason=value;}}
        private string sequenceActor {get{return CurrentSequence.Actor;}set{CurrentSequence.Actor=value;}}
        private string sequenceStartedAt {get{return CurrentSequence.StartedAt;}set{CurrentSequence.StartedAt=value;}}
        private SequenceDefinition sequenceDefinition {get{return CurrentSequence.Definition;}set{CurrentSequence.Definition=value;}}
        private Stopwatch sequenceClock {get{return CurrentSequence.Clock;}}
        private double sequencePausedSeconds {get{return CurrentSequence.PausedSeconds;}set{CurrentSequence.PausedSeconds=value;}}
        private double sequencePauseBegan {get{return CurrentSequence.PauseBegan;}set{CurrentSequence.PauseBegan=value;}}
        private double sequencePrevious {get{return CurrentSequence.Previous;}set{CurrentSequence.Previous=value;}}
        private double? sequenceT0 {get{return CurrentSequence.T0;}set{CurrentSequence.T0=value;}}
        private string sequenceT0At {get{return CurrentSequence.T0At;}set{CurrentSequence.T0At=value;}}
        private int sequencePosition {get{return CurrentSequence.Position;}set{CurrentSequence.Position=value;}}
        private int sequenceGroup {get{return CurrentSequence.Group;}set{CurrentSequence.Group=value;}}
        private int sequenceGroupEnd {get{return CurrentSequence.GroupEnd;}set{CurrentSequence.GroupEnd=value;}}
        private List<SequenceStepRun> sequenceRows {get{return CurrentSequence.Rows;}set{CurrentSequence.Rows=value;}}
        private long sequenceCatalogRevision {get{return CurrentSequence.CatalogRevision;}set{CurrentSequence.CatalogRevision=value;}}
        private string sequenceDecoderRevision {get{return CurrentSequence.DecoderRevision;}set{CurrentSequence.DecoderRevision=value;}}
        private string sequenceTCRevision {get{return CurrentSequence.TCRevision;}set{CurrentSequence.TCRevision=value;}}
        private string sequenceSendRevision {get{return CurrentSequence.SendRevision;}set{CurrentSequence.SendRevision=value;}}
        private readonly object sequenceQueueGate=new object();
        private readonly Queue<SequenceCapturedEvent> sequenceQueue=new Queue<SequenceCapturedEvent>();
        private long sequenceQueueBytes;
        private bool sequencePump,sequenceOverflow;
        internal bool SequenceExecuting {get{lock(Gate)return sequenceTasks.Values.Any(c=>c.Running&&c.Definition.Mode=="execute");}}
        private double SequenceNow {get{return CurrentSequence.Start.F0+sequenceClock.Elapsed.TotalSeconds;}}
        private int SequenceFocusIndex()
        {
            if(sequenceRows.Count==0)return 0;
            if(sequenceGroup>=0)
            {
                for(int i=Math.Max(sequenceGroup+1,sequencePosition);i<sequenceGroupEnd;i++)if(!sequenceRows[i].Terminal&&sequenceRows[i].Begun&&!sequenceRows[i].Step.Hold)return i;
                for(int i=Math.Max(sequenceGroup+1,sequencePosition);i<sequenceGroupEnd;i++)if(!sequenceRows[i].Terminal&&!sequenceRows[i].Step.Hold)return i;
                for(int i=Math.Max(sequenceGroup+1,sequencePosition);i<sequenceGroupEnd;i++)if(!sequenceRows[i].Terminal)return i;
            }
            return Math.Min(sequencePosition,sequenceRows.Count-1);
        }
        internal object SequenceSummaryCore()
        {
            lock(Gate){if(CurrentSequence.FrozenSummary!=null)return CurrentSequence.FrozenSummary;double elapsed=SequenceNow;return new{running=sequenceRunning,paused=sequencePaused,runId=sequenceRunId,controlRevision=Interlocked.Read(ref CurrentSequence.Generation),parentRunId=CurrentSequence.ParentRunId,origin=CurrentSequence.Origin,id=sequenceDefinition==null?"":sequenceDefinition.Id,mode=sequenceDefinition==null?"":sequenceDefinition.Mode,
                code=sequenceDefinition==null?"":sequenceDefinition.Code,name=sequenceDefinition==null?"":sequenceDefinition.Name,reason=sequenceReason,startedAt=sequenceStartedAt,elapsed,sessionElapsed=sequenceClock.Elapsed.TotalSeconds,startRow=CurrentSequence.Start.StartRow,initialF0=CurrentSequence.Start.F0,initialT0=CurrentSequence.Start.T0,t0Preset=CurrentSequence.T0Preset,skipped=sequenceRows.Count(r=>r.Status=="已跳过"),t0=sequenceT0,t0EstablishedAt=sequenceT0At,t0Elapsed=sequenceT0.HasValue?(double?)Math.Max(0,elapsed-sequenceT0.Value):null,
                pausedSeconds=sequencePausedSeconds+(sequencePaused?RuleSeconds()-sequencePauseBegan:0),total=sequenceRows.Count,completed=sequenceRows.Count(r=>r.Terminal&&r.Status!="已跳过"),abnormal=sequenceRows.Count(r=>r.HadFailure),
                currentRow=sequenceRunning&&sequenceRows.Count>0?sequenceRows[SequenceFocusIndex()].Step.Row.Row:0,currentIndex=SequenceFocusIndex(),currentPage=SequenceFocusIndex()/50+1,inParallel=sequenceGroup>=0};}
        }
        internal object SequenceStateCore(string id,int page)
        {
            lock(Gate)
            {
                bool current=sequenceDefinition!=null && sequenceDefinition.Id==id;var definition=current?sequenceDefinition:sequenceConfiguration.Sequences.FirstOrDefault(d=>d.Id==id);
                if(definition==null)return new{runtime=SequenceSummaryCore(),runtimes=SequenceRuntimes(),call=SequenceCallNotice(),rows=new object[0],page=1,pages=1,total=0};
                int count=definition.Rows.Count,pages=Math.Max(1,(count+49)/50);page=Math.Max(1,Math.Min(pages,page));
                var rows=definition.Rows.Skip((page-1)*50).Take(50).Select((row,offset)=>
                {
                    int index=(page-1)*50+offset;var run=current?sequenceRows[index]:null;
                    double effective=sequenceConfiguration.DefaultTimeoutSeconds;double parsed;if(Double.TryParse(row.Timeout,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed))effective=parsed;
                    return run==null?new SequenceRowResult{input=row,timeout=effective}:SequenceRowView(run);
                }).ToArray();return new{runtime=SequenceSummaryCore(),runtimes=SequenceRuntimes(),call=SequenceCallNotice(),definition=new{definition.Id,definition.Code,definition.Name,definition.Mode,definition.SourceIP},rows,page,pages,total=count};
            }
        }
        internal List<SequenceStep> PrepareSequenceCore(string id,SequenceStartOptions start=null)
        {
            lock(Gate)
            {
                EnsureCurrentSequenceIdle();if(sequenceStorageError.Length>0)throw new InvalidOperationException(sequenceStorageError);if(sequenceHistoryError.Length>0)throw new InvalidOperationException(sequenceHistoryError);
                if(judgmentArchives==null || judgmentArchiveError.Length>0)throw new InvalidOperationException(judgmentArchiveError);
                if(!Listening)throw new InvalidOperationException("请先启动真实参数监听，再启动序列；序列启动不会自动发送历史步骤");
                var definition=SequenceDefinition(id);List<string> errors,warnings;var steps=CompileSequence(definition,sequenceConfiguration.DefaultTimeoutSeconds,out errors,out warnings);
                if(errors.Count>0)throw new ArgumentException("序列启动检查失败："+String.Join("；",errors.Take(30)));
                ValidateSequenceStart(steps,start);var remaining=steps.Skip(SequenceStartIndex(steps,start));
                foreach(var step in SequenceJumpSteps(remaining).Where(s=>s.Predicate!=null))foreach(var bound in step.Predicate.Fields.Values)
                {
                    if(NativeDecoder && !bound.Field.PacketEnabled)errors.Add("参数所属SPP未启用："+bound.Field.Reference);
                    if(NativeDecoder?!ReadyFor(bound.Field.Id):!Config.MappingConfirmed || !Protocol.InProfile(bound.Field,Config.Profile))errors.Add("参数解码映射未确认或不在当前布局："+bound.Field.Reference);
                }
                try{CheckCommandCapture(SequenceJumpSteps(remaining).Where(s=>s.Code.Length>0 && (s.Mode=="judge" || s.Inside)).Select(s=>s.Code));}catch(Exception ex){errors.Add(ex.Message);}
                if(errors.Count>0)throw new ArgumentException("序列启动检查失败："+String.Join("；",errors.Take(30)));return steps;
            }
        }
        internal static string CheckSequenceRunName(string name)
        {name=(name??"").Trim();if(name.Length==0 || name.Length>128 || name.Any(Char.IsControl))throw new ArgumentException("请先确认本次序列名称，名称为1～128个字符且不含换行或控制字符");return name;}
        internal void StartSequenceCore(string id,List<SequenceStep> steps,string actor,string sendRevision,string runName=null,SequenceStartOptions start=null)
        {
            lock(Gate)
            {
                EnsureCurrentSequenceIdle();if(!Listening)throw new InvalidOperationException("监听已停止，请重新开始监听");ValidateSequenceStart(steps,start);
                sequenceDefinition=SequenceDefinition(id);sequenceDefinition.Name=CheckSequenceRunName(runName??sequenceDefinition.Name);sequenceRows=steps.Select(s=>new SequenceStepRun{Step=s}).ToList();if(sequenceRunId.Length==0)sequenceRunId=Guid.NewGuid().ToString("N");sequenceActor=actor;
                InitializeSequenceStart(steps,start);sequencePaused=false;sequencePausedSeconds=0;
                sequenceDecoderRevision=DecoderRevision;sequenceTCRevision=commandBindingRevision;sequenceCatalogRevision=Outgoing.Commands.DisplayRevision;sequenceSendRevision=sendRevision;
                sequenceClock.Restart();sequenceStartedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");sequenceReason="运行中";Interlocked.Increment(ref CurrentSequence.Generation);
                SequenceAudit("开始",null,"运行中",SequenceStartDescription());sequenceRunning=true;
                foreach(var skipped in sequenceRows.Where(r=>r.Status=="已跳过"))SequenceRecordRow(skipped);
                ActivateSequenceStartGroup();SequenceAdvance();Note("info",(sequenceDefinition.Mode=="execute"?"序列执行":"序列判读")+"已启动："+sequenceDefinition.Name);
            }
        }
        internal void StopSequenceCore(string reason)
        {
            lock(Gate)
            {
                if(!sequenceRunning)return;
                if(sequencePaused){sequencePausedSeconds+=RuleSeconds()-sequencePauseBegan;sequencePaused=false;}
                sequenceClock.Stop();sequenceRunning=false;sequenceReason=reason;Interlocked.Increment(ref CurrentSequence.Generation);
                try
                {
                    foreach(var row in sequenceRows.Where(r=>!r.Terminal)){row.Status=row.HadFailure?row.Status:"终止";if(!row.HadFailure)row.Reason=reason;row.Terminal=true;row.Finished=SequenceNow;row.FinishedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");SequenceRecordRow(row);}
                    SequenceAudit("结束",null,"终止",reason);
                }
                catch(IOException){sequenceReason=sequenceHistoryError;}
            }
        }
        internal void PauseSequenceCore(string runId)
        {
            lock(Gate)
            {
                if(!sequenceRunning || runId!=sequenceRunId || sequenceDefinition.Mode!="execute")throw new InvalidOperationException("没有可暂停的序列执行");
                if(sequencePaused)return;sequencePaused=true;sequencePauseBegan=RuleSeconds();sequenceClock.Stop();sequenceReason="人工暂停，本序列计时已冻结";Interlocked.Increment(ref CurrentSequence.Generation);SequenceAudit("暂停",null,"人工暂停",sequenceReason);
            }
        }
        internal void ResumeSequenceCore(string runId)
        {
            lock(Gate)
            {
                if(!sequenceRunning || runId!=sequenceRunId || !sequencePaused)throw new InvalidOperationException("当前没有等待继续的序列");
                sequencePausedSeconds+=RuleSeconds()-sequencePauseBegan;sequencePaused=false;sequenceClock.Start();sequenceReason="运行中";Interlocked.Increment(ref CurrentSequence.Generation);
                foreach(var row in sequenceRows.Where(r=>r.Begun&&!r.Terminal)){ResetSequenceBaseline(row);row.CommandSince=RuleSeconds();row.CommandSinceUtc=DateTime.UtcNow;}
                if(sequencePosition<sequenceRows.Count)
                {
                    var row=sequenceRows[sequencePosition];
                    if(row.Step.Row.Keyword=="PAUSE"&&!row.PauseAcknowledged)
                    {
                        row.PauseAcknowledged=true;
                        if(row.Step.Code.Length==0)SequenceComplete(row);
                        else
                        {
                            row.SendPending=true;row.Status="待发送指令";row.Reason="PLUSE已人工继续，等待发送本行指令";
                            row.Due=Math.Max(SequenceNow,sequencePrevious+SequenceMinimumInterval);
                        }
                    }
                }
                SequenceAudit("继续",null,"运行中","");
                foreach(var active in CurrentSequence.ActiveRows.Where(r=>r.Begun&&!r.Terminal&&r.CommandDone&&r.Step.Predicate!=null).ToArray())EvaluateSequenceRow(active);
                SequenceAdvance();
            }
        }
        private void ActivateSequenceRow(SequenceStepRun row,double relativeBase)
        {
            if(row.Activated)return;row.Activated=true;row.ActivatedAt=SequenceNow;CurrentSequence.ActiveRows.Add(row);
            if(row.Step.Row.Keyword!="PAUSE")
            {
                if(row.Step.Absolute && !sequenceT0.HasValue){SequenceFail(row,"异常","计时基准T0未建立");return;}
                row.Planned=(row.Step.FromStart?0:row.Step.Absolute?sequenceT0.Value:relativeBase)+row.Step.Delay;row.Due=Math.Max(Math.Max(row.ActivatedAt,row.Planned.Value),relativeBase+(sequenceDefinition.Mode=="execute"&&row.Step.Row.Keyword!="TEXT"?SequenceMinimumInterval:0));row.Status="等待计划时间";
            }
            else{row.Due=SequenceNow;row.Status="待开始";}
        }
        private void BeginSequenceRow(SequenceStepRun row)
        {
            if(row.Begun || row.Terminal || SequenceNow<row.Due)return;row.Begun=true;row.StartTime=row.LastValid=SequenceNow;row.Actual=row.StartTime;
            row.StartedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");if(row.Planned.HasValue)row.Delay=Math.Max(0,row.StartTime-row.Planned.Value);
            row.CommandSince=RuleSeconds();row.CommandSinceUtc=DateTime.UtcNow;row.CommandDone=row.Step.Code.Length==0;ResetSequenceBaseline(row);
            if(row.Step.Row.Keyword=="TEXT"){CompleteSequenceText(row);return;}
            if(row.Step.NextDefinition!=null){if(row.Step.LaunchJudgment){try{LaunchSequenceJudgment(row);}catch(Exception ex){SequenceFail(row,"异常","CALL启动判读失败："+ex.Message);}return;}row.Reason="序列结束，转至"+row.Step.NextDefinition.Code;SequenceComplete(row);return;}
            if(row.Step.Row.Keyword=="PAUSE"){PauseSequenceCore(sequenceRunId);row.Status="人工暂停";return;}
            if(row.Step.Code.Length>0)
            {if(sequenceDefinition.Mode=="execute"&&!row.Step.Inside){row.SendPending=true;row.Status="待发送指令";row.Reason=row.Step.Predicate==null?"":"等待指令发出后判读参数";}else{row.Status="等待指令";row.Reason=row.Step.Predicate==null?"等待监视到对应指令":"等待监视到指令后判读参数";}}
            else if(row.Step.Predicate!=null){row.Status=row.Step.Hold?"持续检查":"等待参数";row.Reason="等待有效参数数据";EvaluateSequenceRow(row);}
            else SequenceComplete(row);
        }
        private void ResetSequenceBaseline(SequenceStepRun row)
        {
            row.Baselines.Clear();if(row.Step.Predicate==null)return;
            foreach(var pair in row.Step.Predicate.Fields){var h=RuleHistory(pair.Value.Field,pair.Value.Source);row.Baselines[pair.Key]=h==null||h.Samples.Count==0?0:h.Samples[0].sampleNumber;}
        }
        private void SequenceComplete(SequenceStepRun row)
        {
            if(row.Terminal)return;row.Terminal=true;CurrentSequence.ActiveRows.Remove(row);row.Status=row.HadFailure?"异常":"通过";row.Finished=SequenceNow;row.FinishedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            if(!row.HadFailure && row.Step.NextDefinition==null)row.Reason="";
            if(row.Step.Row.Mark.Equals("T0",StringComparison.OrdinalIgnoreCase) && !row.HadFailure){sequenceT0=row.Finished;sequenceT0At=row.FinishedAt;CurrentSequence.T0Preset=false;}
            SequenceRecordRow(row);
        }
        private void SequenceFail(SequenceStepRun row,string status,string reason)
        {
            row.HadFailure=true;row.Status=status;row.Reason=reason;
            if(!row.Episode){row.Episode=true;SequenceAudit("异常",row,status,reason);RecordAlarm("error",sequenceDefinition.Mode=="execute"?"序列执行异常":"序列判读异常",sequenceDefinition.Name+" / 第"+row.Step.Row.Row+"行："+reason,identity:JsonFile.Encode(new[]{sequenceDefinition.Id,row.Step.Row.Row.ToString(),status,reason}),context:new JudgmentAbnormalEntry{Code="第"+row.Step.Row.Row+"行",Name=sequenceDefinition.Name,ValueText=JsonFile.Encode(row.Values),Script=row.Step.Row.Condition,RunId=sequenceRunId,SourceIP=sequenceDefinition.SourceIP});}
            if(!sequenceRunning)return;
            if(sequenceDefinition.Mode=="execute"){StopSequenceCore("第"+row.Step.Row.Row+"行"+status+"："+reason);return;}
            if(!row.Step.Hold){row.Terminal=true;CurrentSequence.ActiveRows.Remove(row);row.Finished=SequenceNow;row.FinishedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");SequenceRecordRow(row);}
        }
        private void SequenceAdvance()
        {
            for(int budget=0;budget<128 && sequenceRunning&&!sequencePaused;budget++)
            {
                if(sequencePosition>=sequenceRows.Count)
                {sequenceRunning=false;sequenceClock.Stop();sequenceReason=SequenceCompletionStatus();Interlocked.Increment(ref CurrentSequence.Generation);SequenceAudit("结束",null,sequenceReason,"");return;}
                if(sequenceGroup>=0)
                {
                    var inside=CurrentSequence.ActiveRows.ToArray();
                    foreach(var row in inside.Where(r=>!r.Terminal)){BeginSequenceRow(row);if(!sequenceRunning)return;CheckSequenceDeadline(row);if(!sequenceRunning)return;}
                    if(inside.Where(r=>!r.Step.Hold).All(r=>r.Terminal) && inside.Where(r=>r.Step.Hold).All(r=>r.Begun&&(r.FreshObserved||r.HadFailure)))
                    {foreach(var hold in inside.Where(r=>r.Step.Hold))SequenceComplete(hold);sequencePosition=sequenceGroupEnd;sequenceGroup=sequenceGroupEnd=-1;sequencePrevious=SequenceNow;continue;}
                    return;
                }
                var current=sequenceRows[sequencePosition];
                ActivateSequenceRow(current,sequencePrevious);if(!sequenceRunning)return;BeginSequenceRow(current);if(sequencePaused||!sequenceRunning)return;CheckSequenceDeadline(current);if(!sequenceRunning)return;
                if(!current.Terminal)return;
                if(current.Step.NextDefinition!=null && !current.Step.LaunchJudgment){TransitionSequence(current.Step);return;}
                if(current.Step.Row.Keyword=="PAR")
                {
                    sequenceGroup=sequencePosition;sequenceGroupEnd=current.Step.Pair;double start=SequenceNow;
                    foreach(var row in sequenceRows.Skip(sequenceGroup+1).Take(sequenceGroupEnd-sequenceGroup-1))
                    {ActivateSequenceRow(row,start);if(!sequenceRunning)return;}
                    continue;
                }
                sequencePrevious=current.Finished??SequenceNow;sequencePosition++;
            }
        }
        private void CheckSequenceDeadline(SequenceStepRun row)
        {
            if(!row.Begun || row.Terminal || sequencePaused)return;
            if(SequenceNow-(row.Step.Hold?row.LastValid:row.StartTime)>=row.Step.Timeout)
            {
                CaptureSequenceParameterValues(row);
                SequenceFail(row,"超时",row.Step.Hold?"HOLD无有效新数据超过时限":"本项在"+row.Step.Timeout.ToString(CultureInfo.InvariantCulture)+"秒内未满足");
            }
        }
        private void CaptureSequenceParameterValues(SequenceStepRun row)
        {
            if(row.Step.Predicate==null)return;
            var values=new Dictionary<string,object>();
            foreach(var pair in row.Step.Predicate.Fields)
            {
                var history=RuleHistory(pair.Value.Field,pair.Value.Source);
                var latest=history==null?null:history.Latest;
                if(latest==null){values[pair.Key]="未接收";continue;}
                // Copy text, not the mutable live reading; retain full engineering precision.
                string value=latest.EngineeringText??Convert.ToString(latest.EngineeringValue,CultureInfo.InvariantCulture);
                string block=SequenceInputBlock(row,pair.Key,pair.Value);
                values[pair.Key]=String.IsNullOrEmpty(block)?value:(String.IsNullOrEmpty(value)?block:value+"（"+block+"）");
            }
            row.Values=values;
        }
        private void SequenceTelemetryCore(string source,HashSet<string> changed)
        {
            if(!sequenceRunning||sequencePaused)return;
            foreach(var row in CurrentSequence.ActiveRows.Where(r=>r.Begun&&!r.Terminal&&r.CommandDone&&r.Step.Predicate!=null).ToArray())
            {
                if(!row.Step.Predicate.Fields.Values.Any(b=>b.Source==source&&changed.Contains(b.Field.Id)))continue;
                EvaluateSequenceRow(row);if(!sequenceRunning)return;
            }
            SequenceAdvance();
        }
        internal SequenceSendWork SequenceTickCore(string sendRevision,bool dispatch=true)
        {
            lock(Gate)
            {
                if(!sequenceRunning)return null;
                if(!Listening){StopSequenceCore("监听停止，序列自动终止");return null;}
                if(sequenceDecoderRevision!=DecoderRevision || sequenceTCRevision!=commandBindingRevision || (sequenceDefinition.Mode=="execute"&&sequenceSendRevision!=sendRevision)){StopSequenceCore("运行所依赖的解码、TC绑定或发送配置已变化");return null;}
                if(sequenceCatalogRevision!=Outgoing.Commands.DisplayRevision)
                {
                    foreach(var step in SequenceJumpSteps(sequenceRows.Where(r=>!r.Terminal&&!r.CommandDone&&r.Status!="已跳过").Select(r=>r.Step)).Where(s=>s.Code.Length>0))
                    {try{if(SequenceCommandSignature(step,ResolveCommandCode(step.Code))!=step.CommandSignature){StopSequenceCore("运行所用指令定义已变化："+step.Code);return null;}}catch(Exception ex){StopSequenceCore("指令依赖失效："+ex.Message);return null;}}
                    sequenceCatalogRevision=Outgoing.Commands.DisplayRevision;
                }
                if(sequencePaused)return null;SequenceAdvance();if(!sequenceRunning)return null;
                return dispatch?TakeSequenceSendWork():null;
            }
        }
        private SequenceSendWork TakeSequenceSendWork()
        {
            if(!sequenceRunning||sequencePaused)return null;
            for(int i=0;i<sequenceRows.Count;i++){var row=sequenceRows[i];if(row.SendPending&&!row.Terminal&&SequenceNow>=row.Due){row.SendPending=false;row.Status="发送中";return new SequenceSendWork{RunId=sequenceRunId,Index=i,Number=row.Step.CommandNumber,Code=row.Step.Code,SendCode=row.Step.SendCode,AllDestinations=row.Step.AllDestinations,Actor=sequenceActor+" / 序列"+sequenceRunId};}}
            return null;
        }

        internal bool SequenceCanSendCore(SequenceSendWork work)
        {lock(Gate){bool ready=sequenceRunning&&!sequencePaused&&Listening&&work.RunId==sequenceRunId&&sequenceRows[work.Index].Status=="发送中";if(ready && work.SendBegan==0)work.SendBegan=RuleSeconds();return ready;}}
        internal void SequenceSentCore(SequenceSendWork work,bool ok,string reason,double? sentAt=null)
        {
            lock(Gate)
            {
                if(!sequenceRunning || work.RunId!=sequenceRunId)return;var row=sequenceRows[work.Index];
                if(!ok){SequenceFail(row,"异常","UDP发送失败："+reason);return;}
                row.CommandDone=true;row.CommandDataSince=sentAt.HasValue&&sentAt.Value>0?sentAt.Value:work.SendBegan>0?work.SendBegan:row.CommandSince;
                SequenceAudit("指令发送",row,"已发出","");
                if(row.Step.Predicate==null)SequenceComplete(row);else{row.Status="等待参数";EvaluateSequenceRow(row);}SequenceAdvance();
            }
        }
        private void QueueSequenceCaptured(SendRecord record)
        {
            var generations=captureSequenceTasks.Where(c=>c.Running&&!c.Paused).ToDictionary(c=>c.RunId,c=>Interlocked.Read(ref c.Generation));
            if(generations.Count==0)return;bool schedule=false;
            lock(sequenceQueueGate)
            {
                if(sequenceQueue.Count>=2000 || sequenceQueueBytes+record.Payload.Length>16*1024*1024)sequenceOverflow=true;
                else{sequenceQueue.Enqueue(new SequenceCapturedEvent{Record=record,Generations=generations,Time=RuleSeconds()});sequenceQueueBytes+=record.Payload.Length;}
                if(!sequencePump){sequencePump=true;schedule=true;}
            }
            if(schedule)ThreadPool.QueueUserWorkItem(ignored=>DrainSequenceCommands());
        }
        private void DrainSequenceCommands()
        {
            while(true)
            {
                SequenceCapturedEvent pending=null;bool overflow;
                lock(sequenceQueueGate){overflow=sequenceOverflow;sequenceOverflow=false;if(sequenceQueue.Count>0){pending=sequenceQueue.Dequeue();sequenceQueueBytes-=pending.Record.Payload.Length;}else{sequencePump=false;if(!overflow)return;}}
                EachSequence(()=>
                {
                    if(overflow&&sequenceRunning)StopSequenceCore("指令事件队列溢出，序列终止");
                    long observed=-1;if(pending!=null)pending.Generations.TryGetValue(sequenceRunId,out observed);
                    if(pending==null || !sequenceRunning || sequencePaused || (!pending.Generations.ContainsKey(sequenceRunId)||observed!=Interlocked.Read(ref CurrentSequence.Generation)))return;
                    try
                    {
                        var record=pending.Record;if(record.SourceIP!=Outgoing.Commands.BindingSource)return;var match=Outgoing.Commands.MatchCommand(record.Payload);var matches=match.Entries;var codes=ObservedCommands.MatchingCodes(match,record.DestinationIP,tcBindings.Bindings);
                        foreach(var row in sequenceRows.Where(r=>r.Begun&&!r.Terminal&&!r.CommandDone&&r.Step.Code.Length>0&&!r.SendPending&&r.Status!="发送中").ToArray())
                        {
                            if(pending.Time<row.CommandSince || record.Time.ToUniversalTime()<row.CommandSinceUtc || !row.Step.Destinations.Contains(record.DestinationIP) || !codes.Contains(row.Step.SendCode) || !matches.Any(m=>m.MatchesNumber(row.Step.CommandNumber)))continue;
                            CheckSequenceDeadline(row);if(!sequenceRunning)return;if(row.Terminal)continue;
                            row.HistoryCommandEvents=new[]{record.HistoryEventId};row.CommandDone=true;row.CommandDataSinceUtc=record.Time.ToUniversalTime();SequenceAudit("指令监视",row,"已监视到",record.DestinationIP);
                            if(row.Step.Predicate==null)SequenceComplete(row);else{row.Status="等待参数";EvaluateSequenceRow(row);if(!sequenceRunning)return;}
                        }
                        SequenceAdvance();
                    }
                    catch(Exception ex){StopSequenceCore("指令事件处理失败："+ex.Message);}
                });
            }
        }
    }
}
