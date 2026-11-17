using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TelemetryMonitor
{
    public sealed class CommandSendRoute
    {public string DestinationIP="";public int SourcePort,DestinationPort;}
    public sealed class CommandSendConfiguration
    {
        public string SourceIP="";
        public List<CommandSendRoute> Routes=new List<CommandSendRoute>{
            new CommandSendRoute{DestinationIP="192.168.1.111",SourcePort=53333,DestinationPort=31000},
            new CommandSendRoute{DestinationIP="192.168.1.112",SourcePort=53335,DestinationPort=31000},
            new CommandSendRoute{DestinationIP="192.168.1.113",SourcePort=53334,DestinationPort=31000},
            new CommandSendRoute{DestinationIP="192.168.1.137",SourcePort=8080,DestinationPort=8080}};
        internal static string IPv4(string value)
        {
            IPAddress address;if(!IPAddress.TryParse(value,out address) || address.AddressFamily!=AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.GetAddressBytes()[0]>=224)throw new ArgumentException("请填写有效的单播IPv4地址："+value);return address.ToString();
        }
        internal void Validate()
        {
            SourceIP=IPv4(SourceIP);if(Routes==null || Routes.Count<1 || Routes.Count>64 || Routes.Any(r=>r==null))throw new ArgumentException("请配置1～64个目的IP的发送端口");
            foreach(var route in Routes){route.DestinationIP=IPv4(route.DestinationIP);if(route.SourcePort<1 || route.SourcePort>65535 || route.DestinationPort<1 || route.DestinationPort>65535)throw new ArgumentException("发送和接收端口须在1～65535之间");}
            if(Routes.Select(r=>r.DestinationIP).Distinct().Count()!=Routes.Count)throw new ArgumentException("端口对应表的目的IP不能重复");
        }
    }
    internal sealed class CommandSendDefinition
    {public long Number,Revision;public string Code,Name,Hex;public byte[] Payload;public string[] Destinations;}
    internal sealed class CommandSendPlan
    {public string Token,Username,ConfigurationRevision,SourceIP;public DateTime Created;public CommandSendDefinition Command;public CommandSendRoute[] Routes;}
    internal sealed class CommandTransmissionRecord
    {internal long ObservedMonotonic;public long id;public string historyEventId=Guid.NewGuid().ToString("N"),time,code,name,sourceIP,destinationIP,actor,error,hex;public int sourcePort,destinationPort,length,sentBytes;public bool success;}

    public sealed partial class ObservedCommands
    {
        internal CommandSendDefinition ReadForSending(long number,string code,string[] selected,bool boundAllDestinations=false)
        {
            lock(gate)
            {
                if(!ready)throw new InvalidOperationException(error);var entry=configuration.Commands.FirstOrDefault(c=>c.MatchesNumber(number));if(entry==null)throw new ArgumentException("指令已不存在，请重新选择");
                code=(code??"").Trim().ToUpperInvariant();string[] allowed;
                if(boundAllDestinations)allowed=entry.DestinationIPs.ToArray();
                else if(code.Length>0){var category=entry.Categories.FirstOrDefault(c=>c.Code==code);if(category==null)throw new ArgumentException("该编号与所选指令不符，请重新选择");allowed=category.DestinationIPs.ToArray();}
                else allowed=entry.DestinationIPs.Where(ip=>!entry.Categories.Any(c=>c.DestinationIPs.Contains(ip))).ToArray();
                if(allowed.Length==0)throw new ArgumentException("所选指令无有效目的IP");
                string[] destinations=selected==null?allowed:selected.Distinct().ToArray();if(destinations.Length==0 || destinations.Any(ip=>!allowed.Contains(ip)))throw new ArgumentException("只能选择该指令已配置的目的IP");
                byte[] payload=entry.Length==0?new byte[0]:PacketCodec.Hex(entry.PayloadHex);if(payload.Length>65507)throw new ArgumentException("指令载荷超过单条IPv4 UDP的65507字节限制");
                if(!String.IsNullOrEmpty(entry.FormatIssue))throw new ArgumentException(entry.Name+"："+entry.FormatIssue);
                if(entry.STC&&entry.InterfaceBytes!=0&&payload.Length>=24&&entry.InterfaceBytes!=CommandEnvelope.InterfaceTypeBytes((ushort)(payload[20]|payload[21]<<8)))throw new ArgumentException("旧STC接口类型长度不符合功能ID规则，请更正后重新入库；原始源码保留");
                payload=CommandEnvelope.PrepareForSending(payload);
                return new CommandSendDefinition{Number=entry.Number,Revision=revision,Code=code,Name=entry.Name,Hex=BitConverter.ToString(payload).Replace('-',' '),Payload=payload,Destinations=destinations};
            }
        }
    }
    // Sends the prepared, previewed bytes exactly once per requested destination. Passive capture remains
    // the sole source of outgoing monitoring counters and TC events; never inject it twice.
    internal sealed partial class CommandTransmitter
    {
        private readonly object gate=new object();
        private readonly string path;
        private readonly ObservedCommands commands;
        private readonly Action<string,string> note;
        private readonly Dictionary<string,CommandSendPlan> plans=new Dictionary<string,CommandSendPlan>();
        private readonly Queue<CommandTransmissionRecord> recent=new Queue<CommandTransmissionRecord>();
        private CommandSendConfiguration configuration;
        private string revision=Guid.NewGuid().ToString("N"),error="";
        private long nextId,total,succeeded,failed;
        internal event Action<CommandTransmissionRecord> Recorded;
        internal CommandTransmitter(string root,ObservedCommands store,Action<string,string> logger)
        {
            path=Path.Combine(root,"config","command-send.json");commands=store;note=logger;
            try{configuration=File.Exists(path)?JsonFile.Load<CommandSendConfiguration>(path):new CommandSendConfiguration{SourceIP=store.BindingSource};configuration.Validate();}
            catch(Exception ex){error="发送配置读取失败，原文件保留："+ex.Message;configuration=new CommandSendConfiguration{SourceIP=store.BindingSource};}
        }
        private static CommandSendConfiguration Clone(CommandSendConfiguration value){return JsonFile.Decode<CommandSendConfiguration>(JsonFile.Encode(value));}
        internal object Configuration()
        {lock(gate)return new{configuration=Clone(configuration),revision,error,interfaces=NetworkReplay.Interfaces()};}
        internal void Save(CommandSendConfiguration value,string expected)
        {
            if(value==null)throw new ArgumentException("缺少指令发送配置");value=Clone(value);value.Validate();
            lock(gate){if(expected!=revision)throw new InvalidOperationException("发送配置已变化，请重新载入后保存");JsonFile.Save(path,value);configuration=value;revision=Guid.NewGuid().ToString("N");error="";plans.Clear();}
        }
        private static string LocalError(string address)
        {
            IPAddress ip=IPAddress.Parse(address);if(IPAddress.IsLoopback(ip))return "";
            bool local=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Any(a=>a.Address.Equals(ip));
            return local?"":"本机未配置发送IP "+address+"，请在指令配置中选择服务器实际IPv4地址";
        }
        internal string SequenceRevision {get{lock(gate)return revision;}}
        internal void CheckSequenceCommand(SequenceStep step)
        {
            lock(gate)
            {
                if(error.Length>0)throw new InvalidOperationException(error);string issue=LocalError(configuration.SourceIP);if(issue.Length>0)throw new ArgumentException(issue);
                var definition=commands.ReadForSending(step.CommandNumber,step.SendCode,null,step.AllDestinations);
                foreach(string ip in definition.Destinations)if(!configuration.Routes.Any(r=>r.DestinationIP==ip))throw new ArgumentException("目的IP "+ip+"尚未配置发送端口");
            }
        }
        internal object Preview(string username,long number,string code,string[] selected,bool boundAllDestinations=false)
        {
            lock(gate)
            {
                if(error.Length>0)throw new InvalidOperationException(error);var command=commands.ReadForSending(number,code,selected,boundAllDestinations);var routes=new List<CommandSendRoute>();
                foreach(string ip in command.Destinations){var route=configuration.Routes.FirstOrDefault(r=>r.DestinationIP==ip);if(route==null)throw new ArgumentException("目的IP "+ip+"尚未配置发送/接收端口，请先到指令配置添加");routes.Add(new CommandSendRoute{DestinationIP=ip,SourcePort=route.SourcePort,DestinationPort=route.DestinationPort});}
                foreach(string key in plans.Where(p=>(DateTime.UtcNow-p.Value.Created).TotalMinutes>2).Select(p=>p.Key).ToArray())plans.Remove(key);
                foreach(string key in plans.Where(p=>p.Value.Username==username).OrderByDescending(p=>p.Value.Created).Skip(7).Select(p=>p.Key).ToArray())plans.Remove(key);
                if(plans.Count>=64)throw new InvalidOperationException("当前发送预览较多，请稍后重试");
                var plan=new CommandSendPlan{Token=Auth.RandomToken(),Username=username,Created=DateTime.UtcNow,ConfigurationRevision=revision,SourceIP=configuration.SourceIP,Command=command,Routes=routes.ToArray()};plans.Add(plan.Token,plan);
                byte[] content=CommandEnvelope.DataContent(command.Payload);
                int offset=CommandEnvelope.DataOffset(command.Payload);string issue=LocalError(plan.SourceIP);return new{token=plan.Token,number=command.Number,code=command.Code,name=command.Name,length=command.Payload.Length,hex=command.Hex,dataContentRecognized=content!=null,dataContentStart=content==null?0:offset+1,interfaceTypeBytes=offset<0?0:offset-22,dataContentLength=content==null?0:content.Length,dataContentHex=content==null?"":BitConverter.ToString(content).Replace('-',' '),sourceIP=plan.SourceIP,routes=plan.Routes,canSend=issue.Length==0,error=issue};
            }
        }
        private static string SocketMessage(Exception ex,string source,int port)
        {
            var socket=ex as SocketException;if(socket==null)return ex.Message;
            if(socket.SocketErrorCode==SocketError.AddressAlreadyInUse)return "本机发送端口已被占用："+source+":"+port+"，请停止占用程序或修改发送配置";
            if(socket.SocketErrorCode==SocketError.AddressNotAvailable)return "本机发送IP不可用："+source;
            return socket.SocketErrorCode+"："+socket.Message;
        }
        internal object Send(string username,string actor,string token)
        {
            lock(gate)
            {
                CommandSendPlan plan;if(!plans.TryGetValue(token??"",out plan) || plan.Username!=username || (DateTime.UtcNow-plan.Created).TotalMinutes>2)throw new ArgumentException("发送预览已使用或过期，请重新预览");
                plans.Remove(token);if(error.Length>0)throw new InvalidOperationException(error);if(plan.ConfigurationRevision!=revision || plan.Command.Revision!=commands.DisplayRevision)throw new InvalidOperationException("指令库或发送配置已变化，请重新预览");
                string localIssue=LocalError(plan.SourceIP);if(localIssue.Length>0)throw new InvalidOperationException(localIssue);
                var sockets=new Dictionary<int,UdpClient>();string preparationError="";int preparingPort=0;double startedMonotonic=0;var records=new List<CommandTransmissionRecord>();
                try
                {
                    // Bind every required source port before sending any destination. A busy
                    // port cannot cause a predictable, half-sent broadcast.
                    try{foreach(var route in plan.Routes)if(!sockets.ContainsKey(route.SourcePort)){preparingPort=route.SourcePort;var client=new UdpClient(AddressFamily.InterNetwork);try{client.ExclusiveAddressUse=true;client.Client.SendTimeout=2000;client.Client.Bind(new IPEndPoint(IPAddress.Parse(plan.SourceIP),route.SourcePort));sockets.Add(route.SourcePort,client);}catch{client.Close();throw;}}}
                    catch(Exception ex){preparationError=SocketMessage(ex,plan.SourceIP,preparingPort);}
                    foreach(var route in plan.Routes)
                    {
                        var record=new CommandTransmissionRecord{time=BeijingTime.Format(DateTime.UtcNow,"yyyy-MM-dd HH:mm:ss.fff"),code=plan.Command.Code,name=plan.Command.Name,sourceIP=plan.SourceIP,sourcePort=route.SourcePort,destinationIP=route.DestinationIP,destinationPort=route.DestinationPort,length=plan.Command.Payload.Length,actor=actor,hex=plan.Command.Hex,error=preparationError};
                        if(preparationError.Length==0)try{if(startedMonotonic==0)startedMonotonic=Monitor.RuleSeconds();record.sentBytes=sockets[route.SourcePort].Send(plan.Command.Payload,plan.Command.Payload.Length,new IPEndPoint(IPAddress.Parse(route.DestinationIP),route.DestinationPort));record.success=record.sentBytes==record.length;if(!record.success)record.error="系统返回的发送字节数与指令长度不符";}catch(Exception ex){record.error=SocketMessage(ex,plan.SourceIP,route.SourcePort);}
                        record.ObservedMonotonic=System.Diagnostics.Stopwatch.GetTimestamp();
                        record.time=BeijingTime.Format(EventStamp.ServerTime(record.ObservedMonotonic),"yyyy-MM-dd HH:mm:ss.fffffff");
                        record.id=++nextId;total++;if(record.success)succeeded++;else failed++;recent.Enqueue(record);while(recent.Count>200)recent.Dequeue();records.Add(record);var captured=Recorded;if(captured!=null)captured(record);
                    }
                }
                finally{foreach(var client in sockets.Values)client.Close();}
                int successes=records.Count(r=>r.success);note(successes==records.Count?"info":"warn","指令发送："+(plan.Command.Code.Length==0?"#"+plan.Command.Number:plan.Command.Code)+" / "+plan.Command.Name+"，成功"+successes+"/"+records.Count+"；操作人："+actor);
                return new{ok=successes==records.Count,succeeded=successes,failed=records.Count-successes,startedMonotonic,eventIds=records.Select(r=>r.historyEventId).ToArray(),rows=records.Select(Row).ToArray()};
            }
        }
        private static object Row(CommandTransmissionRecord r)
        {return new{r.id,r.time,r.code,r.name,r.sourceIP,r.sourcePort,r.destinationIP,r.destinationPort,r.length,r.sentBytes,r.success,r.error,r.actor};}
        internal object History(){lock(gate)return new{total,succeeded,failed,retained=recent.Count,rows=recent.Reverse().Select(Row).ToArray()};}
        internal void Export(TextWriter writer)
        {
            CommandTransmissionRecord[] rows;lock(gate)rows=recent.Reverse().ToArray();writer.WriteLine("记录号,发送时间（北京时间）,指令编号,指令名称,来源IP,发送端口,目的IP,接收端口,载荷字节数,发送结果,异常信息,操作人,完整UDP载荷HEX");
            foreach(var r in rows)writer.WriteLine(String.Join(",",new[]{r.id.ToString(),r.time,r.code,r.name,r.sourceIP,r.sourcePort.ToString(),r.destinationIP,r.destinationPort.ToString(),r.length.ToString(),r.success?"已发出":"失败",r.error,r.actor,r.hex}.Select(Monitor.CsvCell)));
        }
    }
    public sealed partial class Server
    {
        private readonly CommandTransmitter liveTransmitter;
        private CommandTransmitter commandTransmitter {get{return configurationContext!=null&&configurationContext.Owner==this?configurationContext.Transmitter:liveTransmitter;}}
        private bool HandleCommandSending(HttpListenerContext ctx,Session session)
        {
            string path=ctx.Request.Url.AbsolutePath,method=ctx.Request.HttpMethod;if(!path.StartsWith("/api/command-send/",StringComparison.Ordinal))return false;
            if(path=="/api/command-send/config" && method=="GET"){Json(ctx.Response,commandTransmitter.Configuration(),200);return true;}
            if(path=="/api/command-send/config" && method=="POST")
            {var body=Body(ctx.Request);object value;if(!body.TryGetValue("configuration",out value))throw new ArgumentException("缺少指令发送配置");lock(commandGate){monitor.EnsureSequenceIdle();commandTransmitter.Save(JsonFile.Decode<CommandSendConfiguration>(JsonFile.Encode(value)),Text(body,"revision"));}Json(ctx.Response,commandTransmitter.Configuration(),200);return true;}
            if(path=="/api/command-send/preview" && method=="POST")
            {
                var body=Body(ctx.Request);long number;if(!Int64.TryParse(Text(body,"number"),out number) || number<1)throw new ArgumentException("请先选择库内指令");string[] destinations=null;object selected;if(body.TryGetValue("destinations",out selected))destinations=JsonFile.Decode<string[]>(JsonFile.Encode(selected))??new string[0];
                lock(commandGate)Json(ctx.Response,commandTransmitter.Preview(session.Username,number,Text(body,"code"),destinations),200);return true;
            }
            if(path=="/api/command-send/send" && method=="POST")
            {var body=Body(ctx.Request);lock(commandGate){Json(ctx.Response,commandTransmitter.Send(session.Username,auth.User(session).DisplayName,Text(body,"token")),200);}return true;}
            if(path=="/api/command-send/history" && method=="GET"){Json(ctx.Response,commandTransmitter.History(),200);return true;}
            if(path=="/api/command-send/export.csv" && method=="GET")
            {ctx.Response.ContentType="text/csv; charset=utf-8";ctx.Response.SendChunked=true;ctx.Response.Headers["Content-Disposition"]="attachment; filename=command-transmissions.csv";using(var writer=new StreamWriter(ctx.Response.OutputStream,new UTF8Encoding(true),65536,true))commandTransmitter.Export(writer);return true;}
            Json(ctx.Response,new{error="指令发送接口不存在"},404);return true;
        }
    }
}
