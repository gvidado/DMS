using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    internal sealed class RuleWaiting : Exception { public RuleWaiting(string message) : base(message) { } }
    internal sealed class RuleExpression
    {
        public Func<RuleEvaluation, object> Get;
        public string Reference;
        public readonly HashSet<string> Inputs = new HashSet<string>();
        public bool HasTimeUnit;
        public bool IsBoolean;
        public RuleExpression(Func<RuleEvaluation, object> get, string reference = null, params RuleExpression[] children)
        { Get = get; Reference = reference; if(reference!=null && (TelemetryReferences.HasPrefix(reference) || reference.StartsWith("this.")))Inputs.Add(reference);foreach(var child in children){Inputs.UnionWith(child.Inputs);HasTimeUnit|=child.HasTimeUnit;} }
    }
    internal sealed class RuleEvaluation
    {
        public Func<string, object> Read;
        public Func<string, object, bool> InEnum;
        public Dictionary<string, object> Variables;
        public Func<string, object> Command, Clock, Global;
        public Action<string, object> SetGlobal;
        public Func<int, object> Temporal;
        public Func<object> DefaultTarget;
        public bool PartialLogic;
        private object target;
        private bool targetSet;
        public object Target { get { return targetSet ? target : DefaultTarget == null ? null : DefaultTarget(); } set { target=value; targetSet=true; } }
        public string Ret;
        public List<decimal[]> Ranges;
        public decimal? EL, EU, EV, ERR;
        public static decimal Number(object value)
        {
            if (value == null) throw new RuleWaiting("历史样本不足或数据未到达");
            if (value is bool || value is string) throw new ArgumentException("数值运算不能使用布尔或文本值");
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        public static bool Truth(object value)
        { if (value == null) throw new RuleWaiting("条件数据不足"); if (!(value is bool)) throw new ArgumentException("if条件必须是比较或布尔表达式"); return (bool)value; }
        public string Conclusion()
        {
            bool limits = Ranges != null || EL.HasValue || EU.HasValue || EV.HasValue || ERR.HasValue;
            if (Ret != null) { if (limits) throw new ArgumentException("同一分支不能同时设置RET和范围"); return Ret; }
            decimal x = Number(Target);
            if (Ranges != null)
            {
                if (EL.HasValue || EU.HasValue || EV.HasValue || ERR.HasValue) throw new ArgumentException("范围定义相互冲突");
                for (int i=0;i<Ranges.Count;i++) if (Ranges[i][0]>Ranges[i][1] || (i>0 && (Ranges[i][0]<Ranges[i-1][0] || Ranges[i][1]>Ranges[i-1][1]))) throw new ArgumentException("RANGE上下限或嵌套顺序无效");
                if (x>=Ranges.Last()[0] && x<=Ranges.Last()[1]) return "Normal";
                if (Ranges.Count==1) return "Error";
                if (Ranges.Count==2) return x>=Ranges[0][0] && x<=Ranges[0][1] ? "Unusual" : "Error";
                return x>=Ranges[1][0] && x<=Ranges[1][1] ? "Unusual" : x>=Ranges[0][0] && x<=Ranges[0][1] ? "Error" : "SeriousError";
            }
            if (EV.HasValue) { if (EL.HasValue || EU.HasValue) throw new ArgumentException("EV与EL/EU不能混用"); decimal error=ERR??0; if(error<0) throw new ArgumentException("ERR不能为负");return x>=EV.Value-error && x<=EV.Value+error ? "Normal":"Error"; }
            if (EL.HasValue && EU.HasValue) { if(EL>EU || ERR.HasValue) throw new ArgumentException("上下限配置无效");return x>=EL && x<=EU ? "Normal":"Error"; }
            throw new RuleWaiting("当前分支未给出判读结论");
        }
    }
    internal sealed class CompiledRule
    {
        public List<Action<RuleEvaluation>> Init = new List<Action<RuleEvaluation>>(), State = new List<Action<RuleEvaluation>>(), Result = new List<Action<RuleEvaluation>>();
        public HashSet<string> References = new HashSet<string>(), EnumReferences = new HashSet<string>();
        public HashSet<string> Commands = new HashSet<string>(), GlobalReads = new HashSet<string>(), GlobalWrites = new HashSet<string>();
        public bool UsesClock;
        public List<RuleTemporal> Timers = new List<RuleTemporal>();
    }
    internal sealed class RuleTemporal
    {
        public int Id;
        public string Kind, Anchor, Phase;
        public bool ExplicitAnchor;
        public RuleExpression Condition, Amount;
    }
    // A bounded interpreter; no arbitrary C#, JavaScript, filesystem or process execution.
    internal sealed class RuleParser
    {
        private readonly List<string> tokens=new List<string>();
        private readonly HashSet<string> variables=new HashSet<string>(new[]{"this.state"});
        private readonly CompiledRule rule=new CompiledRule();
        private int at, depth, expressionDepth;
        private bool initializing;
        private bool sequenceOnly;
        private string phase = "result";
        private string Current { get { return at<tokens.Count ? tokens[at] : "<结束>"; } }
        private bool Take(string s) { if(Current!=s)return false;at++;return true; }
        private void Expect(string s) { if(!Take(s))throw new ArgumentException("需要“"+s+"”，实际为“"+Current+"”"); }
        private RuleParser(string script)
        {
            if(String.IsNullOrWhiteSpace(script))throw new ArgumentException("判读规则为空，描述列不会自动当作规则执行");
            if(script.Length>20000)throw new ArgumentException("规则超过20000字符");
            script=Regex.Replace(script,@"//[^\r\n]*","");
            var scan=new Regex(@"\G\s*(0[xX][0-9a-fA-F]+(?:\.[0-9a-fA-F]+)?|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|[\p{L}_][\p{L}\p{Nd}_.]*|&&|\|\||==|!=|<=|>=|[+*/%^!<>=(),;\[\]-])");
            int pos=0, nesting=0;
            while(pos<script.Length)
            {
                if(String.IsNullOrWhiteSpace(script.Substring(pos)))break;
                Match m=scan.Match(script,pos);if(!m.Success)throw new ArgumentException("无法识别规则字符，位置"+(pos+1));
                string t=m.Groups[1].Value;tokens.Add(t);pos=m.Index+m.Length;
                if(t=="(" || t=="[") { if(++nesting>32)throw new ArgumentException("表达式嵌套超过32层"); }
                if(t==")" || t=="]") nesting--;
                if(tokens.Count>3000)throw new ArgumentException("规则语句过多");
                if(t=="Filter")throw new ArgumentException("旧Filter需改写为明确的时间条件，当前不能直接执行");
                if(t.StartsWith("COMD.") || t.StartsWith("KUTC.") || t.StartsWith("DAHE."))throw new ArgumentException("旧引用"+t+"尚未映射；遥测请使用TM，指令需后续绑定TC代号");
            }
        }
        public static CompiledRule Compile(string script)
        {
            var p=new RuleParser(script);
            if(p.Take("InitRegion")){p.initializing=true;p.rule.Init=p.Block("EndInitRegion");p.Expect("EndInitRegion");p.initializing=false;}
            if(p.Take("StateRegion")){p.phase="state";p.rule.State=p.Block("EndStateRegion");p.Expect("EndStateRegion");}
            p.phase="result";
            if(p.Take("ResultRegion")){p.rule.Result=p.Block("EndResultRegion");p.Expect("EndResultRegion");}
            else p.rule.Result=p.Block();
            if(p.at!=p.tokens.Count)throw new ArgumentException("多余的规则内容："+p.Current);
            if(p.rule.Result.Count==0)throw new ArgumentException("缺少诊断结论域");
            return p.rule;
        }
        internal static SequencePredicate CompileSequence(string script)
        {
            var p=new RuleParser(script);p.sequenceOnly=true;
            for(int i=0;i<p.tokens.Count;i++)
            {
                string t=p.tokens[i];
                if(t.Equals("AND",StringComparison.OrdinalIgnoreCase)||t=="与")p.tokens[i]="&&";
                else if(t.Equals("OR",StringComparison.OrdinalIgnoreCase)||t=="或")p.tokens[i]="||";
                else if(t.Equals("NOT",StringComparison.OrdinalIgnoreCase)||t=="非")p.tokens[i]="!";
                // Sequences expose current engineering values only. Keep one internal
                // identity so shorthand and legacy .value share binding and freshness.
                else if(Regex.IsMatch(t,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$"))p.tokens[i]=t+".value";
            }
            var functions=new[]{"INENUM","sin","cos","tan","asin","acos","exp","log","log10","abs","sqrt","min","max","pow"};
            foreach(string t in p.tokens)
            {
                if(t=="=")throw new ArgumentException("等于判断请使用 ==，不能使用单个 =；例如 TME.A1 == 1");
                if(new[]{"==","!=",">","<",">=","<="}.Contains(t))continue;
                if(new[]{"+","-","*","/","(",")",",","&&","||","!","true","false"}.Contains(t) || functions.Contains(t) || Regex.IsMatch(t,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*\.value$") || Regex.IsMatch(t,@"^(?:0[xX][0-9a-fA-F]+(?:\.[0-9a-fA-F]+)?|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)$"))continue;
                throw new ArgumentException("序列遥测可写TME.代号或TMB.代号，兼容.value；支持比较、与/或/非、括号、INENUM和数学表达式；不支持："+t);
            }
            var expression=p.Expression();if(p.at!=p.tokens.Count)throw new ArgumentException("序列判据含多余内容");
            if(!expression.IsBoolean)throw new ArgumentException("序列判据必须返回满足或不满足，请填写比较、INENUM或逻辑组合条件");
            if(p.rule.References.Count==0)throw new ArgumentException("判读要求必须引用至少一个遥测参数");
            return new SequencePredicate{Expression=expression,Program=p.rule};
        }
        private List<Action<RuleEvaluation>> Block(params string[] ends)
        {
            if(++depth>32)throw new ArgumentException("if嵌套超过32层");
            var result=new List<Action<RuleEvaluation>>();
            while(Current!="<结束>" && !ends.Contains(Current))
            {
                if(Take(";") || Take(","))continue;
                if(Take("if"))
                {
                    var conditions=new List<RuleExpression>();var branches=new List<List<Action<RuleEvaluation>>>();
                    do{conditions.Add(Expression());Expect("then");branches.Add(Block("elseif","else","end"));}while(Take("elseif"));
                    List<Action<RuleEvaluation>> otherwise=Take("else")?Block("end"):null;Expect("end");
                    result.Add(e=>{for(int i=0;i<conditions.Count;i++)if(RuleEvaluation.Truth(conditions[i].Get(e))){Run(branches[i],e);return;}if(otherwise!=null)Run(otherwise,e);});continue;
                }
                bool local=Take("local");string name=Current;at++;
                if(local){if(!Regex.IsMatch(name,@"^[\p{L}_][\p{L}\p{Nd}_]*$") || !variables.Add(name))throw new ArgumentException("局部变量名无效或重复："+name);}
                if(local && (name.StartsWith("gz",StringComparison.Ordinal) || new[]{"this","VAL","RET","RANGE","EL","EU","EV","ERR","NIL","true","false","Normal","Unusual","Error","SeriousError","OSTIME","YEAR","MONTH","DAY","HOUR","MINUTE","SECOND"}.Contains(name)))throw new ArgumentException("局部变量不能使用保留名称或gz前缀");
                if(local && Take(";")){result.Add(e=>e.Variables[name]=null);continue;}
                Expect("=");
                if(name=="RANGE")
                {
                    var ranges=ParseRange();result.Add(e=>{if(e.Ranges!=null)throw new ArgumentException("重复设置RANGE");e.Ranges=ranges.Select(r=>new[]{RuleEvaluation.Number(r[0].Get(e)),RuleEvaluation.Number(r[1].Get(e))}).ToList();});continue;
                }
                bool global=Regex.IsMatch(name,@"^gz[\p{L}\p{Nd}_]+$");if(global)rule.GlobalWrites.Add(name);
                if(!global && !variables.Contains(name) && !new[]{"VAL","RET","EL","EU","EV","ERR"}.Contains(name))throw new ArgumentException("不支持的赋值目标："+name);
                RuleExpression value=Expression();
                if(global){result.Add(e=>e.SetGlobal(name,value.Get(e)));continue;}
                result.Add(e=>{object v=value.Get(e);switch(name){case "VAL":e.Target=v;break;case "RET":string s=v as string;if(!new[]{"Normal","Unusual","Error","SeriousError"}.Contains(s))throw new ArgumentException("RET结论无效");e.Ret=s;break;case "EL":e.EL=RuleEvaluation.Number(v);break;case "EU":e.EU=RuleEvaluation.Number(v);break;case "EV":e.EV=RuleEvaluation.Number(v);break;case "ERR":e.ERR=RuleEvaluation.Number(v);break;default:if(name=="this.state" && (RuleEvaluation.Number(v)!=Decimal.Truncate(RuleEvaluation.Number(v)) || RuleEvaluation.Number(v)<Int32.MinValue || RuleEvaluation.Number(v)>Int32.MaxValue))throw new ArgumentException("this.state需要32位整数");e.Variables[name]=v;break;}});
            }
            depth--;return result;
        }
        private List<RuleExpression[]> ParseRange()
        {
            int start=at;Expect("[");RuleExpression lo=Expression();Take(",");List<RuleExpression[]> inner=Current=="["?ParseRange():new List<RuleExpression[]>();Take(",");RuleExpression hi=Expression();Expect("]");inner.Insert(0,new[]{lo,hi});if(inner.Count>3)throw new ArgumentException("RANGE最多3层");
            if(!tokens.Skip(start).Take(at-start).Any(t=>Char.IsLetter(t[0]) || t[0]=='_'))
            {var limits=inner.Select(r=>new[]{RuleEvaluation.Number(r[0].Get(null)),RuleEvaluation.Number(r[1].Get(null))}).ToArray();for(int i=0;i<limits.Length;i++)if(limits[i][0]>limits[i][1] || (i>0 && (limits[i][0]<limits[i-1][0] || limits[i][1]>limits[i-1][1])))throw new ArgumentException("RANGE上下限或嵌套顺序无效");}
            return inner;
        }
        public static void Run(IEnumerable<Action<RuleEvaluation>> statements,RuleEvaluation e){foreach(var statement in statements)statement(e);}
        private static int Priority(string token)
        {switch(token){case "||":return 1;case "&&":return 2;case "==":case "!=":return 3;case "<":case "<=":case ">":case ">=":return 4;case "+":case "-":return 5;case "*":case "/":case "%":return 6;case "^":return 7;default:return 0;}}
        private RuleExpression Expression(int min=1)
        {
            if(++expressionDepth>64)throw new ArgumentException("表达式嵌套超过64层");
            RuleExpression left;
            if(Take("-")){RuleExpression a=Expression(7);if(sequenceOnly&&a.IsBoolean)throw new ArgumentException("算术运算不能使用逻辑条件");left=new RuleExpression(e=>-RuleEvaluation.Number(a.Get(e)),null,a);}
            else if(Take("!")){RuleExpression a=Expression(7);if(sequenceOnly&&!a.IsBoolean)throw new ArgumentException("非运算需要逻辑条件，例如 !(TME.A1 == 1)");left=new RuleExpression(e=>!RuleEvaluation.Truth(a.Get(e)),null,a){IsBoolean=true};}
            else if(Take("(")){left=Expression();Expect(")");}
            else
            {
                string t=Current;at++;
                decimal n;
                if(t.StartsWith("0x",StringComparison.OrdinalIgnoreCase)){var parts=t.Substring(2).Split('.');n=Convert.ToUInt64(parts[0],16);if(parts.Length==2){decimal factor=1;foreach(char hex in parts[1]){factor/=16;n+=Convert.ToInt32(hex.ToString(),16)*factor;}}left=new RuleExpression(e=>n);}
                else if(Decimal.TryParse(t,NumberStyles.Float,CultureInfo.InvariantCulture,out n)){bool unit=false;if(Take("ms")){n/=1000;unit=true;}else unit=Take("s");left=new RuleExpression(e=>n){HasTimeUnit=unit};}
                else if(t=="NIL")left=new RuleExpression(e=>null);
                else if(t=="true" || t=="false")left=new RuleExpression(e=>t=="true"){IsBoolean=true};
                else if(new[]{"Normal","Unusual","Error","SeriousError"}.Contains(t))left=new RuleExpression(e=>t);
                else if(Take("("))
                {
                    var args=new List<RuleExpression>();if(Current!=")"){do{args.Add(Expression());}while(Take(","));}Expect(")");
                    if(t=="AFTER" || t=="HOLD" || t=="FRAMES")
                    {
                        if(initializing)throw new ArgumentException("时间和帧数函数用于状态或结论域");
                        if(args.Count<2 || args.Count>(t=="FRAMES"?3:2))throw new ArgumentException(t+"参数数量无效");
                        if(t=="FRAMES" && args[1].HasTimeUnit)throw new ArgumentException("FRAMES帧数不能带时间单位");
                        try{object rawAmount=args[1].Get(null);if(rawAmount==null)throw new ArgumentException(t+"的时间或帧数不能为NIL");decimal limit=RuleEvaluation.Number(rawAmount);if(limit<0 || limit>6000000 || (t=="FRAMES" && (limit<1 || Decimal.Truncate(limit)!=limit)))throw new ArgumentException(t+"的时间或帧数无效");}catch(NullReferenceException){}catch(KeyNotFoundException){}
                        if(t=="AFTER" && (args[0].Reference==null || !args[0].Reference.StartsWith("TC.")))throw new ArgumentException("AFTER的第一个参数需要TC指令事件");
                        string anchor=args.Count==3?args[2].Reference:"this";
                        if(t=="FRAMES" && (anchor==null || (anchor!="this" && !Regex.IsMatch(anchor,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$"))))throw new ArgumentException("FRAMES第三个参数需要TM.代号或this");
                        var spec=new RuleTemporal{Id=rule.Timers.Count,Kind=t,Condition=args[0],Amount=args[1],Anchor=anchor,Phase=phase,ExplicitAnchor=args.Count==3};rule.Timers.Add(spec);left=new RuleExpression(e=>e.Temporal(spec.Id),null,args.ToArray());
                    }
                    else if(t=="INENUM"){if(args.Count!=1 || args[0].Reference==null || !rule.References.Contains(args[0].Reference))throw new ArgumentException("INENUM需要一个遥测值引用");var arg=args[0];rule.EnumReferences.Add(arg.Reference);left=new RuleExpression(e=>e.InEnum(arg.Reference,arg.Get(e)),null,arg){IsBoolean=true};}
                    else if(new[]{"sin","cos","tan","asin","acos","exp","log","log10","abs","sqrt","min","max","pow"}.Contains(t))
                    {int count=new[]{"min","max","pow"}.Contains(t)?2:1;if(args.Count!=count)throw new ArgumentException(t+"参数数量应为"+count);if(sequenceOnly&&args.Any(a=>a.IsBoolean))throw new ArgumentException("数学函数需要数值参数，不能使用逻辑条件");left=new RuleExpression(e=>MathFunction(t,args.Select(a=>RuleEvaluation.Number(a.Get(e))).ToArray()),null,args.ToArray());}
                    else throw new ArgumentException("不支持的函数："+t);
                }
                else if(Regex.IsMatch(t,@"^TC\.[\p{L}_][\p{L}\p{Nd}_]*$")){rule.Commands.Add(t);left=new RuleExpression(e=>e.Command(t),t);}
                else if(new[]{"OSTIME","YEAR","MONTH","DAY","HOUR","MINUTE","SECOND"}.Contains(t)){if(!initializing)rule.UsesClock=true;left=new RuleExpression(e=>e.Clock(t));}
                else if(Regex.IsMatch(t,@"^gz[\p{L}\p{Nd}_]+$")){rule.GlobalReads.Add(t);left=new RuleExpression(e=>e.Global(t));}
                else if(t=="this" || Regex.IsMatch(t,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*$"))left=new RuleExpression(e=>{throw new ArgumentException("参数对象需使用.value取值");},t);
                else if(t=="this.value" || Regex.IsMatch(t,@"^this\.pre[0-9]$") || Regex.IsMatch(t,@"^(?:TM|TME|TMB)\.[\p{L}_][\p{L}\p{Nd}_]*\.(value|pre[0-9])$"))
                {rule.References.Add(t);left=new RuleExpression(e=>e.Read(t),t);}
                else if(variables.Contains(t))left=new RuleExpression(e=>{object v;if(!e.Variables.TryGetValue(t,out v))throw new RuleWaiting("变量未初始化："+t);return v;});
                else throw new ArgumentException("未知标识："+t+"；遥测使用TM.代号.value或this.value");
            }
            while(Priority(Current)>=min)
            {
                string op=Current;at++;RuleExpression a=left,b=Expression(Priority(op)+(op=="^"?0:1));
                bool logical=op=="&&"||op=="||",equal=op=="=="||op=="!=",comparison=new[]{"<","<=",">",">="}.Contains(op);
                if(sequenceOnly && (logical?(!a.IsBoolean||!b.IsBoolean):equal?(a.IsBoolean!=b.IsBoolean):(a.IsBoolean||b.IsBoolean)))throw new ArgumentException("运算符"+op+"两侧类型不匹配；与/或需要逻辑条件，比较和数学运算需要数值，区间请写两个比较并用与连接");
                left=new RuleExpression(e=>Binary(op,a,b,e),null,a,b){IsBoolean=logical||equal||comparison};
            }
            expressionDepth--;return left;
        }
        private static object Binary(string op,RuleExpression a,RuleExpression b,RuleEvaluation e)
        {
            object av;
            try{av=a.Get(e);}
            catch(RuleWaiting missing)
            {
                if(e==null || !e.PartialLogic || (op!="&&" && op!="||"))throw;
                object other;
                try{other=b.Get(e);}catch(RuleWaiting){throw missing;}
                bool truth=RuleEvaluation.Truth(other);
                if(op=="||" && truth)return true;
                if(op=="&&" && !truth)return false;
                throw;
            }
            if(op=="&&")return RuleEvaluation.Truth(av) && RuleEvaluation.Truth(b.Get(e));
            if(op=="||")return RuleEvaluation.Truth(av) || RuleEvaluation.Truth(b.Get(e));
            object bv=b.Get(e);
            if(op=="==" || op=="!=") {bool same=av==null || bv==null ? av==null && bv==null : av is bool || bv is bool || av is string || bv is string ? Object.Equals(av,bv):RuleEvaluation.Number(av)==RuleEvaluation.Number(bv);return op=="=="?same:!same;}
            decimal x=RuleEvaluation.Number(av),y=RuleEvaluation.Number(bv);
            switch(op){case "+":return x+y;case "-":return x-y;case "*":return x*y;case "/":return x/y;case "%":return x%y;case "^":return Convert.ToDecimal(Math.Pow((double)x,(double)y));case "<":return x<y;case "<=":return x<=y;case ">":return x>y;case ">=":return x>=y;default:throw new ArgumentException("未知运算符");}
        }
        private static decimal MathFunction(string name,decimal[] a)
        {
            if(name=="abs")return Math.Abs(a[0]);if(name=="min")return Math.Min(a[0],a[1]);if(name=="max")return Math.Max(a[0],a[1]);
            double x=(double)a[0],v;
            switch(name){case "sin":v=Math.Sin(x);break;case "cos":v=Math.Cos(x);break;case "tan":v=Math.Tan(x);break;case "asin":v=Math.Asin(x);break;case "acos":v=Math.Acos(x);break;case "exp":v=Math.Exp(x);break;case "log":v=Math.Log(x);break;case "log10":v=Math.Log10(x);break;case "sqrt":v=Math.Sqrt(x);break;default:v=Math.Pow(x,(double)a[1]);break;}
            if(Double.IsNaN(v) || Double.IsInfinity(v))throw new ArgumentException(name+"的输入不在定义域或结果非有限数");return Convert.ToDecimal(v);
        }
    }
}
