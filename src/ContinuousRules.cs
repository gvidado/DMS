using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed class ContinuousRule
    {
        public string Id = "", Code = "", Name = "", Level = "一般", Script = "", Description = "";
        public string ParameterId = "", SourceIP = "", SourceFile = "", SourceSheet = "", ModifiedAt = "";
        public string ImportError = "";
        public bool MigrationPending,MigrationOriginalEnabled;
        public string MigrationReason="";
        public int SourceRow;
        public bool Enabled;
        public Dictionary<string, RuleParameterBinding> Bindings = new Dictionary<string, RuleParameterBinding>();
    }
    public sealed class RuleParameterBinding { public string ParameterId = "", SourceIP = ""; }
    internal sealed class BoundTelemetry { public Parameter Field; public string Source; }
    public sealed class ContinuousRuleConfig
    {
        public int SchemaVersion = 1;
        public string Revision = "";
        public List<ContinuousRule> Rules = new List<ContinuousRule>();
    }
    public sealed class RuleImportError
    { public string Sheet, Code, Message; public int Row; }
    internal sealed class RuleBinding
    {
        public string Source,DecoderRevision,TCRevision,ArchiveKey;
        public ContinuousRule Definition;public Settings Configuration;
        public Parameter Main;
        public CompiledRule Program;
        public Dictionary<string, BoundTelemetry> Fields = new Dictionary<string, BoundTelemetry>();
        public Dictionary<int, BoundTelemetry> Anchors = new Dictionary<int, BoundTelemetry>();
    }
    internal sealed class RuleRunState
    {
        public readonly Dictionary<string, object> Variables = new Dictionary<string, object>();
        public bool Initialized;
        public string Result = "", Status = "待接收", Detail = "", Time = "", ValueText = "", LastAlarm = "", AbnormalMarker = "";
        public JudgmentAbnormalEntry ArchivedIncident;
        public readonly HashSet<string> UsedReferences = new HashSet<string>();
        public readonly Dictionary<int, TemporalWindow> Windows = new Dictionary<int, TemporalWindow>();
    }
    public sealed partial class Monitor
    {
        private ContinuousRuleConfig continuous = new ContinuousRuleConfig();
        private string ruleStorageError = "";
        private readonly Dictionary<string, RuleBinding> ruleBindings = new Dictionary<string, RuleBinding>();
        private readonly Dictionary<string, RuleRunState> ruleRuns = new Dictionary<string, RuleRunState>();
        private string RulePath { get { return Path.Combine(root, "config", "continuous-rules.json"); } }
        public const string DefaultContinuousScript="InitRegion\n    this.state=0;\nEndInitRegion\n\nStateRegion\n\nEndStateRegion\n\nResultRegion\n    RET=Normal;\nEndResultRegion";
        public static bool IsInitialTemplateRule(ContinuousRule r)
        { return r!=null && !r.MigrationPending && !r.Enabled && String.Concat((r.Script??"").Where(c=>!Char.IsWhiteSpace(c)))==String.Concat(DefaultContinuousScript.Where(c=>!Char.IsWhiteSpace(c))); }
        private static bool IsInitialConfiguration(ContinuousRule r)
        { return IsUnconfiguredRule(r) || IsInitialTemplateRule(r); }
        public static bool IsUnconfiguredRule(ContinuousRule r)
        { return r != null && !r.MigrationPending && !r.Enabled && String.IsNullOrWhiteSpace(r.Script); }
        public static bool IsDefaultRule(ContinuousRule r)
        { return IsInitialConfiguration(r) && r.Level == "一般" && String.IsNullOrEmpty(r.Description) && (r.Bindings == null || r.Bindings.Count == 0); }
        public List<Parameter> ContinuousParameters()
        { lock(Gate) return Catalog; }
        public object ContinuousSource(string parameterId)
        {
            lock(Gate)
            {
                var field=RuntimeField(parameterId);if(field==null)throw new ArgumentException("请选择存在的解码参数");
                string source="",error="";try{source=BoundSource(field,"");}catch(ArgumentException ex){error=ex.Message;}
                return new{parameterId=field.Id,reference=field.Reference,packet=field.PacketName,sourceIP=source,automatic=true,canAuto=error.Length==0,error};
            }
        }
        private ContinuousRuleConfig PlanDefaultRules(List<Parameter> fields)
        {
            if (ruleStorageError.Length > 0) throw new InvalidOperationException(ruleStorageError);
            var candidate = JsonFile.Decode<ContinuousRuleConfig>(JsonFile.Encode(continuous));
            var fieldsById = fields.ToLookup(p => p.Id);
            var previousById = Catalog.ToLookup(p => p.Id);
            var kept = new List<ContinuousRule>(candidate.Rules.Count);
            bool changed=false;
            // Raw Excel rows are definitions. Only source-qualified SPP mappings own rules.
            foreach(var row in candidate.Rules)
            {
                string reference=TelemetryReferences.Name(row.Code);
                var exact=fieldsById[row.ParameterId].FirstOrDefault(p=>p.Reference==reference);
                if(exact!=null && !row.MigrationPending)
                {
                    var previous=previousById[row.ParameterId].FirstOrDefault();
                    if(previous!=null && row.Name==previous.Name && row.Name!=exact.Name){row.Name=exact.Name;row.ModifiedAt=BeijingTime.Now.ToString("o");changed=true;}
                    kept.Add(row);
                    continue;
                }
                if(IsDefaultRule(row)){changed=true;continue;}
                if(row.MigrationPending)
                {
                    if(row.MigrationReason.StartsWith("尚未建立",StringComparison.Ordinal))
                    {
                        var options=MigrationFields(row,fields);
                        if(options.Length>0)
                        {row.MigrationReason=options.Length==1?"已发现带来源的参数映射，请预览并确认迁移":"同一参数存在多个来源映射，请选择TME/TMB或复制到多个目标";changed=true;}
                    }
                    kept.Add(row);
                    continue;
                }
                if(!reference.StartsWith("TM.",StringComparison.Ordinal))
                {MarkRuleMigration(row,"原SPP来源或参数映射已改变，请选择新的参数映射");kept.Add(row);changed=true;continue;}
                try
                {
                    var targets=MigrationFields(row,fields);
                    if(targets.Length!=1)throw new ArgumentException(targets.Length==0?"尚未建立带来源的SPP参数映射":"同一参数存在多个来源映射，请选择TME/TMB或复制到多个目标");
                    kept.Add(CreateMigratedRule(row,targets[0],fields));changed=true;
                }
                catch(Exception ex){MarkRuleMigration(row,ex.Message);kept.Add(row);changed=true;}
            }
            var boundIds = new HashSet<string>(kept.Where(r=>!r.MigrationPending).Select(r=>r.ParameterId));
            foreach(var field in fields)
            {
                if(!boundIds.Add(field.Id))continue;
                kept.Add(new ContinuousRule{Id=Guid.NewGuid().ToString("N"),Code=field.Reference,Name=field.Name,ParameterId=field.Id,
                    Level="一般",Enabled=false,Script=DefaultContinuousScript,ModifiedAt=BeijingTime.Now.ToString("o")});changed=true;
            }
            if (!changed) return null;
            candidate.Rules=kept;
            candidate.Revision = Guid.NewGuid().ToString("N"); ValidateRuleConfig(candidate); return candidate;
        }
        private void InitializeDefaultRules()
        {
            if (decoder == null || ruleStorageError.Length > 0) return;
            try { var defaults = PlanDefaultRules(Catalog); if (defaults != null) { JsonFile.Save(RulePath, defaults); continuous = defaults; } }
            catch(Exception ex) { ruleStorageError = "默认判读配置初始化失败：" + ex.Message; Note("warn", ruleStorageError); }
        }
        private void LoadContinuousRules()
        {
            if(!File.Exists(RulePath))return;
            try { continuous=JsonFile.Load<ContinuousRuleConfig>(RulePath); ValidateRuleConfig(continuous); }
            catch(Exception ex) { continuous=new ContinuousRuleConfig();ruleStorageError="常态化规则文件读取失败，保留原文件，请修复后重启："+ex.Message;Note("warn",ruleStorageError); }
        }
        private static void ValidateRuleConfig(ContinuousRuleConfig c)
        {
            if(c==null || c.SchemaVersion!=1 || c.Rules==null || c.Rules.Count>20000)throw new ArgumentException("规则配置格式无效或超过20000条");
            if(c.Rules.Any(r=>r==null || String.IsNullOrEmpty(r.Id)) || c.Rules.Select(r=>r.Id).Distinct().Count()!=c.Rules.Count)throw new ArgumentException("规则标识为空或重复");
            foreach(var r in c.Rules) ValidateRuleFields(r);
        }
        private static void ValidateRuleFields(ContinuousRule r)
        {
            if(r!=null && !String.IsNullOrEmpty(r.ImportError))throw new ArgumentException(r.ImportError);
            if(r==null || String.IsNullOrWhiteSpace(r.Code) || r.Code.Length>180 || String.IsNullOrWhiteSpace(r.Name) || r.Name.Length>240 || r.Id.Length>80)throw new ArgumentException("请填写参数代号和名称，且长度应有效");
            if(!new[]{"一般","重要","关键"}.Contains(r.Level))throw new ArgumentException("判读级别应为一般、重要或关键");
            if(r.Script==null || r.Script.Length>20000 || (r.Description??"").Length>4000)throw new ArgumentException("规则或描述过长");
            if(r.Bindings==null)r.Bindings=new Dictionary<string,RuleParameterBinding>();
            r.SourceIP=(r.SourceIP??"").Trim();
            if(r.Bindings.Count>256 || r.Bindings.Any(p=>p.Value==null || !System.Text.RegularExpressions.Regex.IsMatch(p.Key,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$")))throw new ArgumentException("附加参数绑定格式无效");
        }
        private string BoundSource(Parameter field,string specified,string fallback="")
        {
            string source=(specified??"").Trim(),packetSource="";
            if(NativeDecoder){var packet=decoder.Packets.FirstOrDefault(p=>p.Id==field.PacketId);if(packet!=null)packetSource=packet.SourceIP??"";}
            if(source.Length==0 && packetSource.Length>0)source=packetSource;
            if(source.Length==0)source=fallback;
            if(source.Length==0){var ips=Config.SourceIPs.Distinct().ToArray();if(ips.Length!=1)throw new ArgumentException("参数"+field.Reference+"所属SPP尚未限定来源，且监听包含多个IP；请在SPP配置明确来源，或使用规则的可选IP绑定");source=ips[0];}
            if(!Config.SourceIPs.Contains(source) || (packetSource.Length>0 && source!=packetSource))throw new ArgumentException("来源IP与监听白名单或SPP来源限制不符："+field.Reference);
            return source;
        }
        private Parameter BindRuleParameter(ContinuousRule r)
        {
            if(!String.IsNullOrEmpty(r.ParameterId))
            { var field=RuntimeField(r.ParameterId);if(field==null && IsInitialConfiguration(r) && decoder!=null)field=decoder.Catalog.Parameters.FirstOrDefault(p=>p.Id==r.ParameterId);if(field==null)throw new ArgumentException("绑定参数已不存在，请重新选择");if(!String.Equals(TelemetryReferences.Name(r.Code),field.Reference,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("参数代号与绑定参数不一致");return field; }
            string reference=TelemetryReferences.Name(r.Code);
            var fields=RuntimeReferences(reference);
            if(fields.Length==0)fields=RuntimeReferences(reference,true);
            if(fields.Length!=1)throw new ArgumentException(fields.Length==0 ? "当前解码表找不到参数"+r.Code : "参数代号对应多个SPP，请手动选择明确参数");
            return fields[0];
        }
        private RuleBinding CompileContinuous(ContinuousRule r)
        {
            ValidateRuleFields(r);
            if(r.MigrationPending)throw new ArgumentException("旧规则待迁移："+r.MigrationReason);
            Parameter main=BindRuleParameter(r);if(RuntimeField(main.Id)==null)throw new ArgumentException("参数尚未关联SPP，请先关联解码参数表");if(!main.Supported)throw new ArgumentException("参数解码定义不完整");
            string source=BoundSource(main,r.SourceIP);
            var binding=new RuleBinding { Main=main, Source=source, Program=RuleParser.Compile(r.Script) };
            Func<string,BoundTelemetry> resolve=name=>
            {
                if(name=="this")return new BoundTelemetry{Field=main,Source=source};
                Parameter field=null;RuleParameterBinding explicitBinding;
                string specified="";
                if(name==main.Reference)
                {if(r.Bindings.TryGetValue(name,out explicitBinding) && (explicitBinding.ParameterId!=main.Id || (!String.IsNullOrEmpty(explicitBinding.SourceIP) && explicitBinding.SourceIP!=source)))throw new ArgumentException("主参数引用必须与this绑定一致，请修改主参数绑定");return new BoundTelemetry{Field=main,Source=source};}
                if(r.Bindings.TryGetValue(name,out explicitBinding))
                {field=RuntimeField(explicitBinding.ParameterId);if(field==null || field.Reference!=name)throw new ArgumentException("绑定与参数引用不一致或已失效："+name);specified=explicitBinding.SourceIP;}
                else if(name==main.Reference)field=main;
                else {var matches=RuntimeReferences(name);if(matches.Length!=1)throw new ArgumentException("引用不存在或不唯一，请在附加绑定中选择："+name);field=matches[0];}
                if(!field.Supported)throw new ArgumentException("引用参数解码定义不完整："+name);
                return new BoundTelemetry{Field=field,Source=BoundSource(field,specified,source)};
            };
            foreach(string reference in binding.Program.References.Concat(binding.Program.EnumReferences))
            {
                string name=TelemetryReferences.HasPrefix(reference)?reference.Substring(0,reference.LastIndexOf('.')):"this";
                binding.Fields[reference]=resolve(name);
            }
            foreach(string reference in binding.Program.EnumReferences)
            {Parameter f=binding.Fields[reference].Field;if((f.Enums==null || f.Enums.Count==0) && (f.EnumRanges==null || f.EnumRanges.Count==0))throw new ArgumentException("INENUM参数没有可识别的注释枚举或区间："+reference);}
            foreach(var timer in binding.Program.Timers.Where(t=>t.Kind=="FRAMES"))
            {
                var inputs=timer.Condition.Inputs.Where(binding.Fields.ContainsKey).Select(reference=>binding.Fields[reference]).GroupBy(f=>f.Field.Id+"|"+f.Source).Select(g=>g.First()).ToArray();
                if(!timer.ExplicitAnchor && inputs.Length>1)throw new ArgumentException("FRAMES多参数条件必须用第三个参数明确计数来源，例如FRAMES(条件,5,TM.A2)");
                binding.Anchors[timer.Id]=!timer.ExplicitAnchor && inputs.Length==1?inputs[0]:resolve(timer.Anchor);
            }
            ValidateRuleCommands(binding.Program.Commands);
            return binding;
        }
        private RuleBinding Binding(ContinuousRule r)
        {
            RuleBinding b;if(ruleBindings.TryGetValue(r.Id,out b)&&(judgmentRunning||(Object.ReferenceEquals(b.Definition,r)&&Object.ReferenceEquals(b.Configuration,Config)&&b.DecoderRevision==DecoderRevision&&b.TCRevision==commandBindingRevision)))return b;
            b=CompileContinuous(r);b.Definition=r;b.Configuration=Config;b.DecoderRevision=DecoderRevision;b.TCRevision=commandBindingRevision;ruleBindings[r.Id]=b;return b;
        }
        private ParameterHistory RuleHistory(Parameter field,string source)
        {return RuleHistoryById(field.Id,source);}
        private object RuleValue(Parameter field,string source,int index=0)
        {
            var h=RuleHistory(field,source);
            if(h==null || h.Latest==null)throw new RuleWaiting("等待参数数据："+field.Reference);
            if(h.Latest.Status!="有效")throw new RuleWaiting("参数数据无效："+field.Reference);
            if(HistoryAgeMs(h)>Config.DataTimeoutMs)throw new RuleWaiting("数据过期："+field.Reference);
            return h.Samples.Count>index?h.Samples[index].value:null;
        }
        private RuleEvaluation Evaluation(RuleBinding b,RuleRunState state,JudgmentEvent input,Dictionary<string,object> globalSnapshot,Dictionary<string,object> pendingGlobals)
        {
            return new RuleEvaluation { Variables=state.Variables, DefaultTarget=()=>RuleValue(b.Main,b.Source),
                Read=reference=>{state.UsedReferences.Add(reference);int dot=reference.LastIndexOf('.');string suffix=reference.Substring(dot+1);int index=suffix=="value"?0:suffix[3]-'0';var bound=b.Fields[reference];return RuleValue(bound.Field,bound.Source,index);},
                Command=name=>input.Command==name || input.CommandCodes.Contains(name),
                Clock=name=>RuleClock(name),
                Global=name=>{object value;return pendingGlobals.TryGetValue(name,out value)||globalSnapshot.TryGetValue(name,out value)?value:null;},
                SetGlobal=(name,value)=>pendingGlobals[name]=value,
                Temporal=id=>{var w=state.Windows[id];if(w.Waiting!=null)throw new RuleWaiting(w.Waiting);return w.Value;},
                InEnum=(reference,value)=>{if(value==null)throw new RuleWaiting("注释匹配的历史数据不足");decimal n=RuleEvaluation.Number(value);Parameter p=b.Fields[reference].Field;
                    bool enums=(p.Enums??new Dictionary<string,string>()).Keys.Any(k=>n==Decimal.Parse(k,CultureInfo.InvariantCulture)*Convert.ToDecimal(p.Scale)+Convert.ToDecimal(p.Bias));
                    return enums || (p.EnumRanges??new List<EnumRange>()).Any(range=>n>=Convert.ToDecimal(range.Min) && n<=Convert.ToDecimal(range.Max));} };
        }
        private void EvaluateContinuous(string source,HashSet<string> changed)
        {
            SequenceTelemetry(source,changed);
            if(judgmentRunning)ProcessJudgmentEvent(new JudgmentEvent{Source=source,Changed=changed});
        }
        private void ResetContinuous(){StopJudgmentCore("监听或配置已重置");StopSequence("监听或配置已重置");ruleRuns.Clear();ruleBindings.Clear();ClearSharedValues();}
        public ContinuousRuleConfig ContinuousSnapshot(){lock(Gate)return JsonFile.Decode<ContinuousRuleConfig>(JsonFile.Encode(continuous));}
        public object ContinuousList()
        {
            lock(Gate)
            {
                var rows=new List<object>();
                var available = RuntimeFields();
                foreach(var r in continuous.Rules)
                {
                    string error="",status=r.Enabled?"待接收":"已停用",result="",detail="",value="",time="",reference=TelemetryReferences.Name(r.Code),parameterId=r.ParameterId,source=r.SourceIP;
                    object currentState=null;var timings=new List<object>();var resolvedBindings=new List<object>();
                    try
                    {
                        if (IsUnconfiguredRule(r))
                        {
                            Parameter field; available.TryGetValue(r.ParameterId, out field);
                            if (field != null) reference = field.Reference;
                            rows.Add(new { rule=r,reference,parameterId,sourceIP=source,error="",status="未配置",result="",detail="尚未填写判读规则，当前未开启判读",value,time,currentState,timings,bindings=resolvedBindings,canExecute=false,unconfigured=true });
                            continue;
                        }
                        var b=Binding(r);reference=b.Main.Reference;parameterId=b.Main.Id;source=b.Source;
                        foreach(var item in b.Fields)resolvedBindings.Add(new{reference=item.Key,parameterId=item.Value.Field.Id,sourceIP=item.Value.Source});
                        RuleRunState state;
                        if(r.Enabled && ruleRuns.TryGetValue(r.Id,out state)){status=state.Status;result=state.Result;detail=state.Detail;value=state.ValueText;time=state.Time;}
                        if(ruleRuns.TryGetValue(r.Id,out state))
                        {
                            state.Variables.TryGetValue("this.state",out currentState);
                            foreach(var spec in b.Program.Timers){TemporalWindow w;if(state.Windows.TryGetValue(spec.Id,out w))timings.Add(new{id=spec.Id,kind=spec.Kind,value=w.Value,count=w.Count,amount=w.Amount,remainingSeconds=spec.Kind=="AFTER"?w.Due.HasValue?Math.Max(0,w.Due.Value-(JudgmentSeconds()-judgmentStarted)):(double?)null:spec.Kind=="HOLD" && w.Start.HasValue?Math.Max(0,(double)(w.Amount??0)-(JudgmentSeconds()-judgmentStarted-w.Start.Value)):(double?)null,waiting=w.Waiting});}
                        }
                        if(r.Enabled && !judgmentRunning){status="判读未运行";result="";}
                        else if(r.Enabled){try{RuleValue(b.Main,b.Source);if(ruleRuns.TryGetValue(r.Id,out state))foreach(var used in state.UsedReferences){var field=b.Fields[used];RuleValue(field.Field,field.Source);}}catch(RuleWaiting ex){status="等待数据";result="";detail=ex.Message;}}
                    }
                    catch(Exception ex){if(r.MigrationPending){error=ex.Message;status="待迁移";detail=r.MigrationReason;}else if(IsInitialTemplateRule(r)){status="待绑定";detail=ex.Message;}else{error=ex.Message;status="配置错误";}result="";}
                    rows.Add(new{rule=r,reference,parameterId,sourceIP=source,error,status,result,detail,value,time,currentState,timings,bindings=resolvedBindings,canExecute=error.Length==0 && status!="待绑定",initialTemplate=IsInitialTemplateRule(r)});
                }
                return new{revision=continuous.Revision,storageError=ruleStorageError,defaultScript=DefaultContinuousScript,total=continuous.Rules.Count,enabled=continuous.Rules.Count(r=>r.Enabled),execution=JudgmentInfo(),rows=rows};
            }
        }
        public string CheckContinuousRule(ContinuousRule r,bool bind,bool allowInitial=false)
        {
            lock(Gate){try{if(r.MigrationPending)throw new ArgumentException("请先确认旧规则的来源迁移");if(IsUnconfiguredRule(r) || (allowInitial && IsInitialTemplateRule(r))){ValidateRuleFields(r);var field=BindRuleParameter(r);if(!String.IsNullOrWhiteSpace(r.Script))RuleParser.Compile(r.Script);if(bind)r.ParameterId=field.Id;if(!String.IsNullOrEmpty(field.TelemetryNamespace))r.Code=field.Reference;return "";}var b=CompileContinuous(r);if(bind){r.ParameterId=b.Main.Id;if(!String.IsNullOrEmpty(r.SourceIP))r.SourceIP=b.Source;if(!String.IsNullOrEmpty(b.Main.TelemetryNamespace))r.Code=b.Main.Reference;}return "";}catch(Exception ex){return ex.Message;}}
        }
        private string RuleKey(ContinuousRule r)
        {
            if(r.MigrationPending)return "pending:"+r.Id;string source=r.SourceIP??"";
            if(source.Length==0)try{source=BoundSource(BindRuleParameter(r),"");}catch(ArgumentException){}
            return (String.IsNullOrEmpty(r.ParameterId)?TelemetryReferences.Name(r.Code):r.ParameterId)+"\0"+source;
        }
        internal string ContinuousRuleKey(ContinuousRule r){lock(Gate)return RuleKey(r);}
        public static string ContinuousDefinition(ContinuousRule r){return CanonicalJson.Encode(new{r.ParameterId,r.SourceIP,r.Name,r.Level,r.Enabled,r.Script,r.Description,bindings=r.Bindings??new Dictionary<string,RuleParameterBinding>()});}
        private static bool SameContinuousDefinition(ContinuousRule previous,ContinuousRule next)
        {
            if(previous.ParameterId!=next.ParameterId||previous.SourceIP!=next.SourceIP||previous.Name!=next.Name||
                previous.Level!=next.Level||previous.Enabled!=next.Enabled||previous.Script!=next.Script||previous.Description!=next.Description)
                return false;
            int count=previous.Bindings==null?0:previous.Bindings.Count;
            if(count!=(next.Bindings==null?0:next.Bindings.Count))return false;
            if(count==0)return true;
            return ContinuousDefinition(previous)==ContinuousDefinition(next);
        }
        public void SaveContinuousRules(List<ContinuousRule> rows,string revision)
        {
            lock(Gate)
            {
                if(judgmentRunning)throw new InvalidOperationException("请先停止判读，再修改规则或绑定；参数监听可继续运行");
                if(ruleStorageError.Length>0)throw new InvalidOperationException(ruleStorageError);
                if(revision!=continuous.Revision)throw new InvalidOperationException("规则已被其他用户修改，请刷新后重试");
                var candidate=new ContinuousRuleConfig{Revision=Guid.NewGuid().ToString("N"),Rules=rows};ValidateRuleConfig(candidate);
                if(rows.Select(RuleKey).Distinct().Count()!=rows.Count)throw new ArgumentException("同一参数和来源已存在规则，请编辑已有规则");
                if(rows.Any(r=>r.MigrationPending&&r.Enabled))throw new ArgumentException("待迁移规则不能启用");
                var previous=continuous.Rules.ToDictionary(r=>r.Id);
                foreach(var r in rows.Where(x=>x.Enabled)){ContinuousRule old;if(previous.TryGetValue(r.Id,out old) && SameContinuousDefinition(old,r))continue;string error=CheckContinuousRule(r,false);if(error.Length>0)throw new ArgumentException(r.Code+"："+error);}
                JsonFile.Save(RulePath,candidate);
                var nextById=rows.ToDictionary(r=>r.Id);
                foreach(var old in continuous.Rules)
                {ContinuousRule next;if(!nextById.TryGetValue(old.Id,out next) || next.Script!=old.Script || next.ParameterId!=old.ParameterId || next.SourceIP!=old.SourceIP || next.Enabled!=old.Enabled){ruleRuns.Remove(old.Id);ruleBindings.Remove(old.Id);}}
                continuous=candidate;
            }
        }
    }
}
