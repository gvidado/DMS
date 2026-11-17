using System;
using System.IO;
using System.Linq;
using System.Reflection;
using TelemetryMonitor;
public static class JudgmentArchiveBoundaryTest
{
    private static int checks;
    private static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    public static int Main(string[] args)
    {
        string workspace=Path.GetFullPath(args[0]),root=Path.Combine(workspace,"test-output","archive-boundary-"+Guid.NewGuid().ToString("N"));
        if(!Path.GetFullPath(root).StartsWith(workspace+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Fixture path must stay inside workspace");
        Directory.CreateDirectory(root);
        try
        {
            var assembly=typeof(Parameter).Assembly;var type=assembly.GetType("TelemetryMonitor.JudgmentArchiveStore");var bindingType=assembly.GetType("TelemetryMonitor.RuleBinding");
            Func<object> create=()=>Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{root},null);object store=create(),binding=Activator.CreateInstance(bindingType,true);
            bindingType.GetField("Main").SetValue(binding,new Parameter{Id="sheet/A",Code="A",Name="Boundary parameter"});bindingType.GetField("Source").SetValue(binding,"127.0.0.1");
            var rule=new ContinuousRule{Id="rule-a",Code="A",Name="Boundary parameter",Level="一般",Script="RET=Normal;"};
            Action<string> normal=time=>type.GetMethod("RecordNormal",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(store,new object[]{rule,binding,"run",time,"实时UDP"});
            Action<JudgmentAbnormalEntry,string> abnormal=(incident,time)=>type.GetMethod("RecordAbnormal",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(store,new object[]{incident,time});
            Action flush=()=>type.GetMethod("FlushDaily",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(store,null);
            Func<string,DailyJudgmentArchive> read=date=>JsonFile.Load<DailyJudgmentArchive>(Path.Combine(root,"data","archives","continuous",date+".json"));
            normal("2026-11-01 23:59:50.000");normal("2026-11-01 23:59:51.000");flush();Check(read("2026-11-01").Records.Count==1,"multiple passing observations make one daily result");
            var entry=new JudgmentAbnormalEntry{Id=1,ParameterId="sheet/A",Code="A",Name=rule.Name,SourceIP="127.0.0.1",Time="2026-11-01 23:59:59.000",Result="Error",Script="RET=Error;",ValueText="2",EngineeringValue="2"};
            abnormal(entry,entry.Time);abnormal(entry,entry.Time);flush();Check(read("2026-11-01").Records.Count==1 && read("2026-11-01").Records[0].Kind=="abnormal","an abnormal episode replaces the prior pass and is deduplicated");
            abnormal(entry,"2026-11-02 00:00:01.000");flush();var carried=read("2026-11-02").Records.Single();Check(carried.IncidentId==1 && carried.Time==entry.Time && carried.ObservedAt.StartsWith("2026-11-02"),"an episode spanning midnight remains abnormal on the new day with its original incident time");
            normal("2026-11-02 00:00:02.000");flush();Check(read("2026-11-02").Records.Single().Kind=="abnormal","later recovery cannot erase that day's abnormal history");
            normal("2026-11-03 00:00:01.000");flush();Check(read("2026-11-03").Records.Single().Kind=="normal","a following day with only passes starts an independent daily summary");
            abnormal(entry,"2026-11-01 23:59:59.000");flush();Check(Directory.GetFiles(Path.Combine(root,"data","archives","continuous"),"*.json").Length==3 && read("2026-11-01").Records.Count==1,"switching between days neither overwrites nor duplicates historical daily files");
            string path=Path.Combine(root,"data","archives","continuous","2026-11-03.json"),before=File.ReadAllText(path);store=create();normal("2026-11-03 12:00:00.000");flush();Check(File.ReadAllText(path)==before,"a restored daily store keeps a normal result unchanged across restart");
            Console.WriteLine("All "+checks+" archive date-boundary checks passed.");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
