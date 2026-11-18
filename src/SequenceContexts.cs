using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class SequenceRuntimeContext
    {
        public volatile bool Running,Paused;
        public string RunId="",Reason="尚未启动",Actor="",StartedAt="",ParentRunId="",Origin="手动启动";
        public SequenceDefinition Definition;
        public readonly Stopwatch Clock=new Stopwatch();
        public double PausedSeconds,PauseBegan,Previous;
        public double? T0;
        public string T0At="";
        public bool T0Preset;
        public SequenceStartOptions Start=new SequenceStartOptions();
        public int Position,Group=-1,GroupEnd=-1;
        public List<SequenceStepRun> Rows=new List<SequenceStepRun>();
        public readonly HashSet<SequenceStepRun> ActiveRows=new HashSet<SequenceStepRun>();
        public long Generation,CatalogRevision,Ordinal;
        public string DecoderRevision="",TCRevision="",SendRevision="";
        public object FrozenSummary;
        public bool DetailsArchived;
    }
    internal sealed class SequenceCapturedEvent
    {public SendRecord Record;public Dictionary<string,long> Generations;public double Time;}

    public sealed partial class Monitor
    {
        private readonly Dictionary<string,SequenceRuntimeContext> sequenceTasks=new Dictionary<string,SequenceRuntimeContext>();
        private readonly SequenceRuntimeContext emptySequence=new SequenceRuntimeContext();
        private volatile SequenceRuntimeContext[] captureSequenceTasks=new SequenceRuntimeContext[0];
        private readonly string sequenceSession=Guid.NewGuid().ToString("N");
        private long sequenceTaskRevision,sequenceTaskSerial;
        private bool sequenceRegistryDirty=true;
        private SequenceRuntimeContext[] executingSequenceTasks=new SequenceRuntimeContext[0];
        private SequenceRuntimeContext latestJudgeTask,latestExecuteTask;
        private string lastSequenceSendRun="";
        // The cursor and task registry are serialized by Gate; nested CALLs restore the cursor.
        private SequenceRuntimeContext sequenceContext;
        internal bool HasActiveSequenceTasks {get{return captureSequenceTasks.Length>0;}}
        private SequenceRuntimeContext CurrentSequence {get{return sequenceContext??emptySequence;}}
        private bool AnySequenceRunning {get{return sequenceTasks.Values.Any(c=>c.Running);}}
        private SequenceRuntimeContext SequenceContext(string mode)
        {if(mode!="execute"&&mode!="judge")throw new ArgumentException("请选择序列执行或序列判读");RefreshSequenceTasks();return (mode=="execute"?latestExecuteTask:latestJudgeTask)??emptySequence;}
        private SequenceRuntimeContext SequenceTask(string runId)
        {SequenceRuntimeContext value;if(String.IsNullOrEmpty(runId)||!sequenceTasks.TryGetValue(runId,out value))throw new ArgumentException("本次运行不存在；服务器重启前的结果请在判读存档中查看");return value;}
        private T InSequence<T>(SequenceRuntimeContext context,Func<T> action)
        {lock(Gate){var previous=sequenceContext;sequenceContext=context;try{return action();}finally{sequenceContext=previous;}}}
        private T InSequence<T>(string mode,Func<T> action){lock(Gate)return InSequence(SequenceContext(mode),action);}
        private void InSequence(SequenceRuntimeContext context,Action action){InSequence<object>(context,()=>{action();return null;});}
        private void EachSequence(Action action)
        {lock(Gate){RefreshSequenceTasks();var active=captureSequenceTasks;foreach(var context in active)if(context.Running)InSequence(context,action);RefreshSequenceTasks();}}
        private void RefreshSequenceTasks()
        {
            if(!sequenceRegistryDirty)return;sequenceRegistryDirty=false;
            captureSequenceTasks=sequenceTasks.Values.Where(c=>c.Running).OrderBy(c=>c.Ordinal).ToArray();
            executingSequenceTasks=captureSequenceTasks.Where(c=>c.Definition.Mode=="execute").ToArray();latestJudgeTask=latestExecuteTask=null;
            foreach(var task in sequenceTasks.Values)if(task.Definition!=null){if(task.Definition.Mode=="execute"){if(latestExecuteTask==null||task.Ordinal>latestExecuteTask.Ordinal)latestExecuteTask=task;}else if(latestJudgeTask==null||task.Ordinal>latestJudgeTask.Ordinal)latestJudgeTask=task;}
            foreach(var context in sequenceTasks.Values.Where(c=>!c.Running&&c.FrozenSummary==null).ToArray())
                context.FrozenSummary=InSequence(context,()=>SequenceSummaryCore());
            // Completed details are already durable. Keep a bounded hot cache and load older tabs from their own archive.
            int rows=0,count=0;
            foreach(var context in sequenceTasks.Values.Where(c=>!c.Running&&!c.DetailsArchived).OrderByDescending(c=>c.Ordinal).ToArray())
            {
                rows+=context.Rows.Count;count++;
                if(count<=10&&rows<=20000)continue;
                if(judgmentArchives==null||sequenceHistoryError.Length>0)continue;
                context.Rows.Clear();context.Definition.Rows.Clear();context.DetailsArchived=true;
            }
        }
        internal object SequenceRuntimes()
        {lock(Gate)return new{execute=SequenceSummary("execute"),judge=SequenceSummary("judge")};}
        internal object SequenceTaskList(bool activeOnly=false,IEnumerable<string> retain=null)
        {lock(Gate){RefreshSequenceTasks();IEnumerable<SequenceRuntimeContext> tasks=sequenceTasks.Values;if(activeOnly){var selected=new Dictionary<string,SequenceRuntimeContext>();foreach(var task in captureSequenceTasks)selected[task.RunId]=task;foreach(string id in retain??new string[0]){SequenceRuntimeContext task;if(id!=null&&sequenceTasks.TryGetValue(id,out task))selected[id]=task;}foreach(var child in selected.Values.ToArray()){SequenceRuntimeContext parent;if(child.ParentRunId.Length>0&&sequenceTasks.TryGetValue(child.ParentRunId,out parent))selected[parent.RunId]=parent;}tasks=selected.Values;}return new{session=sequenceSession,revision=sequenceTaskRevision,total=sequenceTasks.Count,active=captureSequenceTasks.Length,rows=tasks.OrderBy(c=>c.Ordinal).Select(c=>InSequence(c,()=>SequenceSummaryCore())).ToArray()};}}
        internal object SequenceSummary(string mode="judge"){return InSequence(mode,()=>SequenceSummaryCore());}
        internal object SequenceSummaryForRun(string runId){lock(Gate)return InSequence(SequenceTask(runId),()=>SequenceSummaryCore());}
        internal object SequenceState(string id,int page,string mode="judge",string runId="")
        {
            SequenceRuntimeContext selected;
            lock(Gate)
            {
                if(runId.Length>0)
                {
                    selected=SequenceTask(runId);
                    id=selected.Definition.Id;
                }
                else
                {
                    var definition=sequenceConfiguration.Sequences.FirstOrDefault(d=>d.Id==id);
                    selected=SequenceContext(definition==null?mode:definition.Mode);
                }
                if(!selected.DetailsArchived||selected.Definition.Id!=id)
                    return InSequence(selected,()=>SequenceStateCore(id,page));
            }
            return ArchivedSequenceState(selected,page);
        }
        internal List<SequenceStep> PrepareSequence(string id,SequenceStartOptions start=null)
        {return InSequence(new SequenceRuntimeContext(),()=>PrepareSequenceCore(id,start));}
        internal string StartSequence(string id,List<SequenceStep> steps,string actor,string sendRevision,string runName=null,SequenceStartOptions start=null,string parentRunId="")
        {
            lock(Gate)
            {
                var context=new SequenceRuntimeContext{Ordinal=++sequenceTaskSerial,RunId=Guid.NewGuid().ToString("N"),ParentRunId=parentRunId,Origin=parentRunId.Length>0?"CALL调用":"手动启动"};
                // Register only this instance; a nested CALL can never replace its parent's state.
                sequenceTasks.Add(context.RunId,context);sequenceRegistryDirty=true;
                try
                {
                    InSequence(context,()=>StartSequenceCore(id,steps,actor,sendRevision,runName,start));
                    sequenceTaskRevision++;RefreshSequenceTasks();return context.RunId;
                }
                catch{sequenceTasks.Remove(context.RunId);context.Running=false;context.Clock.Stop();sequenceRegistryDirty=true;RefreshSequenceTasks();throw;}
            }
        }
        internal void StopSequence(string reason){EachSequence(()=>StopSequenceCore(reason));}
        internal string SequenceModeForRun(string runId){lock(Gate)return SequenceTask(runId).Definition.Mode;}
        internal void StopSequenceRun(string runId,string reason){lock(Gate){InSequence(SequenceTask(runId),()=>StopSequenceCore(reason));RefreshSequenceTasks();}}
        internal void PauseSequence(string runId){lock(Gate){InSequence(SequenceTask(runId),()=>PauseSequenceCore(runId));RefreshSequenceTasks();}}
        internal void ResumeSequence(string runId){lock(Gate){InSequence(SequenceTask(runId),()=>ResumeSequenceCore(runId));RefreshSequenceTasks();}}
        private void EnsureCurrentSequenceIdle(){if(sequenceRunning)throw new InvalidOperationException("该运行任务已启动");}
        private void SequenceTelemetry(string source,HashSet<string> changed)
        {EachSequence(()=>{try{SequenceTelemetryCore(source,changed);}catch(Exception ex){StopSequenceCore("序列判读处理失败："+ex.Message);}});}
        internal SequenceSendWork SequenceTick(string sendRevision)
        {
            lock(Gate)
            {
                if(sequenceHistoryError.Length>0||judgmentArchiveError.Length>0){StopSequence("存档不可用，所有序列任务停止："+sequenceHistoryError+judgmentArchiveError);return null;}
                EachSequence(()=>{try{SequenceTickCore(sendRevision,false);}catch(Exception ex){StopSequenceCore("序列运行异常："+ex.Message);}});
                var active=executingSequenceTasks;
                int previous=Array.FindIndex(active,c=>c.RunId==lastSequenceSendRun);
                for(int offset=1;offset<=active.Length;offset++)
                {
                    var context=active[(previous+offset)%active.Length];
                    var work=InSequence(context,()=>TakeSequenceSendWork());
                    if(work!=null){lastSequenceSendRun=context.RunId;return work;}
                }
                return null;
            }
        }
        internal bool SequenceCanSend(SequenceSendWork work)
        {lock(Gate){SequenceRuntimeContext context;return sequenceTasks.TryGetValue(work.RunId,out context)&&InSequence(context,()=>SequenceCanSendCore(work));}}
        internal void SequenceSent(SequenceSendWork work,bool ok,string reason,double? sentAt=null,string[] eventIds=null)
        {
            lock(Gate)
            {
                SequenceRuntimeContext context;
                if(!sequenceTasks.TryGetValue(work.RunId,out context)||!context.Running||work.Index<0||work.Index>=context.Rows.Count)return;
                InSequence(context,()=>
                {
                    context.Rows[work.Index].HistoryCommandEvents=eventIds??new string[0];
                    SequenceSentCore(work,ok,reason,sentAt);
                });
                RefreshSequenceTasks();
            }
        }

        private long sequenceCallRevision;
        private string sequenceCallSourceRun="",sequenceCallTargetRun="",sequenceCallTargetId="";
        internal object SequenceCallNotice(){lock(Gate)return new{revision=sequenceCallRevision,sourceRunId=sequenceCallSourceRun,targetRunId=sequenceCallTargetRun,targetId=sequenceCallTargetId};}
        private string StartSequenceCall(SequenceStep step)
        {
            string source=sequenceRunId;
            string target=StartSequence(step.NextDefinition.Id,step.NextSteps,sequenceActor,sequenceSendRevision,parentRunId:source);
            sequenceCallSourceRun=source;sequenceCallTargetRun=target;sequenceCallTargetId=step.NextDefinition.Id;sequenceCallRevision++;return target;
        }
        private void LaunchSequenceJudgment(SequenceStepRun row)
        {
            var step=row.Step;
            foreach(var command in SequenceJumpSteps(step.NextSteps).Where(s=>s.Code.Length>0))
                if(SequenceCommandSignature(command,ResolveCommandCode(command.Code))!=command.CommandSignature)throw new InvalidOperationException("CALL目标所用指令定义已变化");
            StartSequenceCall(step);
            row.Reason="已启动独立序列判读 "+step.NextDefinition.Code+"；序列执行继续";
            SequenceAudit("启动判读",row,"已启动",row.Reason);SequenceComplete(row);
        }
    }
}
