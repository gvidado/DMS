using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class SequenceShipTime
    {public string Value="",Status="未记录",SourceIP="",SampleTime="";}
    public sealed partial class Monitor
    {
        private SequenceShipTime CaptureSequenceShipTime()
        {
            var snapshot=new SequenceShipTime();
            EnsureRuntimeIndexes();var fields=runtimeShipTimeFields;
            if(fields.Length==0){snapshot.Status="未配置CCYK00001";return snapshot;}
            var enabled=fields.Where(p=>!NativeDecoder||(p.PacketEnabled&&ReadyFor(p.Id))).ToArray();
            if(enabled.Length==0){snapshot.Status="SPP未启用或未核对";return snapshot;}
            BoundTelemetry bound=null;ParameterHistory history=null;
            foreach(var field in enabled)foreach(var source in engineeringHistory)
            {
                ParameterHistory received;
                if(source.Value.TryGetValue(field.Id,out received)&&received.Latest!=null&&(history==null||received.LatestObservation>history.LatestObservation))
                {bound=new BoundTelemetry{Field=field,Source=source.Key};history=received;}
            }
            if(history==null){snapshot.Status="未接收";return snapshot;}snapshot.SourceIP=bound.Source;
            snapshot.SampleTime=BeijingTime.Format(history.Latest.Time,"yyyy-MM-dd HH:mm:ss.fff");
            if(history.Latest.Status!="有效"){snapshot.Status="数据无效";return snapshot;}
            try
            {
                object value=RuleValue(bound.Field,bound.Source);
                if(value==null || history.Samples.Count==0){snapshot.Status="未接收";return snapshot;}
                snapshot.Value=history.Samples[0].valueText??Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);snapshot.Status="有效";
            }
            catch(RuleWaiting){snapshot.Status="数据过期";}
            return snapshot;
        }
    }
}
