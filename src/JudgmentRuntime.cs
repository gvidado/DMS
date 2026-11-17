using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace TelemetryMonitor
{
    internal sealed class TemporalWindow
    {
        public double? Start, Due;
        public decimal? Amount;
        public long LastSample;
        public int Count;
        public bool Value;
        public string Waiting;
        public readonly Dictionary<string,HashSet<string>> Blocked = new Dictionary<string,HashSet<string>>();
    }
    internal sealed class JudgmentEvent
    {
        public string Source="",Command="",CommandEventId="";
        public HashSet<string> Changed=new HashSet<string>();
        public HashSet<string> ExternalGlobals=new HashSet<string>();
        public HashSet<string> CommandCodes=new HashSet<string>();
        public bool Timer,Start;
        public double Observed;
    }
    public sealed partial class Monitor
    {
        private volatile bool judgmentRunning;
        private Timer judgmentTimer;
        private double judgmentStarted, judgmentElapsed;
        private bool judgmentReplay;
        private string judgmentStartedAt="",judgmentReason="尚未启动判读",judgmentRunId="";
        private long judgmentGeneration;
        private readonly Dictionary<string,object> judgmentGlobals=new Dictionary<string,object>();
        private readonly HashSet<string> dirtyGlobals=new HashSet<string>();
        public static double RuleSeconds(){return Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;}
        private double JudgmentSeconds(){return judgmentReplay?ReplayPositionMs()/1000:RuleSeconds();}
        private double HistorySeconds(){return packetStatisticsMode=="demo"?ReplayPositionMs()/1000:RuleSeconds();}
        private decimal ElapsedJudgment(){return Convert.ToDecimal(judgmentRunning?JudgmentSeconds()-judgmentStarted:judgmentElapsed);}
        private object RuleClock(string name)
        {
            if(name=="OSTIME")return ElapsedJudgment();DateTime now=BeijingTime.Now;
            switch(name){case "YEAR":return (decimal)now.Year;case "MONTH":return (decimal)now.Month;case "DAY":return (decimal)now.Day;case "HOUR":return (decimal)now.Hour;case "MINUTE":return (decimal)now.Minute;default:return (decimal)now.Second;}
        }
        public object JudgmentInfo()
        {lock(Gate)return new{running=judgmentRunning,runId=judgmentRunId,startedAt=judgmentStartedAt,elapsedSeconds=ElapsedJudgment(),reason=judgmentReason,globals=new Dictionary<string,object>(judgmentGlobals)};}
        private void InitializeJudgmentTimer(){judgmentTimer=new Timer(JudgmentTick,null,50,50);}
        private void JudgmentTick(object ignored)
        {
            lock(Gate)
            {
                if(!judgmentRunning)return;
                if(!Listening && !Demo){StopJudgmentCore("参数监听或解码预览已结束");return;}
                try{ProcessJudgmentEvent(new JudgmentEvent{Timer=true});}
                catch(Exception ex){StopJudgmentCore("判读调度异常："+ex.Message);RecordAlarm("error","判读运行异常",ex.Message);}
            }
        }
        public void StartJudgment()
        {
            lock(Gate)
            {
                if(judgmentRunning)throw new InvalidOperationException("判读已在运行");
                if(!Listening && !Demo)throw new InvalidOperationException("请先启动参数监听或正在运行的解码预览，再启动判读");
                if(ruleStorageError.Length>0)throw new InvalidOperationException(ruleStorageError);
                if(judgmentArchives==null || judgmentArchiveError.Length>0)throw new InvalidOperationException(judgmentArchiveError);
                if(judgmentAbnormals==null || judgmentAbnormalStorageError.Length>0)throw new InvalidOperationException(judgmentAbnormalStorageError.Length>0?judgmentAbnormalStorageError:"判读异常历史不可用");
                var enabled=continuous.Rules.Where(r=>r.Enabled).OrderBy(r=>r.Code,StringComparer.Ordinal).ToArray();
                if(enabled.Length==0)throw new ArgumentException("请先启用至少一条判读规则");
                var errors=new List<string>();var writers=new Dictionary<string,string>();var reads=new HashSet<string>();
                foreach(var r in enabled)
                {
                    try
                    {
                        var b=Binding(r);
                        foreach(var telemetry in b.Fields.Values.Concat(b.Anchors.Values).Concat(new[]{new BoundTelemetry{Field=b.Main,Source=b.Source}}))
                        {
                            if(NativeDecoder && !telemetry.Field.PacketEnabled)throw new ArgumentException("绑定参数所属SPP未启用："+telemetry.Field.Reference);
                            if(!NativeDecoder && !Protocol.InProfile(telemetry.Field,Config.Profile))throw new ArgumentException("绑定参数不在当前解码布局："+telemetry.Field.Reference);
                            if(!Demo && (NativeDecoder?!ReadyFor(telemetry.Field.Id):!Config.MappingConfirmed))throw new ArgumentException("绑定参数的解码映射尚未确认："+telemetry.Field.Reference);
                        }
                        foreach(string global in b.Program.GlobalWrites){string old;if(writers.TryGetValue(global,out old) && old!=r.Id)throw new ArgumentException("全局变量"+global+"存在多个写入规则");writers[global]=r.Id;}
                        reads.UnionWith(b.Program.GlobalReads);
                    }
                    catch(Exception ex){errors.Add(r.Code+"："+ex.Message);}
                }
                foreach(string global in reads.Where(g=>!writers.ContainsKey(g) && !preparedGlobals.ContainsKey(g)))errors.Add("共享变量未在启用规则中定义且未人工赋值："+global);
                try{CheckCommandCapture(enabled.SelectMany(r=>Binding(r).Program.Commands));}catch(Exception ex){errors.Add(ex.Message);}
                if(errors.Count>0)throw new ArgumentException("启动检查失败："+String.Join("；",errors.Take(20)));
                // Reject inter-rule global cycles; a rule may read its own persistent global value.
                var graph=enabled.ToDictionary(r=>r.Id,r=>new HashSet<string>(Binding(r).Program.GlobalReads.Where(writers.ContainsKey).Select(g=>writers[g]).Where(id=>id!=r.Id)));
                while(graph.Count>0){var leaves=graph.Where(p=>p.Value.Count==0).Select(p=>p.Key).ToArray();if(leaves.Length==0)throw new ArgumentException("规则之间存在全局变量循环依赖");foreach(string id in leaves){graph.Remove(id);foreach(var dependencies in graph.Values)dependencies.Remove(id);}}
                ruleRuns.Clear();var initialGlobals=new Dictionary<string,object>(preparedGlobals);ClearSharedValues();foreach(var initial in initialGlobals)WriteSharedValue(initial.Key,initial.Value,"人工预设");dirtyGlobals.Clear();judgmentReplay=Demo;judgmentStarted=JudgmentSeconds();judgmentElapsed=0;judgmentStartedAt=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");judgmentRunId=Guid.NewGuid().ToString("N");judgmentReason="运行中";
                Interlocked.Increment(ref judgmentGeneration);judgmentRunning=true;
                ProcessJudgmentEvent(new JudgmentEvent{Start=true});
                Note("info","常态化判读已启动："+enabled.Length+"条规则；运行号"+judgmentRunId);
            }
        }
        public void StopJudgment(){lock(Gate)StopJudgmentCore("已手动停止判读，参数监听继续");}
        private void StopJudgmentCore(string reason)
        {
            if(!judgmentRunning)return;
            judgmentElapsed=JudgmentSeconds()-judgmentStarted;judgmentRunning=false;judgmentDispatch=null;Interlocked.Increment(ref judgmentGeneration);judgmentReason=reason;dirtyGlobals.Clear();
            foreach(var state in ruleRuns.Values)state.Windows.Clear();
            Note("info",reason);
        }
        private void PrepareTemporal(RuleBinding b,RuleRunState state,RuleEvaluation e,JudgmentEvent input,string phase)
        {
            double now=JudgmentSeconds()-judgmentStarted;
            foreach(var spec in b.Program.Timers.Where(t=>t.Phase==phase))
            {
                TemporalWindow w;if(!state.Windows.TryGetValue(spec.Id,out w))state.Windows[spec.Id]=w=new TemporalWindow();
                w.Value=false;w.Waiting=null;
                try
                {
                    decimal amount=RuleEvaluation.Number(spec.Amount.Get(e));
                    if(amount<0 || amount>6000000 || (spec.Kind=="FRAMES" && (amount<1 || Decimal.Truncate(amount)!=amount)))throw new ArgumentException(spec.Kind+"的时间或帧数无效");
                    if(w.Amount!=amount){w.Start=null;w.Due=null;w.Count=0;w.Amount=amount;}
                    if(spec.Kind=="AFTER")
                    {
                        if(!w.Due.HasValue && RuleEvaluation.Truth(spec.Condition.Get(e)))w.Due=(input.Observed>0?input.Observed-judgmentStarted:now)+(double)amount;
                        if(w.Due.HasValue && now>=w.Due.Value){w.Value=true;w.Due=null;}
                        continue;
                    }
                    if(w.Blocked.Count>0)
                    {
                        HashSet<string> blocked;
                        if(w.Blocked.TryGetValue(input.Source,out blocked) && input.Changed.Overlaps(blocked))
                        {foreach(string id in blocked){var field=RuntimeField(id);if(field!=null)RuleValue(field,input.Source);}w.Blocked.Remove(input.Source);}
                        if(w.Blocked.Count>0)throw new RuleWaiting("等待异常后的新有效样本");
                    }
                    bool condition=RuleEvaluation.Truth(spec.Condition.Get(e));
                    if(spec.Kind=="HOLD")
                    {if(!condition){w.Start=null;continue;}if(!w.Start.HasValue)w.Start=now;w.Value=now-w.Start.Value>=(double)amount;continue;}
                    var anchor=b.Anchors[spec.Id];RuleValue(anchor.Field,anchor.Source);var history=RuleHistory(anchor.Field,anchor.Source);long sample=history.Samples[0].sampleNumber;
                    if(!condition){w.Count=0;w.LastSample=sample;continue;}
                    if(input.Start)w.LastSample=sample;
                    if(input.Source==anchor.Source && input.Changed.Contains(anchor.Field.Id) && sample!=w.LastSample){w.Count=Math.Min(w.Count+1,Int32.MaxValue-1);w.LastSample=sample;}
                    w.Value=w.Count>=amount;
                }
                catch(RuleWaiting ex){w.Start=null;w.Count=0;w.Waiting=ex.Message;}
            }
        }
        private void ProcessJudgmentEvent(JudgmentEvent input)
        {
            try{ProcessJudgmentEventCore(input);}finally{FlushJudgmentArchive();}
        }
        private void ProcessJudgmentEventCore(JudgmentEvent input)
        {
            if(!judgmentRunning)return;
            var globalSnapshot=new Dictionary<string,object>(judgmentGlobals);var globalWrites=new Dictionary<string,object>();var globalsChanged=new HashSet<string>(dirtyGlobals);dirtyGlobals.Clear();
            foreach(var entry in JudgmentCandidates(input,globalsChanged))
            {
                var r=entry.Rule;var b=entry.Binding;
                bool changed=(input.Source==b.Source && input.Changed.Contains(b.Main.Id)) || b.Fields.Values.Concat(b.Anchors.Values).Any(f=>f.Source==input.Source && input.Changed.Contains(f.Field.Id));
                bool globalChange=b.Program.GlobalReads.Any(g=>globalsChanged.Contains(g) && (!b.Program.GlobalWrites.Contains(g) || input.ExternalGlobals.Contains(g)));
                bool commandChange=(input.Command.Length>0 && b.Program.Commands.Contains(input.Command)) || b.Program.Commands.Overlaps(input.CommandCodes);
                if(!input.Start && !changed && !globalChange && !commandChange && !(input.Timer && (b.Program.UsesClock || b.Program.Timers.Count>0)))continue;
                RuleRunState state;if(!ruleRuns.TryGetValue(r.Id,out state))ruleRuns[r.Id]=state=new RuleRunState();
                var pending=new Dictionary<string,object>();
                try
                {
                    state.UsedReferences.Clear();var e=Evaluation(b,state,input,globalSnapshot,pending);
                    if(!state.Initialized){state.Variables["this.state"]=0m;RuleParser.Run(b.Program.Init,e);state.Initialized=true;}
                    PrepareTemporal(b,state,e,input,"state");RuleParser.Run(b.Program.State,e);
                    // State changes are valid even if a parameter has not arrived yet.
                    e=Evaluation(b,state,input,globalSnapshot,pending);PrepareTemporal(b,state,e,input,"result");
                    RuleValue(b.Main,b.Source);RuleParser.Run(b.Program.Result,e);string result=e.Conclusion();
                    state.Result=result;state.Status="已判读";state.Detail="";state.Time=BeijingTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var h=RuleHistory(b.Main,b.Source);state.ValueText=h.Samples[0].valueText;
                    if(result=="Normal"){state.AbnormalMarker="";state.ArchivedIncident=null;}
                    else if(!CaptureJudgmentAbnormal(r,b,state,result,SharedValueText(e.Target),""))return;
                    if(result!="Normal" && state.LastAlarm!=result)RecordAlarm(result=="Unusual"?"warn":"error","常态化判读",r.Level+" / "+b.Main.Reference+" / "+r.Name+"："+result+"，工程值="+state.ValueText,b.Source,b.Main.PacketName??"",-1,h.Latest.Sequence,identity:JsonFile.Encode(new[]{r.Id,b.Main.Id,result}));
                    state.LastAlarm=result=="Normal"?"":result;
                }
                catch(RuleWaiting ex){state.Status="等待数据";state.Result="";state.Detail=ex.Message;state.LastAlarm="";}
                catch(Exception ex){state.Status="执行异常";state.Result="";state.Detail=ex.Message;var h=RuleHistory(b.Main,b.Source);state.ValueText=h!=null && h.Latest!=null && h.Latest.Status=="有效" && h.Samples.Count>0?h.Samples[0].valueText:"";if(!CaptureJudgmentAbnormal(r,b,state,"执行异常",state.ValueText,ex.Message))return;if(state.LastAlarm!="runtime")RecordAlarm("error","判读规则异常",r.Level+" / "+r.Code+"："+ex.Message,b.Source,identity:JsonFile.Encode(new[]{r.Id,b.Main.Id,ex.Message}));state.LastAlarm="runtime";}
                if(state.Status=="已判读" && state.Result=="Normal" && !ArchiveNormal(r,b,state))return;
                foreach(var write in pending)globalWrites[write.Key]=write.Value;
            }
            foreach(var write in globalWrites)WriteSharedValue(write.Key,write.Value,"规则赋值");
        }
        private void BreakJudgmentContinuity(string source,string packetId)
        {
            if(!judgmentRunning)return;
            if(!NativeDecoder){if(!String.IsNullOrEmpty(packetId) && packetId!="legacy" && packetId!="legacy-apid-"+Config.ParameterApid)return;packetId="";}
            foreach(var r in continuous.Rules.Where(r=>r.Enabled))
            {
                RuleRunState state;if(!ruleRuns.TryGetValue(r.Id,out state))continue;
                RuleBinding b;try{b=Binding(r);}catch(Exception){continue;}
                bool affected=(b.Source==source && (String.IsNullOrEmpty(packetId) || b.Main.PacketId==packetId)) || b.Fields.Values.Concat(b.Anchors.Values).Any(f=>f.Source==source && (String.IsNullOrEmpty(packetId) || f.Field.PacketId==packetId));
                if(!affected)continue;
                var fields=b.Fields.Values.Concat(b.Anchors.Values).Concat(new[]{new BoundTelemetry{Field=b.Main,Source=b.Source}}).Where(f=>f.Source==source && (String.IsNullOrEmpty(packetId) || f.Field.PacketId==packetId)).Select(f=>f.Field.Id).ToArray();
                foreach(var spec in b.Program.Timers.Where(t=>t.Kind!="AFTER")){TemporalWindow w;if(state.Windows.TryGetValue(spec.Id,out w)){w.Start=null;w.Count=0;w.Value=false;HashSet<string> blocked;if(!w.Blocked.TryGetValue(source,out blocked))w.Blocked[source]=blocked=new HashSet<string>();blocked.UnionWith(fields);}}
            }
        }
    }
}
