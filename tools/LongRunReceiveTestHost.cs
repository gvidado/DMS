using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TelemetryMonitor;

// Replays accepted datagrams through the real receive/decode path without opening a socket.
internal static class LongRunReceiveTestHost
{
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static readonly List<string> checks=new List<string>();
    private static void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);Console.WriteLine("PASS "+name);}
    private static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    private static Dictionary<string,object> Snapshot(TelemetryMonitor.Monitor monitor,string id)
    {return JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.Snapshot(new List<string>{id})));}
    private static string Status(Dictionary<string,object> snapshot)
    {return Convert.ToString(((Dictionary<string,object>)((IList)snapshot["rows"])[0])["status"]);}
    private static void FreshListen(TelemetryMonitor.Monitor monitor)
    {
        monitor.GetType().GetMethod("Reset",Private).Invoke(monitor,new object[0]);
        monitor.GetType().GetField("<Listening>k__BackingField",Private).SetValue(monitor,true);
        monitor.GetType().GetField("packetStatisticsMode",Private).SetValue(monitor,"live");
    }
    private static byte[] Packet(int sequence)
    {
        var bytes=new byte[28];
        foreach(var pair in new[]{new[]{0,2050},new[]{4,21},new[]{6,0x0808},new[]{8,0xc000|(sequence&16383)},new[]{10,15},new[]{18,4100},new[]{22,100},new[]{24,100}})
        {bytes[pair[0]]=(byte)(pair[1]>>8);bytes[pair[0]+1]=(byte)pair[1];}
        bytes[12]=48;bytes[13]=3;bytes[14]=25;SppChecksum.Write(bytes,6,bytes.Length-6);return bytes;
    }
    private static long LogLines(string root)
    {string folder=Path.Combine(root,"logs");return Directory.Exists(folder)?Directory.GetFiles(folder,"*.jsonl").Sum(p=>File.ReadLines(p).LongCount()):0;}
    private static bool ReferenceChecksum(byte[] bytes,int offset,int length)
    {
        int a=0,b=0;
        for(int i=offset;i<offset+length;i++){a=(a+bytes[i])%255;b=(b+a)%255;}
        return a==0&&b==0;
    }
    private static void CheckFletcherBlocks()
    {
        var random=new Random(42);
        foreach(int size in new[]{2,3,2047,2048,2049,2050,4095,4096,4097,4098,65501})
        {
            var bytes=new byte[size+6];random.NextBytes(bytes);
            for(int i=0;i<size;i++)if(size%2==0)bytes[6+i]=255;
            SppChecksum.Write(bytes,6,size);
            if(!ReferenceChecksum(bytes,6,size)||!SppChecksum.Verify(bytes,6,size))throw new Exception("Fletcher mismatch at "+size);
            bytes[6+size/2]^=1;
            if(ReferenceChecksum(bytes,6,size)||SppChecksum.Verify(bytes,6,size))throw new Exception("Fletcher failed to reject corruption at "+size);
        }
        Check(true,"Fletcher-16 matches byte-by-byte reference at block boundaries and maximum UDP length");
    }
    private static void CheckExactEngineeringFormatting()
    {
        var bytes=new byte[16];
        var cfg=new Settings{ByteOrder="big",PayloadOffset=6};
        var packet=new Packet{Offset=0,Length=bytes.Length};
        var field=new Parameter{Id="wide",Length=8,Type="uint",Scale=1,Supported=true};
        for(int i=6;i<14;i++)bytes[i]=255;
        Reading value=Protocol.Decode(bytes,packet,field,cfg,DateTime.UtcNow);
        Check(value.EngineeringValue is ulong&&(ulong)value.EngineeringValue==UInt64.MaxValue&&value.EngineeringText=="18446744073709551615"&&value.HexSource=="0xFFFFFFFFFFFFFFFF","wide unsigned readings retain exact integer text and padded hexadecimal source");
        field.Type="int";Array.Clear(bytes,6,8);bytes[6]=128;
        value=Protocol.Decode(bytes,packet,field,cfg,DateTime.UtcNow);
        Check(value.EngineeringValue is long&&(long)value.EngineeringValue==Int64.MinValue&&value.Raw=="-9223372036854775808"&&value.HexSource=="0x8000000000000000","wide signed readings retain exact signed type and raw bits");
        field.Type="bcd";field.Length=2;bytes[6]=0x12;bytes[7]=0x34;
        field.EnumRanges.Add(new EnumRange{Min=1200,Max=1300,Text="first"});
        field.EnumRanges.Add(new EnumRange{Min=1234,Max=1234,Text="second"});
        value=Protocol.Decode(bytes,packet,field,cfg,DateTime.UtcNow);
        Check(value.EngineeringValue is ulong&&(ulong)value.EngineeringValue==1234&&value.HexSource=="0x1234"&&value.Value=="first","BCD numeric type and first matching enum range remain unchanged");
    }
    private static void WithStamp(TelemetryMonitor.Monitor monitor,byte[] bytes,double ageSeconds,DateTime utc)
    {
        var receive=monitor.GetType().GetMethod("ProcessReceived",Private);
        var stampType=receive.GetParameters()[5].ParameterType;
        var stamp=Activator.CreateInstance(stampType,true);
        foreach(var pair in new[]{new KeyValuePair<string,object>("ReceivedUtc",DateTime.UtcNow),new KeyValuePair<string,object>("ObservedUtc",utc),new KeyValuePair<string,object>("Monotonic",Stopwatch.GetTimestamp()),new KeyValuePair<string,object>("DataSeconds",TelemetryMonitor.Monitor.RuleSeconds()-ageSeconds),new KeyValuePair<string,object>("Basis","SOCKET_RECEIVE")})
            stampType.GetField(pair.Key,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(stamp,pair.Value);
        receive.Invoke(monitor,new object[]{bytes,"127.0.0.1",false,6000,0L,stamp});
    }
    private static int Main(string[] args)
    {
        if(args.Length!=1){Console.Error.WriteLine("Usage: LongRunReceiveTestHost FIXTURE_ROOT");return 2;}
        string root=Path.GetFullPath(args[0]);
        try
        {
            using(var monitor=new TelemetryMonitor.Monitor(root))
            {
                ((System.Threading.Timer)Field(monitor,"health")).Dispose();
                FreshListen(monitor);var process=monitor.GetType().GetMethod("Process",Private);var packet=Packet(0);var call=new object[]{packet,"127.0.0.1",false,6000,0L};const int frames=500000;var watch=Stopwatch.StartNew();
                for(int i=0;i<frames;i++)
                {int sequence=i&16383;packet[8]=(byte)(0xc0|(sequence>>8));packet[9]=(byte)sequence;SppChecksum.Write(packet,6,packet.Length-6);process.Invoke(monitor,call);}
                watch.Stop();string id=monitor.Catalog[0].Id;
                Check(monitor.Received==frames&&monitor.Accepted==frames&&monitor.Invalid==0&&monitor.OutOfOrder==0&&monitor.Duplicates==0&&monitor.Lost==0,"500000 frames and thirty natural counter wraps remain valid without restarting reception");
                Check(Status(Snapshot(monitor,id))=="有效","a fresh parameter remains valid after the long receive run");
                var histories=(IDictionary)Field(monitor,"engineeringHistory");foreach(DictionaryEntry stream in histories)foreach(DictionaryEntry row in (IDictionary)stream.Value)
                    Check(((ParameterHistory)row.Value).Samples.Count==10,"engineering history stays bounded to its most recent ten samples");
                var trackers=(IDictionary)Field(monitor,"sequences");foreach(DictionaryEntry stream in trackers)
                {object tracker=stream.Value;Check((int)Field(tracker,"recent").GetType().GetProperty("Count").GetValue(Field(tracker,"recent"),null)<=5&&(int)Field(tracker,"order").GetType().GetProperty("Count").GetValue(Field(tracker,"order"),null)<=5,"duplicate tracking remains bounded after repeated wraps");}
                FreshListen(monitor);monitor.Config.DataTimeoutMs=500;WithStamp(monitor,Packet(1),10,DateTime.UtcNow.AddDays(-40));
                var delayed=Snapshot(monitor,id);Check(Status(delayed)=="过期"&&Convert.ToDouble(delayed["processingDelayMs"])>=9990,"delayed processing is reported without pretending an old receive timestamp is fresh");
                WithStamp(monitor,Packet(2),0,DateTime.UtcNow.AddDays(-40));var fresh=Snapshot(monitor,id);
                Check(Status(fresh)=="有效"&&monitor.Accepted==2,"a genuinely new sample restores valid freshness without restarting the listener");
                Check(Convert.ToDouble(fresh["maxProcessingDelayMs"])>=9990&&Convert.ToDouble(fresh["processingDelayMs"])<500,"current and maximum receive lock delays distinguish recovery from accumulated lag");
                FreshListen(monitor);Check(Convert.ToDouble(Snapshot(monitor,id)["maxProcessingDelayMs"])==0,"new listening sessions reset pipeline delay statistics");
                WithStamp(monitor,Packet(10000),0,DateTime.UtcNow);long before=LogLines(root);
                for(int i=0;i<300;i++)WithStamp(monitor,Packet((i%2==0?2000:10192)+(i/2)),0,DateTime.UtcNow);
                Check(monitor.OutOfOrder==300&&monitor.Accepted==301,"every counter resynchronization remains diagnostic and continues decoding");
                Check(LogLines(root)-before==1,"repeated resynchronizations write one warning without dropping data or creating a disk log storm");
                FreshListen(monitor);WithStamp(monitor,Packet(0),0,DateTime.UtcNow);before=LogLines(root);
                for(int i=2;i<=200;i+=2)WithStamp(monitor,Packet(i),0,DateTime.UtcNow);
                Check(monitor.Lost==100&&monitor.Accepted==101,"each discontinuity still contributes its missing-frame count");
                Check(LogLines(root)-before==1,"a repeated gap condition writes one warning without dropping accepted data");
                var damaged=Packet(201);damaged[23]^=1;long invalid=monitor.Invalid,accepted=monitor.Accepted;WithStamp(monitor,damaged,0,DateTime.UtcNow);
                Check(monitor.Invalid==invalid+1&&monitor.Accepted==accepted,"Fletcher failures are still rejected after prolonged reception and gap diagnostics");
                var nan=Packet(202);nan[22]=0x7f;nan[23]=0xc0;nan[24]=nan[25]=0;var field=new Parameter{Id="nan",Code="NaN",Name="nonfinite",Group="test",Offset=0,Length=4,Type="float32",Scale=1,Supported=true};
                var reading=Protocol.Decode(nan,new Packet{Offset=6,Length=22},field,new Settings{ByteOrder="big",PayloadOffset=16},DateTime.UtcNow);
                Check(reading.Status=="解码无效"&&reading.EngineeringValue==null,"nonfinite engineering values retain strict invalid-data quality");
                CheckFletcherBlocks();CheckExactEngineeringFormatting();
                FreshListen(monitor);
                byte[] unrelated=Packet(1);unrelated[7]=9;WithStamp(monitor,unrelated,0,DateTime.UtcNow);
                var truncated=Packet(2).Take(15).ToArray();WithStamp(monitor,truncated,0,DateTime.UtcNow);
                var diagnostics=JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(monitor.LiveAbnormalList()));
                var rows=((IEnumerable)diagnostics["records"]).Cast<Dictionary<string,object>>().ToArray();
                Check(monitor.Invalid==2&&Convert.ToBoolean(rows[0]["alarm"])&&Convert.ToInt32(rows[0]["apid"])==8&&!Convert.ToBoolean(rows[1]["alarm"]),
                    "indexed rejection retains malformed-SPP alarms and treats unrelated APIDs as information");
                Console.WriteLine(JsonFile.Encode(new{checks=checks.Count,details=checks,frames,seconds=watch.Elapsed.TotalSeconds}));
            }
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
