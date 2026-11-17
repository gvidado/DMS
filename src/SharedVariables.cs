using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class SharedVariableUsage
    {
        public string Name;
        public readonly List<string> Readers=new List<string>(), Writers=new List<string>();
    }
    internal sealed class SharedVariableStamp
    { public long Revision; public string Time="", Actor=""; }
    public sealed partial class Monitor
    {
        private readonly Dictionary<string,object> preparedGlobals=new Dictionary<string,object>();
        private readonly Dictionary<string,SharedVariableStamp> globalStamps=new Dictionary<string,SharedVariableStamp>();
        private long globalRevision;
        private string globalUsageRevision=null;
        private readonly Dictionary<string,SharedVariableUsage> globalUsage=new Dictionary<string,SharedVariableUsage>();
        private readonly List<string> globalUsageErrors=new List<string>();
        private void RefreshSharedUsage()
        {
            if(globalUsageRevision==continuous.Revision)return;
            globalUsage.Clear();globalUsageErrors.Clear();
            foreach(var rule in continuous.Rules.Where(r=>!String.IsNullOrWhiteSpace(r.Script)))
            {
                try
                {
                    var program=RuleParser.Compile(rule.Script);
                    string label=TelemetryReferences.Name(rule.Code)+" / "+rule.Name+(rule.Enabled?"":"（已停用）");
                    foreach(string name in program.GlobalReads.Concat(program.GlobalWrites).Distinct())
                    {
                        SharedVariableUsage usage;if(!globalUsage.TryGetValue(name,out usage))globalUsage[name]=usage=new SharedVariableUsage{Name=name};
                        if(program.GlobalReads.Contains(name))usage.Readers.Add(label);
                        if(program.GlobalWrites.Contains(name))usage.Writers.Add(label);
                    }
                }
                catch(Exception ex){globalUsageErrors.Add(rule.Code+"："+ex.Message);}
            }
            globalUsageRevision=continuous.Revision;
        }
        private static string SharedValueText(object value)
        {return value==null?"NIL":value is bool?(bool)value?"true":"false":Convert.ToString(value,CultureInfo.InvariantCulture);}
        private void WriteSharedValue(string name,object value,string actor,bool force=false)
        {
            object previous;bool changed=!judgmentGlobals.TryGetValue(name,out previous) || !Object.Equals(previous,value);
            judgmentGlobals[name]=value;
            if(changed || force)globalStamps[name]=new SharedVariableStamp{Revision=++globalRevision,Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),Actor=actor};
            if(changed)dirtyGlobals.Add(name);
        }
        private void ClearSharedValues()
        {judgmentGlobals.Clear();preparedGlobals.Clear();globalStamps.Clear();dirtyGlobals.Clear();globalRevision++;}
        public object SharedVariableList()
        {
            lock(Gate)
            {
                RefreshSharedUsage();var rows=new List<object>();
                foreach(var usage in globalUsage.Values.OrderBy(u=>u.Name,StringComparer.Ordinal))
                {
                    object value;bool hasValue=judgmentGlobals.TryGetValue(usage.Name,out value);SharedVariableStamp stamp;globalStamps.TryGetValue(usage.Name,out stamp);
                    rows.Add(new{name=usage.Name,hasValue,value,valueText=SharedValueText(value),kind=value==null?"nil":value is bool?"boolean":value is string?"result":"number",
                        revision=stamp==null?globalRevision:stamp.Revision,time=stamp==null?"":stamp.Time,actor=stamp==null?"":stamp.Actor,
                        readers=usage.Readers,writers=usage.Writers,prepared=preparedGlobals.ContainsKey(usage.Name)});
                }
                return new{canModify=Listening || Demo,running=judgmentRunning,generation,runId=judgmentRunId,rows,errors=globalUsageErrors.ToArray()};
            }
        }
        public void SetSharedVariable(string name,string kind,string text,string revision,string sessionGeneration,string actor)
        {
            lock(Gate)
            {
                if(!Listening && !Demo)throw new InvalidOperationException("请先启动参数监听或解码预览，再修改共享变量");
                RefreshSharedUsage();if(!globalUsage.ContainsKey(name??""))throw new ArgumentException("共享变量不存在，请先在规则中使用gz前缀变量");
                SharedVariableStamp stamp;globalStamps.TryGetValue(name,out stamp);
                long expected,current=stamp==null?globalRevision:stamp.Revision,expectedGeneration;
                if(!Int64.TryParse(revision,out expected) || expected!=current || !Int64.TryParse(sessionGeneration,out expectedGeneration) || expectedGeneration!=generation)
                    throw new InvalidOperationException("变量或监听状态已变化，请刷新后重新修改");
                object value;
                if(kind=="nil")value=null;
                else if(kind=="boolean"){bool flag;if(!Boolean.TryParse(text,out flag))throw new ArgumentException("布尔状态应为true或false");value=flag;}
                else if(kind=="result"){if(!new[]{"Normal","Unusual","Error","SeriousError"}.Contains(text))throw new ArgumentException("判读状态应为Normal、Unusual、Error或SeriousError");value=text;}
                else if(kind=="number"){decimal number;if((text??"").Length>128 || !Decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out number))throw new ArgumentException("请输入有效数值，使用英文小数点");value=number;}
                else throw new ArgumentException("共享变量类型应为数值、布尔、判读状态或NIL");
                object old;judgmentGlobals.TryGetValue(name,out old);
                WriteSharedValue(name,value,"人工："+actor,true);dirtyGlobals.Add(name);
                if(!judgmentRunning)preparedGlobals[name]=value;
                Note("info","共享变量人工修改："+name+"，"+SharedValueText(old)+" → "+SharedValueText(value)+"，操作人："+actor);
                if(judgmentRunning)ProcessJudgmentEvent(new JudgmentEvent{ExternalGlobals=new HashSet<string>{name}});
            }
        }
    }
}
