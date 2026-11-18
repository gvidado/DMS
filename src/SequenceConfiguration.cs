using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private SequenceConfiguration sequenceConfiguration=new SequenceConfiguration{Revision=Guid.NewGuid().ToString("N")};
        private string sequenceStorageError="";
        private const double SequenceExpectedDefault=1.0,SequenceMinimumInterval=0.2;
        private string SequenceConfigPath {get{return Path.Combine(root,"config","sequences.json");}}
        private void InitializeSequences()
        {
            if(File.Exists(SequenceConfigPath))try
            {
                var value=JsonFile.Load<SequenceConfiguration>(SequenceConfigPath);CheckSequenceConfig(value);
                if(AssignSequenceNumbers(value)){value.Revision=Guid.NewGuid().ToString("N");JsonFile.Save(SequenceConfigPath,value);}
                sequenceConfiguration=value;
            }
            catch(Exception ex){sequenceStorageError="序列配置读取失败，原文件保留："+ex.Message;}
            if (!ConfigurationEditing)
            {
                InitializeSequenceHistory();
                Outgoing.Captured+=QueueSequenceCaptured;
            }
        }
        private static T SequenceClone<T>(T value){return JsonFile.Decode<T>(JsonFile.Encode(value));}
        private static void CheckSequenceConfig(SequenceConfiguration value)
        {
            if(value==null || value.SchemaVersion!=1 || value.Sequences==null || value.Sequences.Count>128)throw new ArgumentException("序列配置格式无效或超过128个序列");
            CheckSequenceTimeout(value.DefaultTimeoutSeconds);
            if(value.Sequences.Any(d=>d==null || !Regex.IsMatch(d.Id??"",@"^[a-f0-9]{32}$") || d.Rows==null || d.Rows.Count>5000 || d.Rows.Any(r=>r==null)) || value.Sequences.Select(d=>d.Id).Distinct().Count()!=value.Sequences.Count)throw new ArgumentException("序列标识、行数或数据无效");
        }
        private static void CheckSequenceTimeout(double seconds)
        {if(Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds<=0 || seconds>604800)throw new ArgumentException("超时须大于0秒且不超过7天");}
        private static double SequenceSeconds(string text)
        {double result;if(!Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out result) || Double.IsNaN(result) || Double.IsInfinity(result) || result<0 || result>604800)throw new ArgumentException("时间须为0至604800之间的秒数");return result;}
        internal object SequenceConfigurationInfo()
        {
            lock(Gate)return new{revision=sequenceConfiguration.Revision,defaultTimeout=sequenceConfiguration.DefaultTimeoutSeconds,error=sequenceStorageError,sourceIPs=Config.SourceIPs,
                definitions=sequenceConfiguration.Sequences.Select(d=>new{d.Id,d.Code,d.Name,d.Mode,d.Filename,d.Sheet,d.SourceIP,count=d.Rows.Count}).ToArray(),runtime=SequenceSummary(),runtimes=SequenceRuntimes(),call=SequenceCallNotice()};
        }
        internal object SequenceValidate(SequenceDefinition definition,double timeout)
        {
            lock(Gate)
            {
                List<string> errors,warnings;List<SequenceStep> steps;
                try{steps=CompileSequence(definition,timeout,out errors,out warnings);}
                catch(ArgumentException ex){errors=new List<string>{ex.Message};warnings=new List<string>();steps=new List<SequenceStep>();}
                return new{definition,errors,warnings,commands=steps.Where(s=>s.Code.Length>0).Select(s=>new{row=s.Row.Row,code=s.Code,name=s.CommandName,destinations=s.Destinations,action=definition.Mode=="execute"&&!s.Inside?"发送":"监视"}).ToArray()};
            }
        }
        internal void AnnotateImportedSequenceCommands(IEnumerable<SequenceDefinition> definitions)
        {
            lock(Gate)foreach(var definition in definitions)foreach(var row in definition.Rows)
            {
                if(String.Equals((row.Keyword??"").Trim(),"CALL",StringComparison.OrdinalIgnoreCase))continue;
                string code=(row.Command??"").Trim();if(code.Length==0)continue;
                ObservedCommand entry;
                try{entry=ResolveCommandCode(code);}catch(ArgumentException){continue;}
                if(code.StartsWith("TC.",StringComparison.Ordinal))code=code.Substring(3);
                string suffix="（"+code+" "+entry.Name+"）",description=row.Description??"";
                if(!description.EndsWith(suffix,StringComparison.Ordinal))row.Description=description+suffix;
                row.CommandNote=suffix;
            }
        }
        private List<SequenceStep> CompileSequence(SequenceDefinition definition,double timeout,out List<string> errors,out List<string> warnings,HashSet<string> path=null,int depth=0,int[] budget=null,SequenceConfiguration context=null)
        {
            errors=new List<string>();warnings=new List<string>();CheckSequenceTimeout(timeout);var steps=new List<SequenceStep>();
            context=context??sequenceConfiguration;path=path??new HashSet<string>();budget=budget??new[]{0};
            if(definition==null || definition.Rows==null)throw new ArgumentException("序列内容为空");
            string identity=String.IsNullOrEmpty(definition.Code)?definition.Id:definition.Code;
            if(path.Contains(identity))throw new ArgumentException("CALL存在循环跳转："+identity);
            if(depth>32)throw new ArgumentException("CALL连续跳转最多32次");
            path=new HashSet<string>(path);path.Add(identity);budget[0]+=definition.Rows.Count;
            if(budget[0]>10000)throw new ArgumentException("CALL跳转链的总步骤数超过10000项");
            if(definition==null || definition.Rows==null)throw new ArgumentException("序列内容为空");
            if(definition.Mode!="execute" && definition.Mode!="judge")throw new ArgumentException("请选择序列执行或序列判读");
            if(String.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length>240 || definition.Rows.Count<1 || definition.Rows.Count>5000)errors.Add("序列名称为空、过长或没有有效数据行");
            if(definition.Rows.GroupBy(r=>r.Row).Any(g=>g.Count()>1))errors.Add("原始行号重复");
            int open=-1;bool markSeen=false;
            for(int index=0;index<definition.Rows.Count;index++)
            {
                var row=SequenceClone(definition.Rows[index]);var step=new SequenceStep{Row=row,Timeout=timeout,Mode=definition.Mode};steps.Add(step);
                string prefix="第"+row.Row+"行：";
                try
                {
                    if(row.CellErrors!=null && row.CellErrors.Count>0)throw new ArgumentException(String.Join("；",row.CellErrors.Values));
                    row.Keyword=(row.Keyword??"").Trim().ToUpperInvariant();if(row.Keyword=="PLUSE")row.Keyword="PAUSE";
                    row.Command=(row.Command??"").Trim();row.Condition=(row.Condition??"").Trim();row.Mark=(row.Mark??"").Trim();row.Expected=(row.Expected??"").Trim();row.Timeout=(row.Timeout??"").Trim();
                    if((row.Description??"").Length>4000 || row.Command.Length>80 || row.Condition.Length>20000 || row.Mark.Length>80 || row.Expected.Length>80 || row.Timeout.Length>80)throw new ArgumentException("单元格内容过长");
                    if(!new[]{"","TEXT","PAR","CHANGE","HOLD","PAUSE","CALL"}.Contains(row.Keyword))throw new ArgumentException("未知关键字："+row.Keyword);
                    step.Inside=open>=0 && row.Keyword!="CHANGE";step.Hold=row.Keyword=="HOLD";
                    if(row.Keyword=="PAR"){if(open>=0)throw new ArgumentException("不允许嵌套PAR");open=index;}
                    else if(row.Keyword=="CHANGE"){if(open<0)throw new ArgumentException("CHANGE没有配对的PAR");step.Pair=open;steps[open].Pair=index;open=-1;}
                    if(row.Keyword=="TEXT")
                    {
                        CompileSequenceTiming(step,definition.Mode,ref markSeen,warnings,prefix);
                        if(row.Command.Length+row.Condition.Length+row.Timeout.Length>0)warnings.Add(prefix+"TEXT按期望时间处理说明与T0标志；指令、判据和超时时间不执行，时间留空则到达该行后立即通过");
                        continue;
                    }
                    if(row.Keyword=="PAUSE")
                    {
                        if(definition.Mode!="execute")throw new ArgumentException("PLUSE只用于序列执行");
                        if(step.Inside)throw new ArgumentException("PLUSE不能放在并行段内");
                        if(row.Condition.Length+row.Mark.Length+row.Expected.Length+row.Timeout.Length>0)throw new ArgumentException("PLUSE行可填写说明和指令，不带判据、标志或时间；先暂停，继续后发令");
                        if(row.Command.Length==0)continue;
                    }
                    if(step.Hold && !step.Inside)throw new ArgumentException("HOLD必须位于PAR与CHANGE之间");
                    if(step.Hold && (row.Command.Length>0 || row.Condition.Length==0))throw new ArgumentException("HOLD只能填写参数判据，不能带指令");
                    if(row.Timeout.Length>0){step.Timeout=SequenceSeconds(row.Timeout);CheckSequenceTimeout(step.Timeout);}
                    if(row.Keyword!="PAUSE")CompileSequenceTiming(step,definition.Mode,ref markSeen,warnings,prefix);
                    if(row.Keyword=="CALL")
                    {
                        
                        if(row.Condition.Length>0)throw new ArgumentException("CALL行不填写判读要求，目标序列号填写在指令或参数列");
                        var target=context.Sequences.FirstOrDefault(d=>d.Code==row.Command);
                        if(target==null)throw new ArgumentException("CALL目标序列不存在："+row.Command);
                        step.LaunchJudgment=definition.Mode=="execute"&&target.Mode=="judge";
                        if(target.Mode!=definition.Mode && !step.LaunchJudgment)throw new ArgumentException("序列判读不能通过CALL启动序列执行");
                        if(!step.LaunchJudgment)
                        {
                            if(step.Inside)throw new ArgumentException("同模式CALL跳转不能位于PAR/CHANGE并行段内部");
                            if(definition.Rows.Skip(index+1).Any(r=>!String.Equals((r.Keyword??"").Trim(),"TEXT",StringComparison.OrdinalIgnoreCase) || !String.IsNullOrWhiteSpace(r.Mark) || !String.IsNullOrWhiteSpace(r.Expected)))throw new ArgumentException("同模式CALL跳转必须是序列最后一个有效步骤，其后仅可有不带标志和期望时间的TEXT注释");
                        }
                        warnings.Add(prefix+(step.LaunchJudgment?"CALL启动独立判读，执行序列继续：":"CALL转至")+target.Code+" / "+target.Name+(target.Mode=="execute"?"（序列执行，按目标配置主动发送）":"（序列判读，仅监视）"));
                        List<string> nextErrors,nextWarnings;step.NextDefinition=SequenceClone(target);
                        step.NextSteps=CompileSequence(step.NextDefinition,timeout,out nextErrors,out nextWarnings,path,depth+1,budget,context);
                        errors.AddRange(nextErrors.Select(e=>prefix+"CALL "+target.Code+"："+e));warnings.AddRange(nextWarnings.Select(e=>prefix+"CALL "+target.Code+"："+e));
                        continue;
                    }
                    if(row.Condition.Length>0)
                    {
                        step.Predicate=RuleParser.CompileSequence(row.Condition);
                        foreach(string reference in step.Predicate.Program.References)
                        {
                            string name=reference.Substring(0,reference.LastIndexOf('.'));var matches=RuntimeReferences(name);
                            if(matches.Length!=1)throw new ArgumentException(name.StartsWith("TM.",StringComparison.Ordinal)&&NativeDecoder?"旧TM引用需选择来源，请在序列预览改为TME或TMB引用："+name:"参数不存在或对应多个SPP，请核对数据源映射："+name);
                            var field=matches[0];if(!field.Supported)throw new ArgumentException("参数解码定义不完整："+name);
                            step.Predicate.Fields[reference]=new BoundTelemetry{Field=field,Source=BoundSource(field,definition.SourceIP)};
                        }
                        foreach(string reference in step.Predicate.Program.EnumReferences){var f=step.Predicate.Fields[reference].Field;if((f.Enums==null||f.Enums.Count==0)&&(f.EnumRanges==null||f.EnumRanges.Count==0))throw new ArgumentException("INENUM参数没有可识别的注释枚举或区间："+reference);}
                    }
                    if(row.Command.Length>0)
                    {
                        step.Code=row.Command.StartsWith("TC.",StringComparison.Ordinal)?row.Command:"TC."+row.Command;
                        if(!Regex.IsMatch(step.Code,@"^TC\.[\p{L}_][\p{L}\p{Nd}_]*$"))throw new ArgumentException("指令只填库内编号或TC.编号，不接受COMD/SETP等旧引用");
                        var entry=ResolveCommandCode(step.Code);step.CommandNumber=entry.Number;step.CommandName=entry.Name;
                        var category=entry.Categories.FirstOrDefault(c=>"TC."+c.Code==step.Code);step.AllDestinations=category==null;step.SendCode=step.Code.Substring(3);
                        step.Destinations=(category==null?entry.DestinationIPs:category.DestinationIPs).ToArray();if(step.Destinations.Length==0)throw new ArgumentException("指令没有配置目的IP");
                        step.CommandSignature=SequenceCommandSignature(step,entry);
                    }
                    if(row.Command.Length==0 && row.Condition.Length==0 && row.Keyword!="PAR" && row.Keyword!="CHANGE")throw new ArgumentException("没有指令或判读要求；纯说明请使用TEXT");
                }
                catch(Exception ex){errors.Add(prefix+ex.Message);}
            }
            if(open>=0)errors.Add("第"+steps[open].Row.Row+"行：PAR缺少CHANGE");
            foreach(var step in steps.Where(s=>s.Row.Keyword=="PAR" && s.Pair>=0))
            {int index=steps.IndexOf(step);if(!steps.Skip(index+1).Take(step.Pair-index-1).Any(s=>!s.Hold && s.Row.Keyword!="TEXT" && (s.Code.Length>0 || s.Predicate!=null || s.LaunchJudgment)))errors.Add("第"+step.Row.Row+"行：并行段需要至少一个普通参数或指令项作为完成条件，不能只有HOLD或注释");}
            return steps;
        }
        private static string SequenceCommandSignature(SequenceStep step,ObservedCommand entry)
        {var category=entry.Categories.FirstOrDefault(c=>"TC."+c.Code==step.Code);return CanonicalJson.Encode(new{entry.Number,entry.Name,entry.PayloadHex,sourceCode=step.Code,destinations=(category==null?entry.DestinationIPs:category.DestinationIPs).OrderBy(ip=>ip,StringComparer.Ordinal).ToArray()});}
        internal void SaveSequences(List<SequenceDefinition> incoming,bool replace,string revision,double timeout)
        {
            lock(Gate)
            {
                EnsureSequenceIdle();if(sequenceStorageError.Length>0)throw new InvalidOperationException(sequenceStorageError);if(revision!=sequenceConfiguration.Revision)throw new InvalidOperationException("序列配置已变化，请重新读取预览");
                var copy=SequenceClone(sequenceConfiguration);copy.DefaultTimeoutSeconds=timeout;if(replace)copy.Sequences.Clear();
                foreach(var definition in incoming){List<string> errors,warnings;CompileSequence(definition,timeout,out errors,out warnings);if(errors.Count>0)throw new ArgumentException(definition.Name+"："+String.Join("；",errors.Take(30)));if(copy.Sequences.Any(d=>d.Id==definition.Id || d.Name==definition.Name))throw new ArgumentException("已有同名序列，请修改名称或选择替换全部序列："+definition.Name);var item=SequenceClone(definition);var previous=replace?sequenceConfiguration.Sequences.FirstOrDefault(d=>d.Name==item.Name && d.Mode==item.Mode):null;item.Code=previous==null?"":previous.Code;copy.Sequences.Add(item);}
                AssignSequenceNumbers(copy);
                foreach(var item in copy.Sequences){List<string> graphErrors,graphWarnings;CompileSequence(item,timeout,out graphErrors,out graphWarnings,context:copy);if(graphErrors.Count>0)throw new ArgumentException(item.Code+"："+String.Join("；",graphErrors.Take(30)));}
                copy.Revision=Guid.NewGuid().ToString("N");CheckSequenceConfig(copy);JsonFile.Save(SequenceConfigPath,copy);sequenceConfiguration=copy;
            }
        }
        internal void EditSequence(string id,string mode,string source,double timeout,string revision,bool delete)
        {
            lock(Gate)
            {
                EnsureSequenceIdle();if(sequenceStorageError.Length>0)throw new InvalidOperationException(sequenceStorageError);if(revision!=sequenceConfiguration.Revision)throw new InvalidOperationException("序列配置已变化，请刷新");
                var copy=SequenceClone(sequenceConfiguration);CheckSequenceTimeout(timeout);copy.DefaultTimeoutSeconds=timeout;var definition=copy.Sequences.FirstOrDefault(d=>d.Id==id);
                if(id.Length>0 && definition==null)throw new ArgumentException("序列已不存在");
                if(definition!=null){if(delete)copy.Sequences.Remove(definition);else{if(definition.Mode!=mode)definition.Code="";definition.Mode=mode;definition.SourceIP=source;List<string> errors,warnings;CompileSequence(definition,timeout,out errors,out warnings);if(errors.Count>0)throw new ArgumentException(String.Join("；",errors.Take(30)));}}
                AssignSequenceNumbers(copy);
                foreach(var item in copy.Sequences){List<string> graphErrors,graphWarnings;CompileSequence(item,timeout,out graphErrors,out graphWarnings,context:copy);if(graphErrors.Count>0)throw new ArgumentException(item.Code+"："+String.Join("；",graphErrors.Take(30)));}
                copy.Revision=Guid.NewGuid().ToString("N");JsonFile.Save(SequenceConfigPath,copy);sequenceConfiguration=copy;
            }
        }
        internal SequenceDefinition SequenceDefinition(string id)
        {lock(Gate){var d=sequenceConfiguration.Sequences.FirstOrDefault(v=>v.Id==id);if(d==null)throw new ArgumentException("请先选择已保存序列");return SequenceClone(d);}}
        internal void EnsureSequenceIdle(){if(AnySequenceRunning)throw new InvalidOperationException("请先停止序列执行和序列判读，再修改序列或发送配置");}
    }
}
