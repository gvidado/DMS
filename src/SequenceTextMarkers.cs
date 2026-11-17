using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private static void CompileSequenceTiming(SequenceStep step,string mode,ref bool markSeen,List<string> warnings,string prefix)
        {
            var row=step.Row;bool text=row.Keyword=="TEXT";
            if(row.Expected.Length==0)step.Delay=!text&&mode=="execute"?SequenceExpectedDefault:0;
            else
            {
                var timing=Regex.Match(row.Expected,@"^(T0|F)\s*[:：]\s*(.+)$",RegexOptions.IgnoreCase);
                step.Absolute=timing.Success&&timing.Groups[1].Value.Equals("T0",StringComparison.OrdinalIgnoreCase);
                step.FromStart=timing.Success&&timing.Groups[1].Value.Equals("F",StringComparison.OrdinalIgnoreCase);
                step.Delay=SequenceSeconds(timing.Success?timing.Groups[2].Value:row.Expected);
                if(step.Absolute&&!markSeen)throw new ArgumentException("T0引用之前必须有一行成功建立T0，不能自引用或前向引用");
                if(!text&&mode=="execute"&&!step.Absolute&&!step.FromStart&&step.Delay<SequenceMinimumInterval)
                {step.Delay=SequenceMinimumInterval;warnings.Add(prefix+"相对期望时间小于0.2秒，按0.2秒执行");}
            }
            if(row.Mark.Length>0)
            {
                if(!row.Mark.Equals("T0",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("当前标志仅支持T0");
                if(step.Inside)throw new ArgumentException("T0请在串行或PAR/CHANGE边界行建立，不能在并行段内建立");
                if(markSeen)throw new ArgumentException("同一序列不能重复定义T0");markSeen=true;
            }
        }
        private void CompleteSequenceText(SequenceStepRun row)
        {
            if(row.Terminal)return;
            // Only untimed trailing notes after a terminal CALL enter without normal scheduling.
            if(!row.Begun)
            {
                row.Activated=row.Begun=true;row.ActivatedAt=row.StartTime=SequenceNow;row.Actual=row.StartTime;
                row.Planned=row.StartTime;row.Delay=0;row.StartedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            }
            SequenceComplete(row);
        }
    }
}
