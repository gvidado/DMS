using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    public sealed partial class Monitor
    {
        private long SequenceHistoryVersion()
        {var file=new FileInfo(SequenceHistoryPath);return file.Exists?file.Length:0;}
        private static string SequenceIncidentIdentity(SequenceHistoryEvent e)
        {return String.Join("\0",new[]{e.RunId,e.Mode,e.Time,e.Row.ToString(),e.Reason});}
        private static UnifiedAbnormalRecord SequenceAbnormalRow(SequenceHistoryEvent e)
        {
            return new UnifiedAbnormalRecord{key="sequence:"+e.HistoryId,id=e.HistoryId,
                type=e.Mode=="execute"?"sequence-execute":"sequence-judge",typeName=e.Mode=="execute"?"序列执行":"序列判读",
                category=e.Mode=="execute"?"序列执行异常":"序列判读异常",time=e.Time,code="第"+e.Row+"行",name=e.Sequence,
                value=e.Values,result=e.Status,rule=e.Input==null?"":e.Input.Condition,detail=e.Sequence+" / 第"+e.Row+"行："+e.Reason,
                mode=e.Mode,runId=e.RunId,level="error",SequenceRow=e.Row,SequenceIdentity=SequenceIncidentIdentity(e)};
        }
        private UnifiedAbnormalSnapshot SequenceAbnormalSnapshot(out string error)
        {
            error=sequenceHistoryError;var entries=new Dictionary<long,SequenceHistoryEvent>();long version=0;
            try
            {
                SequenceHistoryLocation[] locations;
                lock (sequenceHistoryIndexGate)
                {
                    var index = UpdateSequenceHistoryIndex();
                    version = index.Position;
                    locations = index.Abnormal.ToArray();
                }
                foreach (var entry in ReadHistoryLocations(locations)) entries.Add(entry.HistoryId, entry);
            }
            catch(Exception ex){error="序列异常历史读取失败，原文件保留："+ex.Message;entries.Clear();}
            return new UnifiedAbnormalSnapshot{Revision=version,Through=entries.Count==0?0:entries.Keys.Max(),
                Rows=()=>entries.Values.Select(SequenceAbnormalRow),Read=id=>SequenceAbnormalRow(entries[id])};
        }
        private static Dictionary<string,UnifiedAbnormalRecord> SequenceIncidentIndex(UnifiedAbnormalPlan plan)
        {
            var result=plan.Rows.Where(r=>r.key.StartsWith("sequence:",StringComparison.Ordinal)).ToDictionary(r=>r.key);
            foreach(var group in result.Values.ToArray().GroupBy(r=>r.SequenceIdentity))if(group.Count()==1)result[group.Key]=group.First();
            return result;
        }
        private static UnifiedAbnormalRecord FindSequenceIncident(SequenceHistoryEvent entry,Dictionary<string,UnifiedAbnormalRecord> incidents)
        {
            UnifiedAbnormalRecord result;if(entry.Event!="异常")return null;
            if(entry.HistoryId>0&&incidents.TryGetValue("sequence:"+entry.HistoryId,out result))return result;
            return incidents.TryGetValue(SequenceIncidentIdentity(entry),out result)?result:null;
        }
        private static void CheckSequenceResultFilter(string result)
        {if(!new[]{"all","abnormal","pending","released"}.Contains(result))throw new ArgumentException("序列结果筛选无效");}
        private static bool SequenceResultMatches(SequenceHistoryEvent entry,string result,Dictionary<string,UnifiedAbnormalRecord> incidents)
        {
            if(result=="all")return true;if(entry.Event!="异常")return false;if(result=="abnormal")return true;
            var incident=FindSequenceIncident(entry,incidents);return incident!=null&&incident.released==(result=="released");
        }
        private static object SequenceHistoryResponse(SequenceHistoryEvent entry, UnifiedAbnormalPlan plan, Dictionary<string, UnifiedAbnormalRecord> incidents)
        {
            var response = JsonFile.Decode<Dictionary<string,object>>(JsonFile.Encode(entry));
            var incident = incidents == null ? null : FindSequenceIncident(entry, incidents);
            response["Abnormal"] = incident == null ? null : UnifiedDetail(incident, plan);
            return response;
        }
    }
}
