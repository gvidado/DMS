using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal static class SequenceShorthandTestHost
    {
        private static int checks;
        private static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
        private static RuleEvaluation Values()
        {
            var values=new Dictionary<string,object>{{"TME.A0.value",9.79m},{"TMB.A1.value",1m}};
            return new RuleEvaluation{Read=name=>values[name],InEnum=(name,value)=>name=="TMB.A1.value"&&Convert.ToDecimal(value)==1};
        }
        private static void Reject(string text)
        {
            try{RuleParser.CompileSequence(text);}catch(ArgumentException){Check(true,"unsupported sequence syntax stays rejected: "+text);return;}
            throw new Exception("Unexpected sequence acceptance: "+text);
        }
        public static int Main()
        {
            var cases=new[]{
                "abs(TME.A0 - 9.8) < 1",
                "TME.A0 >= 9 && TME.A0 <= 10",
                "INENUM(TMB.A1)",
                "!(TMB.A1 == 0) AND TME.A0 > 9",
                "非(TMB.A1 == 0) 与 (TME.A0 > 9 或 TME.A0 < 0)",
                "TME.A0 > 9 && TME.A0.value < 10",
                "abs(TME.A0.value - 9.8) < 1 && INENUM(TMB.A1.value)"
            };
            foreach(string script in cases)
            {
                var condition=RuleParser.CompileSequence(script);
                Check(RuleEvaluation.Truth(condition.Expression.Get(Values())),"shorthand or legacy sequence evaluates: "+script);
            }
            var mixed=RuleParser.CompileSequence("TME.A0 > 9 && TME.A0.value < 10");
            Check(mixed.Program.References.Count==1&&mixed.Program.References.Contains("TME.A0.value"),"mixed spellings share one canonical sample binding");
            var enumeration=RuleParser.CompileSequence("INENUM(TMB.A1)");
            Check(enumeration.Program.EnumReferences.SetEquals(new[]{"TMB.A1.value"}),"shorthand INENUM retains the canonical enumeration binding");
            var historical=RuleParser.Compile("if(TME.A0.value>TME.A0.pre1) then RET=Normal; else RET=Error; end");
            Check(historical.References.Contains("TME.A0.pre1")&&historical.References.Contains("TME.A0.value"),"continuous-rule current and historical attributes remain unchanged");
            foreach(string text in new[]{"TME.A0.pre0 > 0","TME.A0.raw > 0","this.value > 0","A0 > 0","TME.A0","TME.A0 && TMB.A1 > 0","1 < TME.A0 < 10","TME.A0 = 1"})Reject(text);
            Console.WriteLine("All "+checks+" sequence shorthand checks passed.");return 0;
        }
    }
}
