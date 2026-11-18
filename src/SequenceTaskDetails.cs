using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private SequenceRowResult SequenceRowView(SequenceStepRun run)
        {
            return new SequenceRowResult{input=run.Step.Row,status=run.Status,reason=run.Reason,values=new Dictionary<string,object>(run.Values),timeout=run.Step.Timeout,
                remaining=run.Begun&&!run.Terminal?(double?)Math.Max(0,(run.Step.Hold?run.LastValid:run.StartTime)+run.Step.Timeout-SequenceNow):null,
                planned=run.Planned,actual=run.Actual,delay=run.Delay,startedAt=run.StartedAt,finishedAt=run.FinishedAt,shipTime=run.ShipTime,
                evaluation=new{count=run.EvaluationCount,lastAt=run.LastEvaluationAt,result=run.LastEvaluationResult,used=run.UsedReferences},
                abnormal=run.HadFailure,commandName=run.Step.CommandName,destinations=run.Step.Destinations};
        }
        private object ArchivedSequenceState(SequenceRuntimeContext context,int page)
        {
            var events=judgmentArchives.ReadSequenceRun(context.RunId);
            var definitionEvent=events.LastOrDefault(e=>e.Definition!=null);
            if(definitionEvent==null)throw new InvalidOperationException("该运行存档缺少原始序列定义，请核对原始历史记录");
            var definition=definitionEvent.Definition;
            int count=definition.Rows.Count,pages=Math.Max(1,(count+49)/50);page=Math.Max(1,Math.Min(pages,page));
            var results=events.Where(e=>e.Event=="步骤结果"&&e.Result!=null).GroupBy(e=>e.Row).ToDictionary(g=>g.Key,g=>g.Last().Result);
            var rows=definition.Rows.Skip((page-1)*50).Take(50).Select(row=>results.ContainsKey(row.Row)?results[row.Row]:new SequenceRowResult{input=row,status="记录不完整",reason="未找到该行最终结果，请查看原始存档"}).ToArray();
            return new{runtime=context.FrozenSummary,runtimes=SequenceRuntimes(),call=SequenceCallNotice(),definition=new{definition.Id,definition.Code,definition.Name,definition.Mode,definition.SourceIP},rows,page,pages,total=count};
        }
    }
}
