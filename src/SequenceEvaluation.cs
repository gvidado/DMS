using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private string SequenceInputBlock(SequenceStepRun row,string reference,BoundTelemetry bound)
        {
            var h=RuleHistory(bound.Field,bound.Source);
            if(h==null || h.Latest==null)return "未接收";
            if(h.Latest.Status!="有效" || h.Latest.EngineeringValue==null)return "数据无效："+h.Latest.Status+(String.IsNullOrEmpty(h.Latest.Diagnostic)?"":" / "+h.Latest.Diagnostic);
            if(HistoryAgeMs(h)>Config.DataTimeoutMs)return "数据过期";
            if(h.Samples.Count==0)return "未接收";
            var sample=h.Samples[0];
            if(row.CommandDataSince.HasValue && sample.observedMonotonic<row.CommandDataSince.Value)return "指令发出后无新数据";
            if(row.CommandDataSinceUtc.HasValue)
            {
                DateTime observed;
                if(!DateTime.TryParse(sample.timeUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out observed) || observed.ToUniversalTime()<row.CommandDataSinceUtc.Value)return "监视到的指令发出后无新数据";
            }
            return "";
        }
        private string SequenceInputWait(SequenceStepRun row,string reference,BoundTelemetry bound)
        {
            var h=RuleHistory(bound.Field,bound.Source);
            string block=SequenceInputBlock(row,reference,bound);
            return reference+"："+block+"；绑定来源 "+bound.Source+
                (h==null || h.Latest==null?"":"；最近接收 "+BeijingTime.Format(h.Latest.Time,"yyyy-MM-dd HH:mm:ss.fff")+"，帧计数 "+h.Latest.Sequence+"，距接收 "+(HistoryAgeMs(h)/1000).ToString("0.###",CultureInfo.InvariantCulture)+"s / 时限 "+(Config.DataTimeoutMs/1000.0).ToString(CultureInfo.InvariantCulture)+"s");
        }
        private void EvaluateSequenceRow(SequenceStepRun row)
        {
            if(!sequenceRunning || sequencePaused || !row.Begun || row.Terminal || !row.CommandDone || row.Step.Predicate==null)return;
            CheckSequenceDeadline(row);if(!sequenceRunning || row.Terminal)return;
            row.EvaluationCount++;row.LastEvaluationAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            CaptureSequenceParameterValues(row);var values=new Dictionary<string,object>();
            Func<string,object> read=reference=>
            {
                object value;if(values.TryGetValue(reference,out value))return value;
                var bound=row.Step.Predicate.Fields[reference];
                if(SequenceInputBlock(row,reference,bound).Length>0)throw new RuleWaiting(SequenceInputWait(row,reference,bound));
                value=RuleValue(bound.Field,bound.Source);values[reference]=value;return value;
            };
            try
            {
                var evaluation=new RuleEvaluation{Read=read,PartialLogic=true,InEnum=(name,value)=>
                {var p=row.Step.Predicate.Fields[name].Field;decimal n=RuleEvaluation.Number(value);return (p.Enums??new Dictionary<string,string>()).Keys.Any(k=>n==Decimal.Parse(k,CultureInfo.InvariantCulture)*Convert.ToDecimal(p.Scale)+Convert.ToDecimal(p.Bias)) || (p.EnumRanges??new List<EnumRange>()).Any(r=>n>=Convert.ToDecimal(r.Min)&&n<=Convert.ToDecimal(r.Max));}};
                bool satisfied=RuleEvaluation.Truth(row.Step.Predicate.Expression.Get(evaluation));
                // A constant short circuit still requires a usable referenced value.
                if(values.Count==0)
                {
                    foreach(string name in row.Step.Predicate.Fields.Keys){try{read(name);break;}catch(RuleWaiting){}}
                    if(values.Count==0)throw new RuleWaiting("等待有效参数数据");
                }
                row.FreshObserved=true;row.LastValid=SequenceNow;row.LastEvaluationResult=satisfied?"满足":"不满足";row.UsedReferences=values.Keys.ToArray();
                if(row.Step.Hold)
                {if(!satisfied)SequenceFail(row,"异常","HOLD持续判据不满足");else{row.Episode=false;row.Status=row.HadFailure?"持续检查（曾异常）":"持续检查";row.Reason=row.HadFailure?"已恢复，保留本次异常":"";}}
                else if(satisfied)SequenceComplete(row);else{row.Status="等待参数";row.Reason="条件尚未满足";}
            }
            catch(RuleWaiting ex){row.LastEvaluationResult="等待数据";row.Reason=ex.Message;}
            catch(Exception ex){row.LastEvaluationResult="计算异常";row.UsedReferences=values.Keys.ToArray();SequenceFail(row,"异常","表达式计算异常："+ex.Message);}
            finally{row.UsedReferences=values.Keys.ToArray();}
        }
    }
}
