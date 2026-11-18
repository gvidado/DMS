using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class SequenceRunSummary
    {
        public string RunId,Code,Name,Mode,StartedAt,LastTime,Status;
        public int Events;public long Order;
        internal SequenceRunSummary Copy(){return (SequenceRunSummary)MemberwiseClone();}
    }
    public sealed partial class Monitor
    {
        private SequenceRunSummary[] SequenceRunSummaries()
        {
            lock (sequenceHistoryIndexGate)
            {
                return UpdateSequenceHistoryIndex().RunsByStart.AsEnumerable().Reverse()
                    .Select(r => r.Copy()).ToArray();
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
        internal object SequenceRunsPage(int page,string search,string mode="",string knownRevision="")
        {
            if(page<1||page>10000)throw new ArgumentException("运行列表页码须为1至10000");
            search=SequenceRunSearchText(search);
            CheckSequenceHistoryMode(mode);
            lock(sequenceHistoryIndexGate)
            {
                var index=UpdateSequenceHistoryIndex();
                string revision=index.Generation.ToString("N")+"-"+index.Position;
                if(sequenceHistoryError.Length==0&&String.Equals(knownRevision,revision,StringComparison.Ordinal))
                    return new{unchanged=true,revision};
                IList<SequenceRunSummary> matches=index.RunsByStart;
                if(mode.Length>0||search.Length>0)
                    matches=matches.Where(r=>(mode.Length==0||r.Mode==mode)&&(search.Length==0||MatchSequenceRun(r,search))).ToList();
                int pages=Math.Max(1,(matches.Count+49)/50);
                page=Math.Min(page,pages);
                int end=matches.Count-(page-1)*50;
                var rows=new List<SequenceRunSummary>(50);
                for(int i=end-1;i>=Math.Max(0,end-50);i--)rows.Add(matches[i].Copy());
                return new{page,pages,total=matches.Count,rows,error=sequenceHistoryError,revision};
            }
        }
    }
}
