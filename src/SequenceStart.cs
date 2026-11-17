using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        internal static SequenceStartOptions ParseSequenceStart(string row,string t0,string f0)
        {
            var start=new SequenceStartOptions();row=(row??"").Trim();
            if(row.Length>0 && (!Int32.TryParse(row,NumberStyles.None,CultureInfo.InvariantCulture,out start.StartRow) || start.StartRow<1))
                throw new ArgumentException("起始行须填写序列中的原表行号（正整数），留空从第一项开始");
            start.T0=ParseSequenceInitialTime(t0,"T0");start.F0=ParseSequenceInitialTime(f0,"F0")??0;
            return start;
        }
        private static double? ParseSequenceInitialTime(string text,string label)
        {
            text=(text??"").Trim();if(text.Length==0)return null;
            var match=Regex.Match(text,@"^"+label+@"\s*[=＝]\s*(.+)$",RegexOptions.IgnoreCase);
            if(match.Success)text=match.Groups[1].Value.Trim();
            double value;
            if(!Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value<0 || value>604800)
                throw new ArgumentException(label+"已过去时间须为0至604800秒，可填数字或"+label+"=数字");
            return value;
        }
        internal static int SequenceStartIndex(List<SequenceStep> steps,SequenceStartOptions start)
        {
            if(steps.Count==0)throw new ArgumentException("序列没有可运行的行");
            if(start==null || start.StartRow==0)return 0;
            int index=steps.FindIndex(s=>s.Row.Row==start.StartRow);
            if(index<0)throw new ArgumentException("起始原表第"+start.StartRow+"行不在当前序列中，请按逐项运行情况中的原行号填写");
            return index;
        }
        private static void ValidateSequenceStart(List<SequenceStep> steps,SequenceStartOptions start)
        {
            if(start==null)start=new SequenceStartOptions();
            if(Double.IsNaN(start.F0) || Double.IsInfinity(start.F0) || start.F0<0 || start.F0>604800 ||
                (start.T0.HasValue && (Double.IsNaN(start.T0.Value) || Double.IsInfinity(start.T0.Value) || start.T0.Value<0 || start.T0.Value>604800)))
                throw new ArgumentException("启动计时须为0至604800秒");
            int index=SequenceStartIndex(steps,start);bool t0=start.T0.HasValue;
            foreach(var step in steps.Skip(index))
            {
                if(step.Absolute && !t0)throw new ArgumentException("原表第"+step.Row.Row+"行使用T0，但之前的T0标志行已跳过；请在启动窗口填写T0已过去秒数，或从T0标志行之前开始");
                if(step.Row.Mark.Equals("T0",StringComparison.OrdinalIgnoreCase))t0=true;
            }
        }
        private void InitializeSequenceStart(List<SequenceStep> steps,SequenceStartOptions options)
        {
            ValidateSequenceStart(steps,options);
            var start=options==null?new SequenceStartOptions():SequenceClone(options);
            int index=SequenceStartIndex(steps,start);start.StartRow=steps[index].Row.Row;CurrentSequence.Start=start;
            sequencePosition=index;sequenceGroup=sequenceGroupEnd=-1;sequencePrevious=start.F0;
            sequenceT0=start.T0.HasValue?(double?)(start.F0-start.T0.Value):null;CurrentSequence.T0Preset=start.T0.HasValue;
            sequenceT0At=start.T0.HasValue?BeijingTime.Now.AddSeconds(-start.T0.Value).ToString("yyyy-MM-dd HH:mm:ss.fff"):"";
            foreach(var row in sequenceRows.Take(index))
            {row.Terminal=true;row.Status="已跳过";row.Reason="从原表第"+start.StartRow+"行启动，此项未执行";}
            if(steps[index].Inside)
            {
                sequenceGroup=steps.FindLastIndex(index-1,s=>s.Row.Keyword=="PAR" && s.Pair>index);
                if(sequenceGroup<0)throw new ArgumentException("起始行的并行段边界不完整");
                sequenceGroupEnd=steps[sequenceGroup].Pair;
            }
        }
        private string SequenceStartDescription()
        {
            var start=CurrentSequence.Start;
            return "从原表第"+start.StartRow+"行启动；F0="+start.F0.ToString(CultureInfo.InvariantCulture)+"s"+
                (start.T0.HasValue?"；T0="+start.T0.Value.ToString(CultureInfo.InvariantCulture)+"s":"；T0按标志建立")+
                "；前"+sequenceRows.Count(r=>r.Status=="已跳过")+"项未执行";
        }
        private string SequenceCompletionStatus()
        {
            if(sequenceRows.Any(r=>r.HadFailure))return "完成，存在异常";
            int skipped=sequenceRows.Count(r=>r.Status=="已跳过");
            return skipped==0?"全部通过":"运行项目全部通过，已跳过"+skipped+"项";
        }
        private void ActivateSequenceStartGroup()
        {
            if(sequenceGroup<0)return;
            foreach(var row in sequenceRows.Skip(sequencePosition).Take(sequenceGroupEnd-sequencePosition))
            {ActivateSequenceRow(row,sequencePrevious);if(!sequenceRunning)return;}
        }
    }
}
