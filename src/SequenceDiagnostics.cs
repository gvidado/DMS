using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private object CaptureSequenceEvaluationDiagnostic(SequenceStepRun row)
        {
            return new{engineBuild=typeof(Monitor).Assembly.ManifestModule.ModuleVersionId.ToString(),evaluationCount=row.EvaluationCount,lastEvaluationAt=row.LastEvaluationAt,lastEvaluationResult=row.LastEvaluationResult,usedReferences=row.UsedReferences,
                commandDone=row.CommandDone,commandDataSince=row.CommandDataSince,commandDataSinceUtc=row.CommandDataSinceUtc,
                parameters=row.Step.Predicate==null?new object[0]:row.Step.Predicate.Fields.Select(p=>SequenceParameterDiagnostic(row,p.Key,p.Value)).ToArray()};
        }
        private object SequenceParameterDiagnostic(SequenceStepRun row,string reference,BoundTelemetry bound)
        {
            var h=RuleHistory(bound.Field,bound.Source);var latest=h==null?null:h.Latest;Reading displayed;Values.TryGetValue(bound.Field.Id,out displayed);
            long baseline;row.Baselines.TryGetValue(reference,out baseline);string block=SequenceInputBlock(row,reference,bound);
            return new{reference,name=bound.Field.Name,sourceIP=bound.Source,packet=bound.Field.PacketName??"",value=latest==null?"":latest.EngineeringText,
                status=latest==null?"未接收":DisplayReadingStatus(latest),time=latest==null?"":BeijingTime.Format(latest.Time,"yyyy-MM-dd HH:mm:ss.fff"),
                timeUtc=latest==null?"":BeijingTime.Iso(latest.Time),ageSeconds=latest==null?null:(double?)(HistoryAgeMs(h)/1000),frame=latest==null?null:(int?)latest.Sequence,
                sample=h==null||h.Samples.Count==0?0:h.Samples[0].sampleNumber,baseline,eligibleForStep=block.Length==0,blockedReason=block,
                latestDisplayedSource=displayed==null?"":displayed.SourceIP,latestDisplayedValue=displayed==null?"":displayed.EngineeringText,
                displaySourceMatchesBinding=displayed!=null && displayed.SourceIP==bound.Source};
        }
        internal object SequenceDiagnostic(string id,string mode,string runId="")
        {
            lock(Gate)
            {
                var context=runId.Length>0?SequenceTask(runId):SequenceContext(SequenceDefinition(id).Mode);
                if(context.DetailsArchived)return new{runtime=context.FrozenSummary,note="本次运行已结束，以下为保存时的判读诊断，不使用当前参数代替历史值。",rows=judgmentArchives.ReadSequenceRun(context.RunId).Where(e=>e.Diagnostic!=null).Select(e=>new{e.Row,e.Event,e.Status,e.Values,e.Diagnostic}).ToArray()};
                var definition=runId.Length>0?context.Definition:SequenceDefinition(id);id=definition.Id;
                return InSequence(context,()=>
                {
                    bool current=sequenceDefinition!=null && sequenceDefinition.Id==id;
                    return (object)new{generatedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),engineBuild=typeof(Monitor).Assembly.ManifestModule.ModuleVersionId.ToString(),
                        note="参数状态为导出瞬间的快照；lastValues为最近一次判读或超时保存值。时间标签用于查看，过期判定使用持续计时。",
                        runtimeMatchesSelection=current,definition=current?SequenceClone(sequenceDefinition):SequenceClone(definition),runtime=SequenceSummaryCore(),
                        reception=new{listening=Listening,mode=packetStatisticsMode,dataTimeoutMs=Config.DataTimeoutMs,received=Received,accepted=Accepted,duplicates=Duplicates,invalid=Invalid,filtered=Filtered},
                        rows=current?sequenceRows.Select(row=>new{row=row.Step.Row.Row,condition=row.Step.Row.Condition,command=row.Step.Row.Command,status=row.Status,reason=row.Reason,
                            gate=row.Terminal?"本项已结束":sequencePaused?"序列已暂停":!row.Begun?"尚未到本项执行时间":!row.CommandDone?"指令尚未完成，参数暂不判读":"等待或检查参数",
                            timeout=row.Step.Timeout,planned=row.Planned,actual=row.Actual,commandDone=row.CommandDone,commandDataSince=row.CommandDataSince,commandDataSinceUtc=row.CommandDataSinceUtc,
                            evaluationCount=row.EvaluationCount,lastEvaluationAt=row.LastEvaluationAt,lastEvaluationResult=row.LastEvaluationResult,usedReferences=row.UsedReferences,
                            lastValues=new Dictionary<string,object>(row.Values),parameters=row.Step.Predicate==null?new object[0]:row.Step.Predicate.Fields.Select(p=>SequenceParameterDiagnostic(row,p.Key,p.Value)).ToArray()}).ToArray():new object[0]};
                });
            }
        }
    }
}
