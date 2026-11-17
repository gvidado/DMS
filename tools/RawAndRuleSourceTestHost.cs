using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TelemetryMonitor;

// Isolated protocol and source-binding regression; no socket is opened.
internal static class RawAndRuleSourceTestHost
{
    private static int checks;
    private static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
    private static Dictionary<string,object> Object(object value){return JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(value));}
    private static byte[] Bytes(int outer,int apid=8,bool differentFields=false)
    {
        var bytes=new byte[26];Action<int,int> word=(offset,value)=>{bytes[offset]=(byte)(value>>8);bytes[offset+1]=(byte)value;};
        word(0,outer);word(4,bytes.Length-7);word(6,(differentFields?0xF000:0x0800)|apid);word(8,differentFields?1:0xC001);word(10,bytes.Length-13);
        bytes[12]=48;bytes[13]=(byte)(differentFields?99:3);bytes[14]=(byte)(differentFields?87:25);word(18,differentFields?0x77AA:4100);word(22,100);SppChecksum.Write(bytes,6,bytes.Length-6);return bytes;
    }
    private static bool Capture(TelemetryMonitor.Monitor monitor,byte[] bytes,out bool valid,string ip="127.0.0.1")
    {
        var args=new object[]{bytes,ip,false,6000,0L,false,null};bool matched=(bool)typeof(TelemetryMonitor.Monitor).GetMethod("CaptureRawSpp",Flags).Invoke(monitor,args);valid=(bool)args[5];return matched;
    }
    private static int Main(string[] args)
    {
        try
        {
            string rawRoot=Path.GetFullPath(args[0]),ruleRoot=Path.GetFullPath(args[1]);string rawPath=Path.Combine(rawRoot,"config","decoder.json"),rulePath=Path.Combine(ruleRoot,"config","decoder.json");
            var raw=JsonFile.Load<DecoderConfiguration>(rawPath);raw.Validate();Check(raw.Packets[0].OuterSourceId<0&&raw.Packets[0].MonitorRaw&&!raw.Packets[0].Enabled,"slash source is valid for raw monitoring without telemetry decoding");
            using(var monitor=new TelemetryMonitor.Monitor(rawRoot))
            {
                bool valid;var bytes=Bytes(0x1234,8,true);Check(Capture(monitor,bytes,out valid)&&valid,"APID-only raw monitoring ignores outer identifier, primary flags and secondary routing fields");
                Check(Capture(monitor,Bytes(0x0422),out valid)&&valid,"the same slash definition accepts a different outer data source");
                var list=Object(monitor.RawSppList("raw",1));var rows=(System.Collections.IEnumerable)list["records"];var latest=rows.Cast<Dictionary<string,object>>().First();Check(Convert.ToInt32(latest["outerSourceId"])==0x0422,"raw records retain the actual received outer identifier");
                Check(!Capture(monitor,Bytes(0x1234,9),out valid),"another APID is not accepted from payload/header coincidences");
                Check(!Capture(monitor,Bytes(0x1234),out valid,"127.0.0.2"),"configured source IP remains enforced for raw monitoring");
                var bad=Bytes(0x0202);bad[bad.Length-1]^=1;Check(Capture(monitor,bad,out valid)&&!valid,"checksum failures are retained as abnormal raw packets");
                var length=Bytes(0x0202);length[11]++;SppChecksum.Write(length,6,length.Length-6);Check(Capture(monitor,length,out valid)&&!valid,"actual wire length inconsistency remains diagnosed");
                var shortPacket=new byte[]{0x12,0x34,0,0,0,1,0x08,0x08};Check(Capture(monitor,shortPacket,out valid)&&!valid,"truncated APID-matched raw packets remain retained with a diagnostic");
                list=Object(monitor.RawSppList("raw",1));Check(Convert.ToInt32(list["total"])==5,"raw routing does not discard checksum, length or truncation evidence");
                Check(monitor.Catalog.Count==0&&monitor.Values.Count==0,"APID-only raw monitoring creates no telemetry mappings or values");
            }
            var illegal=JsonFile.Decode<DecoderConfiguration>(JsonFile.Encode(raw));illegal.Packets[0].Enabled=true;bool denied=false;try{illegal.Validate();}catch(ArgumentException){denied=true;}Check(denied,"telemetry decoding still requires a concrete outer source identifier");
            var overlap=JsonFile.Decode<DecoderConfiguration>(JsonFile.Encode(raw));var extra=JsonFile.Decode<PacketDefinition>(JsonFile.Encode(overlap.Packets[0]));extra.Id="other";extra.OuterSourceId=0x0802;overlap.Packets.Add(extra);denied=false;try{overlap.Validate();}catch(ArgumentException ex){denied=ex.Message.Contains("监测原包");}Check(denied,"ambiguous wildcard/exact raw routes are rejected before receiving");
            raw.Packets[0].SppOffset=0;JsonFile.Save(rawPath,raw);using(var monitor=new TelemetryMonitor.Monitor(rawRoot)){bool valid;Check(Capture(monitor,Bytes(0x1234).Skip(6).ToArray(),out valid)&&valid,"an explicit zero offset supports a bare SPP raw packet");}
            using(var monitor=new TelemetryMonitor.Monitor(ruleRoot))
            {
                var snapshot=monitor.ContinuousSnapshot();var rule=snapshot.Rules.First();rule.SourceIP="";rule.Enabled=true;rule.Script="RET=Normal;";
                Check(monitor.CheckContinuousRule(rule,true)==""&&rule.SourceIP=="","automatic rule binding does not require or persist a manual IP");
                Check((string)Object(monitor.ContinuousSource(rule.ParameterId))["sourceIP"]=="127.0.0.1","a bound SPP resolves automatic source even with multiple listener IPs");
                monitor.SaveContinuousRules(new List<ContinuousRule>{rule},snapshot.Revision);Check(monitor.ContinuousSnapshot().Rules[0].SourceIP=="","saving rules preserves the automatic-source policy");
                var manual=JsonFile.Decode<ContinuousRule>(JsonFile.Encode(rule));manual.SourceIP="127.0.0.2";Check(monitor.CheckContinuousRule(manual,true)!="","manual IP conflicting with SPP source is still rejected");
                manual.SourceIP="127.0.0.1";manual.Id=Guid.NewGuid().ToString("N");denied=false;try{monitor.SaveContinuousRules(new List<ContinuousRule>{rule,manual},monitor.ContinuousSnapshot().Revision);}catch(ArgumentException){denied=true;}Check(denied,"automatic and equivalent explicit-IP rules cannot silently duplicate one parameter stream");
            }
            var configuration=JsonFile.Load<DecoderConfiguration>(rulePath);configuration.Packets[0].SourceIP="127.0.0.2";configuration.Revision=Guid.NewGuid().ToString("N");JsonFile.Save(rulePath,configuration);
            using(var monitor=new TelemetryMonitor.Monitor(ruleRoot))
            {
                var rule=monitor.ContinuousSnapshot().Rules.First();Check(rule.SourceIP==""&&(string)Object(monitor.ContinuousSource(rule.ParameterId))["sourceIP"]=="127.0.0.2"&&monitor.CheckContinuousRule(rule,false)=="","after reload an automatic rule follows a changed SPP IP without rewriting the rule");
                typeof(TelemetryMonitor.Monitor).GetProperty("Listening").GetSetMethod(true).Invoke(monitor,new object[]{true});monitor.StartJudgment();typeof(TelemetryMonitor.Monitor).GetMethod("Process",Flags).Invoke(monitor,new object[]{Bytes(2050),"127.0.0.2",false,6000,0L});var state=Object(monitor.ContinuousList());var rows=((System.Collections.IEnumerable)state["rows"]).Cast<Dictionary<string,object>>();Check(monitor.Accepted==1&&rows.First(r=>((Dictionary<string,object>)r["rule"])["ParameterId"].Equals(rule.ParameterId))["result"].Equals("Normal"),"automatic source evaluates the real decoded parameter from its resolved IP without opening a listener");monitor.StopJudgment();
            }
            configuration.Packets[0].SourceIP="";configuration.Revision=Guid.NewGuid().ToString("N");JsonFile.Save(rulePath,configuration);using(var monitor=new TelemetryMonitor.Monitor(ruleRoot)){var rule=monitor.ContinuousSnapshot().Rules.First();Check(monitor.CheckContinuousRule(rule,false)!=""&&!Convert.ToBoolean(Object(monitor.ContinuousSource(rule.ParameterId))["canAuto"]),"ambiguous source-free SPPs do not mix histories from multiple listener IPs");}
            Console.WriteLine("All "+checks+" raw/APID and automatic-rule-source checks passed. No sockets were opened.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
