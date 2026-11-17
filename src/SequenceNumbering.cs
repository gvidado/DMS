using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private static bool AssignSequenceNumbers(SequenceConfiguration config)
        {
            bool changed=false;var codes=new HashSet<string>();
            if(config.NextJudgeNumber<1 || config.NextExecuteNumber<1)throw new ArgumentException("序列编号计数器无效");
            foreach(var definition in config.Sequences)
            {
                string prefix=SequenceCodePrefix(definition.Mode);if(String.IsNullOrEmpty(definition.Code))continue;
                long number;
                if(!Regex.IsMatch(definition.Code,"^"+prefix+"[0-9]{4,}$") || !Int64.TryParse(definition.Code.Substring(3),out number) || number<1 || number==Int64.MaxValue || definition.Code!=prefix+number.ToString("D4",CultureInfo.InvariantCulture) || !codes.Add(definition.Code))throw new ArgumentException("序列编号无效或重复："+definition.Code);
                if(prefix=="XLP" && config.NextJudgeNumber<=number){config.NextJudgeNumber=number+1;changed=true;}
                if(prefix=="XLZ" && config.NextExecuteNumber<=number){config.NextExecuteNumber=number+1;changed=true;}
            }
            foreach(var definition in config.Sequences.Where(d=>String.IsNullOrEmpty(d.Code)))
            {
                string prefix=SequenceCodePrefix(definition.Mode);long next=prefix=="XLP"?config.NextJudgeNumber:config.NextExecuteNumber;
                if(next==Int64.MaxValue)throw new ArgumentException("序列编号已超出范围");
                definition.Code=prefix+next.ToString("D4",CultureInfo.InvariantCulture);
                if(prefix=="XLP")config.NextJudgeNumber=next+1;else config.NextExecuteNumber=next+1;
                changed=true;
            }
            return changed;
        }
        private static string SequenceCodePrefix(string mode)
        {if(mode=="judge")return "XLP";if(mode=="execute")return "XLZ";throw new ArgumentException("序列运行模式无效");}
    }
}
