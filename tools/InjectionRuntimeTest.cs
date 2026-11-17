using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using TelemetryMonitor;
using Monitor=TelemetryMonitor.Monitor;
public static class InjectionRuntimeTest
{
    private static int checks;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static Dictionary<string,object> Json(object value){return JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(value));}
    private static byte[] Counter(byte[] packet,int n)
    {var b=(byte[])packet.Clone();b[8]=(byte)n;b[9]=(byte)(n>>8);uint sum=0;for(int i=0;i<b.Length-4;i++)sum+=b[i];for(int i=0;i<4;i++)b[b.Length-4+i]=(byte)(sum>>(8*i));return b;}
    private static int State(Monitor monitor)
    {foreach(Dictionary<string,object> row in (IEnumerable)Json(monitor.ContinuousList())["rows"]){var r=(Dictionary<string,object>)row["rule"];if((string)r["Code"]=="A1")return Convert.ToInt32(row["currentState"]);}return -1;}
    public static int Main(string[] args)
    {try{return Run(args);}catch(Exception ex){Console.WriteLine(ex.GetType().FullName+": "+ex.Message);return 1;}}
    private static int Run(string[] args)
    {
        string root=Path.Combine(args[0],"test-output","injection-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"config"));
        try
        {
            string kind=args.Length>2?args[2]:"YKE";
            byte[] bin=File.ReadAllBytes(args[1]),packet=CommandEnvelope.BuildInjection(bin);var store=new ObservedCommands(root);
            var plan=store.PreviewInjection(bin,"test.bin","注入文件测试","","192.168.1.113",kind);Check(plan.Errors.Count==0,kind+" BIN preview has no errors");store.ApplyCsv(plan);
            byte[] changed=Counter(packet,12345);string before=File.ReadAllText(Path.Combine(root,"config","observed-commands.json"));store.Observe("192.168.1.16","192.168.1.113",changed,DateTime.UtcNow);
            Check(before==File.ReadAllText(Path.Combine(root,"config","observed-commands.json")),"counter variation reuses imported "+kind+" and does not rewrite library");
            var describe=typeof(ObservedCommands).GetMethod("Describe",BindingFlags.Instance|BindingFlags.NonPublic);object display=describe.Invoke(store,new object[]{"192.168.1.16","192.168.1.113",changed});
            Check((string)display.GetType().GetField("Code").GetValue(display)==kind+"0001" && (string)display.GetType().GetField("Name").GetValue(display)=="注入文件测试","captured injection displays "+kind+"0001 and its configured name");
            store.Observe("192.168.1.16","192.168.1.112",changed,DateTime.UtcNow);var config=JsonFile.Load<ObservedCommandConfiguration>(Path.Combine(root,"config","observed-commands.json"));
            Check(config.Commands.Count==1 && config.Commands[0].Categories.Count==1 && config.Commands[0].Categories[0].Kind==kind && config.Commands[0].Categories[0].DestinationIPs.Count==2,"observed new destination extends "+kind+" without allocating a BC code");
            File.Copy(Path.Combine(args[0],"config","parameters.json"),Path.Combine(root,"config","parameters.json"));var settings=JsonFile.Load<Settings>(Path.Combine(args[0],"config","server.json"));settings.BindAddress="127.0.0.1";settings.SourceIPs=new[]{"127.0.0.1"};settings.MappingConfirmed=true;settings.Profile="全程";settings.MulticastGroup="";settings.MulticastInterface="";
            using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))settings.UdpPort=((IPEndPoint)reservation.Client.LocalEndPoint).Port;
            JsonFile.Save(Path.Combine(root,"config","server.json"),settings);JsonFile.Save(Path.Combine(root,"config","send-monitor.json"),new SendMonitorSettings{SourceIP="127.0.0.1",DestinationIPs=new[]{"192.168.1.112","192.168.1.113"}});
            config.SourceIP="127.0.0.1";JsonFile.Save(Path.Combine(root,"config","observed-commands.json"),config);
            using(var monitor=new Monitor(root))
            {
                monitor.SaveContinuousRules(new List<ContinuousRule>{new ContinuousRule{Id="injection-runtime",Code="A1",Name=kind+" event",ParameterId=monitor.Catalog.First(p=>p.Code=="A1").Id,SourceIP="127.0.0.1",Enabled=true,Script=("InitRegion this.state=0; EndInitRegion StateRegion if(TC.YKE0001) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion").Replace("TC.YKE0001","TC."+kind+"0001")}},monitor.ContinuousSnapshot().Revision);
                monitor.Start();monitor.StartJudgment();Type rt=typeof(Monitor).Assembly.GetType("TelemetryMonitor.SendRecord");var enqueue=typeof(Monitor).GetMethod("QueueCapturedCommand",BindingFlags.Instance|BindingFlags.NonPublic);
                for(int i=0;i<2;i++){object record=Activator.CreateInstance(rt,true);rt.GetField("SourceIP").SetValue(record,"127.0.0.1");rt.GetField("DestinationIP").SetValue(record,"192.168.1.113");rt.GetField("Payload").SetValue(record,Counter(packet,100+i));enqueue.Invoke(monitor,new[]{record});DateTime until=DateTime.UtcNow.AddSeconds(5);while(State(monitor)!=i+1){if(DateTime.UtcNow>until)throw new Exception("YKE event timeout");Thread.Sleep(10);}}
                Check(State(monitor)==2,"TC."+kind+"0001 fires once per physical capture with changed outer frame counters; no device packets sent");
            }
            Console.WriteLine("All "+checks+" injection runtime checks passed.");return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
