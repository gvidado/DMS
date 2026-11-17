using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TelemetryMonitor;
public static class SendCommandDisplayTest
{
    private static int checks;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    private static Dictionary<string,object> Json(object value){return JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(value));}
    private static Dictionary<string,object> Row(SendMonitor monitor,long id){return ((IEnumerable)Json(monitor.List())["records"]).Cast<Dictionary<string,object>>().Single(r=>Convert.ToInt64(r["id"])==id);}
    private static byte[] Counter(byte[] original,int n)
    {var b=(byte[])original.Clone();b[8]=(byte)n;b[9]=(byte)(n>>8);uint sum=0;for(int i=0;i<b.Length-4;i++)sum+=b[i];for(int i=0;i<4;i++)b[b.Length-4+i]=(byte)(sum>>(i*8));return b;}
    public static int Main(string[] args)
    {try{return args.Length>1 && args[1]=="--serve"?Serve(args):Run(args);}catch(Exception ex){Console.WriteLine(ex.GetType().FullName+": "+ex.Message);return 1;}}
    private static int Serve(string[] args)
    {
        using(var server=new Server(args[2],true,Int32.Parse(args[3])))
        {
            var monitor=(TelemetryMonitor.Monitor)typeof(Server).GetField("monitor",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(server);
            var send=monitor.Outgoing;Type rt=typeof(SendMonitor).Assembly.GetType("TelemetryMonitor.SendRecord");var queue=typeof(SendMonitor).GetField("recent",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(send);var enqueue=queue.GetType().GetMethod("Enqueue");
            string[] destinations={"192.168.1.113","192.168.1.111","192.168.1.137","192.168.1.113"};
            for(int i=0;i<4;i++){object r=Activator.CreateInstance(rt,true);rt.GetField("Id").SetValue(r,(long)i+1);rt.GetField("Time").SetValue(r,DateTime.UtcNow);rt.GetField("SourceIP").SetValue(r,"192.168.1.16");rt.GetField("DestinationIP").SetValue(r,destinations[i]);rt.GetField("Payload").SetValue(r,i==3?new byte[]{0xFE}:new byte[]{0,0xAA,0xFF});rt.GetField("NetworkFrame").SetValue(r,new byte[]{0x45,0,1});enqueue.Invoke(queue,new[]{r});}
            typeof(SendMonitor).GetField("total",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(send,4L);
            server.Start();System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        }
        return 0;
    }
    private static int Run(string[] args)
    {
        string root=Path.Combine(args[0],"test-output","send-display-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"config"));
        try
        {
            byte[] payload=CommandEnvelope.BuildBC(PacketCodec.Hex("1A08C000001130020A0000000000010800010D370D370736"),false);
            var ips=new List<string>{"192.168.1.113","192.168.1.112","192.168.1.111","192.168.1.137","192.168.1.114"};
            var entry=new ObservedCommand{Number=1,Name="测试指令",DestinationIP=ips[0],DestinationIPs=ips,PayloadHex=BitConverter.ToString(payload).Replace('-',' '),FirstSeenUtc=DateTime.UtcNow.ToString("o"),Categories=new List<CommandCategory>{new CommandCategory{Kind="BC",Code="BC3383"},new CommandCategory{Kind="PSS",Code="PSS0007"},new CommandCategory{Kind="KC",Code="KC0009"}}};
            JsonFile.Save(Path.Combine(root,"config","observed-commands.json"),new ObservedCommandConfiguration{Commands=new List<ObservedCommand>{entry},NextNumber=2});
            using(var monitor=new SendMonitor(root,(a,b)=>{}))
            {
                Type rt=typeof(SendMonitor).Assembly.GetType("TelemetryMonitor.SendRecord");var queue=typeof(SendMonitor).GetField("recent",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor);var enqueue=queue.GetType().GetMethod("Enqueue");long next=0;
                Action<string,string,byte[]> capture=(source,destination,data)=>{object r=Activator.CreateInstance(rt,true);rt.GetField("Id").SetValue(r,++next);rt.GetField("Time").SetValue(r,DateTime.UtcNow);rt.GetField("SourceIP").SetValue(r,source);rt.GetField("DestinationIP").SetValue(r,destination);rt.GetField("Payload").SetValue(r,data);rt.GetField("NetworkFrame").SetValue(r,new byte[]{0x45,0,1});enqueue.Invoke(queue,new[]{r});};
                foreach(string ip in ips)capture("192.168.1.16",ip,payload);
                Check((string)Row(monitor,1)["commandCode"]=="BC3383" && (string)Row(monitor,2)["commandCode"]=="BC3383" && (string)Row(monitor,3)["commandCode"]=="PSS0007" && (string)Row(monitor,4)["commandCode"]=="KC0009","the same payload selects BC/PSS/KC according to the actual destination");
                Check((bool)Row(monitor,5)["commandMatched"] && (string)Row(monitor,5)["commandCode"]=="" && (string)Row(monitor,5)["commandName"]=="测试指令","unclassified saved commands still show their library name");
                capture("192.168.1.16","192.168.1.113",Counter(payload,601));Check((string)Row(monitor,6)["commandCode"]=="BC3383" && (string)Row(monitor,6)["commandName"]=="测试指令","changing the valid EB90 outer counter still identifies the original command");
                capture("192.168.1.17","192.168.1.113",payload);capture("192.168.1.16","192.168.1.110",payload);capture("192.168.1.16","192.168.1.113",new byte[]{0xFF});Check(new long[]{7,8,9}.All(id=>!(bool)Row(monitor,id)["commandMatched"] && (string)Row(monitor,id)["commandCode"]==""),"wrong source, unknown destination and unknown bytes cannot receive a library label");
                var detail=Json(monitor.Detail(6));Check((string)((Dictionary<string,object>)detail["record"])["commandCode"]=="BC3383" && (string)detail["udpHex"]==BitConverter.ToString(Counter(payload,601)).Replace('-',' '),"detail shows command identity while preserving the actual captured payload");
                string path=Path.Combine(root,"config","observed-commands.json"),before=File.ReadAllText(path);var csv=new StringWriter();monitor.WriteCsv(csv);Check(csv.ToString().Contains("完整抓包帧HEX,指令编号,指令名称") && csv.ToString().Contains(",\"BC3383\",\"测试指令\"") && File.ReadAllText(path)==before,"CSV adds command code/name without modifying the library during display or export");
                long revision=Convert.ToInt64(Json(monitor.List())["commandRevision"]);string body="指令编号,指令名称,指令类别,来源IP,目的IP,完整UDP载荷HEX\r\nBC3383,\"新名称,复核\",BC,192.168.1.16,192.168.1.113,"+entry.PayloadHex+"\r\n";var plan=monitor.Commands.PreviewCsv(Encoding.UTF8.GetBytes(body));monitor.Commands.ApplyCsv(plan);
                Check((string)Row(monitor,6)["commandName"]=="新名称,复核" && Convert.ToInt64(Json(monitor.List())["commandRevision"])>revision,"catalog renames immediately update existing recent rows and their refresh revision");
                csv=new StringWriter();monitor.WriteCsv(csv);Check(csv.ToString().Contains(",\"BC3383\",\"新名称,复核\""),"CSV preserves and escapes renamed command text");
                var clear=(CommandImportPlan)typeof(ObservedCommands).GetMethod("PreviewClear",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(monitor.Commands,new object[]{Convert.ToInt64(Json(monitor.List())["commandRevision"])});monitor.Commands.ApplyCsv(clear);
                Check(!(bool)Row(monitor,6)["commandMatched"] && (string)Json(monitor.Detail(6))["udpHex"]==(string)detail["udpHex"],"clearing definitions removes stale labels while retaining captured raw records");
            }
            Console.WriteLine("All "+checks+" command display checks passed; no device packets sent.");return 0;
        }
        finally{Directory.Delete(root,true);}
    }
}
