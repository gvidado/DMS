using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class PacketHealthState
    {
        public DateTime LastValid=DateTime.MinValue;
        public string LastSource="";
        public bool TimeoutConditionActive;
    }
    public sealed partial class Monitor
    {
        private readonly Dictionary<string,PacketHealthState> packetHealth=new Dictionary<string,PacketHealthState>();
        private IEnumerable<PacketDefinition> MonitoredPackets()
        {return NativeDecoder?decoder.Packets.Where(p=>p.Enabled):LegacyStatisticsDefinitions();}
        private PacketHealthState HealthFor(string id)
        {PacketHealthState health;if(!packetHealth.TryGetValue(id,out health))packetHealth[id]=health=new PacketHealthState();return health;}
        private void RecordPacketFreshness(string id,string source)
        {
            var health=HealthFor(id);health.LastValid=DateTime.UtcNow;health.LastSource=source;health.TimeoutConditionActive=false;
        }
        private void CheckPacketHealth(DateTime now)
        {
            if(!Config.EnablePacketTimeoutAlarm)return;
            foreach(var definition in MonitoredPackets())
            {
                var health=HealthFor(definition.Id);DateTime since=health.LastValid==DateTime.MinValue?started:health.LastValid;
                DateTime configured;if(configurationPacketSince.TryGetValue(definition.Id,out configured)&&configured>since)since=configured;
                if((now-since).TotalMilliseconds<=Config.DataTimeoutMs || health.TimeoutConditionActive)continue;
                health.TimeoutConditionActive=true;
                string message="SPP包无有效新帧超时："+definition.Name+"（APID="+definition.Apid+"）；超过"+Config.DataTimeoutMs+"毫秒"+(health.LastValid==DateTime.MinValue?"，本次监听尚未收到有效新帧":"，最近有效新帧时间="+BeijingTime.Format(health.LastValid,"yyyy-MM-dd HH:mm:ss.fff"))+"；确认后保留异常记录";
                RecordAlarm("error","SPP超时",message,String.IsNullOrEmpty(definition.SourceIP)?health.LastSource:definition.SourceIP,definition.Name,definition.Apid,identity:definition.Id);
                Note("error",message,false);
            }
        }
        private object PacketHealthSnapshot(PacketDefinition definition)
        {
            PacketHealthState health;packetHealth.TryGetValue(definition.Id,out health);
            bool active=Config.EnablePacketTimeoutAlarm && Listening;
            string state=!Config.EnablePacketTimeoutAlarm?"未开启":!Listening?"未监测 / 已停止":health!=null && health.TimeoutConditionActive?"超时异常":health==null || health.LastValid==DateTime.MinValue?"等待有效新帧":"正常";
            return new{enabled=Config.EnablePacketTimeoutAlarm,active,state,timedOut=health!=null && health.TimeoutConditionActive,
                lastValid=health==null || health.LastValid==DateTime.MinValue?"—":BeijingTime.Format(health.LastValid,"yyyy-MM-dd HH:mm:ss.fff"),timeoutMs=Config.DataTimeoutMs};
        }
    }
}
