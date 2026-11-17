using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace TelemetryMonitor
{
    public sealed class TCBinding { public string Code="";public long Number; }
    public sealed class TCBindingConfig { public int SchemaVersion=1;public string Revision="";public List<TCBinding> Bindings=new List<TCBinding>(); }
    public sealed partial class Monitor
    {
        private TCBindingConfig tcBindings=new TCBindingConfig();
        private string commandBindingRevision="",tcStorageError="";
        private readonly object commandQueueGate=new object();
        private readonly Queue<Tuple<SendRecord,long,double>> commandQueue=new Queue<Tuple<SendRecord,long,double>>();
        private bool commandPump,commandOverflow;
        private long commandQueueBytes;
        private string TCPath {get{return Path.Combine(root,"config","tc-bindings.json");}}
        private void LoadTCBindings()
        {
            if(File.Exists(TCPath))try{tcBindings=JsonFile.Load<TCBindingConfig>(TCPath);ValidateTCConfig(tcBindings);commandBindingRevision=tcBindings.Revision;}catch(Exception ex){tcBindings=new TCBindingConfig();tcStorageError="TC绑定配置读取失败，原文件保留："+ex.Message;Note("warn",tcStorageError);}
            Outgoing.Captured+=QueueCapturedCommand;
        }
        private static void ValidateTCConfig(TCBindingConfig config)
        {
            if(config==null || config.SchemaVersion!=1 || config.Bindings==null || config.Bindings.Count>5000)throw new ArgumentException("TC绑定格式无效");
            if(config.Bindings.Any(b=>b==null || b.Number<1 || !Regex.IsMatch(b.Code??"",@"^[\p{L}_][\p{L}\p{Nd}_]{0,63}$")) || config.Bindings.Select(b=>b.Code).Distinct().Count()!=config.Bindings.Count || config.Bindings.Select(b=>b.Number).Distinct().Count()!=config.Bindings.Count)throw new ArgumentException("TC代号或指令记录号无效、重复");
        }
        private void ValidateRuleCommands(IEnumerable<string> commands)
        {
            var names=commands.ToArray();if(names.Length==0)return;if(tcStorageError.Length>0)throw new ArgumentException(tcStorageError);
            foreach(string name in names)ResolveCommandCode(name);
        }
        private ObservedCommand ResolveCommandCode(string name)
        {
            string code=name.StartsWith("TC.",StringComparison.Ordinal)?name.Substring(3):name;var entries=Outgoing.Commands.BindingEntries();var automatic=entries.FirstOrDefault(e=>e.Categories.Any(c=>c.Code==code));var binding=tcBindings.Bindings.FirstOrDefault(b=>b.Code==code);var manual=binding==null?null:entries.FirstOrDefault(e=>e.MatchesNumber(binding.Number));if(automatic!=null && manual!=null && automatic.Number!=manual.Number)throw new ArgumentException("TC代号与另一条手动绑定冲突："+name);var entry=automatic??manual;if(entry==null)throw new ArgumentException("TC代号尚未绑定有效指令："+name);return entry;
        }
        private void CheckCommandCapture(IEnumerable<string> commands)
        {
            var names=commands.Distinct().ToArray();if(names.Length==0)return;
            if(ConfigurationEditing){foreach(string code in names)ResolveCommandCode(code);return;}
            if(!Listening || !Outgoing.IsRunning)throw new ArgumentException("含TC事件的规则需真实监听和发送监视运行；仅解码预览不产生TC事件");
            if(!Outgoing.CanObserveSource(Outgoing.Commands.BindingSource))throw new ArgumentException("发送监视筛选范围未包含TC指令库的来源IP");
            foreach(string name in names){var entry=ResolveCommandCode(name);var category=entry.Categories.FirstOrDefault(c=>"TC."+c.Code==name);if(category!=null && !((entry.STC||ObservedCommands.IsFlexibleKind(category.Kind))?category.DestinationIPs.ToArray():ObservedCommands.KindDestinations(category.Kind)).Any(ip=>Outgoing.CanObserve(Outgoing.Commands.BindingSource,ip)))throw new ArgumentException("发送监视目的IP未覆盖"+name+"的类别");}
        }
        public object CommandBindingList(int page,string search)
        {
            lock(Gate)
            {
                var entries=Outgoing.Commands.BindingEntries();var filtered=entries.Where(e=>(e.Number+" "+e.Name+" "+String.Join(" ",e.DestinationIPs)+" "+String.Join(" ",e.Aliases.Select(a=>a.Number+" "+a.Name))).IndexOf(search??"",StringComparison.OrdinalIgnoreCase)>=0).OrderBy(e=>e.Name,ObservedCommands.CommandNameOrder).ThenBy(e=>e.Number).ToArray();int pages=Math.Max(1,(filtered.Length+49)/50);page=Math.Max(1,Math.Min(page,pages));
                var rows=filtered.Skip((page-1)*50).Take(50).Select(e=>{var bound=tcBindings.Bindings.Where(b=>e.MatchesNumber(b.Number)).OrderBy(b=>b.Number).ToArray();return new{number=e.Number,name=e.Name,destinationIP=e.DestinationIP,destinationIPs=e.DestinationIPs,aliases=e.Aliases,automaticCodes=e.Categories.Select(c=>c.Code).ToArray(),manualCodes=bound.Select(b=>b.Code).ToArray(),codes=bound.Select(b=>b.Code).Concat(e.Categories.Select(c=>c.Code)).Distinct().ToArray(),length=e.Length,hexPreview=e.PayloadHex.Length>95?e.PayloadHex.Substring(0,95)+" …":e.PayloadHex,code=bound.Length==0?"":bound[0].Code};}).ToArray();
                return new{revision=tcBindings.Revision,error=tcStorageError,sourceIP=Outgoing.Commands.BindingSource,total=entries.Length,page,pages,rows,bindings=tcBindings.Bindings};
            }
        }
        public object CommandBindingDetail(long number)
        {lock(Gate){var entry=Outgoing.Commands.BindingEntries().FirstOrDefault(e=>e.MatchesNumber(number));if(entry==null)throw new ArgumentException("指令记录不存在");return new{number=entry.Number,name=entry.Name,destinationIP=entry.DestinationIP,destinationIPs=entry.DestinationIPs,aliases=entry.Aliases,categories=entry.Categories,length=entry.Length,payloadHex=entry.PayloadHex};}}
        public void SaveCommandBinding(long number,string code,string revision)
        {
            lock(Gate)
            {
                if(judgmentRunning || AnySequenceRunning)throw new InvalidOperationException("请先停止常态化判读和序列，再修改TC绑定；监听可继续");
                if(tcStorageError.Length>0)throw new InvalidOperationException(tcStorageError);
                if(revision!=tcBindings.Revision)throw new InvalidOperationException("TC绑定已变化，请刷新");
                var entry=Outgoing.Commands.BindingEntries().FirstOrDefault(e=>e.Number==number);if(entry==null)throw new ArgumentException("指令记录已不存在或已合并，请刷新");
                code=(code??"").Trim();if(code.StartsWith("TC.",StringComparison.Ordinal))code=code.Substring(3);
                if(Outgoing.Commands.BindingEntries().Any(e=>e.Number!=entry.Number && e.Categories.Any(c=>c.Code==code)))throw new ArgumentException("该代号已是其他指令的自动分类编号");
                var copy=JsonFile.Decode<TCBindingConfig>(JsonFile.Encode(tcBindings));copy.Bindings.RemoveAll(b=>entry.MatchesNumber(b.Number));
                if(code.Length>0)copy.Bindings.Add(new TCBinding{Number=number,Code=code});copy.Revision=Guid.NewGuid().ToString("N");ValidateTCConfig(copy);JsonFile.Save(TCPath,copy);tcBindings=copy;commandBindingRevision=copy.Revision;ruleBindings.Clear();
            }
        }
        private void QueueCapturedCommand(SendRecord record)
        {
            long observedGeneration=Interlocked.Read(ref judgmentGeneration);
            if(!judgmentRunning)return;bool schedule=false;
            lock(commandQueueGate)
            {
                if(commandQueue.Count>=2000 || commandQueueBytes+record.Payload.Length>32*1024*1024)commandOverflow=true;
                else{commandQueue.Enqueue(Tuple.Create(record,observedGeneration,RuleSeconds()));commandQueueBytes+=record.Payload.Length;}
                if(!commandPump){commandPump=true;schedule=true;}
            }
            if(schedule)ThreadPool.QueueUserWorkItem(unused=>DrainCapturedCommands());
        }
        private void DrainCapturedCommands()
        {
            while(true)
            {
                Tuple<SendRecord,long,double> pending=null;bool overflow;
                lock(commandQueueGate){overflow=commandOverflow;commandOverflow=false;if(commandQueue.Count>0){pending=commandQueue.Dequeue();commandQueueBytes-=pending.Item1.Payload.Length;}else{commandPump=false;if(!overflow)return;}}
                lock(Gate)
                {
                    if(overflow && judgmentRunning){StopJudgmentCore("指令事件队列溢出，判读已停止，监听继续");RecordAlarm("error","判读运行异常","指令事件队列溢出");}
                    if(pending==null || !judgmentRunning || pending.Item2!=Interlocked.Read(ref judgmentGeneration))continue;
                    try
                    {
                        var record=pending.Item1;if(record.SourceIP!=Outgoing.Commands.BindingSource)continue;
                        var match=Outgoing.Commands.MatchCommand(record.Payload);if(match.Entries.Length==0)continue;
                        var codes=ObservedCommands.MatchingCodes(match,record.DestinationIP,tcBindings.Bindings);
                        foreach(string code in codes)ResolveCommandCode("TC."+code);if(codes.Count>0)ProcessJudgmentEvent(new JudgmentEvent{CommandCodes=new HashSet<string>(codes.Select(code=>"TC."+code)),CommandEventId=record.HistoryEventId,Observed=pending.Item3});
                    }
                    catch(Exception ex){StopJudgmentCore("TC事件处理异常："+ex.Message);RecordAlarm("error","判读运行异常",ex.Message);}
                }
            }
        }
    }
}
