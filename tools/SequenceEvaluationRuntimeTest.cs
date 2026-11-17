using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TelemetryMonitor;
using Monitor=TelemetryMonitor.Monitor;

public static class SequenceEvaluationRuntimeTest
{
    private static int checks,bugs,serial=16370;
    private static bool repro;
    private static void Check(bool ok,string message)
    {checks++;if(!ok && repro){bugs++;Console.WriteLine("REPRO "+message);return;}if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
    private static Dictionary<string,object> Map(object data){return JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(data));}
    private static Dictionary<string,object> Row(Monitor m,string id){return ((IEnumerable)Map(m.SequenceState(id,1,"execute"))["rows"]).Cast<Dictionary<string,object>>().First();}
    private static ParameterHistory History(Monitor m)
    {return ((Dictionary<string,Dictionary<string,ParameterHistory>>)typeof(Monitor).GetField("engineeringHistory",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(m))["127.0.0.1"]["a1"];}
    private static void Packet(Monitor m,double value,bool invalid=false,string source="127.0.0.1")
    {
        var data=new byte[1030];Action<int,int> u16=(at,n)=>{data[at]=(byte)(n>>8);data[at+1]=(byte)n;};
        u16(6,0x0808);u16(8,0xc000|((++serial)&16383));u16(10,1017);data[12]=0x30;data[13]=3;data[14]=25;u16(18,0x1004);
        ulong rawValue=(ulong)Math.Round(value*10000);for(int i=26;i>=22;i--){data[i]=(byte)rawValue;rawValue>>=8;}
        byte[] number=BitConverter.GetBytes(invalid?Single.NaN:1f);if(BitConverter.IsLittleEndian)Array.Reverse(number);Array.Copy(number,0,data,35,4);
        SppChecksum.Write(data,6,data.Length-6);
        lock(m.Gate)typeof(Monitor).GetMethod("Process",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(m,new object[]{data,source,false,6000,(long)0});
    }
    private static SequenceSendWork Begin(Monitor m,string id)
    {
        var steps=m.PrepareSequence(id);m.StartSequence(id,steps,"isolated test","test",id);
        SequenceSendWork work=null;DateTime end=DateTime.UtcNow.AddSeconds(2);
        while(Row(m,id)["actual"]==null){work=m.SequenceTick("test");if(DateTime.UtcNow>end)throw new Exception("step activation timeout");Thread.Sleep(2);}
        if(work!=null)Check(m.SequenceCanSend(work),"active send work is valid before its test callback");
        return work;
    }
    private static void End(Monitor m)
    {var state=Map(m.SequenceSummary("execute"));if((bool)state["running"])m.StopSequenceRun((string)state["runId"],"isolated test end");}
    public static int Main(string[] args)
    {
        repro=args.Contains("--repro");string root=Path.Combine(args[0],"test-output","sequence-evaluation-fix","runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"config"));
        try
        {
            var settings=JsonFile.Load<Settings>(Path.Combine(args[0],"config","server.json"));settings.BindAddress="127.0.0.1";settings.SourceIPs=new[]{"127.0.0.1","127.0.0.2"};settings.MappingConfirmed=true;settings.Profile="全程";settings.MulticastGroup="";settings.MulticastInterface="";settings.DataTimeoutMs=2000;
            JsonFile.Save(Path.Combine(root,"config","server.json"),settings);
            JsonFile.Save(Path.Combine(root,"config","parameters.json"),new List<Parameter>{new Parameter{Id="a1",Code="A1",Name="a",Group="工仪全程",Offset=0,Length=5,Type="uint",Scale=.0001,Precision=3,Supported=true},new Parameter{Id="a4",Code="A4",Name="optional",Group="工仪全程",Offset=13,Length=4,Type="float32",Scale=1,Precision=3,Supported=true}});
            File.WriteAllText(Path.Combine(root,"config","observed-commands.json"),"{\"SchemaVersion\":1,\"SourceIP\":\"127.0.0.1\",\"NextNumber\":2,\"Commands\":[{\"Number\":1,\"Name\":\"test only\",\"DestinationIP\":\"127.0.0.2\",\"DestinationIPs\":[\"127.0.0.2\"],\"PayloadHex\":\"AA BB 01\"}]}");
            JsonFile.Save(Path.Combine(root,"config","tc-bindings.json"),new TCBindingConfig{Bindings=new List<TCBinding>{new TCBinding{Code="K001",Number=1}}});
            var definitions=new List<SequenceDefinition>();
            foreach(var item in new[]{new[]{"fast","K001","abs(TM.A1.value - 9.8) < 1"},new[]{"plain","","abs(TM.A1.value - 9.8) < 1"},new[]{"or","","abs(TM.A1.value - 9.8) < 1 || TM.A4.value > 0"},new[]{"reverse-or","","TM.A4.value > 0 || abs(TM.A1.value - 9.8) < 1"}})
                definitions.Add(new SequenceDefinition{Id=definitions.Count.ToString("x").PadLeft(32,'0'),Code="XLZ"+(definitions.Count+1).ToString("D4"),Name=item[0],Mode="execute",SourceIP="127.0.0.1",Rows=new List<SequenceRow>{new SequenceRow{Row=2,Command=item[1],Condition=item[2],Expected=".2",Timeout="2"}}});
            var passive=new SequenceDefinition{Id=new string('f',32),Code="XLP0001",Name="passive race",Mode="judge",SourceIP="127.0.0.1",Rows=new List<SequenceRow>{new SequenceRow{Row=2,Command="K001",Condition="abs(TM.A1.value - 9.8) < 1",Timeout="2"}}};definitions.Add(passive);
            var chain=new SequenceDefinition{Id=new string('e',32),Code="XLP0002",Name="single-frame chain",Mode="judge",SourceIP="127.0.0.1",Rows=new List<SequenceRow>{new SequenceRow{Row=2,Condition="abs(TM.A1 - 9.8) < 1"},new SequenceRow{Row=3,Condition="abs(TM.A1 - 9.8) < 1"}}};definitions.Add(chain);
            var planned=new SequenceDefinition{Id=new string('d',32),Code="XLP0003",Name="planned latest value",Mode="judge",SourceIP="127.0.0.1",Rows=new List<SequenceRow>{new SequenceRow{Row=2,Condition="abs(TM.A1 - 9.8) < 1",Expected="F:.1"}}};definitions.Add(planned);
            var parallel=new SequenceDefinition{Id=new string('c',32),Code="XLP0004",Name="active parallel rows",Mode="judge",SourceIP="127.0.0.1",Rows=new List<SequenceRow>{new SequenceRow{Row=2,Keyword="PAR"},new SequenceRow{Row=3,Keyword="HOLD",Condition="TM.A1>0"},new SequenceRow{Row=4,Condition="TM.A1==10"},new SequenceRow{Row=5,Keyword="CHANGE"}}};definitions.Add(parallel);
            JsonFile.Save(Path.Combine(root,"config","sequences.json"),new SequenceConfiguration{Sequences=definitions});
            using(var m=new Monitor(root))
            {
                // Deterministic engine harness: no sockets, capture, service timer or physical devices.
                typeof(Monitor).GetProperty("Listening").GetSetMethod(true).Invoke(m,new object[]{true});
                string fast=definitions[0].Id,plain=definitions[1].Id,or=definitions[2].Id;
                var work=Begin(m,fast);Packet(m,9.79);m.SequenceSent(work,true,"");
                Check((string)Row(m,fast)["status"]=="通过","a=9.79 received between UDP send and completion callback satisfies abs(a-9.8)<1");End(m);
                work=Begin(m,fast);Packet(m,9.79);double actualSend=Monitor.RuleSeconds();m.SequenceSent(work,true,"",actualSend);
                Check((string)Row(m,fast)["status"]!="通过","a frame before the actual send timestamp cannot satisfy post-command judgment");Packet(m,9.79);Check((string)Row(m,fast)["status"]=="通过","the first new valid post-command frame passes");End(m);
                work=Begin(m,fast);Packet(m,9.79);m.SequenceSent(work,false,"controlled send failure");Check((string)Row(m,fast)["status"]!="通过" && !((bool)Map(m.SequenceSummary("execute"))["running"]),"a failed send cannot turn a matching value into a successful step");End(m);
                Packet(m,0,true);Begin(m,or);Packet(m,9.79,true);Check((string)Row(m,or)["status"]=="通过","a satisfied OR branch is not blocked by an invalid unused parameter");End(m);
                Packet(m,0,true);Begin(m,definitions[3].Id);Packet(m,9.79,true);Check((string)Row(m,definitions[3].Id)["status"]=="通过","an invalid left OR branch also permits a valid true right branch");End(m);
                Packet(m,9.79);History(m).Latest.Time=DateTime.UtcNow.AddHours(-2);
                var displayed=((IEnumerable)Map(m.Snapshot(new List<string>{"a1"}))["rows"]).Cast<Dictionary<string,object>>().First();
                Check((string)displayed["status"]=="有效","fresh monotonic data does not display expired after a wall-clock timestamp shift");
                History(m).LatestDataTime-=100;Begin(m,plain);Packet(m,9.79);Check((string)Row(m,plain)["status"]=="通过","fresh data replaces truly expired data and resumes a simple condition");End(m);
                Packet(m,0);Begin(m,plain);Packet(m,9.79,source:"127.0.0.2");Check((string)Row(m,plain)["status"]!="通过","a matching value from another source IP cannot satisfy the bound source");
                var diagnostic=Map(m.SequenceDiagnostic(plain,"execute"));var detail=((IEnumerable)diagnostic["rows"]).Cast<Dictionary<string,object>>().First();var parameter=((IEnumerable)detail["parameters"]).Cast<Dictionary<string,object>>().First();Check((string)parameter["sourceIP"]=="127.0.0.1" && (string)parameter["latestDisplayedSource"]=="127.0.0.2" && !(bool)parameter["displaySourceMatchesBinding"],"diagnostic captures actual bound source separately from the parameter page's most recent source");
                Packet(m,9.79);Check((string)Row(m,plain)["status"]=="通过","the correct source satisfies the same engineering condition");End(m);
                typeof(ParameterHistory).GetField("serial",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(History(m),(long)Int32.MaxValue+100);Packet(m,0);
                bool repeated=true;for(int i=0;i<24;i++){Packet(m,0);Begin(m,plain);Packet(m,9.79);repeated&=(string)Row(m,plain)["status"]=="通过";End(m);}
                Check(repeated && History(m).Samples[0].sampleNumber>Int32.MaxValue,"24 repeated runs pass with unchanged values, 14-bit frame wrap and sample counts beyond 32 bits");
                Begin(m,plain);Check((string)Row(m,plain)["status"]=="通过","a current valid matching value is evaluated immediately when a step becomes due");End(m);
                Packet(m,0);m.StartSequence(chain.Id,m.PrepareSequence(chain.Id),"single frame chain","test");Packet(m,9.79);
                Check(!((bool)Map(m.SequenceSummary("judge"))["running"])&&((IEnumerable)Map(m.SequenceState(chain.Id,1,"judge"))["rows"]).Cast<Dictionary<string,object>>().All(r=>(string)r["status"]=="通过"),"one transient matching frame can satisfy adjacent due parameter steps without being lost during the transition");End(m);
                m.StartSequence(planned.Id,m.PrepareSequence(planned.Id),"planned check","test");
                Check((bool)Map(m.SequenceSummary("judge"))["running"],"an already-matching current value never bypasses a future F schedule");Thread.Sleep(130);m.SequenceTick("test");
                Check(!((bool)Map(m.SequenceSummary("judge"))["running"]),"a due scheduled step checks the valid current value even when no extra frame arrives");End(m);
                m.StartSequence(parallel.Id,m.PrepareSequence(parallel.Id),"parallel active set","test");Packet(m,10);
                Check(!((bool)Map(m.SequenceSummary("judge"))["running"])&&((IEnumerable)Map(m.SequenceState(parallel.Id,1,"judge"))["rows"]).Cast<Dictionary<string,object>>().All(r=>(string)r["status"]=="通过"),"parallel parameter and HOLD evaluation remain correct as completed active rows are removed");End(m);
                Packet(m,0);Begin(m,plain);serial--;Packet(m,9.79);Check((string)Row(m,plain)["status"]!="通过" && m.Duplicates>0,"duplicate frame counters remain filtered instead of falsely satisfying a new run");Packet(m,9.79);Check((string)Row(m,plain)["status"]=="通过","a new counter after a duplicate still triggers judgment");End(m);
                // Compile directly to isolate capture-event processing without starting Npcap.
                var compile=typeof(Monitor).GetMethod("CompileSequence",BindingFlags.Instance|BindingFlags.NonPublic);
                object[] arguments={passive,2.0,null,null,null,0,null,null};var passiveSteps=(List<SequenceStep>)compile.Invoke(m,arguments);
                m.StartSequence(passive.Id,passiveSteps,"isolated passive test","test");
                var captured=new SendRecord{Time=DateTime.UtcNow,SourceIP="127.0.0.1",DestinationIP="127.0.0.2",Payload=new byte[]{0xaa,0xbb,1}};
                lock(m.Gate){typeof(Monitor).GetMethod("QueueSequenceCaptured",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(m,new object[]{captured});Packet(m,9.79);}
                DateTime until=DateTime.UtcNow.AddSeconds(1);while((string)Row(m,passive.Id)["status"]!="通过" && DateTime.UtcNow<until)Thread.Sleep(5);
                Check((string)Row(m,passive.Id)["status"]=="通过","telemetry arriving before the queued command-monitor callback is evaluated once that command is confirmed");
                var persisted=File.ReadAllLines(Path.Combine(root,"data","sequence-history.jsonl")).Select(line=>Map(JsonFile.Decode<SequenceHistoryEvent>(line))).Where(e=>(string)e["Event"]=="步骤结果" && (string)e["Status"]=="通过").Last();
                Check(persisted["Diagnostic"]!=null,"step archives persist evaluation time, input quality and source diagnostic");
                Console.WriteLine("Completed "+checks+" engine checks; reproduced defects="+bugs+"; no network traffic sent.");
            }
            return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
