using System;
using System.Collections.Generic;
using System.Linq;

namespace TelemetryMonitor
{
    internal sealed class JudgmentDispatchEntry
    {internal ContinuousRule Rule;internal RuleBinding Binding;}
    internal sealed class JudgmentDispatch
    {
        internal long Generation;
        internal ContinuousRuleConfig Configuration;
        internal readonly List<JudgmentDispatchEntry> Entries=new List<JudgmentDispatchEntry>();
        internal readonly Dictionary<string,HashSet<int>> Telemetry=new Dictionary<string,HashSet<int>>(),Commands=new Dictionary<string,HashSet<int>>(),Globals=new Dictionary<string,HashSet<int>>();
        internal readonly HashSet<int> Timers=new HashSet<int>();
        internal static void Add(Dictionary<string,HashSet<int>> index,string key,int ordinal)
        {HashSet<int> rows;if(!index.TryGetValue(key,out rows))index[key]=rows=new HashSet<int>();rows.Add(ordinal);}
        internal static void Include(Dictionary<string,HashSet<int>> index,string key,HashSet<int> rows)
        {HashSet<int> found;if(index.TryGetValue(key,out found))rows.UnionWith(found);}
    }
    public sealed partial class Monitor
    {
        private JudgmentDispatch judgmentDispatch;
        private IEnumerable<JudgmentDispatchEntry> JudgmentCandidates(JudgmentEvent input,HashSet<string> globals)
        {
            var plan=judgmentDispatch;
            if(plan==null||plan.Generation!=judgmentGeneration||!Object.ReferenceEquals(plan.Configuration,continuous))
            {
                plan=new JudgmentDispatch{Generation=judgmentGeneration,Configuration=continuous};
                foreach(var rule in continuous.Rules.Where(r=>r.Enabled).OrderBy(r=>r.Code,StringComparer.Ordinal))
                {
                    RuleBinding binding;try{binding=Binding(rule);}catch(Exception){continue;}
                    int ordinal=plan.Entries.Count;plan.Entries.Add(new JudgmentDispatchEntry{Rule=rule,Binding=binding});
                    JudgmentDispatch.Add(plan.Telemetry,binding.Source+"\0"+binding.Main.Id,ordinal);
                    foreach(var field in binding.Fields.Values.Concat(binding.Anchors.Values))JudgmentDispatch.Add(plan.Telemetry,field.Source+"\0"+field.Field.Id,ordinal);
                    foreach(string command in binding.Program.Commands)JudgmentDispatch.Add(plan.Commands,command,ordinal);
                    foreach(string global in binding.Program.GlobalReads)JudgmentDispatch.Add(plan.Globals,global,ordinal);
                    if(binding.Program.UsesClock||binding.Program.Timers.Count>0)plan.Timers.Add(ordinal);
                }
                judgmentDispatch=plan;
            }
            if(input.Start)return plan.Entries;
            var candidates=new HashSet<int>();foreach(string field in input.Changed)JudgmentDispatch.Include(plan.Telemetry,input.Source+"\0"+field,candidates);
            if(input.Command.Length>0)JudgmentDispatch.Include(plan.Commands,input.Command,candidates);
            foreach(string command in input.CommandCodes)JudgmentDispatch.Include(plan.Commands,command,candidates);
            foreach(string global in globals)JudgmentDispatch.Include(plan.Globals,global,candidates);
            if(input.Timer)candidates.UnionWith(plan.Timers);
            return candidates.OrderBy(i=>i).Select(i=>plan.Entries[i]);
        }
    }
}
