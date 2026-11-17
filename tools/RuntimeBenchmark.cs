using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using TelemetryMonitor;
using RuntimeMonitor=TelemetryMonitor.Monitor;

internal static class RuntimeBenchmark
{
    private const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    private static void Put(byte[] data,int at,int value){data[at]=(byte)(value>>8);data[at+1]=(byte)value;}
    private static byte[] Packet(int fields,int index,int sequence)
    {
        var data=new byte[24+fields*2];Put(data,0,2050);Put(data,4,data.Length-7);Put(data,6,0x0800|8+index);Put(data,8,0xc000|sequence);Put(data,10,data.Length-13);data[12]=0x30;data[13]=3;data[14]=25;Put(data,18,4100);for(int i=0;i<fields;i++)Put(data,22+i*2,100);SppChecksum.Write(data,6,data.Length-6);return data;
    }
    private static double Measure(int count,Action action)
    {GC.Collect();GC.WaitForPendingFinalizers();var timer=Stopwatch.StartNew();for(int i=0;i<count;i++)action();return timer.Elapsed.TotalMilliseconds/count;}
    private static object Read(object owner,string name){return owner.GetType().GetField(name,Hidden).GetValue(owner);}
    private static void Set(object owner,string name,object value){owner.GetType().GetField(name,BindingFlags.Public|Hidden).SetValue(owner,value);}
    private static int Main(string[] args)
    {
        try
        {
            int fields=Int32.Parse(args[1]),sequence=0;var result=new Dictionary<string,object>();result["parameters"]=fields*6;
            using(var monitor=new RuntimeMonitor(args[0]))
            {
                var process=(Action<byte[],string,bool,int,long>)Delegate.CreateDelegate(typeof(Action<byte[],string,bool,int,long>),monitor,typeof(RuntimeMonitor).GetMethod("Process",Hidden));
                var health=(Timer)Read(monitor,"health");health.Dispose();((Timer)Read(monitor,"judgmentTimer")).Dispose();Set(monitor,"<Listening>k__BackingField",true);
                Action receive=()=>process(Packet(fields,0,++sequence),"127.0.0.1",false,53000,0);for(int i=0;i<10;i++)receive();
                result["receiveDecodeRawMsPerPacket"]=Measure(150,receive);if(monitor.Accepted!=160)throw new Exception("unexpected frame acceptance");
                monitor.ContinuousList();result["judgmentDisplayMsPerRequest"]=Measure(5,()=>monitor.ContinuousList());
                var selected=monitor.Catalog.Select(p=>p.Id).ToList();result["snapshotMsPerRequest"]=Measure(80,()=>monitor.Snapshot(selected));
                var auth=new Auth(args[0]);auth.Add("runtimeqa","性能测试","runtime-local-test-password",true,true,selected);var session=auth.Login("runtimeqa","runtime-local-test-password");auth.Select(session,selected);result["resolveSelectionMsPerRequest"]=Measure(300,()=>auth.ResolveSelection(session,monitor.Catalog));
                monitor.StartJudgment();receive();result["receiveWithJudgmentMsPerPacket"]=Measure(30,receive);
                var eventType=typeof(RuntimeMonitor).Assembly.GetType("TelemetryMonitor.JudgmentEvent");var tick=Activator.CreateInstance(eventType);eventType.GetField("Timer").SetValue(tick,true);var evaluate=typeof(RuntimeMonitor).GetMethod("ProcessJudgmentEvent",Hidden);result["idleJudgmentTickMs"]=Measure(100,()=>evaluate.Invoke(monitor,new[]{tick}));monitor.StopJudgment();
                var registry=(IDictionary)Read(monitor,"sequenceTasks");var contextType=typeof(RuntimeMonitor).Assembly.GetType("TelemetryMonitor.SequenceRuntimeContext");
                for(int i=0;i<1000;i++){var item=Activator.CreateInstance(contextType);Set(item,"RunId",i.ToString("x32"));Set(item,"Ordinal",(long)i);Set(item,"FrozenSummary",new object());Set(item,"DetailsArchived",true);Set(item,"Definition",new SequenceDefinition{Mode=i%2==0?"judge":"execute"});registry.Add(i.ToString("x32"),item);}
                var dirty=typeof(RuntimeMonitor).GetField("sequenceRegistryDirty",Hidden);if(dirty!=null)dirty.SetValue(monitor,true);
                var refresh=typeof(RuntimeMonitor).GetMethod("RefreshSequenceTasks",Hidden);refresh.Invoke(monitor,null);
                var each=(Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>),monitor,typeof(RuntimeMonitor).GetMethod("EachSequence",Hidden));result["sequenceTickWith1000CompletedTasksMs"]=Measure(400,()=>each(()=>{}));
                result["accepted"]=monitor.Accepted;result["rules"]=fields*6;
            }
            Console.WriteLine(JsonFile.Encode(result));return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
