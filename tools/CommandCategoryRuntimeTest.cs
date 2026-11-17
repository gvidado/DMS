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
public static class CommandCategoryRuntimeTest
{
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);}
    private static Dictionary<string,int> States(Monitor monitor)
    {
        var result=new Dictionary<string,int>();var data=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.ContinuousList()));
        foreach(Dictionary<string,object> row in (IEnumerable)data["rows"]){var rule=(Dictionary<string,object>)row["rule"];result[(string)rule["Code"]]=Convert.ToInt32(row["currentState"]);}
        return result;
    }
    private static void Wait(Func<bool> condition){DateTime until=DateTime.UtcNow.AddSeconds(5);while(!condition()){if(DateTime.UtcNow>until)throw new Exception("event timed out");Thread.Sleep(10);}}
    public static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetTempPath(),"category-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"config"));
        try
        {
            File.Copy(Path.Combine(args[0],"config","parameters.json"),Path.Combine(root,"config","parameters.json"));var settings=JsonFile.Load<Settings>(Path.Combine(args[0],"config","server.json"));settings.BindAddress="127.0.0.1";settings.SourceIPs=new[]{"127.0.0.1"};settings.MappingConfirmed=true;settings.Profile="全程";settings.MulticastGroup="";settings.MulticastInterface="";
            using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))settings.UdpPort=((IPEndPoint)reservation.Client.LocalEndPoint).Port;
            JsonFile.Save(Path.Combine(root,"config","server.json"),settings);JsonFile.Save(Path.Combine(root,"config","send-monitor.json"),new SendMonitorSettings{SourceIP="127.0.0.1",DestinationIPs=new[]{"192.168.1.112","192.168.1.113","192.168.1.111","192.168.1.137"}});JsonFile.Save(Path.Combine(root,"config","observed-commands.json"),new ObservedCommandConfiguration{SourceIP="127.0.0.1"});
            using(var monitor=new Monitor(root))
            {
                byte[] first=new byte[]{0,0xAA,0xFF};var store=monitor.Outgoing.Commands;
                foreach(string ip in new[]{"192.168.1.113","192.168.1.112","192.168.1.111","192.168.1.137"})Check(store.Observe("127.0.0.1",ip,first,DateTime.UtcNow)=="","observe "+ip);
                var text=new StringWriter();store.Export(text);var config=JsonFile.Decode<ObservedCommandConfiguration>(text.ToString());Check(config.Commands.Count==1 && config.Commands[0].Categories.Select(c=>c.Code).SequenceEqual(new[]{"BC0001","PSS001","KC0001"}) && config.Commands[0].Categories[0].DestinationIPs.Count==2,"one source has independent category codes while both BC destinations share one");
                string before=File.ReadAllText(Path.Combine(root,"config","observed-commands.json"));store.Observe("127.0.0.1","192.168.1.113",first,DateTime.UtcNow);Check(File.ReadAllText(Path.Combine(root,"config","observed-commands.json"))==before,"repeated source and destination neither rewrites nor increments numbering");
                store.Observe("127.0.0.1","192.168.1.112",new byte[]{0,0xAA,0xFF,0},DateTime.UtcNow);text=new StringWriter();store.Export(text);config=JsonFile.Decode<ObservedCommandConfiguration>(text.ToString());Check(config.Commands[1].Categories[0].Code=="BC0002" && config.NextCategoryNumbers["PSS"]==2,"changed complete payload increments only its own category");
                var rules=new List<ContinuousRule>();string[] codes={"A1","A4","A8"},categories={"BC0001","PSS001","KC0001"};
                monitor.SaveCommandBinding(config.Commands[0].Number,"KLEGACY","");
                for(int i=0;i<3;i++)rules.Add(new ContinuousRule{Id="category-"+i,Code=codes[i],Name=codes[i],ParameterId=monitor.Catalog.First(p=>p.Code==codes[i]).Id,SourceIP="127.0.0.1",Enabled=true,Script="InitRegion this.state=0; EndInitRegion StateRegion if(TC."+categories[i]+") then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion"});
                rules.Add(new ContinuousRule{Id="aliases",Code="A3",Name="alias",ParameterId=monitor.Catalog.First(p=>p.Code=="A3").Id,SourceIP="127.0.0.1",Enabled=true,Script="InitRegion this.state=0; EndInitRegion StateRegion if(TC.BC0001 || TC.KLEGACY) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion"});
                monitor.SaveContinuousRules(rules,monitor.ContinuousSnapshot().Revision);monitor.Start();monitor.StartJudgment();
                Type recordType=typeof(Monitor).Assembly.GetType("TelemetryMonitor.SendRecord");var enqueue=typeof(Monitor).GetMethod("QueueCapturedCommand",BindingFlags.Instance|BindingFlags.NonPublic);
                Action<string> capture=destination=>{object record=Activator.CreateInstance(recordType,true);recordType.GetField("SourceIP").SetValue(record,"127.0.0.1");recordType.GetField("DestinationIP").SetValue(record,destination);recordType.GetField("Payload").SetValue(record,first);enqueue.Invoke(monitor,new[]{record});};
                capture("192.168.1.113");Wait(()=>States(monitor)["A1"]==1);Check(States(monitor)["A4"]==0 && States(monitor)["A8"]==0 && States(monitor)["A3"]==1,"BC destination triggers only its category; automatic plus manual alias produces one dispatch");
                capture("192.168.1.112");Wait(()=>States(monitor)["A1"]==2);capture("192.168.1.111");Wait(()=>States(monitor)["A4"]==1);capture("192.168.1.137");Wait(()=>States(monitor)["A8"]==1);Check(States(monitor)["A1"]==2 && States(monitor)["A4"]==1 && States(monitor)["A8"]==1,"same payload selects the destination category event; no actual device transmissions were made");
                Check(States(monitor)["A3"]==4,"existing manual alias remains usable without double-counting its automatic alias");monitor.StopJudgment();bool conflict=false;try{monitor.SaveCommandBinding(config.Commands[1].Number,"BC0001",JsonFile.Load<TCBindingConfig>(Path.Combine(root,"config","tc-bindings.json")).Revision);}catch(ArgumentException){conflict=true;}Check(conflict,"manual binding cannot take another command's automatic category code");
            }
            return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
