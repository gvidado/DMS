using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class SequenceRunSummary
    {
        public string RunId,Code,Name,Mode,StartedAt,LastTime,Status;
        public int Events;public long Order;
    }
    public sealed partial class Monitor
    {
        private readonly object sequenceRunSearchGate=new object();
        private long sequenceRunSearchLength=-1;private DateTime sequenceRunSearchTime;
        private SequenceRunSummary[] sequenceRunSearchCache=new SequenceRunSummary[0];
        private SequenceRunSummary[] SequenceRunSummaries()
        {
            lock(sequenceRunSearchGate)
            {
                var before=new FileInfo(SequenceHistoryPath);long length=before.Exists?before.Length:0;DateTime modified=before.Exists?before.LastWriteTimeUtc:DateTime.MinValue;
                if(length==sequenceRunSearchLength && modified==sequenceRunSearchTime)return sequenceRunSearchCache;
                var runs=new Dictionary<string,SequenceRunSummary>();long order=0;
                foreach(var entry in ReadSequenceHistory())
                {
                    SequenceRunSummary run;if(!runs.TryGetValue(entry.RunId,out run))
                    {run=new SequenceRunSummary{RunId=entry.RunId,Code=entry.Code,Name=entry.Sequence,Mode=entry.Mode,StartedAt=entry.Time,Order=order++};runs.Add(entry.RunId,run);}
                    if(entry.Event=="开始"){run.StartedAt=entry.Time;run.Name=entry.Sequence;run.Mode=entry.Mode;}
                    if(!String.IsNullOrEmpty(entry.Code))run.Code=entry.Code;
                    run.Events++;run.LastTime=entry.Time;run.Status=entry.Event=="结束"?entry.Status:entry.Event=="步骤结果"?"运行中":entry.Status;
                }
                var result=runs.Values.OrderByDescending(r=>r.Order).ToArray();
                var after=new FileInfo(SequenceHistoryPath);
                if((after.Exists?after.Length:0)==length && (after.Exists?after.LastWriteTimeUtc:DateTime.MinValue)==modified)
                {sequenceRunSearchLength=length;sequenceRunSearchTime=modified;sequenceRunSearchCache=result;}
                return result;
            }
        }
        private static void CheckSequenceHistoryMode(string mode)
        {if(mode!=""&&mode!="execute"&&mode!="judge")throw new ArgumentException("序列模式无效");}
        private static string SequenceRunSearchText(string search)
        {search=(search??"").Trim();if(search.Length>512)throw new ArgumentException("序列搜索文字最多512个字符");return search;}
        private static bool MatchSequenceRun(SequenceRunSummary run,string search)
        {return (run.Code+" "+run.Name+" "+run.RunId+" "+run.StartedAt).IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0;}
        private HashSet<string> MatchingSequenceRuns(string search)
        {search=SequenceRunSearchText(search);return search.Length==0?null:new HashSet<string>(SequenceRunSummaries().Where(r=>MatchSequenceRun(r,search)).Select(r=>r.RunId));}
        internal object SequenceRunsPage(int page,string search,string mode="")
        {
            if(page<1 || page>10000)throw new ArgumentException("运行列表页码须为1至10000");search=SequenceRunSearchText(search);CheckSequenceHistoryMode(mode);
            var matches=SequenceRunSummaries().Where(r=>(mode.Length==0||r.Mode==mode)&&MatchSequenceRun(r,search)).ToArray();int pages=Math.Max(1,(matches.Length+49)/50);page=Math.Min(page,pages);
            return new{page,pages,total=matches.Length,rows=matches.Skip((page-1)*50).Take(50).ToArray(),error=sequenceHistoryError};
        }
    }
}
