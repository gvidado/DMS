using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Text;
using TelemetryMonitor;
using Monitor=TelemetryMonitor.Monitor;
public static class BCCommandRuntimeTest
{
    private static int checks;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static ObservedCommandConfiguration Disk(string root){return JsonFile.Load<ObservedCommandConfiguration>(Path.Combine(root,"config","observed-commands.json"));}
    private static byte[] Counter(byte[] packet,int n)
    {var b=(byte[])packet.Clone();b[8]=(byte)n;b[9]=(byte)(n>>8);uint sum=0;for(int i=0;i<b.Length-4;i++)sum+=b[i];for(int i=0;i<4;i++)b[b.Length-4+i]=(byte)(sum>>(8*i));return b;}
    private static int State(Monitor monitor)
    {var data=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.ContinuousList()));foreach(Dictionary<string,object> row in (IEnumerable)data["rows"]){var r=(Dictionary<string,object>)row["rule"];if((string)r["Code"]=="A1")return Convert.ToInt32(row["currentState"]);}return -1;}
    public static int Main(string[] args)
    {try{return Run(args);}catch(Exception ex){Console.WriteLine(ex.GetType().FullName+": "+ex.Message);return 1;}}
    private static int Run(string[] args)
    {
        string root=Path.Combine(args[0],"test-output","bc-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"config"));
        try
        {
            byte[] workbook=File.ReadAllBytes(args[1]);var store=new ObservedCommands(root);var plan=store.PreviewBCWorkbook(workbook);
            Check(plan.Errors.Count==0,String.Join(";",plan.Errors.ToArray())+" workbook has no blocking errors");
            Check(plan.BCommands==1999 && plan.BroadcastCommands==21 && plan.Skipped==4 && plan.Expanded==1999,"1999 in-range B commands and 21 broadcasts; BC4000 and 3 reserved rows skipped");
            Check(plan.AddedCommands==2020 && plan.AddedCategories==2020,"preview contains concrete, unique original BC codes");
            Check(plan.Warnings.Any(s=>s.Contains("BC4000")) && plan.Warnings.Any(s=>s.Contains("BC0018")),"skipped rows are identified by code");
            store.ApplyCsv(plan);var config=Disk(root);var b=config.Commands.Single(e=>e.Categories.Any(c=>c.Code=="BC3383"));var bc=config.Commands.Single(e=>e.Categories.Any(c=>c.Code=="BC0005"));
            Check(b.PayloadHex=="EB 90 EB 90 30 00 00 00 AA 00 01 00 00 00 01 00 00 00 00 00 53 15 20 00 00 00 11 01 1A 08 C0 00 00 11 30 02 0A 00 00 00 00 00 01 08 00 01 0D 37 0D 37 07 36 6A 06 00 00","BC3383 reconstructs the exact user-supplied 56-byte example");
            Check(bc.PayloadHex=="EB 90 EB 90 30 00 00 00 07 00 00 00 01 00 00 00 01 00 00 00 53 15 20 00 00 00 1F 01 1A 70 C0 00 00 11 30 02 0A 00 00 00 00 00 01 01 00 01 00 05 00 05 DB 7E D4 06 00 00","BC0005 reconstructs the exact user-supplied broadcast example");
            Check(b.DestinationIPs.SequenceEqual(new[]{"192.168.1.113"}) && bc.DestinationIPs.SequenceEqual(new[]{"192.168.1.112","192.168.1.113"}),"B and broadcast destination lists match the request");
            Check(config.Commands.All(e=>CommandEnvelope.Valid(PacketCodec.Hex(e.PayloadHex))),"all 2020 full UDP payloads have correct length and outer checksum");
            Check(config.NextCategoryNumbers["BC"]==25,"importing high B codes does not consume low BC numbering");
            var repeated=store.PreviewBCWorkbook(workbook);Check(repeated.Errors.Count==0 && repeated.AddedCommands==0 && repeated.AddedDestinations==0,"repeat Excel import is idempotent");store.ApplyCsv(repeated);
            var csv=new StringWriter();store.ExportCsv(csv);var roundtrip=store.PreviewCsv(Encoding.UTF8.GetBytes(csv.ToString()));Check(roundtrip.Errors.Count==0 && roundtrip.AddedCommands==0,"all imported definitions round-trip through the existing six-column CSV");
            byte[] changed=Counter(PacketCodec.Hex(b.PayloadHex),65535);Check(CommandEnvelope.Valid(changed) && CommandEnvelope.IdentityHex(changed)==CommandEnvelope.IdentityHex(PacketCodec.Hex(b.PayloadHex)),"changed frame count retains command identity after checksum recalculation");
            string before=File.ReadAllText(Path.Combine(root,"config","observed-commands.json"));Check(store.Observe("192.168.1.16","192.168.1.113",changed,DateTime.UtcNow)=="" && File.ReadAllText(Path.Combine(root,"config","observed-commands.json"))==before,"known command with different counter creates no duplicate and does not rewrite config");
            var bad=(byte[])changed.Clone();bad[9]^=1;Check(!CommandEnvelope.Valid(bad) && CommandEnvelope.IdentityHex(bad)!=CommandEnvelope.IdentityHex(changed),"bad checksum cannot use the counter-insensitive identity");
            store.Observe("192.168.1.16","192.168.1.113",new byte[]{99,98,97},DateTime.UtcNow);Check(Disk(root).Commands.Last().Categories.Single().Code=="BC0025","unknown command uses the next free low BC code");
            var restarted=new ObservedCommands(root);Check(restarted.Observe("192.168.1.16","192.168.1.113",Counter(changed,1),DateTime.UtcNow)=="" && Disk(root).Commands.Count==2021,"matching and numbering remain stable after restart");
            File.Copy(Path.Combine(args[0],"config","parameters.json"),Path.Combine(root,"config","parameters.json"));var settings=JsonFile.Load<Settings>(Path.Combine(args[0],"config","server.json"));settings.BindAddress="127.0.0.1";settings.SourceIPs=new[]{"127.0.0.1"};settings.MappingConfirmed=true;settings.Profile="全程";settings.MulticastGroup="";settings.MulticastInterface="";
            using(var reservation=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))settings.UdpPort=((IPEndPoint)reservation.Client.LocalEndPoint).Port;
            JsonFile.Save(Path.Combine(root,"config","server.json"),settings);JsonFile.Save(Path.Combine(root,"config","send-monitor.json"),new SendMonitorSettings{SourceIP="127.0.0.1",DestinationIPs=new[]{"192.168.1.112","192.168.1.113"}});
            config=Disk(root);config.SourceIP="127.0.0.1";JsonFile.Save(Path.Combine(root,"config","observed-commands.json"),config);
            using(var monitor=new Monitor(root))
            {
                monitor.SaveCommandBinding(b.Number,"KLEGACY","");monitor.SaveContinuousRules(new List<ContinuousRule>{new ContinuousRule{Id="bc-runtime",Code="A1",Name="B command event",ParameterId=monitor.Catalog.First(p=>p.Code=="A1").Id,SourceIP="127.0.0.1",Enabled=true,Script="InitRegion this.state=0; EndInitRegion StateRegion if(TC.BC3383 || TC.KLEGACY) then this.state=this.state+1; end EndStateRegion ResultRegion RET=Normal; EndResultRegion"}},monitor.ContinuousSnapshot().Revision);
                monitor.Start();monitor.StartJudgment();Type rt=typeof(Monitor).Assembly.GetType("TelemetryMonitor.SendRecord");var enqueue=typeof(Monitor).GetMethod("QueueCapturedCommand",BindingFlags.Instance|BindingFlags.NonPublic);
                for(int i=0;i<2;i++){object record=Activator.CreateInstance(rt,true);rt.GetField("SourceIP").SetValue(record,"127.0.0.1");rt.GetField("DestinationIP").SetValue(record,"192.168.1.113");rt.GetField("Payload").SetValue(record,Counter(changed,100+i));enqueue.Invoke(monitor,new[]{record});DateTime until=DateTime.UtcNow.AddSeconds(5);while(State(monitor)!=i+1){if(DateTime.UtcNow>until)throw new Exception("TC event timeout");Thread.Sleep(10);}}
                Check(State(monitor)==2,"TC.BC3383 and legacy manual alias fire once per physical capture despite changing outer counter; no device packets sent");
            }
            Console.WriteLine("All "+checks+" BC command checks passed.");return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
