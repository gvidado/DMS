using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using TelemetryMonitor;
using Monitor=TelemetryMonitor.Monitor;

public static class CanonicalComparisonTest
{
    private static int checks;
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static object Json(string text){return new JavaScriptSerializer().DeserializeObject(text);}
    public static int Main(string[] args)
    {
        var a=Json("{\"b\":2,\"a\":{\"y\":1.2000,\"x\":1},\"rows\":[{\"value\":\"中文\",\"row\":2},3]}");
        var b=Json("{\"rows\":[{\"row\":2,\"value\":\"中文\"},3],\"a\":{\"x\":1.0,\"y\":1.2},\"b\":2}");
        Check(CanonicalJson.Hash(a)==CanonicalJson.Hash(b),"nested object order and equivalent numeric formatting do not change fingerprints");
        Check(!CanonicalJson.Equivalent(Json("[1,2]"),Json("[2,1]")),"array and sequence order remains significant");
        Check(!CanonicalJson.Equivalent(Json("{\"a\":1}"),Json("{\"A\":1}")),"field spelling and case remain significant");
        Check(!CanonicalJson.Equivalent(null,""),"null and empty text remain distinct");
        Check(!CanonicalJson.Equivalent(Json("18446744073709551615"),Json("18446744073709551614")),"uint64 values retain their exact distinguishing bit");
        var entry=JsonFile.Decode<SequenceHistoryEvent>("{\"Event\":\"步骤结果\",\"RunId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Time\":\"2026-11-06 23:59:59.000\",\"Mode\":\"judge\",\"Status\":\"通过\",\"Values\":\"9.79\",\"Input\":{\"Row\":2,\"Description\":\"跨日记录\"}}");
        string stable=CanonicalJson.Hash(entry);
        foreach(var field in typeof(SequenceHistoryEvent).GetFields().Reverse())field.GetValue(entry);
        var cloned=JsonFile.Decode<SequenceHistoryEvent>(JsonFile.Encode(entry));
        Check(CanonicalJson.Hash(cloned)==stable,"history serialization remains stable after reflection caches and JSON round trips");
        cloned.Time="2026-11-07 00:00:00.000";Check(CanonicalJson.Hash(cloned)!=stable,"an actual stored timestamp change is still detected");
        cloned.Time=entry.Time;cloned.Status="异常";Check(CanonicalJson.Hash(cloned)!=stable,"a changed sequence conclusion is still detected");
        cloned.Status=entry.Status;cloned.Values="9.78";Check(CanonicalJson.Hash(cloned)!=stable,"changed engineering values are still detected");
        var culture=Thread.CurrentThread.CurrentCulture;
        try{Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo("tr-TR");Check(CanonicalJson.Hash(a)==CanonicalJson.Hash(b) && CanonicalJson.Hash(entry)==stable,"semantic fingerprints are independent of current numeric and text culture");}
        finally{Thread.CurrentThread.CurrentCulture=culture;}
        var rule1=new ContinuousRule{Code="A1",Script="RET=Normal;",Bindings=new Dictionary<string,RuleParameterBinding>{{"TME.A1",new RuleParameterBinding{ParameterId="a",SourceIP="127.0.0.1"}},{"TMB.A2",new RuleParameterBinding{ParameterId="b",SourceIP="127.0.0.2"}}}};
        var rule2=JsonFile.Decode<ContinuousRule>(JsonFile.Encode(rule1));rule2.Bindings=rule2.Bindings.Reverse().ToDictionary(p=>p.Key,p=>p.Value);
        Check(Monitor.ContinuousDefinition(rule1)==Monitor.ContinuousDefinition(rule2),"rule duplicate detection ignores binding-map insertion order");
        rule2.Bindings["TME.A1"].SourceIP="127.0.0.3";Check(Monitor.ContinuousDefinition(rule1)!=Monitor.ContinuousDefinition(rule2),"a real telemetry binding change is not ignored");
        var parameter1=new Parameter{Name="p",Code="A1",Group="g",Scale=1,Enums=new Dictionary<string,string>{{"2","two"},{"1","one"}}};
        var parameter2=JsonFile.Decode<Parameter>(JsonFile.Encode(parameter1));parameter2.Enums=parameter2.Enums.Reverse().ToDictionary(p=>p.Key,p=>p.Value);
        var definition=typeof(DecoderImport).GetMethod("Definition",BindingFlags.Static|BindingFlags.NonPublic);
        Check((string)definition.Invoke(null,new object[]{parameter1})==(string)definition.Invoke(null,new object[]{parameter2}),"decoder duplicate detection ignores enum-map order");
        parameter2.Scale=2;Check((string)definition.Invoke(null,new object[]{parameter1})!=(string)definition.Invoke(null,new object[]{parameter2}),"changed decoder scaling remains a real difference");
        var command=new ObservedCommand{Number=1,Name="command",PayloadHex="AA 01",DestinationIPs=new List<string>{"192.168.1.113","192.168.1.112"},Categories=new List<CommandCategory>()};
        var step=new SequenceStep{Code="TC.K001"};var signature=typeof(Monitor).GetMethod("SequenceCommandSignature",BindingFlags.Static|BindingFlags.NonPublic);string first=(string)signature.Invoke(null,new object[]{step,command});command.DestinationIPs.Reverse();
        Check(first==(string)signature.Invoke(null,new object[]{step,command}),"command dependency signature is stable for equivalent destination ordering");
        command.PayloadHex="AA 02";Check(first!=(string)signature.Invoke(null,new object[]{step,command}),"changed command bytes still invalidate the dependency");
        string root=Path.Combine(Path.GetFullPath(args[0]),"test-output","canonical-config-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string file=Path.Combine(root,"sequences.json");File.WriteAllText(file,"{\"b\":2,\"a\":1}");string original=File.ReadAllText(file);
            var plan=typeof(Monitor).GetMethod("PlanCommandReferenceFile",BindingFlags.Static|BindingFlags.NonPublic).MakeGenericMethod(typeof(Dictionary<string,object>));
            var before=new Dictionary<string,object>{{"a",1},{"b",2}};var after=new Dictionary<string,object>{{"a",1},{"b",3}};
            var change=(CommandEditFile)plan.Invoke(null,new object[]{root,"sequences.json",before,after});Check(Convert.FromBase64String(change.Before).SequenceEqual(File.ReadAllBytes(file)) && File.ReadAllText(file)==original,"equivalent reordered config passes edit preflight and preserves the exact before-image");
            File.WriteAllText(file,"{\"b\":7,\"a\":1}");bool rejected=false;try{plan.Invoke(null,new object[]{root,"sequences.json",before,after});}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}
            Check(rejected && File.ReadAllText(file).Contains("7"),"actual external config changes are rejected without overwrite");
        }
        finally{if(!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetFullPath(args[0]),"test-output")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("test cleanup path");Directory.Delete(root,true);}
        Console.WriteLine("All "+checks+" canonical comparison checks passed.");return 0;
    }
}
