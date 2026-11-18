using System;
using System.Collections.Generic;

namespace TelemetryMonitor
{
    public sealed class SequenceRow
    {
        public int Row;
        public bool Added;
        public string Keyword="",Description="",Command="",Condition="",Mark="",Expected="",Timeout="";
        public string CommandNote="";
        public Dictionary<string,string> CellErrors=new Dictionary<string,string>();
    }
    public sealed class SequenceDefinition
    {
        public string Id="",Code="",Name="",Mode="judge",Filename="",Sheet="",SourceIP="",Group="";
        public int NextRowNumber;
        public List<SequenceRow> Rows=new List<SequenceRow>();
    }
    public sealed class SequenceConfiguration
    {
        public int SchemaVersion=1;
        public string Revision="";
        public double DefaultTimeoutSeconds=30;
        public long NextJudgeNumber=1,NextExecuteNumber=1;
        public List<SequenceDefinition> Sequences=new List<SequenceDefinition>();
    }
    internal sealed class SequencePredicate
    {
        public RuleExpression Expression;
        public CompiledRule Program;
        public Dictionary<string,BoundTelemetry> Fields=new Dictionary<string,BoundTelemetry>();
    }
    internal sealed class SequenceStartOptions
    {
        public int StartRow;
        public double F0;
        public double? T0;
    }
    internal sealed class SequenceStep
    {
        public SequenceRow Row;
        public SequenceDefinition NextDefinition;public List<SequenceStep> NextSteps;
        public SequencePredicate Predicate;
        public double Timeout,Delay;
        public bool Absolute,FromStart,Hold,Inside,AllDestinations,LaunchJudgment;
        public int Pair=-1;
        public long CommandNumber;
        public string Code="",SendCode="",CommandName="",CommandSignature="",Mode="";
        public string[] Destinations=new string[0];
    }
    internal sealed class SequenceStepRun
    {
        internal string[] HistoryCommandEvents=new string[0];
        public SequenceStep Step;
        internal int Index;
        public string Status="未开始",Reason="",StartedAt="",FinishedAt="";
        public bool Activated,Begun,Terminal,CommandDone,SendPending,FreshObserved,HadFailure,Episode,Recorded,PauseAcknowledged;
        public double Due,StartTime,ActivatedAt,CommandSince,LastValid;
        public double? Planned,Actual,Finished,Delay;
        public DateTime CommandSinceUtc;
        public double? CommandDataSince;
        public DateTime? CommandDataSinceUtc;
        public long EvaluationCount;
        public string LastEvaluationAt="",LastEvaluationResult="";
        public string[] UsedReferences=new string[0];
        public readonly Dictionary<string,long> Baselines=new Dictionary<string,long>();
        public Dictionary<string,object> Values=new Dictionary<string,object>();
        public SequenceShipTime ShipTime=new SequenceShipTime();
    }
    internal sealed class SequenceRowResult
    {
        public SequenceRow input;
        public string status="未开始",reason="",startedAt="",finishedAt="",commandName="";
        public Dictionary<string,object> values=new Dictionary<string,object>();
        public double? timeout,remaining,planned,actual,delay;
        public SequenceShipTime shipTime=new SequenceShipTime();
        public object evaluation;
        public bool abnormal;
        public string[] destinations=new string[0];
    }
    internal sealed class SequenceSendWork
    {
        public string RunId,Code,SendCode,Actor;
        public int Index;
        public long Number;
        public bool AllDestinations;
        public double SendBegan;
    }
    internal sealed class SequenceHistoryEvent
    {
        internal long HistoryId;
        public string Event="",RunId="",Code="",Mode="",Sequence="",Sheet="",Operator="",Time="",Status="",Reason="";
        public int Row;
        public SequenceRow Input;
        public double? Timeout,Planned,Actual,Delay,Elapsed,Paused;
        public string CommandName="",Destinations="",Values="";
        public SequenceDefinition Definition;
        public SequenceStartOptions Start;
        public object Diagnostic;
        public SequenceRowResult Result;
        public string ParentRunId="",Origin="";
        public SequenceShipTime ShipTime=new SequenceShipTime();
    }
}
